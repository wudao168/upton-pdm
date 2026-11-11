using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProductionMobileEndpoints
{
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请先登录。"),
        Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? "Engineer"));

    public static void MapProductionMobileEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/production-mobile").RequireAuthorization();
        api.MapGet("/session", async (HttpContext context, IPdmRepository repository, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (!await repository.HasUserPermissionAsync(actor, role, ProductionMobilePermissions.Use, ct))
                throw new UnauthorizedAccessException("尚未授权使用生产执行 App，请联系管理员在权限中心授权。");
            var user = await repository.FindUserAsync(actor, ct);
            return Results.Ok(new { DisplayName = string.IsNullOrWhiteSpace(user?.DisplayName) ? "姓名待补充" : user.DisplayName,
                CanManagePrinters = await repository.HasUserPermissionAsync(actor, role, ProductionMobilePermissions.Printers, ct),
                CanObsolete = await repository.HasUserPermissionAsync(actor, role, ProductionMobilePermissions.Obsolete, ct) });
        });
        api.MapGet("/scan", async (string code, HttpContext context, IPdmRepository repository,
            ProductionTrackingService tracking, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (!await repository.HasUserPermissionAsync(actor, role, ProductionMobilePermissions.Use, ct))
                throw new UnauthorizedAccessException("没有使用生产执行 App 的权限。");
            var results = new List<ProductionScanResult>();
            foreach (var item in await tracking.ResolveScanAsync(code, actor, role, ct))
            {
                var record = await tracking.FindAsync(item.ReleasePackageId, actor, role, ct);
                if (!await AuthorizationPolicy.CanAsync(repository, record.State.ProjectId, actor, ProductionMobilePermissions.Use, ct)) continue;
                await ProductionDocumentGuard.RequireAsync(repository, record.State, item.LineId, ct);
                results.Add(item);
            }
            return Results.Ok(results);
        });
        async Task<ProductionTrackingRecord> Record(Guid id, HttpContext context, IPdmRepository repository,
            ProductionTrackingService tracking, CancellationToken ct)
        {
            var (actor, role) = User(context);
            var record = await tracking.FindAsync(id, actor, role, ct);
            if (TenantContext.CompanyId is Guid company && record.State.CompanyId != company
                || !await AuthorizationPolicy.CanAsync(repository, record.State.ProjectId, actor, ProductionMobilePermissions.Use, ct))
                throw new UnauthorizedAccessException("没有访问该项目生产执行 App 的权限。");
            return record;
        }
        api.MapGet("/releases/{id:guid}", async (Guid id, Guid lineId, HttpContext context, IPdmRepository repository,
            ProductionTrackingService tracking, CancellationToken ct) =>
        {
            var record = await Record(id, context, repository, tracking, ct);
            var (actor, role) = User(context);
            if (record.State.Lines.All(item => item.Id != lineId)) throw new PdmNotFoundException("生产物料不存在。");
            await ProductionDocumentGuard.RequireAsync(repository, record.State, lineId, ct);
            var lots = record.State.Lots.Where(item => item.LineId == lineId).ToArray();
            var lotIds = lots.Select(item => item.Id).ToHashSet();
            var actions = await tracking.ActionsAsync(id, actor, role, ct);
            return Results.Ok(new {
                record.State.ReleasePackageId, record.State.ProjectCode, record.State.PackageNumber,
                Line = record.State.Lines.Single(item => item.Id == lineId), Lots = lots,
                Actions = actions.Actions.Where(item => lotIds.Contains(item.LotId)).Select(item => item with
                    { CreatesPackageLabel = ProductionLabelRules.IsPackaging(record.State, lots.Single(lot => lot.Id == item.LotId), item.Kind) }), actions.Hint,
                CanPrint = await AuthorizationPolicy.CanAsync(repository, record.State.ProjectId, actor, ProductionMobilePermissions.Print, ct),
                CanReprint = await AuthorizationPolicy.CanAsync(repository, record.State.ProjectId, actor, ProductionMobilePermissions.Reprint, ct),
                Movements = record.State.Movements.Where(item => lotIds.Contains(item.LotId)).OrderByDescending(item => item.OccurredAt).Take(100),
                PrintJobs = record.State.PrintJobs.Where(item => record.State.Movements.Any(m => m.Label?.Id == item.LabelId && lotIds.Contains(m.LotId)))
            });
        });
        api.MapPost("/releases/{id:guid}/movements", async (Guid id, RecordProductionMovement input,
            HttpContext context, IPdmRepository repository, ProductionTrackingService tracking, CancellationToken ct) =>
        {
            await Record(id, context, repository, tracking, ct);
            var (actor, role) = User(context);
            var result = await tracking.RecordAsync(id, input, actor, role, ct);
            return Results.Ok(result.State.Movements.Single(item => item.RequestId == input.RequestId));
        });
        api.MapGet("/printers", async (HttpContext context, ProductionLabelService labels, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await labels.PrintersAsync(actor, role, ct)); });
        api.MapPut("/printers", async (SaveProductionPrinters input, HttpContext context, ProductionLabelService labels, CancellationToken ct) =>
        { var (actor, role) = User(context); await labels.SavePrintersAsync(input, actor, role, ct); return Results.NoContent(); });
        api.MapPost("/releases/{id:guid}/labels/{labelId:guid}/reprint", async (Guid id, Guid labelId, ReprintProductionLabel input,
            HttpContext context, ProductionLabelService labels, CancellationToken ct) =>
        { var (actor, role) = User(context); await labels.ReprintAsync(id, labelId, input, actor, role, ct); return Results.NoContent(); });
        api.MapPost("/printers/{printerId:guid}/claim", async (Guid printerId, HttpContext context, ProductionLabelService labels, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await labels.ClaimAsync(printerId, actor, role, ct)); });
        api.MapPost("/releases/{id:guid}/jobs/{jobId:guid}/result", async (Guid id, Guid jobId, CompleteProductionPrint input,
            HttpContext context, ProductionLabelService labels, CancellationToken ct) =>
        { var (actor, role) = User(context); await labels.CompleteAsync(id, jobId, input, actor, role, ct); return Results.NoContent(); });
        api.MapGet("/drawings", async (string q, HttpContext context, IPdmRepository repository, ProductionDrawingService drawings, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (q.Trim().Length < 2) throw new PdmRuleException("请输入至少两个字的图号或名称。");
            var result = new List<ProductionDrawingItem>();
            foreach (var item in (await drawings.ListAsync(actor, role, true, null, ct))
                .Where(item => item.DrawingNumber.Contains(q, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                if (await AuthorizationPolicy.CanAsync(repository, item.ProjectId, actor, ProductionMobilePermissions.Use, ct)
                    && await AuthorizationPolicy.CanAsync(repository, item.ProjectId, actor, ProductionMobilePermissions.Obsolete, ct)) result.Add(item);
            return Results.Ok(result.GroupBy(item => item.DocumentId).Select(group => new { group.First().DocumentId,
                group.First().DrawingNumber, group.First().Name, group.First().Revision,
                Releases = group.Select(item => item.ReleasePackageNumber).Distinct() }).Take(21));
        });
    }
}
