using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class StandardLibraryEndpointExtensions
{
    public static void MapStandardLibraryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/standard-library").RequireAuthorization();

        api.MapGet("/categories", async (bool? includeInactive, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListCategoriesAsync(includeInactive ?? false, actor, role, cancellationToken));
        });

        api.MapPost("/categories", async (SaveStandardLibraryCategoryRequest request, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveCategoryAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/categories/{categoryId:guid}", async (Guid categoryId, SaveStandardLibraryCategoryRequest request, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveCategoryAsync(categoryId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapDelete("/categories/{categoryId:guid}", async (Guid categoryId, long expectedRowVersion, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteCategoryAsync(categoryId, expectedRowVersion, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapGet("/materials", async (Guid? categoryId, string? query, string? brand, bool? recommendedOnly, int? page, int? pageSize, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ListMaterialsAsync(new(categoryId, query, brand, recommendedOnly ?? false, page ?? 1, pageSize ?? 50), actor, role, cancellationToken);
            return Results.Ok(new
            {
                Items = result.Items.Select(item => new
                {
                    Material = MaterialEndpointExtensions.MapMaterial(item.Material),
                    item.Categories,
                    CoverImage = item.CoverImage is null ? null : MaterialEndpointExtensions.MapAttachment(item.CoverImage)
                }),
                result.Total,
                result.Page,
                result.PageSize
            });
        });

        api.MapPost("/memberships", async (AddStandardLibraryMaterialsRequest request, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.AddMaterialsAsync(new(request.CategoryIds, request.MaterialIds), actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapDelete("/categories/{categoryId:guid}/materials/{materialId:guid}", async (Guid categoryId, Guid materialId, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.RemoveMaterialAsync(categoryId, materialId, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapPut("/materials/{materialId:guid}/recommended", async (Guid materialId, SetStandardLibraryRecommendationRequest request, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MaterialEndpointExtensions.MapMaterial(await service.SetRecommendedAsync(materialId, request.IsRecommended, request.ExpectedRowVersion, actor, role, cancellationToken)));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}

public sealed record SaveStandardLibraryCategoryRequest(string Name, Guid? ParentId, int SortOrder, bool IsActive, long? ExpectedRowVersion)
{
    public SaveStandardLibraryCategoryCommand ToCommand() => new(Name, ParentId, SortOrder, IsActive, ExpectedRowVersion);
}

public sealed record AddStandardLibraryMaterialsRequest(IReadOnlyList<Guid> CategoryIds, IReadOnlyList<Guid> MaterialIds);
public sealed record SetStandardLibraryRecommendationRequest(bool IsRecommended, long ExpectedRowVersion);
