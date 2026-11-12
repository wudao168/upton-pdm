using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

/// <summary>业务回归使用真实账号；权限边界测试仍直接创建空仓库。</summary>
internal static class BusinessTestRepository
{
    public static InMemoryPdmRepository CreateWithDeveloper(TimeProvider clock)
    {
        var repository = Create(clock);
        repository.CreateUserAsync(new(Guid.NewGuid(), "developer", "业务验收维护员", "unused", UserRole.Administrator, true,
            RoleCode: "developer", CompanyId: Guid.Parse("70000000-0000-0000-0000-000000000001")), default).GetAwaiter().GetResult();
        return repository;
    }

    public static InMemoryPdmRepository Create(TimeProvider clock,
        IProjectContactInformationRepository? projectContacts = null,
        IProductionTrackingStore? productionTracking = null,
        IProjectBudgetRepository? projectBudgets = null)
    {
        var repository = new InMemoryPdmRepository(clock, projectContacts, productionTracking, projectBudgets);
        PermissionTestUsers.Ensure(repository, ("admin", UserRole.Administrator), ("engineer", UserRole.Engineer))
            .GetAwaiter().GetResult();
        return repository;
    }
}
