using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class U9BomEndpointExtensions
{
    public static WebApplication MapU9BomEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/u9-boms/query", async (
            U9BomQueryRequest request,
            HttpContext context,
            U9BomQueryService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.QueryAsync(new(
                request.ItemCode,
                request.BomVersionCode,
                request.Lot,
                request.ProductUomCode), actor, role, cancellationToken));
        });
        api.MapPost("/u9-boms/write-preview", async (
            U9BomWriteRequest request,
            HttpContext context,
            U9BomWriteService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PreviewAsync(request.ToCommand(), actor, role, cancellationToken));
        });
        api.MapPost("/u9-boms/write-execute", async (
            U9BomWriteExecuteRequest request,
            HttpContext context,
            U9BomWriteService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ExecuteAsync(
                request.Command.ToCommand(), request.RequestSha256, request.Confirmation,
                actor, role, cancellationToken));
        });
        return app;
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息缺少用户名。");
        var roleValue = principal.FindFirst(ClaimTypes.Role)?.Value ?? principal.FindFirst("role")?.Value;
        if (!Enum.TryParse<UserRole>(roleValue, true, out var role))
            throw new UnauthorizedAccessException("登录信息缺少有效角色。");
        return (actor, role);
    }
}

public sealed record U9BomQueryRequest(
    string ItemCode,
    string? BomVersionCode = null,
    int? Lot = null,
    string? ProductUomCode = null);

public sealed record U9BomComponentRequest(
    int Sequence,
    string ItemCode,
    decimal UsageQty,
    string IssueUomCode,
    decimal ParentQty = 1,
    string? ItemVersionCode = null,
    bool IsEffective = true,
    DateOnly? EffectiveDate = null,
    DateOnly? DisableDate = null,
    string? Remark = null,
    int ComponentType = 0,
    int IssueStyle = 0,
    int SupplyStyle = 0,
    bool IsPhantomPart = false,
    bool IsDelete = false)
{
    public U9BomComponentCommand ToCommand() => new(
        Sequence, ItemCode, UsageQty, IssueUomCode, ParentQty, ItemVersionCode,
        IsEffective, EffectiveDate, DisableDate, Remark, ComponentType, IssueStyle,
        SupplyStyle, IsPhantomPart, IsDelete);
}

public sealed record U9BomWriteRequest(
    U9BomWriteOperation Operation,
    string ItemCode,
    string BomVersionCode,
    string ProductUomCode,
    int Lot,
    IReadOnlyList<U9BomComponentRequest> Components,
    DateOnly? EffectiveDate = null,
    DateOnly? DisableDate = null,
    int BomSort = 0,
    int BomType = 0,
    string? ProjectMapNum = null,
    string? Explain = null)
{
    public U9BomWriteCommand ToCommand() => new(
        Operation, ItemCode, BomVersionCode, ProductUomCode, Lot,
        Components.Select(component => component.ToCommand()).ToArray(), EffectiveDate,
        DisableDate, BomSort, BomType, ProjectMapNum, Explain);
}

public sealed record U9BomWriteExecuteRequest(
    U9BomWriteRequest Command,
    string RequestSha256,
    string Confirmation);
