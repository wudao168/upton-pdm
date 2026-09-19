using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class U9ProcurementServiceTests
{
    [Fact]
    public async Task ListProjectUsesCurrentImpactStageWithoutChangingPublishedDemand()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var itemId = Guid.NewGuid();
        var publishedItem = new BomItem(
            itemId, project.Id, BomKind.Standard, 1, "STD-001", "已发布名称", 2, "001", null, "M6", "A", true)
        {
            ImpactStage = ProjectPlanStage.Commissioning
        };
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard,
        [
            publishedItem with
            {
                Name = "当前名称",
                Quantity = 99,
                ImpactStage = ProjectPlanStage.Assembly
            }
        ], default);
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), project.Id, "RP-IMPACT-001", ReleasePackageState.Published, Guid.NewGuid(),
            "A", "A", [], time.GetUtcNow().AddMinutes(-1), time.GetUtcNow(), "D:\\Release\\RP-IMPACT-001")
        {
            Scope = ReleaseScope.StandardFormal,
            StandardBomSnapshot = [publishedItem]
        }, default);

        var service = new U9ProcurementService(
            new InMemoryU9ProcurementRepository(),
            new InMemoryMaterialRepository(time),
            repository,
            new InMemoryProjectPlanningRepository(),
            null!,
            null!,
            null!,
            time);

        var result = await service.ListProjectAsync(project.Id, "admin", UserRole.Administrator, default);

        var row = Assert.Single(result.Items);
        Assert.Equal(ProjectPlanStage.Assembly, row.ImpactStage);
        Assert.Equal(2, row.Quantity);
        Assert.Equal("已发布名称", row.MaterialName);
    }

    [Fact]
    public async Task ListProjectTakesAssemblyAndCommissioningStartDatesFromPlanStageTasks()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard,
        [
            new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, "STD-002", "装配件", 1, "001", null, "M6", "A", true)
        ], default);
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), project.Id, "RP-STAGE-001", ReleasePackageState.Published, Guid.NewGuid(),
            "A", "A", [], time.GetUtcNow().AddMinutes(-1), time.GetUtcNow(), "D:\\Release\\RP-STAGE-001")
        {
            Scope = ReleaseScope.StandardFormal,
            StandardBomSnapshot = await repository.GetBomAsync(project.Id, BomKind.Standard, default)
        }, default);

        var plans = new InMemoryProjectPlanningRepository();
        ProjectPlanTask Task(string stage, string name, DateOnly start) =>
            new(Guid.NewGuid(), name, stage, null, 3, start, start.AddDays(2), null, null, null, null, 0,
                ProjectPlanTaskStatus.NotStarted, [], 1, false, true, 10);
        await plans.SavePlanAsync(new ProjectPlan(
            Guid.NewGuid(), project.Id, Guid.NewGuid(), "标准模板", ProjectPlanStage.Assembly, null, null,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 31), 0,
            [
                Task(ProjectPlanStage.Design, "机械设计与内部评审", new DateOnly(2026, 9, 1)),
                Task(ProjectPlanStage.Assembly, "设备装配", new DateOnly(2026, 9, 20)),
                Task(ProjectPlanStage.Assembly, "装配补件", new DateOnly(2026, 9, 24)),
                Task(ProjectPlanStage.Commissioning, "厂内调试", new DateOnly(2026, 10, 2))
            ],
            "admin", time.GetUtcNow(), "admin", time.GetUtcNow(), 0), null, null, default);

        var service = new U9ProcurementService(
            new InMemoryU9ProcurementRepository(),
            new InMemoryMaterialRepository(time),
            repository,
            plans,
            null!,
            null!,
            null!,
            time);

        var result = await service.ListProjectAsync(project.Id, "admin", UserRole.Administrator, default);

        var row = Assert.Single(result.Items);
        Assert.Equal(new DateOnly(2026, 9, 20), row.AssemblyStartDate);
        Assert.Equal(new DateOnly(2026, 10, 2), row.CommissioningStartDate);
    }
}
