using System.Security.Claims;
using Upton.Pdm.Application;
namespace Upton.Pdm.Api;

public static class CalendarTodoEndpoints
{
    public static void MapCalendarTodos(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/calendar-todos", async (DateOnly from, DateOnly to, HttpContext context, UserTodoService service, CancellationToken ct) => Results.Ok(await service.ListAsync(Actor(context), from, to, ct)));
        api.MapPost("/todos", async (CreateUserTodo command, HttpContext context, UserTodoService service, CancellationToken ct) => Results.Ok(await service.CreateAsync(command, Actor(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.FindFirstValue(ClaimTypes.Name) ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
}
