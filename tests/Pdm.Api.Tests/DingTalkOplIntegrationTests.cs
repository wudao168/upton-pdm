using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Api;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api.Tests;

public sealed class DingTalkOplIntegrationTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static DingTalkOplOptions Settings() => new() { Enabled = true, CorpId = "corp", AppKey = "app", AppSecret = "secret", CompanyId = Guid.NewGuid(), PublicUrl = "https://plm.example.com", Users = new() { ["ding-user"] = "engineer" }, Projects = new() { [ProjectId] = new() { AccessToken = "robot-token", SigningSecret = "SEC-secret" } } };
    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Requests.Count - 1]) };
        }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1234567890000); }
    private static OplIssue Issue() => new(Guid.NewGuid(), ProjectId, 8, "相机支架松动", "普通", "待处理", "engineer", null, "", "engineer", DateTimeOffset.UtcNow, null, "engineer", DateTimeOffset.UtcNow, 1, []);
    [Fact]
    public async Task IdentityUsesDingTalkNameWithoutPlmMapping()
    {
        var options = Settings(); options.Users.Clear();
        var handler = new Handler("{\"accessToken\":\"token\"}", "{\"errcode\":0,\"result\":{\"userid\":\"ding-user\",\"name\":\"现场提交人\"}}");
        var service = new DingTalkOplIntegration(new(handler), Options.Create(options), new Clock());
        Assert.Equal(new DingTalkVisitor("ding-user", "现场提交人"), await service.ResolveVisitorAsync(null,"auth-code",default));
        Assert.Equal(2, handler.Requests.Count);
    }
    [Fact]
    public async Task MissingNameIsReadFromDingTalkUserDetails()
    {
        var handler = new Handler("{\"accessToken\":\"token\"}", "{\"errcode\":0,\"result\":{\"userid\":\"ding-user\"}}", "{\"errcode\":0,\"result\":{\"name\":\"现场王工\"}}");
        var service = new DingTalkOplIntegration(new(handler), Options.Create(Settings()), new Clock());
        Assert.Equal("现场王工", (await service.ResolveVisitorAsync(null, "code", default)).Name);
        Assert.Equal("/topapi/v2/user/get", handler.Requests[2].Url.AbsolutePath);
    }
    [Fact]
    public async Task DisabledOrUnboundProjectsNeverCallDingTalk()
    {
        var handler = new Handler(); var options = Settings(); options.Enabled = false;
        var service = new DingTalkOplIntegration(new(handler), Options.Create(options), new Clock());
        Assert.False(service.SubmissionEnabled(ProjectId));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ResolveVisitorAsync(ProjectId, "code", default));
        Assert.False((await service.NotifyAsync(Issue(), false, default)).Sent);
        Assert.Empty(handler.Requests);
        options.Enabled = true;
        Assert.False(service.SubmissionEnabled(Guid.NewGuid()));
    }
    [Fact]
    public async Task LegacyWebhookIsNeverUsedAndNonWebAddressesAreRejected()
    {
        var handler = new Handler(); var options = Settings();
        var service = new DingTalkOplIntegration(new(handler), Options.Create(options), new Clock());
        Assert.False((await service.NotifyAsync(Issue(),false,default)).Sent);
        Assert.Empty(handler.Requests);
        options.RobotCode="robot"; options.Projects[ProjectId].OpenConversationId="cid-test";
        Assert.True(service.NotificationEnabled(ProjectId));
        options.PublicUrl="javascript:alert(1)"; Assert.False(service.NotificationEnabled(ProjectId));
        options.PublicUrl="file:///C:/private"; Assert.False(service.NotificationEnabled(ProjectId));
    }
    [Fact]
    public async Task PublicBootstrapDoesNotExposeSecretsAndSubmissionRequiresAuthentication()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var bootstrap = await client.GetFromJsonAsync<JsonElement>($"/api/integrations/dingtalk/opl/{ProjectId}/bootstrap");
        Assert.False(bootstrap.GetProperty("enabled").GetBoolean());
        Assert.False(bootstrap.TryGetProperty("appSecret", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/projects/{ProjectId}/opl/integrations/dingtalk/submit", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/integrations/dingtalk/opl/login", new { projectId = ProjectId, code = "code" })).StatusCode);
    }

    [Fact]
    public async Task SpeechRequiresTheCurrentGroupAndBoundProject()
    {
        var settings = Settings();
        using var host = new PdmApiFactory();
        using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new DingTalkOplIntegration(new HttpClient(new Handler()), Options.Create(settings), TimeProvider.System));
            services.AddSingleton<IOplSpeechRecognizer, GuestSpeech>();
        }));
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        settings.CompanyId = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var bound = await repo.CreateNumberedProjectAsync(new(settings.CompanyId, "P", 2, customer, "群绑定项目",
            null, null, 1, "admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var other = await repo.CreateNumberedProjectAsync(new(settings.CompanyId, "P", 2, customer, "其他群项目",
            null, null, 1, "admin", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var group = Guid.NewGuid();
        var otherGroup = Guid.NewGuid();
        settings.Groups[group] = new() { OpenConversationId = "bound-group" };
        settings.Groups[otherGroup] = new() { OpenConversationId = "another-group" };
        settings.Bindings[bound.Id] = group;
        settings.Bindings[other.Id] = otherGroup;
        async Task<HttpResponseMessage> Send(Guid projectId)
        {
            using var audio = new ByteArrayContent(ProjectOplApiTests.TestWave());
            audio.Headers.ContentType = new("audio/wav");
            return await client.PostAsync($"/api/integrations/dingtalk/opl/guest/projects/{projectId}/speech/transcribe", audio);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(bound.Id)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<DingTalkVisitorSession>().Issue(settings, group, new("ding-user", "现场人员"));
        client.DefaultRequestHeaders.Add("X-DingTalk-Session", session);
        using var accepted = await Send(bound.Id);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal("安装支架松动。", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(other.Id)).StatusCode);
        settings.Bindings.Remove(bound.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(bound.Id)).StatusCode);
    }

    private sealed class GuestSpeech : IOplSpeechRecognizer
    {
        public Task<string?> RecognizeAsync(byte[] wave, CancellationToken ct) => Task.FromResult<string?>("安装支架松动。");
    }
}
