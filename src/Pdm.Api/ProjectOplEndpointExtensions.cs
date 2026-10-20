using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectOplEndpointExtensions
{
    public static void MapProjectOplEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/opl").RequireAuthorization();
        api.MapGet("", async (Guid projectId, bool? includePeople, HttpContext context, ProjectOplService service, ProjectOplViewState viewed, CancellationToken ct) =>
        {
            var view = await service.GetAsync(projectId, Actor(context), Role(context), ct, includePeople ?? true);
            var unread = await viewed.UnreadAsync(view, Actor(context), ct);
            return Results.Ok(new { Rows = view.Rows.Select(row => new { row.Issue, row.CanEdit, row.CanComplete, row.Releases, IsNew = unread.Contains(row.Issue.Id) }), view.People, view.RootProject, view.Subprojects });
        });
        api.MapPost("", (Guid projectId, HttpContext context, ProjectOplService service, CancellationToken ct) => Save(projectId, null, context, service, ct));
        api.MapPost("/integrations/dingtalk/submit", async (Guid projectId, HttpContext context, ProjectOplService service, DingTalkOplIntegration integration, CancellationToken ct) =>
        {
            var view = await service.GetAsync(projectId, Actor(context), Role(context), ct, false);
            await integration.LoadAsync(TenantContext.CompanyId, ct);
            if (!integration.SubmissionEnabled(view.RootProject.Id)) throw new PdmRuleException("钉钉提交尚未启用，请联系管理员。");
            return await Save(projectId, null, context, service, ct, "DingTalk");
        });
        api.MapPut("/{id:guid}", (Guid projectId, Guid id, HttpContext context, ProjectOplService service, CancellationToken ct) => Save(projectId, id, context, service, ct));
        api.MapPost("/{id:guid}/view", async (Guid projectId, Guid id, HttpContext context, ProjectOplService service, ProjectOplViewState viewed, CancellationToken ct) =>
        {
            var view = await service.GetAsync(projectId, Actor(context), Role(context), ct, false);
            var issue = view.Rows.SingleOrDefault(row => row.Issue.Id == id)?.Issue;
            if (issue is null || (projectId != view.RootProject.Id && issue.RelatedProjectId != projectId)) throw new PdmNotFoundException("当前项目中没有该开口问题。");
            await viewed.MarkAsync(id, Actor(context), ct);
            return Results.NoContent();
        });
        api.MapPost("/{id:guid}/complete", async (Guid projectId, Guid id, CompleteOplInput input, HttpContext context, ProjectOplService service, CancellationToken ct) =>
        {
            var previous = input.NotifyGroup ? (await service.GetAsync(projectId, Actor(context), Role(context), ct, false)).Rows.SingleOrDefault(row => row.Issue.Id == id)?.Issue : null;
            var issue = await service.CompleteAsync(projectId, id, input.ExpectedRowVersion, Actor(context), Role(context), ct);
            var notification = input.NotifyGroup ? await context.RequestServices.GetRequiredService<DingTalkOplIntegration>().NotifyAsync(issue, false, ct, previous) : null;
            return MutationResult(issue, notification);
        });
        api.MapGet("/{id:guid}/attachments/{attachmentId:guid}", async (Guid projectId, Guid id, Guid attachmentId, HttpContext context, ProjectOplService service, CancellationToken ct) =>
        {
            var (file, content) = await service.OpenAsync(projectId, id, attachmentId, Actor(context), Role(context), ct);
            return Results.File(content, "application/octet-stream", file.Name, enableRangeProcessing: true);
        });
        api.MapGet("/integrations/dingtalk", async (Guid projectId, HttpContext context, ProjectOplService service, DingTalkOplIntegration integration, CancellationToken ct) =>
        {
            var view = await service.GetAsync(projectId, Actor(context), Role(context), ct, false);
            var rootId = view.RootProject.Id;
            await integration.LoadAsync(TenantContext.CompanyId, ct);
            return Results.Ok(new { enabled = integration.SubmissionEnabled(rootId), submissionEnabled = integration.SubmissionEnabled(rootId), notificationEnabled = integration.NotificationEnabled(rootId), message = "钉钉企业应用免登录提交与群机器人通知需由管理员配置。" });
        });
    }
    internal static async Task<(T Input, List<OplUpload> Uploads)> ReadFormAsync<T>(HttpContext context, CancellationToken ct) where T : class
    {
        const long max = 32 * 1024 * 1024;
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = max;
        if (context.Request.ContentLength > max) throw new PdmRuleException("单次上传附件总大小不能超过30MB。");
        if (!context.Request.HasFormContentType) throw new PdmRuleException("提交格式无效，请刷新页面后重试。");
        var form = await context.Request.ReadFormAsync(ct);
        T input;
        try { input = JsonSerializer.Deserialize<T>(form["data"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException(); }
        catch (JsonException) { throw new PdmRuleException("问题内容格式无效，请检查日期和必填项。"); }
        if (form.Files.Count > 10 || form.Files.Any(x => x.Length > ProjectOplService.MaxFileLength) || form.Files.Sum(x => x.Length) > 30 * 1024 * 1024) throw new PdmRuleException("最多10个附件，单个不超过10MB，单次总大小不超过30MB。");
        var uploads = new List<OplUpload>();
        foreach (var file in form.Files)
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, ct);
            uploads.Add(new(file.FileName, stream.ToArray()));
        }
        return (input, uploads);
    }
    private static async Task<IResult> Save(Guid projectId, Guid? id, HttpContext context, ProjectOplService service, CancellationToken ct, string source = "Manual")
    {
        var (input, uploads) = await ReadFormAsync<OplInput>(context, ct);
        var previous = id is not null && input.NotifyGroup ? (await service.GetAsync(projectId, Actor(context), Role(context), ct, false)).Rows.SingleOrDefault(row => row.Issue.Id == id)?.Issue : null;
        var issue = await service.SaveAsync(projectId, id, input, uploads, Actor(context), Role(context), ct, source);
        DingTalkNotificationResult? notification = null;
        if (id is null || input.NotifyGroup)
        {
            var integration = context.RequestServices.GetRequiredService<DingTalkOplIntegration>();
            notification = await integration.NotifyAsync(issue, false, ct, previous);
            context.Response.Headers["X-OPL-Notification"] = notification.Sent ? "sent" : "not-sent";
        }
        return MutationResult(issue, notification);
    }
    private static IResult MutationResult(OplIssue issue, DingTalkNotificationResult? notification)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var value = JsonSerializer.SerializeToNode(issue, json)!;
        if (notification is not null) value["notification"] = JsonSerializer.SerializeToNode(notification, json);
        return Results.Ok(value);
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
