using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class RolePermissionRepositoryTests
{
    [Fact]
    public async Task PlatformAdministratorPermissionsCannotBeEditedOrCopied()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);

        var editError = await Assert.ThrowsAsync<PdmRuleException>(() => repository.SetRolePermissionsAsync("platform_admin", [], default));
        Assert.Contains("平台管理员", editError.Message);
        var copyError = await Assert.ThrowsAsync<PdmRuleException>(() => repository.CreateRoleAsync("平台副本", "", "platform_admin", default));
        Assert.Contains("平台管理员", copyError.Message);

        var directory = await repository.GetRolePermissionDirectoryAsync(default);
        var platform = Assert.Single(directory.Roles, role => role.Role == "platform_admin");
        Assert.True(RolePermissionCatalog.Defaults[UserRole.PlatformAdministrator].SetEquals(platform.Permissions));
        Assert.DoesNotContain(directory.Roles, role => role.Name == "平台副本");
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
        Assert.True(permissions.SetEquals([
            PermissionCodes.MaterialView, PermissionCodes.ProductionDrawingManage, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView]));

        await repository.SetRolePermissionsAsync(customRole.Role, [], default);
        var afterRevocation = await repository.GetUserPermissionsAsync("delivery-role-test", UserRole.ProductionViewer, default);
        Assert.True(afterRevocation.SetEquals([PermissionCodes.MaterialView]));
        Assert.False(await repository.HasUserPermissionAsync("delivery-role-test", UserRole.ProductionViewer, PermissionCodes.ProductionDrawingManage, default));
    }
}
