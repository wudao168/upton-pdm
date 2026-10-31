using System.Security.Claims;
using QRCoder;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record ReceiveProductionRelease(Guid RequestId);

public static class ProductionTrackingEndpoints
{
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)
                ?? throw new UnauthorizedAccessException("角色信息无效。")));

    public static void MapProductionTrackingEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/production-tracking").RequireAuthorization();
        api.MapGet("/scan-settings", async (HttpContext context, ProductionScanService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReadAsync(actor, role, ct));
        });
        api.MapPut("/scan-settings", async (SaveProductionScanCatalog input, HttpContext context, ProductionScanService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.SaveAsync(input, actor, role, ct));
        });
        api.MapGet("/scan-settings/options", async (HttpContext context, ProductionScanService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.OptionsAsync(actor, role, ct));
        });
        api.MapGet("/releases/{id:guid}/actions", async (Guid id, HttpContext context, ProductionTrackingService tracking, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await tracking.ActionsAsync(id, actor, role, ct));
        });
        api.MapGet("/projects", async (HttpContext context, ProductionTrackingService service, CancellationToken ct, bool selfOnly = false, bool externalOnly = false) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ListAsync(actor, role, ct, selfOnly, externalOnly));
        });
        api.MapGet("/releases/{id:guid}", async (Guid id, HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.FindAsync(id, actor, role, ct));
        });
        api.MapGet("/releases/{id:guid}/external-suppliers", async (Guid id, HttpContext context,
            ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ExternalSupplierChoicesAsync(id, actor, role, ct));
        });
        api.MapGet("/releases/{id:guid}/opening-preview", async (Guid id, HttpContext context,
            ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.PreviewExistingAsync(id, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/include-existing", async (Guid id,
            IncludeExistingProductionRelease input, HttpContext context,
            ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.IncludeExistingAsync(id, input, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/receive", async (Guid id, ReceiveProductionRelease input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReceiveAsync(id, input.RequestId, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/lots", async (Guid id, AssignProductionLot input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.AssignAsync(id, input, actor, role, ct));
        });
        api.MapPut("/releases/{id:guid}/lot-allocation", async (Guid id, UpdateProductionLotAllocation input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.UpdateAllocationAsync(id, input, actor, role, ct));
        });
        api.MapPut("/releases/{id:guid}/lot-allocations", async (Guid id, BatchProductionAllocation input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.BatchAllocateAsync(id, input, actor, role, ct));
        });
        api.MapPut("/releases/{id:guid}/delivery", async (Guid id, UpdateProductionLineDelivery input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.UpdateDeliveryAsync(id, input, actor, role, ct));
        });
        api.MapPut("/releases/{id:guid}/lot-promise", async (Guid id, UpdateProductionLotPromise input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.UpdateLotPromiseAsync(id, input, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/cancel-unassigned-demand", async (Guid id, CancelUnassignedProductionDemand input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.CancelUnassignedDemandAsync(id, input, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/movements", async (Guid id, RecordProductionMovement input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.RecordAsync(id, input, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/revision-decisions", async (Guid id,
            ResolveProductionRevision input, HttpContext context, ProductionTrackingService service,
            CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ResolveRevisionAsync(id, input, actor, role, ct));
        });
        api.MapPost("/releases/{id:guid}/schedule", async (Guid id, ProductionScheduleInput input,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ScheduleAsync(id, input, actor, role, ct));
        });
        api.MapGet("/capacity", async (HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var (calendar, usage) = await service.CapacityAsync(actor, role, ct);
            return Results.Ok(new { calendar, usage });
        });
        api.MapPut("/capacity", async (ProductionCapacityInput input, HttpContext context,
            ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.SetCapacityAsync(input, actor, role, ct));
        });
        api.MapGet("/scan", async (string code, HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ResolveScanAsync(code, actor, role, ct));
        });
        api.MapGet("/releases/{id:guid}/lots/{lotId:guid}/card-qr", async (Guid id, Guid lotId,
            HttpContext context, ProductionTrackingService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var record = await service.FindAsync(id, actor, role, ct);
            if (!record.State.Lots.Any(item => item.Id == lotId))
                throw new PdmNotFoundException("生产批次不存在，无法打印流转卡。");
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode($"UPLM-PRODUCTION|{id:N}|{lotId:N}", QRCodeGenerator.ECCLevel.Q);
            using var qr = new SvgQRCode(data);
            return Results.Content(qr.GetGraphic(8), "image/svg+xml");
        });
    }
}
