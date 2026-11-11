using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ExpenseRequestEndpoints
{
    public static void MapExpenseRequestEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/expense-requests").RequireAuthorization();
        api.MapGet("", async (Guid projectId, HttpContext context, ExpenseRequestService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(projectId, Actor(context), Role(context), ct)));
        api.MapPost("/preview", async (Guid projectId, SaveExpenseRequest input, HttpContext context, ExpenseRequestService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(projectId, input, Actor(context), Role(context), ct)));
        api.MapPut("/{id:guid}", async (Guid projectId, Guid id, SaveExpenseRequest input, HttpContext context, ExpenseRequestService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(projectId, id, input, Actor(context), Role(context), ct)));
        api.MapPost("/{id:guid}/decision", async (Guid projectId, Guid id, DecideExpenseRequest input, HttpContext context, ExpenseRequestService service, CancellationToken ct) =>
            Results.Ok(await service.DecideAsync(projectId, id, input, Actor(context), Role(context), ct)));
        api.MapPost("/{id:guid}/withdraw", async (Guid projectId, Guid id, WithdrawExpenseRequest input, HttpContext context, ExpenseRequestService service, CancellationToken ct) =>
            Results.Ok(await service.WithdrawAsync(projectId, id, input, Actor(context), Role(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
