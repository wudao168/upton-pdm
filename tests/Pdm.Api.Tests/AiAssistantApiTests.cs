using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class AiAssistantApiTests
{
    [Theory]
    [InlineData("2026-11-11T15:59:00Z", "2026-11-11 23:59:00", "星期三")]
    [InlineData("2026-11-11T16:01:00Z", "2026-11-12 00:01:00", "星期四")]
    public void CurrentDateUsesShanghaiDayAcrossUtcBoundary(string utc, string expected, string weekday)
    {
        var context = AiClockContext.Describe(DateTimeOffset.Parse(utc)); Assert.Contains(expected, context); Assert.Contains(weekday, context); Assert.Contains("无需联网", context);
    }

    [Fact]
    public async Task ExistingOrdinaryConversationCanEnableAndDisableToolsWithFreshAuthorization()
    {
        await using var upstream = await MockModel.Start(); await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings); await f.Configure(upstream.Url); var c = await f.Conversation();
        var path = $"/api/ai/conversations/{c.Id}/messages";
        var ordinary = await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "今天星期几，系统如何使用，能联网吗？", f.Model, false));
        Assert.Contains("event: done", await ordinary.Content.ReadAsStringAsync());
        var initial = upstream.Requests.Last(); Assert.False(initial.TryGetProperty("tools", out _));
        var prompt = initial.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("本轮可信服务器时间", prompt); Assert.Contains("系统内置基础说明", prompt); Assert.Contains("未接入联网搜索", prompt);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "查询项目", f.Model, true))).StatusCode);
        var project = await f.Project("模式切换验收"); await f.ProjectGrant(project.Id, AiPermissions.Business, PermissionCodes.ProjectView);
        var enabled = await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "查询项目", f.Model, true)); Assert.DoesNotContain("event: error", await enabled.Content.ReadAsStringAsync());
        Assert.True((await f.Client.GetFromJsonAsync<AiConversation>($"/api/ai/conversations/{c.Id}"))!.Business);
        Assert.True(upstream.Requests.Last().TryGetProperty("tools", out var tools)); Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "ReadBomPublication");
        var disabled = await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "今天星期几", f.Model, false)); Assert.DoesNotContain("event: error", await disabled.Content.ReadAsStringAsync());
        Assert.False(upstream.Requests.Last().TryGetProperty("tools", out _)); Assert.False((await f.Client.GetFromJsonAsync<AiConversation>($"/api/ai/conversations/{c.Id}"))!.Business);
    }

    [Fact]
    public async Task PublicationQuerySeparatesReleasedDraftAndReviewAndHonorsResourceRevocation()
    {
        await using var f = await Fixture.Create(); await f.Grant(AiPermissions.Chat);
        var project = await f.Project("发布状态验收"); var other = await f.Project("范围外项目");
        await f.ProjectGrant(project.Id, AiPermissions.Business, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("bom"), "bom.mechanical.row.view");
        var standard = await f.Repo.SaveBomDraftAsync(project.Id, BomKind.Standard, [], Fixture.Actor, default);
        var nonstandard = await f.Repo.SaveBomDraftAsync(project.Id, BomKind.NonStandard, [], Fixture.Actor, default);
        await f.Repo.SetBomVersionStateAsync([nonstandard.Id], BomVersionState.InReview, Fixture.Actor, null, default);
        using var scope = f.Factory.Services.CreateScope(); var tools = scope.ServiceProvider.GetRequiredService<AiBusinessTools>();
        TenantContext.Set(new(f.UserId, f.Company, f.Company, Fixture.Actor, Fixture.Role, false, new HashSet<string>()));
        try {
            var draft = JsonSerializer.SerializeToElement(await tools.ReadBomPublication(project.Id, "Standard", default));
            Assert.Equal(JsonValueKind.Null, draft.GetProperty("currentPublishedVersion").ValueKind); Assert.Equal(1, draft.GetProperty("draftCount").GetInt32());
            var review = JsonSerializer.SerializeToElement(await tools.ReadBomPublication(project.Id, "NonStandard", default));
            Assert.Equal(JsonValueKind.Null, review.GetProperty("currentPublishedVersion").ValueKind); Assert.Equal(1, review.GetProperty("reviewingCount").GetInt32());
            await f.Repo.SetBomVersionStateAsync([standard.Id], BomVersionState.Released, Fixture.Actor, DateTimeOffset.UtcNow, default);
            var released = JsonSerializer.SerializeToElement(await tools.ReadBomPublication(project.Id, "Standard", default)); Assert.Equal(standard.Label, released.GetProperty("currentPublishedVersion").GetString());
            Assert.Contains("businessStatus", JsonSerializer.Serialize(await tools.ReadProjectStatus(project.Id, default)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadBomPublication(project.Id, "Electrical", default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectStatus(other.Id, default));
            await f.ProjectGrant(null);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadBomPublication(project.Id, "Standard", default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectStatus(project.Id, default));
        } finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task ExplicitProjectStatusScopeExposesOnlyTwoBoundToolsAndStillChecksProjectAccess()
    {
        await using var upstream = await MockModel.Start(); await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings); await f.Configure(upstream.Url);
        var project = await f.Project("限定状态验收"); var other = await f.Project("禁止读取项目");
        await f.ProjectGrant(project.Id, AiPermissions.Business, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView);
        var c = await f.Conversation(); var path = $"/api/ai/conversations/{c.Id}/messages";
        var response = await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "查询当前项目状态", f.Model, true, project.Id));
        Assert.DoesNotContain("event: error", await response.Content.ReadAsStringAsync());
        var body = upstream.Requests.Last(); var names = body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["ReadProjectStatus", "ReadBomPublication"], names);
        var result = JsonDocument.Parse(body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!).RootElement;
        Assert.Equal(2, result.EnumerateObject().Count()); Assert.Equal(project.Code, result.GetProperty("project").GetString());
        var detail = await f.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}");
        Assert.Equal(detail.GetProperty("businessStatus").GetString(), result.GetProperty("businessStatus").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), "查询", f.Model, true, other.Id))).StatusCode);
    }

    [Fact]
    public async Task CatalogueDefaultsSecretsConfigurationAndCompanyIsolation()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/ai/bootstrap")).StatusCode);
        var before = await f.Repo.GetAuthorizationSettingsAsync(default);
        await f.Grant(PermissionCodes.RoleSettingsView);
        var catalogue = await f.Client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        var permissions = catalogue.GetProperty("permissions").EnumerateArray().Where(p => p.GetProperty("code").GetString()!.StartsWith("ai.")).ToArray();
        Assert.Equal(4, permissions.Length);
        Assert.DoesNotContain(before.Templates.SelectMany(t => t.Grants), g => g.Permission.StartsWith("ai."));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/ai/settings")).StatusCode);
        await f.Grant(AiPermissions.Settings, AiPermissions.Chat);
        var input = f.Configuration("http://127.0.0.1:1/v1", "验收密钥");
        var saved = await f.Client.PutAsJsonAsync("/api/ai/settings", input); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var json = await saved.Content.ReadAsStringAsync(); Assert.DoesNotContain("验收密钥", json); Assert.DoesNotContain("protectedKey", json); Assert.Contains("hasKey", json);
        var stored = await ((IAiStore)f.Repo).SettingsAsync(f.Company, default); Assert.NotEqual("验收密钥", stored.Providers[0].ProtectedKey);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PutAsJsonAsync("/api/ai/settings", input)).StatusCode);
        var changed = input with { Revision = 1, Providers = [input.Providers[0] with { BaseUrl = "http://127.0.0.1:2/v1", ApiKey = null }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsJsonAsync("/api/ai/settings", changed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PutAsJsonAsync("/api/ai/settings", changed with { Providers = [changed.Providers[0] with { ClearKey = true }] })).StatusCode);
        await f.Repo.SetUserCompanyScopeAsync(Fixture.Actor, f.Company, true, [f.Other], "admin", default);
        f.CompanyHeader(f.Other);
        var other = await f.Client.GetFromJsonAsync<JsonElement>("/api/ai/settings"); Assert.Empty(other.GetProperty("providers").EnumerateArray());
        f.CompanyHeader(f.Company); await f.Grant();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/ai/settings")).StatusCode);
    }

    [Fact]
    public async Task ActualDeepSeekProtocolStreamsToolsPersistsAndRevokesHistory()
    {
        await using var upstream = await MockModel.Start(); await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings);
        var first = await f.Project("允许项目"); var second = await f.Project("范围外项目");
        await f.ProjectGrant(first.Id, AiPermissions.Business, PermissionCodes.ProjectView);
        await f.Configure(upstream.Url);
        var probe = await f.Client.PostAsJsonAsync($"/api/ai/providers/{f.Provider}/test", new AiTestInput("test-model", true));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        var c = await f.Conversation(true); var request = new AiSendInput(Guid.NewGuid(), "查询项目", f.Model);
        var result = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", request);
        var sse = await result.Content.ReadAsStringAsync(); Assert.Contains("event: delta", sse); Assert.Contains("event: done", sse); Assert.DoesNotContain("event: error", sse);
        var history = await f.Client.GetFromJsonAsync<AiConversation>($"/api/ai/conversations/{c.Id}");
        Assert.Equal("模拟回复：查询完成。", history!.Messages[^1].Text); Assert.Equal("complete", history.Messages[^1].Status);
        Assert.Equal(first.Id, Assert.Single(history.Messages[^1].Sources!).ProjectId); Assert.Null(history.BusyUntil);
        var toolRequest = upstream.Requests.Last(r => r.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"));
        var toolText = toolRequest.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        Assert.Contains(first.Id.ToString(), toolText); Assert.DoesNotContain(second.Id.ToString(), toolText);
        foreach (var body in upstream.Requests) { Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString()); Assert.True(body.GetProperty("stream").GetBoolean()); Assert.True(body.TryGetProperty("max_tokens", out _)); }
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", request)).StatusCode);
        var config = await ((IAiStore)f.Repo).SettingsAsync(f.Company, default);
        await ((IAiStore)f.Repo).SaveSettingsAsync(f.Company, config with { Providers = [config.Providers[0] with { Enabled = false }] }, default);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", request with { RequestId = Guid.NewGuid() })).StatusCode);
        await f.ProjectGrant(null);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
        Assert.Empty((await f.Client.GetFromJsonAsync<JsonElement>("/api/ai/conversations")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", request with { RequestId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task ToolsCheckEveryProjectCompanyAndOriginalBusinessPermission()
    {
        await using var f = await Fixture.Create(); await f.Grant(AiPermissions.Chat);
        var first = await f.Project("范围内项目"); var second = await f.Project("范围外项目");
        await f.ProjectGrant(first.Id, AiPermissions.Business, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("bom"), "bom.mechanical.row.view");
        using var scope = f.Factory.Services.CreateScope(); var tools = scope.ServiceProvider.GetRequiredService<AiBusinessTools>();
        TenantContext.Set(new(f.UserId, f.Company, f.Company, Fixture.Actor, Fixture.Role, false, new HashSet<string>()));
        try {
            var json = JsonSerializer.Serialize(await tools.SearchProjects("项目", default)); Assert.Contains(first.Id.ToString(), json); Assert.DoesNotContain(second.Id.ToString(), json);
            await tools.ReadProjectBom(first.Id, "Standard", "", default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectBom(second.Id, "Standard", "", default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectBom(first.Id, "Electrical", "", default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.SearchMaterials("传感器", default));
            TenantContext.Set(TenantContext.Current! with { CompanyId = f.Other });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectBom(first.Id, "Standard", "", default));
            TenantContext.Set(TenantContext.Current! with { CompanyId = f.Company });
            await f.ProjectGrant(null);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.ReadProjectBom(first.Id, "Standard", "", default));
        } finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task CloudIsSeparateAndConversationsBelongToOneUserAndCompany()
    {
        await using var f = await Fixture.Create(); await f.Grant(AiPermissions.Chat, AiPermissions.Settings);
        await f.Configure("https://api.deepseek.com", "cloud");
        var boot = await f.Client.GetFromJsonAsync<JsonElement>("/api/ai/bootstrap"); Assert.Empty(boot.GetProperty("models").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PostAsJsonAsync("/api/ai/conversations", new AiCreateInput(f.Model))).StatusCode);
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Cloud); var c = await f.Conversation();
        var user = new UserAccount(Guid.NewGuid(), "ai-second", "另一位验收人员", "unused", UserRole.Engineer, true, RoleCode: Fixture.Role, CompanyId: f.Company);
        await f.Repo.CreateUserAsync(user, default); await f.Repo.SetUserCompanyScopeAsync(user.Username, f.Company, false, [], "admin", default);
        f.Login(user.Username);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.DeleteAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
        Assert.Empty((await f.Client.GetFromJsonAsync<JsonElement>("/api/ai/conversations")).EnumerateArray());
        f.Login(Fixture.Actor); await f.Repo.SetUserCompanyScopeAsync(Fixture.Actor, f.Company, true, [f.Other], "admin", default); f.CompanyHeader(f.Other);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task UpstreamErrorsAreChineseAndFailedRunReleasesConversation()
    {
        await using var upstream = await MockModel.Start(401); await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings); await f.Configure(upstream.Url); var c = await f.Conversation();
        var response = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "你好", f.Model));
        var sse = await response.Content.ReadAsStringAsync(); Assert.Contains("event: error", sse); Assert.DoesNotContain("secret-upstream-error", sse);
        var saved = await f.Client.GetFromJsonAsync<AiConversation>($"/api/ai/conversations/{c.Id}"); Assert.Equal("failed", saved!.Messages[^1].Status); Assert.Null(saved.BusyUntil);
        var test = await f.Client.PostAsJsonAsync($"/api/ai/providers/{f.Provider}/test", new AiTestInput("test-model"));
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode); Assert.Contains("模型服务拒绝认证", await test.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("http://169.254.169.254", "intranet")]
    [InlineData("http://8.8.8.8/v1", "intranet")]
    [InlineData("https://127.0.0.1/v1", "cloud")]
    [InlineData("http://example.com/v1", "cloud")]
    [InlineData("http://user:password@127.0.0.1/v1", "intranet")]
    [InlineData("http://127.0.0.1/v1/chat/completions", "intranet")]
    public void EndpointPolicyRejectsMismatchedOrSensitiveTargets(string url, string network) => Assert.Throws<PdmRuleException>(() => AiEndpointPolicy.Validate(url, network));

    [Fact]
    public async Task CancellationStopsUpstreamAndConcurrentSubmissionsCannotOverwriteHistory()
    {
        await using var upstream = await MockModel.Start(pause: true); await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings); await f.Configure(upstream.Url); var c = await f.Conversation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "暂停验收", f.Model), cancellation.Token);
        await upstream.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), "重复请求", f.Model))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.DeleteAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await upstream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        AiConversation? saved = null;
        for (var attempt = 0; attempt < 40; attempt++) { saved = await ((IAiStore)f.Repo).FindAsync(f.Company, f.UserId, c.Id, default); if (saved?.BusyUntil is null) break; await Task.Delay(50); }
        Assert.Null(saved!.BusyUntil); Assert.Equal(2, saved.Messages.Length); Assert.Equal("stopped", saved.Messages[^1].Status);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Client.DeleteAsync($"/api/ai/conversations/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task OptionalLocalMySqlStoreRoundTripUsesBinaryIdentifiersAndOptimisticRevision()
    {
        var connection = Environment.GetEnvironmentVariable("PDM_AI_MYSQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return; // Explicit local deployment acceptance only.
        var company = Guid.NewGuid(); var user = Guid.NewGuid(); var id = Guid.NewGuid();
        IAiStore store = new Upton.Pdm.Infrastructure.MySqlPdmRepository(Microsoft.Extensions.Options.Options.Create(new Upton.Pdm.Infrastructure.PdmDatabaseOptions { ConnectionString = connection }), TimeProvider.System);
        await using var db = new MySqlConnector.MySqlConnection(connection); await db.OpenAsync();
        try {
            await store.SaveSettingsAsync(company, new(0, []), default); Assert.Equal(1, (await store.SettingsAsync(company, default)).Revision);
            await Assert.ThrowsAsync<PdmConflictException>(() => store.SaveSettingsAsync(company, new(0, []), default));
            var c = new AiConversation(id, company, user, 0, "数据库模拟验收", "test", false, [], DateTimeOffset.UtcNow);
            await store.SaveAsync(c, default); Assert.Null(await store.FindAsync(Guid.NewGuid(), user, id, default)); Assert.Null(await store.FindAsync(company, Guid.NewGuid(), id, default));
            var saved = (await store.FindAsync(company, user, id, default))!; Assert.Equal(1, saved.Revision);
            await store.SaveAsync(saved with { Messages = [new(Guid.NewGuid(), "assistant", "中文持久化正常", DateTimeOffset.UtcNow)] }, default);
            Assert.Equal("中文持久化正常", Assert.Single((await store.ListAsync(company, user, default)).Single().Messages).Text);
            await Assert.ThrowsAsync<PdmConflictException>(() => store.SaveAsync(saved, default));
            await store.DeleteAsync(company, user, id, 2, default); Assert.Null(await store.FindAsync(company, user, id, default));
        } finally {
            await using var cleanup = db.CreateCommand(); cleanup.CommandText = "DELETE FROM ai_conversations WHERE company_id=@company; DELETE FROM ai_settings WHERE company_id=@company"; cleanup.Parameters.AddWithValue("@company", company); await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Actor = "ai-acceptance", Role = "ai-acceptance-role";
        public PdmApiFactory Factory { get; } = new();
        public HttpClient Client { get; private set; } = null!;
        public IPdmRepository Repo { get; private set; } = null!;
        public Guid Company, Other, UserId = Guid.NewGuid(), Provider = Guid.NewGuid();
        public string Model => Provider + ":test-model";
        public static async Task<Fixture> Create() {
            var f = new Fixture(); f.Client = f.Factory.CreateClient(); f.Repo = f.Factory.Services.GetRequiredService<IPdmRepository>();
            var directory = await f.Repo.GetOrganizationDirectoryAsync(default); f.Company = directory.Organizations[0].Id; f.Other = directory.Organizations[1].Id;
            await f.Repo.CreateUserAsync(new(f.UserId, Actor, "AI功能验收", "unused", UserRole.Engineer, true, RoleCode: Role, CompanyId: f.Company), default);
            await f.Repo.SetUserCompanyScopeAsync(Actor, f.Company, false, [], "admin", default); f.CompanyHeader(f.Company); f.Login(Actor); return f;
        }
        public void CompanyHeader(Guid company) { Client.DefaultRequestHeaders.Remove("X-Company-Id"); Client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString()); }
        public void Login(string actor) => Client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Engineer")], expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256))));
        public async Task Grant(params string[] permissions) {
            var s = await Repo.GetAuthorizationSettingsAsync(default);
            await Repo.SaveAuthorizationSettingsAsync(s with { Templates = [..s.Templates.Where(t => t.Key != Role), new(Role, "AI验收岗位", "", permissions.Select(p => new AuthorizationGrant(p, ["company"])).ToArray(), Role)] }, s.Revision, default);
        }
        public async Task ProjectGrant(Guid? project, params string[] permissions) {
            var s = await Repo.GetAuthorizationSettingsAsync(default);
            await Repo.SaveAuthorizationSettingsAsync(s with { Templates = [..s.Templates.Where(t => t.Key != "ai-project"), new("ai-project", "AI项目范围", "", permissions.Select(p => new AuthorizationGrant(p, ["company"])).ToArray())], Assignments = [..s.Assignments.Where(a => a.TemplateKey != "ai-project"), ..(project is Guid id ? new[] { new AuthorizationAssignment(Guid.NewGuid(), Actor, "ai-project", Company, "project", id) } : [])] }, s.Revision, default);
        }
        public async Task<Project> Project(string name) {
            var customer = (await Repo.ListCustomersAsync(true, default)).First().Id;
            var path = Path.Combine(Path.GetTempPath(), "pdm-ai-tests", UserId.ToString("N"));
            var system = await Repo.GetSystemSettingsAsync(default); await Repo.UpdateSystemSettingsAsync(system with { VaultRoot = path }, default);
            return await Repo.CreateNumberedProjectAsync(new(Company, "P", 2, customer, name, null, null, 1, Actor, path, Path.Combine(path, "release")), default);
        }
        public AiSettingsInput Configuration(string url, string? key = null, string network = "intranet") => new(0, [new(Provider, "模拟服务", url, network, "deepseek", key, false, true, [new("test-model", "模拟模型", Tools: true)])], Model);
        public async Task Configure(string url, string network = "intranet") { var response = await Client.PutAsJsonAsync("/api/ai/settings", Configuration(url, network: network)); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); }
        public async Task<AiConversation> Conversation(bool business = false) { var response = await Client.PostAsJsonAsync("/api/ai/conversations", new AiCreateInput(Model, business)); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<AiConversation>())!; }
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); }
    }

    private sealed class MockModel(WebApplication app) : IAsyncDisposable
    {
        public string Url => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/v1";
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static async Task<MockModel> Start(int status = 200, bool pause = false) {
            var builder = WebApplication.CreateSlimBuilder(); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            var app = builder.Build(); var mock = new MockModel(app);
            app.MapPost("/v1/chat/completions", async (HttpContext context) => {
                var body = await context.Request.ReadFromJsonAsync<JsonElement>(); mock.Requests.Enqueue(body.Clone());
                mock.Received.TrySetResult();
                if (pause) { try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { mock.Cancelled.TrySetResult(); } return; }
                if (status != 200) { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(new { error = new { message = "secret-upstream-error", type = "authentication_error" } }); return; }
                context.Response.ContentType = "text/event-stream";
                var last = body.GetProperty("messages").EnumerateArray().Last();
                async Task Chunk(object delta, string? reason = null) { await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { id = "mock", @object = "chat.completion.chunk", created = 1, model = "test-model", choices = new[] { new { index = 0, delta, finish_reason = reason } } }) + "\n\n"); await context.Response.Body.FlushAsync(); }
                if (last.GetProperty("role").GetString() != "tool" && body.TryGetProperty("tools", out var toolList) && toolList.GetArrayLength() > 0) {
                    var name = toolList[0].GetProperty("function").GetProperty("name").GetString()!;
                    await Chunk(new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call-1", type = "function", function = new { name, arguments = name == "connection_check" ? "{}" : "{\"query\":\"\"}" } } } }, "tool_calls");
                } else {
                    await Chunk(new { role = "assistant", content = "模拟回复：" }); await Chunk(new { content = "查询完成。" }, "stop");
                }
                await context.Response.WriteAsync("data: [DONE]\n\n");
            });
            await app.StartAsync(); return mock;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
