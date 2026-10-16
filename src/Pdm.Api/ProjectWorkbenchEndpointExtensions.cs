using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectWorkbenchEndpointExtensions
{
    public static void MapProjectWorkbenchEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/project-calendar", async (HttpContext context, ProjectWorkbenchService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(null, Actor(context), Role(context), ct, calendarOnly: true)));
        api.MapGet("/workbench", async (string? view, string? scope, bool? summaryOnly, Guid? projectId, HttpContext context, ProjectWorkbenchService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(view, Actor(context), Role(context), ct, scope, summaryOnly ?? false, projectId)));
        api.MapPost("/projects/{projectId:guid}/risks", async (Guid projectId, RegisterWorkbenchRisk command, HttpContext context, ProjectWorkbenchService service, CancellationToken ct) =>
            Results.Ok(await service.RegisterRiskAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPost("/workbench/risks/{id:guid}/close", async (Guid id, HttpContext context, ProjectWorkbenchService service, CancellationToken ct) =>
        {
            await service.CloseRiskAsync(id, Actor(context), Role(context), ct);
            return Results.NoContent();
        });
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
