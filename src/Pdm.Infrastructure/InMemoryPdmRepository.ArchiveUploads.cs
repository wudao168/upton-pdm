using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, (UploadSession Session, string State)> archiveUploads = new();

    public Task RegisterArchiveUploadAsync(UploadSession session, CancellationToken cancellationToken)
    { lock (gate) { archiveUploads[session.Id] = (session, "Staged"); return Task.CompletedTask; } }

    private void ValidateArchiveUpload(Guid documentId, string actor, Guid sessionId, Guid? batchId, StoredFile file)
    {
        var upload = archiveUploads.Values.FirstOrDefault(item => string.Equals(item.Session.TargetRelativePath!.Replace('\\','/'), file.RelativePath.Replace('\\','/'), StringComparison.OrdinalIgnoreCase));
        if (upload.Session is null) return;
        if (upload.Session.ArchiveContext!.DocumentId != documentId || upload.Session.TotalLength != file.Length || !string.Equals(upload.Session.ExpectedSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("待存档文件与服务器上传记录不一致。");
        if (upload.State == "Adopted") return;
        if (upload.State != "Staged" || upload.Session.ExpiresAt <= timeProvider.GetUtcNow()
            || upload.Session.ArchiveContext!.BatchId != batchId || upload.Session.ArchiveContext.CheckoutSessionId != sessionId
            || !string.Equals(upload.Session.Owner, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("暂存文件已过期或不属于当前存档批次，请重新存档。");
    }

    public Task<IReadOnlyList<ArchiveUploadCleanup>> ClaimExpiredArchiveUploadsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var collected = new List<ArchiveUploadCleanup>();
            foreach (var item in archiveUploads.Values.Where(item => item.State is "Staged" or "Collecting" && item.Session.ExpiresAt <= timeProvider.GetUtcNow()).Take(limit).ToArray())
            {
                var session = item.Session;
                var path = session.TargetRelativePath!;
                var referenced = versions.Values.Any(version => string.Equals(version.StorageRelativePath.Replace('\\','/'), path.Replace('\\','/'), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(version.Preview?.StorageRelativePath.Replace('\\','/'), path.Replace('\\','/'), StringComparison.OrdinalIgnoreCase));
                archiveUploads[session.Id] = (session, referenced ? "Adopted" : "Collecting");
                if (!referenced) collected.Add(new(session.Id, session.ProjectId, new(path, session.TotalLength, session.ExpectedSha256, timeProvider.GetUtcNow())));
            }
            foreach (var pending in pendingArchiveDocuments.Values.Where(p => p.Document.CheckoutLeaseExpiresAt <= timeProvider.GetUtcNow() && !archiveBatchReceipts.ContainsKey(p.BatchId)).ToArray())
                pendingArchiveDocuments.Remove(pending.Document.Id);
            return Task.FromResult<IReadOnlyList<ArchiveUploadCleanup>>(collected);
        }
    }

    public Task CompleteArchiveUploadCleanupAsync(Guid sessionId, CancellationToken cancellationToken)
    { lock (gate) { if (archiveUploads.TryGetValue(sessionId, out var item) && item.State == "Collecting") archiveUploads[sessionId] = (item.Session, "Collected"); return Task.CompletedTask; } }
}
