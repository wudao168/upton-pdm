using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ReleaseItemCommentService(
    IPdmRepository repository,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ReleaseItemComment>> ListAsync(
        Guid releasePackageId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var package = await FindReadablePackageAsync(releasePackageId, actor, role, cancellationToken);
        return await repository.ListReleaseItemCommentsAsync(package.Id, cancellationToken);
    }

    public async Task<ReleaseItemComment> AddAsync(
        Guid releasePackageId,
        Guid bomItemId,
        string text,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var package = await FindReadablePackageAsync(releasePackageId, actor, role, cancellationToken);
        if (package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
            throw new PdmConflictException("只有审批中的发布包可以新增物料批注。");

        var currentTask = package.ApprovalTasks.OrderBy(item => item.StepOrder).FirstOrDefault(item => item.Decision is null)
            ?? throw new PdmConflictException("当前发布包没有待处理的审批节点。");
        var canEmergencySubstitute = await repository.HasUserPermissionAsync(
            actor, role, PermissionCodes.ApprovalEmergencySubstitute, cancellationToken);
        if (!string.Equals(currentTask.Assignee, actor, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase)
            && !canEmergencySubstitute)
            throw new UnauthorizedAccessException("只有当前审批人或紧急代批人员可以新增物料批注。");

        text = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) throw new PdmRuleException("物料批注不能为空。");
        if (text.Length > 1000) throw new PdmRuleException("物料批注不能超过1000个字符。");

        var item = SnapshotItems(package).FirstOrDefault(candidate => candidate.Id == bomItemId)
            ?? throw new PdmNotFoundException("该物料不在当前发布包的审批固化快照中。");
        var comment = new ReleaseItemComment(
            Guid.NewGuid(),
            package.Id,
            item.Id,
            MaterialKey(item),
            item.DrawingNumber?.Trim() ?? string.Empty,
            item.Name,
            item.Specification,
            item.SourceInstancePath,
            text,
            actor,
            timeProvider.GetUtcNow());
        var saved = await repository.AddReleaseItemCommentAsync(comment, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            actor,
            "release-package.item-comment.add",
            nameof(ReleasePackage),
            package.Id.ToString(),
            $"{item.DrawingNumber} · {item.Name} · {text}"), cancellationToken);
        return saved;
    }

    private async Task<ReleasePackage> FindReadablePackageAsync(
        Guid releasePackageId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return package;
    }

    private static IEnumerable<BomItem> SnapshotItems(ReleasePackage package) => package.StandardBomSnapshot
        .Concat(package.NonStandardBomSnapshot)
        .Concat(package.ElectricalBomSnapshot)
        .Concat(package.MechanicalBomSnapshot)
        .GroupBy(item => item.Id)
        .Select(group => group.First());

    private static string MaterialKey(BomItem item)
    {
        var materialCode = item.DrawingNumber?.Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(materialCode)
            ? $"material:{materialCode}|{item.Unit?.Trim().ToLowerInvariant()}"
            : $"item:{item.Id:N}";
    }
}
