using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed partial class AiAssistantApiTests
{
    private sealed class FakeWeb : IAiWebClient
    {
        public int Searches, Fetches;
        public bool Fail;
        public Func<Task>? BeforeReturn;
        public async Task<AiWebPage[]> SearchAsync(AiProvider provider, string query, CancellationToken ct) {
            Searches++;
            if (BeforeReturn is not null) await BeforeReturn();
            if (Fail) throw new PdmRuleException("DeepSeek账户余额不足，请充值后重试。");
            return [new("官方公开来源", "https://1.1.1.1/", "公开资料摘要", "2026-11-12")];
        }
        public Task<AiWebPage> FetchAsync(AiProvider provider, string url, CancellationToken ct) {
            Fetches++; return Task.FromResult(new AiWebPage("公开网页", url, "网页正文"));
        }
    }
    private static async Task ConfigureWeb(Fixture f, string modelUrl, AiWebSettings? web = null)
    {
        await f.Configure(modelUrl);
        var config = f.Configuration(modelUrl) with { Revision = 1 };
        var provider = new AiProviderInput(Guid.NewGuid(), "联网服务", "https://api.deepseek.com", "cloud", "deepseek", "mock-key", false, true, [new("web-model", "联网模型")]);
        config = config with { Providers = [..config.Providers, provider], Web = (web ?? new()) with { Enabled = true, ProviderId = provider.Id } };
        var result = await f.Client.PutAsJsonAsync("/api/ai/settings", config);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WebModeRequiresExplicitPermissionsAndConfigurationAndIsCompanyScoped()
    {
        var web = new FakeWeb(); await using var f = await Fixture.Create(web); await using var upstream = await MockModel.Start();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings); await ConfigureWeb(f, upstream.Url);
        async Task<HttpResponseMessage> Create() => await f.Client.PostAsJsonAsync("/api/ai/conversations", new AiCreateInput(f.Model, Web: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await Create()).StatusCode);
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web);
        Assert.Equal(HttpStatusCode.Forbidden, (await Create()).StatusCode);
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web, AiPermissions.Cloud);
        Assert.Equal(HttpStatusCode.OK, (await Create()).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync("/api/ai/conversations", new AiCreateInput(f.Model, true, true))).StatusCode);
        var store = (IAiStore)f.Repo; var settings = await store.SettingsAsync(f.Company, default);
        await store.SaveSettingsAsync(f.Company, settings with { Web = settings.Web! with { Enabled = false } }, default);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create()).StatusCode);
        f.CompanyHeader(f.Other); Assert.Equal(HttpStatusCode.Unauthorized, (await Create()).StatusCode);
        Assert.Equal(0, web.Searches);
    }

    [Fact]
    public async Task WebTurnOmitsAllPriorBusinessContentAndOnlyExposesPublicToolsAndSources()
    {
        var web = new FakeWeb(); await using var f = await Fixture.Create(web); await using var upstream = await MockModel.Start();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web, AiPermissions.Cloud, AiPermissions.Business);
        await ConfigureWeb(f, upstream.Url); var c = await f.Conversation(); var store = (IAiStore)f.Repo;
        await store.SaveAsync(c with { Messages = [new(Guid.NewGuid(), "user", "内部项目机密编号", DateTimeOffset.UtcNow), new(Guid.NewGuid(), "assistant", "内部BOM细节仅供测试", DateTimeOffset.UtcNow, Sources: [new("内部来源", null, [], "project")])] }, default);
        var result = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "搜索公开资料", f.Model, false, Web: true));
        Assert.DoesNotContain("event: error", await result.Content.ReadAsStringAsync()); Assert.Equal(1, web.Searches);
        foreach (var request in upstream.Requests) {
            Assert.DoesNotContain("内部", request.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() != "system").Select(m => m.GetRawText()).Aggregate("", (a, b) => a + b));
            Assert.Equal(["SearchWeb", "ReadWebPage"], request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()));
        }
        var saved = await store.FindAsync(f.Company, f.UserId, c.Id, default);
        var source = Assert.Single(saved!.Messages[^1].Sources!); Assert.Equal("web", source.Kind); Assert.Equal("https://1.1.1.1/", source.Url);
        Assert.Equal(new AiWebUsage(1, 1), await store.WebUsageAsync(f.Company, DateTimeOffset.UtcNow, default));
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Cloud, AiPermissions.Business);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task RevokingWebPermissionWhileToolRunsDiscardsResultAndStopsAnswer()
    {
        var web = new FakeWeb(); await using var f = await Fixture.Create(web); await using var upstream = await MockModel.Start();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web, AiPermissions.Cloud); await ConfigureWeb(f, upstream.Url);
        web.BeforeReturn = () => f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Cloud);
        var c = await f.Conversation();
        var result = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "搜索公开资料", f.Model, Web: true));
        Assert.Contains("event: error", await result.Content.ReadAsStringAsync());
        var saved = await ((IAiStore)f.Repo).FindAsync(f.Company, f.UserId, c.Id, default);
        Assert.Empty(saved!.Messages[^1].Text); Assert.Empty(saved.Messages[^1].Sources!); Assert.Equal("failed", saved.Messages[^1].Status);
    }

    [Theory]
    [InlineData("https://1.1.1.1/", "读取 https://1.1.1.1/", 1)]
    [InlineData("https://1.1.1.1/", "搜索公开资料", 0)]
    [InlineData("http://192.168.2.8/", "读取 http://192.168.2.8/", 0)]
    public async Task WebFetchOnlyAcceptsUserOrSearchPublicUrls(string url, string question, int calls)
    {
        var web = new FakeWeb(); await using var f = await Fixture.Create(web);
        await using var upstream = await MockModel.Start(requestedTool: "ReadWebPage", arguments: JsonSerializer.Serialize(new { url }));
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web, AiPermissions.Cloud); await ConfigureWeb(f, upstream.Url); var c = await f.Conversation();
        var result = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), question, f.Model, Web: true));
        Assert.DoesNotContain("event: error", await result.Content.ReadAsStringAsync()); Assert.Equal(calls, web.Fetches);
        Assert.Equal(calls, (await ((IAiStore)f.Repo).WebUsageAsync(f.Company, DateTimeOffset.UtcNow, default)).Today);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebTurnAndCompanyCapsIncludeFailedAttempts(bool fail)
    {
        var web = new FakeWeb { Fail = fail }; await using var f = await Fixture.Create(web); await using var upstream = await MockModel.Start(toolRounds: 3);
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Web, AiPermissions.Cloud);
        await ConfigureWeb(f, upstream.Url, new(DailyLimit: 1, MonthlyLimit: 2, PerTurnLimit: 2));
        var c = await f.Conversation();
        var result = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "搜索公开资料", f.Model, Web: true));
        Assert.DoesNotContain("event: error", await result.Content.ReadAsStringAsync()); Assert.Equal(1, web.Searches);
        var toolText = upstream.Requests.Last().GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").Select(m => m.GetProperty("content").GetString()).ToArray();
        Assert.Contains(toolText, t => t!.Contains("当前公司今日或本月")); Assert.Contains(toolText, t => t!.Contains("本轮联网次数"));
        if (fail) Assert.Contains(toolText, t => t!.Contains("余额不足"));
        Assert.Equal(new AiWebUsage(1, 1), await ((IAiStore)f.Repo).WebUsageAsync(f.Company, DateTimeOffset.UtcNow, default));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")][InlineData("https://169.254.169.254/")][InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")][InlineData("http://100.64.0.1/")][InlineData("https://user:secret@example.com/")]
    [InlineData("file:///c:/secret")][InlineData("https://foo.local/")][InlineData("https://example.com:8443/")]
    public void WebRejectsPrivateCredentialedAndNonHttpUrls(string url) => Assert.False(AiPublicUrl.TryParse(url, out _));

    [Fact]
    public async Task WebSettingsRejectNonOfficialProvidersAndHideKeys()
    {
        await using var f = await Fixture.Create(); await f.Grant(AiPermissions.Settings, AiPermissions.Chat);
        var config = f.Configuration("https://example.com", "mock-key", "cloud") with { Web = new(true, f.Provider) };
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync("/api/ai/settings", config)).StatusCode);
        config = config with { Providers = [config.Providers[0] with { BaseUrl = "https://api.deepseek.com" }] };
        var response = await f.Client.PutAsJsonAsync("/api/ai/settings", config); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("mock-key", text); Assert.DoesNotContain("protectedKey", text);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync("/api/ai/settings", config with { Revision = 1, Web = config.Web! with { PerTurnLimit = 6 } })).StatusCode);
    }

    [Fact]
    public async Task WebDefaultGrantAppliesOnceOnlyToBuiltInAdministratorAndPreservesRevocation()
    {
        await using var f = await Fixture.Create();
        var settings = await f.Repo.GetAuthorizationSettingsAsync(default);
        Assert.True(settings.AiWebDefaultsApplied);
        Assert.Equal(nameof(UserRole.Administrator), Assert.Single(settings.Templates, t => t.Grants.Any(g => g.Permission == AiPermissions.Web)).RoleCode);
        var revoked = settings with { Templates = settings.Templates.Select(t => t with { Grants = t.Grants.Where(g => g.Permission != AiPermissions.Web).ToArray() }).ToArray() };
        await f.Repo.SaveAuthorizationSettingsAsync(revoked, settings.Revision, default);
        Assert.DoesNotContain(AuthorizationMigration.EnableAiWebDefaults(await f.Repo.GetAuthorizationSettingsAsync(default)).Templates.SelectMany(t => t.Grants), g => g.Permission == AiPermissions.Web);
        var old = revoked with { AiWebDefaultsApplied = false };
        var migrated = AuthorizationMigration.EnableAiWebDefaults(old);
        Assert.Single(migrated.Templates.SelectMany(t => t.Grants), g => g.Permission == AiPermissions.Web);
        Assert.Equal(old.Templates.SelectMany(t => t.Grants), migrated.Templates.SelectMany(t => t.Grants).Where(g => g.Permission != AiPermissions.Web));
    }

    [Fact]
    public async Task WebQuotaIsAtomicAcrossCallsAndRollsOverAtBeijingMidnight()
    {
        await using var f = await Fixture.Create();
        await VerifyQuota((IAiStore)f.Repo, (IAiStore)f.Repo, f.Company);
    }
    private static async Task VerifyQuota(IAiStore store, IAiStore second, Guid company)
    {
        var at = DateTimeOffset.Parse("2026-11-12T15:59:59Z"); var limits = new AiWebSettings(DailyLimit: 3, MonthlyLimit: 4);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => (i % 2 == 0 ? store : second).ReserveWebCallAsync(company, at, limits, default)));
        Assert.Equal(3, attempts.Count(success => success)); Assert.Equal(new AiWebUsage(3, 3), await second.WebUsageAsync(company, at, default));
        Assert.True(await second.ReserveWebCallAsync(company, at.AddSeconds(2), limits, default));
        Assert.False(await store.ReserveWebCallAsync(company, at.AddSeconds(2), limits, default));
        Assert.Equal(new AiWebUsage(1, 4), await second.WebUsageAsync(company, at.AddSeconds(2), default));
        Assert.True(await second.ReserveWebCallAsync(company, DateTimeOffset.Parse("2026-11-30T16:00:00Z"), limits, default));
        Assert.Equal(new AiWebUsage(0, 0), await store.WebUsageAsync(Guid.NewGuid(), at, default));
    }
    [Fact]
    public async Task OptionalLocalMySqlWebQuotaIsDurableAtomicAndCompanyIsolated()
    {
        var connection = Environment.GetEnvironmentVariable("PDM_AI_MYSQL_TEST_CONNECTION"); if (string.IsNullOrWhiteSpace(connection)) return;
        var company = Guid.NewGuid();
        IAiStore Store() => new MySqlPdmRepository(Microsoft.Extensions.Options.Options.Create(new PdmDatabaseOptions { ConnectionString = connection }), TimeProvider.System);
        try { await VerifyQuota(Store(), Store(), company); }
        finally {
            await using var db = new MySqlConnector.MySqlConnection(connection); await db.OpenAsync();
            await using var cleanup = db.CreateCommand(); cleanup.CommandText = "DELETE FROM ai_web_usage WHERE company_id=@company"; cleanup.Parameters.AddWithValue("@company", company); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
