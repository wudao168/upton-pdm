using System.Globalization;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class StockedMaterialService(IStockedMaterialStore store, StockedMaterialGate gate,
    IMaterialRepository materials, IStandardLibraryRepository categories, IPdmRepository repository,
    IU9OpenApiClient client, IU9InventoryClient inventoryClient, IU9InventoryRepository inventory,
    IU9SecretProtector secrets, TimeProvider clock, StandardLibraryImportService materialImport)
{
    public async Task<StockedMaterialScope> RequireScopeAsync(string actor, UserRole role, bool manage, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, manage ? PermissionCodes.StandardLibraryManage : PermissionCodes.StandardLibraryView, ct))
            throw new UnauthorizedAccessException("当前角色无权" + (manage ? "维护" : "查看") + "常备件。");
        var company = TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。");
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        return new(company, config.EnterpriseCode, config.OrganizationCode);
    }

    public async Task<StockedMaterialPage> ListAsync(StockedMaterialScope scope, Guid? categoryId, string? query,
        string? brand, int page, int pageSize, CancellationToken ct)
    {
        var catalog = await store.ReadAsync(scope, ct);
        var allCategories = await categories.ListCategoriesAsync(true, ct);
        var categoryScope = categoryId is null ? null : Descendants(allCategories, categoryId.Value);
        var rows = new List<StockedMaterialRow>();
        foreach (var group in catalog.Memberships.GroupBy(item => item.MaterialId))
        {
            if (categoryScope is not null && !group.Any(item => categoryScope.Contains(item.CategoryId))) continue;
            var management = catalog.ManagementMaterials?.FirstOrDefault(item => item.Id == group.Key);
            var material = management?.Display(catalog.LastSuccessAt ?? clock.GetUtcNow()) ?? await materials.FindMaterialAsync(group.Key, ct);
            if (material is null || material.IsArchived) continue;
            if (!string.IsNullOrWhiteSpace(brand) && !string.Equals(material.Brand, brand.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(query) && !new[] { material.Name, material.MaterialCode, material.Specification, material.Brand }
                    .Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true)) continue;
            var snapshot = catalog.Snapshots.FirstOrDefault(item => item.MaterialId == group.Key) ?? new(group.Key);
            var cover = material.CoverImageAttachmentId is Guid id ? await materials.FindMaterialAttachmentAsync(id, ct) : null;
            rows.Add(new(material, allCategories.Where(category => group.Any(item => item.CategoryId == category.Id)).ToArray(), snapshot, cover,
                management is not null || StockedMaterialPolicy.ManagementOnlyCategory(material.CategoryCode)));
        }
        var ordered = rows.OrderByDescending(item => item.Snapshot.IsLowStock)
            .ThenBy(item => item.Material.MaterialCode, StringComparer.OrdinalIgnoreCase).ToArray();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        return new(ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), ordered.Length,
            rows.Count(item => item.Snapshot.IsLowStock), page, pageSize, scope, catalog.LastSuccessAt, catalog.LastError,
            rows.Count(item => item.Snapshot.IsFormal), rows.Count(item => !item.Snapshot.IsFormal), rows.Count(item => item.ManagementOnly));
    }

    public async Task AddAsync(StockedMaterialScope scope, AddStandardLibraryMaterialsCommand input, string actor, CancellationToken ct)
    {
        if (input.MaterialIds is null || input.CategoryIds is null || input.MaterialIds.Count is < 1 or > 100 || input.CategoryIds.Count is < 1 or > 100)
            throw new PdmRuleException("请选择1至100个料品和分类。");
        var active = await categories.ListCategoriesAsync(false, ct);
        if (input.CategoryIds.Any(id => !active.Any(item => item.Id == id))) throw new PdmRuleException("只能加入已启用的标准分类。");
        foreach (var id in input.MaterialIds) await RequireMaterialAsync(id, ct);
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            var members = catalog.Memberships.Concat(from category in input.CategoryIds.Distinct() from material in input.MaterialIds.Distinct()
                select new StandardLibraryMembership(category, material, actor, clock.GetUtcNow()))
                .DistinctBy(item => (item.CategoryId, item.MaterialId)).ToArray();
            // 新收录先作为常备配置草稿；仅已回查生效的普通常备件触发标准库收录。
            var formalIds = catalog.Snapshots.Where(item => item.IsFormal).Select(item => item.MaterialId).ToHashSet();
            await categories.AddMembershipsAsync(members.Where(item => formalIds.Contains(item.MaterialId)).ToArray(), ct);
            await SaveAsync(catalog with { Memberships = members }, ct);
            await AuditAsync(actor, "stocked-material.add", scope, $"收录{input.MaterialIds.Distinct().Count()}个常备配置；新配置为草稿，未修改料品批准状态和U9C参数。", ct);
        }
        finally { gate.Semaphore.Release(); }
    }

    public async Task RemoveAsync(StockedMaterialScope scope, Guid materialId, Guid? categoryId, string actor, CancellationToken ct)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            if (!catalog.Memberships.Any(item => item.MaterialId == materialId)) return;
            var members = catalog.Memberships.Where(item => item.MaterialId != materialId || categoryId.HasValue && item.CategoryId != categoryId).ToArray();
            if (!members.Any(item => item.MaterialId == materialId))
            {
                var material = await ResolveManagedMaterialAsync(catalog, materialId, ct);
                var observed = catalog.Snapshots.FirstOrDefault(item => item.MaterialId == materialId) ?? new(materialId);
                try
                {
                    var config = await ConfigurationAsync(scope, false, ct);
                    var token = await TokenAsync(config, ct);
                    var live = await ReadPlanAsync(scope, config, token, material.MaterialCode, ct);
                    observed = observed with { Plan = live, LastError = null };
                    if (live.SafetyStockQuantity > 0 || !PlanningMatches(live, false))
                    {
                        config = await ConfigurationAsync(scope, true, ct);
                        observed = observed with { Plan = await ModifyAndReadPlanAsync(scope, config, token, material.MaterialCode,
                            new(0, live.ReorderQuantity ?? 0, live.SyncedAt), ct, safetyOnly: true) };
                    }
                    if (observed.Plan!.SafetyStockQuantity != 0)
                        throw new PdmRuleException("U9C回查的安全库存量尚未清零，未移除PLM收录，请核对接口后重试。");
                    if (!PlanningMatches(observed.Plan, false))
                        throw new PdmRuleException("U9C回查的四项规划设置尚未恢复为非常备件设置，未移除PLM收录，请核对后重试。");
                    await materials.SaveStockPlansAsync([observed.Plan!], ct);
                    await AuditAsync(actor, "stocked-material.cancel", scope,
                        $"取消料号{material.MaterialCode}常备：U9C安全库存量{live.SafetyStockQuantity}→0，四项规划设置恢复为MRP规划、空、不勾选、空，回查确认；再订货量和料品主档保留。", ct);
                }
                catch
                {
                    await SaveAsync(catalog with { Snapshots = catalog.Snapshots.Where(item => item.MaterialId != materialId)
                        .Append(observed with { LastError = "取消常备未完成，已保留PLM收录，请刷新回查U9C结果后重试。" }).ToArray() }, CancellationToken.None);
                    throw;
                }
            }
            await SaveAsync(catalog with { Memberships = members, Snapshots = catalog.Snapshots.Where(item => members.Any(member => member.MaterialId == item.MaterialId)).ToArray(),
                ManagementMaterials = catalog.ManagementMaterials?.Where(item => members.Any(member => member.MaterialId == item.Id)).ToArray() }, ct);
            await AuditAsync(actor, "stocked-material.remove", scope, $"移除常备件{materialId}的分类成员；{(members.Any(item => item.MaterialId == materialId) ? "仍保留其他常备目录收录" : "已确认U9C安全库存量为0，保留标准库收录和料品主档")}。", ct);
        }
        finally { gate.Semaphore.Release(); }
    }

    public async Task<StockedMaterialSettingsView> SettingsAsync(StockedMaterialScope scope, CancellationToken ct)
    {
        var catalog = await store.ReadAsync(scope, ct);
        var users = await RecipientOptionsAsync(scope.CompanyId, ct);
        return new(catalog.Settings with { Recipients = catalog.Settings.Recipients ?? [] }, catalog.RowVersion, users, ResolveRecipients(catalog.Settings, users), scope, catalog.LastSuccessAt, catalog.LastError);
    }

    public async Task<StockedMaterialSettingsView> SaveSettingsAsync(StockedMaterialScope scope, StockedMaterialSettings settings,
        long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct))
            throw new UnauthorizedAccessException("当前角色无权维护常备件同步与提醒设置。");
        if (settings.SyncIntervalMinutes is < 15 or > 1440) throw new PdmRuleException("回读间隔必须在15分钟至24小时之间。");
        var options = await RecipientOptionsAsync(scope.CompanyId, ct);
        if (settings.Recipients?.Count > 100 || (settings.Recipients ?? []).Any(name => !options.Any(user => user.Username.Equals(name, StringComparison.OrdinalIgnoreCase))))
            throw new PdmRuleException("请选择当前公司的有效接收人员，最多100人。");
        if (settings.NotifyLowStock && !settings.UseStandardAssistant && settings.Recipients?.Count is not > 0)
            throw new PdmRuleException("启用低库存提醒时，请至少选择一名接收人员。");
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            if (catalog.RowVersion != expectedVersion) throw new PdmConflictException("常备件设置或回读数据已更新，请刷新后重新保存。");
            await SaveAsync(catalog with { Settings = settings with { Recipients = (settings.Recipients ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() } }, ct);
            await AuditAsync(actor, "stocked-material.settings", scope, $"回读间隔{settings.SyncIntervalMinutes}分钟；低库存提醒{(settings.NotifyLowStock ? "启用" : "关闭")}。", ct);
        }
        finally { gate.Semaphore.Release(); }
        return await SettingsAsync(scope, ct);
    }

    public async Task SynchronizeAsync(StockedMaterialScope scope, string actor, CancellationToken ct, bool scheduled = false)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            var started = clock.GetUtcNow();
            if (scheduled && catalog.SourceDiscoveryAttempted && catalog.LastAttemptAt.HasValue && started - catalog.LastAttemptAt < TimeSpan.FromMinutes(catalog.Settings.SyncIntervalMinutes)) return;
            catalog = await SaveAsync(catalog with { LastAttemptAt = started, SourceDiscoveryAttempted = true }, ct);
            try
            {
                var config = await ConfigurationAsync(scope, false, ct);
                var token = await TokenAsync(config, ct);
                var source = await ExternalAsync(() => client.QueryStockPlansAsync(config.BaseUrl, token, scope.EnterpriseCode,
                    scope.OrganizationCode, started, ct));
                if (source is null) throw new PdmRuleException("U9C常备件清单查询不可用，已保留上次成功数据。");
                var plans = source.Where(plan => plan.EnterpriseCode == scope.EnterpriseCode && plan.OrganizationCode == scope.OrganizationCode).ToList();
                await materials.SaveStockPlansAsync(plans, ct);
                var previousIds = catalog.Memberships.Select(item => item.MaterialId).ToHashSet();
                var discovery = await DiscoverMembershipsAsync(catalog, plans, actor, ct);
                catalog = await SaveAsync(discovery.Catalog, ct);
                var managed = new List<PdmMaterial>();
                foreach (var id in catalog.Memberships.Select(item => item.MaterialId).Distinct())
                {
                    var item = catalog.ManagementMaterials?.FirstOrDefault(item => item.Id == id)?.Display(started)
                        ?? await materials.FindMaterialAsync(id, ct);
                    if (item is { IsArchived: false, U9SyncConfirmed: true }) managed.Add(item);
                }
                var observed = plans.Select(plan => plan.MaterialCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var batch in managed.Where(item => !observed.Contains(item.MaterialCode)).Chunk(100))
                {
                    var result = await ExternalAsync(() => client.QueryStockPlansAsync(config.BaseUrl, token, scope.EnterpriseCode, scope.OrganizationCode,
                        started, ct, batch.Select(item => item.MaterialCode).ToArray()));
                    if (result is null) throw new PdmRuleException("U9C库存规划查询不可用，已保留上次成功数据。");
                    plans.AddRange(result.Where(plan => plan.EnterpriseCode == scope.EnterpriseCode && plan.OrganizationCode == scope.OrganizationCode));
                }
                var stockRows = new List<U9InventorySourceRow>();
                if (managed.Count > 0)
                {
                    var inventorySettings = await inventory.GetSettingsAsync(ct);
                    var result = await ExternalAsync(() => inventoryClient.QueryInventoryAsync(config.BaseUrl, inventorySettings.QueryPath, token,
                        scope.OrganizationCode, managed.Count == 1 ? managed[0].MaterialCode : null, ct));
                    if (result.ResponseCode != 0 || !result.Success) throw new PdmRuleException("U9C库存数量回读失败，已保留上次成功数据，请检查库存接口。");
                    var codes = managed.Select(item => item.MaterialCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    stockRows.AddRange(result.Rows.Where(row => row.OrganizationCode == scope.OrganizationCode && codes.Contains(row.MaterialCode)));
                }
                var through = DateOnly.FromDateTime(started.ToOffset(TimeSpan.FromHours(8)).Date);
                var from = through.AddMonths(-3);
                var purchases = await ExternalAsync(() => client.QueryStockedPurchasesAsync(config.BaseUrl, token, scope.OrganizationCode,
                    plans.Where(plan => plan.IsStocked).Select(plan => plan.MaterialCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), from, through, ct))
                    ?? throw new PdmRuleException("U9C请购采购信息查询不可用，已保留上次成功数据。");
                var currentPurchases = purchases.Where(row => row.OrganizationCode == scope.OrganizationCode && !row.IsCanceled
                    && row.BusinessDate >= from && row.BusinessDate <= through).DistinctBy(row => row.Id).ToArray();
                var snapshots = managed.Select(material =>
                {
                    var before = catalog.Snapshots.FirstOrDefault(item => item.MaterialId == material.Id) ?? new(material.Id);
                    var matching = plans.Where(plan => plan.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var requested = currentPurchases.Where(row => row.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length == 1 && requested.Any(row => row.UnitCode != (matching[0].UnitCode ?? material.UnitCode)))
                        throw new PdmRuleException($"料号{material.MaterialCode}的请购单位与库存单位不一致，已保留上次汇总，请核对单位换算。");
                    var outstanding = requested.Where(row => row.PurchaseQuantity > row.ArrivedQuantity).ToArray();
                    return matching.Length != 1 ? before with { LastError = "U9C未返回唯一的当前组织库存规划，请核对料号及组织。" }
                        : before with { Plan = matching[0], StockQuantity = stockRows.Where(row => row.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)).Sum(row => row.StockQuantity),
                            InventorySyncedAt = started, LastError = null,
                            HasPendingRequisition = matching[0].IsStocked && requested.Any(row => row.RequestedQuantity > row.ArrivedQuantity),
                            PurchaseQuantity = outstanding.Sum(row => row.PurchaseQuantity), ArrivedQuantity = outstanding.Sum(row => row.ArrivedQuantity), ProcurementSyncedAt = started,
                            RequisitionDate = requested.Where(row => row.RequestedQuantity > row.ArrivedQuantity).Select(row => (DateOnly?)row.BusinessDate).Max(),
                            ExpectedArrivalDate = outstanding.Select(row => row.ExpectedArrivalDate).Max(),
                            InventoryRows = stockRows.Where(row => row.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)).ToArray() };
                }).ToArray();
                await materials.SaveStockPlansAsync(snapshots.Where(item => item.LastError is null && item.Plan is not null).Select(item => item.Plan!).ToArray(), ct);
                var errors = new List<string>();
                if (discovery.Failures.Count > 0) errors.Add($"{discovery.Failures.Count}个U9C常备件未能导入：{string.Join("；", discovery.Failures.Take(5))}");
                if (snapshots.Any(item => item.LastError is not null)) errors.Add("部分料品库存规划回读失败，请查看列表中的同步状态。");
                var error = errors.Count == 0 ? null : string.Join("；", errors);
                catalog = catalog with { Snapshots = snapshots, LastError = error, LastSuccessAt = error is null ? started : catalog.LastSuccessAt,
                    BusinessSnapshotReady = catalog.BusinessSnapshotReady || error is null };
                await EnsureFormalStandardMembershipsAsync(catalog, actor, ct);
                // 先固化本次低库存事件，再发送消息；失败重试使用相同来源键，避免重复消息。
                catalog = PrepareEpisodes(catalog);
                catalog = await SaveAsync(catalog, ct);
                await NotifyAsync(catalog, managed, ct);
                await AuditAsync(actor, "stocked-material.readback", scope, $"U9C发现{plans.Count(plan => plan.IsStocked)}个常备件，新增收录{managed.Count(item => !previousIds.Contains(item.Id))}个；回读{managed.Count}个料品的库存规划及现存量；{error ?? "同步成功"}", ct);
            }
            catch (Exception error)
            {
                var current = await store.ReadAsync(scope, CancellationToken.None);
                await SaveAsync(current with { LastAttemptAt = started, LastError = error is PdmRuleException ? error.Message : "常备件回读未完成，已保留上次成功数据，请检查U9C连接后重试。" }, CancellationToken.None);
                throw;
            }
        }
        finally { gate.Semaphore.Release(); }
    }

    private async Task<(StockedMaterialCatalog Catalog, IReadOnlyList<string> Failures)> DiscoverMembershipsAsync(
        StockedMaterialCatalog catalog, IReadOnlyList<MaterialStockPlan> plans, string actor, CancellationToken ct)
    {
        var codes = plans.Where(plan => plan.IsStocked).Select(plan => plan.MaterialCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length == 0) return (catalog, []);
        var sourceCategories = (await materials.ListCategoriesAsync(true, ct)).ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
        var directories = (await categories.ListCategoriesAsync(true, ct)).ToList();
        var directoryIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        async Task<Guid> DirectoryAsync(string code)
        {
            if (directoryIds.TryGetValue(code, out var known)) return known;
            if (!sourceCategories.TryGetValue(code, out var source) || !source.IsActive) throw new PdmRuleException($"U9C分类{code}尚未启用或未同步到PLM分类树。");
            Guid? parent = source.ParentCode is { Length: > 0 } parentCode ? await DirectoryAsync(parentCode) : null;
            var directory = directories.FirstOrDefault(item => item.ParentId == parent && item.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (directory is { IsActive: false }) throw new PdmRuleException($"目录分类“{source.Name}”已停用，请先核对分类设置。");
            if (directory is null)
            {
                directory = await categories.CreateCategoryAsync(new(Guid.NewGuid(), source.Name, parent, source.SortOrder, true, actor, clock.GetUtcNow(), actor, clock.GetUtcNow(), 1), ct);
                directories.Add(directory);
            }
            directoryIds[code] = directory.Id;
            return directory.Id;
        }
        var failures = new List<string>();
        var read = new List<U9MaterialReadRow>();
        foreach (var batch in codes.Chunk(200)) read.AddRange(await ExternalAsync(() => materialImport.ReadForSynchronizationAsync(batch, ct)));
        var management = (catalog.ManagementMaterials ?? []).ToDictionary(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase);
        var successful = new List<(Guid Id, string Code, string Category)>();
        foreach (var row in read)
        {
            if (row.Item is not { } item) { failures.Add($"{row.Code}：{row.Error}"); continue; }
            if (!StockedMaterialPolicy.ManagementOnlyCategory(item.U9CategoryCode)) continue;
            if (!sourceCategories.TryGetValue(item.U9CategoryCode!, out var source) || !source.IsActive)
            { failures.Add($"{row.Code}：原料品分类尚未启用。"); continue; }
            var id = management.GetValueOrDefault(row.Code)?.Id ?? Guid.NewGuid();
            management[row.Code] = new(id, row.Code, item.U9ItemName!, item.U9Specification, item.U9Brand,
                U9UnitCatalog.NormalizeInbound(item.U9UnitCode!), source.Code, source.Name, item.U9ItemId);
            successful.Add((id, row.Code, source.Code));
        }
        var ordinary = read.Where(row => row.Item is not null && !StockedMaterialPolicy.ManagementOnlyCategory(row.Item.U9CategoryCode)).ToArray();
        foreach (var batch in ordinary.Chunk(200))
        {
            var imported = await ExternalAsync(() => materialImport.ImportForSynchronizationAsync(batch.Select(row => row.Code).ToArray(), actor, ct, batch, addToLibrary: false));
            failures.AddRange(imported.Rows.Where(row => row.Status == "Failed").Select(row => $"{row.MaterialCode}：{row.Message}"));
            var valid = imported.Rows.Where(row => row.Status != "Failed").Select(row => row.MaterialCode).ToArray();
            foreach (var material in await materials.FindMaterialsByCodesAsync(valid, ct)) successful.Add((material.Id, material.MaterialCode, material.CategoryCode!));
        }
        var standardMembers = (await categories.ListMembershipsAsync(ct)).GroupBy(item => item.MaterialId).ToDictionary(group => group.Key, group => group.ToArray());
        var members = catalog.Memberships.ToList();
        var snapshots = catalog.Snapshots.ToList();
        foreach (var item in successful)
        {
            var current = members.Where(member => member.MaterialId == item.Id).ToArray();
            var classified = current.Any(member => directories.Any(directory => directory.Id == member.CategoryId && !StandardLibraryDefaults.IsUncategorized(directory)));
            if (!classified)
            {
                var standard = standardMembers.GetValueOrDefault(item.Id)?.Where(member => directories.Any(directory => directory.Id == member.CategoryId && !StandardLibraryDefaults.IsUncategorized(directory))).ToArray() ?? [];
                members.RemoveAll(member => member.MaterialId == item.Id);
                if (standard.Length > 0) members.AddRange(standard.Select(member => new StandardLibraryMembership(member.CategoryId, item.Id, actor, clock.GetUtcNow())));
                else members.Add(new(await DirectoryAsync(item.Category), item.Id, actor, clock.GetUtcNow()));
            }
            if (!snapshots.Any(snapshot => snapshot.MaterialId == item.Id))
                snapshots.Add(new(item.Id, plans.Single(plan => plan.MaterialCode.Equals(item.Code, StringComparison.OrdinalIgnoreCase))));
        }
        return (catalog with { Memberships = members, Snapshots = snapshots, ManagementMaterials = management.Values.ToArray() }, failures);
    }

    private Task<PdmMaterial> ResolveManagedMaterialAsync(StockedMaterialCatalog catalog, Guid id, CancellationToken ct) =>
        catalog.ManagementMaterials?.FirstOrDefault(item => item.Id == id) is { } management
            ? Task.FromResult(management.Display(clock.GetUtcNow())) : RequireMaterialAsync(id, ct);

    private async Task EnsureFormalStandardMembershipsAsync(StockedMaterialCatalog catalog, string actor, CancellationToken ct)
    {
        var management = (catalog.ManagementMaterials ?? []).Select(item => item.Id).ToHashSet();
        var formal = catalog.Snapshots.Where(item => item.IsFormal && !management.Contains(item.MaterialId)).Select(item => item.MaterialId).ToHashSet();
        var memberships = catalog.Memberships.Where(item => formal.Contains(item.MaterialId)).Select(item => item with { AddedBy = actor, AddedAt = clock.GetUtcNow() }).ToArray();
        await categories.AddMembershipsAsync(memberships, ct);
    }

    public async Task<StockedMaterialSnapshot> ReadPlanForMaintenanceAsync(StockedMaterialScope scope, Guid materialId, string actor, CancellationToken ct)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            if (!catalog.Memberships.Any(item => item.MaterialId == materialId)) throw new PdmNotFoundException("该料品尚未加入常备件管理。");
            var material = await ResolveManagedMaterialAsync(catalog, materialId, ct);
            var config = await ConfigurationAsync(scope, false, ct);
            var plan = await ReadPlanAsync(scope, config, await TokenAsync(config, ct), material.MaterialCode, ct);
            await materials.SaveStockPlansAsync([plan], ct);
            var snapshot = (catalog.Snapshots.FirstOrDefault(item => item.MaterialId == materialId) ?? new(materialId)) with { Plan = plan, LastError = null };
            catalog = catalog with { Snapshots = catalog.Snapshots.Where(item => item.MaterialId != materialId).Append(snapshot).ToArray() };
            await EnsureFormalStandardMembershipsAsync(catalog, actor, ct);
            await SaveAsync(catalog, ct);
            return snapshot;
        }
        finally { gate.Semaphore.Release(); }
    }
    public async Task<StockedMaterialSnapshot> MaintainAsync(StockedMaterialScope scope, Guid materialId, SaveStockedPlan input, string actor, CancellationToken ct)
    {
        if (input.SafetyStockQuantity < 0 || input.ReorderQuantity < 0 || input.SafetyStockQuantity > 999999999999m || input.ReorderQuantity > 999999999999m
            || decimal.Round(input.SafetyStockQuantity, 9) != input.SafetyStockQuantity || decimal.Round(input.ReorderQuantity, 9) != input.ReorderQuantity)
            throw new PdmRuleException("安全库存量和再订货量必须为非负数，最多12位整数、9位小数。");
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(scope, ct);
            if (!catalog.Memberships.Any(item => item.MaterialId == materialId)) throw new PdmNotFoundException("该料品尚未加入当前常备件分类。");
            var material = await ResolveManagedMaterialAsync(catalog, materialId, ct);
            var before = catalog.Snapshots.FirstOrDefault(item => item.MaterialId == materialId) ?? new(materialId);
            if (before.Plan is null) throw new PdmRuleException("请先刷新回读U9C库存规划，再维护库存参数。");
            if (before.Plan?.SyncedAt != input.ExpectedSyncedAt) throw new PdmConflictException("库存规划已更新，请刷新后重新维护。");
            var config = await ConfigurationAsync(scope, true, ct);
            var token = await TokenAsync(config, ct);
            var live = await ReadPlanAsync(scope, config, token, material.MaterialCode, ct);
            if (before.Plan is not null && (live.SafetyStockQuantity != before.Plan.SafetyStockQuantity || live.ReorderQuantity != before.Plan.ReorderQuantity))
                throw new PdmConflictException("U9C库存规划已被修改，请先刷新列表，再重新维护。");
            if (!live.IsEffective) throw new PdmRuleException("U9C料品已失效，不能维护库存规划。");
            try
            {
                var verified = await ModifyAndReadPlanAsync(scope, config, token, material.MaterialCode, input, ct);
                await materials.SaveStockPlansAsync([verified], ct);
                var matches = verified.SafetyStockQuantity == input.SafetyStockQuantity && verified.ReorderQuantity == input.ReorderQuantity && verified.IsEffective
                    && PlanningMatches(verified, input.SafetyStockQuantity > 0);
                var after = before with { Plan = verified, LastError = matches ? null : "U9C回查的库存数量或四项规划设置与本次维护值不一致，请核对后重试。" };
                catalog = catalog with { Snapshots = catalog.Snapshots.Where(item => item.MaterialId != materialId).Append(after).ToArray() };
                await EnsureFormalStandardMembershipsAsync(catalog, actor, ct);
                await SaveAsync(catalog, ct);
                await AuditAsync(actor, "stocked-material.maintain", scope,
                    $"料号{material.MaterialCode}：安全库存量{live.SafetyStockQuantity}→{verified.SafetyStockQuantity}；再订货量{live.ReorderQuantity}→{verified.ReorderQuantity}；联动四项{(input.SafetyStockQuantity > 0 ? "常备件" : "非常备件")}规划设置，回查{(matches ? "一致" : "不一致")}。", ct);
                if (!matches) throw new PdmRuleException(after.LastError!);
                return after;
            }
            catch
            {
                var current = await store.ReadAsync(scope, CancellationToken.None);
                var snapshot = current.Snapshots.FirstOrDefault(item => item.MaterialId == materialId) ?? before;
                await SaveAsync(current with { Snapshots = current.Snapshots.Where(item => item.MaterialId != materialId)
                    .Append(snapshot with { LastError = "库存规划同步未完成，请刷新回查U9C结果后再维护。" }).ToArray() }, CancellationToken.None);
                throw;
            }
        }
        finally { gate.Semaphore.Release(); }
    }

    private async Task<MaterialStockPlan> ModifyAndReadPlanAsync(StockedMaterialScope scope, U9MaterialIntegrationConfiguration config,
        string token, string code, SaveStockedPlan input, CancellationToken ct, bool safetyOnly = false)
    {
        var result = await ExternalAsync(() => client.PostBatchAsync(config.BaseUrl, config.ItemModifyPath, token, ModifyPayload(code, input, safetyOnly), ct));
        if (result.ResponseCode != 0 || result.Rows.Count != 1 || !result.Rows[0].IsSuccess
            || !string.IsNullOrWhiteSpace(result.Rows[0].U9ItemCode) && !result.Rows[0].U9ItemCode!.Equals(code, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C未确认库存规划修改成功，请检查接口日志，并刷新核对后重试。");
        var verified = await ReadPlanAsync(scope, config, token, code, ct);
        return verified;
    }

    private static bool PlanningMatches(MaterialStockPlan plan, bool stocked) =>
        plan.InventoryPlanningMethod == (stocked ? 1 : 4) && plan.InventoryPlanTime == (stocked ? 0 : -1)
        && plan.IsRequiredShipperSupply == stocked && plan.SupplyMethod == (stocked ? 2 : -1);

    public static string ModifyPayload(string code, SaveStockedPlan input, bool safetyOnly = false)
    {
        // U9C实查：1=再订货点法，4=MRP；时机0=即时；补货2=取较大量；空枚举=-1。
        var stocked = input.SafetyStockQuantity > 0;
        var attributes = new List<object>
        {
            new { AttributeName = "InventoryInfo.SafetyStockQty", AttributeValue = input.SafetyStockQuantity.ToString(CultureInfo.InvariantCulture) },
            new { AttributeName = "InventoryInfo.InventoryPlanningMethod", AttributeValue = stocked ? "1" : "4" },
            new { AttributeName = "InventoryInfo.InventoryPlanTime", AttributeValue = stocked ? "0" : "-1" },
            new { AttributeName = "InventoryInfo.IsRequiredShipperSupply", AttributeValue = stocked ? "true" : "false" },
            new { AttributeName = "InventoryInfo.SupplyMethod", AttributeValue = stocked ? "2" : "-1" }
        };
        if (!safetyOnly) attributes.Add(new { AttributeName = "InventoryInfo.ReorderQty", AttributeValue = input.ReorderQuantity.ToString(CultureInfo.InvariantCulture) });
        return JsonSerializer.Serialize(new[] { new { Code = code, OtherID = $"stock-plan-{Guid.NewGuid():N}", Attributes = attributes } });
    }

    private async Task<MaterialStockPlan> ReadPlanAsync(StockedMaterialScope scope, U9MaterialIntegrationConfiguration config, string token, string code, CancellationToken ct)
    {
        var plans = await ExternalAsync(() => client.QueryStockPlansAsync(config.BaseUrl, token, scope.EnterpriseCode, scope.OrganizationCode, clock.GetUtcNow(), ct, [code]));
        var matching = plans?.Where(plan => plan.EnterpriseCode == scope.EnterpriseCode && plan.OrganizationCode == scope.OrganizationCode
            && plan.MaterialCode.Equals(code, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching?.Length != 1) throw new PdmRuleException("U9C未返回唯一的当前组织库存规划，请刷新并核对料号、组织和接口权限。");
        return matching[0];
    }

    private async Task<PdmMaterial> RequireMaterialAsync(Guid id, CancellationToken ct)
    {
        var material = await materials.FindMaterialAsync(id, ct) ?? throw new PdmNotFoundException("料品不存在。");
        if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved || !material.U9SyncConfirmed)
            throw new PdmRuleException($"料品{material.MaterialCode}须为未停用、已批准且U9C已确认的正式料品。");
        return material;
    }
    private async Task<U9MaterialIntegrationConfiguration> ConfigurationAsync(StockedMaterialScope scope, bool write, CancellationToken ct)
    {
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        if (config.EnterpriseCode != scope.EnterpriseCode || config.OrganizationCode != scope.OrganizationCode)
            throw new PdmRuleException("U9C企业或组织配置已变更，请刷新当前公司后重试。");
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ClientSecretCiphertext))
            throw new PdmRuleException("请先在系统设置中完整配置U9C接口。");
        if (write && (!config.WriteEnabled || !config.ItemModifyPath.Equals(U9MaterialContract.ModifyPath, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("请先在U9C接口设置中启用真实写入，并核对料品修改接口路径。");
        return config;
    }
    private async Task<string> TokenAsync(U9MaterialIntegrationConfiguration config, CancellationToken ct) =>
        (await ExternalAsync(() => client.AuthenticateAsync(new(config.BaseUrl, config.EnterpriseCode, config.OrganizationCode, config.UserCode,
            config.ClientId, secrets.Unprotect(config.ClientSecretCiphertext)), ct))).Token;
    private static async Task<T> ExternalAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new InvalidOperationException("U9C接口调用失败，请检查连接和接口权限。", error); }
    }
    private async Task<StockedMaterialCatalog> SaveAsync(StockedMaterialCatalog catalog, CancellationToken ct)
    {
        if (!await store.SaveAsync(catalog, catalog.RowVersion, ct)) throw new PdmConflictException("常备件数据已更新，请刷新后核对操作结果。");
        return catalog with { RowVersion = catalog.RowVersion + 1 };
    }
    private Task AuditAsync(string actor, string action, StockedMaterialScope scope, string text, CancellationToken ct) =>
        repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "StockedMaterial", scope.CompanyId.ToString(), text), ct);
    private static HashSet<Guid> Descendants(IReadOnlyList<StandardLibraryCategory> all, Guid root)
    {
        var ids = new HashSet<Guid> { root };
        for (var previous = -1; previous != ids.Count;)
        { previous = ids.Count; foreach (var category in all.Where(item => item.ParentId.HasValue && ids.Contains(item.ParentId.Value))) ids.Add(category.Id); }
        return ids;
    }
    private async Task<IReadOnlyList<StockedMaterialRecipient>> RecipientOptionsAsync(Guid company, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles.Where(role => role.Role == "StandardizationAssistant" || role.Name == "标准化助理").Select(role => role.Role).ToArray();
        return directory.Users.Where(user => user.IsActive && !NotificationRecipientPolicy.SystemOnly(user.EffectiveRoleCodes)
            && (user.CompanyId == company || user.CompanyId is null && directory.Memberships.Any(member => member.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)
                && directory.Units.Any(unit => unit.Id == member.UnitId && unit.OrganizationId == company && unit.IsActive))))
            .Select(user => new StockedMaterialRecipient(user.Username, user.DisplayName, roles.Any(role => user.EffectiveRoleCodes.Contains(role, StringComparer.OrdinalIgnoreCase))))
            .OrderBy(user => user.DisplayName).ToArray();
    }
    private static IReadOnlyList<string> ResolveRecipients(StockedMaterialSettings settings, IReadOnlyList<StockedMaterialRecipient> options) =>
        options.Where(user => settings.UseStandardAssistant ? user.IsDefault : (settings.Recipients ?? []).Contains(user.Username, StringComparer.OrdinalIgnoreCase)).Select(user => user.Username).ToArray();
    private static StockedMaterialCatalog PrepareEpisodes(StockedMaterialCatalog catalog) => catalog with
    {
        Snapshots = catalog.Snapshots.Select(item => item.LastError is not null ? item : item.IsLowStock
            ? item with { LowStockEpisode = item.LowStockEpisode ?? Guid.NewGuid() }
            : item with { LowStockEpisode = null, NotifiedRecipients = [] }).ToArray()
    };
    private async Task NotifyAsync(StockedMaterialCatalog catalog, IReadOnlyList<PdmMaterial> managed, CancellationToken ct)
    {
        if (!catalog.Settings.NotifyLowStock) return;
        var recipients = ResolveRecipients(catalog.Settings, await RecipientOptionsAsync(catalog.Scope.CompanyId, ct));
        if (recipients.Count == 0) return;
        var snapshots = catalog.Snapshots.ToArray();
        for (var index = 0; index < snapshots.Length; index++)
        {
            var item = snapshots[index];
            if (!item.IsLowStock || item.LastError is not null || item.LowStockEpisode is null) continue;
            var pending = recipients.Except(item.NotifiedRecipients ?? [], StringComparer.OrdinalIgnoreCase).ToArray();
            var material = managed.Single(row => row.Id == item.MaterialId);
            await repository.CreateUserNotificationsAsync(pending.Select(username => new UserNotification(Guid.NewGuid(), username,
                "stocked-material", "常备件低于安全库存", $"{material.MaterialCode} · {material.Name}：现存量{item.StockQuantity}，安全库存量{item.Plan!.SafetyStockQuantity}，缺口{item.Plan.SafetyStockQuantity - item.StockQuantity}，再订货量{item.Plan.ReorderQuantity}（{item.Plan.UnitName ?? item.Plan.UnitCode}）。请在常备件页面核对并安排补货。",
                null, null, $"stocked-low:{catalog.Scope.CompanyId:N}:{item.LowStockEpisode:N}", clock.GetUtcNow(), null)).ToArray(), ct);
            snapshots[index] = item with { NotifiedRecipients = (item.NotifiedRecipients ?? []).Union(pending, StringComparer.OrdinalIgnoreCase).ToArray() };
        }
        await SaveAsync(catalog with { Snapshots = snapshots }, ct);
    }
}
