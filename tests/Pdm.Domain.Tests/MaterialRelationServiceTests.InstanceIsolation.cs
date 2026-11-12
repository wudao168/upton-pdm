using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingOneInstanceNeverChangesAnotherSameMaterialOrKitInstance(bool useKit)
    {
        var clock = TimeProvider.System;
        var pdm = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(clock);
        var materials = new InMemoryMaterialRepository(clock);
        var kits = new InMemoryEngineeringKitRepository();
        var store = new InMemoryMaterialRelationRepository();
        var service = new MaterialRelationService(store, materials, pdm, clock, kits: kits);
        var main = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ISOLATION-MAIN", "重复主料");
        var panelA = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ISOLATION-A", "面板甲");
        var panelB = await AddApprovedMaterialAsync(materials, MaterialKind.Standard, "0102", "ISOLATION-B", "面板乙");
        var at = clock.GetUtcNow();
        var kitId = Guid.NewGuid(); var kitRevision = Guid.NewGuid(); var componentId = Guid.NewGuid();
        var component = new EngineeringKitComponent(componentId, kitRevision, main.Id, main.MaterialCode, main.Name, 1, "001", false, 1);
        var revision = new EngineeringKitRevision(kitRevision, kitId, 1, EngineeringKitRevisionState.Draft, "隔离验证", [component], "admin", at, null, null);
        var kit = await kits.SaveDraftAsync(new(kitId, null, null, "重复套件", "UPTON", null, null, [revision], "admin", at, "admin", at, 1), revision, null, default);
        kit = await kits.PublishAsync(kitId, kit.RowVersion, "admin", at, "UTJ-12", default);
        var draft = await service.SaveDraftAsync(null, new(useKit ? kit.Id : main.Id, "实例独立配置", null, null,
            [new("面板", true, MaterialRelationSelectionMode.Single, 1, 1, false, 1,
                [new(panelA.Id, MaterialRelationQuantityMode.PerMainQuantity, 1, false, 1), new(panelB.Id, MaterialRelationQuantityMode.PerMainQuantity, 1, false, 2)])]),
            "admin", UserRole.Administrator, default);
        var template = await service.PublishAsync(draft.Id, draft.DraftRevision!.Id, draft.DraftRevision.RowVersion, "admin", UserRole.Administrator, default);
        var group = template.PublishedRevision!.Groups.Single();
        var project = await pdm.CreateProjectAsync(new($"ISOLATION-{Guid.NewGuid():N}", "实例隔离", "admin", "vault", "release"), "admin", default);
        var references = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var rows = Enumerable.Range(0, 2).Select(index => new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, index + 1,
            main.MaterialCode, main.Name, index + 2, "001", null, null, "W1", true)
        {
            EngineeringKitReferenceId = useKit ? references[index] : null,
            EngineeringKitId = useKit ? kit.Id : null,
            EngineeringKitRevisionId = useKit ? kitRevision : null,
            EngineeringKitComponentId = useKit ? componentId : null,
            EngineeringKitCode = useKit ? kit.Code : null
        }).ToArray();
        var owners = useKit ? references : rows.Select(item => item.Id).ToArray();
        await pdm.ReplaceBomAsync(project.Id, BomKind.Standard, rows, default);
        await service.ApplyAsync(project.Id,
            [new(owners[0], [new(group.Id, [group.Options[0].Id])]), new(owners[1], [new(group.Id, [group.Options[1].Id])])],
            "admin", UserRole.Administrator, default);
        var before = (await pdm.GetBomAsync(project.Id, BomKind.Standard, default)).ToArray();
        string Snapshot(IEnumerable<BomItem> items) => JsonSerializer.Serialize(items
            .Where(item => item.Id == rows[1].Id || item.EngineeringKitReferenceId == owners[1] || item.RelationMainBomItemId == owners[1])
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.DrawingNumber, item.Name, item.Quantity,
                item.EngineeringKitReferenceId, item.RelationMainBomItemId, item.RelationAllocations, item.EngineeringKitRelationAllocations }));
        var untouched = Snapshot(before);
        foreach (var option in new[] { group.Options[1].Id, group.Options[0].Id })
        {
            var result = await service.ApplyAsync(project.Id, [new(owners[0], [new(group.Id, [option])])], "admin", UserRole.Administrator, default);
            var saved = await pdm.GetBomAsync(project.Id, BomKind.Standard, default);
            Assert.Equal(untouched, Snapshot(saved));
            Assert.Equal(untouched, Snapshot(before));
            Assert.Equal(group.Options[1].Id, result.MainMaterials.Single(item => item.MainBomItemId == owners[1]).Groups.Single().SelectedOptionIds.Single());
            Assert.Equal(3, Assert.Single(saved, item => item.RelationMainBomItemId == owners[1]).Quantity);
            Assert.Equal(2, Assert.Single(saved, item => item.RelationMainBomItemId == owners[0]).Quantity);
            Assert.Equal(2, saved.Where(item => item.RelationMainBomItemId.HasValue).Select(item => item.Id).Distinct().Count());
            Assert.True(result.IsComplete);
        }
    }
}
