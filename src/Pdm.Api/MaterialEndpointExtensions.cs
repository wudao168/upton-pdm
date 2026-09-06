using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class MaterialEndpointExtensions
{
    public static void MapPdmMaterialEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/materials", async (string? query, string? categoryCode, bool? includeArchived, int? limit, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListMaterialsAsync(query, categoryCode, includeArchived ?? false, limit ?? 100, actor, role, cancellationToken)).Select(MapMaterial));
        });

        api.MapGet("/materials/page", async (string? query, string? categoryCode, string? brand, bool? includeArchived, int? page, int? pageSize, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.ListMaterialPageAsync(query, categoryCode, brand, includeArchived ?? false, page ?? 1, pageSize ?? 50, actor, role, cancellationToken);
            return Results.Ok(new
            {
                Items = result.Items.Select(MapMaterial),
                result.Total,
                result.Page,
                result.PageSize
            });
        });

        api.MapGet("/materials/{materialId:guid}/attachments", async (Guid materialId, string? kind, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            MaterialAttachmentKind? parsedKind = string.IsNullOrWhiteSpace(kind) ? null : Parse<MaterialAttachmentKind>(kind, "附件类型");
            return Results.Ok((await service.ListAsync(materialId, parsedKind, actor, role, cancellationToken)).Select(MapAttachment));
        });

        api.MapPost("/materials/{materialId:guid}/attachments/uploads", async (Guid materialId, StartMaterialAttachmentUploadRequest request, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var session = await service.StartUploadAsync(materialId, Parse<MaterialAttachmentKind>(request.Kind, "附件类型"), request.FileName, request.TotalLength, request.Sha256, actor, role, cancellationToken);
            return Results.Ok(MapAttachmentUploadSession(session));
        });

        api.MapPut("/material-attachment-uploads/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapAttachmentUploadSession(await service.WriteChunkAsync(sessionId, chunkIndex, request.Body, actor, role, cancellationToken)));
        });

        api.MapPost("/material-attachment-uploads/{sessionId:guid}/complete", async (Guid sessionId, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapAttachment(await service.CompleteUploadAsync(sessionId, actor, role, cancellationToken)));
        });

        api.MapGet("/materials/{materialId:guid}/attachments/{attachmentId:guid}/file", async (Guid materialId, Guid attachmentId, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var download = await service.OpenDownloadAsync(materialId, attachmentId, actor, role, cancellationToken);
            return Results.File(download.Content, "application/octet-stream", download.Attachment.OriginalFileName, enableRangeProcessing: true);
        });

        api.MapPut("/materials/{materialId:guid}/cover", async (Guid materialId, SetMaterialCoverRequest request, HttpContext context, MaterialAttachmentService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.SetCoverAsync(materialId, request.AttachmentId, request.ExpectedRowVersion, actor, role, cancellationToken)));
        });

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
            return Results.Ok(new { Material = MapMaterial(changed.Material), Task = changed.Task is null ? null : MapTask(changed.Task) });
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

        api.MapPost("/materials/{materialId:guid}/reactivate", async (Guid materialId, long expectedRowVersion, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapMaterial(await service.ReactivateAsync(materialId, expectedRowVersion, actor, role, cancellationToken)));
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

        api.MapPost("/material-code/resolve", async (ResolveMaterialCodesRequest request, HttpContext context, MaterialService service, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var results = await service.ResolveStandardBomMaterialsAsync(new(request.ProjectId, request.BomItemIds), actor, role, cancellationToken);
            foreach (var result in results.Where(item => item.Status == MaterialCodeResolutionStatus.Matched && item.Material?.U9SyncConfirmed == true))
            {
                await service.LinkAutomaticallyMatchedBomMaterialAsync(new(request.ProjectId, result.BomItemId, result.Material!.Id), actor, role, cancellationToken);
                await workflow.ApplyAutomaticallyMatchedMaterialCodeToBomAsync(request.ProjectId, result.BomItemId, result.Material.MaterialCode, actor, cancellationToken);
            }
            return Results.Ok(results.Select(MapResolution));
        });

        api.MapPost("/material-code/applications", async (ApplyMaterialCodesRequest request, HttpContext context, MaterialService service, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var results = await service.ApplyForMaterialCodesAsync(new(request.ProjectId, request.BomItemIds), actor, role, cancellationToken);
            foreach (var result in results.Where(item => item.Status == MaterialCodeResolutionStatus.Matched && item.Material?.U9SyncConfirmed == true))
            {
                await service.LinkAutomaticallyMatchedBomMaterialAsync(new(request.ProjectId, result.BomItemId, result.Material!.Id), actor, role, cancellationToken);
                await workflow.ApplyAutomaticallyMatchedMaterialCodeToBomAsync(request.ProjectId, result.BomItemId, result.Material.MaterialCode, actor, cancellationToken);
            }
            return Results.Ok(results.Select(MapResolution));
        });

        api.MapGet("/material-code/applications", async (Guid? projectId, string? status, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            MaterialCodeApplicationStatus? parsedStatus = string.IsNullOrWhiteSpace(status) ? null : Parse<MaterialCodeApplicationStatus>(status, "申请状态");
            return Results.Ok((await service.ListCodeApplicationsAsync(projectId, parsedStatus, actor, role, cancellationToken)).Select(MapApplication));
        });

        api.MapPost("/material-code/applications/{applicationId:guid}/decision", async (Guid applicationId, DecideMaterialCodeApplicationRequest request, HttpContext context, MaterialService service, MaterialSyncBatchService syncBatches, ApprovalU9AutomationService automation, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.DecideMaterialCodeApplicationAsync(applicationId, request.ExpectedRowVersion, request.Approved, request.Comment, actor, role, cancellationToken);
            MaterialSyncBatch? automaticBatch = null;
            ApprovalU9AutomationResult? automationResult = null;
            if (request.Approved && result.Application.BomHeaderKind is not null && result.Task is not null)
            {
                automaticBatch = await syncBatches.CreateAsync([result.Task.Id], actor, role, cancellationToken);
                automationResult = new ApprovalU9AutomationResult(
                    ApprovalU9AutomationStage.NotRequested,
                    "BOM料号已批准，U9C料品与BOM自动同步已排队，无需再次确认。",
                    null,
                    []);
            }
            else if (request.Approved && result.Application.BomHeaderKind is not null && result.Material?.U9SyncConfirmed == true)
            {
                automationResult = await automation.ContinueAfterMaterialSyncAsync(result.Application.ProjectId, actor, cancellationToken);
            }
            else if (request.Approved)
            {
                automationResult = new ApprovalU9AutomationResult(
                    ApprovalU9AutomationStage.NotRequested,
                    "PLM料号已批准；请在当前页面勾选对应记录并执行批量同步到U9C。",
                    null,
                    []);
            }
            return Results.Ok(new
            {
                Application = MapApplication(result.Application),
                Material = result.Material is null ? null : MapMaterial(result.Material),
                Task = result.Task is null ? null : MapTask(result.Task),
                Automation = automationResult,
                AutomaticBatch = automaticBatch is null ? null : MapSyncBatch(automaticBatch)
            });
        });

        api.MapPost("/material-code/projects/{projectId:guid}/u9-continue", async (Guid projectId, HttpContext context, IPdmRepository repository, ApprovalU9AutomationService automation, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken))
                return Results.Forbid();
            return Results.Ok(await automation.ContinueAfterMaterialSyncAsync(projectId, actor, cancellationToken));
        });

        api.MapPost("/materials/{materialId:guid}/approve", async (Guid materialId, long expectedRowVersion, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var approved = await service.ApproveAsync(materialId, expectedRowVersion, actor, role, cancellationToken);
            return Results.Ok(new { Material = MapMaterial(approved.Material), Task = MapTask(approved.Task) });
        });

        api.MapGet("/material-category-rules", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListCategoryRulesAsync(actor, role, cancellationToken)).Select(MapRule));
        });

        api.MapGet("/material-categories", async (bool? includeHidden, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListCategoriesAsync(includeHidden ?? false, actor, role, cancellationToken)).Select(MapCategory));
        });

        api.MapGet("/material-numbering-settings", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetNumberingSettingsAsync(actor, role, cancellationToken));
        });

        api.MapPut("/material-numbering-settings", async (UpdateMaterialNumberingSettingsRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.UpdateNumberingSettingsAsync(request.StartSequence, actor, role, cancellationToken));
        });

        api.MapGet("/material-duplicate-rules", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetDuplicateRulesAsync(actor, role, cancellationToken));
        });

        api.MapPut("/material-duplicate-rules", async (UpdateMaterialDuplicateRulesRequest request, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.UpdateDuplicateRulesAsync(
                request.Rules.Select(rule => new MaterialDuplicateRule(rule.CategoryCode, rule.Fields)).ToArray(),
                actor, role, cancellationToken));
        });

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
            var routeKind = Parse<MaterialKind>(kind, "PLM物料分类");
            var requestKind = Parse<MaterialKind>(request.PdmKind, "PLM物料分类");
            if (routeKind != requestKind) throw new PdmRuleException("路径中的PLM物料分类与请求内容不一致。");
            var (actor, role) = CurrentUser(context.User);
            var saved = await service.SaveCategoryRuleAsync(new(
                requestKind,
                request.U9CategoryCode,
                request.U9CategoryName,
                Parse<MaterialSupplyMode>(request.DefaultSupplyMode, "供给方式"),
                request.IsEnabled), actor, role, cancellationToken);
            return Results.Ok(MapRule(saved));
        });

        api.MapGet("/material-sync-tasks", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListSyncTasksAsync(actor, role, cancellationToken)).Select(MapTask));
        });

        api.MapPost("/material-sync-tasks/{taskId:guid}/retry", async (Guid taskId, HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapTask(await service.RetrySyncTaskAsync(taskId, actor, role, cancellationToken)));
        });

        api.MapPost("/material-sync-tasks/{taskId:guid}/execute", async (Guid taskId, HttpContext context, MaterialCodeSynchronizationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.SynchronizeTaskAsync(taskId, actor, role, cancellationToken);
            return Results.Ok(new
            {
                Material = MapMaterial(result.Material),
                Task = MapTask(result.Task),
                Created = result.ItemSync?.Created ?? false,
                AlreadyExisted = result.ItemSync?.AlreadyExisted ?? result.Material.U9SyncConfirmed,
                Updated = result.ItemSync?.Updated ?? false,
                Automation = result.Automation,
                Applications = result.Applications.Select(MapApplication),
                result.Completed,
                result.Message
            });
        });

        api.MapPost("/material-sync-batches", async (CreateMaterialSyncBatchRequest request, HttpContext context, MaterialSyncBatchService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapSyncBatch(await service.CreateAsync(request.TaskIds, actor, role, cancellationToken)));
        });

        api.MapGet("/material-sync-batches", async (HttpContext context, MaterialSyncBatchService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListRecentAsync(actor, role, cancellationToken)).Select(MapSyncBatch));
        });

        api.MapGet("/material-sync-batches/{batchId:guid}", async (Guid batchId, HttpContext context, MaterialSyncBatchService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapSyncBatch(await service.GetAsync(batchId, actor, role, cancellationToken)));
        });

        api.MapGet("/u9-material-integration", async (HttpContext context, MaterialService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetIntegrationSettingsAsync(actor, role, cancellationToken));
        });

        api.MapGet("/u9-material-full-sync/status", async (
            HttpContext context,
            IMaterialRepository materials,
            IPdmRepository repository,
            U9MaterialFullSyncService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
                throw new UnauthorizedAccessException("当前角色无权查看U9C料品自动同步状态。");
            var categories = (await materials.ListCategoriesAsync(true, cancellationToken))
                .Where(category => category.AllowCreate && category.IsVisible && category.IsActive && category.PdmKind is not null)
                .OrderBy(category => category.SortOrder)
                .ThenBy(category => category.Code, StringComparer.OrdinalIgnoreCase)
                .Select(category => new { category.Code, category.Name })
                .ToArray();
            var latestRun = await service.GetLatestRunAsync(cancellationToken);
            return Results.Ok(new
            {
                ScheduleTime = "02:00",
                CheckIntervalMinutes = 30,
                Categories = categories,
                LatestRun = latestRun is null ? null : new
                {
                    latestRun.Id,
                    latestRun.TriggerKind,
                    Status = latestRun.Status.ToString(),
                    latestRun.CategoryCodes,
                    latestRun.CategoryResults,
                    latestRun.CategoryCount,
                    latestRun.CompletedCategoryCount,
                    latestRun.DiscoveredCount,
                    latestRun.CreatedCount,
                    latestRun.RefreshedCount,
                    latestRun.SkippedCount,
                    latestRun.FailedCategoryCount,
                    latestRun.LastError,
                    latestRun.StartedAt,
                    latestRun.CompletedAt
                }
            });
        });

        api.MapPost("/u9-material-full-sync/run", async (
            HttpContext context,
            IPdmRepository repository,
            U9MaterialFullSyncCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
                throw new UnauthorizedAccessException("当前角色无权执行U9C料品全量同步。");
            if (!coordinator.TryStart(actor, "Manual"))
                return Results.Conflict(new { Message = "已有U9C料品全量同步正在运行，请刷新状态查看进度。" });
            return Results.Accepted("/api/u9-material-full-sync/status", new { Message = "U9C料品全量同步已在后台启动。" });
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
                request.UnitCodeMappings,
                request.CustomerQueryPath,
                request.BomCreatePath,
                request.BomQueryPath,
                request.BomModifyPath,
                request.BomDeletePath,
                request.BomBatchUnapprovePath,
                request.BomBipQueryPagePath), actor, role, cancellationToken));
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
        Parse<MaterialKind>(request.Kind, "PLM物料分类"),
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
        request.PurchaseLink,
        request.SelectionAdvice,
        request.ReferencePrice,
        request.Model3DLink,
        request.DocumentLink,
        request.IsRecommended);

    private static SaveMaterialCategoryCommand ToCommand(SaveMaterialCategoryRequest request) => new(
        request.Code,
        request.Name,
        request.ParentCode,
        request.U9CategoryId,
        string.IsNullOrWhiteSpace(request.PdmKind) ? null : Parse<MaterialKind>(request.PdmKind, "PLM业务分类"),
        Parse<MaterialSupplyMode>(request.DefaultSupplyMode, "默认供给方式"),
        request.AllowCreate,
        request.IsVisible,
        request.IsActive,
        request.NumberPrefix,
        request.SequenceLength,
        request.CounterScope,
        request.SortOrder,
        request.ExpectedRowVersion);

    internal static object MapMaterial(PdmMaterial material) => new
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
        material.SelectionAdvice,
        material.ReferencePrice,
        material.Model3DLink,
        material.DocumentLink,
        material.IsRecommended,
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
        material.LastU9SyncedAt,
        material.ReferenceCount,
        material.Model3DAttachmentCount,
        material.DocumentAttachmentCount,
        material.CoverImageAttachmentId
    };

    internal static object MapAttachment(MaterialAttachment attachment) => new
    {
        attachment.Id,
        attachment.MaterialId,
        Kind = attachment.Kind.ToString(),
        attachment.OriginalFileName,
        attachment.FileLength,
        attachment.Sha256,
        attachment.UploadedBy,
        attachment.UploadedAt
    };

    private static object MapAttachmentUploadSession(MaterialAttachmentUploadSession session) => new
    {
        session.Id,
        session.MaterialId,
        Kind = session.Kind.ToString(),
        session.FileName,
        session.TotalLength,
        session.ChunkSize,
        session.ReceivedLength,
        session.ExpiresAt
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
        task.MaterialCode,
        task.MaterialName,
        task.CategoryCode,
        task.ProjectId,
        task.ProjectCode,
        task.ProjectName,
        BomHeaderKind = task.BomHeaderKind?.ToString(),
        task.RequestedBy,
        task.RequestedAt,
        task.CreatedAt,
        task.UpdatedAt
    };

    private static object MapSyncBatch(MaterialSyncBatch batch) => new
    {
        batch.Id,
        Status = batch.Status.ToString(),
        batch.RequestedBy,
        RequestedRole = batch.RequestedRole.ToString(),
        batch.TotalCount,
        batch.CompletedCount,
        batch.SucceededCount,
        batch.WaitingCount,
        batch.FailedCount,
        batch.CurrentTaskId,
        batch.CurrentMaterialCode,
        batch.LastError,
        batch.CreatedAt,
        batch.StartedAt,
        batch.CompletedAt,
        Items = batch.Items.Select(item => new
        {
            item.Id,
            item.BatchId,
            item.TaskId,
            item.Ordinal,
            Status = item.Status.ToString(),
            item.Message,
            item.StartedAt,
            item.CompletedAt
        })
    };

    private static object MapApplication(MaterialCodeApplication application) => new
    {
        application.Id,
        application.ProjectId,
        application.BomItemId,
        BomHeaderKind = application.BomHeaderKind?.ToString(),
        ApplicationType = application.BomHeaderKind is null ? "StandardBomItem" : "BomHeader",
        Status = application.Status.ToString(),
        application.RequestedBy,
        application.RequestedAt,
        application.DecidedBy,
        application.DecidedAt,
        application.DecisionComment,
        application.MaterialId,
        application.MaterialCode,
        application.RowVersion,
        application.BomItemName,
        application.ApplicationName,
        application.ProjectCode,
        application.ProjectName,
        application.CategoryCode,
        application.RequestedMaterialCode,
        application.Specification,
        application.Brand,
        application.Remark,
        WorkflowState = application.WorkflowState.ToString(),
        application.SyncTaskId,
        SyncStatus = application.SyncStatus?.ToString(),
        application.SyncError,
        application.WorkflowMessage
    };

    private static object MapResolution(MaterialCodeResolution resolution) => new
    {
        resolution.BomItemId,
        Status = resolution.Status.ToString(),
        Material = resolution.Material is null ? null : MapMaterial(resolution.Material),
        Candidates = resolution.Candidates.Select(MapMaterial),
        Application = resolution.Application is null ? null : MapApplication(resolution.Application),
        resolution.Issues
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
