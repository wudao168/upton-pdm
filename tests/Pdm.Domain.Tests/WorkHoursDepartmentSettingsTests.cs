using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class WorkHoursManagementTests
{
    [Fact]
    public async Task DepartmentGroupsFollowOrganizationCodeOrderInsteadOfSortNumberOrName()
    {
        var f = await Setup();
        var third = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,null,"SORT-T3","甲部门",OrganizationUnitKind.Department,true,-100),default);
        var first = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,null,"SORT-T1","丙部门",OrganizationUnitKind.Department,true,999),default);
        var second = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,null,"SORT-T2","乙部门",OrganizationUnitKind.Department,true,0),default);
        var ids = new HashSet<Guid> { first.Id, second.Id, third.Id };
        var settings = await f.Service.ReadManagementAsync("admin",default);
        Assert.Equal(new[] { first.Id, second.Id, third.Id },settings.DepartmentGroups.Where(x=>ids.Contains(x.Id)).Select(x=>x.Id));
        var report = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"admin",UserRole.Administrator,default);
        Assert.Equal("SORT-T2",report.OrganizationUnits.Single(x=>x.Id==second.Id).Code);
    }

    [Fact]
    public async Task ReportOrganizationKeepsHierarchyAndDepartmentIdsWithoutExposingUnauthorizedBranches()
    {
        var f = await Setup();
        await Enable(f, f.Unit, f.OtherUnit);
        var own = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,f.Unit,"MECH-A","机械组",OrganizationUnitKind.Department,true,1),default);
        var other = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,f.OtherUnit,"MECH-B","机械组",OrganizationUnitKind.Department,true,1),default);
        await f.Repo.SetOrganizationMembershipsAsync("worker",[own.Id],own.Id,default);
        await f.Repo.SetOrganizationMembershipsAsync("outside",[other.Id],other.Id,default);
        var admin = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"admin",UserRole.Administrator,default);
        Assert.Equal(own.Id,admin.People.Single(x=>x.Username=="worker").DepartmentId);
        Assert.Equal(other.Id,admin.People.Single(x=>x.Username=="outside").DepartmentId);
        Assert.Equal(f.Unit,admin.OrganizationUnits.Single(x=>x.Id==own.Id).ParentUnitId);
        Assert.Equal(f.OtherUnit,admin.OrganizationUnits.Single(x=>x.Id==other.Id).ParentUnitId);
        var worker = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"worker",UserRole.Engineer,default);
        Assert.Equal(own.Id,worker.DefaultDepartmentId);
        Assert.Contains(worker.OrganizationUnits,x=>x.Id==f.Unit);
        Assert.Contains(worker.OrganizationUnits,x=>x.Id==own.Id);
        Assert.DoesNotContain(worker.OrganizationUnits,x=>x.Id==f.OtherUnit||x.Id==other.Id);
        Assert.DoesNotContain(worker.ParticipatingDepartmentIds, id=>id==f.OtherUnit||id==other.Id);
    }

    [Fact]
    public async Task ReportDepartmentChoicesUseParticipationAndKeepAuthorizedHistorySeparate()
    {
        var f = await Setup();
        var team = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,f.Unit,"WH-CHILD","机械组",OrganizationUnitKind.Department,true,1),default);
        var empty = await f.Repo.SaveOrganizationUnitAsync(new(null,f.Company,null,"WH-EMPTY","参与但暂无人员",OrganizationUnitKind.Department,true,2),default);
        await f.Repo.SetOrganizationMembershipsAsync("worker",[team.Id],team.Id,default);
        var settings = await Enable(f, f.Unit, empty.Id);
        await Fill(f, f.Clock.Day, 4);
        var report = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"admin",UserRole.Administrator,default);
        Assert.Contains(f.Unit,report.ParticipatingDepartmentIds); Assert.Contains(team.Id,report.ParticipatingDepartmentIds); Assert.Contains(empty.Id,report.ParticipatingDepartmentIds);
        Assert.DoesNotContain(f.OtherUnit,report.ParticipatingDepartmentIds);
        Assert.Contains(report.OrganizationUnits,x=>x.Id==f.OtherUnit);
        var outside = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"outside",UserRole.Engineer,default);
        Assert.Empty(outside.ParticipatingDepartmentIds); Assert.Null(outside.DefaultDepartmentId); Assert.Equal("",outside.DefaultDepartment);
        var worker = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"worker",UserRole.Engineer,default);
        Assert.Equal(team.Id,worker.DefaultDepartmentId);
        Assert.DoesNotContain(empty.Id,worker.ParticipatingDepartmentIds);
        await f.Service.SaveGroupSettingsAsync(new(f.Company,settings.RowVersion,[new(f.Unit,false)],[]),"admin",default);
        report = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"admin",UserRole.Administrator,default);
        Assert.DoesNotContain(f.Unit,report.ParticipatingDepartmentIds); Assert.DoesNotContain(team.Id,report.ParticipatingDepartmentIds);
        Assert.Equal(4,Assert.Single(report.Sheets.SelectMany(x=>x.Entries)).Hours);
    }

    [Fact]
    public async Task LegacyParticipationUsesTheSameCurrentRosterRuleAsSettings()
    {
        var f = await Setup();
        Assert.True(await f.Store.SaveCatalogAsync(new(f.Company,0,[new(Guid.NewGuid(),"worker",f.Clock.Day,null,8)],[],[],[]),default));
        var settings = await f.Service.ReadManagementAsync("admin",default);
        var report = await f.Service.ReadAsync(f.Clock.Day,f.Clock.Day,true,"admin",UserRole.Administrator,default);
        Assert.Equal(settings.DepartmentGroups.Where(x=>x.Participating).Select(x=>x.Id).OrderBy(x=>x),report.ParticipatingDepartmentIds.OrderBy(x=>x));
    }
    [Theory]
    [InlineData("ProductionAssistant")]
    [InlineData("SalesAssistant")]
    public async Task DepartmentAssistantRolesCanMaintainTheirOwnDepartment(string roleCode)
    {
        var f = await Setup();
        await f.Repo.CreateUserAsync(new(Guid.NewGuid(), "department-assistant", "部门助理", "unused", UserRole.Engineer, true, RoleCode:roleCode, CompanyId:f.Company), default);
        await f.Repo.SetOrganizationMembershipsAsync("department-assistant", [f.OtherUnit], f.OtherUnit, default);
        var settings = await Enable(f, f.OtherUnit);
        settings = await f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [], [new("outside", true)]), "department-assistant", default);
        Assert.True(settings.DepartmentGroups.Single(x => x.Id == f.OtherUnit).People.Single(x => x.Username == "outside").Required);
        Assert.False(settings.DepartmentGroups.Single(x => x.Id == f.Unit).People.Single(x => x.Username == "worker").CanEdit);
        Assert.False(settings.CanAdjust);
        var own = await f.Service.ReadAsync(f.Clock.Day, f.Clock.Day, false, "department-assistant", UserRole.Engineer, default);
        Assert.True(own.CanManageSettings); Assert.False(own.CanManage);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadCorrectionAsync("outside", "2026-10", "department-assistant", default));
    }
    private static async Task<WorkHoursManagementView> Enable(Fixture f, params Guid[] units)
    {
        var view = await f.Service.ReadManagementAsync("admin", default);
        return await f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, units.Select(x => new WorkHoursDepartmentChange(x, true)).ToArray(), []), "admin", default);
    }
    [Fact]
    public async Task GroupedSettingsSeparateAdministratorDepartmentAndAssistantPeoplePermissions()
    {
        var f = await Setup(); var view = await Enable(f, f.Unit, f.OtherUnit);
        Assert.True(view.CanAuthorize);
        Assert.All(view.DepartmentGroups.SelectMany(x => x.People), x => Assert.False(x.CanEdit));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [], [new("worker", true)]), "admin", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SetRequirementAsync(new(["worker"], f.Clock.Day, null, 8, true, "越权代选", view.RowVersion), "admin", default));
        var assistant = await f.Service.ReadManagementAsync("assistant", default);
        Assert.False(assistant.CanAuthorize);
        Assert.True(assistant.DepartmentGroups.Single(x => x.Id == f.Unit).People.Single(x => x.Username == "worker").CanEdit);
        Assert.False(assistant.DepartmentGroups.Single(x => x.Id == f.OtherUnit).People.Single(x => x.Username == "outside").CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [new(f.OtherUnit, false)], []), "assistant", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [], [new("outside", true)]), "assistant", default));
        view = await f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [], [new("worker", true)]), "assistant", default);
        Assert.Equal(8, Assert.Single(view.Requirements).Hours); Assert.Equal(f.Clock.Day, view.Requirements.Single().From);
        Assert.Contains(view.History, x => x.Actor == "assistant" && x.AfterRequirements.Any(r => r.Username == "worker"));
    }
    [Fact]
    public async Task AssistantInNestedAssistantTeamCanMaintainDivisionAndRosterListsEachPersonOnce()
    {
        var f = await Setup();
        var team = await f.Repo.SaveOrganizationUnitAsync(new(null, f.Company, f.Unit, "ASSIST", "技术助理", OrganizationUnitKind.Team, true, 2), default);
        var engineers = await f.Repo.SaveOrganizationUnitAsync(new(null, f.Company, f.Unit, "MECH", "机械组", OrganizationUnitKind.Department, true, 3), default);
        await f.Repo.SetOrganizationMembershipsAsync("assistant", [team.Id], team.Id, default);
        await f.Repo.SetOrganizationMembershipsAsync("worker", [engineers.Id, f.OtherUnit], engineers.Id, default);
        var view = await Enable(f, f.Unit);
        view = await f.Service.ReadManagementAsync("assistant", default);
        Assert.DoesNotContain(view.DepartmentGroups, x => x.Id == team.Id || x.Id == engineers.Id);
        var worker = Assert.Single(view.DepartmentGroups.SelectMany(x => x.People), x => x.Username == "worker");
        Assert.True(worker.CanEdit); Assert.Equal("机械组", worker.Department);
        await f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [], [new("worker", true)]), "assistant", default);
    }
    [Fact]
    public async Task AssistantCanSaveSeveralAuthorizedDepartmentsInOneTransaction()
    {
        var f = await Setup(); var view = await Enable(f, f.Unit, f.OtherUnit);
        view = await f.Service.SetGrantAsync(new("assistant", [f.OtherUnit], "兼管装配", view.RowVersion), "admin", default);
        view = await f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [], [new("worker", true), new("outside", true)]), "assistant", default);
        Assert.Equal(2, view.Requirements.Count); Assert.Equal(2, view.History.Last().AfterRequirements.Count);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion - 1, [], [new("worker", false)]), "assistant", default));
        Assert.Equal(2, (await f.Store.ReadCatalogAsync(f.Company, default)).Requirements.Count);
    }
    [Fact]
    public async Task ClosingDepartmentPreservesRosterAndPastStatisticsButStopsRemindersAndFutureRequirements()
    {
        var f = await Setup(); var settings = await Enroll(f, "worker");
        f.Clock.Day = new(2026, 10, 21);
        Assert.Equal(1, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
        settings = await f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [new(f.Unit, false)], []), "admin", default);
        Assert.True(Assert.Single(settings.Requirements).To is null);
        Assert.False(settings.DepartmentGroups.Single(x => x.Id == f.Unit).Participating);
        Assert.NotNull(Assert.Single(await f.Repo.ListUserNotificationsAsync("worker", 200, default)).ReadAt);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [], [new("assistant", true)]), "assistant", default));
        f.Clock.Day = new(2026, 10, 22);
        var report = await f.Service.ReadAsync(new(2026, 10, 20), f.Clock.Day, true, "admin", UserRole.Administrator, default);
        var compliance = Assert.Single(report.Compliance); Assert.Single(compliance.Days); Assert.Equal(20, compliance.Days.Single().Date.Day); Assert.False(compliance.ReminderEnabled);
        Assert.Equal(0, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
        settings = await f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [new(f.Unit, true)], []), "admin", default);
        Assert.True(settings.DepartmentGroups.Single(x => x.Id == f.Unit).People.Single(x => x.Username == "worker").Required);
        f.Clock.Day = new(2026, 10, 23);
        compliance = Assert.Single((await f.Service.ReadAsync(new(2026, 10, 20), f.Clock.Day, false, "worker", UserRole.Engineer, default)).Compliance);
        Assert.Equal(new[] { 20, 22 }, compliance.Days.Select(x => x.Date.Day));
    }
    [Fact]
    public async Task UncheckingPersonStopsRemindersWithoutErasingPastRequiredDays()
    {
        var f = await Setup(); var settings = await Enroll(f, "worker"); f.Clock.Day = new(2026, 10, 21);
        await f.Service.RemindAsync(new(["worker"]), "assistant", default);
        settings = await f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [], [new("worker", false)]), "assistant", default);
        Assert.True(settings.DepartmentGroups.Single(x => x.Id == f.Unit).Participating);
        Assert.Equal(new DateOnly(2026, 10, 20), Assert.Single(settings.Requirements).To);
        Assert.NotNull(Assert.Single(await f.Repo.ListUserNotificationsAsync("worker", 200, default)).ReadAt);
        Assert.Equal(0, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
    }
    [Fact]
    public async Task InvalidMixedSaveAndForeignCompanyDoNotApplyAnyPartialChange()
    {
        var f = await Setup(); var settings = await Enable(f, f.Unit, f.OtherUnit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [], [new("worker", true), new("outside", true)]), "assistant", default));
        Assert.Empty((await f.Store.ReadCatalogAsync(f.Company, default)).Requirements);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveGroupSettingsAsync(new(Guid.NewGuid(), settings.RowVersion, [new(f.Unit, false)], []), "admin", default));
        Assert.Equal(settings.RowVersion, (await f.Store.ReadCatalogAsync(f.Company, default)).RowVersion);
    }
    [Fact]
    public async Task FirstGroupedSaveKeepsLegacyHistoryAndMigratesExistingDepartmentParticipation()
    {
        var f = await Setup(); var date = f.Clock.Day;
        var legacy = new WorkHoursCatalog(f.Company, 0, [new(Guid.NewGuid(), "worker", date, null, 4)], [], [], []);
        Assert.True(await f.Store.SaveCatalogAsync(legacy, default));
        f.Clock.Day = date.AddDays(2);
        var settings = await f.Service.ReadManagementAsync("assistant", default);
        Assert.True(settings.DepartmentGroups.Single(x => x.Id == f.Unit).Participating);
        settings = await f.Service.SaveGroupSettingsAsync(new(f.Company, settings.RowVersion, [], [new("assistant", true)]), "assistant", default);
        var catalog = await f.Store.ReadCatalogAsync(f.Company, default); Assert.Equal(f.Clock.Day, catalog.DepartmentScopeFrom);
        Assert.Equal(4, catalog.Requirements.Single(x => x.Username == "worker").Hours);
        var old = Assert.Single((await f.Service.ReadAsync(date, date.AddDays(1), false, "worker", UserRole.Engineer, default)).Compliance);
        Assert.Equal(2, old.Days.Count); Assert.All(old.Days, x => Assert.Equal(4, x.Expected));
    }
}
