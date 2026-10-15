using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, PendingArchiveDocument> pendingArchiveDocuments = new();

    public Task<PendingArchiveDocument?> FindPendingArchiveDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    { lock (gate) return Task.FromResult(pendingArchiveDocuments.GetValueOrDefault(documentId)); }

    public Task<PdmDocument> ReserveArchiveDocumentAsync(RegisterDocumentCommand command, string actor,
        ArchiveRegistrationContext context, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var existing = pendingArchiveDocuments.Values.FirstOrDefault(p => p.BatchId == context.BatchId && p.Command.FileName == command.FileName);
            if (existing is not null)
            {
                RequireArchiveRegistrationOwner(existing, context.BatchId, command.ProjectId, actor, context.SessionId);
                if (existing.Command != command) throw new PdmConflictException("同一存档批次的图档信息发生变化，请重新开始整批存档。");
                return Task.FromResult(existing.Document);
            }
            var now = timeProvider.GetUtcNow();
            var document = new PdmDocument(Guid.NewGuid(), command.ProjectId, command.DrawingNumber, command.Name, command.FileName,
                command.Kind, DocumentLifecycleState.Work, RevisionLabel.InitialWork(), actor, now)
            { FolderId = command.FolderId, CheckoutSessionId = context.SessionId, CheckoutMachine = context.MachineName,
                CheckedOutAt = now, CheckoutLastHeartbeatAt = now, CheckoutLeaseExpiresAt = now.AddHours(24), StoredVersionCount = 0 };
            pendingArchiveDocuments[document.Id] = new(context.BatchId, document, command);
            return Task.FromResult(document);
        }
    }

    public Task CancelArchiveRegistrationAsync(Guid batchId, string actor, Guid sessionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var id in pendingArchiveDocuments.Values.Where(p => p.BatchId == batchId && p.Document.CheckedOutBy == actor
                && p.Document.CheckoutSessionId == sessionId).Select(p => p.Document.Id).ToArray()) pendingArchiveDocuments.Remove(id);
            return Task.CompletedTask;
        }
    }

    private static void RequireArchiveRegistrationOwner(PendingArchiveDocument pending, Guid batchId, Guid projectId, string actor, Guid sessionId)
    {
        if (pending.BatchId != batchId || pending.Document.ProjectId != projectId || pending.Document.CheckedOutBy != actor
            || pending.Document.CheckoutSessionId != sessionId)
            throw new PdmConflictException("新图档不属于当前存档批次或编辑会话，整批提交已取消。");
    }

    private void MaterializeArchiveRegistrations(Guid batchId, Guid projectId, string actor, Guid sessionId, IReadOnlyCollection<Guid> plannedIds)
    {
        var pendingFiles = pendingArchiveDocuments.Values.Where(p => p.BatchId == batchId).ToArray();
        foreach (var pending in pendingFiles)
        {
            RequireArchiveRegistrationOwner(pending, batchId, projectId, actor, sessionId);
            if (pending.Document.CheckoutLeaseExpiresAt <= timeProvider.GetUtcNow()) throw new PdmConflictException("新图档暂存登记已过期，请重新整体存档。");
            if (!plannedIds.Contains(pending.Document.Id)) throw new PdmRuleException("整批存档遗漏了已准备的新图档，全部提交已取消。");
            if (documents.Values.Any(d => d.ProjectId == projectId && d.FileName.Equals(pending.Document.FileName, StringComparison.OrdinalIgnoreCase) && d.PurgedAt is null))
                throw new PdmConflictException("项目中已存在同名图档，整批提交已取消：" + pending.Document.FileName);
            documents[pending.Document.Id] = pending.Document;
            documentSourceFingerprints[pending.Document.Id] = pending.Command.SourceSha256!;
        }
        foreach (var pending in pendingFiles) SaveDocumentRelation(pending.Command, pending.Document);
        foreach (var pending in pendingFiles) pendingArchiveDocuments.Remove(pending.Document.Id);
    }
}
