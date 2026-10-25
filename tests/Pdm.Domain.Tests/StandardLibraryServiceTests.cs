using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class StandardLibraryServiceTests
{
    [Fact]
    public async Task RootCategoryNumbersAreAutomaticImmutableAndNeverReused()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.SaveCategoryAsync(null, new("机械件", null, 1, true, Code: " jx "), "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-01", first.Code);
        var second = await fixture.Service.SaveCategoryAsync(null, new("气动件", null, 2, true, Code: "jx"), "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-02", second.Code);
        var child = await fixture.Service.SaveCategoryAsync(null, new("轴承", first.Id, 1, true), "admin", UserRole.Administrator, default);
        Assert.Null(child.Code);
        var edited = await fixture.Service.SaveCategoryAsync(first.Id, new("机械标准件", null, 200, true, first.RowVersion, Code: "改号"), "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-01", edited.Code);
        await fixture.Service.DeleteCategoryAsync(second.Id, second.RowVersion, "admin", UserRole.Administrator, default);
        var third = await fixture.Service.SaveCategoryAsync(null, new("真空件", null, 2, true), "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-03", third.Code);
        var promoted = await fixture.Service.SaveCategoryAsync(child.Id, new(child.Name, null, 3, true, child.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-04", promoted.Code);
    }

    [Fact]
    public async Task CategoryHierarchy_PreventsCycleAndUnsafeDelete()
    {
        var fixture = new Fixture();
        var root = await fixture.Service.SaveCategoryAsync(null, new("气动标准件", null, 1, true), "admin", UserRole.Administrator, default);
        var child = await fixture.Service.SaveCategoryAsync(null, new("真空元器件", root.Id, 1, true), "admin", UserRole.Administrator, default);

        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.SaveCategoryAsync(
            root.Id, new(root.Name, child.Id, root.SortOrder, true, root.RowVersion), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.DeleteCategoryAsync(
            root.Id, root.RowVersion, "admin", UserRole.Administrator, default));

        await fixture.Service.DeleteCategoryAsync(child.Id, child.RowVersion, "admin", UserRole.Administrator, default);
        var categories = await fixture.Service.ListCategoriesAsync(true, "admin", UserRole.Administrator, default);
        Assert.Single(categories, item => !StandardLibraryDefaults.IsUncategorized(item));
    }

    [Fact]
    public async Task Membership_IsIdempotent_ParentIncludesDescendants_AndDoesNotChangeBomReferenceCount()
    {
        var fixture = new Fixture();
        var root = await fixture.Service.SaveCategoryAsync(null, new("内部标准件", null, 1, true), "admin", UserRole.Administrator, default);
        var child = await fixture.Service.SaveCategoryAsync(null, new("真空阀", root.Id, 1, true), "admin", UserRole.Administrator, default);
        var material = await fixture.CreateEligibleMaterialAsync("01010000099", "真空阀");
        await fixture.Materials.LinkBomItemAsync(Guid.NewGuid(), material.Id, "admin", fixture.Now, default);

        var command = new AddStandardLibraryMaterialsCommand([child.Id], [material.Id]);
        await fixture.Service.AddMaterialsAsync(command, "admin", UserRole.Administrator, default);
        await fixture.Service.AddMaterialsAsync(command, "admin", UserRole.Administrator, default);

        var page = await fixture.Service.ListMaterialsAsync(new(root.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default);
        var row = Assert.Single(page.Items);
        Assert.Single(row.Categories);
        Assert.Equal(1, row.Material.ReferenceCount);
    }

    [Fact]
    public async Task AddMaterials_RejectsIneligibleMaterial_AndRecommendedMaterialSortsFirst()
    {
        var fixture = new Fixture();
        var category = await fixture.Service.SaveCategoryAsync(null, new("电气标准件", null, 1, true), "admin", UserRole.Administrator, default);
        var first = await fixture.CreateEligibleMaterialAsync("01010000010", "普通传感器");
        var recommended = await fixture.CreateEligibleMaterialAsync("01010000020", "推荐传感器");
        var draft = await fixture.CreateDraftMaterialAsync("01010000030", "草稿传感器");

        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.AddMaterialsAsync(
            new([category.Id], [draft.Id]), "admin", UserRole.Administrator, default));
        await fixture.Service.AddMaterialsAsync(new([category.Id], [first.Id, recommended.Id]), "admin", UserRole.Administrator, default);
        await fixture.Service.SetRecommendedAsync(recommended.Id, true, recommended.RowVersion, "admin", UserRole.Administrator, default);

        var page = await fixture.Service.ListMaterialsAsync(new(category.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default);
        Assert.Equal(recommended.Id, page.Items[0].Material.Id);
    }

    [Fact]
    public async Task RemovingMembershipPreservesMaterialMasterAndOtherCategoriesAndRequiresManagementPermission()
    {
        var fixture = new Fixture();
        var root = await fixture.Service.SaveCategoryAsync(null, new("气动标准件", null, 1, true), "admin", UserRole.Administrator, default);
        var child = await fixture.Service.SaveCategoryAsync(null, new("气动子类", root.Id, 1, true), "admin", UserRole.Administrator, default);
        var other = await fixture.Service.SaveCategoryAsync(null, new("机械标准件", null, 2, true), "admin", UserRole.Administrator, default);
        var material = await fixture.CreateEligibleMaterialAsync("01010000111", "已收录物料");
        await fixture.Service.AddMaterialsAsync(new([child.Id, other.Id], [material.Id]), "admin", UserRole.Administrator, default);
        var before = await fixture.Materials.FindMaterialAsync(material.Id, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RemoveMaterialAsync(child.Id, material.Id, "engineer", UserRole.Engineer, default));
        Assert.Single((await fixture.Service.ListMaterialsAsync(new(root.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default)).Items);
        await fixture.Service.RemoveMaterialAsync(child.Id, material.Id, "admin", UserRole.Administrator, default);
        Assert.Empty((await fixture.Service.ListMaterialsAsync(new(root.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default)).Items);
        Assert.Single((await fixture.Service.ListMaterialsAsync(new(other.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default)).Items);
        Assert.Equal(before, await fixture.Materials.FindMaterialAsync(material.Id, default));
        await fixture.Service.RemoveMaterialAsync(other.Id, material.Id, "admin", UserRole.Administrator, default);
        Assert.Empty((await fixture.Service.ListMaterialsAsync(new(null, null, null, false, 1, 50), "admin", UserRole.Administrator, default)).Items);
        Assert.Equal(before, await fixture.Materials.FindMaterialAsync(material.Id, default));
    }

    [Fact]
    public async Task InactiveMaterialsAreHiddenByDefaultAndVisibleWhenRequestedWithoutLosingMembership()
    {
        var fixture = new Fixture();
        var category = await fixture.Service.SaveCategoryAsync(null, new("标准分类", null, 1, true), "admin", UserRole.Administrator, default);
        var material = await fixture.CreateEligibleMaterialAsync("01010000112", "停用物料");
        await fixture.Service.AddMaterialsAsync(new([category.Id], [material.Id]), "admin", UserRole.Administrator, default);
        var archived = await fixture.Materials.ArchiveMaterialAsync(material.Id, material.RowVersion, "admin", fixture.Now, default);
        Assert.Empty((await fixture.Service.ListMaterialsAsync(new(null, null, null, false, 1, 50), "engineer", UserRole.Engineer, default)).Items);
        var included = await fixture.Service.ListMaterialsAsync(new(category.Id, null, null, false, 1, 50, IncludeInactive: true), "engineer", UserRole.Engineer, default);
        var row = Assert.Single(included.Items);
        Assert.True(row.Material.IsArchived);
        Assert.Equal(material.Id, row.Material.Id);
        Assert.Equal(category.Id, Assert.Single(row.Categories).Id);
        await fixture.Materials.ReactivateMaterialAsync(material.Id, archived.RowVersion, "admin", fixture.Now, default);
        Assert.Single((await fixture.Service.ListMaterialsAsync(new(null, null, null, false, 1, 50), "engineer", UserRole.Engineer, default)).Items);
    }

    private sealed class Fixture
    {
        public TimeProvider TimeProvider { get; } = TimeProvider.System;
        public DateTimeOffset Now => TimeProvider.GetUtcNow();
        public InMemoryPdmRepository Repository { get; }
        public InMemoryMaterialRepository Materials { get; }
        public StandardLibraryService Service { get; }

        public Fixture()
        {
            Repository = new(TimeProvider);
            Materials = new(TimeProvider);
            Service = new(new InMemoryStandardLibraryRepository(), Materials, Repository, TimeProvider);
        }

        public async Task<PdmMaterial> CreateEligibleMaterialAsync(string code, string name)
        {
            var category = (await Materials.FindCategoryAsync("0101", default))!;
            var material = NewMaterial(code, name, MaterialApprovalStatus.Approved, true);
            return await Materials.CreateMaterialAsync(material, category, default);
        }

        public async Task<PdmMaterial> CreateDraftMaterialAsync(string code, string name)
        {
            var category = (await Materials.FindCategoryAsync("0101", default))!;
            return await Materials.CreateMaterialAsync(NewMaterial(code, name, MaterialApprovalStatus.Draft, false), category, default);
        }

        private PdmMaterial NewMaterial(string code, string name, MaterialApprovalStatus approval, bool confirmed) => new(
            Guid.NewGuid(), code, name, MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001", "M18", null, null, "SICK", null,
            null, null, null, approval, approval == MaterialApprovalStatus.Approved ? "admin" : null, approval == MaterialApprovalStatus.Approved ? Now : null,
            "0101", confirmed ? $"u9-{code}" : null, confirmed ? code : null, confirmed ? MaterialSyncStatus.Succeeded : MaterialSyncStatus.NotQueued,
            "admin", Now, "admin", Now, 1, "0101", U9SyncConfirmed: confirmed);
    }
}
