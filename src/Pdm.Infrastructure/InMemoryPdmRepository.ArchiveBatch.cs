using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, ArchiveBatchReceipt> archiveBatchReceipts = new();
    private readonly Dictionary<Guid, (ArchiveBatchFollowup Work, DateTimeOffset NextAttempt)> archiveBatchFollowups = new();

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
            var oldPending = pendingArchiveDocuments.ToDictionary(); var oldRelations = documentRelations.ToDictionary();
            try
            {
                var ordered = ArchiveBatchRules.Order(files);
                var projectId = ordered[0].Commit.ReferenceSnapshot.ProjectId;
                if (!projects.TryGetValue(projectId, out var project) || !project.IsActive)
                    throw new PdmConflictException("项目不存在或已停用，不能提交存档。");
                MaterializeArchiveRegistrations(batchId, projectId, actor, sessionId, ordered.Select(file => file.DocumentId).ToArray());
                var previousDocuments = new List<PdmDocument>();
                var results = new Dictionary<Guid, DocumentCheckInResult>();
                foreach (var file in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!documents.TryGetValue(file.DocumentId, out var owner) || owner.ProjectId != projectId)
                        throw new PdmRuleException("整批存档包含其他项目的图档，已取消提交。");
                    var commit = ArchiveBatchRules.BindCommittedReferences(file.Commit, results);
                    previousDocuments.Add(owner);
                    if (file.RestoreHistoricalVersion)
                    {
                        ArchiveBatchRules.ValidateRestoreSession(owner, actor, file.ExpectedCheckoutSessionId);
                        if (commit.SourceVersionId is not Guid sourceId || !versions.TryGetValue(sourceId, out var source) || source.DocumentId != file.DocumentId)
                            throw new PdmRuleException("恢复来源版本无效。");
                        if (owner.CheckoutSessionId is null)
                            documents[file.DocumentId] = owner with { CheckedOutBy = actor, CheckoutSessionId = sessionId };
                        commit = commit with { IsHistoricalRestore = true };
                    }
                    foreach (var node in ArchiveBatchRules.References(commit.ReferenceSnapshot.Root).Where(node => node.Status != ReferenceNodeStatus.Virtual))
                    {
                        if (node.DocumentId is not Guid id || node.Revision is null || !documents.TryGetValue(id, out var document)
                            || document.ProjectId != projectId || document.DeletedAt.HasValue || document.PurgedAt.HasValue
                            || document.Kind != node.Kind || !string.Equals(document.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)
                            || !versions.Values.Any(version => version.DocumentId == id && version.Revision == node.Revision))
                            throw new PdmRuleException("引用图档未完整存档，整批提交已取消：" + node.FileName);
                    }
                    results[file.DocumentId] = CheckInVersionCore(file.DocumentId, actor, sessionId, commit, file.DrawingReviewWritebackId, cancellationToken, batchId).GetAwaiter().GetResult();
                    if (commit.ReferenceSnapshot.Root.Kind == DocumentKind.Drawing)
                    {
                        var models = commit.ReferenceSnapshot.Root.Children.Where(node => node.Kind is DocumentKind.Assembly or DocumentKind.Part)
                            .Where(node => node.Status != ReferenceNodeStatus.Virtual && node.DocumentId.HasValue).Select(node => node.DocumentId!.Value).Distinct().ToArray();
                        if (models.Length == 0) throw new PdmRuleException("工程图缺少实际模型关联，整批提交已取消：" + commit.ReferenceSnapshot.Root.FileName);
                        foreach (var key in documentRelations.Keys.Where(key => key.DrawingId == file.DocumentId).ToArray()) documentRelations.TryRemove(key, out _);
                        foreach (var modelId in models) documentRelations[(file.DocumentId, modelId)] = new(modelId, file.DocumentId);
                    }
                }
                RefreshWorkingReferenceRoots(projectId, actor, timeProvider.GetUtcNow());
                var receipt = new ArchiveBatchReceipt(batchId, projectId, actor, requestHash, ordered.Select(file => results[file.DocumentId]).ToArray()) { FollowupPending = true };
                archiveBatchReceipts[batchId] = receipt;
                foreach (var result in receipt.Results.Where(result => result.VersionCreated && result.Version is not null))
                    foreach (var item in archiveUploads.Values.Where(item => item.Session.ArchiveContext!.DocumentId == result.Document.Id
                        && string.Equals(item.Session.TargetRelativePath!.Replace('\\','/'), result.Version!.StorageRelativePath.Replace('\\','/'), StringComparison.OrdinalIgnoreCase)).ToArray())
                        archiveUploads[item.Session.Id] = (item.Session, "Adopted");
                archiveBatchFollowups[batchId] = (new(receipt, previousDocuments), timeProvider.GetUtcNow());
                foreach (var file in ordered)
                    audits.Enqueue(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor,
                        file.RestoreHistoricalVersion ? "document.version.restore" : "document.checkin.batch", "PdmDocument", file.DocumentId.ToString(), "整批存档：" + batchId));
                return Task.FromResult(receipt);
            }
            catch
            {
                RestoreArchiveState(documents, oldDocuments); RestoreArchiveState(versions, oldVersions);
                RestoreArchiveState(referenceSnapshots, oldSnapshots); RestoreArchiveState(referenceRoots, oldRoots);
                RestoreArchiveState(referenceSnapshotHistory, oldHistory); RestoreArchiveState(documentSourceFingerprints, oldFingerprints);
                RestoreArchiveState(pendingArchiveDocuments, oldPending); RestoreArchiveState(documentRelations, oldRelations);
                throw;
            }
        }
    }

    private static void RestoreArchiveState<TKey, TValue>(IDictionary<TKey, TValue> target, IDictionary<TKey, TValue> source) where TKey : notnull
    { target.Clear(); foreach (var pair in source) target[pair.Key] = pair.Value; }

    public Task<IReadOnlyList<ArchiveBatchFollowup>> ListArchiveBatchFollowupsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var pending = archiveBatchFollowups.Values.Where(item => item.NextAttempt <= timeProvider.GetUtcNow()).Take(Math.Clamp(limit, 1, 20)).Select(item => item.Work).ToArray();
            foreach (var work in pending) archiveBatchFollowups[work.Receipt.BatchId] = (work, timeProvider.GetUtcNow().AddMinutes(5));
            return Task.FromResult<IReadOnlyList<ArchiveBatchFollowup>>(pending);
        }
    }

    public Task CompleteArchiveBatchFollowupAsync(Guid batchId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            archiveBatchFollowups.Remove(batchId);
            if (archiveBatchReceipts.TryGetValue(batchId, out var receipt)) archiveBatchReceipts[batchId] = receipt with { FollowupPending = false };
            return Task.CompletedTask;
        }
    }
}
