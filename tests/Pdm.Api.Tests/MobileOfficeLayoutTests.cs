using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class MobileOfficeLayoutTests
{
    private const string Settings = "/api/settings/dingtalk/office-layout";
    private const string Published = "/api/integrations/dingtalk/office/layout";
    private static string Token(string username, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
    private static async Task<Guid[]> SeedAsync(PdmApiFactory factory)
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Where(item => item.IsActive).Select(item => item.Id).ToArray();
        foreach (var user in new[] { ("admin", UserRole.Administrator), ("employee", UserRole.Engineer) })
        {
            await ApiTestAuthorization.EnsureUserAsync(repository, new(Guid.NewGuid(), user.Item1, user.Item1, "unused", user.Item2, true), default);
            await repository.SetUserCompanyScopeAsync(user.Item1, companies[0], user.Item2 == UserRole.Administrator, user.Item2 == UserRole.Administrator ? companies : [], "admin", default);
        }
        return companies;
    }
    private static void Auth(HttpClient client, Guid company, string user = "admin", string role = "Administrator")
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(user, role));
        client.DefaultRequestHeaders.Remove("X-Company-Id"); client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
    }
    [Fact]
    public async Task DraftPublishRollbackPreserveHistoryAndCompanyIsolation()
    {
        using var factory = new PdmApiFactory(); var companies = await SeedAsync(factory);
        using var client = factory.CreateClient(); Auth(client, companies[0]);
        var initial = (await client.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!;
        Assert.Equal(0, initial.RowVersion); Assert.Equal(0, initial.PublishedVersion);
        var changed = initial.Draft with { Theme = "blue", Blocks = initial.Draft.Blocks.Reverse().ToArray(),
            Navigation = initial.Draft.Navigation.Select(item => item.Key == "tasks" ? item with { Label = "审批中心" } : item).ToArray() };
        var save = await client.PutAsJsonAsync(Settings + "/draft", new SaveMobileOfficeLayout(changed, 0));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var stillDefault = await client.GetFromJsonAsync<JsonElement>(Published);
        Assert.Equal("green", stillDefault.GetProperty("layout").GetProperty("theme").GetString());
        Assert.False(stillDefault.TryGetProperty("draft", out _)); Assert.False(stillDefault.TryGetProperty("history", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(Settings + "/draft", new SaveMobileOfficeLayout(initial.Draft, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Settings + "/publish", new PublishMobileOfficeLayout(0))).StatusCode);
        var publish = await client.PostAsJsonAsync(Settings + "/publish", new PublishMobileOfficeLayout(1));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var first = (await publish.Content.ReadFromJsonAsync<MobileOfficeLayoutView>())!;
        Assert.Equal(1, first.PublishedVersion); Assert.Equal(2, first.History.Count);
        var active = await client.GetFromJsonAsync<JsonElement>(Published);
        Assert.Equal("blue", active.GetProperty("layout").GetProperty("theme").GetString());
        Assert.Equal("projects", active.GetProperty("layout").GetProperty("blocks")[0].GetProperty("key").GetString());
        var rollback = await client.PostAsJsonAsync(Settings + "/rollback", new RollbackMobileOfficeLayout(0, 2));
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        var restored = (await rollback.Content.ReadFromJsonAsync<MobileOfficeLayoutView>())!;
        Assert.Equal(2, restored.PublishedVersion); Assert.Equal(3, restored.History.Count);
        Assert.Equal("rollback", restored.History[0].Action); Assert.Equal(0, restored.History[0].SourceVersion);
        Assert.Equal("blue", restored.Draft.Theme); Assert.Equal("blue", restored.History.Single(item => item.Version == 1).Layout.Theme);
        Assert.Equal("green", (await client.GetFromJsonAsync<JsonElement>(Published)).GetProperty("layout").GetProperty("theme").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Settings + "/rollback", new RollbackMobileOfficeLayout(99, 3))).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!.RowVersion);
        using var reopened = factory.CreateClient(); Auth(reopened, companies[0]);
        Assert.Equal(2, (await reopened.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!.PublishedVersion);
        if (companies.Length > 1)
        {
            Auth(client, companies[1]);
            var other = (await client.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!;
            Assert.Equal(0, other.RowVersion); Assert.Equal("green", other.Draft.Theme); Assert.Single(other.History);
        }
        var audit = await factory.Services.GetRequiredService<IPdmRepository>().ListAuditAsync("admin", UserRole.Administrator, 100, default);
        Assert.Contains(audit, item => item.Action == "settings.mobile-office.rollback");
    }
    [Fact]
    public async Task RejectsInvalidLayoutAndEnforcesManagementAndReadPermissions()
    {
        using var factory = new PdmApiFactory(); var companies = await SeedAsync(factory);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Published)).StatusCode);
        Auth(client, companies[0], "employee", "Engineer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Published)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(Settings + "/draft", new SaveMobileOfficeLayout(MobileOfficeLayout.Default(), 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Settings + "/publish", new PublishMobileOfficeLayout(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Settings + "/rollback", new RollbackMobileOfficeLayout(0, 0))).StatusCode);
        Auth(client, companies[0]);
        var layout = MobileOfficeLayout.Default();
        var invalid = new[] {
            layout with { SchemaVersion = 2 }, layout with { Theme = "url(javascript:alert(1))" },
            layout with { Blocks = [] }, layout with { Blocks = layout.Blocks.Select(item => item with { Enabled = false }).ToArray() },
            layout with { Blocks = layout.Blocks.Select(item => item with { Width = 3 }).ToArray() },
            layout with { Blocks = layout.Blocks.Select(item => item with { Limit = 0 }).ToArray() },
            layout with { Blocks = [layout.Blocks[0], layout.Blocks[0], ..layout.Blocks.Skip(2)] },
            layout with { Navigation = layout.Navigation.Select(item => item with { Icon = "unknown" }).ToArray() },
            layout with { Navigation = layout.Navigation.Select(item => item.Key == "home" ? item with { Enabled = false } : item).ToArray() },
            layout with { DefaultPage = "projects", Navigation = layout.Navigation.Select(item => item.Key == "projects" ? item with { Enabled = false } : item).ToArray() },
        };
        foreach (var candidate in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Settings + "/draft", new SaveMobileOfficeLayout(candidate, 0))).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!.RowVersion);
    }
    [Fact]
    public async Task ConcurrentPublishesActivateExactlyOneNewVersion()
    {
        using var factory = new PdmApiFactory(); var companies = await SeedAsync(factory);
        using var first = factory.CreateClient(); using var second = factory.CreateClient();
        Auth(first, companies[0]); Auth(second, companies[0]);
        var responses = await Task.WhenAll(first.PostAsJsonAsync(Settings + "/publish", new PublishMobileOfficeLayout(0)),
            second.PostAsJsonAsync(Settings + "/publish", new PublishMobileOfficeLayout(0)));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        var saved = (await first.GetFromJsonAsync<MobileOfficeLayoutView>(Settings))!;
        Assert.Equal(1, saved.RowVersion); Assert.Equal(1, saved.PublishedVersion); Assert.Equal(2, saved.History.Count);
    }
}
