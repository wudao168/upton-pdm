using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;
public static class MaterialBrandEndpointExtensions
{
    public static void MapMaterialBrandEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/material-brands").RequireAuthorization();
        api.MapGet("/", async (HttpContext context, MaterialBrandService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(context.User.Identity!.Name!, Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!), ct)));
        api.MapPost("/", async (SaveMaterialBrand request, HttpContext context, MaterialBrandService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(null, request.Name, request.IsActive, null, context.User.Identity!.Name!, Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!), ct)));
        api.MapPut("/{id:guid}", async (Guid id, SaveMaterialBrand request, HttpContext context, MaterialBrandService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(id, request.Name, request.IsActive, request.RowVersion, context.User.Identity!.Name!, Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!), ct)));
    }
}
public sealed record SaveMaterialBrand(string Name, bool IsActive, long? RowVersion);
