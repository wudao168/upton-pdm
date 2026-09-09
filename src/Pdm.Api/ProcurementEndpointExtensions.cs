using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record UpdateU9ProcurementSettingsRequest(
    bool AutoSyncEnabled,
    int SyncIntervalMinutes,
    string QueryPath);

public static class ProcurementEndpointExtensions
{
    public static void MapPdmProcurementEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/projects/{projectId:guid}/procurement-tracking", async (
            Guid projectId,
            HttpContext context,
            U9ProcurementService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListProjectAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/procurement-tracking/refresh", async (
            Guid projectId,
            HttpContext context,
            U9ProcurementService service,
            U9ProcurementSyncCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DemandProjectViewAsync(projectId, actor, role, cancellationToken);
            if (!coordinator.TryStart(actor, "Manual"))
                return Results.Conflict(new { Message = "已有U9C采购跟踪刷新正在运行，请稍后再试。" });
            return Results.Accepted($"/api/projects/{projectId}/procurement-tracking", new { Message = "U9C请购、采购状态只读刷新已在后台启动。" });
        });

        api.MapGet("/u9-procurement-sync/status", async (
            HttpContext context,
            U9ProcurementService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var settings = await service.GetSettingsAsync(actor, role, cancellationToken);
            var latestRun = await service.GetLatestRunAsync(cancellationToken);
            return Results.Ok(new
            {
                Settings = MapSettings(settings),
                LatestRun = latestRun is null ? null : MapRun(latestRun)
            });
        });

        api.MapPut("/u9-procurement-sync/settings", async (
            UpdateU9ProcurementSettingsRequest request,
            HttpContext context,
            U9ProcurementService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapSettings(await service.UpdateSettingsAsync(
                request.AutoSyncEnabled, request.SyncIntervalMinutes, request.QueryPath,
                actor, role, cancellationToken)));
        });

        api.MapPost("/u9-procurement-sync/run", async (
            HttpContext context,
            U9ProcurementService service,
            U9ProcurementSyncCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            _ = await service.GetSettingsAsync(actor, role, cancellationToken);
            if (!coordinator.TryStart(actor, "Manual"))
                return Results.Conflict(new { Message = "已有U9C采购跟踪刷新正在运行，请稍后再试。" });
            return Results.Accepted("/api/u9-procurement-sync/status", new { Message = "U9C请购、采购状态只读刷新已在后台启动。" });
        });
    }

    private static object MapSettings(U9ProcurementSyncSettings settings) => new
    {
        settings.AutoSyncEnabled,
        settings.SyncIntervalMinutes,
        settings.QueryPath,
        settings.UpdatedBy,
        settings.UpdatedAt
    };

    private static object MapRun(U9ProcurementSyncRun run) => new
    {
        run.Id,
        run.TriggerKind,
        Status = run.Status.ToString(),
        run.SourceRowCount,
        run.StoredRowCount,
        run.ProjectCount,
        run.LastError,
        run.StartedAt,
        run.CompletedAt
    };

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}
