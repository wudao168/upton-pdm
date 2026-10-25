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
        api.MapGet("/settings", async (HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.ReadManagementAsync(Actor(context), ct)));
        api.MapPut("/settings", async (SaveWorkHoursGroupSettings input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.SaveGroupSettingsAsync(input, Actor(context), ct)));
        api.MapPut("/settings/requirements", async (SetWorkHoursRequirement input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.SetRequirementAsync(input, Actor(context), ct)));
        api.MapPost("/settings/exceptions", async (SetWorkHoursException input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.SetExceptionAsync(input, Actor(context), ct)));
        api.MapPost("/settings/exceptions/remove", async (RemoveWorkHoursException input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.RemoveExceptionAsync(input, Actor(context), ct)));
        api.MapPut("/settings/grants", async (SetWorkHoursGrant input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.SetGrantAsync(input, Actor(context), ct)));
        api.MapGet("/correction", async (string username, string month, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.ReadCorrectionAsync(username, month, Actor(context), ct)));
        api.MapPut("/correction", async (CorrectWorkHours input, HttpContext context, WorkHoursService service, CancellationToken ct) => { await service.CorrectAsync(input, Actor(context), Role(context), ct); return Results.NoContent(); });
        api.MapPost("/remind", async (RemindWorkHours input, HttpContext context, WorkHoursService service, CancellationToken ct) => Results.Ok(await service.RemindAsync(input, Actor(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效，请重新登录。"));
}
