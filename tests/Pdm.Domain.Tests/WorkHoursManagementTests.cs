using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class WorkHoursManagementTests
{
    private sealed class Clock : TimeProvider
    {
        public DateOnly Day { get; set; } = new(2026, 10, 20);
        public override DateTimeOffset GetUtcNow() => new(Day.ToDateTime(new(2, 0)), TimeSpan.Zero);
    }
    private sealed record Fixture(InMemoryPdmRepository Repo, InMemoryWorkHoursStore Store, WorkHoursService Service, Clock Clock, Guid Company, Guid Unit, Guid OtherUnit, Project Project);
    private static async Task<Fixture> Setup()
    {
        TenantContext.Clear();
        var clock = new Clock(); var repo = new InMemoryPdmRepository(clock); var store = new InMemoryWorkHoursStore();
        var company = (await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        foreach (var name in new[] { "admin", "assistant", "worker", "outside", "manager" })
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "admin" ? UserRole.Administrator : name == "manager" ? UserRole.BusinessUnitManager : UserRole.Engineer, true,
                RoleCode: name == "assistant" ? "TechnicalAssistant" : null, CompanyId: company), default);
        var unit = await repo.SaveOrganizationUnitAsync(new(null, company, null, "WH-A", "设计部", OrganizationUnitKind.BusinessDivision, true, 1), default);
        var other = await repo.SaveOrganizationUnitAsync(new(null, company, null, "WH-B", "装配部", OrganizationUnitKind.Department, true, 2), default);
        foreach (var name in new[] { "worker", "assistant", "manager" }) await repo.SetOrganizationMembershipsAsync(name, [unit.Id], unit.Id, default);
        await repo.SetOrganizationMembershipsAsync("outside", [other.Id], other.Id, default);
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "工时管理测试", null, clock.Day, 1, "worker", "D:/test", "D:/release"), default);
        await repo.SetMainProjectStaffingAsync(project.Id, new("worker", [], ["worker"]), "admin", default);
        await store.StartDateAsync(company, clock.Day, default);
        return new(repo, store, new(repo, store, clock), clock, company, unit.Id, other.Id, project);
    }
    private static async Task<WorkHoursManagementView> Enroll(Fixture f, params string[] names)
    {
        var view = await f.Service.ReadManagementAsync("admin", default);
        if (!view.DepartmentGroups.Single(x => x.Id == f.Unit).Participating)
            view = await f.Service.SaveGroupSettingsAsync(new(f.Company, view.RowVersion, [new(f.Unit, true)], []), "admin", default);
        return await f.Service.SetRequirementAsync(new(names, f.Clock.Day, null, 8, true, "开始填报", view.RowVersion), "assistant", default);
    }
    private static async Task Fill(Fixture f, DateOnly day, decimal hours)
    {
        var own = await f.Service.ReadAsync(day, day, false, "worker", UserRole.Engineer, default);
        await f.Service.SaveAsync(new(day, day, [new(day, "厂内", hours, f.Project.Id)], own.Sheets.Select(x => new WorkHoursVersion(x.Month, x.RowVersion)).ToArray()), "worker", UserRole.Engineer, default);
    }
    [Fact]
    public async Task EmptyRosterDoesNotCountAccountsAndVoluntaryRecordsRemain()
    {
        var f = await Setup(); await Fill(f, f.Clock.Day, 4);
        f.Clock.Day = f.Clock.Day.AddDays(2);
        var report = await f.Service.ReadAsync(new(2026, 10, 1), f.Clock.Day, true, "admin", UserRole.Administrator, default);
        Assert.Empty(report.Compliance); Assert.Equal(4, Assert.Single(report.Sheets.SelectMany(x => x.Entries)).Hours);
        Assert.Empty((await f.Service.ReadManagementAsync("admin", default)).Requirements);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SetRequirementAsync(new(["worker"], f.Clock.Day.AddDays(-1), null, 8, true, "不能追溯加入", 0), "admin", default));
    }
    [Fact]
    public async Task CompletenessUsesDailyAllProjectHoursAndNeverOffsetsAnotherDay()
    {
        var f = await Setup(); await Enroll(f, "worker");
        f.Clock.Day = new(2026, 10, 24);
        await Fill(f, new(2026, 10, 20), 12); await Fill(f, new(2026, 10, 21), 4); await Fill(f, new(2026, 10, 23), 8);
        var report = await f.Service.ReadAsync(new(2026, 10, 19), new(2026, 10, 31), true, "admin", UserRole.Administrator, default);
        var days = Assert.Single(report.Compliance).Days;
        Assert.Equal(4, days.Count); Assert.Equal(2, days.Count(x => x.Status == "完整"));
        Assert.Equal("填写不完整", days.Single(x => x.Date.Day == 21).Status);
        Assert.Equal("未填写", days.Single(x => x.Date.Day == 22).Status);
        Assert.Equal(12, days.Sum(x => Math.Max(0, x.Expected - x.Actual)));
        Assert.DoesNotContain(days, x => x.Date >= f.Clock.Day);
    }
    [Fact]
    public async Task MutingDoesNotChangeStatisticsExemptionDoesAndBothAreAudited()
    {
        var f = await Setup(); var settings = await Enroll(f, "worker"); f.Clock.Day = new(2026, 10, 22);
        await Fill(f, new(2026, 10, 20), 4);
        settings = await f.Service.SetExceptionAsync(new("worker", new(2026, 10, 20), new(2026, 10, 22), "mute", null, "特殊任务暂停通知", settings.RowVersion), "assistant", default);
        var own = await f.Service.ReadAsync(new(2026, 10, 20), f.Clock.Day, false, "worker", UserRole.Engineer, default);
        var day = Assert.Single(own.Compliance).Days.Single(x => x.Date.Day == 20);
        Assert.True(day.Muted); Assert.Equal(8, day.Expected); Assert.Equal("填写不完整", day.Status);
        settings = await f.Service.SetExceptionAsync(new("worker", new(2026, 10, 21), new(2026, 10, 21), "target", 0, "请假免填", settings.RowVersion), "assistant", default);
        own = await f.Service.ReadAsync(new(2026, 10, 20), f.Clock.Day, false, "worker", UserRole.Engineer, default);
        Assert.Equal("已豁免", Assert.Single(own.Compliance).Days.Single(x => x.Date.Day == 21).Status);
        var reminders = await f.Service.RemindAsync(new(["worker"]), "assistant", default); Assert.Equal(0, reminders.Sent);
        settings = await f.Service.RemoveExceptionAsync(new(settings.Exceptions.Single(x => x.Kind == "mute").Id, "恢复提醒", settings.RowVersion), "assistant", default);
        Assert.Equal(1, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
        Assert.Equal(5, settings.History.Count);
        Assert.Equal(4, Assert.Single((await f.Store.ReadAsync(f.Company, "2026-10", "2026-10", default)).SelectMany(x => x.Entries)).Hours);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SetExceptionAsync(new("worker", new(2026, 10, 20), new(2026, 10, 20), "target", 4, "旧设置不能覆盖", 0), "assistant", default));
    }
    [Fact]
    public async Task ManagersAndAssistantsAreConfinedToAuthorizedUnitsAndCannotGrantThemselves()
    {
        var f = await Setup();
        var view = await f.Service.ReadManagementAsync("assistant", default);
        Assert.Contains(view.People, x => x.Username == "worker"); Assert.DoesNotContain(view.People, x => x.Username == "outside"); Assert.False(view.CanAuthorize);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadManagementAsync("worker", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SetRequirementAsync(new(["outside"], f.Clock.Day, null, 8, true, "跨部门", view.RowVersion), "assistant", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SetGrantAsync(new("assistant", [f.OtherUnit], "自行扩权", view.RowVersion), "assistant", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadCorrectionAsync("outside", "2026-10", "assistant", default));
        var admin = await f.Service.ReadManagementAsync("admin", default);
        await f.Service.SetGrantAsync(new("assistant", [f.OtherUnit], "兼管装配部", admin.RowVersion), "admin", default);
        Assert.Contains((await f.Service.ReadManagementAsync("assistant", default)).People, x => x.Username == "outside");
        Assert.Contains((await f.Service.ReadAsync(f.Clock.Day, f.Clock.Day, true, "assistant", UserRole.Engineer, default)).People, x => x.Username == "outside");
        Assert.Contains((await f.Service.ReadManagementAsync("manager", default)).People, x => x.Username == "worker");
    }
    [Fact]
    public async Task CorrectionRequiresReasonPreservesHistoryAndRejectsStaleVersions()
    {
        var f = await Setup(); await Enroll(f, "worker"); await Fill(f, f.Clock.Day, 4);
        var correction = await f.Service.ReadCorrectionAsync("worker", "2026-10", "assistant", default);
        var input = new CorrectWorkHours("worker", new(f.Clock.Day, f.Clock.Day, [new(f.Clock.Day, "厂内", 8, f.Project.Id)], [new("2026-10", correction.Sheet.RowVersion)]), "补正项目实际投入");
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.CorrectAsync(input with { Reason = "" }, "assistant", UserRole.Engineer, default));
        await f.Service.CorrectAsync(input, "assistant", UserRole.Engineer, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.CorrectAsync(input, "assistant", UserRole.Engineer, default));
        var history = await f.Service.HistoryAsync("2026-10", "worker", default);
        Assert.Equal(2, history.Count); Assert.Equal("assistant", history.Last().Actor); Assert.Equal(input.Reason, history.Last().Reason);
        Assert.Equal(4, Assert.Single(history.Last().Before).Hours); Assert.Equal(8, Assert.Single(history.Last().After).Hours);
        Assert.Contains(await f.Repo.ListUserNotificationsAsync("worker", 200, default), x => x.Category == "work-hours-correction");
        Assert.Empty(await f.Service.HistoryAsync("2026-10", "outside", default));
    }
    [Fact]
    public async Task DailyNotificationsAreMergedDeduplicatedAndResolvedAfterFilling()
    {
        var f = await Setup(); await Enroll(f, "worker"); f.Clock.Day = new(2026, 10, 22);
        await f.Service.SendDailyRemindersAsync(default); await f.Service.SendDailyRemindersAsync(default);
        Assert.Equal(1, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
        var notices = await f.Repo.ListUserNotificationsAsync("worker", 200, default);
        Assert.Equal(2, notices.Count);
        var notice = Assert.Single(notices, n => !n.SourceKey.Contains(":manual:"));
        Assert.Contains("2天未填写", notice.Content); Assert.Null(notice.ReadAt);
        await Fill(f, new(2026, 10, 20), 8);
        Assert.All(await f.Repo.ListUserNotificationsAsync("worker", 200, default), n => Assert.Null(n.ReadAt));
        await Fill(f, new(2026, 10, 21), 8);
        Assert.All(await f.Repo.ListUserNotificationsAsync("worker", 200, default), n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task ManualRemindersCanRepeatAfterReadAndDoNotConsumeAutomaticDailyLimit()
    {
        var f = await Setup(); await Enroll(f, "worker"); f.Clock.Day = new(2026,10,21);
        var first = await f.Service.RemindAsync(new(["worker","worker"]),"assistant",default);
        Assert.Equal(1,first.Sent); Assert.Equal(0,first.Skipped);
        var notice = Assert.Single(await f.Repo.ListUserNotificationsAsync("worker",200,default));
        await f.Repo.MarkUserNotificationReadAsync(notice.Id,"worker",f.Clock.GetUtcNow(),default);
        Assert.Equal(1,(await f.Service.RemindAsync(new(["worker"]),"assistant",default)).Sent);
        await f.Service.SendDailyRemindersAsync(default); await f.Service.SendDailyRemindersAsync(default);
        var notifications = await f.Repo.ListUserNotificationsAsync("worker",200,default);
        Assert.Equal(3,notifications.Count);
        Assert.Equal(2,notifications.Count(n=>n.SourceKey.Contains(":manual:")));
        Assert.Single(notifications,n=>!n.SourceKey.Contains(":manual:"));
        Assert.Equal(2,notifications.Count(n=>n.ReadAt is null));
        Assert.All(notifications.Where(n=>n.SourceKey.Contains(":manual:")),n=>Assert.Contains("手动提醒",n.Content));
        await Fill(f,new(2026,10,20),8);
        Assert.All(await f.Repo.ListUserNotificationsAsync("worker",200,default),n=>Assert.NotNull(n.ReadAt));
        var complete=await f.Service.RemindAsync(new(["worker"]),"assistant",default);
        Assert.Equal(0,complete.Sent);Assert.Equal(1,complete.Skipped);
        Assert.Contains("已无未填写",Assert.Single(complete.Results).Reason);
    }

    [Fact]
    public async Task ManualReminderReportsIndividualSkipReasonsAndRespectsMutedDaysAndScope()
    {
        var f=await Setup();var settings=await Enroll(f,"worker");f.Clock.Day=new(2026,10,21);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.RemindAsync(new(["outside"]),"assistant",default));
        var mixed=await f.Service.RemindAsync(new(["worker","outside"]),"admin",default);
        Assert.Equal(1,mixed.Sent);Assert.Equal(1,mixed.Skipped);
        Assert.Contains("应填名单",mixed.Results.Single(r=>r.Username=="outside").Reason);
        await f.Service.SetExceptionAsync(new("worker",new(2026,10,20),f.Clock.Day,"mute",null,"临时暂停",settings.RowVersion),"assistant",default);
        var muted=await f.Service.RemindAsync(new(["worker"]),"assistant",default);
        Assert.Equal(0,muted.Sent);Assert.Contains("暂停提醒",Assert.Single(muted.Results).Reason);
        Assert.Single(await f.Repo.ListUserNotificationsAsync("worker",200,default));
    }
    [Fact]
    public async Task ExpiredMuteRestoresRemindersForStillIncompleteHistoricalDates()
    {
        var f = await Setup(); var settings = await Enroll(f, "worker"); f.Clock.Day = new(2026, 10, 21);
        await f.Service.SetExceptionAsync(new("worker", new(2026, 10, 20), f.Clock.Day, "mute", null, "暂停至今天", settings.RowVersion), "assistant", default);
        Assert.Equal(0, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
        f.Clock.Day = f.Clock.Day.AddDays(1);
        var view = await f.Service.ReadAsync(new(2026, 10, 20), f.Clock.Day, false, "worker", UserRole.Engineer, default);
        Assert.All(Assert.Single(view.Compliance).Days, x => Assert.False(x.Muted));
        Assert.Equal(1, (await f.Service.RemindAsync(new(["worker"]), "assistant", default)).Sent);
    }
    [Fact]
    public async Task EndingRosterRetainsPriorPeriodsAndCompanyCatalogsAreIsolated()
    {
        var f = await Setup(); await Enroll(f, "worker"); f.Clock.Day = new(2026, 10, 22);
        var settings = await f.Service.ReadManagementAsync("assistant", default);
        settings = await f.Service.SetRequirementAsync(new(["worker"], f.Clock.Day, null, 8, false, "转岗结束填报", settings.RowVersion), "assistant", default);
        Assert.Equal(new DateOnly(2026, 10, 21), Assert.Single(settings.Requirements).To);
        f.Clock.Day = new(2026, 10, 24);
        Assert.Equal(2, Assert.Single((await f.Service.ReadAsync(new(2026, 10, 1), f.Clock.Day, true, "admin", UserRole.Administrator, default)).Compliance).Days.Count);
        var empty = await f.Store.ReadCatalogAsync(Guid.NewGuid(), default); Assert.Empty(empty.Requirements);
        var original = await f.Store.ReadCatalogAsync(f.Company, default);
        var saves = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Store.SaveCatalogAsync(original, default)));
        Assert.Single(saves, x => x);
    }
    [Fact]
    public void SharedCalendarHonorsHolidaysAndSundayOnlyDefault()
    {
        var calendar = new WorkHoursCalendar();
        Assert.False(calendar.IsWorkingDay(new(2026, 10, 1)));
        Assert.True(calendar.IsWorkingDay(new(2026, 10, 17)));
        Assert.False(calendar.IsWorkingDay(new(2026, 10, 18)));
        Assert.True(calendar.IsWorkingDay(new(2026, 10, 10)));
    }
}
