using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectApplicationEndpoints
{
    public static void MapProjectApplicationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/project-applications").RequireAuthorization();
        api.MapPut("/projects/{projectId:guid}/order-date", async (Guid projectId, ProjectOrderDateInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateOrderDateAsync(projectId, input, Actor(context), Role(context), ct)));
        api.MapGet("", async (HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(Actor(context), Role(context), ct)));
        api.MapGet("/{id:guid}", async (Guid id, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.DetailAsync(id, Actor(context), Role(context), ct)));
        api.MapPost("", async (SaveProjectApplicationInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(null, input, Actor(context), Role(context), ct)));
        api.MapPut("/{id:guid}", async (Guid id, SaveProjectApplicationInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(id, input, Actor(context), Role(context), ct)));
        api.MapPost("/{id:guid}/withdraw", async (Guid id, ProjectApplicationActionInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.WithdrawAsync(id, input, Actor(context), Role(context), ct)));
        api.MapPost("/{id:guid}/return", async (Guid id, ProjectApplicationActionInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.ReturnAsync(id, input, Actor(context), Role(context), ct)));
        api.MapPost("/{id:guid}/approve", async (Guid id, ProjectApplicationApprovalInput input, HttpContext context, ProjectApplicationService service, CancellationToken ct) =>
            Results.Ok(await service.ApproveAsync(id, input, Actor(context), Role(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
