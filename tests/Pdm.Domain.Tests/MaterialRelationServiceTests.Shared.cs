using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    private static SaveMaterialRelationTemplateCommand SharedCommand(PdmMaterial accessory, params PdmMaterial[] mains) =>
        new(mains[0].Id, "M6系列共享配置", "共享规则", null,
            [new("垫圈", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1, [new(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 2, false, 1)])], mains.Select(item => item.Id).ToArray());

    [Fact]
    public async Task SharedRangeIsDraftedThenPublishedAndRemovedAnchorCanJoinAnotherGroup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, repository, TimeProvider.System);
        var a = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-A", "M6x18");
        var b = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-B", "M6x20");
        var c = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-C", "M6x25");
        var part = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "PART-M6", "M6垫圈");
        var first = await service.SaveDraftAsync(null, SharedCommand(part, a, b), "admin", UserRole.Administrator, default);
        var v1 = await service.PublishAsync(first.Id, first.DraftRevision!.Id, first.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var second = await service.SaveDraftAsync(first.Id, SharedCommand(part, b, c), "admin", UserRole.Administrator, default);
        Assert.Equal(new[] { b.Id, c.Id }, second.MainMaterials.Select(item => item.MaterialId));
        Assert.Equal(new[] { a.Id, b.Id }, Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default)).MainMaterials.Select(item => item.MaterialId));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveDraftAsync(null, SharedCommand(part, a), "admin", UserRole.Administrator, default));
        await service.PublishAsync(second.Id, second.DraftRevision!.Id, second.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(new[] { a.Id, b.Id }, v1.PublishedMainMaterials.Select(item => item.MaterialId));
        var separate = await service.SaveDraftAsync(null, SharedCommand(part, a), "admin", UserRole.Administrator, default);
        Assert.NotEqual(first.Id, separate.Id);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveDraftAsync(first.Id, SharedCommand(part, b, c) with { ExpectedRevisionRowVersion = second.DraftRevision.RowVersion }, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EverySharedMainResolvesTheSameRevisionAndKeepsItsOwnBomQuantity()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var service = new MaterialRelationService(new InMemoryMaterialRelationRepository(), materials, repository, TimeProvider.System);
        var a = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-A", "M6x18");
        var b = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-B", "M6x20");
        var part = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "PART-M6", "M6垫圈");
        var draft = await service.SaveDraftAsync(null, SharedCommand(part, a, b), "admin", UserRole.Administrator, default);
        var published = await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var group = published.PublishedRevision!.Groups.Single();
        var project = await repository.CreateProjectAsync(new("SHARED-BOM", "共享选配验证", "admin", "vault", "release"), "admin", default);
        var lineA = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, a.MaterialCode, a.Name, 2, "001", null, null, "W1", true);
        var lineB = lineA with { Id = Guid.NewGuid(), Sequence = 2, DrawingNumber = b.MaterialCode, Name = b.Name, Quantity = 3 };
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [lineA, lineB], default);
        var result = await service.ApplyAsync(project.Id, [new(lineA.Id, [new(group.Id, [group.Options.Single().Id])]), new(lineB.Id, [new(group.Id, [group.Options.Single().Id])])], "admin", UserRole.Administrator, default);
        Assert.Equal(2, result.MainMaterialCount);
        Assert.All(result.MainMaterials, item => Assert.Equal(published.PublishedRevision.Id, item.RevisionId));
        Assert.True(result.IsComplete);
        Assert.Equal(new decimal[] { 4, 6 }, (await repository.GetBomAsync(project.Id, BomKind.Standard, default)).Where(item => item.Source == "MaterialRelation").Select(item => item.Quantity).Order());
        await service.SaveDraftAsync(draft.Id, SharedCommand(part, a, b), "admin", UserRole.Administrator, default);
        Assert.Equal(published.PublishedRevision.Id, Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default)).PublishedRevision!.Id);
    }

    [Fact]
    public async Task AllSharedMainsRequireTheirOwnCategoryAndAppearInTheirOwnLibraryEntries()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var library = new InMemoryStandardLibraryRepository();
        var service = new MaterialRelationService(store, materials, repository, TimeProvider.System, library);
        var a = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-A", "M6x18");
        var b = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MAIN-B", "M6x20");
        var part = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "PART-M6", "M6垫圈");
        var now = DateTimeOffset.UtcNow;
        var categoryA = await library.CreateCategoryAsync(new(Guid.NewGuid(), "机械件", null, 1, true, "admin", now, "admin", now, 1), default);
        var categoryB = await library.CreateCategoryAsync(new(Guid.NewGuid(), "机电件", null, 2, true, "admin", now, "admin", now, 1), default);
        await service.AssignMainCategoryAsync(a.Id, categoryA.Id, "admin", UserRole.Administrator, default);
        var draft = await service.SaveDraftAsync(null, SharedCommand(part, a, b), "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default));
        Assert.Contains(b.MaterialCode, error.Message);
        await service.AssignMainCategoryAsync(b.Id, categoryB.Id, "admin", UserRole.Administrator, default);
        var published = await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(2, published.StandardLibraryCategories.Count);
        Assert.All(published.MainMaterials, item => Assert.Single(item.StandardLibraryCategories));
        var libraryService = new StandardLibraryService(library, materials, repository, TimeProvider.System, relations: store);
        var rows = await libraryService.ListMaterialsAsync(new(null, null, null, false, 1, 50), "admin", UserRole.Administrator, default);
        Assert.Equal(2, rows.Items.Count);
        Assert.All(rows.Items, item => Assert.Equal(published.Id, item.Relation!.Id));
        Assert.All(rows.Items, item => Assert.All(item.Relation!.PublishedMainMaterials, main => Assert.Single(main.StandardLibraryCategories)));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(draft.Id, SharedCommand(a, a, b), "admin", UserRole.Administrator, default));
    }
}
