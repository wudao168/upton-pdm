using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private async Task<string?> ResolveMentionUserIdAsync(string token, string mobile, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("https://oapi.dingtalk.com/topapi/v2/user/getbymobile?access_token=" + Uri.EscapeDataString(token),new { mobile },ct);
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(json.RootElement.GetProperty("errcode").GetInt32()!=0) return null;
        return json.RootElement.GetProperty("result").GetProperty("userid").GetString();
    }
    private async Task SendApplicationCardAsync(DingTalkOplGroup group, string token, string title, string text, IReadOnlyList<string> images, string? atUserId, string ownerName, string link, CancellationToken ct)
    {
        var contents=new List<object> { new { type="markdown",text,id="content" } };
        if(images.Count>0) contents.Add(new {type="imageList",images,id="photos"});
        contents.Add(new {type="action",id="entry",actions=new[]{new {type="button",id="submit",label=new {type="text",text="提交开口问题"},actionType="openLink",url=new {all=link},status="primary"}}});
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.dingtalk.com/v1.0/im/v1.0/robot/interactiveCards/send");
        request.Headers.Add("x-acs-dingtalk-access-token",token);
        request.Content=JsonContent.Create(new {
            cardTemplateId="StandardCard",robotCode=Settings.RobotCode,openConversationId=group.OpenConversationId,cardBizId="opl-"+clock.GetUtcNow().ToUnixTimeMilliseconds()+"-"+Guid.NewGuid().ToString("N"),pullStrategy=false,
            cardData=JsonSerializer.Serialize(new {config=new {autoLayout=true,enableForward=true},header=new {title=new {type="text",text=title}},contents}),
            sendOptions=new {atAll=false,atUserListJson=JsonSerializer.Serialize(string.IsNullOrWhiteSpace(atUserId) ? Array.Empty<object>() : new object[]{new {userId=atUserId,nickName=ownerName}})}
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
            await SendApplicationCardAsync(group,token,"OPL 开口问题提交入口","点击下方按钮反馈项目问题，提交后自动在本群通知。无需PLM账号。",[],null,"",link,ct);
            return new(true,"绑定已保存，提交入口卡片已发到群内，可由群主置顶。");
        } catch(Exception error) when(!ct.IsCancellationRequested) {
            logger?.LogWarning("钉钉入口卡片发送失败，群 {GroupId}，原因类型 {ErrorType}",groupId,error.GetType().Name);
            return new(false,"绑定已保存；入口卡片未发送，请检查机器人卡片权限及是否已加入该群，然后重发入口。");
        }
    }
    // 保留全部字段，按完整段落拆分，避免超过钉钉单条消息大小。
    private static IReadOnlyList<string> MessageParts(IEnumerable<string> blocks)
    {
        var result = new List<string>(); var current = new StringBuilder();
        foreach (var block in blocks)
        {
            if (current.Length > 0 && Encoding.UTF8.GetByteCount(current.ToString()) + Encoding.UTF8.GetByteCount(block) > 15000) { result.Add(current.ToString()); current.Clear(); }
            current.Append(block).Append("\n\n");
        }
        if (current.Length > 0) result.Add(current.ToString()); return result;
    }
    public async Task<DingTalkNotificationResult> NotifyAsync(OplIssue issue, bool reminder, CancellationToken ct)
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
            var profile = owner is null || projects is null ? null : await projects.FindUserProfileAsync(owner.Username, ct);
            var mobile = Regex.Replace(profile?.MobilePhone ?? "", @"[^\d+]", "");
            if (mobile.StartsWith("+86", StringComparison.Ordinal)) mobile = mobile[3..];
            if (!Regex.IsMatch(mobile, @"^\+?\d{7,15}$")) mobile = "";
            var userId = Settings.Users.FirstOrDefault(pair => string.Equals(pair.Value, issue.Owner, StringComparison.OrdinalIgnoreCase)).Key;
            var projectLabel = root is null ? "项目开口问题" : root.Code + " · " + root.Name;
            var title = reminder ? "OPL 开口问题处理提醒" : "OPL 有新的问题点提交";
            var blocks = new List<string> {
                $"### {title} #{issue.Number}", $"**所属项目：** {Markdown(projectLabel)}", $"**子项目：** {Markdown(child is null ? "项目整体" : child.Code + " · " + child.Name)}",
                $"**问题描述**\n\n{Markdown(issue.Description)}", $"**优先级：** {Markdown(issue.Priority)}　**实施状态：** {Markdown(issue.Status)}",
                $"**负责人：** {Markdown(owner?.DisplayName ?? (string.IsNullOrEmpty(issue.Owner) ? "待分派" : issue.Owner))}", $"**需求完成日期：** {issue.DueDate?.ToString("yyyy-MM-dd") ?? "未填写"}",
                $"**解决措施**\n\n{Markdown(string.IsNullOrWhiteSpace(issue.Solution) ? "未填写" : issue.Solution)}", $"**提交人：** {Markdown(issue.CreatedByDisplayName ?? issue.CreatedBy)}　**提交日期：** {issue.CreatedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm}"
            };
            if (issue.Attachments.Count > 0) blocks.Add("**附件：** " + string.Join("、", issue.Attachments.Select(file => Markdown(file.Name))));
            var token = await ApplicationTokenAsync(ct);
            if (!string.IsNullOrEmpty(issue.Owner) && string.IsNullOrWhiteSpace(userId) && mobile.Length > 0)
            {
                try { userId = await ResolveMentionUserIdAsync(token, mobile, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested) { logger?.LogWarning("钉钉负责人识别失败，问题 {IssueId}，原因类型 {ErrorType}", issue.Id, error.GetType().Name); }
            }
            if (!string.IsNullOrEmpty(issue.Owner) && string.IsNullOrWhiteSpace(userId)) warnings.Add("未能识别负责人的钉钉账户，@未发送；请检查负责人手机号和应用的手机号查询权限");
            var imageIds = new List<string>();
            foreach (var file in issue.Attachments.Where(file => ImageTypes.ContainsKey(Path.GetExtension(file.Name))))
            {
                try { var media = await UploadImageAsync(issue, file, token!, ct); imageIds.Add(media); }
                catch (Exception error) when (!ct.IsCancellationRequested) { logger?.LogWarning("钉钉图片上传未完成，问题 {IssueId} 附件 {AttachmentId}，原因类型 {ErrorType}",issue.Id,file.Id,error.GetType().Name); warnings.Add($"图片“{file.Name}”未推送，请检查机器人媒体上传权限或图片格式"); }
            }
            if (issue.Attachments.Any(file => new[] { ".webp", ".heic", ".heif", ".svg" }.Contains(Path.GetExtension(file.Name).ToLowerInvariant()))) warnings.Add("部分图片格式不受钉钉媒体接口支持，请使用JPG、PNG、GIF或BMP");
            var link = Settings.PublicUrl.TrimEnd('/') + "/?dingtalkOpl=" + issue.ProjectId + "&companyId=" + Settings.CompanyId;
            var parts = MessageParts(blocks);
            for (var i=0; i<parts.Count; i++)
            {
                var partTitle = parts.Count == 1 ? title : $"{title}（{i+1}/{parts.Count}）";
                await SendApplicationCardAsync(group,token,partTitle,parts[i],i==parts.Count-1 ? imageIds : [],i==0 ? userId : null,owner?.DisplayName ?? issue.Owner,link,ct);
                sent++;
            }
            return new(true, warnings.Count == 0 ? "群通知已发送。" : "群通知已发送；" + string.Join("；", warnings) + "。问题已保存，无需重复提交。");
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("钉钉通知未完成，问题 {IssueId}，已发送 {SentCount} 条，原因类型 {ErrorType}",issue.Id,sent,error.GetType().Name);
            return new(false,(sent>0 ? "部分群消息已发送，后续消息或@提醒未完成" : "群通知未发送") + "，请检查企业机器人、群登记状态及互动卡片权限。问题已保存，无需重复提交。");
        }
    }
}
