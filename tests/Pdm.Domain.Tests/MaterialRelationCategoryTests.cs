using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class MaterialRelationCategoryTests
{
    [Fact]
    public async Task InheritsMultipleMainCategoriesAndReflectsRenamesWithoutCopying()
    {
        var f = new Fixture();
        var main = await f.Material("MAIN");
        var draft = await f.Draft(main);
        var root = await f.Category("机械件");
        var child = await f.Category("安装件", root.Id);
        await f.Library.AddMembershipsAsync([new(root.Id, main.Id, "admin", f.Now), new(child.Id, main.Id, "admin", f.Now)], default);
        var listed = Assert.Single(await f.Service.ListTemplatesAsync(true, "admin", UserRole.Administrator, default));
        Assert.Equal(2, listed.StandardLibraryCategories.Count);
        await f.Library.UpdateCategoryAsync(child with { Name = "附件" }, child.RowVersion, default);
        Assert.Contains((await f.Service.GetMainCategoriesAsync(main.Id, "admin", UserRole.Administrator, default)), item => item.Name == "附件");
        Assert.Equal(draft.Id, listed.Id);
    }

    [Fact]
    public async Task MissingCategoryBlocksPublicationAndStandardizationCanSupplementOnce()
    {
        var f = new Fixture();
        var main = await f.Material("MAIN");
        var draft = await f.Draft(main);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Publish(draft));
        var category = await f.Category("安装件");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AssignMainCategoryAsync(main.Id, category.Id, "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.AssignMainCategoryAsync(main.Id, StandardLibraryDefaults.UncategorizedId, "admin", UserRole.Administrator, default));
        var inactive = await f.Category("停用分类", active: false);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.AssignMainCategoryAsync(main.Id, inactive.Id, "admin", UserRole.Administrator, default));
        await f.Service.AssignMainCategoryAsync(main.Id, category.Id, "admin", UserRole.Administrator, default);
        await f.Service.AssignMainCategoryAsync(main.Id, category.Id, "admin", UserRole.Administrator, default);
        Assert.Single(await f.Library.ListMembershipsAsync(default));
        var other = await f.Category("电气件");
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.AssignMainCategoryAsync(main.Id, other.Id, "admin", UserRole.Administrator, default));
        Assert.NotNull((await f.Publish(draft)).PublishedRevision);
    }

    [Fact]
    public async Task StandardLibraryReusesMainEntryAndExposesOnlyEffectiveRelation()
    {
        var f = new Fixture();
        var main = await f.Material("MAIN");
        var category = await f.Category("附件");
        await f.Service.AssignMainCategoryAsync(main.Id, category.Id, "admin", UserRole.Administrator, default);
        var draft = await f.Draft(main);
        var libraryService = new StandardLibraryService(f.Library, f.Materials, f.Pdm, TimeProvider.System, relations: f.Relations);
        async Task<StandardLibraryMaterial> Row() => Assert.Single((await libraryService.ListMaterialsAsync(new(category.Id, null, null, false, 1, 50), "admin", UserRole.Administrator, default)).Items);
        Assert.Null((await Row()).Relation);
        await f.Publish(draft);
        await f.Draft(main);
        var row = await Row();
        Assert.Equal(main.Id, row.Material.Id);
        Assert.NotNull(row.Relation?.PublishedRevision);
        Assert.Null(row.Relation?.DraftRevision);
        Assert.Single(await f.Library.ListMembershipsAsync(default));
        await f.Pdm.SetRolePermissionsAsync("Engineer", [PermissionCodes.MaterialRelationView], default);
        var readOnly = Assert.Single(await f.Service.ListTemplatesAsync(true, "engineer", UserRole.Engineer, default));
        Assert.Null(readOnly.DraftRevision);
    }

    private sealed class Fixture
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public InMemoryPdmRepository Pdm { get; } = new(TimeProvider.System);
        public InMemoryMaterialRepository Materials { get; } = new(TimeProvider.System);
        public InMemoryMaterialRelationRepository Relations { get; } = new();
        public InMemoryStandardLibraryRepository Library { get; } = new();
        public MaterialRelationService Service => new(Relations, Materials, Pdm, TimeProvider.System, Library);
        public Task<StandardLibraryCategory> Category(string name, Guid? parent = null, bool active = true) => Library.CreateCategoryAsync(new(Guid.NewGuid(), name, parent, 1, active, "admin", Now, "admin", Now, 1), default);
        public async Task<PdmMaterial> Material(string code)
        {
            var existing = (await Materials.ListMaterialsAsync(code, null, true, 100, default)).FirstOrDefault(item => item.MaterialCode == code);
            if (existing is not null) return existing;
            var category = await Materials.FindCategoryAsync("0102", default) ?? throw new InvalidOperationException();
            return await Materials.CreateMaterialAsync(new(Guid.NewGuid(), code, code, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", null, null, null, null, null,
                null, null, null, MaterialApprovalStatus.Approved, "admin", Now, "0102", null, code, MaterialSyncStatus.Succeeded, "admin", Now, "admin", Now, 1, "0102", U9SyncConfirmed: true), category, default);
        }
        public async Task<MaterialRelationTemplate> Draft(PdmMaterial main)
        {
            var accessory = await Material("PART");
            return await Service.SaveDraftAsync(null, new(main.Id, "关联配置", "修改", null,
                [new("附件", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1, [new(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 2, true, 1, null)])]), "admin", UserRole.Administrator, default);
        }
        public Task<MaterialRelationTemplate> Publish(MaterialRelationTemplate draft) => Service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
    }
}
