using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    [Fact]
    public async Task ChangingOnlyAnAccessoryStillRequiresItsMainMaterialRelationToBeComplete()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, TimeProvider.System);
        var project = await pdm.CreateProjectAsync(new("REL-ACCESSORY-" + Guid.NewGuid().ToString("N"), "配件单独发布核对", "admin", "vault", "release"), "admin", default);
        var material = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "REL-MAIN", "电机");
        var accessory = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "REL-ACCESSORY", "控制器");
        var template = await SaveAndPublishAsync(service, material, accessory);
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, material.MaterialCode, material.Name, 1, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main, main with { Id = Guid.NewGuid(), Sequence = 2 }], default);
        var group = template.PublishedRevision!.Groups.Single();
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [group.Options.Single().Id])])], "admin", UserRole.Administrator, default);
        var row = Assert.Single(await pdm.GetBomAsync(project.Id, BomKind.Electrical, default));
        await service.EnsureCompleteAsync(project.Id, [row.Id], default);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Electrical, [row with { Quantity = 2 }], default);
        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, [row.Id], default));
        Assert.Contains(material.MaterialCode, blocked.Message);
        Assert.Contains("数量不匹配", blocked.Message);
        Assert.DoesNotContain("第2行", blocked.Message);
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [])])], "admin", UserRole.Administrator, default);
        Assert.Empty(await store.ListSelectionsAsync(project.Id, default));
        Assert.Equal(main.Id, Assert.Single(await store.ListRelatedMainBomItemIdsAsync(project.Id, [row.Id], default)));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, [row.Id], default));
        var package = await pdm.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-REMOVE-RELATED", ReleasePackageState.Draft,
            null, "", "", [], DateTimeOffset.UtcNow, null, "") { Scope = ReleaseScope.ElectricalSupplement, ConfirmedRemovedBomItemIds = [row.Id] }, default);
        var pendingRemoval = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default, package.Id);
        Assert.False(pendingRemoval.IsComplete);
        Assert.Equal(main.Id, Assert.Single(pendingRemoval.MainMaterials).MainBomItemId);
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [], true)])], "admin", UserRole.Administrator, default);
        Assert.True((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default, package.Id)).IsComplete);
    }

    [Fact]
    public async Task ReleaseChecksOnlySelectedMainRowsAndDoesNotBlockOnAnotherRowWithTheSameCode()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var service = new MaterialRelationService(new InMemoryMaterialRelationRepository(), materials, pdm, TimeProvider.System);
        var project = await pdm.CreateProjectAsync(new("REL-SCOPE-" + Guid.NewGuid().ToString("N"), "发布范围核对", "admin", "vault", "release"), "admin", default);
        var material = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "SCOPE-MAIN", "电机");
        var accessory = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "SCOPE-ACC", "控制器");
        var template = await SaveAndPublishAsync(service, material, accessory);
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, material.MaterialCode, material.Name, 1, "001", null, null, "W1", true);
        var other = main with { Id = Guid.NewGuid(), Sequence = 2 };
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main, other], default);
        await service.ApplyAsync(project.Id, [new(main.Id, [new(template.PublishedRevision!.Groups.Single().Id, [], true)])], "admin", UserRole.Administrator, default);
        await service.EnsureCompleteAsync(project.Id, [main.Id], default);
        var package = await pdm.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-MAIN-SCOPE", ReleasePackageState.Draft,
            null, "", "", [], DateTimeOffset.UtcNow, null, "") { Scope = ReleaseScope.StandardFormal, SelectedBomItemIds = [main.Id] }, default);
        var scoped = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default, package.Id);
        Assert.True(scoped.IsComplete);
        Assert.Equal(main.Id, Assert.Single(scoped.MainMaterials).MainBomItemId);
        var legacy = await pdm.CreateReleasePackageAsync(package with { Id = Guid.NewGuid(), Number = "RP-LEGACY-SCOPE", SelectedBomItemIds = [], StandardBomSnapshot = [main] }, default);
        var legacyScope = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default, legacy.Id);
        Assert.True(legacyScope.IsComplete);
        Assert.Equal(main.Id, Assert.Single(legacyScope.MainMaterials).MainBomItemId);
        await service.EnsureCompleteAsync(project.Id, [], default);
        var pending = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, [other.Id], default));
        Assert.Contains("第2行", pending.Message);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryGroupRequiresSelectionOrExplicitNoAccessoryEvenAfterSavingEmptyChoices(bool required)
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, TimeProvider.System);
        var project = await pdm.CreateProjectAsync(new("REL-GATE-" + Guid.NewGuid().ToString("N"), "发布核对测试", "admin", "vault", "release"), "admin", default);
        var mainMaterial = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "GATE-MAIN", "驱动电机");
        var accessory = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "GATE-ACCESSORY", "电机控制器");
        var template = await service.SaveDraftAsync(null, new(mainMaterial.Id, "发布配套", null, null,
            [new("控制器选型", required, MaterialRelationSelectionMode.Single, required ? 1 : 0, 1, false, 1,
                [new(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 1, false, 1)])]), "admin", UserRole.Administrator, default);
        template = await service.PublishAsync(template.Id, template.DraftRevision!.Id, template.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var group = template.PublishedRevision!.Groups.Single();
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 8, mainMaterial.MaterialCode, mainMaterial.Name, 2, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
        var empty = await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [])])], "admin", UserRole.Administrator, default);
        Assert.False(empty.IsComplete);
        Assert.Empty(await store.ListReviewsAsync(project.Id, default));
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
        Assert.Contains(mainMaterial.MaterialCode, error.Message);
        Assert.Contains(mainMaterial.Name, error.Message);
        Assert.Contains(group.Name, error.Message);
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [group.Options.Single().Id])])], "admin", UserRole.Administrator, default);
        await service.EnsureCompleteAsync(project.Id, default);
        var noAccessory = await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [], true)])], "admin", UserRole.Administrator, default);
        Assert.True(noAccessory.IsComplete);
        Assert.Equal(MaterialRelationReviewDecision.NoAccessory, Assert.Single(await store.ListReviewsAsync(project.Id, default)).Decision);
        await service.EnsureCompleteAsync(project.Id, default);
        Assert.Empty(await pdm.GetBomAsync(project.Id, BomKind.Electrical, default));
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [])])], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
    }
}
