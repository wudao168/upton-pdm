using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectContactInformationEndpointExtensions
{
    public static void MapProjectContactInformationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects").RequireAuthorization();
        api.MapGet("/{projectId:guid}/contact-information", async (Guid projectId, HttpContext context, ProjectContactInformationService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(projectId, Actor(context), Role(context), ct)));
        api.MapPut("/{projectId:guid}/contact-information", async (Guid projectId, SaveProjectContactInformationCommand command, HttpContext context, ProjectContactInformationService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(projectId, command, Actor(context), Role(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
