using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class StandardLibraryEndpointExtensions
{
    public static void MapStandardLibraryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/standard-library").RequireAuthorization();

        api.MapPost("/imports/parse", async (HttpContext context, StandardLibraryService service, CancellationToken ct) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.ListCategoriesAsync(true, actor, role, ct);
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > 2 * 1024 * 1024 || !file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("请选择不超过2MB的.xlsx文件，只需提供料号列。");
            try
            {
                await using var input = file.OpenReadStream();
                return Results.Ok(new { MaterialCodes = MaterialImportWorkbook.ReadMaterialCodes(input) });
            }
            catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException)
            { throw new PdmRuleException("Excel文件无法读取，请使用有效的.xlsx文件，料号列按文本保存。"); }
        });

        api.MapPost("/imports", async (StandardLibraryImportRequest request, HttpContext context, StandardLibraryImportService service, ILoggerFactory logs, CancellationToken ct) =>
        {
            var (actor, role) = CurrentUser(context.User);
            try { return Results.Ok(await service.ImportAsync(request.MaterialCodes, actor, role, ct)); }
            catch (Exception error) when (error is not (PdmRuleException or PdmConflictException or UnauthorizedAccessException or OperationCanceledException))
            {
                logs.CreateLogger("StandardLibraryImport").LogError(error, "标准物料ERP导入中断");
                return Results.Problem("ERP导入未完成，请检查ERP连接后重试；已完成的料号不会重复创建，请刷新列表核对。", statusCode: 500);
            }
        });

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

        api.MapGet("/materials", async (Guid? categoryId, string? query, string? brand, bool? recommendedOnly, int? page, int? pageSize, bool? includeInactive, HttpContext context, StandardLibraryService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ListMaterialsAsync(new(categoryId, query, brand, recommendedOnly ?? false, page ?? 1, pageSize ?? 50, includeInactive ?? false), actor, role, cancellationToken);
            return Results.Ok(new
            {
                Items = result.Items.Select(item => new
                {
                    Material = MaterialEndpointExtensions.MapMaterial(item.Material),
                    item.Categories,
                    item.Kit,
                    item.Relation,
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

public sealed record SaveStandardLibraryCategoryRequest(string Name, Guid? ParentId, int SortOrder, bool IsActive, long? ExpectedRowVersion, string? Code = null)
{
    public SaveStandardLibraryCategoryCommand ToCommand() => new(Name, ParentId, SortOrder, IsActive, ExpectedRowVersion, Code);
}

public sealed record AddStandardLibraryMaterialsRequest(IReadOnlyList<Guid> CategoryIds, IReadOnlyList<Guid> MaterialIds);
public sealed record StandardLibraryImportRequest(IReadOnlyList<string> MaterialCodes);
public sealed record SetStandardLibraryRecommendationRequest(bool IsRecommended, long ExpectedRowVersion);
