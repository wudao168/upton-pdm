using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9ProcurementService(
    IU9ProcurementRepository procurement,
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient authenticationClient,
    IU9ProcurementClient procurementClient,
    TimeProvider timeProvider)
{
    public async Task<ProjectProcurementTrackingResult> ListProjectAsync(
        Guid projectId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");

        var root = project.ParentProjectId is null
            ? project
            : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, cancellationToken)
                ?? throw new PdmNotFoundException("主项目不存在。");
        var subprojectCode = project.ParentProjectId is null ? null : project.Code;
        var sourceRows = await procurement.ListForProjectAsync(root.Code, subprojectCode, cancellationToken);
        var published = await ListPublishedBomAsync(projectId, cancellationToken);
        var latestRun = await procurement.GetLatestRunAsync(cancellationToken);

        var output = new List<ProjectProcurementTrackingItem>(published.Count);
        foreach (var line in published.OrderBy(item => item.BomKind).ThenBy(item => item.Item.Sequence).ThenBy(item => item.Item.Id))
        {
            var material = await materials.FindMaterialBySourceBomItemAsync(line.Item.Id, cancellationToken);
            var materialCode = Clean(material?.U9ItemCode) ?? Clean(material?.MaterialCode) ?? line.Item.DrawingNumber.Trim();
            var matching = sourceRows
                .Where(row => string.Equals(row.MaterialCode, materialCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var requisitions = matching
                .Where(row => row.RecordKind == U9ProcurementRecordKinds.PurchaseRequisition)
                .ToArray();
            var requisitionIds = requisitions.Select(row => row.LineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orders = matching
                .Where(row => row.RecordKind == U9ProcurementRecordKinds.PurchaseOrder
                    && (string.IsNullOrWhiteSpace(row.SourcePrLineId) || requisitionIds.Count == 0 || requisitionIds.Contains(row.SourcePrLineId)))
                .ToArray();

            var activeRequisitions = requisitions.Where(row => !row.IsCanceled).ToArray();
            var activeOrders = orders.Where(row => !row.IsCanceled).ToArray();
            var details = requisitions.Select(row => Detail(row, "请购", "项目+子项目+料号"))
                .Concat(orders.Select(row => Detail(row, "采购", requisitionIds.Contains(row.SourcePrLineId ?? string.Empty) ? "源请购行" : "项目+子项目+料号")))
                .OrderBy(detail => detail.Kind)
                .ThenBy(detail => detail.DocumentNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(detail => detail.LineNumber)
                .ToArray();

            output.Add(new ProjectProcurementTrackingItem(
                output.Count + 1,
                root.Code,
                subprojectCode,
                materialCode,
                line.Item.Name,
                Clean(line.Item.Specification) ?? Clean(material?.Specification),
                Clean(line.Item.Remark),
                Clean(line.Item.Brand) ?? Clean(material?.Brand),
                line.Quantity,
                line.BomKind,
                line.PackageNumbers,
                DistinctDocuments(requisitions),
                AggregateStatus(requisitions, "未请购"),
                Latest(requisitions.Select(row => row.DeliveryDate)),
                DistinctDocuments(orders),
                AggregateStatus(orders, "未采购"),
                activeOrders.Sum(row => row.PurchaseQuantity),
                activeOrders.Sum(row => row.ArrivedQuantity),
                JoinText(activeOrders.Select(row => row.PurchaseRemark)),
                Latest(activeOrders.Select(row => row.DeliveryDate)),
                Latest(activeOrders.Select(row => row.LatestDeliveryDate ?? row.DeliveryDate)),
                activeRequisitions.Sum(row => row.RequestedQuantity),
                activeRequisitions.Sum(row => row.ApprovedQuantity),
                details));
        }

        return new ProjectProcurementTrackingResult(
            projectId,
            root.Code,
            subprojectCode,
            ProcurementTrackingAggregation.Group(output, sourceRows),
            latestRun?.Status == U9InventorySyncStatus.Succeeded ? latestRun.CompletedAt : null,
            latestRun?.Status == U9InventorySyncStatus.Failed ? latestRun.LastError : null,
            published.Count > 0);
    }

    public async Task<U9ProcurementSyncSettings> GetSettingsAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireManageAsync(actor, role, cancellationToken);
        return await procurement.GetSettingsAsync(cancellationToken);
    }

    public Task<U9ProcurementSyncRun?> GetLatestRunAsync(CancellationToken cancellationToken) =>
        procurement.GetLatestRunAsync(cancellationToken);

    public async Task<U9ProcurementSyncSettings> UpdateSettingsAsync(
        bool autoSyncEnabled,
        int syncIntervalMinutes,
        string queryPath,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireManageAsync(actor, role, cancellationToken);
        if (syncIntervalMinutes is < 15 or > 1440)
            throw new PdmRuleException("采购跟踪自动同步间隔必须在15分钟到24小时之间。");
        var normalizedPath = Required(queryPath, "采购查询接口路径");
        if (!normalizedPath.StartsWith("/webapi/", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("采购查询接口路径必须以/webapi/开头。");
        var now = timeProvider.GetUtcNow();
        var current = await procurement.GetSettingsAsync(cancellationToken);
        var saved = await procurement.SaveSettingsAsync(current with
        {
            AutoSyncEnabled = autoSyncEnabled,
            SyncIntervalMinutes = syncIntervalMinutes,
            QueryPath = normalizedPath,
            UpdatedBy = actor,
            UpdatedAt = now
        }, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), now, actor, "u9.procurement.settings.update", nameof(U9ProcurementSyncSettings), "u9c",
            $"采购跟踪自动同步：{(autoSyncEnabled ? "启用" : "关闭")}；间隔：{syncIntervalMinutes}分钟；查询路径：{normalizedPath}。"), cancellationToken);
        return saved;
    }

    public async Task DemandProjectViewAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");
    }

    public async Task<U9ProcurementSyncRun> SynchronizeAsync(
        string actor,
        string triggerKind,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var run = new U9ProcurementSyncRun(Guid.NewGuid(), Required(triggerKind, "触发方式"),
            U9InventorySyncStatus.Running, 0, 0, 0, null, startedAt, null);
        await procurement.SaveRunAsync(run, cancellationToken);
        try
        {
            var configuration = await RequireConfigurationAsync(cancellationToken);
            var settings = await procurement.GetSettingsAsync(cancellationToken);
            var projectCodes = (await repository.ListProjectsAsync(cancellationToken))
                .Where(project => project.IsActive && !string.IsNullOrWhiteSpace(project.Code))
                .Select(project => project.Code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var result = await QueryAsync(configuration, settings.QueryPath, projectCodes, cancellationToken);
            EnsureSuccessful(result);
            var refreshedAt = timeProvider.GetUtcNow();
            await procurement.ReplaceSnapshotAsync(run.Id, result.Rows, refreshedAt, cancellationToken);
            run = run with
            {
                Status = U9InventorySyncStatus.Succeeded,
                SourceRowCount = result.Rows.Count,
                StoredRowCount = result.Rows.Count,
                ProjectCount = projectCodes.Length,
                CompletedAt = refreshedAt
            };
            await procurement.SaveRunAsync(run, cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(
                Guid.NewGuid(), refreshedAt, actor, "u9.procurement.full-sync", nameof(U9ProcurementSyncRun), run.Id.ToString(),
                $"U9C请购、采购状态全量只读刷新：源明细{run.SourceRowCount}，项目范围{run.ProjectCount}；完整成功后切换快照，未查询价格或财务字段，未执行U9C写入。"), cancellationToken);
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
            await procurement.SaveRunAsync(run, CancellationToken.None);
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
            await procurement.SaveRunAsync(run, CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyList<PublishedBomLine>> ListPublishedBomAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var packages = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
            .Where(package => package.State == ReleasePackageState.Published && package.PublishedAt.HasValue)
            .OrderBy(package => package.PublishedAt ?? package.CreatedAt)
            .ToArray();
        var lines = new List<PublishedBomLine>();
        AddCategory(lines, packages, "标准件", package => package.Scope is ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement or ReleaseScope.LegacyCombined,
            package => package.StandardBomSnapshot, includeLongLead: true);
        AddCategory(lines, packages, "电气件", package => package.Scope is ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement or ReleaseScope.LegacyCombined,
            package => package.ElectricalBomSnapshot, includeLongLead: false);
        return lines
            .GroupBy(line => (line.Item.Id, line.BomKind))
            .Select(group => new PublishedBomLine(
                group.Last().Item,
                group.Key.BomKind,
                string.Join("、", group.Select(item => item.PackageNumbers).Distinct(StringComparer.OrdinalIgnoreCase)),
                group.Sum(item => item.Quantity)))
            .ToArray();
    }

    private static void AddCategory(
        ICollection<PublishedBomLine> target,
        IReadOnlyList<ReleasePackage> packages,
        string label,
        Func<ReleasePackage, bool> isBaseline,
        Func<ReleasePackage, IReadOnlyList<BomItem>> items,
        bool includeLongLead)
    {
        var baseline = packages.LastOrDefault(package => isBaseline(package) && items(package).Count > 0);
        if (baseline is not null)
        {
            foreach (var item in EffectiveItems(items(baseline)))
                target.Add(new(item, label, baseline.Number, item.Quantity * Math.Max(1, baseline.WholeSetMultiplier)));
        }
        if (!includeLongLead) return;
        var baselineAt = baseline?.PublishedAt;
        foreach (var package in packages.Where(package => package.Scope == ReleaseScope.StandardLongLead
                     && (!baselineAt.HasValue || package.PublishedAt > baselineAt)))
            foreach (var item in EffectiveItems(package.StandardBomSnapshot))
                target.Add(new(item, label, package.Number, item.Quantity * Math.Max(1, package.WholeSetMultiplier)));
    }

    private async Task<U9ProcurementQueryResult> QueryAsync(
        U9MaterialIntegrationConfiguration configuration,
        string queryPath,
        IReadOnlyCollection<string> projectCodes,
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
        return await procurementClient.QueryProcurementAsync(
            configuration.BaseUrl, queryPath, authentication.Token,
            configuration.OrganizationCode, projectCodes, cancellationToken);
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
            throw new PdmRuleException("U9C连接尚未完整配置，不能刷新采购跟踪状态。");
        return configuration;
    }

    private async Task RequireManageAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权维护U9C采购跟踪同步设置。");
    }

    internal static ProcurementDocumentDetail Detail(U9ProcurementSnapshotRow row, string kind, string matchKind) => new(
        kind,
        row.DocumentNumber,
        row.LineNumber,
        DescribeStatus(row.RecordKind, row.LineStatus, row.IsCanceled),
        row.LineStatus,
        row.IsCanceled,
        row.RecordKind == U9ProcurementRecordKinds.PurchaseRequisition ? row.RequestedQuantity : row.PurchaseQuantity,
        row.ArrivedQuantity,
        row.PurchaseRemark,
        row.BusinessDate,
        row.DeliveryDate,
        row.LatestDeliveryDate,
        matchKind);

    private static string AggregateStatus(IReadOnlyList<U9ProcurementSnapshotRow> rows, string empty) => rows.Count == 0
        ? empty
        : string.Join("、", rows.Select(row => DescribeStatus(row.RecordKind, row.LineStatus, row.IsCanceled)).Distinct());

    internal static string DescribeStatus(string recordKind, int status, bool canceled)
    {
        if (canceled) return "已取消";
        // U9 native enums: PRStatusEnum / PODOCStatusEnum (zh-CN, verified 2026-09-09).
        return recordKind == U9ProcurementRecordKinds.PurchaseRequisition
            ? status switch { 0 => "开立", 1 => "核准中", 2 => "已核准", 3 => "自然关闭", 4 => "短缺关闭", 5 => "超额关闭", _ => $"状态{status}" }
            : status switch { 0 => "开立", 1 => "审核中", 2 => "已核准", 3 => "自然关闭", 4 => "短缺关闭", 5 => "超额关闭", _ => $"状态{status}" };
    }

    private static IReadOnlyList<string> DistinctDocuments(IEnumerable<U9ProcurementSnapshotRow> rows) => rows
        .Select(row => row.DocumentNumber)
        .Where(number => !string.IsNullOrWhiteSpace(number))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(number => number, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> values) => values
        .Where(value => value.HasValue)
        .Select(value => value!.Value)
        .DefaultIfEmpty()
        .Max() is var maximum && maximum == default ? null : maximum;

    private static string? JoinText(IEnumerable<string?> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return items.Length == 0 ? null : string.Join("；", items);
    }

    private static IEnumerable<BomItem> EffectiveItems(IEnumerable<BomItem> items) =>
        items.Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval);

    private static void EnsureSuccessful(U9ProcurementQueryResult result)
    {
        if (result.ResponseCode != 0 || !result.Success)
            throw new PdmRuleException($"U9C采购跟踪查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");
    }

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? throw new PdmRuleException($"{field}不能为空。") : normalized;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PublishedBomLine(BomItem Item, string BomKind, string PackageNumbers, decimal Quantity);
}
