using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class StockedMaterialEndpoints
{
    public static void MapStockedMaterialEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/stocked-materials").RequireAuthorization();
        api.MapGet("/", async (Guid? categoryId, string? query, string? brand, int? page, int? pageSize,
            HttpContext context, StockedMaterialService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var scope = await service.RequireScopeAsync(actor, role, false, ct);
            var result = await service.ListAsync(scope, categoryId, query, brand, page ?? 1, pageSize ?? 50, ct);
            return Results.Ok(new { items = result.Items.Select(item => new { material = MaterialEndpointExtensions.MapMaterial(item.Material),
                item.Categories, item.Snapshot, item.ManagementOnly, coverImage = item.CoverImage is null ? null : MaterialEndpointExtensions.MapAttachment(item.CoverImage) }),
                result.Total, result.LowStockCount, result.FormalCount, result.DraftCount, result.ManagementOnlyCount,
                result.Page, result.PageSize, result.Scope, result.LastSuccessAt, result.LastError,
                canSynchronize = TenantContext.Current?.HasRole("StandardizationAssistant") == true,
                canManageSettings = TenantContext.Current?.HasPermission(PermissionCodes.StorageSettingsManage) == true });
        });
        api.MapPost("/memberships", async (AddStandardLibraryMaterialsRequest request, HttpContext context, StockedMaterialService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context); var scope = await service.RequireScopeAsync(actor, role, true, ct);
            await service.AddAsync(scope, new(request.CategoryIds, request.MaterialIds), actor, ct); return Results.NoContent();
        });
        api.MapDelete("/{materialId:guid}", async (Guid materialId, Guid? categoryId, HttpContext context, StockedMaterialService service,
            ILogger<StockedMaterialService> logger, CancellationToken ct) =>
        {
            var (actor, role) = User(context); var scope = await service.RequireScopeAsync(actor, role, true, ct);
            try { await service.RemoveAsync(scope, materialId, categoryId, actor, ct); return Results.NoContent(); }
            catch (Exception error) when (error is not (PdmRuleException or PdmConflictException or PdmNotFoundException or UnauthorizedAccessException or OperationCanceledException))
            { logger.LogError(error, "Stock cancellation failed for {MaterialId}", materialId); throw new PdmRuleException("取消常备未完成，已保留PLM收录，请刷新回查U9C结果，并检查接口连接后重试。"); }
        });
        api.MapPut("/{materialId:guid}/plan", async (Guid materialId, SaveStockedPlan request, HttpContext context,
            StockedMaterialService service, ILogger<StockedMaterialService> logger, CancellationToken ct) =>
        {
            var (actor, role) = User(context); var scope = await service.RequireScopeAsync(actor, role, true, ct);
            try { return Results.Ok(await service.MaintainAsync(scope, materialId, request, actor, ct)); }
            catch (Exception error) when (error is not (PdmRuleException or PdmConflictException or PdmNotFoundException or UnauthorizedAccessException or OperationCanceledException))
            {
                logger.LogError(error, "Stock plan write/readback failed for {MaterialId}", materialId);
                throw new PdmRuleException("库存规划同步未完成，请刷新回查U9C结果，并检查接口连接后重试。");
            }
        });
        api.MapPost("/{materialId:guid}/read-plan", async (Guid materialId, HttpContext context, StockedMaterialService service, ILogger<StockedMaterialService> logger, CancellationToken ct) =>
        {
            var (actor, role) = User(context); var scope = await service.RequireScopeAsync(actor, role, true, ct);
            try { return Results.Ok(await service.ReadPlanForMaintenanceAsync(scope, materialId, actor, ct)); }
            catch (Exception error) when (error is not (PdmRuleException or PdmConflictException or PdmNotFoundException or UnauthorizedAccessException or OperationCanceledException))
            { logger.LogError(error, "Stock plan readback for maintenance failed"); throw new PdmRuleException("库存规划读取失败，请检查U9C连接后重新维护。"); }
        });
        api.MapPost("/refresh", async (HttpContext context, StockedMaterialService service, IPdmRepository repository, ILogger<StockedMaterialService> logger, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (TenantContext.Current?.HasRole("StandardizationAssistant") != true
                && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct))
                throw new UnauthorizedAccessException("请由标准化助理执行立即同步，或由系统设置管理员在设置页操作。");
            var scope = await service.RequireScopeAsync(actor, role, false, ct);
            try { await service.SynchronizeAsync(scope, actor, ct); return Results.Ok(await service.ListAsync(scope, null, null, null, 1, 50, ct)); }
            catch (Exception error) when (error is not (PdmRuleException or PdmConflictException or UnauthorizedAccessException or OperationCanceledException))
            {
                logger.LogError(error, "Stocked material readback failed");
                throw new PdmRuleException("常备件回读未完成，已保留上次成功数据，请检查U9C连接后重试。");
            }
        });
        api.MapGet("/settings", async (HttpContext context, StockedMaterialService service, IPdmRepository repository, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) throw new UnauthorizedAccessException("当前角色无权查看常备件设置。");
            var scope = await service.RequireScopeAsync(actor, role, false, ct); return Results.Ok(await service.SettingsAsync(scope, ct));
        });
        api.MapPut("/settings", async (SaveStockedSettings request, HttpContext context, StockedMaterialService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context); var scope = await service.RequireScopeAsync(actor, role, false, ct);
            return Results.Ok(await service.SaveSettingsAsync(scope, request.Settings, request.ExpectedRowVersion, actor, role, ct));
        });
    }
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请重新登录。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("登录角色无效，请重新登录。")));
}
public sealed record SaveStockedSettings(StockedMaterialSettings Settings, long ExpectedRowVersion);
