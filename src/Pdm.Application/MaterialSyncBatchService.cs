using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialSyncBatchService(
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider)
{
    public async Task<MaterialSyncBatch> CreateAsync(
        IReadOnlyList<Guid> taskIds,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await CanSynchronizeAsync(actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行料号同步。");

        return await CreateCoreAsync(taskIds, actor, role, cancellationToken);
    }

    public async Task<MaterialSyncBatch> CreateAutomaticAsync(
        IReadOnlyList<Guid> taskIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var batch = await CreateCoreAsync(taskIds, actor, UserRole.Administrator, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            actor,
            "u9.material-sync.batch.auto-create",
            nameof(MaterialSyncBatch),
            batch.Id.ToString(),
            $"多级BOM表头料号自动同步批次：{batch.TotalCount}项。"), cancellationToken);
        return batch;
    }

    private async Task<MaterialSyncBatch> CreateCoreAsync(
        IReadOnlyList<Guid> taskIds,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {

        var selectedIds = taskIds.Distinct().ToArray();
        if (selectedIds.Length == 0) throw new PdmRuleException("请至少选择一个U9C同步任务。");
        if (selectedIds.Length > 500) throw new PdmRuleException("单次批量同步最多允许500项。");

        var available = (await materials.ListSyncTasksAsync(cancellationToken))
            .Where(task => selectedIds.Contains(task.Id))
            .ToDictionary(task => task.Id);
        if (available.Count != selectedIds.Length)
            throw new PdmRuleException("部分U9C同步任务不存在，请刷新后重试。");
        if (available.Values.Any(task => task.Status is not (MaterialSyncStatus.PreviewReady or MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview)))
            throw new PdmRuleException("选中的任务包含不可执行项，请刷新后重新选择。");

        selectedIds = selectedIds
            .OrderBy(taskId => available[taskId].MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(taskId => taskId)
            .ToArray();
        var now = timeProvider.GetUtcNow();
        var batchId = Guid.NewGuid();
        var items = selectedIds.Select((taskId, index) => new MaterialSyncBatchItem(
            Guid.NewGuid(), batchId, taskId, index + 1, MaterialSyncBatchItemStatus.Queued,
            null, null, null, null)).ToArray();
        var batch = new MaterialSyncBatch(
            batchId, MaterialSyncBatchStatus.Queued, actor, role, items.Length,
            0, 0, 0, 0, null, null, null, now, null, null, items);
        return await materials.CreateSyncBatchAsync(batch, cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialSyncBatch>> ListRecentAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await CanSynchronizeAsync(actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权查看料号同步批次。");
        return await materials.ListRecentSyncBatchesAsync(actor, 10, cancellationToken);
    }

    public async Task<MaterialSyncBatch> GetAsync(
        Guid batchId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await CanSynchronizeAsync(actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权查看料号同步批次。");
        var batch = await materials.FindSyncBatchAsync(batchId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C批量同步任务不存在。");
        if (!string.Equals(batch.RequestedBy, actor, StringComparison.OrdinalIgnoreCase)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken))
            throw new UnauthorizedAccessException("只能查看本人发起的U9C同步批次。");
        return batch;
    }

    private async Task<bool> CanSynchronizeAsync(string actor, UserRole role, CancellationToken cancellationToken) =>
        await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken)
        || await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
}
