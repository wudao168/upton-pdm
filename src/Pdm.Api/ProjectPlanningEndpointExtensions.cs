using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectPlanningEndpointExtensions
{
    public static void MapProjectPlanningEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/project-plan-templates", async (bool? includeInactive, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListTemplatesAsync(includeInactive ?? false, actor, role, cancellationToken));
        });

        api.MapPost("/project-plan-templates", async (SaveProjectPlanTemplateRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveTemplateAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/project-plan-templates/{templateId:guid}", async (Guid templateId, SaveProjectPlanTemplateRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveTemplateAsync(templateId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/plan", async (Guid projectId, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Json(await service.GetPlanAsync(projectId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/plan/portfolio", async (Guid projectId, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetPortfolioAsync(projectId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/plan/versions", async (Guid projectId, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListVersionsAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/generate", async (Guid projectId, GenerateProjectPlanRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GenerateAsync(projectId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/stage-schedule", async (Guid projectId, SupplementStageScheduleRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SupplementStageScheduleAsync(projectId, request.StartDate, request.TotalDurationDays, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapDelete("/projects/{projectId:guid}/plan", async (Guid projectId, long expectedRowVersion, bool? includeIndependentChildren, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteDraftAsync(projectId, expectedRowVersion, actor, role, cancellationToken, includeIndependentChildren ?? false);
            return Results.NoContent();
        });

        api.MapPut("/projects/{projectId:guid}/plan", async (Guid projectId, SaveProjectPlanRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveAsync(projectId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/baseline", async (Guid projectId, SetProjectPlanBaselineRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SetBaselineAsync(projectId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/submit", async (Guid projectId, SetProjectPlanBaselineRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SubmitApprovalAsync(projectId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/change-request", async (Guid projectId, SubmitProjectPlanChangeCommand request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SubmitChangeAsync(projectId, request, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/change-complete", async (Guid projectId, CompleteProjectPlanChangeCommand request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.CompleteChangeAsync(projectId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/change-abandon", async (Guid projectId, CompleteProjectPlanChangeCommand request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.AbandonChangeAsync(projectId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/decision", async (Guid projectId, DecideProjectPlanRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.DecideApprovalAsync(projectId, request.ExpectedRowVersion, request.Approve, request.Comment, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/plan/tasks/{taskId:guid}/progress", async (Guid projectId, Guid taskId, UpdateProjectPlanTaskProgressRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.UpdateProgressAsync(projectId, taskId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/plan/stage", async (Guid projectId, SetProjectPlanStageRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SetStageAsync(projectId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/plan/reuse", async (Guid projectId, ReuseProjectPlanRequest request, HttpContext context, ProjectPlanningService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ReuseAsync(projectId, request.ToCommand(), actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}

public sealed record SaveProjectPlanTemplateRequest(string Name, string? ProjectTypeCode, bool IsActive, IReadOnlyList<ProjectPlanTemplateTask> Tasks, long? ExpectedRowVersion, IReadOnlyList<ProjectPlanStageDefinition>? Stages = null)
{
    public SaveProjectPlanTemplateCommand ToCommand() => new(Name, ProjectTypeCode, IsActive, Tasks, ExpectedRowVersion, Stages);
}

public sealed record GenerateProjectPlanRequest(Guid TemplateId, DateOnly StartDate, int TotalDurationDays, bool ReplaceExisting, string? ChangeReason)
{
    public IReadOnlyList<ProjectPlanStageSchedule>? IndependentStages { get; init; }
    public IReadOnlyList<string>? DeferredStages { get; init; }
    public GenerateProjectPlanCommand ToCommand() => new(TemplateId, StartDate, TotalDurationDays, ReplaceExisting, ChangeReason, IndependentStages, DeferredStages);
}

public sealed record SupplementStageScheduleRequest(DateOnly StartDate, int TotalDurationDays, long ExpectedRowVersion);

public sealed record SaveProjectPlanRequest(IReadOnlyList<ProjectPlanTask> Tasks, string ChangeReason, long ExpectedRowVersion, IReadOnlyList<ProjectPlanStageDefinition>? Stages = null, bool CreateMissingFollowers = false)
{
    public SaveProjectPlanCommand ToCommand() => new(Tasks, ChangeReason, ExpectedRowVersion, Stages, CreateMissingFollowers);
}

public sealed record DecideProjectPlanRequest(long ExpectedRowVersion, bool Approve, string? Comment);

public sealed record SetProjectPlanBaselineRequest(long ExpectedRowVersion);

public sealed record UpdateProjectPlanTaskProgressRequest(int CompletionPercent, DateOnly? ActualStart, DateOnly? ActualFinish, long ExpectedRowVersion)
{
    public UpdateProjectPlanTaskProgressCommand ToCommand() => new(CompletionPercent, ActualStart, ActualFinish, ExpectedRowVersion);
}

public sealed record SetProjectPlanStageRequest(string? Stage, string Reason, long ExpectedRowVersion)
{
    public SetProjectPlanStageCommand ToCommand() => new(Stage, Reason, ExpectedRowVersion);
}

public sealed record ReuseProjectPlanRequest(Guid SourceProjectId, IReadOnlyList<Guid> TargetProjectIds, bool ReplaceExisting, string ChangeReason)
{
    public ReuseProjectPlanCommand ToCommand() => new(SourceProjectId, TargetProjectIds, ReplaceExisting, ChangeReason);
}
