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
}
