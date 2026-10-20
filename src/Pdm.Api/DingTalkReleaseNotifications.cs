using System.Net.Http.Json;
using System.Text.Json;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed partial class DingTalkOplIntegration
{
    public async Task<string> SendReleaseNoticeAsync(Guid groupId, Guid deliveryId, string title, string text, string link, CancellationToken ct)
    {
        if (!Settings.Groups.TryGetValue(groupId, out var group) || !ApplicationRobotEnabled(group))
            throw new PdmRuleException("发布通知接收群或企业机器人尚未配置。");
        var token = await ApplicationTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dingtalk.com/v1.0/im/v1.0/robot/interactiveCards/send");
        request.Headers.Add("x-acs-dingtalk-access-token", token);
        request.Content = JsonContent.Create(new
        {
            cardTemplateId = "StandardCard", robotCode = Settings.RobotCode, openConversationId = group.OpenConversationId,
            cardBizId = "release-" + deliveryId.ToString("N"), pullStrategy = false,
            cardData = JsonSerializer.Serialize(new { config = new { autoLayout = true, enableForward = false }, header = new { title = new { type = "text", text = title } },
                contents = new object[] { new { type = "markdown", text = Markdown(text), id = "content" },
                    new { type = "action", id = "entry", actions = new[] { new { type = "button", id = "release", label = new { type = "text", text = "查看发布及回执" }, actionType = "openLink", url = new { all = link }, status = "primary" } } } } }),
            sendOptions = new { atAll = false, atUserListJson = "[]" }
        });
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!result.RootElement.TryGetProperty("processQueryKey", out var key) || string.IsNullOrWhiteSpace(key.GetString()))
            throw new InvalidOperationException("钉钉未确认接收本次通知。");
        return key.GetString()!;
    }
}
