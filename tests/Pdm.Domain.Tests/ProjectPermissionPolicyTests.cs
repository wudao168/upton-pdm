using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectPermissionPolicyTests
{
    [Fact]
    public void MainDesigner_CanEditEveryChild_EngineerOnlyAssignedChild()
    {
        var root = NewProject() with { DesignLeads = ["lead"], Designers = ["root-engineer"] };
        var first = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id, Designers = ["engineer"] };
        var second = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id };
        var settings = ProjectPermissionSettings.Default;

        Assert.True(ProjectPermissionPolicy.Allows(first, root, "lead", settings, PermissionCodes.BomMechanicalEdit));
        Assert.True(ProjectPermissionPolicy.Allows(second, root, "lead", settings, PermissionCodes.ValidationPlanEdit));
        Assert.True(ProjectPermissionPolicy.Allows(first, root, "engineer", settings, PermissionCodes.ValidationPlanEdit));
        Assert.False(ProjectPermissionPolicy.Allows(second, root, "engineer", settings, PermissionCodes.ValidationPlanEdit));
    }

    [Fact]
    public void Manager_CanManageOwnTree_BomRemainsReadOnly_AndSettingsTakeEffect()
    {
        var root = NewProject() with { PrimaryProjectManager = "manager", CollaborativeProjectManagers = ["collaborator"] };
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id, PrimaryProjectManager = "child-manager" };
        var settings = ProjectPermissionSettings.Default;

        Assert.True(ProjectPermissionPolicy.Allows(child, root, "manager", settings, PermissionCodes.ValidationPlanEdit));
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "collaborator", settings, PermissionCodes.ReleaseManage));
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "child-manager", settings, PermissionCodes.ValidationPlanEdit));
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "manager", settings, PermissionCodes.ProjectBudgetView));
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "child-manager", settings, PermissionCodes.ProjectBudgetView));
        Assert.False(ProjectPermissionPolicy.Allows(root, root, "child-manager", settings, PermissionCodes.ProjectBudgetView));
        Assert.False(ProjectPermissionPolicy.Allows(root, root, "child-manager", settings, PermissionCodes.ValidationPlanEdit));
        Assert.False(ProjectPermissionPolicy.Allows(child, root, "manager", settings, PermissionCodes.BomMechanicalEdit));
        Assert.False(ProjectPermissionPolicy.Allows(child, root, "other", settings, PermissionCodes.ValidationPlanEdit));

        var configured = new ProjectPermissionSettings(settings.Rules.ToDictionary(item => item.Key, item => item.Key == ProjectPermissionPositions.MainManager
            ? (IReadOnlyList<string>)item.Value.Append(PermissionCodes.BomMechanicalEdit).ToArray()
            : item.Value)).Normalize();
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "manager", configured, PermissionCodes.BomMechanicalEdit));
        Assert.DoesNotContain(PermissionCodes.BomMechanicalEdit,
            ProjectPermissionPolicy.AllowedOperations(child, root, "manager", new HashSet<string> { PermissionCodes.ValidationPlanEdit }, configured));
    }

    [Fact]
    public void ElectricalHardwareOwners_CanMaintainRootAndChildren_WithinConfiguredPermissions()
    {
        var root = NewProject() with { PhaseOwners = new Dictionary<string, string> { ["ElectricalHardware"] = "hardware、second" } };
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id };
        var permissions = new HashSet<string> { PermissionCodes.BomEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ReleaseManage };
        foreach (var project in new[] { root, child })
        {
            foreach (var actor in new[] { "HARDWARE", "second" })
            {
                var allowed = ProjectPermissionPolicy.AllowedOperations(project, root, actor, permissions, ProjectPermissionSettings.Default);
                Assert.Contains(PermissionCodes.BomElectricalEdit, allowed);
                Assert.Contains(PermissionCodes.ReleaseManage, allowed);
                Assert.DoesNotContain(PermissionCodes.BomMechanicalEdit, allowed);
            }
            Assert.Empty(ProjectPermissionPolicy.AllowedOperations(project, root, "unassigned", permissions, ProjectPermissionSettings.Default));
        }
        var otherRoot = NewProject();
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(otherRoot, otherRoot, "hardware", permissions, ProjectPermissionSettings.Default));
        var configured = new ProjectPermissionSettings(ProjectPermissionSettings.Default.Rules.ToDictionary(item => item.Key,
            item => item.Key == ProjectPermissionPositions.Engineer ? (IReadOnlyList<string>)[] : item.Value));
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(child, root, "hardware", permissions, configured));
    }

    [Fact]
    public async Task HardwareOwner_ProjectResponseAndOperationChecksUseSamePermissions()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"), "P", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "硬件权限主项目", null,
            new DateOnly(2026, 10, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "电气权限验证", null, 1), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "hardware", "硬件工程师", "unused", UserRole.Engineer, true,
            RoleCode: "HardwareEngineer", RoleCodes: ["HardwareEngineer"]), default);
        await repository.SetProjectPhaseOwnersAsync(root.Id, new Dictionary<string, string> { ["ElectricalHardware"] = "hardware" }, "admin", default);
        var visibleProjects = (await repository.ListProjectsForUserAsync("hardware", UserRole.Engineer, default)).Where(p => p.Id == root.Id || p.Id == child.Id).ToArray();
        Assert.Equal(2, visibleProjects.Length);
        foreach (var project in visibleProjects)
        {
            Assert.Contains(PermissionCodes.BomElectricalEdit, project.EffectiveProjectPermissions);
            Assert.Contains(PermissionCodes.ReleaseManage, project.EffectiveProjectPermissions);
            Assert.DoesNotContain(PermissionCodes.BomMechanicalEdit, project.EffectiveProjectPermissions);
            Assert.True(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "hardware", UserRole.Engineer, PermissionCodes.BomElectricalEdit, default));
            Assert.True(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "hardware", UserRole.Engineer, PermissionCodes.ReleaseManage, default));
            Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "hardware", UserRole.Engineer, PermissionCodes.BomMechanicalEdit, default));
        }
        await repository.SetProjectPhaseOwnersAsync(root.Id, new Dictionary<string, string>(), "admin", default);
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, child.Id, "hardware", UserRole.Engineer, PermissionCodes.BomElectricalEdit, default));
    }

    private static Project NewProject() => new(Guid.NewGuid(), "P700001", "测试项目", "owner", @"D:\PDM\Vault", @"D:\PDM\Release", true);
}
