using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

internal static class ProductionTestAuthorization
{
    public static async Task EnableAsync(InMemoryPdmRepository repository, Guid? companyId = null)
    {
        var company = companyId ?? TenantContext.CompanyId ?? (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "测试管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var current = await repository.GetRolePermissionsAsync(UserRole.Administrator, default);
        await repository.SetRolePermissionsAsync("Administrator", current.Concat(new[] {
            PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProductionAssign,
            PermissionCodes.ProductionExecute, PermissionCodes.ProductionInspect, PermissionCodes.ProductionIssue,
            PermissionCodes.ProductionSchedule, PermissionCodes.ProductionScanManage, PermissionCodes.ProductionCapacityManage,
            ProductionMobilePermissions.Use, ProductionMobilePermissions.Print, ProductionMobilePermissions.Reprint,
            ProductionMobilePermissions.Agent }).Distinct().ToArray(), default);
    }
}
