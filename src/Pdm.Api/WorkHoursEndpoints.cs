using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class WorkHoursEndpoints
{
    public static void MapWorkHoursEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/work-hours").RequireAuthorization();
        api.MapGet("", async (DateOnly from, DateOnly to, bool? report, HttpContext context, WorkHoursService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAsync(from, to, report == true, Actor(context), Role(context), ct)));
        api.MapPut("", async (SaveWorkHours input, HttpContext context, WorkHoursService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(input, Actor(context), Role(context), ct)));
        api.MapGet("/history", async (string month, HttpContext context, WorkHoursService service, CancellationToken ct) =>
            Results.Ok(await service.HistoryAsync(month, Actor(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效，请重新登录。"));
}
