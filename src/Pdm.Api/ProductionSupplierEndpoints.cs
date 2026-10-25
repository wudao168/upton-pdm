using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProductionSupplierEndpoints
{
    public static void MapProductionSupplierEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/production-suppliers").RequireAuthorization();
        static (string Actor, UserRole Role) User(HttpContext context) =>
            (context.User.Identity?.Name ?? "", Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role)
                ? role : UserRole.Engineer);
        api.MapGet("/", async (HttpContext context, ProductionSupplierService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReadAsync(actor, role, ct));
        });
        api.MapGet("/choices", async (HttpContext context, ProductionSupplierService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ChoicesAsync(actor, role, ct));
        });
        api.MapPut("/categories/{id:guid}", async (Guid id, SaveProductionSupplierCategory input,
            HttpContext context, ProductionSupplierService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.SaveCategoryAsync(id, input, actor, role, ct));
        });
        api.MapPut("/{id:guid}", async (Guid id, SaveProductionSupplier input,
            HttpContext context, ProductionSupplierService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.SaveSupplierAsync(id, input, actor, role, ct));
        });
    }
}
