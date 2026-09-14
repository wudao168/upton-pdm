using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectContentResetEndpointExtensions
{
    public static void MapProjectContentResetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/content-reset").RequireAuthorization();
        api.MapGet("/readiness", async (Guid projectId, bool includeChildren, HttpContext context, ProjectContentResetService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetReadinessAsync(projectId, includeChildren, actor, role, cancellationToken));
        });
        api.MapPost("", async (Guid projectId, ResetProjectContentRequest request, HttpContext context, ProjectContentResetService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ResetAsync(projectId, request.IncludeChildren, request.Reason, request.Confirmation, actor, role, cancellationToken));
        });
        api.MapPost("/{snapshotId:guid}/restore", async (Guid projectId, Guid snapshotId, RestoreProjectContentRequest request, HttpContext context, ProjectContentResetService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.RestoreAsync(projectId, snapshotId, request.Confirmation, actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(role));
    }
}

public sealed record ResetProjectContentRequest(bool IncludeChildren, string Reason, string Confirmation);
public sealed record RestoreProjectContentRequest(string Confirmation);
