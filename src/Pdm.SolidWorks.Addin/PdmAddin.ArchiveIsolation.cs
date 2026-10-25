using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<IReadOnlyList<BatchOperationItem>> PrepareNativeIsolationAsync(
        IDictionary<string, PreparedArchiveFile> prepared, Guid projectId, Guid batchId,
        ISet<string> submissionPaths, IDictionary<string, string> inspectedHashes,
        IDictionary<string, IReadOnlyDictionary<string, string>> inspectedProperties,
        IDictionary<string, bool> initialSaveFlags, CancellationToken token)
    {
        var nodes = EnumerateArchiveNodes(currentTree).Concat(prepared.Values.Select(file => file.Node))
            .Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var requested = prepared.Values.Where(file => file.Node.Kind != CadDocumentKind.Other)
            .ToDictionary(file => file.Node.FullPath, file => string.IsNullOrWhiteSpace(file.UploadCopyPath) ? file.Node.FullPath : file.UploadCopyPath,
                StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return Array.Empty<BatchOperationItem>();
        var additions = new List<BatchOperationItem>();
        token.ThrowIfCancellationRequested();
        using (var workspace = new ArchiveNativeCopyWorkspace(application, nodes.Keys, requested, report: name =>
        {
            token.ThrowIfCancellationRequested();
            ArchiveSession.Current?.ReportFile(0, 0, name, "正在核对独立归档副本、固化外部来源…");
        }))
        {
            foreach (var isolated in workspace.Files.Where(file => requested.ContainsKey(file.Source) || file.ContentFrozen))
            {
                token.ThrowIfCancellationRequested();
                if (!nodes.TryGetValue(isolated.Source, out var node) || node.Kind == CadDocumentKind.Other) continue;
                prepared.TryGetValue(isolated.Source, out var original);
                var keepExistingPermission = IsCheckedOutByCurrentUser(node);
                if (original == null)
                {
                    if (!node.DocumentId.HasValue || FindLoadedDocument(node.FullPath)?.GetSaveFlag() == true)
                        throw new InvalidOperationException("依赖图档尚未登记或有未保存修改，不能自动作为原版本的固化副本：" + node.FileName);
                    var latest = (await apiClient.GetVersionsAsync(node.DocumentId.Value, token)).FirstOrDefault();
                    if (latest == null || !VersionMatchesLocalFile(latest, node.FullPath, isolated.SourceSha256))
                        throw new InvalidOperationException("依赖图档有实际内容变化，不能将其误当作技术固化：" + node.FileName);
                    node.NativeIsolationOnly = true;
                    var item = new BatchOperationItem(node, 1);
                    additions.Add(item);
                    await PrepareBatchCheckInPermissionAsync(item, projectId, token, archiveBatchId: batchId);
                    original = new PreparedArchiveFile(node, latest.PropertySnapshot, "", isolated.SourceSha256, latest, null);
                    inspectedHashes[node.FullPath] = isolated.SourceSha256;
                    inspectedProperties[node.FullPath] = latest.PropertySnapshot;
                    initialSaveFlags[node.FullPath] = false;
                    submissionPaths.Add(node.FullPath);
                }
                if (original.StoredFile != null && !isolated.ContentFrozen) { original.NativeIsolationVerified = true; continue; }
                CheckInUploadHashes.VerifySource(node.FullPath, original.SourceHash, token);
                var upload = original.UploadCopyPath;
                if (string.IsNullOrWhiteSpace(upload)) upload = CreateCheckInUploadCopy(node.FullPath, node.DocumentId.Value);
                File.Copy(isolated.Path, upload, true);
                var replacement = new PreparedArchiveFile(node, original.Properties, upload, original.SourceHash, original.Latest, null);
                replacement.NativeIsolationVerified = true;
                if (isolated.ContentFrozen && original.Latest != null && !node.RequiresModelUpdate
                    && VersionMatchesLocalFile(original.Latest, node.FullPath, original.SourceHash)
                    && NativeProjectionPropertiesMatch(original.Properties, original.Latest.PropertySnapshot))
                {
                    replacement.NativeIsolationSourceVersionId = original.Latest.Id;
                    replacement.NativeIsolationSourceSha256 = original.Latest.Sha256;
                    using (var sha = SHA256.Create())
                        replacement.NativeIsolationEvidence = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                            original.SourceHash + "|" + replacement.UploadCopySha256 + "|原生全配置几何及结构校验通过"))).Replace("-", "");
                }
                if (node.NativeIsolationOnly) replacement.NativeIsolationKeepEditing = keepExistingPermission;
                prepared[node.FullPath] = replacement;
                LogOperation(node.FileName + (replacement.NativeIsolationSourceVersionId.HasValue
                    ? "：已准备独立技术副本，原工程版本和原文保留。" : "：已准备独立原生存档文件。"));
            }
        }
        return additions;
    }

    private static bool NativeProjectionPropertiesMatch(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> previous)
    {
        bool Meaningful(string key) => key != "FileName" && key != "Extension" && key != "LastWriteTimeUtc" && key != "SourceFileSha256"
            && !key.Split('/').Last().StartsWith("UPLM_QR_", StringComparison.OrdinalIgnoreCase);
        var before = (previous ?? new Dictionary<string, string>()).Where(pair => Meaningful(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var after = current.Where(pair => Meaningful(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        return before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
