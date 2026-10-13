using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class DingTalkOplOptions
{
    public bool Enabled { get; set; }
    public string CorpId { get; set; } = "";
    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string RobotCode { get; set; } = "";
    public Guid CompanyId { get; set; }
    public string PublicUrl { get; set; } = "";
    public Dictionary<string, string> Users { get; set; } = [];
    public Dictionary<Guid, DingTalkOplGroup> Projects { get; set; } = [];
    public Dictionary<Guid, DingTalkOplGroup> Groups { get; set; } = [];
    public Dictionary<Guid, Guid> Bindings { get; set; } = [];
    public long RowVersion { get; set; }
}
public sealed class DingTalkOplGroup
{
    public string Name { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public string OpenConversationId { get; set; } = "";
}
public sealed record DingTalkLoginInput(Guid? ProjectId, string Code, Guid? CompanyId = null, Guid? GroupId = null);
public sealed record DingTalkNotificationResult(bool Sent, string Message);
public sealed record DingTalkVisitor(string UserId, string Name);

// 请求地址固定为钉钉官方域名；配置与响应中均不向浏览器暴露应用密钥或机器人密钥。
public sealed partial class DingTalkOplIntegration(HttpClient http, IOptions<DingTalkOplOptions> options, TimeProvider clock, DingTalkConfigurationService? configuration = null, IPdmRepository? projects = null, ILogger<DingTalkOplIntegration>? logger = null, IProjectOplStorage? storage = null)
{
    private DingTalkOplOptions? loaded;
    public DingTalkOplOptions Settings => loaded ?? options.Value;
    public async Task LoadAsync(Guid? companyId, CancellationToken ct)
    { if (configuration is not null) loaded = await configuration.LoadAsync(companyId, ct); }
    public bool ApplicationEnabled => Settings.Enabled && Settings.CompanyId != Guid.Empty
        && !string.IsNullOrWhiteSpace(Settings.CorpId) && !string.IsNullOrWhiteSpace(Settings.AppKey)
        && !string.IsNullOrWhiteSpace(Settings.AppSecret);
    private DingTalkOplGroup? Group(Guid projectId) => Settings.Bindings.TryGetValue(projectId, out var id)
        ? Settings.Groups.GetValueOrDefault(id) : Settings.Projects.GetValueOrDefault(projectId);
    public bool SubmissionEnabled(Guid projectId) => ApplicationEnabled && Group(projectId) is not null;
    private bool ApplicationRobotEnabled(DingTalkOplGroup group) => ApplicationEnabled && !string.IsNullOrWhiteSpace(Settings.RobotCode) && !string.IsNullOrWhiteSpace(group.OpenConversationId);
    public bool NotificationEnabled(Guid projectId) => Settings.Enabled && Group(projectId) is { } group
        && ApplicationRobotEnabled(group)
        && Uri.TryCreate(Settings.PublicUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";
    public async Task<DingTalkVisitor> ResolveVisitorAsync(Guid? projectId, string code, CancellationToken ct)
    {
        if (!ApplicationEnabled || projectId is Guid id && !SubmissionEnabled(id)) throw new PdmRuleException("钉钉提交尚未配置，请联系管理员完成应用与项目绑定。");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 512) throw new PdmRuleException("钉钉授权码无效，请重新打开群内入口。");
        try
        {
            using var tokenResponse = await http.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", new { appKey = Settings.AppKey, appSecret = Settings.AppSecret }, ct);
            tokenResponse.EnsureSuccessStatusCode();
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            var accessToken = token.RootElement.GetProperty("accessToken").GetString();
            if (string.IsNullOrWhiteSpace(accessToken)) throw new JsonException();
            using var userResponse = await http.PostAsJsonAsync("https://oapi.dingtalk.com/topapi/v2/user/getuserinfo?access_token=" + Uri.EscapeDataString(accessToken), new { code }, ct);
            userResponse.EnsureSuccessStatusCode();
            using var user = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(ct));
            if (user.RootElement.GetProperty("errcode").GetInt32() != 0) throw new PdmRuleException("钉钉授权已失效，请重新打开群内入口。");
            var identity = user.RootElement.GetProperty("result");
            var userId = identity.GetProperty("userid").GetString();
            var name = identity.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(userId)) throw new PdmRuleException("未能识别钉钉账户，请重新打开群入口。");
            if (string.IsNullOrWhiteSpace(name))
            {
                using var detailResponse = await http.PostAsJsonAsync("https://oapi.dingtalk.com/topapi/v2/user/get?access_token=" + Uri.EscapeDataString(accessToken), new { userid = userId }, ct);
                detailResponse.EnsureSuccessStatusCode();
                using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(ct));
                if (detail.RootElement.GetProperty("errcode").GetInt32() != 0) throw new PdmRuleException("未能读取钉钉姓名，请联系管理员检查应用的成员信息读取权限。");
                name = detail.RootElement.GetProperty("result").GetProperty("name").GetString();
            }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new PdmRuleException("未能读取有效钉钉姓名，请重新打开群入口。");
            return new(userId, name.Trim());
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new PdmRuleException("钉钉身份验证暂时不可用，请稍后重试或联系管理员检查应用配置。"); }
    }
}
