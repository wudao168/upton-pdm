using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

// Immutable content/ownership proof for one archive attempt, never a filename exception.
internal sealed class HistoricalReferenceRepair
{
    public HistoricalReferenceRepair(Guid projectId, CadTreeNode owner, string fileSha256)
    {
        ProjectId = projectId; OwnerNode = owner; OwnerId = owner.DocumentId.Value;
        OwnerVersionId = owner.SuppressedArchiveVersion.Id;
        OwnerSha256 = owner.SuppressedArchiveSha256; FileSha256 = fileSha256;
    }

    public Guid ProjectId { get; }
    public CadTreeNode OwnerNode { get; }
    public Guid OwnerId { get; }
    public Guid OwnerVersionId { get; }
    public string OwnerSha256 { get; }
    public string FileSha256 { get; }

    public static CadTreeNode Owner(BatchOperationItem item)
    {
        if (item?.Node == null || item.Node.DocumentId.HasValue || BatchOperationDialog.IsEmbedded(item.Node)
            || (item.Node.Kind != CadDocumentKind.Part && item.Node.Kind != CadDocumentKind.Assembly)) return null;
        return item.Ancestors.LastOrDefault(ancestor => ancestor.Kind == CadDocumentKind.Assembly && ancestor.DocumentId.HasValue
            && !ancestor.IsModifiedInSolidWorks && !ancestor.IsRenamePendingSave
            && ancestor.SuppressedArchiveVersion?.ReferenceSnapshot?.DocumentId == ancestor.DocumentId
            && !string.IsNullOrWhiteSpace(ancestor.SuppressedLocalStructureSha256)
            && string.Equals(ancestor.SuppressedLocalStructureSha256, ancestor.SuppressedArchiveSha256, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed partial class PdmAddin
{
    private async Task<IReadOnlyList<BatchOperationItem>> IncludeHistoricalReferenceRepairsAsync(
        CadTreeNode tree, IReadOnlyList<BatchOperationItem> planned, Guid projectId, CancellationToken token, bool validateOnly = false)
    {
        if (tree == null || planned.Count == 0) return planned;
        var required = new HashSet<string>(planned.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        var historical = BuildBatchOperationItems(tree).Where(item => required.Contains(item.Node.FullPath)
            && HistoricalReferenceRepair.Owner(item) != null).ToArray();
        if (historical.Length == 0) return planned;

        var documents = await apiClient.GetDocumentsAsync(projectId, token);
        var additions = new List<BatchOperationItem>();
        var proofs = new HashSet<CadTreeNode>();
        // Validate the whole repair set before changing any in-memory identities.
        var verified = new List<Tuple<BatchOperationItem, HistoricalReferenceRepair, DocumentDto, DocumentVersionDto>>();
        foreach (var item in historical)
        {
            token.ThrowIfCancellationRequested();
            var node = item.Node; var owner = HistoricalReferenceRepair.Owner(item);
            if (!documents.Any(document => document.Id == owner.DocumentId && document.ProjectId == projectId && document.Kind == (int)owner.Kind))
                throw new InvalidOperationException("历史引用所属装配不在当前项目，尚未上传：" + owner.FileName);
            if (proofs.Add(owner))
            {
                var hash = await Task.Run(() => ArchiveFileFingerprint.Read(owner.FullPath, token), token);
                if (!string.Equals(hash, owner.SuppressedArchiveSha256, StringComparison.OrdinalIgnoreCase)
                    || FindLoadedDocument(owner.FullPath)?.GetSaveFlag() == true)
                    throw new InvalidOperationException("历史引用所属装配内容已变化，不能按历史资料自动补齐。请将该装配的实际修改纳入存档：" + owner.FileName);
                var history = await apiClient.GetVersionsAsync(owner.DocumentId.Value, token);
                if (!history.Any(version => version.Id == owner.SuppressedArchiveVersion.Id && VersionMatchesFile(version, hash)))
                    throw new InvalidOperationException("历史引用所属装配的原存档版本未核对通过，尚未上传：" + owner.FileName);
            }
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata)
                && metadata.ProjectId.HasValue && metadata.ProjectId != projectId)
                throw new InvalidOperationException("历史引用仍绑定其他项目，不能自动改归属，尚未上传：" + node.FileName);
            if (node.IsModifiedInSolidWorks || node.IsRenamePendingSave || FindLoadedDocument(node.FullPath)?.GetSaveFlag() == true)
                throw new InvalidOperationException("历史引用存在未保存修改，不能按原文件补齐，尚未上传：" + node.FileName);
            var fileHash = await Task.Run(() => ArchiveFileFingerprint.Read(node.FullPath, token), token);
            var configurations = (application.GetConfigurationNames(node.FullPath) as Array)?.Cast<object>()
                .Select(Convert.ToString).ToArray();
            var requiredConfigurations = EnumerateArchiveNodes(tree).Where(candidate => PathsEqual(candidate.FullPath, node.FullPath))
                .Select(candidate => candidate.Configuration).Where(configuration => !string.IsNullOrWhiteSpace(configuration))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (requiredConfigurations.Length > 0 && (configurations == null || configurations.Length == 0
                || requiredConfigurations.Any(configuration => !configurations.Contains(configuration, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidOperationException("历史引用的实际配置未核对通过，尚未上传：" + node.FileName
                    + "；需要配置：" + string.Join("、", requiredConfigurations));
            var sameName = documents.Where(document => document.ProjectId == projectId && document.Kind == (int)node.Kind
                && string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sameName.Length > 1)
                throw new InvalidOperationException("当前项目内的历史引用有多个同名图档，不能自动选择，尚未上传：" + node.FileName);
            DocumentVersionDto match = null;
            if (sameName.Length == 1)
            {
                var history = await apiClient.GetVersionsAsync(sameName[0].Id, token);
                match = history.FirstOrDefault(version => VersionMatchesFile(version, fileHash));
                if (match == null)
                    throw new InvalidOperationException("当前项目同名历史引用与实际文件内容不同，不能自动复用，尚未上传：" + node.FileName);
            }
            verified.Add(Tuple.Create(item, new HistoricalReferenceRepair(projectId, owner, fileHash), sameName.SingleOrDefault(), match));
        }
        if (validateOnly) return planned;
        foreach (var entry in verified)
        {
            var node = entry.Item1.Node;
            foreach (var instance in EnumerateArchiveNodes(tree).Where(candidate => PathsEqual(candidate.FullPath, node.FullPath)))
            {
                instance.HistoricalRepair = entry.Item2;
                if (entry.Item3 == null) continue;
                instance.DocumentId = entry.Item3.Id; instance.Revision = instance.CurrentRevision = entry.Item4.Revision.Display;
                instance.OpenedVersionId = entry.Item4.Id; instance.OpenedRevision = entry.Item4.Revision.Display;
            }
            if (entry.Item3 == null) additions.Add(entry.Item1);
            else if (planned.Any(item => PathsEqual(item.Node.FullPath, node.FullPath)))
                planned = planned.Where(item => !PathsEqual(item.Node.FullPath, node.FullPath)).ToArray();
            var message = entry.Item3 == null ? "历史资料补齐（随整批保存，不计工程变更）" : "历史引用已匹配本项目原存档版本，不升版";
            PluginDiagnosticLog.Record("历史引用", node.FileName + "：" + message + "；所属装配=" + entry.Item2.OwnerNode.FileName
                + "；原版本=" + entry.Item2.OwnerVersionId + "；文件校验=" + entry.Item2.FileSha256);
            ArchiveSession.Current?.TrackFile(node, message);
        }
        return planned.Concat(additions).GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
    }

    private static void ValidateHistoricalReferenceScope(CadTreeNode tree, IReadOnlyList<BatchOperationItem> planned)
    {
        if (tree == null || planned == null || planned.Count == 0) return;
        var paths = new HashSet<string>(planned.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(planned.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        var unverified = BuildBatchOperationItems(tree).Where(item => required.Contains(item.Node.FullPath)
            && HistoricalReferenceRepair.Owner(item) != null && item.Node.HistoricalRepair == null).Select(item => item.Node.FileName).ToArray();
        if (unverified.Length > 0) throw new InvalidOperationException("历史遗漏引用尚未完成自动核对，尚未上传：" + string.Join("、", unverified));
        var pending = planned.SelectMany(item => EnumerateArchiveNodes(item.Node)).Where(node => node.HistoricalRepair != null
            && !node.DocumentId.HasValue && !paths.Contains(node.FullPath)).Select(node => node.FileName).Distinct().ToArray();
        if (pending.Length > 0) throw new InvalidOperationException("历史资料补齐清单未完整准备，尚未上传：" + string.Join("、", pending));
    }

    private void ValidateHistoricalRepairProofs(IReadOnlyList<BatchOperationItem> items, Guid projectId, CancellationToken token)
    {
        var nodes = items.SelectMany(item => EnumerateArchiveNodes(item.Node)).Where(node => node.HistoricalRepair != null).Distinct().ToArray();
        var owners = new HashSet<CadTreeNode>();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested(); var proof = node.HistoricalRepair;
            if (proof.ProjectId != projectId || proof.OwnerNode.DocumentId != proof.OwnerId
                || proof.OwnerNode.SuppressedArchiveVersion?.Id != proof.OwnerVersionId
                || proof.OwnerNode.IsModifiedInSolidWorks || proof.OwnerNode.IsRenamePendingSave
                || FindLoadedDocument(proof.OwnerNode.FullPath)?.GetSaveFlag() == true
                || node.IsModifiedInSolidWorks || FindLoadedDocument(node.FullPath)?.GetSaveFlag() == true
                || !string.Equals(ArchiveFileFingerprint.Read(node.FullPath, token), proof.FileSha256, StringComparison.OrdinalIgnoreCase)
                || owners.Add(proof.OwnerNode) && !string.Equals(ArchiveFileFingerprint.Read(proof.OwnerNode.FullPath, token), proof.OwnerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("历史资料在核对后发生变化，已停止整批存档，原文件与修改均保留：" + node.FileName);
        }
    }
}
