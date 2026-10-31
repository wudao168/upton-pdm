using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class WorkHoursApiTests
{
    [Fact]
    public async Task CalendarMoveRequiresAuthenticationMergesAtomicallyAndRejectsStaleReplay()
    {
        using var factory=new PdmApiFactory();using var client=factory.CreateClient();
        using var serviceScope=factory.Services.CreateScope();
        var repo=serviceScope.ServiceProvider.GetRequiredService<IPdmRepository>();var service=serviceScope.ServiceProvider.GetRequiredService<WorkHoursService>();
        var company=(await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        var day=DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);var source=day.AddDays(-2);
        await repo.CreateUserAsync(new(Guid.NewGuid(),"calendar-worker","工时操作测试","unused",UserRole.Engineer,true,CompanyId:company),default);
        var root=await repo.CreateNumberedProjectAsync(new(company,"P",2,Guid.Parse("c0046500-0000-0000-0000-000000000001"),"日历剪切测试",null,source,1,"calendar-worker","D:/test","D:/release"),default);
        await repo.SetMainProjectStaffingAsync(root.Id,new("calendar-worker",[],["calendar-worker"]),"admin",default);
        var read=await service.ReadAsync(source,day,false,"calendar-worker",UserRole.Engineer,default);
        await service.SaveAsync(new(source,source,[new(source,"厂内",8,root.Id)],read.Sheets.Where(x=>x.Month==source.ToString("yyyy-MM")).Select(x=>new WorkHoursVersion(x.Month,x.RowVersion)).ToArray()),"calendar-worker",UserRole.Engineer,default);
        read=await service.ReadAsync(source,day,false,"calendar-worker",UserRole.Engineer,default);
        var move=new MoveWorkHoursDay(company,source,day,read.Sheets.Single(x=>x.Month==source.ToString("yyyy-MM")).RowVersion,read.Sheets.Single(x=>x.Month==day.ToString("yyyy-MM")).RowVersion);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/work-hours/move-day",move)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Company-Id",company.ToString());Authenticate(client,"calendar-worker",UserRole.Engineer);
        var response=await client.PostAsJsonAsync("/api/work-hours/move-day",move);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<WorkHoursView>())!;
        Assert.Equal(8,result.Sheets.SelectMany(x=>x.Entries).Where(x=>x.Date==day).Sum(x=>x.Hours));
        Assert.DoesNotContain(result.Sheets.SelectMany(x=>x.Entries),x=>x.Date==source);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/work-hours/move-day",move)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/work-hours/move-day",move with{CompanyId=Guid.NewGuid()})).StatusCode);
    }

    [Fact]
    public async Task ManualReminderHttpRepeatsAfterAutomaticReminderAndReturnsRecipientResults()
    {
        using var factory=new PdmApiFactory();using var client=factory.CreateClient();
        var repo=factory.Services.GetRequiredService<IPdmRepository>();var store=factory.Services.GetRequiredService<IWorkHoursStore>();
        var company=(await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        var day=DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var calendar=factory.Services.GetRequiredService<IWorkHoursCalendar>();var from=day.AddDays(-1);
        while(!calendar.IsWorkingDay(from))from=from.AddDays(-1);
        foreach(var name in new[]{"remind-assistant","remind-worker"})
            await repo.CreateUserAsync(new(Guid.NewGuid(),name,name,"unused",UserRole.Engineer,true,RoleCode:name.EndsWith("assistant")?"TechnicalAssistant":null,CompanyId:company),default);
        var unit=await repo.SaveOrganizationUnitAsync(new(null,company,null,"WH-REMIND","提醒测试部",OrganizationUnitKind.Department,true,1),default);
        await repo.SetOrganizationMembershipsAsync("remind-worker",[unit.Id],unit.Id,default);
        await repo.SetOrganizationMembershipsAsync("remind-assistant",[unit.Id],unit.Id,default);
        await store.StartDateAsync(company,from,default);
        Assert.True(await store.SaveCatalogAsync(new(company,0,[new(Guid.NewGuid(),"remind-worker",from,null,8)],[],[],[]),default));
        client.DefaultRequestHeaders.Add("X-Company-Id",company.ToString());Authenticate(client,"remind-assistant",UserRole.Engineer);
        using var scope=factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkHoursService>().SendDailyRemindersAsync(default);
        for(var i=0;i<2;i++)
        {
            var response=await client.PostAsJsonAsync("/api/work-hours/remind",new RemindWorkHours(["remind-worker"]));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var result=(await response.Content.ReadFromJsonAsync<WorkHoursReminderResult>())!;
            Assert.Equal(1,result.Sent);Assert.True(Assert.Single(result.Results).Sent);
        }
        var notices=await repo.ListUserNotificationsAsync("remind-worker",200,default);
        Assert.Equal(2,notices.Count(n=>n.SourceKey.Contains(":manual:")));
        Assert.All(notices.Where(n=>n.SourceKey.Contains(":manual:")),n=>Assert.Null(n.ReadAt));
    }

    [Fact]
    public async Task SettingsAndCorrectionHttpEnforceScopeAndRetainVersionsAndReasons()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
        foreach (var name in new[] { "wh-admin", "wh-assistant", "wh-worker", "wh-outsider" })
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "wh-admin" ? UserRole.Administrator : UserRole.Engineer, true,
                RoleCode: name == "wh-assistant" ? "TechnicalAssistant" : null, CompanyId: company), default);
        var unit = await repo.SaveOrganizationUnitAsync(new(null, company, null, "WH-API", "工时接口测试部", OrganizationUnitKind.Department, true, 1), default);
        await repo.SetOrganizationMembershipsAsync("wh-worker", [unit.Id], unit.Id, default);
        await repo.SetOrganizationMembershipsAsync("wh-assistant", [unit.Id], unit.Id, default);
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "更正测试", null, day, 1, "wh-worker", "D:/test", "D:/release"), default);
        await repo.SetMainProjectStaffingAsync(project.Id, new("wh-worker", [], ["wh-worker"]), "wh-admin", default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Authenticate(client, "wh-admin", UserRole.Administrator);
        var initialSettings = (await client.GetFromJsonAsync<WorkHoursManagementView>("/api/work-hours/settings"))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/work-hours/settings", new SaveWorkHoursGroupSettings(company, initialSettings.RowVersion, [new(unit.Id, true)], []))).StatusCode);
        Authenticate(client, "wh-worker", UserRole.Engineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/work-hours/settings")).StatusCode);
        var ownReport = (await client.GetFromJsonAsync<WorkHoursView>($"/api/work-hours?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}&report=true"))!;
        Assert.Equal(unit.Id, ownReport.DefaultDepartmentId);
        Assert.Equal(unit.Id, Assert.Single(ownReport.People).DepartmentId);
        Assert.Contains(ownReport.OrganizationUnits, x => x.Id == unit.Id && x.ParentUnitId == unit.ParentUnitId);
        Authenticate(client, "wh-assistant", UserRole.Engineer);
        var settings = (await client.GetFromJsonAsync<WorkHoursManagementView>("/api/work-hours/settings"))!;
        Assert.DoesNotContain(settings.People, x => x.Username == "wh-outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/work-hours/settings", new SaveWorkHoursGroupSettings(company, settings.RowVersion, [new(unit.Id,false)], []))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/work-hours/settings", new SaveWorkHoursGroupSettings(Guid.NewGuid(), settings.RowVersion, [], [new("wh-worker",true)]))).StatusCode);
        var requirement = new SetWorkHoursRequirement(["wh-worker"], day, null, 8, true, "开始工时填报", settings.RowVersion);
        var response = await client.PutAsJsonAsync("/api/work-hours/settings/requirements", requirement);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        settings = (await response.Content.ReadFromJsonAsync<WorkHoursManagementView>())!;
        Assert.Single(settings.Requirements); Assert.Equal("wh-assistant", settings.History.Last().Actor);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/work-hours/settings/requirements", requirement)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/work-hours/settings/requirements", requirement with { Usernames = ["wh-outsider"], RowVersion = settings.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/work-hours/settings/grants", new SetWorkHoursGrant("wh-assistant", [], "越权", settings.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/work-hours/correction?username=wh-outsider&month={day:yyyy-MM}")).StatusCode);
        var correction = (await client.GetFromJsonAsync<WorkHoursCorrectionView>($"/api/work-hours/correction?username=wh-worker&month={day:yyyy-MM}"))!;
        var command = new CorrectWorkHours("wh-worker", new(day, day, [new(day, "厂外", 7.5m, project.Id)], [new(day.ToString("yyyy-MM"), correction.Sheet.RowVersion)]), "核对后代更正");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/work-hours/correction", command with { Reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/work-hours/correction", command)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/work-hours/correction", command)).StatusCode);
        Authenticate(client, "wh-worker", UserRole.Engineer);
        var history = (await client.GetFromJsonAsync<WorkHourRevision[]>($"/api/work-hours/history?month={day:yyyy-MM}"))!;
        Assert.Equal("核对后代更正", Assert.Single(history).Reason);
        Assert.Equal("wh-assistant", history.Single().Actor);
        Assert.Equal(7.5m, Assert.Single(history.Single().After).Hours);
    }
    private static void Authenticate(HttpClient client, string username, UserRole role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role.ToString())],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }
    [Fact]
    public async Task HttpSavesOwnHoursRejectsInvalidAndStaleWritesAndPreservesHistory()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var path = $"/api/work-hours?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetProjectNumberingOptionsAsync(default)).Organizations.First();
        await repo.CreateUserAsync(new(Guid.NewGuid(), "hours-admin", "工时管理员", "unused", UserRole.Administrator, true) { CompanyId = company.Id }, default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "hours-worker", "普通员工", "unused", UserRole.Engineer, true) { CompanyId = company.Id }, default);
        await repo.CreateNumberedProjectAsync(new(company.Id, "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "HTTP工时测试", null, day, 1, "hours-admin", "D:/test", "D:/release"), default);
        Authenticate(client, "hours-admin", UserRole.Administrator);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.Id.ToString());
        var view = (await client.GetFromJsonAsync<WorkHoursView>(path))!;
        var project = view.Projects.First();
        var command = new SaveWorkHours(day, day, [new(day, "厂外", 7.5m, project.Id)], view.Sheets.Select(x => new WorkHoursVersion(x.Month, x.RowVersion)).ToArray());
        var savedResponse = await client.PutAsJsonAsync("/api/work-hours", command);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = (await savedResponse.Content.ReadFromJsonAsync<WorkHoursView>())!;
        Assert.Equal(7.5m, Assert.Single(saved.Sheets.SelectMany(x => x.Entries)).Hours);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/work-hours", command)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/work-hours", command with { Entries = [new(day, "厂内", 0.25m, project.Id)] })).StatusCode);
        var history = (await client.GetFromJsonAsync<WorkHourRevision[]>($"/api/work-hours/history?month={day:yyyy-MM}"))!;
        Assert.Equal(7.5m, Assert.Single(Assert.Single(history).After).Hours);
        Authenticate(client, "hours-worker", UserRole.Engineer);
        var other = (await client.GetFromJsonAsync<WorkHoursView>(path + "&report=true"))!;
        Assert.Single(other.People);
        Assert.Empty(other.Sheets.SelectMany(x => x.Entries));
        Assert.Empty((await client.GetFromJsonAsync<WorkHourRevision[]>($"/api/work-hours/history?month={day:yyyy-MM}"))!);
        Authenticate(client, "hours-admin", UserRole.Administrator);
        var report = (await client.GetFromJsonAsync<WorkHoursView>(path + "&report=true"))!;
        Assert.Contains(report.People, x => x.Username == "hours-worker");
    }
}
