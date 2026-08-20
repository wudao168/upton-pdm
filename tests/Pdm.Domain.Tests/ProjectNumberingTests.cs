using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectNumberingTests
{
    private static readonly Guid KunshanId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid GuangzhouId = Guid.Parse("30000000-0000-0000-0000-000000000001");
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

    [Fact]
    public async Task UpdatingMainProjectDetailsPropagatesOrderDateWithoutChangingSystemNumbers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var parent = await repository.CreateNumberedProjectAsync(Command("主项目", quantity: 1), CancellationToken.None);
        var child = await repository.CreateSubprojectAsync(new(parent.Id, "子项目", null, 1), CancellationToken.None);

        var saved = await repository.UpdateProjectDetailsAsync(parent.Id, new(
            KunshanId, "P", 2, CustomerId, "主项目新名称", "新别名", new DateOnly(2026, 8, 16), 1), CancellationToken.None);
        var savedChild = await repository.FindProjectAsync(child.Id, CancellationToken.None);

        Assert.Equal("主项目新名称", saved.Name);
        Assert.Equal("新别名", saved.ProjectAlias);
        Assert.Equal(new DateOnly(2026, 8, 16), saved.SignedDate);
        Assert.Equal(new DateOnly(2026, 8, 16), savedChild?.SignedDate);
        Assert.Equal(parent.Code, saved.Code);
        Assert.Equal(parent.DeviceModel, saved.DeviceModel);
        Assert.Equal(parent.SerialNumbers, saved.SerialNumbers);
    }

    [Fact]
    public async Task DeletingMainProjectReleasesProjectModelAndSerialNumbersForNextCreation()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var deleted = await repository.CreateNumberedProjectAsync(Command("待删除项目", quantity: 2), CancellationToken.None);

        await repository.DeleteProjectAsync(deleted.Id, CancellationToken.None);
        var replacement = await repository.CreateNumberedProjectAsync(Command("替代项目", quantity: 2), CancellationToken.None);

        Assert.Equal(deleted.Code, replacement.Code);
        Assert.Equal(deleted.DeviceModel, replacement.DeviceModel);
        Assert.Equal(deleted.SerialNumbers, replacement.SerialNumbers);
    }

    [Fact]
    public async Task DeletingChildProjectReusesSmallestChildAndSerialNumber()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var parent = await repository.CreateNumberedProjectAsync(Command("主项目", quantity: 1), CancellationToken.None);
        var deleted = await repository.CreateSubprojectAsync(new(parent.Id, "待删除子项目", null, 1), CancellationToken.None);
        await repository.CreateSubprojectAsync(new(parent.Id, "保留子项目", null, 1), CancellationToken.None);

        await repository.DeleteProjectAsync(deleted.Id, CancellationToken.None);
        var replacement = await repository.CreateSubprojectAsync(new(parent.Id, "替代子项目", null, 1), CancellationToken.None);

        Assert.Equal(1, replacement.ChildSequence);
        Assert.Equal(deleted.Code, replacement.Code);
        Assert.Equal(deleted.DeviceModel, replacement.DeviceModel);
        Assert.Equal(deleted.SerialNumbers, replacement.SerialNumbers);
    }

    [Fact]
    public async Task EditingAllCreationFieldsRegeneratesRootAndChildNumbersTogether()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var parent = await repository.CreateNumberedProjectAsync(Command("主项目", quantity: 1), CancellationToken.None);
        var child = await repository.CreateSubprojectAsync(new(parent.Id, "子项目", null, 1), CancellationToken.None);

        var saved = await repository.UpdateProjectDetailsAsync(parent.Id, new(
            GuangzhouId, "W", 8, CustomerId, "新主项目", "新别名", new DateOnly(2026, 8, 16), 2), CancellationToken.None);
        var savedChild = await repository.FindProjectAsync(child.Id, CancellationToken.None);

        Assert.Equal("W300001", saved.Code);
        Assert.Equal("AG-8-C00465-001-00", saved.DeviceModel);
        Assert.Equal(2, saved.SerialNumbers.Count);
        Assert.All(saved.SerialNumbers, serial => Assert.StartsWith("3", serial));
        Assert.Equal("W300001-1", savedChild?.Code);
        Assert.Equal("AG-8-C00465-001-01", savedChild?.DeviceModel);
        Assert.Equal(new DateOnly(2026, 8, 16), savedChild?.SignedDate);
        Assert.All(savedChild!.SerialNumbers, serial => Assert.StartsWith("3", serial));
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
