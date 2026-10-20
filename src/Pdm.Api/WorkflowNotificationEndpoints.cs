using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class WorkflowNotificationEndpoints
{
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。")));
    public static void MapWorkflowNotificationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/workflow-notifications").RequireAuthorization();
        api.MapGet("/", async (HttpContext context, WorkflowNotificationService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.ReadAsync(actor, role, ct)); });
        api.MapPut("/flows/{flowCode}", async (string flowCode, SaveWorkflowNotification input, HttpContext context, WorkflowNotificationService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.SaveFlowAsync(flowCode, input, actor, role, ct)); });
        api.MapGet("/flows/{flowCode}/records", async (string flowCode, HttpContext context, WorkflowNotificationService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.RecordsAsync(flowCode, actor, role, ct)); });
    }
}
