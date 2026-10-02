using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectContactInformationServiceTests
{
    [Fact]
    public async Task ContactInformationIsSharedByTheFamilyAndRejectsStaleWrites()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "联系信息验证", null, new(2026, 10, 2), 1, "admin", "D:/test", "D:/release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        var store = new InMemoryProjectContactInformationRepository();
        var service = new ProjectContactInformationService(repository, store, TimeProvider.System);
        var saved = await service.SaveAsync(child.Id, new(" 张先生 ", "13800000000", " 苏州市工业园区 ", "李女士", "13900000000", 0), "admin", UserRole.Administrator, default);
        Assert.Equal(root.Id, saved.Information.ProjectId);
        Assert.Equal(root.CustomerName, saved.CustomerName);
        Assert.Equal("张先生", saved.Information.CustomerContact);
        Assert.Equal("苏州市工业园区", saved.Information.ShippingAddress);
        Assert.Equal(saved.Information, (await service.GetAsync(root.Id, "admin", UserRole.Administrator, default)).Information);
        Assert.Null(await store.FindAsync(child.Id, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 0), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task OnlyAssignedProjectManagersAndAdministratorsCanWrite()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetMainProjectStaffingAsync(root.Id, new("pm", ["co-pm"], ["designer"]), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        var service = new ProjectContactInformationService(repository, new InMemoryProjectContactInformationRepository(), TimeProvider.System);
        foreach (var actor in new[] { "pm", "co-pm" })
        {
            var view = await service.GetAsync(root.Id, actor, UserRole.Engineer, default);
            Assert.True(view.CanEdit);
            await service.SaveAsync(root.Id, new(actor, "", "", "", "", view.Information.RowVersion), actor, UserRole.Engineer, default);
        }
        Assert.False((await service.GetAsync(root.Id, "designer", UserRole.Engineer, default)).CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 2), "designer", UserRole.Engineer, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(root.Id, "pm", UserRole.Engineer, default));
    }

    [Fact]
    public async Task InvalidLengthsAndMissingProjectsAreRejected()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectContactInformationService(repository, new InMemoryProjectContactInformationRepository(), TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new(new string('x', 101), "", "", "", "", 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", new string('x', 501), "", "", 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.GetAsync(Guid.NewGuid(), "admin", UserRole.Administrator, default));
    }
}
