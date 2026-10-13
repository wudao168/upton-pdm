using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class DingTalkVisitorEndpoints
{
    public static void MapDingTalkVisitorEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/integrations/dingtalk/opl/guest").AllowAnonymous();
        api.MapGet("/projects", (HttpContext context, DingTalkVisitorSession sessions, DingTalkOplIntegration integration, IPdmRepository repository, CancellationToken ct) =>
            InSession(context, sessions, integration, async (ticket, _) => {
                var projects = (await repository.ListProjectsAsync(ct)).Where(x => x.OrganizationId == ticket.CompanyId && x.IsActive).ToArray();
                return Results.Ok(projects.Where(x => x.ParentProjectId is null && IsBound(x.Id, ticket, integration)).OrderBy(x => x.Code)
                    .Select(x => new { x.Id, x.Code, x.Name, subprojects = projects.Where(child => child.ParentProjectId is not null && (child.RootProjectId ?? child.ParentProjectId) == x.Id)
                        .OrderBy(child => child.Code).Select(child => new { child.Id, child.Code, child.Name }) }));
            }, ct));
        api.MapPost("/projects/{projectId:guid}/submit", (Guid projectId, HttpContext context, DingTalkVisitorSession sessions, DingTalkOplIntegration integration,
            IPdmRepository repository, ProjectOplService opl, CancellationToken ct) =>
            InSession(context, sessions, integration, async (ticket, actor) => {
                var root = await repository.FindProjectAsync(projectId, ct);
                if (root is null || root.OrganizationId != ticket.CompanyId || root.ParentProjectId is not null || !root.IsActive || !IsBound(projectId, ticket, integration))
                    throw new PdmRuleException("请选择当前入口对应的项目，或重新打开群入口。");
                var (input, uploads) = await ProjectOplEndpointExtensions.ReadFormAsync<OplVisitorInput>(context, ct);
                var issue = await opl.SubmitVisitorAsync(root, input, uploads, actor, ticket.Name, ct);
                var notification = await integration.NotifyAsync(issue, false, ct);
                return Results.Ok(new { issue.Number, submittedBy = ticket.Name, notificationSent = notification.Sent, notificationMessage = notification.Message });
            }, ct));
        api.MapGet("/projects/{projectId:guid}/people", (Guid projectId, HttpContext context, DingTalkVisitorSession sessions, DingTalkOplIntegration integration, IPdmRepository repository, CancellationToken ct) =>
            InSession(context, sessions, integration, async (ticket, _) => {
                var root = await repository.FindProjectAsync(projectId, ct);
                if (root is null || root.OrganizationId != ticket.CompanyId || root.ParentProjectId is not null || !root.IsActive || !IsBound(projectId, ticket, integration)) throw new PdmRuleException("请选择当前入口对应的项目。");
                var people = new List<OplPerson>();
                foreach (var user in await repository.ListUsersAsync(ct))
                    if (user.IsActive && await repository.HasProjectContentReadAccessAsync(root.Id, user.Username, user.Role, ct)) people.Add(new(user.Username, user.DisplayName));
                return Results.Ok(people);
            }, ct));
    }
    private static bool IsBound(Guid projectId, DingTalkVisitorTicket ticket, DingTalkOplIntegration integration) => integration.SubmissionEnabled(projectId)
        && (ticket.GroupId is null || integration.Settings.Bindings.GetValueOrDefault(projectId) == ticket.GroupId);
    private static async Task<IResult> InSession(HttpContext context, DingTalkVisitorSession sessions, DingTalkOplIntegration integration,
        Func<DingTalkVisitorTicket, string, Task<IResult>> action, CancellationToken ct)
    {
        var ticket = sessions.Read(context);
        await integration.LoadAsync(ticket.CompanyId, ct);
        if (!integration.ApplicationEnabled || integration.Settings.CorpId != ticket.CorpId || ticket.GroupId is Guid group && !integration.Settings.Groups.ContainsKey(group))
            throw new UnauthorizedAccessException("群提交入口已变更或停用，请重新打开入口。");
        var actor = "dingtalk:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket.CorpId + "\n" + ticket.UserId)))[..32];
        var previous = TenantContext.Current;
        TenantContext.Set(new(Guid.Empty, ticket.CompanyId, ticket.CompanyId, actor, "dingtalk_visitor", false, new HashSet<string>()));
        try { return await action(ticket, actor); }
        finally { if (previous is null) TenantContext.Clear(); else TenantContext.Set(previous); }
    }
}
