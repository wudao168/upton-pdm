using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9InventoryService(
    IU9InventoryRepository inventory,
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient authenticationClient,
    IU9InventoryClient inventoryClient,
    TimeProvider timeProvider)
{
    public async Task<U9InventoryPage> ListAsync(
        U9InventoryFilters filters,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireViewAsync(actor, role, cancellationToken);
        return await inventory.ListAsync(filters with
        {
            Page = Math.Max(1, filters.Page),
            PageSize = Math.Clamp(filters.PageSize, 1, 200)
        }, cancellationToken);
    }

    public async Task<U9InventoryPage> RefreshMaterialAsync(
        string materialCode,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireViewAsync(actor, role, cancellationToken);
        var normalizedCode = Required(materialCode, "料号");
        var material = await materials.FindMaterialByCodeAsync(normalizedCode, cancellationToken)
            ?? throw new PdmNotFoundException($"料品主档中不存在料号 {normalizedCode}。");
        if (material.IsArchived) throw new PdmRuleException($"料品 {normalizedCode} 已停用，不能执行库存刷新。");

        var configuration = await RequireConfigurationAsync(cancellationToken);
        var settings = await inventory.GetSettingsAsync(cancellationToken);
        var result = await QueryAsync(configuration, settings.QueryPath, normalizedCode, cancellationToken);
        EnsureSuccessful(result);
        var rows = result.Rows
            .Where(row => string.Equals(row.MaterialCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var refreshedAt = timeProvider.GetUtcNow();
        await inventory.RefreshMaterialAsync(normalizedCode, rows, refreshedAt, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), refreshedAt, actor, "u9.inventory.material-refresh", nameof(U9InventorySnapshotRow), normalizedCode,
            $"只读刷新U9C料品库存：返回{rows.Length}条库存明细；未执行U9C写入。"), cancellationToken);
        return await inventory.ListAsync(new(
            normalizedCode, null, null, null, null, null, null, false, 1, 200), cancellationToken);
    }

    public async Task<U9InventorySyncSettings> GetSettingsAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireManageAsync(actor, role, cancellationToken);
        return await inventory.GetSettingsAsync(cancellationToken);
    }

    public async Task<U9InventorySyncSettings> UpdateSettingsAsync(
        bool autoSyncEnabled,
        int syncIntervalMinutes,
        string queryPath,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireManageAsync(actor, role, cancellationToken);
        if (syncIntervalMinutes is < 15 or > 1440)
            throw new PdmRuleException("库存自动同步间隔必须在15分钟到24小时之间。");
        var normalizedPath = Required(queryPath, "库存查询接口路径");
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
            throw new PdmRuleException("库存查询接口路径必须以/开头。");
        var current = await inventory.GetSettingsAsync(cancellationToken);
        var updatedAt = timeProvider.GetUtcNow();
        var saved = await inventory.SaveSettingsAsync(current with
        {
            AutoSyncEnabled = autoSyncEnabled,
            SyncIntervalMinutes = syncIntervalMinutes,
            QueryPath = normalizedPath,
            UpdatedBy = actor,
            UpdatedAt = updatedAt
        }, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), updatedAt, actor, "u9.inventory.settings.update", nameof(U9InventorySyncSettings), "u9c",
            $"库存自动同步：{(autoSyncEnabled ? "启用" : "关闭")}；间隔：{syncIntervalMinutes}分钟；查询路径：{normalizedPath}。"), cancellationToken);
        return saved;
    }

    public Task<U9InventorySyncRun?> GetLatestRunAsync(CancellationToken cancellationToken) =>
        inventory.GetLatestRunAsync(cancellationToken);

    public async Task<U9InventorySyncRun> SynchronizeAsync(
        string actor,
        string triggerKind,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var run = new U9InventorySyncRun(
            Guid.NewGuid(), Required(triggerKind, "触发方式"), U9InventorySyncStatus.Running,
            0, 0, 0, null, startedAt, null);
        await inventory.SaveRunAsync(run, cancellationToken);
        try
        {
            var configuration = await RequireConfigurationAsync(cancellationToken);
            var settings = await inventory.GetSettingsAsync(cancellationToken);
            var activeCodes = await inventory.ListActiveMaterialCodesAsync(cancellationToken);
            var result = await QueryAsync(configuration, settings.QueryPath, null, cancellationToken);
            EnsureSuccessful(result);
            var scopedRows = result.Rows
                .Where(row => activeCodes.Contains(row.MaterialCode) && row.StockQuantity != 0m)
                .ToArray();
            var completedAt = timeProvider.GetUtcNow();
            await inventory.ReplaceSnapshotAsync(run.Id, scopedRows, completedAt, cancellationToken);
            run = run with
            {
                Status = U9InventorySyncStatus.Succeeded,
                SourceRowCount = result.Rows.Count,
                StoredRowCount = scopedRows.Length,
                MaterialCount = scopedRows.Select(row => row.MaterialCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                CompletedAt = completedAt
            };
            await inventory.SaveRunAsync(run, cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(
                Guid.NewGuid(), completedAt, actor, "u9.inventory.full-sync", nameof(U9InventorySyncRun), run.Id.ToString(),
                $"U9C库存全量只读刷新：源明细{run.SourceRowCount}，PLM范围内明细{run.StoredRowCount}，料号{run.MaterialCount}；完整成功后切换快照，未执行U9C写入。"), cancellationToken);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run = run with
            {
                Status = U9InventorySyncStatus.Failed,
                LastError = "刷新被服务停止或超时中断；已保留上一份完整快照。",
                CompletedAt = timeProvider.GetUtcNow()
            };
            await inventory.SaveRunAsync(run, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            run = run with
            {
                Status = U9InventorySyncStatus.Failed,
                LastError = exception.Message,
                CompletedAt = timeProvider.GetUtcNow()
            };
            await inventory.SaveRunAsync(run, CancellationToken.None);
            throw;
        }
    }

    private async Task<U9InventoryQueryResult> QueryAsync(
        U9MaterialIntegrationConfiguration configuration,
        string queryPath,
        string? materialCode,
        CancellationToken cancellationToken)
    {
        var secret = secretProtector.Unprotect(configuration.ClientSecretCiphertext);
        var authentication = await authenticationClient.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secret), cancellationToken);
        return await inventoryClient.QueryInventoryAsync(
            configuration.BaseUrl, queryPath, authentication.Token,
            configuration.OrganizationCode, materialCode, cancellationToken);
    }

    private async Task<U9MaterialIntegrationConfiguration> RequireConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.BaseUrl)
            || string.IsNullOrWhiteSpace(configuration.EnterpriseCode)
            || string.IsNullOrWhiteSpace(configuration.OrganizationCode)
            || string.IsNullOrWhiteSpace(configuration.UserCode)
            || string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C连接尚未完整配置，不能查询库存。");
        return configuration;
    }

    private async Task RequireViewAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        foreach (var permission in new[] { PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView })
            if (await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken)) return;
        throw new UnauthorizedAccessException("当前角色无权查看料品库存。");
    }

    private async Task RequireManageAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权维护U9C库存同步设置。");
    }

    private static void EnsureSuccessful(U9InventoryQueryResult result)
    {
        if (result.ResponseCode != 0 || !result.Success)
            throw new PdmRuleException($"U9C库存查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");
    }

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? throw new PdmRuleException($"{field}不能为空。") : normalized;
    }
}
