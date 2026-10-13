using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, ArchiveBatchReceipt> archiveBatchReceipts = new();

    public Task<ArchiveBatchReceipt?> FindArchiveBatchAsync(Guid batchId, CancellationToken cancellationToken)
    { lock (gate) return Task.FromResult(archiveBatchReceipts.GetValueOrDefault(batchId)); }

    public Task<ArchiveBatchReceipt> CheckInBatchVersionsAsync(Guid batchId, string requestHash, string actor, Guid sessionId,
        IReadOnlyList<ArchiveBatchFile> files, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (archiveBatchReceipts.TryGetValue(batchId, out var existing))
                return Task.FromResult(ArchiveBatchRules.RequireMatchingReceipt(existing, actor, requestHash));
            var oldDocuments = documents.ToDictionary(); var oldVersions = versions.ToDictionary();
            var oldSnapshots = referenceSnapshots.ToDictionary(); var oldRoots = referenceRoots.ToDictionary();
            var oldHistory = referenceSnapshotHistory.ToDictionary(); var oldFingerprints = documentSourceFingerprints.ToDictionary();
            try
            {
                var ordered = ArchiveBatchRules.Order(files);
                var projectId = ordered[0].Commit.ReferenceSnapshot.ProjectId;
                var results = new Dictionary<Guid, DocumentCheckInResult>();
                foreach (var file in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!documents.TryGetValue(file.DocumentId, out var owner) || owner.ProjectId != projectId)
                        throw new PdmRuleException("整批存档包含其他项目的图档，已取消提交。");
                    var commit = ArchiveBatchRules.BindCommittedReferences(file.Commit, results);
                    foreach (var node in ArchiveBatchRules.References(commit.ReferenceSnapshot.Root).Where(node => node.Status != ReferenceNodeStatus.Virtual))
                    {
                        if (node.DocumentId is not Guid id || node.Revision is null || !documents.TryGetValue(id, out var document)
                            || document.ProjectId != projectId || document.DeletedAt.HasValue || document.PurgedAt.HasValue
                            || document.Kind != node.Kind || !string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)
                            || !versions.Values.Any(version => version.DocumentId == id && version.Revision == node.Revision))
                            throw new PdmRuleException("引用图档未完整存档，整批提交已取消：" + node.FileName);
                    }
                    results[file.DocumentId] = CheckInVersionAsync(file.DocumentId, actor, sessionId, commit, file.DrawingReviewWritebackId, cancellationToken).GetAwaiter().GetResult();
                }
                var receipt = new ArchiveBatchReceipt(batchId, projectId, actor, requestHash, ordered.Select(file => results[file.DocumentId]).ToArray());
                archiveBatchReceipts[batchId] = receipt;
                return Task.FromResult(receipt);
            }
            catch
            {
                RestoreArchiveState(documents, oldDocuments); RestoreArchiveState(versions, oldVersions);
                RestoreArchiveState(referenceSnapshots, oldSnapshots); RestoreArchiveState(referenceRoots, oldRoots);
                RestoreArchiveState(referenceSnapshotHistory, oldHistory); RestoreArchiveState(documentSourceFingerprints, oldFingerprints);
                throw;
            }
        }
    }

    private static void RestoreArchiveState<TKey, TValue>(IDictionary<TKey, TValue> target, IDictionary<TKey, TValue> source) where TKey : notnull
    { target.Clear(); foreach (var pair in source) target[pair.Key] = pair.Value; }
}
