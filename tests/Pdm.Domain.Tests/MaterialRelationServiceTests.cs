using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class MaterialRelationServiceTests
{
    [Fact]
    public async Task UniqueAccessory_RequiresExplicitSelectionAndKeepsEachMainRowIndependent()
    {
        var clock = TimeProvider.System;
        var pdm = new InMemoryPdmRepository(clock);
        var materials = new InMemoryMaterialRepository(clock);
        var relations = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(relations, materials, pdm, clock);
        var project = await pdm.CreateProjectAsync(new(
            $"REL-{Guid.NewGuid():N}", "配套物料测试", "admin", @"D:\PDM\RelationTest", @"D:\PDM\RelationRelease"), "admin", default);
        var motorA = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MOTOR-A", "电机A");
        var motorB = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MOTOR-B", "电机B");
        var controller = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "CTRL-01", "共用控制器");

        var publishedA = await SaveAndPublishAsync(service, motorA, controller);
        var publishedB = await SaveAndPublishAsync(service, motorB, controller);
        Assert.NotEqual(publishedA.PublishedRevision!.Id, publishedB.PublishedRevision!.Id);

        var mainA = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, motorA.MaterialCode, motorA.Name, 2, "001", null, null, "W1", true);
        var mainB = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 2, motorB.MaterialCode, motorB.Name, 3, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [mainA, mainB], default);

        var incomplete = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.False(incomplete.IsComplete);
        Assert.All(incomplete.MainMaterials.SelectMany(item => item.Groups), group => Assert.Equal("待核对", group.Status));
        await service.EnsureCompleteAsync(project.Id, default);

        var unchanged = await service.ApplyAsync(project.Id,
        [
            new ApplyMaterialRelationsCommand(mainA.Id, []),
            new ApplyMaterialRelationsCommand(mainB.Id, [])
        ], "admin", UserRole.Administrator, default);
        Assert.False(unchanged.IsComplete);
        Assert.DoesNotContain(await pdm.GetBomAsync(project.Id, BomKind.Electrical, default), item => item.Source == "MaterialRelation");

        var groupA = publishedA.PublishedRevision!.Groups.Single();
        var groupB = publishedB.PublishedRevision!.Groups.Single();
        var result = await service.ApplyAsync(project.Id,
        [
            new ApplyMaterialRelationsCommand(mainA.Id, [new MaterialRelationChoice(groupA.Id, [groupA.Options.Single().Id])]),
            new ApplyMaterialRelationsCommand(mainB.Id, [new MaterialRelationChoice(groupB.Id, [groupB.Options.Single().Id])])
        ], "admin", UserRole.Administrator, default);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.MainMaterialCount);
        var accessoryRows = (await pdm.GetBomAsync(project.Id, BomKind.Electrical, default))
            .Where(item => item.Source == "MaterialRelation" && item.DrawingNumber == controller.MaterialCode)
            .OrderBy(item => item.Quantity)
            .ToArray();
        Assert.Equal(2, accessoryRows.Length);
        Assert.Equal([2m, 3m], accessoryRows.Select(item => item.Quantity).ToArray());
        Assert.Equal(2, accessoryRows.Select(item => item.Id).Distinct().Count());
        Assert.Equal(2, (await relations.ListSelectionsAsync(project.Id, default)).Count);
        await service.EnsureCompleteAsync(project.Id, default);

        // A same-quantity attribute change must invalidate every main row, including rows sharing an accessory.
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [mainA with { Remark = "设计备注变更" }, mainB], default);
        var stale = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.False(stale.IsComplete);
        Assert.All(stale.MainMaterials, item => Assert.False(item.IsComplete));
        Assert.All(stale.MainMaterials.SelectMany(item => item.Groups), item => Assert.Contains("重新核对", item.Status));
        await service.EnsureCompleteAsync(project.Id, default);
        var rechecked = await service.ApplyAsync(project.Id,
        [
            new ApplyMaterialRelationsCommand(mainA.Id, [new MaterialRelationChoice(groupA.Id, [groupA.Options.Single().Id])]),
            new ApplyMaterialRelationsCommand(mainB.Id, [new MaterialRelationChoice(groupB.Id, [groupB.Options.Single().Id])])
        ], "admin", UserRole.Administrator, default);
        Assert.True(rechecked.IsComplete);
        Assert.True((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default)).IsComplete);
        Assert.Equal(2, (await pdm.GetBomAsync(project.Id, BomKind.Electrical, default)).Count);

        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [mainA with { Remark = "设计备注变更", Quantity = 2.0000m, ReconciliationUpdatedAt = DateTimeOffset.UtcNow }, mainB], default);
        Assert.True((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default)).IsComplete);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [mainA with { Remark = "设计备注变更" }], default);
        Assert.False((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default)).IsComplete);

        var extra = mainB with { Id = Guid.NewGuid(), DrawingNumber = "UNRELATED", Sequence = 3 };
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [mainA with { Remark = "设计备注变更" }, mainB, extra], default);
        Assert.False((await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default)).IsComplete);
    }

    [Fact]
    public async Task NoAccessoryConfirmation_IsRecordedAndQuantityChangeReturnsToPending()
    {
        var clock = TimeProvider.System;
        var pdm = new InMemoryPdmRepository(clock);
        var materials = new InMemoryMaterialRepository(clock);
        var relations = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(relations, materials, pdm, clock);
        var project = await pdm.CreateProjectAsync(new(
            $"REL-{Guid.NewGuid():N}", "无需配套测试", "admin", @"D:\PDM\RelationTest", @"D:\PDM\RelationRelease"), "admin", default);
        var motor = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "MOTOR-A", "电机A");
        var controller = await AddApprovedMaterialAsync(materials, MaterialKind.Electrical, "0101", "CTRL-01", "控制器");
        var published = await SaveAndPublishAsync(service, motor, controller);
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, motor.MaterialCode, motor.Name, 2, "001", null, null, "W1", true);
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        var group = published.PublishedRevision!.Groups.Single();

        var reviewed = await service.ApplyAsync(project.Id,
            [new ApplyMaterialRelationsCommand(main.Id, [new MaterialRelationChoice(group.Id, [], true, "仅补充主物料")])],
            "admin", UserRole.Administrator, default);

        var groupCheck = Assert.Single(Assert.Single(reviewed.MainMaterials).Groups);
        Assert.True(groupCheck.IsComplete);
        Assert.Equal("已确认无需", groupCheck.Status);
        Assert.Equal(MaterialRelationReviewDecision.NoAccessory, groupCheck.ReviewDecision);
        Assert.Equal("仅补充主物料", groupCheck.ReviewReason);
        Assert.Empty(await relations.ListSelectionsAsync(project.Id, default));

        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, [main with { Quantity = 3 }], default);
        var changed = await service.GetCompletenessAsync(project.Id, "admin", UserRole.Administrator, default);
        var changedGroup = Assert.Single(Assert.Single(changed.MainMaterials).Groups);
        Assert.False(changedGroup.IsComplete);
        Assert.Equal("待核对", changedGroup.Status);
        await service.EnsureCompleteAsync(project.Id, default);
    }

    private static async Task<MaterialRelationTemplate> SaveAndPublishAsync(MaterialRelationService service, PdmMaterial main, PdmMaterial accessory)
    {
        var draft = await service.SaveDraftAsync(null, new(
            main.Id,
            $"{main.Name}标准配套",
            "初版",
            null,
            [new SaveMaterialRelationGroupCommand(
                "控制器", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1,
                [new SaveMaterialRelationOptionCommand(accessory.Id, MaterialRelationQuantityMode.PerMainQuantity, 1, false, 1)])]),
            "admin", UserRole.Administrator, default);
        return await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
    }

    private static async Task<PdmMaterial> AddApprovedMaterialAsync(
        InMemoryMaterialRepository repository,
        MaterialKind kind,
        string categoryCode,
        string code,
        string name)
    {
        var category = await repository.FindCategoryAsync(categoryCode, default) ?? throw new InvalidOperationException();
        var now = DateTimeOffset.UtcNow;
        var material = new PdmMaterial(
            Guid.NewGuid(), code, name, kind, MaterialSupplyMode.Purchase, "001", null, null, null, null, null,
            null, null, null, MaterialApprovalStatus.Approved, "admin", now, categoryCode, null, code,
            MaterialSyncStatus.Succeeded, "admin", now, "admin", now, 1, categoryCode, U9SyncConfirmed: true);
        return await repository.CreateMaterialAsync(material, category, default);
    }
}
