using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class MaterialEndpointExtensions
{
    public static void MapPdmMaterialEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/materials", async (string? query, string? categoryCode, bool? includeArchived, int? limit, MaterialService service, CancellationToken cancellationToken) =>
            Results.Ok((await service.ListMaterialsAsync(query, categoryCode, includeArchived ?? false, limit ?? 100, cancellationToken)).Select(MapMaterial)));

        api.MapPost("/materials", async (SaveMaterialRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.CreateAsync(ToCommand(request), actor, role, cancellationToken)));
        });

        api.MapPut("/materials/{materialId:guid}", async (Guid materialId, SaveMaterialRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.UpdateAsync(materialId, ToCommand(request), actor, role, cancellationToken)));
        });

        api.MapPost("/materials/{materialId:guid}/change", async (Guid materialId, SaveMaterialRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var changed = await service.ChangeApprovedAsync(materialId, ToCommand(request), actor, role, cancellationToken);
            return Results.Ok(new { Material = MapMaterial(changed.Material), Task = MapTask(changed.Task) });
        });

        api.MapDelete("/materials/{materialId:guid}", async (Guid materialId, long expectedRowVersion, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.RemoveAsync(materialId, expectedRowVersion, actor, role, cancellationToken);
            return Results.Ok(new { Material = MapMaterial(result.Material), result.Deleted, result.Archived });
        });

        api.MapGet("/materials/{materialId:guid}/removal-readiness", async (Guid materialId, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.InspectRemovalAsync(materialId, actor, role, cancellationToken));
        });

        api.MapPost("/materials/{materialId:guid}/archive", async (Guid materialId, long expectedRowVersion, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.ArchiveAsync(materialId, expectedRowVersion, actor, role, cancellationToken)));
        });

        api.MapPost("/materials/link-bom", async (LinkBomMaterialRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.LinkBomMaterialAsync(new(request.ProjectId, request.BomItemId, request.MaterialId), actor, role, cancellationToken)));
        });

        api.MapPost("/materials/from-bom", async (CreateMaterialFromBomRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.CreateFromBomAsync(new(request.ProjectId, request.BomItemId), actor, role, cancellationToken)));
        });

        api.MapPost("/materials/{materialId:guid}/approve", async (Guid materialId, long expectedRowVersion, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var approved = await service.ApproveAsync(materialId, expectedRowVersion, actor, role, cancellationToken);
            return Results.Ok(new { Material = MapMaterial(approved.Material), Task = MapTask(approved.Task) });
        });

        api.MapGet("/material-category-rules", async (MaterialService service, CancellationToken cancellationToken) =>
            Results.Ok((await service.ListCategoryRulesAsync(cancellationToken)).Select(MapRule)));

        api.MapGet("/material-categories", async (bool? includeHidden, MaterialService service, CancellationToken cancellationToken) =>
            Results.Ok((await service.ListCategoriesAsync(includeHidden ?? false, cancellationToken)).Select(MapCategory)));

        api.MapPost("/material-categories", async (SaveMaterialCategoryRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapCategory(await service.SaveCategoryAsync(ToCommand(request), actor, role, cancellationToken)));
        });

        api.MapPut("/material-categories/{code}", async (string code, SaveMaterialCategoryRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            if (!string.Equals(code, request.Code, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("分类编码创建后不可修改。");
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapCategory(await service.SaveCategoryAsync(ToCommand(request), actor, role, cancellationToken)));
        });

        api.MapPut("/material-categories/{code}/counter", async (string code, CalibrateMaterialCategoryCounterRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapCategory(await service.CalibrateCategoryCounterAsync(code, new(request.LastMaterialCode), actor, role, cancellationToken)));
        });

        api.MapPut("/material-category-rules/{kind}", async (string kind, SaveMaterialCategoryRuleRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var routeKind = Parse<MaterialKind>(kind, "PDM物料分类");
            var requestKind = Parse<MaterialKind>(request.PdmKind, "PDM物料分类");
            if (routeKind != requestKind) throw new PdmRuleException("路径中的PDM物料分类与请求内容不一致。");
            var (actor, role) = CurrentUser(context.User);
            var saved = await service.SaveCategoryRuleAsync(new(
                requestKind,
                request.U9CategoryCode,
                request.U9CategoryName,
                Parse<MaterialSupplyMode>(request.DefaultSupplyMode, "供给方式"),
                request.IsEnabled), actor, role, cancellationToken);
            return Results.Ok(MapRule(saved));
        });

        api.MapGet("/material-sync-tasks", async (MaterialService service, CancellationToken cancellationToken) =>
            Results.Ok((await service.ListSyncTasksAsync(cancellationToken)).Select(MapTask)));

        api.MapPost("/material-sync-tasks/{taskId:guid}/retry", async (Guid taskId, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapTask(await service.RetrySyncTaskAsync(taskId, actor, role, cancellationToken)));
        });

        api.MapPost("/material-sync-tasks/{taskId:guid}/execute", async (Guid taskId, HttpContext context, U9MaterialIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ExecuteTaskAsync(taskId, actor, role, cancellationToken);
            return Results.Ok(new
            {
                Material = MapMaterial(result.Material),
                Task = MapTask(result.Task),
                result.Created,
                result.AlreadyExisted,
                result.Updated
            });
        });

        api.MapGet("/u9-material-integration", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetIntegrationSettingsAsync(actor, role, cancellationToken));
        });

        api.MapPut("/u9-material-integration", async (UpdateU9MaterialIntegrationRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.UpdateIntegrationSettingsAsync(new(
                request.BaseUrl,
                request.EnterpriseCode,
                request.OrganizationCode,
                request.UserCode,
                request.ClientId,
                request.ClientSecret,
                request.ItemCreatePath,
                request.ItemQueryPath,
                request.WriteEnabled,
                request.ItemModifyPath,
                request.ItemDeletePath,
                request.UnitCodeMappings), actor, role, cancellationToken));
        });

        api.MapPost("/u9-material-integration/test", async (HttpContext context, U9MaterialIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.TestConnectionAsync(actor, role, cancellationToken));
        });

        api.MapGet("/u9-material-query/{materialCode}", async (string materialCode, HttpContext context, U9MaterialIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.QueryByCodeAsync(materialCode, actor, role, cancellationToken));
        });

        api.MapPost("/u9-material-sample/preview", async (U9MaterialSampleRequest request, HttpContext context, U9MaterialIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PreviewSampleAsync(request.CategoryCodes, request.LimitPerCategory, actor, role, cancellationToken));
        });

        api.MapPost("/u9-material-sample/import", async (U9MaterialSampleRequest request, HttpContext context, U9MaterialIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ImportSampleAsync(request.CategoryCodes, request.LimitPerCategory, actor, role, cancellationToken);
            return Results.Ok(new
            {
                result.Preview,
                result.CreatedCount,
                result.RefreshedCount,
                result.SkippedCount,
                Materials = result.Materials.Select(MapMaterial),
                result.ImportedAt
            });
        });
    }

    private static SaveMaterialCommand ToCommand(SaveMaterialRequest request) => new(
        request.MaterialCode,
        request.Name,
        Parse<MaterialKind>(request.Kind, "PDM物料分类"),
        Parse<MaterialSupplyMode>(request.SupplyMode, "供给方式"),
        request.UnitCode,
        request.Specification,
        request.Material,
        request.Remark,
        request.Brand,
        request.SurfaceTreatment,
        request.Weight,
        request.WeightUnit,
        request.ExpectedRowVersion,
        request.CategoryCode,
        request.PurchaseLink);

    private static SaveMaterialCategoryCommand ToCommand(SaveMaterialCategoryRequest request) => new(
        request.Code,
        request.Name,
        request.ParentCode,
        request.U9CategoryId,
        string.IsNullOrWhiteSpace(request.PdmKind) ? null : Parse<MaterialKind>(request.PdmKind, "PDM业务分类"),
        Parse<MaterialSupplyMode>(request.DefaultSupplyMode, "默认供给方式"),
        request.AllowCreate,
        request.IsVisible,
        request.IsActive,
        request.NumberPrefix,
        request.SequenceLength,
        request.CounterScope,
        request.SortOrder,
        request.ExpectedRowVersion);

    private static object MapMaterial(PdmMaterial material) => new
    {
        material.Id,
        material.MaterialCode,
        material.Name,
        Kind = material.Kind.ToString(),
        SupplyMode = material.SupplyMode.ToString(),
        material.UnitCode,
        material.Specification,
        material.Material,
        material.Remark,
        material.Brand,
        material.SurfaceTreatment,
        material.Weight,
        material.WeightUnit,
        material.PurchaseLink,
        material.SourceBomItemId,
        ApprovalStatus = material.ApprovalStatus.ToString(),
        material.ApprovedBy,
        material.ApprovedAt,
        material.U9CategoryCode,
        material.U9ItemId,
        material.U9ItemCode,
        SyncStatus = material.SyncStatus.ToString(),
        material.CreatedBy,
        material.CreatedAt,
        material.UpdatedBy,
        material.UpdatedAt,
        material.RowVersion,
        material.CategoryCode,
        material.IsArchived,
        material.ArchivedBy,
        material.ArchivedAt,
        material.U9SyncConfirmed,
        SourceSystem = material.SourceSystem.ToString(),
        MasterOwner = material.MasterOwner.ToString(),
        material.LastU9SyncedAt
    };

    private static object MapCategory(MaterialCategory category) => new
    {
        category.Code,
        category.Name,
        category.ParentCode,
        category.U9CategoryId,
        PdmKind = category.PdmKind?.ToString(),
        DefaultSupplyMode = category.DefaultSupplyMode.ToString(),
        category.AllowCreate,
        category.IsVisible,
        category.IsActive,
        category.NumberPrefix,
        category.SequenceLength,
        category.CounterScope,
        category.SortOrder,
        category.UpdatedBy,
        category.UpdatedAt,
        category.RowVersion,
        category.CurrentSequence
    };

    private static object MapRule(MaterialCategoryRule rule) => new
    {
        PdmKind = rule.PdmKind.ToString(),
        rule.U9CategoryCode,
        rule.U9CategoryName,
        DefaultSupplyMode = rule.DefaultSupplyMode.ToString(),
        rule.IsEnabled,
        rule.UpdatedBy,
        rule.UpdatedAt
    };

    private static object MapTask(MaterialSyncTask task) => new
    {
        task.Id,
        task.MaterialId,
        Operation = task.Operation.ToString(),
        Status = task.Status.ToString(),
        task.CorrelationId,
        task.PayloadJson,
        task.PayloadSha256,
        task.AttemptCount,
        task.NextAttemptAt,
        task.LastError,
        task.ResponsePreview,
        task.U9ItemId,
        task.U9ItemCode,
        task.CreatedAt,
        task.UpdatedAt
    };

    private static T Parse<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result) ? result : throw new PdmRuleException($"{field}取值无效：{value}。");

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}
