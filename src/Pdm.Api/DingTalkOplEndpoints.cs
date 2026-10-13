using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class DingTalkOplEndpoints
{
    public static void MapDingTalkOplEndpoints(this WebApplication app)
    {
        app.MapGet("/api/integrations/dingtalk/opl/{projectId:guid}/bootstrap", async (Guid projectId, Guid? companyId, DingTalkOplIntegration integration, CancellationToken ct) => {
            await integration.LoadAsync(companyId, ct);
            return Results.Ok(new { enabled = integration.SubmissionEnabled(projectId), corpId = integration.SubmissionEnabled(projectId) ? integration.Settings.CorpId : "", companyId = integration.Settings.CompanyId });
        }).AllowAnonymous();
        app.MapGet("/api/integrations/dingtalk/opl/bootstrap", async (Guid? companyId, DingTalkOplIntegration integration, CancellationToken ct) => {
            await integration.LoadAsync(companyId, ct);
            return Results.Ok(new { enabled = integration.ApplicationEnabled, corpId = integration.ApplicationEnabled ? integration.Settings.CorpId : "", companyId = integration.Settings.CompanyId });
        }).AllowAnonymous();
        app.MapPost("/api/integrations/dingtalk/opl/login", async (DingTalkLoginInput input, DingTalkOplIntegration integration,
            DingTalkVisitorSession sessions, CancellationToken ct) =>
        {
            await integration.LoadAsync(input.CompanyId, ct);
            if (input.GroupId is Guid group && !integration.Settings.Groups.ContainsKey(group)) throw new PdmRuleException("群入口无效，请在项目设置中重新获取入口。");
            var visitor = await integration.ResolveVisitorAsync(input.ProjectId, input.Code, ct);
            return Results.Ok(new { guestToken = sessions.Issue(integration.Settings, input.GroupId, visitor), displayName = visitor.Name });
        }).AllowAnonymous();
        app.MapDingTalkVisitorEndpoints();
        app.MapGet("/api/integrations/dingtalk/opl/projects", async (Guid? groupId, HttpContext context, IPdmRepository repository, DingTalkOplIntegration integration, CancellationToken ct) => {
            await integration.LoadAsync(TenantContext.CompanyId, ct);
            var (actor, role) = User(context);
            var projects = await repository.ListProjectsForUserAsync(actor, role, ct);
            return Results.Ok(projects.Where(x => x.ParentProjectId is null && x.IsActive && x.CanReadContent && integration.SubmissionEnabled(x.Id)
                && (groupId is null || integration.Settings.Bindings.GetValueOrDefault(x.Id) == groupId)).OrderBy(x => x.Code).Select(x => new { x.Id, x.Code, x.Name }));
        }).RequireAuthorization();
        app.MapGet("/api/settings/dingtalk", async (HttpContext context, DingTalkConfigurationService service, CancellationToken ct) => { var (actor, role) = User(context); return Results.Ok(await service.GetApplicationAsync(actor, role, ct)); }).RequireAuthorization();
        app.MapPut("/api/settings/dingtalk", async (SaveDingTalkApplication input, HttpContext context, DingTalkConfigurationService service, CancellationToken ct) => { var (actor, role) = User(context); return Results.Ok(await service.SaveApplicationAsync(input, actor, role, ct)); }).RequireAuthorization();
        app.MapGet("/api/projects/{projectId:guid}/dingtalk", async (Guid projectId, HttpContext context, DingTalkConfigurationService service, CancellationToken ct) => { var (actor, role) = User(context); return Results.Ok(await service.GetBindingAsync(projectId, actor, role, ct)); }).RequireAuthorization();
        app.MapPut("/api/projects/{projectId:guid}/dingtalk", async (Guid projectId, SaveDingTalkBinding input, HttpContext context, DingTalkConfigurationService service, DingTalkOplIntegration integration, CancellationToken ct) => {
            var (actor,role)=User(context);
            var saved=await service.SaveBindingAsync(projectId,input,actor,role,ct);
            var view=System.Text.Json.JsonSerializer.SerializeToNode(saved,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            if(input.GroupId is Guid group) view["entryMessage"]=(await integration.PublishEntryAsync(group,ct)).Message;
            return Results.Ok(view);
        }).RequireAuthorization();
        app.MapPost("/api/projects/{projectId:guid}/dingtalk/entry",async (Guid projectId,HttpContext context,DingTalkConfigurationService service,DingTalkOplIntegration integration,CancellationToken ct)=>{
            var (actor,role)=User(context); var group=await service.EntryGroupAsync(projectId,actor,role,ct);
            return Results.Ok(await integration.PublishEntryAsync(group,ct));
        }).RequireAuthorization();
        app.MapPost("/api/projects/{projectId:guid}/opl/{id:guid}/notify", async (Guid projectId, Guid id, HttpContext context,
            ProjectOplService opl, DingTalkOplIntegration integration, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = context.User.Identity!.Name!;
            var role = Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!);
            var view = await opl.GetAsync(projectId, actor, role, ct, false);
            var row = view.Rows.SingleOrDefault(row => row.Issue.Id == id) ?? throw new PdmNotFoundException("开口问题不存在。");
            if (!row.CanEdit) throw new UnauthorizedAccessException("仅提交人、负责人或项目经理可以发送问题提醒。");
            if (row.Issue.Status is "已完成" or "已作废") throw new PdmRuleException("已关闭的问题无需催办。");
            var result = await integration.NotifyAsync(row.Issue, true, ct);
            await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "project.opl.dingtalk.notify", nameof(OplIssue), id.ToString(), result.Message), ct);
            return Results.Ok(result);
        }).RequireAuthorization();
    }
    private static (string Actor, UserRole Role) User(HttpContext context) => (context.User.Identity!.Name!, Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!));
}
