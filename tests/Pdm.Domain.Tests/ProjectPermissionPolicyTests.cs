using Upton.Pdm.Application;
using Upton.Pdm.Domain;

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

    private static Project NewProject() => new(Guid.NewGuid(), "P700001", "测试项目", "owner", @"D:\PDM\Vault", @"D:\PDM\Release", true);
}
