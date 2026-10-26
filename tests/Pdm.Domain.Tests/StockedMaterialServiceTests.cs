using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class StockedMaterialServiceTests
{
    [Fact]
    public async Task StockedMembershipsRequireStandardLibraryButStandardMaterialsNeedNotBeStocked()
    {
        var f = await Fixture.CreateAsync();
        var material = await f.AddAsync("01010000001");
        await f.Service.AddAsync(f.Scope, new([f.Category.Id], [material.Id]), "admin", default);
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Memberships);
        Assert.Empty((await f.Service.ListAsync(f.Scope with { OrganizationCode = "other" }, null, null, null, 1, 50, default)).Items);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        await f.Service.RemoveAsync(f.Scope, material.Id, null, "admin", default);
        Assert.Empty((await f.Store.ReadAsync(f.Scope, default)).Memberships);
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        Assert.NotNull(await f.Materials.FindMaterialAsync(material.Id, default));
        Assert.Empty(f.Client.Writes);
        Assert.Single(await f.Materials.ListStockedMaterialPlansAsync("01", "7", default));
    }

    [Fact]
    public async Task LowStockSortsBeforePaginationAndStrictlyBelowThreshold()
    {
        var f = await Fixture.CreateAsync();
        var normal = await f.AddAsync("01010000001", 15);
        var low = await f.AddAsync("01010000099", 14.5m);
        f.Client.Rows.Add(f.Client.Rows[0] with { OrganizationCode = "another", StockQuantity = 999 });
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var page = await f.Service.ListAsync(f.Scope, null, null, null, 1, 1, default);
        Assert.Equal(low.Id, Assert.Single(page.Items).Material.Id);
        Assert.Equal(1, page.LowStockCount);
        Assert.Equal(14.5m, page.Items[0].Snapshot.StockQuantity);
        Assert.Equal(normal.Id, Assert.Single((await f.Service.ListAsync(f.Scope, null, null, null, 2, 1, default)).Items).Material.Id);
    }

    [Fact]
    public async Task LowStockNotifiesDefaultAssistantOnceAndNotifiesAgainAfterRecovery()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("01010000001", 0);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var first = Assert.Single(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
        Assert.Contains("现存量0", first.Content);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
        f.Client.Rows.Clear(); f.Client.Rows.Add(FakeClient.Stock("01010000001", 15));
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        f.Client.Rows.Clear();
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        Assert.Equal(2, (await f.Repository.ListUserNotificationsAsync("assistant", 100, default)).Count);
        Assert.All(await f.Repository.ListUserNotificationsAsync("assistant", 100, default), notice => Assert.StartsWith($"stocked-low:{f.Scope.CompanyId:N}:", notice.SourceKey));
    }

    [Fact]
    public async Task SettingsDefaultFourHoursAllowMultipleRecipientsAndRejectForeignUsers()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("01010000001", 1);
        var view = await f.Service.SettingsAsync(f.Scope, default);
        Assert.Equal(240, view.Settings.SyncIntervalMinutes);
        Assert.Equal(new[] { "assistant" }, view.EffectiveRecipients);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveSettingsAsync(f.Scope, new(240, true, false, ["foreign"]), view.RowVersion, "admin", UserRole.Administrator, default));
        view = await f.Service.SaveSettingsAsync(f.Scope, new(120, true, false, ["assistant", "second"]), view.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(2, view.EffectiveRecipients.Count);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
        Assert.Single(await f.Repository.ListUserNotificationsAsync("second", 100, default));
        Assert.Empty(await f.Repository.ListUserNotificationsAsync("foreign", 100, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveSettingsAsync(f.Scope, view.Settings, view.RowVersion, "admin", UserRole.Administrator, default));
        var reads = f.Client.PlanReads;
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default, scheduled: true);
        Assert.Equal(reads, f.Client.PlanReads);
        f.Clock.Now = f.Clock.Now.AddHours(2);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default, scheduled: true);
        Assert.True(f.Client.PlanReads > reads);
    }

    [Fact]
    public async Task FailureOrMissingPlanningRetainsSnapshotAndDoesNotInferZero()
    {
        var f = await Fixture.CreateAsync();
        await f.AddAsync("01010000001", 20);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var prior = Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots);
        f.Client.InventoryFails = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SynchronizeAsync(f.Scope, "admin", default));
        Assert.Equal(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots)));
        f.Client.InventoryFails = false; f.Client.Plans.Clear(); f.Client.Rows.Clear();
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var after = Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots);
        Assert.Equal(20, after.StockQuantity); Assert.NotNull(after.LastError);
        Assert.Empty(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
        Assert.Single(await f.Materials.ListStockedMaterialPlansAsync("01", "7", default));
    }

    [Fact]
    public async Task MaintainWritesExactlyTwoFieldsVerifiesReadbackAndPreservesMaster()
    {
        var f = await Fixture.CreateAsync();
        var material = await f.AddAsync("01010000001", 20);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var prior = Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots);
        var master = await f.Materials.FindMaterialAsync(material.Id, default);
        var after = await f.Service.MaintainAsync(f.Scope, material.Id, new(0, 50, prior.Plan!.SyncedAt), "admin", default);
        Assert.Equal(0, after.Plan!.SafetyStockQuantity);
        Assert.Equal(50, after.Plan.ReorderQuantity);
        Assert.Equal(master, await f.Materials.FindMaterialAsync(material.Id, default));
        using var payload = JsonDocument.Parse(Assert.Single(f.Client.Writes));
        var attributes = payload.RootElement[0].GetProperty("Attributes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "InventoryInfo.SafetyStockQty", "InventoryInfo.ReorderQty" }, attributes.Select(row => row.GetProperty("AttributeName").GetString()));
        Assert.Empty(await f.Materials.ListStockedMaterialPlansAsync("01", "7", default));
    }

    [Fact]
    public async Task MaintainRejectsExternalChangesAndDoesNotClaimSuccessOnReadbackMismatch()
    {
        var f = await Fixture.CreateAsync();
        var material = await f.AddAsync("01010000001", 20, initialize: false);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.MaintainAsync(f.Scope, material.Id, new(20, 80, null), "admin", default));
        Assert.Empty(f.Client.Writes);
        await f.Service.SynchronizeAsync(f.Scope, "admin", default);
        var prior = Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots);
        f.Client.Plans[material.MaterialCode] = prior.Plan! with { SafetyStockQuantity = 12 };
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.MaintainAsync(f.Scope, material.Id, new(20, 80, prior.Plan!.SyncedAt), "admin", default));
        Assert.Empty(f.Client.Writes);
        f.Client.Plans[material.MaterialCode] = prior.Plan!; f.Client.ApplyWrite = false;
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.MaintainAsync(f.Scope, material.Id, new(20, 80, prior.Plan!.SyncedAt), "admin", default));
        Assert.NotNull(Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Snapshots).LastError);
    }

    [Fact]
    public async Task CategoryCannotBeDeletedWhileStockedMembershipExists()
    {
        var f = await Fixture.CreateAsync(); await f.AddAsync("01010000001");
        var service = new StandardLibraryService(f.Categories, f.Materials, f.Repository, f.Clock, stockedMaterials: f.Store);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.DeleteCategoryAsync(f.Category.Id, f.Category.RowVersion, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task LastStandardMembershipCannotBeRemovedUntilStockedMembershipIsRemoved()
    {
        var f = await Fixture.CreateAsync(); var material = await f.AddAsync("01010000001");
        var standard = new StandardLibraryService(f.Categories, f.Materials, f.Repository, f.Clock, stockedMaterials: f.Store);
        await Assert.ThrowsAsync<PdmRuleException>(() => standard.RemoveMaterialAsync(f.Category.Id, material.Id, "admin", UserRole.Administrator, default));
        await f.Service.RemoveAsync(f.Scope, material.Id, null, "admin", default);
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        await standard.RemoveMaterialAsync(f.Category.Id, material.Id, "admin", UserRole.Administrator, default);
        Assert.Empty(await f.Categories.ListMembershipsAsync(default));
        await standard.AddMaterialsAsync(new([f.Category.Id], [material.Id]), "admin", UserRole.Administrator, default);
        Assert.Empty((await f.Store.ReadAsync(f.Scope, default)).Memberships);
    }

    [Fact]
    public async Task DiscoversU9StockedMaterialsImportsMissingMastersAndPreservesExistingCategoriesAndPlmMetadata()
    {
        var f = await Fixture.CreateAsync();
        var existing = await f.AddAsync("01010000001", 20);
        await f.Service.RemoveAsync(f.Scope, existing.Id, null, "admin", default);
        const string code = "01010009001";
        f.Client.Plans[code] = new("01", "7", code, 15, 50, true, "001", "个", f.Clock.Now);
        f.Client.Details[code] = new("u9-new", code, "U9新增常备件", "S-1", "0101", "电气外购件", "001", 9, "品牌")
            { StockPlan = new("7", true, 15, 50, "001", "个") };
        f.Client.Rows.Add(FakeClient.Stock(code, 20));
        await f.Service.SynchronizeAsync(f.Scope, "system:stocked-material-sync", default);
        var imported = (await f.Materials.FindMaterialByCodeAsync(code, default))!;
        Assert.Equal(MaterialDataSource.U9C, imported.SourceSystem);
        Assert.Equal(MaterialMasterOwner.U9C, imported.MasterOwner);
        var catalog = await f.Store.ReadAsync(f.Scope, default);
        Assert.Equal(2, catalog.Memberships.Select(item => item.MaterialId).Distinct().Count());
        Assert.Equal(f.Category.Id, Assert.Single(catalog.Memberships, item => item.MaterialId == existing.Id).CategoryId);
        var importedCategory = Assert.Single(catalog.Memberships, item => item.MaterialId == imported.Id).CategoryId;
        Assert.Equal((await f.Materials.FindCategoryAsync("0101", default))!.Name,
            (await f.Categories.FindCategoryAsync(importedCategory, default))!.Name);
        Assert.Equal(2, (await f.Categories.ListMembershipsAsync(default)).Count);
        await f.Materials.UpdatePlmMetadataAsync(imported with { SelectionAdvice = "保留PLM建议", ReferencePrice = 12 }, imported.RowVersion, default);
        f.Client.Details[code] = f.Client.Details[code] with { U9ItemName = "U9更新名称" };
        await f.Service.SynchronizeAsync(f.Scope, "system:stocked-material-sync", default);
        var refreshed = (await f.Materials.FindMaterialByCodeAsync(code, default))!;
        Assert.Equal(imported.Id, refreshed.Id);
        Assert.Equal("U9更新名称", refreshed.Name);
        Assert.Equal("保留PLM建议", refreshed.SelectionAdvice);
        Assert.Equal(12, refreshed.ReferencePrice);
        Assert.Equal(2, (await f.Store.ReadAsync(f.Scope, default)).Memberships.Count);
        Assert.Empty(f.Client.Writes);
    }

    [Fact]
    public async Task FirstUpgradeDiscoveryRunsWithAnEmptyCatalogAndRecentPriorReadbackThenUsesConfiguredInterval()
    {
        var f = await Fixture.CreateAsync(); const string code = "01010009002";
        var catalog = await f.Store.ReadAsync(f.Scope, default);
        await f.Store.SaveAsync(catalog with { LastAttemptAt = f.Clock.Now }, 0, default);
        f.Client.Plans[code] = new("01", "7", code, 10, 30, true, "001", "个", f.Clock.Now);
        f.Client.Details[code] = new("u9-new", code, "首次补齐", "S-2", "0101", "电气外购件", "001", 9);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default, scheduled: true);
        Assert.Single((await f.Store.ReadAsync(f.Scope, default)).Memberships);
        var reads = f.Client.PlanReads;
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default, scheduled: true);
        Assert.Equal(reads, f.Client.PlanReads);
        f.Clock.Now = f.Clock.Now.AddHours(4);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default, scheduled: true);
        Assert.True(f.Client.PlanReads > reads);
    }

    [Fact]
    public async Task SourceDiscoveryFiltersInactiveZeroAndOtherOrganizationsAndReportsUnmappedMaterialsWithoutLosingKnownRows()
    {
        var f = await Fixture.CreateAsync(); var existing = await f.AddAsync("01010000001", 20);
        var plan = f.Client.Plans[existing.MaterialCode];
        f.Client.Plans["foreign"] = plan with { MaterialCode = "foreign", OrganizationCode = "8" };
        f.Client.Plans["inactive"] = plan with { MaterialCode = "inactive", IsEffective = false };
        f.Client.Plans["zero"] = plan with { MaterialCode = "zero", SafetyStockQuantity = 0 };
        const string code = "01010009003";
        f.Client.Plans[code] = plan with { MaterialCode = code };
        f.Client.Details[code] = new("u9-unmapped", code, "未映射分类", "S-3", "UNKNOWN", "未知分类", "001", 9);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        var catalog = await f.Store.ReadAsync(f.Scope, default);
        Assert.Equal(existing.Id, Assert.Single(catalog.Memberships).MaterialId);
        Assert.Contains(code, catalog.LastError);
        Assert.Null(catalog.LastSuccessAt);
        Assert.Null(await f.Materials.FindMaterialByCodeAsync("foreign", default));
        Assert.Null(await f.Materials.FindMaterialByCodeAsync("inactive", default));
        Assert.Null(await f.Materials.FindMaterialByCodeAsync("zero", default));
        f.Client.Details[code] = f.Client.Details[code] with { U9CategoryCode = "0101", StockPlan = new("8", true, 15, 50, "001", "个") };
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        Assert.Null(await f.Materials.FindMaterialByCodeAsync(code, default));
        Assert.Contains("不属于当前U9C组织", (await f.Store.ReadAsync(f.Scope, default)).LastError);
        f.Client.Details[code] = f.Client.Details[code] with { StockPlan = new("7", true, 15, 50, "001", "个") };
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        Assert.Equal(2, (await f.Store.ReadAsync(f.Scope, default)).Memberships.Count);
        Assert.Null((await f.Store.ReadAsync(f.Scope, default)).LastError);
    }

    [Theory]
    [InlineData("010402", "工具刀具")]
    [InlineData("010405", "包装材料")]
    [InlineData("010502", "非金属")]
    public async Task ManagementOnlyCategoriesNeverCreateGeneralMastersOrStandardLibraryMemberships(string categoryCode, string categoryName)
    {
        var f = await Fixture.CreateAsync(); var code = categoryCode + "0000001";
        f.Client.Plans[code] = new("01", "7", code, 10, 30, true, "001", "个", f.Clock.Now);
        f.Client.Details[code] = new("u9-managed", code, categoryName + "料品", "S-4", categoryCode, categoryName, "001", 9);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        Assert.Null(await f.Materials.FindMaterialByCodeAsync(code, default));
        Assert.Empty(await f.Categories.ListMembershipsAsync(default));
        var catalog = await f.Store.ReadAsync(f.Scope, default);
        var management = Assert.Single(catalog.ManagementMaterials!);
        Assert.Equal(code, management.MaterialCode);
        Assert.True(Assert.Single(catalog.Snapshots).IsFormal);
        var page = await f.Service.ListAsync(f.Scope, null, null, null, 1, 50, default);
        Assert.True(Assert.Single(page.Items).ManagementOnly);
        Assert.Equal(categoryName, (await f.Categories.FindCategoryAsync(Assert.Single(catalog.Memberships).CategoryId, default))!.Name);
        var query = new StockedMaterialQueryService(f.Store, f.Materials, new InMemoryU9InventoryRepository());
        Assert.Empty(await query.ListBusinessPlansAsync(f.Scope.CompanyId, default));
        Assert.Null((await f.Materials.FindCategoryAsync(categoryCode, default))!.PdmKind);
    }

    [Fact]
    public async Task ZeroParametersAreOnlyStockDraftsAndOnlyVerifiedPositiveSafetyStockEntersBusinessRules()
    {
        var f = await Fixture.CreateAsync(); var material = await f.AddAsync("01010009005", initialize: false);
        var query = new StockedMaterialQueryService(f.Store, f.Materials, new InMemoryU9InventoryRepository());
        Assert.Empty(await f.Categories.ListMembershipsAsync(default));
        Assert.Equal("Draft", Assert.Single((await f.Service.ListAsync(f.Scope, null, null, null, 1, 50, default)).Items).Snapshot.State);
        Assert.Empty(await query.ListBusinessPlansAsync(f.Scope.CompanyId, default));
        f.Client.Plans[material.MaterialCode] = f.Client.Plans[material.MaterialCode] with { SafetyStockQuantity = 0, ReorderQuantity = 0 };
        var read = await f.Service.ReadPlanForMaintenanceAsync(f.Scope, material.Id, "admin", default);
        var draft = await f.Service.MaintainAsync(f.Scope, material.Id, new(0, 30, read.Plan!.SyncedAt), "admin", default);
        Assert.False(draft.IsFormal);
        Assert.Empty(await f.Categories.ListMembershipsAsync(default));
        var formal = await f.Service.MaintainAsync(f.Scope, material.Id, new(10, 0, draft.Plan!.SyncedAt), "admin", default);
        Assert.True(formal.IsFormal);
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        Assert.Single(await query.ListBusinessPlansAsync(f.Scope.CompanyId, default));
        await f.Service.MaintainAsync(f.Scope, material.Id, new(0, 0, formal.Plan!.SyncedAt), "admin", default);
        Assert.Empty(await query.ListBusinessPlansAsync(f.Scope.CompanyId, default));
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        Assert.Equal(MaterialApprovalStatus.Approved, (await f.Materials.FindMaterialAsync(material.Id, default))!.ApprovalStatus);
        Assert.Empty(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
    }

    [Fact]
    public async Task StandardMachinedPartsPreserveTheirOriginalCategoryAndManufactureMode()
    {
        var f = await Fixture.CreateAsync(); const string code = "02030000999";
        var category = (await f.Materials.FindCategoryAsync("0203", default))!;
        await f.Materials.SaveCategoryAsync(category with { PdmKind = MaterialKind.Standard }, category.RowVersion, default);
        f.Client.Plans[code] = new("01", "7", code, 10, 30, true, "001", "个", f.Clock.Now);
        f.Client.Details[code] = new("u9-machined", code, "标准机加件", "M-1", "0203", "标准机加件", "001", 2);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        var material = (await f.Materials.FindMaterialByCodeAsync(code, default))!;
        Assert.Equal(MaterialKind.Standard, material.Kind);
        Assert.Equal(MaterialSupplyMode.Manufacture, material.SupplyMode);
        Assert.Equal("0203", material.CategoryCode);
        Assert.Single(await f.Categories.ListMembershipsAsync(default));
        Assert.False((await f.Materials.FindCategoryAsync("0203", default))!.AllowCreate);
    }

    [Fact]
    public async Task BusinessQueriesIgnoreRawU9CacheAndDraftsAndInventoryRefreshReadsTheSameLocalStockSnapshot()
    {
        var f = await Fixture.CreateAsync(); var material = await f.AddAsync("01010009006", 20);
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        await f.AddAsync("01010009007", initialize: false);
        await f.Materials.SaveStockPlansAsync([new("01", "7", "unregistered", 999, 1000, true, "001", "个", f.Clock.Now)], default);
        var inventory = new InMemoryU9InventoryRepository();
        var query = new StockedMaterialQueryService(f.Store, f.Materials, inventory);
        var plan = Assert.Single(await query.ListBusinessPlansAsync(f.Scope.CompanyId, default));
        Assert.Equal(material.MaterialCode, plan.MaterialCode);
        Assert.Equal(20, plan.StockQuantity);
        Assert.Empty(await query.ListBusinessPlansAsync(Guid.NewGuid(), default));
        TenantContext.Set(new(Guid.NewGuid(), f.Scope.CompanyId, f.Scope.CompanyId, "assistant", "StandardizationAssistant", false, new HashSet<string>()));
        try
        {
            var service = new U9InventoryService(inventory, f.Materials, f.Repository, new Protector(), f.Client, f.Client, f.Clock, query);
            var reads = f.Client.InventoryReads;
            var page = await service.RefreshMaterialAsync(material.MaterialCode, "assistant", UserRole.Engineer, default);
            Assert.Equal(20, Assert.Single(page.Items).StockQuantity);
            Assert.Equal(reads, f.Client.InventoryReads);
            Assert.Equal(f.Clock.Now, page.LastSuccessfulRefreshAt);
        }
        finally { TenantContext.Clear(); }
    }

    [Theory]
    [InlineData("system:u9-material-full-sync", true, true)]
    [InlineData("admin", true, false)]
    [InlineData("system:u9-material-full-sync", false, false)]
    public async Task DiscoveryRestoresOnlyU9OwnedMaterialsAutomaticallyArchivedByPriorFullSync(string archivedBy, bool u9Owned, bool restored)
    {
        var f = await Fixture.CreateAsync(); var template = await f.AddAsync("01010009009"); const string code = "01010009010";
        var material = await f.Materials.CreateMaterialAsync(template with { Id = Guid.NewGuid(), MaterialCode = code,
            MasterOwner = u9Owned ? MaterialMasterOwner.U9C : MaterialMasterOwner.Pdm,
            SourceSystem = u9Owned ? MaterialDataSource.U9C : MaterialDataSource.Pdm }, (await f.Materials.FindCategoryAsync("0101", default))!, default);
        await f.Materials.ArchiveMaterialAsync(material.Id, material.RowVersion, archivedBy, f.Clock.Now, default);
        f.Client.Plans[code] = f.Client.Plans[template.MaterialCode] with { MaterialCode = code };
        f.Client.Details[code] = f.Client.Details[template.MaterialCode] with { U9ItemCode = code, U9ItemId = "u9-restored" };
        await f.Service.SynchronizeAsync(f.Scope, "scheduled", default);
        Assert.Equal(!restored, (await f.Materials.FindMaterialAsync(material.Id, default))!.IsArchived);
        var catalog = await f.Store.ReadAsync(f.Scope, default);
        Assert.Equal(restored, catalog.Memberships.Any(item => item.MaterialId == material.Id));
        Assert.Equal(restored, catalog.BusinessSnapshotReady);
        if (!restored) Assert.Contains(code, catalog.LastError);
        Assert.Empty(f.Client.Writes);
    }

    private sealed class Fixture
    {
        public TestClock Clock { get; } = new();
        public InMemoryPdmRepository Repository { get; private set; } = null!;
        public InMemoryMaterialRepository Materials { get; private set; } = null!;
        public InMemoryStandardLibraryRepository Categories { get; } = new();
        public InMemoryStockedMaterialStore Store { get; } = new();
        public FakeClient Client { get; } = new();
        public StockedMaterialService Service { get; private set; } = null!;
        public StockedMaterialScope Scope { get; private set; } = null!;
        public StandardLibraryCategory Category { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); f.Repository = new(f.Clock); f.Materials = new(f.Clock);
            var company = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
            f.Scope = new(company, "01", "7");
            await f.Materials.SaveIntegrationConfigurationAsync(new("http://u9.test", "01", "7", "pdm", "PDM", "secret", U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", f.Clock.Now), default);
            var roles = await f.Repository.GetRolePermissionDirectoryAsync(default);
            var role = roles.Roles.Single(item => item.Name == "标准化助理").Role;
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "assistant", "标准助理", "hash", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "second", "第二接收人", "hash", UserRole.Engineer, true, CompanyId: company), default);
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "foreign", "其他公司人员", "hash", UserRole.Engineer, true, CompanyId: Guid.NewGuid()), default);
            f.Category = await f.Categories.CreateCategoryAsync(new(Guid.NewGuid(), "常备分类", null, 1, true, "admin", f.Clock.Now, "admin", f.Clock.Now, 1), default);
            var template = (await f.Materials.FindCategoryAsync("0101", default))!;
            foreach (var (code, name, supply) in new[] { ("010402", "工具刀具", MaterialSupplyMode.Purchase), ("010405", "包装材料", MaterialSupplyMode.Purchase), ("010502", "非金属", MaterialSupplyMode.Purchase), ("0203", "标准机加件", MaterialSupplyMode.Manufacture) })
                await f.Materials.SaveCategoryAsync(template with { Code = code, Name = name, ParentCode = null, PdmKind = null, AllowCreate = false,
                    DefaultSupplyMode = supply, NumberPrefix = code, CounterScope = code, CurrentSequence = 0, RowVersion = 1 }, null, default);
            f.Service = new(f.Store, new(), f.Materials, f.Categories, f.Repository, f.Client, f.Client,
                new InMemoryU9InventoryRepository(), new Protector(), f.Clock,
                new StandardLibraryImportService(f.Categories, f.Materials, f.Repository, new Protector(), f.Client, f.Clock));
            return f;
        }
        public async Task<PdmMaterial> AddAsync(string code, decimal stock = 20, bool initialize = true)
        {
            var material = await Materials.CreateMaterialAsync(new(Guid.NewGuid(), code, "常备料品", MaterialKind.Electrical,
                MaterialSupplyMode.Purchase, "001", "M18", null, null, "SICK", null, null, null, null,
                MaterialApprovalStatus.Approved, "admin", Clock.Now, "0101", $"u9-{code}", code, MaterialSyncStatus.Succeeded,
                "admin", Clock.Now, "admin", Clock.Now, 1, "0101", U9SyncConfirmed: true), (await Materials.FindCategoryAsync("0101", default))!, default);
            Client.Plans[code] = new("01", "7", code, 15, 50, true, "001", "个", Clock.Now);
            Client.Details[code] = new($"u9-{code}", code, material.Name, material.Specification, "0101", "电气外购件", "001", 9, material.Brand);
            Client.Rows.Add(FakeClient.Stock(code, stock));
            await Service.AddAsync(Scope, new([Category.Id], [material.Id]), "admin", default);
            if (initialize) await Service.ReadPlanForMaintenanceAsync(Scope, material.Id, "admin", default);
            return material;
        }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Protector : IU9SecretProtector
    { public string Protect(string secret) => secret; public string Unprotect(string ciphertext) => ciphertext; }
    private sealed class FakeClient : IU9OpenApiClient, IU9InventoryClient
    {
        public Dictionary<string, MaterialStockPlan> Plans { get; } = [];
        public Dictionary<string, U9ItemReference> Details { get; } = [];
        public List<U9InventorySourceRow> Rows { get; } = [];
        public List<string> Writes { get; } = [];
        public bool InventoryFails { get; set; }
        public bool ApplyWrite { get; set; } = true;
        public int PlanReads { get; private set; }
        public int InventoryReads { get; private set; }
        public static U9InventorySourceRow Stock(string code, decimal quantity) => new("7", code, "料品", null, "W01", "原料仓", null, null, null, null, null, null, quantity, quantity, 0, 0);
        public Task<IReadOnlyList<MaterialStockPlan>?> QueryStockPlansAsync(string baseUrl, string token, string enterpriseCode, string organizationCode, DateTimeOffset syncedAt, CancellationToken ct, IReadOnlyList<string>? materialCodes = null)
        { PlanReads++; return Task.FromResult<IReadOnlyList<MaterialStockPlan>?>(Plans.Values.Where(item => materialCodes is null ? item.SafetyStockQuantity != 0 : materialCodes.Contains(item.MaterialCode)).Select(item => item with { SyncedAt = syncedAt }).ToArray()); }
        public Task<U9InventoryQueryResult> QueryInventoryAsync(string baseUrl, string path, string token, string organizationCode, string? materialCode, CancellationToken ct)
        { InventoryReads++; return Task.FromResult(new U9InventoryQueryResult(InventoryFails ? 1 : 0, !InventoryFails, null, Rows.ToArray())); }
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken ct) => Task.FromResult(new U9AuthenticationResult("token"));
        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken ct)
        {
            Writes.Add(payloadJson); using var json = JsonDocument.Parse(payloadJson); var code = json.RootElement[0].GetProperty("Code").GetString()!;
            var fields = json.RootElement[0].GetProperty("Attributes").EnumerateArray().ToArray();
            if (ApplyWrite) Plans[code] = Plans[code] with { SafetyStockQuantity = decimal.Parse(fields[0].GetProperty("AttributeValue").GetString()!, System.Globalization.CultureInfo.InvariantCulture), ReorderQuantity = decimal.Parse(fields[1].GetProperty("AttributeValue").GetString()!, System.Globalization.CultureInfo.InvariantCulture) };
            return Task.FromResult(new U9BusinessBatchResult(0, null, [new(true, null, "u9-id", code)]));
        }
        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(payloadJson);
            var codes = json.RootElement.EnumerateArray().Select(item => item.GetProperty("ItemMaster").GetProperty("Code").GetString()!).ToArray();
            return Task.FromResult(new U9ItemQueryResult(0, null, codes.Where(Details.ContainsKey).Select(code => Details[code]).ToArray()));
        }
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken ct) => throw new NotSupportedException();
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken ct) => throw new NotSupportedException();
    }
}
