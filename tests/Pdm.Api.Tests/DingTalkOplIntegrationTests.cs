using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
}
