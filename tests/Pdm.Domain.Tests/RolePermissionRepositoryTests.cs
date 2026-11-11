using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class RolePermissionRepositoryTests
{
    [Fact]
    public async Task PlatformPermissionsCanBeEditedAndCopiedAsExplicitTemplates()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var current = await repository.GetAuthorizationSettingsAsync(default);
        var platform = current.Templates.Single(t => t.RoleCode == "platform_admin");
        var saved = await repository.SaveAuthorizationSettingsAsync(current with { Templates = current.Templates.Select(t => t.Key == platform.Key ? t with { Grants = [] } : t).Append(platform with { Key = "platform-copy", RoleCode = null }).ToArray() }, current.Revision, default);
        Assert.Empty(saved.Templates.Single(t => t.Key == platform.Key).Grants);
        Assert.Equal(platform.Grants, saved.Templates.Single(t => t.Key == "platform-copy").Grants);
    }

    [Fact]
    public async Task CustomRolePermissionsCombineAcrossAssignmentsAndRevocationTakesEffectImmediately()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var directory = await repository.CreateRoleAsync("图纸发放员", "", "ProductionViewer", default);
        var customRole = Assert.Single(directory.Roles, role => role.Name == "图纸发放员");
        await repository.SetRolePermissionsAsync(customRole.Role, [PermissionCodes.ProductionDrawingManage], default);
        await repository.SetRolePermissionsAsync("ProductionViewer", [PermissionCodes.MaterialView], default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "delivery-role-test", "权限测试", "unused", UserRole.ProductionViewer, true,
            RoleCodes: ["ProductionViewer", customRole.Role]), default);

        var permissions = await repository.GetUserPermissionsAsync("delivery-role-test", UserRole.ProductionViewer, default);
        Assert.True(permissions.IsSupersetOf([
            PermissionCodes.MaterialView, PermissionCodes.ProductionDrawingManage, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView]));
        Assert.DoesNotContain(PermissionCodes.BomMechanicalEdit, permissions);
        Assert.DoesNotContain(PermissionCodes.ProjectBudgetView, permissions);

        await repository.SetRolePermissionsAsync(customRole.Role, [], default);
        var afterRevocation = await repository.GetUserPermissionsAsync("delivery-role-test", UserRole.ProductionViewer, default);
        Assert.True(afterRevocation.Where(p => p != ProjectCenterPermissions.SchemaMarker).ToHashSet().SetEquals([PermissionCodes.MaterialView]));
        Assert.False(await repository.HasUserPermissionAsync("delivery-role-test", UserRole.ProductionViewer, PermissionCodes.ProductionDrawingManage, default));
    }
}
