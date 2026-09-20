using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProgramTemplateEndpointExtensions
{
    public static void MapProgramTemplateEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/program-templates").RequireAuthorization();

        api.MapGet("", async (bool? mine, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var values = mine == true
                ? await service.ListMineAsync(actor, role, cancellationToken)
                : await service.ListPublishedAsync(actor, role, cancellationToken);
            return Results.Ok(values.Select(template => MapTemplate(template)));
        });

        api.MapGet("/{templateId:guid}", async (Guid templateId, HttpContext context, ProgramTemplateService service, IProgramTemplateRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var template = await service.FindAsync(templateId, actor, role, cancellationToken);
            var tasksByRevision = new Dictionary<Guid, IReadOnlyList<ProgramTemplateApprovalTask>>();
            foreach (var revision in template.Revisions)
                tasksByRevision[revision.Id] = await repository.ListRevisionTasksAsync(revision.Id, cancellationToken);
            return Results.Ok(MapTemplate(template, tasksByRevision));
        });

        api.MapGet("/options", async (HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetOptionCatalogAsync(actor, role, cancellationToken));
        });

        api.MapPut("/options", async (ProgramTemplateOptionCatalog request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveOptionCatalogAsync(request, actor, role, cancellationToken));
        });

        api.MapPost("", async (CreateProgramTemplateRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var created = await service.CreateAsync(new CreateProgramTemplateCommand(
                request.AssetType, request.Name, request.Category, request.Description, request.Vendor, request.Platform,
                request.SoftwareVersion, request.ApplicableSeries, request.Tags ?? [], request.ChangeNote,
                (request.Parameters ?? []).Select(MapParameter).ToArray()), actor, role, cancellationToken);
            return Results.Created($"/api/program-templates/{created.Id}", MapTemplate(created));
        });

        api.MapPost("/{templateId:guid}/revisions", async (Guid templateId, CreateProgramTemplateRevisionRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRevision(await service.CreateRevisionAsync(templateId, request.Bump, actor, role, cancellationToken)));
        });

        api.MapPut("/revisions/{revisionId:guid}", async (Guid revisionId, UpdateProgramTemplateDraftRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRevision(await service.UpdateDraftAsync(revisionId, new UpdateProgramTemplateDraftCommand(
                request.Name, request.Category, request.Description, request.Vendor, request.Platform, request.SoftwareVersion,
                request.ApplicableSeries, request.Tags ?? [], request.ChangeNote,
                (request.Parameters ?? []).Select(MapParameter).ToArray(), request.ExpectedRowVersion), actor, role, cancellationToken)));
        });

        api.MapDelete("/revisions/{revisionId:guid}", async (Guid revisionId, long expectedRowVersion, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteDraftAsync(revisionId, expectedRowVersion, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/revisions/{revisionId:guid}/uploads", async (Guid revisionId, StartProgramTemplateUploadRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.StartUploadAsync(revisionId, request.Kind, request.FileName, request.TotalLength, request.Sha256, actor, role, cancellationToken));
        });

        api.MapPut("/uploads/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            return Results.Ok(await service.WriteChunkAsync(sessionId, chunkIndex, request.Body, actor, cancellationToken));
        });

        api.MapPost("/uploads/{sessionId:guid}/complete", async (Guid sessionId, CompleteProgramTemplateUploadRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRevision(await service.CompleteUploadAsync(sessionId, request.ExpectedRowVersion, actor, role, cancellationToken)));
        });

        api.MapPost("/revisions/{revisionId:guid}/submit", async (Guid revisionId, SubmitProgramTemplateRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRevision(await service.SubmitAsync(revisionId, request.ExpectedRowVersion, actor, role, cancellationToken)));
        });

        api.MapGet("/tasks/mine", async (HttpContext context, ProgramTemplateService service, IProgramTemplateRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var tasks = await service.ListMyTasksAsync(actor, role, cancellationToken);
            var results = new List<ProgramTemplateTaskResponse>();
            foreach (var task in tasks)
            {
                var revision = await repository.FindRevisionAsync(task.RevisionId, cancellationToken);
                if (revision is null) continue;
                var template = await repository.FindAsync(revision.TemplateId, cancellationToken);
                if (template is null) continue;
                results.Add(new ProgramTemplateTaskResponse(
                    task.Id, template.Id, template.Code, revision.Id, revision.Name, revision.VersionLabel,
                    task.Stage.ToString(), revision.State.ToString(),
                    task.Stage == ProgramTemplateApprovalStage.Review ? await service.GetChecklistAsync(template.AssetType, cancellationToken) : [],
                    task.CreatedAt, task.RowVersion));
            }
            return Results.Ok(results);
        });

        api.MapPost("/tasks/{taskId:guid}/decision", async (Guid taskId, DecideProgramTemplateRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.DecideAsync(taskId, new ProgramTemplateDecisionCommand(
                request.Decision, request.Comment, request.ChecklistItems ?? [], request.ExpectedRowVersion), actor, role, cancellationToken);
            return Results.Ok(new { revision = MapRevision(result.Revision), task = result.Task });
        });

        api.MapGet("/{templateId:guid}/download", async (Guid templateId, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var file = await service.OpenPublishedDownloadAsync(templateId, actor, role, cancellationToken);
            context.Response.Headers["X-Content-SHA256"] = file.Sha256;
            return Results.Stream(file.Content, "application/zip", file.FileName, enableRangeProcessing: true);
        });

        api.MapPost("/{templateId:guid}/archive", async (Guid templateId, SetProgramTemplateArchivedRequest request, HttpContext context, ProgramTemplateService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapTemplate(await service.SetArchivedAsync(templateId, request.Archived, request.Reason, actor, role, cancellationToken)));
        });
    }

    private static ProgramTemplateParameterInput MapParameter(ProgramTemplateParameterRequest item) =>
        new(item.Direction, item.SortOrder, item.Name, item.DataType, item.DefaultValue, item.Unit, item.Description);

    private static ProgramTemplateResponse MapTemplate(
        ProgramTemplate template,
        IReadOnlyDictionary<Guid, IReadOnlyList<ProgramTemplateApprovalTask>>? tasksByRevision = null) => new(
        template.Id, template.Code, template.AssetType, template.OriginCompanyId, template.OriginCompanyName,
        template.CurrentPublishedRevisionId, template.IsArchived, template.CreatedBy, template.CreatedAt,
        template.Revisions
            .Select(revision => MapRevision(revision, tasksByRevision?.GetValueOrDefault(revision.Id)))
            .ToArray());

    private static ProgramTemplateRevisionResponse MapRevision(
        ProgramTemplateRevision revision,
        IReadOnlyList<ProgramTemplateApprovalTask>? approvalTasks = null) => new(
        revision.Id, revision.VersionLabel, revision.AttemptNumber, revision.State.ToString(), revision.Name, revision.Category,
        revision.Description, revision.Vendor, revision.Platform, revision.SoftwareVersion, revision.ApplicableSeries,
        revision.Tags, revision.ChangeNote, revision.PackageFileName, revision.PackageFileLength, revision.PackageSha256,
        revision.EvidenceFileName, revision.EvidenceFileLength, revision.EvidenceSha256, revision.CreatedBy,
        revision.CreatedAt, revision.SubmittedAt, revision.PublishedAt, revision.RowVersion,
        revision.Parameters.OrderBy(item => item.SortOrder).Select(item => new ProgramTemplateParameterResponse(
            item.Id, item.Direction.ToString(), item.SortOrder, item.Name, item.DataType, item.DefaultValue, item.Unit, item.Description)).ToArray(),
        (approvalTasks ?? [])
            .OrderBy(item => item.Stage)
            .Select(item => new ProgramTemplateApprovalTaskResponse(
                item.Stage.ToString(), item.Assignee, item.AssigneeRoleCode, item.Decision?.ToString(), item.DecisionBy,
                item.Comment, item.CreatedAt, item.DecidedAt))
            .ToArray());

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}
