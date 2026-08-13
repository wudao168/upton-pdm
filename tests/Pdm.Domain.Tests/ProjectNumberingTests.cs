using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectNumberingTests
{
    private static readonly Guid KunshanId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("c0046500-0000-0000-0000-000000000001");

    [Fact]
    public async Task ConcurrentMainProjectsReserveUniqueProjectCustomerAndSerialNumbers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var tasks = Enumerable.Range(1, 20).Select(index => repository.CreateNumberedProjectAsync(
            Command($"并发项目{index}", quantity: 2), CancellationToken.None));

        var projects = await Task.WhenAll(tasks);

        Assert.Equal(20, projects.Select(project => project.Code).Distinct().Count());
        Assert.Equal(20, projects.Select(project => project.DeviceModel).Distinct().Count());
        Assert.Equal(40, projects.SelectMany(project => project.SerialNumbers).Distinct().Count());
        Assert.All(projects, project =>
        {
            Assert.Equal(2, project.SerialNumbers.Count);
            Assert.EndsWith("-00", project.DeviceModel);
        });
    }

    [Fact]
    public async Task ChildProjectHasOneModelWhileQuantityReservesMultipleContinuousSerials()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var parent = await repository.CreateNumberedProjectAsync(Command("主项目", quantity: 1), CancellationToken.None);

        var first = await repository.CreateSubprojectAsync(new(parent.Id, "子项目一", null, 2), CancellationToken.None);
        var second = await repository.CreateSubprojectAsync(new(parent.Id, "子项目二", null, 1), CancellationToken.None);

        Assert.Equal("P700001-1", first.Code);
        Assert.Equal("AK-2-C00465-001-01", first.DeviceModel);
        Assert.Equal(["70000002", "70000003"], first.SerialNumbers);
        Assert.Equal("P700001-2", second.Code);
        Assert.Equal("AK-2-C00465-001-02", second.DeviceModel);
        Assert.Equal(["70000004"], second.SerialNumbers);
    }

    [Fact]
    public async Task OrganizationBaselineContinuesExistingProjectAndSerialNumbersAndCannotMoveBackward()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.AdvanceOrganizationCountersAsync(KunshanId, 2130, 6071, CancellationToken.None);

        var project = await repository.CreateNumberedProjectAsync(Command("延续历史流水", quantity: 2), CancellationToken.None);

        Assert.Equal("P702131", project.Code);
        Assert.Equal(["70006072", "70006073"], project.SerialNumbers);
        await Assert.ThrowsAsync<PdmRuleException>(() => repository.AdvanceOrganizationCountersAsync(KunshanId, 2000, 6000, CancellationToken.None));
    }

    private static CreateNumberedProjectCommand Command(string name, int quantity) => new(
        KunshanId,
        "P",
        2,
        CustomerId,
        name,
        null,
        new DateOnly(2026, 8, 13),
        quantity,
        "engineer",
        @"D:\PDM\Vault",
        @"D:\PDM\Release");
}
