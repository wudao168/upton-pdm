using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class QualityReviewEndpoints
{
    public static void MapQualityReviewEndpoints(this WebApplication app)
    {
        var company = app.MapGroup("/api/quality-review").RequireAuthorization();
        company.MapGet("", async (HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Results.Ok(await service.WorkspaceAsync(Actor(context), Role(context), ct)));
        company.MapGet("/settings", async (HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Results.Ok(await service.SettingsAsync(Actor(context), Role(context), ct)));
        company.MapPut("/settings", async (SaveQualityReviewSettings input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveSettingsAsync(input, Actor(context), Role(context), ct)));
        company.MapPut("/themes/{id:guid}", async (Guid id, SaveQualityTheme input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveThemeAsync(id, input, Actor(context), Role(context), ct)));
        company.MapDelete("/themes/{id:guid}", async (Guid id, long expectedRowVersion, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.DeleteThemeAsync(id, expectedRowVersion, Actor(context), Role(context), ct)));
        company.MapPut("/improvements/{id:guid}", async (Guid id, SaveQualityImprovement input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveImprovementAsync(id, input, Actor(context), Role(context), ct)));
        company.MapPost("/improvements/{id:guid}/transition", async (Guid id, QualityTransition input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.TransitionImprovementAsync(id, input, Actor(context), Role(context), ct)));
        company.MapPost("/improvements/{id:guid}/observations", async (Guid id, SaveQualityObservation input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveObservationAsync(id, input, Actor(context), Role(context), ct)));
        company.MapPut("/experiences/{id:guid}", async (Guid id, PublishQualityExperience input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.PublishExperienceAsync(id, input, Actor(context), Role(context), ct)));
        company.MapPost("/adoptions", async (AssignQualityAdoption input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.AssignAdoptionAsync(input, Actor(context), Role(context), ct)));
        company.MapPost("/adoptions/{id:guid}/confirm", async (Guid id, ConfirmQualityAdoption input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.ConfirmAdoptionAsync(id, input, Actor(context), Role(context), ct)));

        var project = app.MapGroup("/api/projects/{projectId:guid}").RequireAuthorization();
        project.MapGet("/quality-review", async (Guid projectId, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Results.Ok(await service.ProjectAsync(projectId, Actor(context), Role(context), ct)));
        project.MapGet("/quality-review/issues/{issueId:guid}", async (Guid projectId, Guid issueId, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Results.Ok(await service.IssueAsync(projectId, issueId, Actor(context), Role(context), ct)));
        project.MapPut("/quality-review/issues/{issueId:guid}", async (Guid projectId, Guid issueId, SaveQualityIssueProfile input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveIssueAsync(projectId, issueId, input, Actor(context), Role(context), ct)));
        project.MapPut("/retrospectives/{id:guid}", async (Guid projectId, Guid id, SaveProjectRetrospective input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.SaveSummaryAsync(projectId, id, input, Actor(context), Role(context), ct)));
        project.MapPost("/retrospectives/{id:guid}/transition", async (Guid projectId, Guid id, QualityTransition input, HttpContext context, QualityReviewService service, CancellationToken ct) =>
            Saved(await service.TransitionSummaryAsync(projectId, id, input, Actor(context), Role(context), ct)));
    }

    // Mutations expose only the concurrency token; project access never returns the company catalog.
    private static IResult Saved(QualityReviewCatalog catalog) => Results.Ok(new { catalog.RowVersion });
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
    private static UserRole Role(HttpContext context) => Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role)
        ? role : throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
}
