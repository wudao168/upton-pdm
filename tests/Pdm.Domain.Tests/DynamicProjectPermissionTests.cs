using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class DynamicProjectPermissionTests
{
    private const string Position = "custom-11111111111111111111111111111111";

    [Fact]
    public async Task SavedGranularRoleDoesNotRegainRevokedActionsThroughReadOrLegacyCapability()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.SetRolePermissionsAsync("Engineer", [FunctionPermissionCatalog.SchemaMarker, PermissionCodes.ProjectContentView, "bom.mechanical.row.create"], default);
        Assert.True(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, "bom.mechanical.row.create", default));
        Assert.True(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, PermissionCodes.BomMechanicalEdit, default));
        Assert.False(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, "bom.mechanical.row.update", default));
        Assert.False(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, "project.file.delete", default));
        var raw = RolePermissionCatalog.Normalize(UserRole.Engineer, [FunctionPermissionCatalog.SchemaMarker, "bom.mechanical.row.create"]);
        Assert.True(raw.SetEquals(RolePermissionCatalog.Normalize(UserRole.Engineer, raw)));
    }

    [Fact]
    public void LegacyRoleAndPositionBundlesHaveEquivalentActionsAndUnchangedBudgetRule()
    {
        var original = ProjectPermissionSettings.Default;
        var migrated = original.Normalize();
        foreach (var position in ProjectPermissionPositions.All)
            foreach (var operation in RolePermissionCatalog.Permissions.Where(item => item.LegacyPermission is not null && !item.LegacyAllPositions))
                Assert.Equal(original.Rules[position].Contains(operation.LegacyPermission!), migrated.Rules[position].Contains(operation.Code));
        foreach (var position in ProjectPermissionPositions.All)
            Assert.Equal(original.Rules[position].Contains(PermissionCodes.ProjectBudgetView), migrated.Rules[position].Contains(PermissionCodes.ProjectBudgetView));
        Assert.DoesNotContain("bom.mechanical.row.create", migrated.Rules[ProjectPermissionPositions.Other]);
        Assert.Contains("project.file.create", migrated.Rules[ProjectPermissionPositions.Other]);
    }

    [Fact]
    public void CustomPositionSelfSubtreeDisableAndCompanyBoundariesAreEnforced()
    {
        var root = new Project(Guid.NewGuid(), "ROOT", "测试项目", "owner", "D:/test", "D:/release", true)
        {
            OrganizationId = Guid.NewGuid(), CustomPositionMembers = new Dictionary<string, IReadOnlyList<string>> { [Position] = ["member", "second"] }
        };
        var child = root with { Id = Guid.NewGuid(), ParentProjectId = root.Id, RootProjectId = root.Id, CustomPositionMembers = new Dictionary<string, IReadOnlyList<string>>() };
        var rules = ProjectPermissionSettings.Default.Normalize().Rules.ToDictionary(item => item.Key, item => item.Value);
        rules[Position] = ["bom.mechanical.row.create"];
        var position = new ProjectPositionDefinition(Position, "复核工程师", "测试岗位");
        var settings = new ProjectPermissionSettings(rules, [position], 2).Normalize();
        Assert.True(ProjectPermissionPolicy.Allows(root, root, "MEMBER", settings, "bom.mechanical.row.create"));
        Assert.True(ProjectPermissionPolicy.Allows(root, root, "second", settings, "bom.mechanical.row.create"));
        Assert.False(ProjectPermissionPolicy.Allows(child, root, "member", settings, "bom.mechanical.row.create"));
        Assert.False(ProjectPermissionPolicy.Allows(root, root, "member", settings, "bom.mechanical.row.delete"));
        settings = settings with { CustomPositions = [position with { Scope = "subtree" }] };
        Assert.True(ProjectPermissionPolicy.Allows(child, root, "member", settings, "bom.mechanical.row.create"));
        Assert.False(ProjectPermissionPolicy.Allows(child with { OrganizationId = Guid.NewGuid() }, root, "member", settings, "bom.mechanical.row.create"));
        settings = settings with { CustomPositions = [position with { IsActive = false }] };
        Assert.False(ProjectPermissionPolicy.Allows(root, root, "member", settings, "bom.mechanical.row.create"));
    }

    [Fact]
    public async Task ReferencedPositionCannotBeDeletedAndAssignmentsKeepHistory()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var rules = ProjectPermissionSettings.Default.Normalize().Rules.ToDictionary(item => item.Key, item => item.Value);
        rules[Position] = [];
        var settings = new ProjectPermissionSettings(rules, [new(Position, "复核工程师", "测试岗位")], 2);
        await repository.UpdateProjectPermissionSettingsAsync(settings, default);
        await repository.SetCustomProjectPositionsAsync(project.Id, new Dictionary<string, IReadOnlyList<string>> { [Position] = ["engineer"] }, "admin", default);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.UpdateProjectPermissionSettingsAsync(ProjectPermissionSettings.Default.Normalize(), "admin", UserRole.Administrator, default));
        await workflow.UpdateProjectPermissionSettingsAsync(settings with { CustomPositions = [settings.CustomPositions![0] with { IsActive = false }] }, "admin", UserRole.Administrator, default);
        Assert.Equal("engineer", Assert.Single((await repository.FindProjectAsync(project.Id, default))!.CustomPositionMembers[Position]));
    }

    [Fact]
    public void DirectoryDependenciesAndOperationsAreRegisteredAndUnique()
    {
        var directory = RolePermissionCatalog.Permissions;
        Assert.Equal(directory.Count, directory.Select(item => item.Code).Distinct().Count());
        Assert.All(directory.SelectMany(item => item.Dependencies), dependency => Assert.Contains(directory, item => item.Code == dependency));
        Assert.All(directory.Where(item => item.LegacyPermission is not null), item => Assert.Contains(directory, parent => parent.Code == item.LegacyPermission));
        Assert.Contains(directory, item => item.Code == "project.plan.create" && item.Routes.Count > 0);
        Assert.Contains(directory, item => item.Code == "project.file.recycle" && item.Routes.Count > 0);
        Assert.DoesNotContain("unregistered.operation", ProjectPermissionSettings.Operations);
    }
}
