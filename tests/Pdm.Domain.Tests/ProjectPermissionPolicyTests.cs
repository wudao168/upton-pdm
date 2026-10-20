using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectPermissionPolicyTests
{
    [Fact]
    public void AssignedElectricalEngineer_HasEngineerPositionOnlyOnItsProject()
    {
        var root = NewProject();
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id, PhaseOwners = new Dictionary<string, string> { ["ElectricalEngineer"] = "electrical" } };
        var sibling = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id };
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "electrical", ProjectPermissionSettings.Default, PermissionCodes.BomElectricalEdit));
        Assert.False(ProjectPermissionPolicy.Allows(sibling, root, "electrical", ProjectPermissionSettings.Default, PermissionCodes.BomElectricalEdit));
        Assert.False(ProjectPermissionPolicy.Allows(root, root, "electrical", ProjectPermissionSettings.Default, PermissionCodes.BomElectricalEdit));
    }

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
            ProjectPermissionPolicy.AllowedOperations(child, root, "manager", new HashSet<string> { PermissionCodes.ProjectContentView, PermissionCodes.ValidationPlanEdit }, configured));
    }

    [Fact]
    public void ElectricalHardwareOwners_CanMaintainRootAndChildren_WithinConfiguredPermissions()
    {
        var root = NewProject() with { PhaseOwners = new Dictionary<string, string> { ["ElectricalHardware"] = "hardware、second" } };
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id };
        var permissions = new HashSet<string> { PermissionCodes.ProjectContentView, PermissionCodes.BomEdit, PermissionCodes.BomElectricalEdit, PermissionCodes.ReleaseManage };
        foreach (var project in new[] { root, child })
        {
            foreach (var actor in new[] { "HARDWARE", "second" })
            {
                var allowed = ProjectPermissionPolicy.AllowedOperations(project, root, actor, permissions, ProjectPermissionSettings.Default);
                Assert.Contains(PermissionCodes.BomElectricalEdit, allowed);
                Assert.Contains(PermissionCodes.ReleaseManage, allowed);
                Assert.DoesNotContain(PermissionCodes.BomMechanicalEdit, allowed);
            }
            Assert.DoesNotContain(PermissionCodes.BomElectricalEdit, ProjectPermissionPolicy.AllowedOperations(project, root, "unassigned", permissions, ProjectPermissionSettings.Default));
        }
        var otherRoot = NewProject();
        Assert.DoesNotContain(PermissionCodes.BomElectricalEdit, ProjectPermissionPolicy.AllowedOperations(otherRoot, otherRoot, "hardware", permissions, ProjectPermissionSettings.Default));
        var configured = new ProjectPermissionSettings(ProjectPermissionSettings.Default.Rules.ToDictionary(item => item.Key,
            item => item.Key == ProjectPermissionPositions.Engineer ? (IReadOnlyList<string>)[] : item.Value));
        Assert.DoesNotContain(PermissionCodes.BomElectricalEdit, ProjectPermissionPolicy.AllowedOperations(child, root, "hardware", permissions, configured));
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

    [Fact]
    public async Task ChildHardwareOwner_OnlyReceivesAssignedChildPermissions_AndRoleRevocationTakesEffect()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"), "P", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "子项目硬件权限", null,
            new DateOnly(2026, 10, 15), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "已分配子项目", null, 1), default);
        var sibling = await repository.CreateSubprojectAsync(new(root.Id, "其他子项目", null, 1), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "child-hardware", "子项目硬件工程师", "unused", UserRole.Engineer, true,
            RoleCode: "HardwareEngineer", RoleCodes: ["HardwareEngineer"]), default);
        await repository.SetProjectPhaseOwnersAsync(child.Id, new Dictionary<string, string> { ["ElectricalHardware"] = "child-hardware" }, "admin", default);

        var visible = await repository.ListProjectsForUserAsync("child-hardware", UserRole.Engineer, default);
        foreach (var project in new[] { root, child, sibling })
        {
            var permitted = project.Id == child.Id;
            Assert.Equal(permitted, visible.Single(item => item.Id == project.Id).EffectiveProjectPermissions.Contains(PermissionCodes.BomElectricalEdit));
            Assert.Equal(permitted, await ProjectPermissionPolicy.CanAsync(repository, project.Id, "child-hardware", UserRole.Engineer, PermissionCodes.BomElectricalEdit, default));
            Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "child-hardware", UserRole.Engineer, PermissionCodes.BomMechanicalEdit, default));
        }

        await repository.SetRolePermissionsAsync("HardwareEngineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, child.Id, "child-hardware", UserRole.Engineer, PermissionCodes.BomElectricalEdit, default));
        Assert.DoesNotContain(PermissionCodes.BomElectricalEdit, (await repository.ListProjectsForUserAsync("child-hardware", UserRole.Engineer, default))
            .Single(item => item.Id == child.Id).EffectiveProjectPermissions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyIdentity_IsDeniedEvenWhenOtherPositionOrAdministratorWouldAllow(string? actor)
    {
        var project = NewProject();
        var settings = new ProjectPermissionSettings(new Dictionary<string, IReadOnlyList<string>>
        {
            [ProjectPermissionPositions.Other] = [PermissionCodes.BomEdit]
        });
        Assert.False(ProjectPermissionPolicy.Allows(project, project, actor!, settings, PermissionCodes.BomEdit));
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var existing = Assert.Single(await repository.ListProjectsAsync(default));
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, existing.Id, actor!, UserRole.Administrator, PermissionCodes.BomEdit, default));
    }

    [Fact]
    public void UnknownOperation_AndUnrelatedOrCrossCompanyRoot_CannotGrantPermissions()
    {
        var root = NewProject() with { PrimaryProjectManager = "manager", OrganizationId = Guid.NewGuid() };
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id, OrganizationId = root.OrganizationId };
        var unrelated = NewProject() with { PrimaryProjectManager = "manager", OrganizationId = root.OrganizationId };
        var settings = new ProjectPermissionSettings(new Dictionary<string, IReadOnlyList<string>>
        {
            [ProjectPermissionPositions.MainManager] = [PermissionCodes.ProjectPlanEdit, "project.unknown"]
        });
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "manager", settings, PermissionCodes.ProjectPlanEdit));
        Assert.False(ProjectPermissionPolicy.Allows(child, root, "manager", settings, "project.unknown"));
        Assert.False(ProjectPermissionPolicy.Allows(child, unrelated, "manager", settings, PermissionCodes.ProjectPlanEdit));
        Assert.False(ProjectPermissionPolicy.Allows(child, root with { OrganizationId = Guid.NewGuid() }, "manager", settings, PermissionCodes.ProjectPlanEdit));
    }

    [Fact]
    public async Task BudgetView_UsesExistingRoleOrProjectPositionGrant_ConsistentlyWithBudgetService()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetMainProjectStaffingAsync(project.Id, new("manager", [], ["engineer"]), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        var budgets = new ProjectBudgetService(repository, new InMemoryProjectBudgetRepository(), new InMemoryU9ProcurementRepository(),
            TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));

        Assert.Contains(PermissionCodes.ProjectBudgetView, (await repository.ListProjectsForUserAsync("engineer", UserRole.Engineer, default))
            .Single(item => item.Id == project.Id).EffectiveProjectPermissions);
        Assert.True(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "engineer", UserRole.Engineer, PermissionCodes.ProjectBudgetView, default));
        Assert.NotNull(await budgets.GetAsync(project.Id, "engineer", UserRole.Engineer, default));
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "outsider", UserRole.Engineer, PermissionCodes.ProjectBudgetView, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => budgets.GetAsync(project.Id, "outsider", UserRole.Engineer, default));

        var settings = await repository.GetProjectPermissionSettingsAsync(default);
        await repository.UpdateProjectPermissionSettingsAsync(new(settings.Rules.ToDictionary(pair => pair.Key,
            pair => pair.Key == ProjectPermissionPositions.MainDesigner
                ? (IReadOnlyList<string>)pair.Value.Where(code => code != PermissionCodes.ProjectBudgetView).ToArray() : pair.Value)), default);
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "engineer", UserRole.Engineer, PermissionCodes.ProjectBudgetView, default));
        Assert.DoesNotContain(PermissionCodes.ProjectBudgetView, (await repository.ListProjectsForUserAsync("engineer", UserRole.Engineer, default))
            .Single(item => item.Id == project.Id).EffectiveProjectPermissions);

        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectBudgetView], default);
        Assert.True(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "outsider", UserRole.Engineer, PermissionCodes.ProjectBudgetView, default));
        Assert.Contains(PermissionCodes.ProjectBudgetView, (await repository.ListProjectsForUserAsync("outsider", UserRole.Engineer, default))
            .Single(item => item.Id == project.Id).EffectiveProjectPermissions);
        Assert.NotNull(await budgets.GetAsync(project.Id, "outsider", UserRole.Engineer, default));

        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView], default);
        await repository.UpdateProjectPermissionSettingsAsync(settings, default);
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "engineer", UserRole.Engineer, PermissionCodes.ProjectBudgetView, default));
        Assert.Empty((await repository.ListProjectsForUserAsync("engineer", UserRole.Engineer, default))
            .Single(item => item.Id == project.Id).EffectiveProjectPermissions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => budgets.GetAsync(project.Id, "engineer", UserRole.Engineer, default));
    }

    [Fact]
    public void BudgetProjection_RejectsMissingReadPermission_EmptyActor_AndInvalidProjectContext()
    {
        var root = NewProject() with { DesignLead = "lead", OrganizationId = Guid.NewGuid() };
        var child = NewProject() with { ParentProjectId = root.Id, RootProjectId = root.Id, OrganizationId = root.OrganizationId };
        var permissions = new HashSet<string> { PermissionCodes.ProjectContentView, PermissionCodes.ProjectBudgetView };
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(child, root, "lead", new HashSet<string> { PermissionCodes.ProjectBudgetView }, ProjectPermissionSettings.Default));
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(child, root, "", permissions, ProjectPermissionSettings.Default));
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(child, root with { Id = Guid.NewGuid() }, "lead", permissions, ProjectPermissionSettings.Default));
        Assert.Empty(ProjectPermissionPolicy.AllowedOperations(child, root with { OrganizationId = Guid.NewGuid() }, "lead", permissions, ProjectPermissionSettings.Default));
    }

    private static Project NewProject() => new(Guid.NewGuid(), "P700001", "测试项目", "owner", @"D:\PDM\Vault", @"D:\PDM\Release", true);
}
