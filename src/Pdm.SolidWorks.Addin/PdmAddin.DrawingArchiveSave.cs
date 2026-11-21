using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Dictionary<string, string> savedArchiveSourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private bool SavedArchiveSourceMatches(CadTreeNode node, string hash = null) =>
        savedArchiveSourceHashes.TryGetValue(node.FullPath, out var saved)
        && string.Equals(saved, hash ?? ComputeFileHash(node.FullPath), StringComparison.OrdinalIgnoreCase);

    // Only the confirmed archive scope may save an original. A read-only model
    // retains its committed-copy workflow; a verified drawing may obtain the
    // write access already granted for the related check-in.
    private async Task<bool> SaveConfirmedWorkingSourcesForArchiveAsync(IReadOnlyList<BatchOperationItem> items, Guid projectId, CancellationToken token)
    {
        var user = authenticatedUsername;
        var scope = apiClient.EditJournalScope;
        var savedAny = false;
        foreach (var item in OrderBatchItemsForSave(DistinctBatchItems(items)))
        {
            token.ThrowIfCancellationRequested();
            var node = item.Node;
            if (!(node.Kind is CadDocumentKind.Drawing or CadDocumentKind.Assembly or CadDocumentKind.Part)
                || !node.DocumentId.HasValue || node.HistoricalRepair != null
                || node.NativeIsolationOnly || node.Role == CadReferenceRole.ExternalFileDependency || IsReadOnlyPreviewContext(node)
                || !IsCheckedOutByCurrentUser(node) || !PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
                || MemoryCopy(node) is not ArchiveMemoryCopy copy)
                continue;
            var drawing = node.Kind == CadDocumentKind.Drawing;
            if (!drawing && (copy.Model.IsOpenedReadOnly() || (File.GetAttributes(node.FullPath) & FileAttributes.ReadOnly) != 0))
                continue;
            var id = node.DocumentId.Value;
            var versionsTask = apiClient.GetVersionsAsync(id, token, 1);
            var documentTask = apiClient.GetDocumentAsync(id, token);
            await Task.WhenAll(versionsTask, documentTask);
            var latest = (await versionsTask).FirstOrDefault();
            var document = await documentTask;
            token.ThrowIfCancellationRequested();
            if (authenticatedUsername != user || apiClient.EditJournalScope != scope)
                throw new InvalidOperationException("账号或公司已变化，已停止存档，本地文件和修改已保留。");
            ValidateBatchPropertyWriteAccess(document, id, projectId, user, checkoutSessionId, DateTime.UtcNow,
                node.FileName, checkoutMachineName, ResolveUserDisplayName);
            if (latest == null || !PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var binding)
                || binding.DocumentId != id || binding.ProjectId != projectId || binding.VersionId != latest.Id)
                continue;
            void RequireConfirmedSource()
            {
                if (!ReferenceEquals(copy, MemoryCopy(node)) || !ReferenceEquals(copy.Model, FindLoadedDocument(node.FullPath))
                    || !copy.MatchesMemory() || !string.Equals(copy.SourceHash, ComputeFileHash(node.FullPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("准备保存时图档窗口或本地文件发生变化，已保留修改，请核对后重新存档：" + node.FileName);
            }
            RequireConfirmedSource();
            // An engineering drawing opened for a related model's check-in is
            // initially read-only. Apply the exact checkout we just verified.
            if (copy.Model.IsOpenedReadOnly() || (File.GetAttributes(node.FullPath) & FileAttributes.ReadOnly) != 0)
            {
                if (!drawing) continue;
                EnsureDocumentEditable(copy.Model, node.FullPath);
                RequireConfirmedSource();
            }
            ArchiveSession.Current?.ReportFile(0, 0, node.FileName, "正在保存已确认提交的工作副本…");
            SaveManagedDocumentOnly(copy.Model, node.FullPath);
            if (copy.Model.GetSaveFlag() || !PathsEqual(copy.Model.GetPathName(), node.FullPath))
                throw new InvalidOperationException("图档保存后仍有待保存修改，已保留当前窗口，请核对后重新存档：" + node.FileName);
            var hash = ComputeFileHash(node.FullPath);
            savedArchiveSourceHashes[node.FullPath] = hash;
            node.IsModifiedInSolidWorks = false;
            node.WorkState = CadWorkState.PendingCheckIn;
            archiveMemoryCopies.Remove(node.FullPath);
            copy.SourceSavedToDisk = true;
            copy.Dispose();
            LogOperation("已保存确认提交的工作副本，将从同一文件存档：" + node.FullPath);
            savedAny = true;
        }
        return savedAny;
    }
}
