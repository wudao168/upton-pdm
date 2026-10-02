using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectNameplateServiceTests
{
    [Theory]
    [InlineData("HardwareEngineer", UserRole.Engineer)]
    [InlineData("ProcessReviewer", UserRole.ProcessReviewer)]
    [InlineData("Approver", UserRole.Approver)]
    public async Task HardwareAndStandardizationCanMaintainWithoutProjectEdit(string roleCode, UserRole role)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.CreateUserAsync(new(Guid.NewGuid(), "nameplate-editor", "铭牌维护", "unused", role, true, RoleCode: roleCode), default);
        await repository.SetRolePermissionsAsync(roleCode, [PermissionCodes.ProjectContentView], default);
        var service = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(), TimeProvider.System);
        Assert.True((await service.GetAsync(project.Id, "nameplate-editor", role, default)).CanEdit);
        Assert.True((await service.SaveAsync(project.Id, new(true, "380V 50Hz / 5A", "", "", "", null, 0), "nameplate-editor", role, default)).Nameplate.Enabled);
        await repository.SetRolePermissionsAsync(roleCode, [], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(project.Id, "nameplate-editor", role, default));
    }

    [Fact]
    public async Task MechanicalEngineerAndDesignLeadAreLimitedToTheirProjects()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "铭牌权限", null, new(2026, 10, 2), 1, "admin", "D:/test", "D:/release"), default);
        var first = await repository.CreateSubprojectAsync(new(root.Id, "设备一", null, 1), default);
        var second = await repository.CreateSubprojectAsync(new(root.Id, "设备二", null, 1), default);
        var other = await repository.CreateProjectAsync(new("OTHER-NAMEPLATE", "无关项目", "admin", "D:/test", "D:/release"), "admin", default);
        await repository.SetMainProjectStaffingAsync(root.Id, new("manager", [], ["lead"]), "admin", default);
        await repository.SetChildProjectDesignersAsync(first.Id, ["engineer", "electrical"], "admin", default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "electrical", "电气工程师", "unused", UserRole.Engineer, true, RoleCode: "ElectricalEngineer"), default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView], default);
        await repository.SetRolePermissionsAsync("ElectricalEngineer", [PermissionCodes.ProjectContentView], default);
        var service = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(), TimeProvider.System);
        Assert.True((await service.GetAsync(first.Id, "engineer", UserRole.Engineer, default)).CanEdit);
        await service.SaveAsync(first.Id, new(true, "", "", "", "", null, 0), "engineer", UserRole.Engineer, default);
        foreach (var id in new[] { root.Id, second.Id, other.Id })
        {
            Assert.False((await service.GetAsync(id, "engineer", UserRole.Engineer, default)).CanEdit);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(id, new(true, "", "", "", "", null, 0), "engineer", UserRole.Engineer, default));
        }
        Assert.False((await service.GetAsync(first.Id, "electrical", UserRole.Engineer, default)).CanEdit);
        foreach (var id in new[] { root.Id, first.Id, second.Id })
        {
            Assert.True((await service.GetAsync(id, "lead", UserRole.Engineer, default)).CanEdit);
            var version = (await service.GetAsync(id, "lead", UserRole.Engineer, default)).Nameplate.RowVersion;
            await service.SaveAsync(id, new(true, "", "", "", "", null, version), "lead", UserRole.Engineer, default);
        }
        Assert.False((await service.GetAsync(other.Id, "lead", UserRole.Engineer, default)).CanEdit);
    }

    [Fact]
    public async Task EachProjectHasItsOwnNameplateAndIdentityComesFromTheProject()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        var other = await repository.CreateProjectAsync(new("NAMEPLATE-2", "第二台设备", "admin", "D:/test", "D:/release"), "admin", default);
        var store = new InMemoryProjectNameplateRepository();
        var service = new ProjectNameplateService(repository, store, TimeProvider.System);
        var saved = await service.SaveAsync(root.Id, new(true, " AC 380V ", "1000×500×800 mm", "0.6 MPa", "500 kg", new(2026, 10, 2), 0), "admin", UserRole.Administrator, default);
        Assert.True(saved.Nameplate.Enabled);
        Assert.Equal("AC 380V", saved.Nameplate.PowerSupply);
        Assert.Equal(root.Name, saved.Name);
        Assert.Equal(root.DeviceModel ?? "", saved.Model);
        Assert.Equal(root.SerialNumbers, saved.SerialNumbers);
        Assert.Equal(1, saved.Nameplate.RowVersion);
        Assert.False((await service.GetAsync(other.Id, "admin", UserRole.Administrator, default)).Nameplate.Enabled);
        Assert.Equal("", (await service.GetAsync(other.Id, "admin", UserRole.Administrator, default)).Nameplate.PowerSupply);
        Assert.Equal("AC 380V", (await service.GetAsync(root.Id, "admin", UserRole.Administrator, default)).Nameplate.PowerSupply);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(root.Id, new(true, "覆盖", "", "", "", null, 0), "admin", UserRole.Administrator, default));
    }
    [Fact]
    public async Task ReadAndWriteRequireProjectAccessAndEditingPermission()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(), TimeProvider.System);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(project.Id, "unknown", UserRole.Engineer, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, new(true, "", "", "", "", null, 0), "unknown", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.GetAsync(Guid.NewGuid(), "admin", UserRole.Administrator, default));
    }
    [Fact]
    public async Task TemplateRequiresAdministratorAndChecksVersionAndFields()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(), TimeProvider.System);
        var template = NameplateTemplate.Default with { Title = "UPTON 设备铭牌" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveTemplateAsync(template, "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveTemplateAsync(template with { Labels = new Dictionary<string, string>() }, "admin", UserRole.Administrator, default));
        var saved = await service.SaveTemplateAsync(template, "admin", UserRole.Administrator, default);
        Assert.Equal(1, saved.RowVersion);
        Assert.Equal(saved, (await service.GetAsync(project.Id, "admin", UserRole.Administrator, default)).Template);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveTemplateAsync(template, "admin", UserRole.Administrator, default));
    }
    [Fact]
    public async Task OverlongManualFieldsAreRejected()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(), TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(true, new string('x', 101), "", "", "", null, 0), "admin", UserRole.Administrator, default));
    }
}
