using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record UpdateU9InventorySettingsRequest(
    bool AutoSyncEnabled,
    int SyncIntervalMinutes,
    string QueryPath);

public static class InventoryEndpointExtensions
{
    public static void MapPdmInventoryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/material-inventory", async (
            string? materialCode,
            string? itemName,
            string? specification,
            string? brand,
            string? warehouse,
            string? projectCode,
            string? subproject,
            bool? positiveStockOnly,
            int? page,
            int? pageSize,
            HttpContext context,
            U9InventoryService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ListAsync(new(
                materialCode, itemName, specification, brand, warehouse, projectCode, subproject,
                positiveStockOnly ?? true, page ?? 1, pageSize ?? 50), actor, role, cancellationToken);
            return Results.Ok(MapPage(result));
        });

        api.MapPost("/material-inventory/{materialCode}/refresh", async (
            string materialCode,
            HttpContext context,
            U9InventoryService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapPage(await service.RefreshMaterialAsync(materialCode, actor, role, cancellationToken)));
        });

        api.MapGet("/u9-inventory-sync/status", async (
            HttpContext context,
            U9InventoryService service,
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

        api.MapPut("/u9-inventory-sync/settings", async (
            UpdateU9InventorySettingsRequest request,
            HttpContext context,
            U9InventoryService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapSettings(await service.UpdateSettingsAsync(
                request.AutoSyncEnabled,
                request.SyncIntervalMinutes,
                request.QueryPath,
                actor,
                role,
                cancellationToken)));
        });

        api.MapPost("/u9-inventory-sync/run", async (
            HttpContext context,
            U9InventoryService service,
            U9InventorySyncCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            _ = await service.GetSettingsAsync(actor, role, cancellationToken);
            if (!coordinator.TryStart(actor, "Manual"))
                return Results.Conflict(new { Message = "已有U9C库存全量刷新正在运行，请稍后刷新状态。" });
            return Results.Accepted("/api/u9-inventory-sync/status", new { Message = "U9C库存全量刷新已在后台启动。" });
        });
    }

    private static object MapPage(U9InventoryPage result) => new
    {
        Items = result.Items.Select(row => new
        {
            row.OrganizationCode,
            row.WarehouseCode,
            row.WarehouseName,
            row.MaterialCode,
            row.ItemName,
            row.Brand,
            row.Specification,
            row.ProjectCode,
            row.ProjectName,
            row.Subproject,
            row.StockQuantity,
            row.AvailableQuantity,
            row.ReservedQuantity,
            row.UnavailableQuantity,
            row.BinCode,
            row.BinName,
            row.StorageType,
            row.RefreshedAt
        }),
        result.Total,
        result.Page,
        result.PageSize,
        result.WarehouseNames,
        result.BrandNames,
        result.ProjectCodes,
        result.SubprojectOptions,
        result.LastSuccessfulRefreshAt
    };

    private static object MapSettings(U9InventorySyncSettings settings) => new
    {
        settings.AutoSyncEnabled,
        settings.SyncIntervalMinutes,
        settings.QueryPath,
        settings.UpdatedBy,
        settings.UpdatedAt
    };

    private static object MapRun(U9InventorySyncRun run) => new
    {
        run.Id,
        run.TriggerKind,
        Status = run.Status.ToString(),
        run.SourceRowCount,
        run.StoredRowCount,
        run.MaterialCount,
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
