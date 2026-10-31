using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private sealed class ArchiveDiskCopy
    {
        internal string Path, Hash;
        internal long Length;
    }
    private async Task<int> RegisterPendingArchiveDocumentsAsync(IReadOnlyList<BatchOperationItem> items,
        Guid projectId, Guid batchId, IReadOnlyDictionary<string, RegistrationDecision> decisions,
        ICollection<Guid> reservedIds, CancellationToken token)
    {
        var pending = items.Where(item => !item.Node.DocumentId.HasValue && !IsCheckedOutByCurrentUser(item.Node)).ToArray();
        if (pending.Length == 0) return 0;
        var completed = 0; var permissions = 0;
        // Drawings may refer to model identities returned by the first group.
        foreach (var drawings in new[] { false, true })
        {
            var group = pending.Where(item => (item.Node.Kind == CadDocumentKind.Drawing) == drawings).ToArray();
            var requests = new List<PdmApiClient.ArchiveRequest>();
            foreach (var item in group)
            {
                token.ThrowIfCancellationRequested(); ValidateAcquireNode(item.Node);
                ApplyDrawingModelRelation(item.Node);
                var decision = RegistrationDecisionFor(item.Node, decisions);
                requests.Add(apiClient.CreateArchiveRegistration(projectId, batchId, checkoutSessionId,
                    checkoutMachineName, item.Node, decision.SourceSha256, decision.AllowDuplicateContent, decision.DuplicateReason));
            }
            var offset = completed;
            var results = await apiClient.RunArchiveRequestsAsync<DocumentDto>(requests,
                (request, document) => {
                    if (document == null || document.Id == Guid.Empty) throw new InvalidOperationException("图档身份预留未返回有效结果。");
                    ArchiveBatchJournal.AddReservedDocument(batchId, document.Id); reservedIds.Add(document.Id);
                }, (done, total, file) => ArchiveSession.Current?.ReportFile(offset + done, items.Count, file, "本批图档身份已预留"), token);
            for (var index = 0; index < group.Length; index++)
            {
                ApplyPreparedDocumentToMatchingInstances(group[index].Node, results[index], false);
                if (IsCheckedOutByCurrentUser(group[index].Node))
                { group[index].Node.WorkState = CadWorkState.Editable; permissions++; }
            }
            completed += group.Length;
            SynchronizeBatchFileIdentities(items);
        }
        return permissions;
    }

    private async Task CompleteDeferredArchiveCopiesAsync(Dictionary<string, PreparedArchiveFile> prepared,
        CancellationToken token)
    {
        var files = prepared.Values.Where(file => file.StoredFile == null && string.IsNullOrWhiteSpace(file.UploadCopyPath)).ToArray();
        // Capture only immutable file facts. No CadTreeNode or COM object is accessed by the worker.
        var jobs = files.Select(file => new { Path = file.Node.FullPath, Name = file.Node.FileName,
            Id = file.Node.DocumentId.Value, Hash = file.SourceHash }).ToArray();
        var alreadyPrepared = prepared.Count - jobs.Length;
        var copies = await Task.Run(() => {
            var paths = new List<string>();
            var results = new List<ArchiveDiskCopy>();
            try
            {
                for (var index = 0; index < jobs.Length; index++)
                {
                    token.ThrowIfCancellationRequested(); var job = jobs[index];
                    var copy = CreateCheckInUploadCopy(job.Path, job.Id); paths.Add(copy);
                    CheckInUploadHashes.VerifySource(job.Path, job.Hash, token);
                    var hash = ComputeFileHash(copy);
                    if (!string.Equals(hash, job.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new System.IO.IOException("上传副本与已确认源文件内容不一致，尚未上传：" + job.Name);
                    results.Add(new ArchiveDiskCopy { Path = copy, Hash = hash, Length = new System.IO.FileInfo(copy).Length });
                    ArchiveSession.Current?.ReportFile(alreadyPrepared + index + 1, prepared.Count, job.Name, "上传副本已准备并核对源文件");
                }
                return results.ToArray();
            }
            catch { foreach (var path in paths) DeleteCheckInUploadCopy(path); throw; }
        }, token);
        try
        {
            // One return to the CAD thread for the whole batch, not one per file.
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                token.ThrowIfCancellationRequested();
                prepared[file.Node.FullPath] = new PreparedArchiveFile(file.Node, file.Properties, copies[index].Path,
                    file.SourceHash, file.Latest, null, copies[index].Hash, copies[index].Length);
            }
        }
        catch { foreach (var copy in copies) DeleteCheckInUploadCopy(copy.Path); throw; }
    }

    private async Task PreflightPreparedArchiveBatchAsync(IReadOnlyList<BatchOperationItem> items, Guid projectId,
        DocumentReferenceNodeDto projectRoot, IReadOnlyDictionary<string, BatchDocumentIdentity> identities,
        IReadOnlyDictionary<Guid, Guid> writebacks, IReadOnlyDictionary<string, PreparedArchiveFile> prepared,
        ICollection<BatchOperationItem> failedItems, ICollection<string> failureMessages, bool keepEditing, string handoffTo,
        CancellationToken token)
    {
        var requests = new List<PdmApiClient.ArchiveRequest>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested(); var node = item.Node; var file = prepared[node.FullPath];
            BatchDocumentIdentity identity = null; identities?.TryGetValue(node.FullPath, out identity);
            Guid? writeback = null;
            if (writebacks != null && writebacks.TryGetValue(node.DocumentId.Value, out var id)) writeback = id;
            requests.Add(apiClient.CreateArchivePreflight(node.DocumentId.Value, projectId, node.FullPath, file.ReferenceSnapshot,
                file.Properties, checkoutSessionId, IsIndependentArchiveRoot(node, projectRoot, item.Depth), identity?.DrawingNumber,
                identity?.Name, writeback, file.NativeIsolationKeepEditing ?? (node.HistoricalRepair == null && keepEditing),
                node.NativeIsolationOnly ? null : node.HistoricalRepair == null ? handoffTo : null));
        }
        var errors = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await apiClient.RunArchiveRequestsAsync<object>(requests, null,
                (done, total, name) => ArchiveSession.Current?.ReportFile(done, total, name, "服务器存档条件已核对"), token,
                (request, error) => errors[request.Key] = error);
        }
        catch (Exception error) { ThrowOnArchiveConnectionFailure(error); throw; }
        var failures = new List<string>();
        foreach (var item in items)
        {
            if (!errors.TryGetValue(item.Node.FullPath, out var error)) continue;
            LogDiagnostic("CheckInBatch.Preflight." + item.Node.FileName, error);
            var message = item.Node.FileName + "：" + ArchivePreflightError(error);
            failures.Add(message); failedItems?.Add(item); failureMessages?.Add(message);
        }
        if (failedItems == null) ThrowArchivePreflightFailures(failures);
    }
}
