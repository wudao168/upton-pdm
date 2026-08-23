using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class RolePermissionCatalogTests
{
    [Fact]
    public void InitialRoles_ContainConfirmedBusinessRolesWithLeastPrivilegeDefaults()
    {
        Assert.Equal(26, RolePermissionCatalog.Roles.Count);
        Assert.Equal(RolePermissionCatalog.Roles.Count, RolePermissionCatalog.Roles.Select(role => role.RoleCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(RolePermissionCatalog.Roles, role => role.Name == "机械工程师" && role.IsSystem);
        Assert.Contains(RolePermissionCatalog.Roles, role => role.Name == "标准化主管" && role.IsSystem);
        Assert.Contains(RolePermissionCatalog.Roles, role => role.Name == "生产物料员" && role.IsSystem);
        Assert.Contains(RolePermissionCatalog.Roles, role => role.RoleCode == "platform_admin" && role.Name == "平台管理员" && role.IsSystem);
        Assert.Contains(RolePermissionCatalog.Roles, role => role.RoleCode == "developer" && role.Name == "开发者" && role.IsSystemAdministrator);

        var mechanical = RolePermissionCatalog.InitialPermissions(UserRole.Engineer.ToString(), UserRole.Engineer);
        Assert.Contains(PermissionCodes.DocumentEdit, mechanical);
        Assert.Contains(PermissionCodes.BomEdit, mechanical);

        var standardizationSupervisor = RolePermissionCatalog.InitialPermissions(UserRole.Approver.ToString(), UserRole.Approver);
        Assert.Contains(PermissionCodes.ApprovalDecide, standardizationSupervisor);
        Assert.DoesNotContain(PermissionCodes.DocumentEdit, standardizationSupervisor);
        Assert.DoesNotContain(PermissionCodes.BomEdit, standardizationSupervisor);

        var productionMaterialHandler = RolePermissionCatalog.InitialPermissions(UserRole.ProductionViewer.ToString(), UserRole.ProductionViewer);
        Assert.Contains(PermissionCodes.ProjectContentView, productionMaterialHandler);
        Assert.DoesNotContain(PermissionCodes.DocumentEdit, productionMaterialHandler);
        Assert.DoesNotContain(PermissionCodes.BomEdit, productionMaterialHandler);

        var platformAdministrator = RolePermissionCatalog.InitialPermissions("platform_admin", UserRole.PlatformAdministrator);
        Assert.Contains(PermissionCodes.OrganizationSettingsManage, platformAdministrator);
        Assert.DoesNotContain(PermissionCodes.ProjectView, platformAdministrator);

        var developer = RolePermissionCatalog.InitialPermissions("developer", UserRole.Administrator);
        Assert.Equal(RolePermissionCatalog.Permissions.Count, developer.Count);
        Assert.All(RolePermissionCatalog.Permissions, permission => Assert.Contains(permission.Code, developer));
    }
}
