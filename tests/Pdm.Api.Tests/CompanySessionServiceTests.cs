using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class CompanySessionServiceTests
{
    private static readonly Guid KunshanCompanyId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid GuangzhouCompanyId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task OrdinaryUser_DefaultsToPrimaryCompanyAndRejectsUnassignedCompany()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var account = new UserAccount(Guid.NewGuid(), "engineer", "工程师", "unused", UserRole.Engineer, true, CompanyId: KunshanCompanyId);
        await repository.CreateUserAsync(account, default);
        var service = new CompanySessionService(repository);

        var session = await service.ResolveAsync(account, null, default);

        Assert.Equal(KunshanCompanyId, session.PrimaryCompanyId);
        Assert.Equal(KunshanCompanyId, session.ActiveCompanyId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(account, GuangzhouCompanyId.ToString(), default));
    }

    [Fact]
    public async Task OrdinaryUser_CanSwitchOnlyToExplicitlyAssignedCompany()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var account = new UserAccount(Guid.NewGuid(), "cross-user", "跨公司用户", "unused", UserRole.Engineer, true, CompanyId: KunshanCompanyId, CrossCompanyView: true);
        await repository.CreateUserAsync(account, default);
        await repository.SetUserCompanyScopeAsync(account.Username, KunshanCompanyId, true, [GuangzhouCompanyId], "platformadmin", default);
        var service = new CompanySessionService(repository);

        var session = await service.ResolveAsync(account, GuangzhouCompanyId.ToString(), default);

        Assert.Equal(GuangzhouCompanyId, session.ActiveCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == KunshanCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == GuangzhouCompanyId);
    }

    [Fact]
    public async Task PlatformAdministrator_CanSwitchToEveryEnabledCompany()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var account = new UserAccount(Guid.NewGuid(), "platformadmin", "平台管理员", "unused", UserRole.PlatformAdministrator, true,
            RoleCode: "platform_admin", CompanyId: KunshanCompanyId);
        await repository.CreateUserAsync(account, default);
        var service = new CompanySessionService(repository);

        var session = await service.ResolveAsync(account, GuangzhouCompanyId.ToString(), default);

        Assert.Equal(GuangzhouCompanyId, session.ActiveCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == KunshanCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == GuangzhouCompanyId);
    }

    [Fact]
    public async Task Developer_CanSwitchToEveryEnabledCompany()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var account = new UserAccount(Guid.NewGuid(), "developer", "开发者", "unused", UserRole.Administrator, true,
            RoleCode: "developer", CompanyId: KunshanCompanyId);
        await repository.CreateUserAsync(account, default);
        var service = new CompanySessionService(repository);

        var session = await service.ResolveAsync(account, GuangzhouCompanyId.ToString(), default);

        Assert.Equal(GuangzhouCompanyId, session.ActiveCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == KunshanCompanyId);
        Assert.Contains(session.AccessibleCompanies, company => company.Id == GuangzhouCompanyId);
    }
}
