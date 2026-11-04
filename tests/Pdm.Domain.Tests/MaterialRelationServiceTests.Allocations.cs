using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    [Fact]
    public async Task Allocations_SeparateSingleChoicesPreserveTotalsAndFrozenSnapshot()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, TimeProvider.System);
        var project = await pdm.CreateProjectAsync(new($"AL-{Guid.NewGuid():N}", "组合选配", "admin", @"D:\PDM\Allocation", @"D:\PDM\Release"), "admin", default);
        var mainMaterial = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ALLOC-MAIN", "主料");
        var a = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "ALLOC-A", "驱动甲");
        var b = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "ALLOC-B", "驱动乙");
        var draft = await service.SaveDraftAsync(null, new(mainMaterial.Id, "两种配套", null, null,
            [new("驱动", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1,
                [new(a.Id, MaterialRelationQuantityMode.PerMainQuantity, 2, false, 1), new(b.Id, MaterialRelationQuantityMode.PerMainQuantity, 3, false, 2)])]), "admin", UserRole.Administrator, default);
        var template = await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var group = template.PublishedRevision!.Groups.Single();
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, mainMaterial.MaterialCode, mainMaterial.Name, 3, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        var allocations = new[] {
            new MaterialRelationAllocationCommand(Guid.NewGuid(), "组合甲", 2, [new(group.Id, [group.Options[0].Id])]),
            new MaterialRelationAllocationCommand(Guid.NewGuid(), "组合乙", 1, [new(group.Id, [group.Options[1].Id])]) };
        var result = await service.ApplyAsync(project.Id, [new(main.Id, [], allocations)], "admin", UserRole.Administrator, default);
        Assert.True(result.IsComplete);
        Assert.Equal(2, Assert.Single(result.MainMaterials).Allocations.Count);
        var accessories = await pdm.GetBomAsync(project.Id, BomKind.Electrical, default);
        Assert.Equal(4, accessories.Single(item => item.DrawingNumber == a.MaterialCode).Quantity);
        Assert.Equal(3, accessories.Single(item => item.DrawingNumber == b.MaterialCode).Quantity);
        await service.EnsureCompleteAsync(project.Id, default);
        var saved = Assert.Single(await pdm.GetBomAsync(project.Id, BomKind.Standard, default));
        var frozen = JsonSerializer.Deserialize<BomItem>(JsonSerializer.Serialize(saved))!;
        var invalid = allocations.Select(item => item with { Quantity = 2 }).ToArray();
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(project.Id, [new(main.Id, [], invalid)], "admin", UserRole.Administrator, default));
        Assert.Contains("合计必须等于3", error.Message);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(Assert.Single(await pdm.GetBomAsync(project.Id, BomKind.Standard, default))));
        var fractions=allocations.Select(item=>item with {Quantity=1.5m}).ToArray();
        var fractionError=await Assert.ThrowsAsync<PdmRuleException>(()=>service.ApplyAsync(project.Id,[new(main.Id,[],fractions)],"admin",UserRole.Administrator,default));
        Assert.Contains("整数",fractionError.Message);
        await service.ApplyAsync(project.Id, [new(main.Id, [new(group.Id, [group.Options[0].Id])])], "admin", UserRole.Administrator, default);
        Assert.Equal(2, frozen.RelationAllocations.Count);
        Assert.Equal(new[] { 2m, 1m }, frozen.RelationAllocations.Select(item => item.Quantity));
        Assert.Equal("驱动甲", frozen.RelationAllocations[0].Groups.Single().Options.Single().MaterialName);
        Assert.Equal(4, frozen.RelationAllocations[0].Groups.Single().Options.Single().Quantity);
        Assert.Single(Assert.Single(await pdm.GetBomAsync(project.Id, BomKind.Standard, default)).RelationAllocations);
    }

    [Fact]
    public async Task Allocations_OneUnreviewedCombinationBlocksReleaseAndFixedOptionCountsPerCombination()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, TimeProvider.System);
        var project = await pdm.CreateProjectAsync(new($"AL-{Guid.NewGuid():N}", "组合核对", "admin", @"D:\PDM\Allocation", @"D:\PDM\Release"), "admin", default);
        var mainMaterial = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ALLOC-M", "主料");
        var accessory = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ALLOC-F", "固定配件");
        var draft = await service.SaveDraftAsync(null, new(mainMaterial.Id, "固定配套", null, null,
            [new("附件", false, MaterialRelationSelectionMode.Single, 0, 1, false, 1, [new(accessory.Id, MaterialRelationQuantityMode.Fixed, 1, false, 1)])]), "admin", UserRole.Administrator, default);
        var template = await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var group = template.PublishedRevision!.Groups.Single();
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, mainMaterial.MaterialCode, mainMaterial.Name, 5, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        var allocations = new[] { new MaterialRelationAllocationCommand(Guid.NewGuid(), "甲", 3, [new(group.Id, [group.Options[0].Id])]),
            new MaterialRelationAllocationCommand(Guid.NewGuid(), "乙", 2, []) };
        var result = await service.ApplyAsync(project.Id, [new(main.Id, [], allocations)], "admin", UserRole.Administrator, default);
        Assert.False(result.IsComplete);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureCompleteAsync(project.Id, default));
        allocations[1] = allocations[1] with { Choices = [new(group.Id, [group.Options[0].Id])] };
        Assert.True((await service.ApplyAsync(project.Id, [new(main.Id, [], allocations)], "admin", UserRole.Administrator, default)).IsComplete);
        Assert.Equal(2, (await pdm.GetBomAsync(project.Id, BomKind.Standard, default)).Single(item => item.Source == "MaterialRelation").Quantity);
    }
}
