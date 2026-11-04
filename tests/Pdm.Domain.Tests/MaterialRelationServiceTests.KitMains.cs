using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    [Fact]
    public async Task ReleasedKitCanShareConfigurationAndEachReferenceSelectsAccessoriesOnce()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var kits = new InMemoryEngineeringKitRepository();
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, TimeProvider.System, kits: kits);
        var a = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "KIT-A", "子料甲");
        var b = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "KIT-B", "子料乙");
        var accessory = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "KIT-ACC", "外接配件");
        var now = DateTimeOffset.UtcNow;
        var kitId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var components = new[] { new EngineeringKitComponent(Guid.NewGuid(), revisionId, a.Id, a.MaterialCode, a.Name, 2, "001", false, 1),
            new EngineeringKitComponent(Guid.NewGuid(), revisionId, b.Id, b.MaterialCode, b.Name, 3, "001", false, 2) };
        var draft = new EngineeringKitRevision(revisionId, kitId, 1, EngineeringKitRevisionState.Draft, "初版", components, "admin", now, null, null);
        var kit = await kits.SaveDraftAsync(new(kitId, null, null, "HMI组合", "UPTON", null, null, [draft], "admin", now, "admin", now, 1), draft, null, default);
        Assert.DoesNotContain(await service.SearchMainOptionsAsync("HMI", "admin", UserRole.Administrator, default), row => row.MaterialId == kitId);
        kit = await kits.PublishAsync(kitId, kit.RowVersion, "admin", now, "UTJ-12", default);
        Assert.True(Assert.Single(await service.SearchMainOptionsAsync(kit.Model, "admin", UserRole.Administrator, default)).IsEngineeringKit);
        var relation = await service.SaveDraftAsync(null, new(kitId, "套件配套", null, null,
            [new("外接配件", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1, [new(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 2, false, 1)])], [kitId, a.Id]), "admin", UserRole.Administrator, default);
        var published = await service.PublishAsync(relation.Id, relation.DraftRevision!.Id, relation.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        // Keep the ordinary material out of this BOM: the kit itself is configured once per reference.
        var project = await pdm.CreateProjectAsync(new("KIT-RELATION-QA", "套件关联验证", "admin", "vault", "release"), "admin", default);
        var reference = Guid.NewGuid();
        var rows = components.Select((component, index) => new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, index + 1,
            component.MaterialCode, component.MaterialName, component.Quantity * 3, "001", null, null, "W1", true) {
                EngineeringKitReferenceId = reference, EngineeringKitId = kitId, EngineeringKitRevisionId = revisionId,
                EngineeringKitCode = kit.Code, EngineeringKitComponentId = component.Id }).ToArray();
        // This test shares with another main without giving that component its own rule.
        relation = await service.SaveDraftAsync(relation.Id, new(kitId, "套件配套", null, null,
            [new("外接配件", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1, [new(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 2, false, 1)])], [kitId]), "admin", UserRole.Administrator, default);
        published = await service.PublishAsync(relation.Id, relation.DraftRevision!.Id, relation.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, rows, default);
        var check = Assert.Single((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default)).MainMaterials);
        Assert.Equal(reference, check.MainBomItemId); Assert.Equal(3, check.MainQuantity);
        var group = published.PublishedRevision!.Groups.Single();
        var pending = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
        Assert.Contains(kit.Code!, pending.Message);
        Assert.Contains(group.Name, pending.Message);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, [rows[0].Id], default));
        var applied = await service.ApplyAsync(project.Id, [new(reference, [new(group.Id, [group.Options.Single().Id])])], "admin", UserRole.Administrator, default);
        Assert.True(applied.IsComplete);
        await service.EnsureCompleteAsync(project.Id, default);
        await service.EnsureCompleteAsync(project.Id, [rows[0].Id], default);
        var saved = await pdm.GetBomAsync(project.Id, BomKind.Standard, default);
        Assert.Equal(6, Assert.Single(saved, item => item.DrawingNumber == accessory.MaterialCode).Quantity);
        Assert.Equal(new decimal[] { 6, 9 }, saved.Where(item => item.EngineeringKitReferenceId == reference).Select(item => item.Quantity));
        var componentRelation = await SaveAndPublishAsync(service, a, accessory);
        var componentGroup = componentRelation.PublishedRevision!.Groups.Single();
        var split = new[] { new MaterialRelationAllocationCommand(Guid.NewGuid(), "套件组合甲", 1, [new(group.Id, [group.Options.Single().Id])]),
            new MaterialRelationAllocationCommand(Guid.NewGuid(), "套件组合乙", 2, [new(group.Id, [group.Options.Single().Id])]) };
        var combined = await service.ApplyAsync(project.Id,
            [new(reference, [], split), new(rows[0].Id, [new(componentGroup.Id, [componentGroup.Options.Single().Id])])], "admin", UserRole.Administrator, default);
        Assert.True(combined.IsComplete);
        Assert.Equal(2, combined.MainMaterials.Single(item => item.MainBomItemId == reference).Allocations.Count);
        Assert.Single(combined.MainMaterials.Single(item => item.MainBomItemId == rows[0].Id).Allocations);
        var componentRow = (await pdm.GetBomAsync(project.Id, BomKind.Standard, default)).Single(item => item.Id == rows[0].Id);
        Assert.Equal(2, componentRow.EngineeringKitRelationAllocations.Count);
        Assert.Single(componentRow.RelationAllocations);
        await service.ChangeQuantitiesAsync(project.Id,[new(split.Select(allocation=>new BomConfigurationQuantityMember(reference,allocation.Id)).ToArray(),5,3,true)],"admin",UserRole.Administrator,default);
        var resized=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);
        Assert.Equal(new decimal[]{10,15},resized.Where(item=>item.EngineeringKitReferenceId==reference).Select(item=>item.Quantity));
        Assert.All(resized.Where(item=>item.RelationMainBomItemId.HasValue),item=>Assert.Equal(10,item.Quantity));
        Assert.True((await service.GetCompletenessAsync(project.Id,"admin",UserRole.Administrator,default)).IsComplete);
        var child=resized.Single(item=>item.Id==rows[0].Id);
        await service.ChangeQuantitiesAsync(project.Id,[new([new(child.Id,child.RelationAllocations.Single().Id)],20,10,true)],"admin",UserRole.Administrator,default);
        var fromChild=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);
        Assert.Equal(new decimal[]{20,30},fromChild.Where(item=>item.EngineeringKitReferenceId==reference).Select(item=>item.Quantity));
        Assert.All(fromChild.Where(item=>item.RelationMainBomItemId.HasValue),item=>Assert.Equal(20,item.Quantity));
        await Assert.ThrowsAsync<PdmRuleException>(()=>service.ChangeQuantitiesAsync(project.Id,[new([new(child.Id,child.RelationAllocations.Single().Id)],21,20,true)],"admin",UserRole.Administrator,default));
        await kits.SetActiveAsync(kitId, false, kit.RowVersion, "admin", now, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null, new(kitId, "无效套件", null, null, [], [kitId]), "admin", UserRole.Administrator, default));
    }
}
