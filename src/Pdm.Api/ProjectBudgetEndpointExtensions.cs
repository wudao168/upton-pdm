using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectBudgetEndpointExtensions
{
    public static void MapProjectBudgetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/budget").RequireAuthorization();
        api.MapGet("/assessments", async (Guid projectId, HttpContext context, BudgetAssessmentService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(projectId, Actor(context), Role(context), ct)));
        api.MapPut("/assessments", async (Guid projectId, SaveBudgetAssessmentCommand command, HttpContext context, BudgetAssessmentService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPut("/assessments/labor-rates", async (Guid projectId, SaveAssessmentLaborRatesCommand command, HttpContext context, BudgetAssessmentService service, CancellationToken ct) =>
            Results.Ok(await service.SaveLaborRatesAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPut("/settlement", async (Guid projectId, SaveProjectSettlementCommand command, HttpContext context, ProjectBudgetService service, CancellationToken ct) =>
            Results.Ok(await service.SaveSettlementAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPut("/bonus-rate", async (Guid projectId, SaveProjectBonusRateCommand command, HttpContext context, ProjectBudgetService service, CancellationToken ct) =>
            Results.Ok(await service.SaveBonusRateAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPost("/notes", async (Guid projectId, AddProjectBudgetNoteCommand command, HttpContext context, ProjectBudgetService service, CancellationToken ct) =>
            Results.Ok(await service.AddNoteAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapGet("", async (Guid projectId, HttpContext context, ProjectBudgetService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(projectId, Actor(context), Role(context), ct)));
        api.MapPut("", async (Guid projectId, SaveProjectBudgetCommand command, HttpContext context, ProjectBudgetService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(projectId, command, Actor(context), Role(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
