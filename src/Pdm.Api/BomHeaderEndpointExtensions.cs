using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class BomHeaderEndpointExtensions
{
    public static void MapPdmBomHeaderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/bom-headers").RequireAuthorization();
        api.MapGet("", async (Guid projectId, HttpContext context, BomHeaderService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListAsync(projectId, actor, role, cancellationToken));
        });
        api.MapPut("/{kind}/material", async (Guid projectId, string kind, BindBomHeaderMaterialRequest request, HttpContext context, BomHeaderService service, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<ProjectBomHeaderKind>(kind, true, out var parsed)) return Results.BadRequest(new { message = "BOM层级类型无效。" });
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.BindMaterialAsync(projectId, parsed, request.MaterialId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });
        api.MapPost("/generate-hierarchy", async (Guid projectId, HttpContext context, BomHeaderService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GenerateHierarchyMaterialsAsync(projectId, actor, role, cancellationToken));
        });
        api.MapPost("/{kind}/u9-preview", async (Guid projectId, string kind, HttpContext context, ProjectBomU9SyncService service, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<ProjectBomHeaderKind>(kind, true, out var parsed)) return Results.BadRequest(new { message = "BOM层级类型无效。" });
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PreviewAsync(projectId, parsed, actor, role, cancellationToken));
        });
        api.MapPost("/{kind}/u9-execute", async (Guid projectId, string kind, ProjectBomU9ExecuteRequest request, HttpContext context, ProjectBomU9SyncService service, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<ProjectBomHeaderKind>(kind, true, out var parsed)) return Results.BadRequest(new { message = "BOM层级类型无效。" });
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ExecuteAsync(
                projectId, parsed, request.RequestSha256, request.Confirmation,
                actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("未登录。");
        var roleText = principal.FindFirst(ClaimTypes.Role)?.Value ?? principal.FindFirst("role")?.Value;
        var role = Enum.TryParse<UserRole>(roleText, true, out var parsed) ? parsed : UserRole.Engineer;
        return (actor, role);
    }

    private sealed record BindBomHeaderMaterialRequest(Guid MaterialId, long ExpectedRowVersion);
    private sealed record ProjectBomU9ExecuteRequest(string RequestSha256, string Confirmation);
}
