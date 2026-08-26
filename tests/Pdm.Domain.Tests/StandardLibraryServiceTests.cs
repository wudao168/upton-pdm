using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class StandardLibraryServiceTests
{
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
        Assert.Single(categories);
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
