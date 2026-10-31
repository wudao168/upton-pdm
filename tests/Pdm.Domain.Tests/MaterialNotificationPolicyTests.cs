using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class MaterialNotificationPolicyTests
{
    [Theory]
    [InlineData("ProcessReviewer", UserRole.ProcessReviewer, true)]
    [InlineData("Approver", UserRole.Approver, true)]
    [InlineData("StandardizationAssistant", UserRole.Engineer, true)]
    [InlineData("Engineer", UserRole.Engineer, false)]
    [InlineData("ProjectManager", UserRole.Engineer, false)]
    [InlineData("MechanicalManager", UserRole.Approver, false)]
    [InlineData("ProductionManager", UserRole.Approver, false)]
    [InlineData("Administrator", UserRole.Administrator, false)]
    [InlineData("developer", UserRole.Administrator, false)]
    public async Task OnlyStandardizationBusinessRolesReceiveByDefault(string code, UserRole role, bool expected)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "notice-user", "提醒验证", "unused", role, true, RoleCode: code), default);
        Assert.Equal(expected, await MaterialNotificationPolicy.CanReceiveAsync(repository, "notice-user", role, default));
    }

    [Fact]
    public async Task NotificationSwitchDoesNotGrantMaterialManagementAndCanBeRevoked()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.MaterialView], default);
        Assert.False(await MaterialNotificationPolicy.CanReceiveAsync(repository, "engineer", UserRole.Engineer, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.MaterialNotify], default);
        Assert.True(await MaterialNotificationPolicy.CanReceiveAsync(repository, "engineer", UserRole.Engineer, default));
        Assert.True(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, PermissionCodes.MaterialView, default));
        Assert.False(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, PermissionCodes.MaterialManage, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.MaterialView], default);
        Assert.False(await MaterialNotificationPolicy.CanReceiveAsync(repository, "engineer", UserRole.Engineer, default));
    }

    [Theory]
    [InlineData("Engineer", false)]
    [InlineData("Approver", true)]
    public async Task MixedAdministratorUsesTheBusinessRolesNotificationPermission(string businessRole, bool expected)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mixed-notice", "兼任岗位", "unused", UserRole.Administrator, true, RoleCodes: ["Administrator", businessRole]), default);
        Assert.Equal(expected, await MaterialNotificationPolicy.CanReceiveAsync(repository, "mixed-notice", UserRole.Administrator, default));
    }
}
