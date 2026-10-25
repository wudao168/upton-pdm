using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<bool> TryAcquireDrawingWorkingCopiesAsync(IReadOnlyList<BatchOperationItem> selected, Guid projectId)
    {
        if (selected.Count == 0 || selected.Any(item => item.Node.Kind != CadDocumentKind.Drawing
            || !item.Node.DocumentId.HasValue || item.Node.IsReadOnlyPreview
            || !PdmDocumentIdentityStore.IsControlledWorkspacePath(item.Node.FullPath)
            || IsReadOnlyPreviewPath(item.Node.FullPath) || IsHistoricalDerivedPath(item.Node.FullPath))) return false;
        if (!EnsureEditJournal()) throw new InvalidOperationException("工程图编辑恢复记录尚未准备完成，已保留当前窗口和修改，请重新登录后重试。");
        var documents = (await apiClient.GetDocumentsAsync(projectId, lifetime.Token)).ToDictionary(document => document.Id);
        var targets = new List<Tuple<CadTreeNode, ControlledOpenFileDto, DocumentVersionDto, DocumentDto>>();
        foreach (var item in selected.GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First()))
        {
            var node = item.Node; var id = node.DocumentId.Value;
            var latest = (await apiClient.GetVersionsAsync(id, lifetime.Token, 1)).FirstOrDefault();
            if (!documents.TryGetValue(id, out var document) || document.ProjectId != projectId || document.Kind != (int)CadDocumentKind.Drawing
                || !string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)
                || latest == null || !File.Exists(node.FullPath)
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var binding)
                || binding.DocumentId != id || binding.ProjectId != projectId || binding.VersionId != latest.Id)
                throw new InvalidOperationException("当前工程图副本的图档、项目或版本需要核对，已保留全部窗口和文件；未切换或保存上层装配体：" + node.FileName);
            var file = new ControlledOpenFileDto { DocumentId = id, VersionId = latest.Id, FileName = node.FileName,
                RelativePath = node.FileName, Sha256 = binding.Sha256, FileLength = binding.FileLength ?? latest.FileLength, Revision = binding.Revision };
            localEdits.TryGetValue(id, out var draft);
            var ownLease = document.CheckoutSessionId == checkoutSessionId
                && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
            var verifiedDraft = WorkingReuseDraftMatches(node.FullPath, file, document, draft, checkoutSessionId, authenticatedUsername);
            var loaded = FindLoadedDocument(node.FullPath);
            if (!VersionMatchesLocalFile(latest, node.FullPath, ComputeFileHash(node.FullPath)) && !verifiedDraft
                || loaded?.GetSaveFlag() == true && !loaded.IsOpenedReadOnly() && !ownLease && !verifiedDraft)
                throw new InvalidOperationException("当前工程图存在尚未核对的本地修改，已保留其窗口和内容，未重新下载或覆盖文件：" + node.FileName);
            targets.Add(Tuple.Create(node, file, latest, document));
        }
        foreach (var target in targets)
        {
            var node = target.Item1; var id = node.DocumentId.Value; var previous = target.Item4;
            var created = string.IsNullOrWhiteSpace(previous.CheckedOutBy);
            var acquired = false;
            var wasReadOnly = (File.GetAttributes(node.FullPath) & FileAttributes.ReadOnly) != 0;
            try
            {
                var document = previous;
                if (document.CheckoutSessionId != checkoutSessionId || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    plmAuthoritativeCheckouts.Add(id);
                    try { document = await apiClient.CheckoutAsync(id, checkoutSessionId, checkoutMachineName, lifetime.Token); }
                    finally { plmAuthoritativeCheckouts.Remove(id); }
                }
                if (document.Id != id || document.ProjectId != projectId || document.CheckoutSessionId != checkoutSessionId
                    || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("服务器尚未确认当前工程图的编辑权限，已保留原窗口和文件。");
                acquired = true;
                var latest = (await apiClient.GetVersionsAsync(id, lifetime.Token, 1)).FirstOrDefault();
                if (latest?.Id != target.Item3.Id) throw new InvalidOperationException("获取期间工程图存档版本发生变化，已停止解锁并保留当前窗口和文件。");
                var matching = (currentTree == null ? Enumerable.Empty<CadTreeNode>() : EnumerateCadNodes(currentTree))
                    .Where(candidate => candidate.DocumentId == id && PathsEqual(candidate.FullPath, node.FullPath)).Concat(new[] { node }).Distinct().ToArray();
                ApplyCheckoutDocumentToMatchingInstances(matching, document);
                localEdits.TryGetValue(id, out var recorded);
                if (!WorkingReuseDraftMatches(node.FullPath, target.Item2, document, recorded, checkoutSessionId, authenticatedUsername))
                    throw new InvalidOperationException("工程图恢复记录尚未与当前路径和版本对应，已保留窗口、修改及权限，未切换上层装配体。");
                SetFileReadOnly(node.FullPath, false);
                EnsureLoadedDocumentEditable(node.FullPath);
                LogOperation("工程图原位获取完成 document=" + id + " path=" + node.FullPath + " alreadyOwned=" + !created + " reopened=0 replaced=0");
            }
            catch
            {
                if (created && acquired && FindLoadedDocument(node.FullPath)?.GetSaveFlag() != true
                    && VersionMatchesLocalFile(target.Item3, node.FullPath, ComputeFileHash(node.FullPath)))
                    try
                    {
                        var released = await apiClient.DiscardCheckoutAsync(id, checkoutSessionId, lifetime.Token);
                        var matching = (currentTree == null ? Enumerable.Empty<CadTreeNode>() : EnumerateCadNodes(currentTree))
                            .Where(candidate => candidate.DocumentId == id && PathsEqual(candidate.FullPath, node.FullPath)).Concat(new[] { node }).Distinct().ToArray();
                        ApplyCheckoutDocumentToMatchingInstances(matching, released);
                        SetFileReadOnly(node.FullPath, wasReadOnly);
                        if (wasReadOnly) ProtectLoadedDocument(node.FullPath);
                    }
                    catch (Exception error) { LogDiagnostic("工程图获取未完成，新增权限暂未释放", error); }
                throw;
            }
        }
        return true;
    }
}
