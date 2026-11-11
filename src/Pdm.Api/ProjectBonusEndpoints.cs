using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Api;
public static class ProjectBonusEndpoints
{
    public static void MapProjectBonusEndpoints(this WebApplication app)
    {
        var settings = app.MapGroup("/api/settings/project-bonus").RequireAuthorization();
        settings.MapGet("", async (HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.SettingsAsync(Actor(c), Role(c), ct)));
        settings.MapPut("", async (ProjectBonusSettings input, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.SaveSettingsAsync(input, Actor(c), Role(c), ct)));
        var api = app.MapGroup("/api/projects/{projectId:guid}/bonus").RequireAuthorization();
        api.MapGet("", async (Guid projectId, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.GetAsync(projectId, Actor(c), Role(c), ct)));
        api.MapGet("/history", async (Guid projectId, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.HistoryAsync(projectId, Actor(c), Role(c), ct)));
        api.MapPut("/{phase}", async (Guid projectId, string phase, SaveBonusDraft input, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.SaveAsync(projectId, phase, input, Actor(c), Role(c), ct)));
        api.MapPost("/{phase}/scores", async (Guid projectId, string phase, SaveBonusScores input, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.ScoreAsync(projectId, phase, input, Actor(c), Role(c), ct)));
        api.MapPost("/{phase}/payment", async (Guid projectId, string phase, BonusPaymentCommand input, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.PayAsync(projectId, phase, input, Actor(c), Role(c), ct)));
        api.MapPost("/{phase}/{action}", async (Guid projectId, string phase, string action, BonusAction input, HttpContext c, ProjectBonusService s, CancellationToken ct) => Results.Ok(await s.ActAsync(projectId, phase, action, input, Actor(c), Role(c), ct)));
    }
    private static string Actor(HttpContext c) => c.User.Identity?.Name ?? throw new UnauthorizedAccessException("请先登录。");
    private static UserRole Role(HttpContext c) => Enum.Parse<UserRole>(c.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("登录角色无效。"));
}
