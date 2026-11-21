using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class PreviewServerSettingsApiTests
{
    private const string Route = "/api/system-settings/preview-servers";
    private const string SettingsRoute = "/api/system-settings";

    [Fact]
    public async Task ConnectionStatusChecksDisabledServersAndDistinguishesAgentReadinessFromOffline()
    {
        var probes = new ProbeClient(uri => uri.Host switch
        {
            "ready" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { workerExists = true }) },
            "disabled" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { workerExists = false }) },
            "unrelated-web-service" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { other = true }) },
            _ => throw new HttpRequestException("simulated connection failure")
        });
        await using var factory = new PdmApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(probes)));
        using var client = AuthenticatedClient(factory.CreateClient(), "admin", "Administrator");
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var settings = await repository.GetSystemSettingsAsync(default);
        const string ready = "http://ready:5199";
        const string disabled = "http://disabled:5199";
        const string offline = "http://offline:5199";
        const string unrelatedService = "http://unrelated-web-service:5199";
        const string secret = "test-status-secret";
        await repository.UpdateSystemSettingsAsync(settings with
        {
            PreviewConversion = new()
            {
                Mode = PreviewConversionMode.Remote,
                Servers =
                [
                    new("可用", ready, ""),
                    new PreviewAgentServer("停用", disabled, secret) { Disabled = true },
                    new("离线", offline, ""),
                    new("其他服务", unrelatedService, "")
                ]
            }
        }, default);

        using var response = await client.GetAsync(Route + "/status");
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync());
        var statuses = (await response.Content.ReadFromJsonAsync<StatusView[]>())!.ToDictionary(status => status.AgentUrl);
        Assert.Equal(4, statuses.Count);
        Assert.True(statuses[ready].Connected);
        Assert.True(statuses[ready].WorkerReady);
        Assert.True(statuses[disabled].Connected);
        Assert.False(statuses[disabled].WorkerReady);
        Assert.False(statuses[offline].Connected);
        Assert.False(statuses[offline].WorkerReady);
        Assert.False(statuses[unrelatedService].Connected);
        Assert.All(statuses.Values, status => Assert.NotEqual(default, status.CheckedAt));
        Assert.Contains((disabled + "/health", secret), probes.Requests);
        Assert.True((await repository.GetSystemSettingsAsync(default)).PreviewConversion.Servers.Single(server => server.AgentUrl == disabled).Disabled);
    }

    [Fact]
    public async Task LegacySettingsBecomeIndependentRowsWithoutReturningTokens()
    {
        var probes = new ProbeClient();
        await using var factory = new PdmApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(probes)));
        using var client = AuthenticatedClient(factory.CreateClient(), "admin", "Administrator");
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var settings = await repository.GetSystemSettingsAsync(default);
        const string original = "http://pc-one:5199";
        const string updated = "http://pc-one-new:5299";
        const string second = "http://pc-two:5199";
        const string secret = "test-legacy-secret";
        await repository.UpdateSystemSettingsAsync(settings with
        {
            PreviewConversion = new() { Mode = PreviewConversionMode.Remote, AgentUrl = original + "/", AgentToken = secret, TimeoutMinutes = 45 }
        }, default);

        var legacy = Assert.Single((await client.GetFromJsonAsync<ServerView[]>(Route))!);
        Assert.Equal(new ServerView(original, original, true, 45, false), legacy);
        var stale = await client.GetFromJsonAsync<PdmSystemSettings>(SettingsRoute);
        Assert.Equal(string.Empty, stale!.PreviewConversion.AgentToken);
        var created = await client.PostAsJsonAsync(Route, new { name = "二号转图", agentUrl = second, agentToken = "test-second-secret", timeoutMinutes = 75, disabled = true });
        created.EnsureSuccessStatusCode();
        Assert.DoesNotContain("test-second-secret", await created.Content.ReadAsStringAsync());

        var duplicate = await client.PostAsJsonAsync(Route, new { name = "相同地址", agentUrl = second + "/", timeoutMinutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var edited = await client.PutAsJsonAsync(Route, new { originalUrl = original, name = "一号转图", agentUrl = updated, timeoutMinutes = 60, disabled = false });
        edited.EnsureSuccessStatusCode();
        Assert.DoesNotContain(secret, await edited.Content.ReadAsStringAsync());
        Assert.Equal(new ServerView("一号转图", updated, true, 60, false), (await edited.Content.ReadFromJsonAsync<ServerView>())!);
        var staleSave = await client.PutAsJsonAsync(SettingsRoute, stale);
        staleSave.EnsureSuccessStatusCode();
        Assert.DoesNotContain(secret, await staleSave.Content.ReadAsStringAsync());
        var rows = (await client.GetFromJsonAsync<ServerView[]>(Route))!;
        Assert.Equal(new[] { 60, 75 }, rows.Select(row => row.TimeoutMinutes));
        Assert.True(rows[1].Disabled);

        var test = await client.PostAsJsonAsync(Route + "/test", new { agentUrl = updated });
        test.EnsureSuccessStatusCode();
        Assert.True((await test.Content.ReadFromJsonAsync<PreviewAgentProbeResult>())!.Ok);
        Assert.Equal((updated + "/health", secret), Assert.Single(probes.Requests));
        var invalidProbe = await client.PostAsJsonAsync(Route + "/test", new { agentUrl = second, originalUrl = updated });
        Assert.Equal(HttpStatusCode.BadRequest, invalidProbe.StatusCode);
        Assert.Single(probes.Requests);

        (await client.PutAsJsonAsync(Route, new { originalUrl = updated, name = "一号转图", agentUrl = updated, agentToken = "", timeoutMinutes = 60, disabled = false })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Route + "/test", new { agentUrl = updated })).EnsureSuccessStatusCode();
        Assert.Null(probes.Requests.Last().Token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Route + "?agentUrl=" + Uri.EscapeDataString(updated))).StatusCode);
        Assert.Equal(second, Assert.Single((await client.GetFromJsonAsync<ServerView[]>(Route))!).AgentUrl);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Route + "?agentUrl=" + Uri.EscapeDataString(second))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ServerView[]>(Route))!);
        var conversion = (await repository.GetSystemSettingsAsync(default)).PreviewConversion;
        Assert.True(conversion.AwaitingRegistration);
        Assert.Equal(PreviewConversionMode.Remote, conversion.Mode);
        Assert.Empty(conversion.GetConfiguredServers());
    }

    [Fact]
    public async Task ManagementAndProbeRespectPermissionAndServerCompany()
    {
        await using var factory = new PdmApiFactory();
        using var admin = AuthenticatedClient(factory.CreateClient(), "admin", "Administrator");
        using var viewer = AuthenticatedClient(factory.CreateClient(), "viewer", "ProductionViewer");
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var settings = await repository.GetSystemSettingsAsync(default);
        const string elsewhere = "http://other-company:5199";
        await repository.UpdateSystemSettingsAsync(settings with
        {
            PreviewConversion = new()
            {
                Mode = PreviewConversionMode.Remote, AgentUrl = elsewhere, AgentToken = "test-company-secret",
                Servers = [new("另一公司服务器", elsewhere, "test-company-secret", CompanyId: Guid.NewGuid())]
            }
        }, default);
        var row = new { originalUrl = elsewhere, name = "转图服务器", agentUrl = elsewhere, timeoutMinutes = 30 };
        var delete = Route + "?agentUrl=" + Uri.EscapeDataString(elsewhere);
        Assert.Empty((await admin.GetFromJsonAsync<ServerView[]>(Route))!);
        Assert.Empty((await admin.GetFromJsonAsync<StatusView[]>(Route + "/status"))!);
        Assert.DoesNotContain(elsewhere, await admin.GetStringAsync(SettingsRoute));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync(Route, row)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Route + "/test", row)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Route + "/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Route, row)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(Route, row)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Route + "/test", row)).StatusCode);
        Assert.Equal(elsewhere, Assert.Single((await repository.GetSystemSettingsAsync(default)).PreviewConversion.Servers).AgentUrl);
    }

    private static HttpClient AuthenticatedClient(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private sealed record ServerView(string Name, string AgentUrl, bool HasToken, int TimeoutMinutes, bool Disabled);
    private sealed record StatusView(string AgentUrl, bool Connected, bool WorkerReady, DateTimeOffset CheckedAt, string Message);

    private sealed class ProbeClient(Func<Uri, HttpResponseMessage>? respond = null) : IHttpClientFactory
    {
        public ConcurrentQueue<(string Url, string? Token)> Requests { get; } = new();

        public HttpClient CreateClient(string name) => new(new ProbeHandler(Requests, respond));

        private sealed class ProbeHandler(ConcurrentQueue<(string Url, string? Token)> requests, Func<Uri, HttpResponseMessage>? respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                requests.Enqueue((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues(PreviewAgentProtocol.TokenHeader, out var tokens) ? tokens.Single() : null));
                return Task.FromResult(respond?.Invoke(request.RequestUri) ??
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { machine = "测试服务器", workerExists = true }) });
            }
        }
    }
}
