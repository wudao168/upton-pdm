using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class ValidationPlanEndpointExtensions
{
    public static void MapValidationPlanEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/validation-check-catalog", async (bool? includeInactive, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListCatalogAsync(includeInactive ?? false, actor, role, cancellationToken));
        });

        api.MapPost("/validation-check-catalog/categories", async (SaveValidationCheckCategoryRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveCategoryAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/validation-check-catalog/categories/{categoryId:guid}", async (Guid categoryId, SaveValidationCheckCategoryRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveCategoryAsync(categoryId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapDelete("/validation-check-catalog/categories/{categoryId:guid}", async (Guid categoryId, long expectedRowVersion, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteCategoryAsync(categoryId, expectedRowVersion, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/validation-check-catalog/items", async (SaveValidationCheckItemRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveItemAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/validation-check-catalog/items/{itemId:guid}", async (Guid itemId, SaveValidationCheckItemRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveItemAsync(itemId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapDelete("/validation-check-catalog/items/{itemId:guid}", async (Guid itemId, long expectedRowVersion, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteItemAsync(itemId, expectedRowVersion, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapGet("/projects/{projectId:guid}/validation-plan", async (Guid projectId, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Json(await service.GetPlanAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/validation-plan", async (Guid projectId, SaveProjectValidationPlanRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SavePlanAsync(projectId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/validation-plan/export", async (Guid projectId, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var export = await service.PrepareExportAsync(projectId, actor, role, cancellationToken);
            return Results.File(ValidationPlanWorkbook.Write(export), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ValidationPlanWorkbook.FileName(export));
        });

        api.MapPost("/projects/{projectId:guid}/validation-plan/revisions", async (Guid projectId, long expectedRowVersion, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.CreateRevisionAsync(projectId, expectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/validation-plan/submit", async (Guid projectId, long expectedRowVersion, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SubmitAsync(projectId, expectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/validation-plan-approval-tasks/{taskId:guid}/decision", async (Guid taskId, ApprovalRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.DecideAsync(taskId, request.Decision, request.Comment, actor, role, cancellationToken));
        });

        api.MapGet("/validation-plan-approval-tasks/mine", async (HttpContext context, IPdmRepository repository, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var projects = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken)).ToDictionary(item => item.Id);
            var tasks = await service.ListMyApprovalTasksAsync(actor, role, cancellationToken);
            return Results.Ok(tasks.Where(item => projects.ContainsKey(item.ProjectId)).Select(item => new ValidationPlanApprovalTaskResponse(
                item.Id, item.PlanId, item.ProjectId, projects[item.ProjectId].Code, projects[item.ProjectId].Name, item.RevisionNumber, item.Stage, item.StepName, item.CreatedAt)));
        });

        api.MapPost("/validation-plans/{planId:guid}/attachment-uploads", async (Guid planId, StartValidationPlanAttachmentUploadRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.StartAttachmentUploadAsync(planId, request.Kind, request.FileName, request.TotalLength, request.Sha256, actor, role, cancellationToken));
        });

        api.MapPost("/validation-plans/{planId:guid}/attachment-uploads/{sessionId:guid}/complete", async (Guid planId, Guid sessionId, CompleteValidationPlanAttachmentUploadRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.CompleteAttachmentUploadAsync(planId, request.Kind, sessionId, actor, role, cancellationToken));
        });

        api.MapGet("/validation-plan-attachments/{attachmentId:guid}/download", async (Guid attachmentId, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var download = await service.PrepareAttachmentDownloadAsync(attachmentId, actor, role, cancellationToken);
            return Results.File(download.Content, "application/octet-stream", download.Attachment.OriginalFileName);
        });

        api.MapPost("/validation-plan-attachments/{attachmentId:guid}/recognize", async (Guid attachmentId, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.RecognizeAttachmentAsync(attachmentId, actor, role, cancellationToken));
        });

        api.MapGet("/validation-plans/{planId:guid}/execution-records", async (Guid planId, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListExecutionRecordsAsync(planId, actor, role, cancellationToken));
        });

        api.MapPost("/validation-plans/{planId:guid}/execution-records", async (Guid planId, ConfirmValidationPlanExecutionRequest request, HttpContext context, ValidationPlanService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ConfirmExecutionRecordAsync(planId, request.ToCommand(), actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}

public sealed record SaveValidationCheckCategoryRequest(string Name, int SortOrder, bool IsActive, string? Note, long? ExpectedRowVersion)
{
    public SaveValidationCheckCategoryCommand ToCommand() => new(Name, SortOrder, IsActive, Note, ExpectedRowVersion);
}

public sealed record SaveValidationCheckItemRequest(Guid CategoryId, string Content, string? DefaultInformationSource, int SortOrder, bool IsActive, string? Note, long? ExpectedRowVersion)
{
    public SaveValidationCheckItemCommand ToCommand() => new(CategoryId, Content, DefaultInformationSource, SortOrder, IsActive, Note, ExpectedRowVersion);
}

public sealed record SaveProjectValidationPlanItemRequest(Guid? CatalogItemId, string? ValidationContent, string? InformationSource, DateOnly? ValidationDate, string? Result, string? ResponsiblePerson, string? Remark, int SortOrder)
{
    public SaveProjectValidationPlanItemCommand ToCommand() => new(CatalogItemId, ValidationContent, InformationSource, ValidationDate, Result, ResponsiblePerson, Remark, SortOrder);
}

public sealed record SaveProjectValidationPlanRequest(string? PreparedBy, DateOnly? ValidationDate, IReadOnlyList<SaveProjectValidationPlanItemRequest> Items, long? ExpectedRowVersion)
{
    public SaveProjectValidationPlanCommand ToCommand() => new(PreparedBy, ValidationDate, Items.Select(item => item.ToCommand()).ToArray(), ExpectedRowVersion);
}

public sealed record StartValidationPlanAttachmentUploadRequest(ValidationPlanAttachmentKind Kind, string FileName, long TotalLength, string Sha256);

public sealed record CompleteValidationPlanAttachmentUploadRequest(ValidationPlanAttachmentKind Kind);

public sealed record ConfirmValidationPlanExecutionItemRequest(
    Guid PlanItemId,
    decimal MatchConfidence,
    string SourceText,
    string? RecognizedResult,
    DateOnly? RecognizedValidationDate,
    string? RecognizedResponsiblePerson,
    string? RecognizedRemark,
    string? Result,
    DateOnly? ValidationDate,
    string? ResponsiblePerson,
    string? Remark)
{
    public ConfirmValidationPlanExecutionItemCommand ToCommand() => new(PlanItemId, MatchConfidence, SourceText, RecognizedResult, RecognizedValidationDate, RecognizedResponsiblePerson, RecognizedRemark, Result, ValidationDate, ResponsiblePerson, Remark);
}

public sealed record ConfirmValidationPlanExecutionRequest(Guid SourceAttachmentId, string OcrText, IReadOnlyList<ConfirmValidationPlanExecutionItemRequest> Items)
{
    public ConfirmValidationPlanExecutionCommand ToCommand() => new(SourceAttachmentId, OcrText, Items.Select(item => item.ToCommand()).ToArray());
}

public sealed record ValidationPlanApprovalTaskResponse(Guid Id, Guid PlanId, Guid ProjectId, string ProjectCode, string ProjectName, int RevisionNumber, ApprovalStage Stage, string StepName, DateTimeOffset CreatedAt);
