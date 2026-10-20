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
