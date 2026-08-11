using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PdmWorkflowService(IPdmRepository repository, IReleasePackagePublisher publisher, TimeProvider timeProvider)
{
    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (document.State == DocumentLifecycleState.InReview)
        {
            throw new PdmConflictException("图档正在审批，不能获取编辑权限。 ");
        }

        if (document.CheckedOutBy is not null && !string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException($"图档正在由{document.CheckedOutBy}编辑。 ");
        }

        var updated = await repository.CheckoutAsync(documentId, actor, cancellationToken);
        await AuditAsync(actor, "document.checkout", nameof(PdmDocument), documentId.ToString(), updated.Revision.Display, cancellationToken);
        return updated;
    }

    public async Task<PdmDocument> CheckInAsync(Guid documentId, string actor, UserRole role, CadReferenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException("只有当前编辑人员可以提交存档。 ");
        }

        if (snapshot.Root.HasBlockingIssue)
        {
            throw new PdmRuleException("结构树存在缺失引用，不能提交存档。 ");
        }

        var updated = await repository.CheckInAsync(documentId, actor, document.Revision.NextWork(), snapshot, cancellationToken);
        await AuditAsync(actor, "document.checkin", nameof(PdmDocument), documentId.ToString(), updated.Revision.Display, cancellationToken);
        return updated;
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid referenceSnapshotId,
        string number,
        string mechanicalBomRevision,
        string electricalBomRevision,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");

        var mechanical = await repository.GetBomAsync(projectId, BomKind.Mechanical, cancellationToken);
        var electrical = await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken);
        if (mechanical.Count == 0 || electrical.Count == 0 || mechanical.Concat(electrical).Any(item => !item.IsComplete))
        {
            throw new PdmRuleException("机械BOM和电气BOM必须存在且完整。 ");
        }

        var packageId = Guid.NewGuid();
        var tasks = new[]
        {
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.ProcessReview, processReviewer, null, null, null, null),
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.Approval, approver, null, null, null, null)
        };
        var package = new ReleasePackage(
            packageId,
            projectId,
            number,
            ReleasePackageState.ProcessReview,
            referenceSnapshotId,
            mechanicalBomRevision,
            electricalBomRevision,
            tasks,
            timeProvider.GetUtcNow(),
            null,
            null);

        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), number, cancellationToken);
        return created;
    }

    public async Task<ReleasePackage> DecideAsync(Guid taskId, string actor, UserRole role, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
    {
        if (role is not (UserRole.ProcessReviewer or UserRole.Approver or UserRole.Administrator))
        {
            throw new PdmRuleException("当前角色不能处理审批。 ");
        }

        var package = await repository.DecideApprovalAsync(taskId, actor, decision, comment, cancellationToken);
        await AuditAsync(actor, "approval.decide", nameof(ApprovalTask), taskId.ToString(), decision.ToString(), cancellationToken);

        if (package.State != ReleasePackageState.Publishing)
        {
            return package;
        }

        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。 ");
        string publishedPath;
        try
        {
            publishedPath = await publisher.PublishAsync(package, project, cancellationToken);
        }
        catch (Exception exception)
        {
            await repository.MarkPublishFailedAsync(package.Id, exception.Message, cancellationToken);
            await AuditAsync(actor, "release-package.publish-failed", nameof(ReleasePackage), package.Id.ToString(), exception.Message, cancellationToken);
            throw;
        }

        var publishedAt = timeProvider.GetUtcNow();
        await repository.MarkPublishedAsync(package.Id, publishedPath, publishedAt, cancellationToken);
        await AuditAsync(actor, "release-package.publish", nameof(ReleasePackage), package.Id.ToString(), publishedPath, cancellationToken);
        return (await repository.FindReleasePackageAsync(package.Id, cancellationToken))!;
    }

    private static void RequireRole(UserRole actual, params UserRole[] allowed)
    {
        if (!allowed.Contains(actual))
        {
            throw new PdmRuleException("当前角色无权执行此操作。 ");
        }
    }

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);
}
