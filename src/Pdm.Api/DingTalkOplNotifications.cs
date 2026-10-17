using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed partial class DingTalkOplIntegration
{
    private async Task<string> ApplicationTokenAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", new { appKey = Settings.AppKey, appSecret = Settings.AppSecret }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("accessToken").GetString() ?? throw new PdmRuleException("未能取得钉钉应用凭证。");
    }
    private static string Markdown(string? value) => Regex.Replace(value ?? "", @"([\\`*_{}\[\]()<>#!])", @"\$1");
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { [".jpg"]="image/jpeg",[".jpeg"]="image/jpeg",[".png"]="image/png",[".gif"]="image/gif",[".bmp"]="image/bmp" };
    private async Task<string> UploadImageAsync(OplIssue issue, OplAttachment file, string accessToken, CancellationToken ct)
    {
        if (storage is null || projects is null) throw new PdmRuleException("附件存储暂不可用。");
        var vault = (await projects.GetSystemSettingsAsync(ct)).VaultRoot;
        await using var stream = await storage.OpenAsync(vault, file.StorageProjectId ?? issue.AttachmentProjectId ?? issue.ProjectId, file.Id, ct);
        using var body = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(ImageTypes[Path.GetExtension(file.Name)]);
        body.Add(content, "media", file.Name);
        using var response = await http.PostAsync("https://oapi.dingtalk.com/media/upload?access_token=" + Uri.EscapeDataString(accessToken) + "&type=image", body, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (json.RootElement.TryGetProperty("errcode", out var error) && error.GetInt32() != 0) throw new PdmRuleException("图片上传失败。");
        var id = json.RootElement.GetProperty("media_id").GetString();
        return string.IsNullOrWhiteSpace(id) ? throw new PdmRuleException("钉钉未返回图片标识。") : id;
    }
    private async Task SendApplicationCardAsync(DingTalkOplGroup group, string token, string title, string text, IReadOnlyList<string> images, string link, CancellationToken ct)
    {
        var contents=new List<object> { new { type="markdown",text,id="content" } };
        if(images.Count>0) contents.Add(new {type="imageList",images,id="photos"});
        contents.Add(new {type="action",id="entry",actions=new[]{new {type="button",id="submit",label=new {type="text",text="提交开口问题"},actionType="openLink",url=new {all=link},status="primary"}}});
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.dingtalk.com/v1.0/im/v1.0/robot/interactiveCards/send");
        request.Headers.Add("x-acs-dingtalk-access-token",token);
        request.Content=JsonContent.Create(new {
            cardTemplateId="StandardCard",robotCode=Settings.RobotCode,openConversationId=group.OpenConversationId,cardBizId="opl-"+clock.GetUtcNow().ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N"),pullStrategy=false,
            cardData=JsonSerializer.Serialize(new {config=new {autoLayout=true,enableForward=true},header=new {title=new {type="text",text=title}},contents}),
            sendOptions=new {atAll=false,atUserListJson="[]"}
        });
        using var response=await http.SendAsync(request,ct); response.EnsureSuccessStatusCode();
        using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!result.RootElement.TryGetProperty("processQueryKey",out var key) || string.IsNullOrWhiteSpace(key.GetString())) throw new PdmRuleException("钉钉未确认接收图文卡片。");
    }
    public async Task<DingTalkNotificationResult> PublishEntryAsync(Guid groupId,CancellationToken ct)
    {
        try {
            await LoadAsync(TenantContext.CompanyId,ct);
            if(!Settings.Groups.TryGetValue(groupId,out var group) || !ApplicationRobotEnabled(group)) return new(false,"绑定已保存；请先完成企业机器人配置并登记项目群。");
            var link=Settings.PublicUrl.TrimEnd('/')+"/?dingtalkOpl=1&companyId="+Settings.CompanyId+"&groupId="+groupId;
            var token=await ApplicationTokenAsync(ct);
            await SendApplicationCardAsync(group,token,"OPL 开口问题提交入口","点击下方按钮反馈项目问题，提交后自动在本群通知。无需PLM账号。",[],link,ct);
            return new(true,"绑定已保存，提交入口卡片已发到群内，可由群主置顶。");
        } catch(Exception error) when(!ct.IsCancellationRequested) {
            logger?.LogWarning("钉钉入口卡片发送失败，群 {GroupId}，原因类型 {ErrorType}",groupId,error.GetType().Name);
            return new(false,"绑定已保存；入口卡片未发送，请检查机器人卡片权限及是否已加入该群，然后重发入口。");
        }
    }
    // 按UTF-8大小拆分，单个长字段也不能超过钉钉单条消息限制。
    private static IReadOnlyList<string> MessageParts(IEnumerable<string> blocks)
    {
        var result = new List<string>(); var current = new StringBuilder(); var bytes = 0;
        foreach (var block in blocks)
        {
            foreach (var rune in (block + "\n\n").EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 15000) { result.Add(current.ToString()); current.Clear(); bytes = 0; }
                current.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
        }
        if (current.Length > 0) result.Add(current.ToString()); return result;
    }
    public async Task<DingTalkNotificationResult> NotifyAsync(OplIssue issue, bool reminder, CancellationToken ct, OplIssue? previous = null)
    {
        var sent = 0; var warnings = new List<string>();
        try
        {
            await LoadAsync(TenantContext.CompanyId, ct);
            if (!NotificationEnabled(issue.ProjectId)) return new(false, "群通知尚未配置，问题已保存。");
            var group = Group(issue.ProjectId)!;
            var root = projects is null ? null : await projects.FindProjectAsync(issue.ProjectId, ct);
            var child = issue.RelatedProjectId is Guid childId && projects is not null ? await projects.FindProjectAsync(childId, ct) : null;
            var owner = string.IsNullOrEmpty(issue.Owner) || projects is null ? null : await projects.FindUserAsync(issue.Owner, ct);
            var projectLabel = root is null ? "项目开口问题" : root.Code + " · " + root.Name;
            var title = previous is not null ? issue.Status == "已完成" && previous.Status != "已完成" ? "OPL 问题已完成" : "OPL 问题已更新"
                : reminder ? "OPL 开口问题处理提醒" : "OPL 有新的问题点提交";
            var changes = new List<string>();
            if (previous is not null)
            {
                if (previous.Status != issue.Status) changes.Add($"实施状态由{previous.Status}改为{issue.Status}");
                if (previous.Priority != issue.Priority) changes.Add($"优先级由{previous.Priority}改为{issue.Priority}");
                if (previous.Owner != issue.Owner) changes.Add("负责人已变更");
                if (previous.Description != issue.Description) changes.Add("问题描述已更新");
                if (previous.Solution != issue.Solution) changes.Add("解决措施已更新");
                if (previous.DueDate != issue.DueDate) changes.Add("要求完成日期已更新");
                if (previous.RelatedProjectId != issue.RelatedProjectId) changes.Add("子项目已变更");
                if (!previous.Attachments.Select(file => file.Id).Order().SequenceEqual(issue.Attachments.Select(file => file.Id).Order())) changes.Add("附件已更新");
                if (changes.Count == 0) return new(false, "问题已保存，内容没有变化，未重复发送群通知。");
            }
            var updatedBy = projects is null ? null : await projects.FindUserAsync(issue.UpdatedBy, ct);
            var values = new Dictionary<string, string> {
                ["所属项目"] = projectLabel, ["子项目"] = child is null ? "项目整体" : child.Code + " · " + child.Name,
                ["问题编号"] = issue.Number.ToString(), ["问题描述"] = issue.Description, ["优先级"] = issue.Priority, ["实施状态"] = issue.Status,
                ["负责人"] = owner?.DisplayName ?? (string.IsNullOrEmpty(issue.Owner) ? "待分派" : issue.Owner), ["提交人"] = issue.CreatedByDisplayName ?? issue.CreatedBy,
                ["要求完成日期"] = issue.DueDate?.ToString("yyyy-MM-dd") ?? "未填写", ["解决措施"] = issue.Solution,
                ["提交日期"] = issue.CreatedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm"), ["完成日期"] = issue.CompletedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm") ?? "",
                ["更新人"] = updatedBy?.DisplayName ?? issue.UpdatedBy, ["变更内容"] = string.Join("；", changes), ["附件"] = string.Join("、", issue.Attachments.Select(file => file.Name))
            };
            var blocks = DingTalkOplMessageTemplate.Render(Settings.NotificationTemplate, values).Select(Markdown).ToList();
            if (blocks.Count == 0) return new(false, "问题已保存；消息模板没有可发送的内容，请调整钉钉应用中的消息模板。");
            var token = await ApplicationTokenAsync(ct);
            var imageIds = new List<string>();
            foreach (var file in issue.Attachments.Where(file => ImageTypes.ContainsKey(Path.GetExtension(file.Name))))
            {
                try { var media = await UploadImageAsync(issue, file, token!, ct); imageIds.Add(media); }
                catch (Exception error) when (!ct.IsCancellationRequested) { logger?.LogWarning("钉钉图片上传未完成，问题 {IssueId} 附件 {AttachmentId}，原因类型 {ErrorType}",issue.Id,file.Id,error.GetType().Name); warnings.Add($"图片“{file.Name}”未推送，请检查机器人媒体上传权限或图片格式"); }
            }
            if (issue.Attachments.Any(file => new[] { ".webp", ".heic", ".heif", ".svg" }.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))) warnings.Add("部分图片格式不受钉钉媒体接口支持，请使用JPG、PNG、GIF或BMP");
            var link = Settings.PublicUrl.TrimEnd('/') + "/?dingtalkOpl=" + issue.ProjectId + "&companyId=" + Settings.CompanyId
                + (Settings.Bindings.TryGetValue(issue.ProjectId, out var groupId) ? "&groupId=" + groupId : "");
            var parts = MessageParts(blocks);
            for (var i=0; i<parts.Count; i++)
            {
                var partTitle = parts.Count == 1 ? title : $"{title}（{i+1}/{parts.Count}）";
                await SendApplicationCardAsync(group,token,partTitle,parts[i],i==parts.Count-1 ? imageIds : [],link,ct);
                sent++;
            }
            return new(true, warnings.Count == 0 ? "群通知已发送。" : "群通知已发送；" + string.Join("；", warnings) + "。问题已保存，无需重复提交。");
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("钉钉通知未完成，问题 {IssueId}，已发送 {SentCount} 条，原因类型 {ErrorType}",issue.Id,sent,error.GetType().Name);
            return new(false,(sent>0 ? "部分群消息已发送，后续消息未完成" : "群通知未发送") + "，请检查企业机器人、群登记状态及互动卡片权限。问题已保存，无需重复提交。");
        }
    }
}
