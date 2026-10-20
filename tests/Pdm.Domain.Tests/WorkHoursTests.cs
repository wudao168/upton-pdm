using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class WorkHoursTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero); }
    private static readonly DateOnly From = new(2026, 9, 28), To = new(2026, 10, 4);
    private static async Task<(InMemoryPdmRepository Repo, InMemoryWorkHoursStore Store, WorkHoursService Service, Project Root, Project Child)> Setup()
    {
        TenantContext.Clear();
        var clock = new Clock();
        var repo = new InMemoryPdmRepository(clock);
        var company = (await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First();
        foreach (var name in new[] { "admin", "worker", "other", "manager" })
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "admin" ? UserRole.Administrator : UserRole.Engineer, true) { CompanyId = company.Id }, default);
        var root = await repo.CreateNumberedProjectAsync(new(company.Id, "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "工时测试", null, new(2026, 9, 1), 1, "worker", "D:/test", "D:/release"), default);
        var child = await repo.CreateSubprojectAsync(new(root.Id, "上料单元", null, 1), default);
        await repo.SetMainProjectStaffingAsync(root.Id, new("worker", [], ["worker"]), "admin", default);
        var store = new InMemoryWorkHoursStore();
        return (repo, store, new(repo, store, clock), root, child);
    }
    private static SaveWorkHours Input(WorkHoursView view, params WorkHourInput[] entries) => new(From, To, entries,
        view.Sheets.Select(x => new WorkHoursVersion(x.Month, x.RowVersion)).ToArray());

    [Fact]
    public async Task CrossMonthWeekRetainsOtherDaysHistoryAndRejectsStaleEdits()
    {
        var (_, _, service, root, child) = await Setup();
        var view = await service.ReadAsync(From, To, false, "worker", UserRole.Engineer, default);
        Assert.Equal(new(2026, 10, 20), view.StartDate);
        Assert.Contains(view.Projects, x => x.Id == child.Id);
        var input = Input(view, new(From, "厂内", 4.5m, child.Id), new(new(2026, 10, 1), "厂外", 8, root.Id));
        view = await service.SaveAsync(input, "worker", UserRole.Engineer, default);
        Assert.Equal(2, view.Sheets.Sum(x => x.Entries.Count));
        var childEntry = view.Sheets.SelectMany(x => x.Entries).Single(x => x.ProjectId == child.Id);
        Assert.Equal(root.Id, childEntry.RootProjectId);
        Assert.Equal(root.Code, childEntry.RootProjectCode);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(input, "worker", UserRole.Engineer, default));
        var revised = await service.SaveAsync(new(From, From, [new(From, "厂内", 6, child.Id)], [new("2026-09", view.Sheets.Single(x => x.Month == "2026-09").RowVersion)]), "worker", UserRole.Engineer, default);
        Assert.Equal(childEntry.Id, Assert.Single(revised.Sheets.Single().Entries).Id);
        var all = await service.ReadAsync(From, To, false, "worker", UserRole.Engineer, default);
        Assert.Equal(14, all.Sheets.SelectMany(x => x.Entries).Sum(x => x.Hours));
        var history = await service.HistoryAsync("2026-09", "worker", default);
        Assert.Equal(2, history.Count);
        Assert.Equal(4.5m, Assert.Single(history.Last().Before).Hours);
        Assert.Equal(6, Assert.Single(history.Last().After).Hours);
        await service.SaveAsync(new(From, From, [], [new("2026-09", revised.Sheets.Single().RowVersion)]), "worker", UserRole.Engineer, default);
        history = await service.HistoryAsync("2026-09", "worker", default);
        Assert.Empty(history.Last().After);
        Assert.Single(history.Last().Before);
    }

    [Theory]
    [InlineData(0, "厂内")]
    [InlineData(-1, "厂内")]
    [InlineData(0.25, "厂内")]
    [InlineData(25, "厂内")]
    [InlineData(4, "远程")]
    public async Task InvalidHoursAndLocationsDoNotWrite(decimal hours, string location)
    {
        var (_, store, service, root, _) = await Setup();
        var view = await service.ReadAsync(From, To, false, "worker", UserRole.Engineer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(Input(view, new WorkHourInput(From, location, hours, root.Id)), "worker", UserRole.Engineer, default));
        Assert.Empty(await store.ReadAsync(root.OrganizationId!.Value, "2026-09", "2026-10", default));
    }

    [Fact]
    public async Task DuplicateOver24FutureAndForeignProjectsAreRejected()
    {
        var (_, _, service, root, child) = await Setup();
        var view = await service.ReadAsync(From, To, false, "worker", UserRole.Engineer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(Input(view, new(From, "厂内", 8, root.Id), new(From, "厂内", 4, root.Id)), "worker", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(Input(view, new(From, "厂内", 16, root.Id), new(From, "厂外", 9, child.Id)), "worker", UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(Input(view, new WorkHourInput(From, "厂内", 8, Guid.NewGuid())), "worker", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(new(new(2026, 10, 21), new(2026, 10, 21), [], []), "worker", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ReadAsync(new(2025, 1, 1), new(2026, 1, 2), true, "worker", UserRole.Engineer, default));
    }

    [Fact]
    public async Task EmployeeSeesSelfAndUnitManagerOnlyTheirSubtreeWhileAdministratorSeesCompany()
    {
        var (repo, _, service, root, _) = await Setup();
        var unit = await repo.SaveOrganizationUnitAsync(new(null, root.OrganizationId!.Value, null, "WH", "工时部门", OrganizationUnitKind.Department, true, 1), default);
        await repo.SetOrganizationMembershipsAsync("worker", [unit.Id], unit.Id, default);
        await repo.SetOrganizationUnitManagersAsync(unit.Id, "manager", [], default);
        var own = await service.ReadAsync(From, To, true, "worker", UserRole.Engineer, default);
        Assert.Single(own.People);
        var managed = await service.ReadAsync(From, To, true, "manager", UserRole.Engineer, default);
        Assert.Contains(managed.People, x => x.Username == "worker");
        Assert.DoesNotContain(managed.People, x => x.Username == "other");
        var admin = await service.ReadAsync(From, To, true, "admin", UserRole.Administrator, default);
        Assert.Contains(admin.People, x => x.Username == "other");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(From, To, false, "unknown", UserRole.Engineer, default));
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "worker", "Engineer", false, new HashSet<string>()));
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(From, To, true, "worker", UserRole.Engineer, default)); }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task StoreCommitsBothMonthsOrNeitherAndOnlyOneConcurrentSaveWins()
    {
        var (_, store, _, root, _) = await Setup();
        var company = root.OrganizationId!.Value;
        var september = new WorkHoursMonth(company, "worker", "2026-09", 0, [], []);
        var october = september with { Month = "2026-10" };
        Assert.True(await store.SaveAsync([october], default));
        Assert.False(await store.SaveAsync([september, october], default));
        Assert.DoesNotContain(await store.ReadAsync(company, "2026-09", "2026-10", default), x => x.Month == "2026-09");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.SaveAsync([september], default)));
        Assert.Single(results, x => x);
    }
}
