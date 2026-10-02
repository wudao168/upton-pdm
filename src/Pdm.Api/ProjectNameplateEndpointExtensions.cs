using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectNameplateEndpointExtensions
{
    public static void MapProjectNameplateEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/projects/{projectId:guid}/nameplate", async (Guid projectId, HttpContext context, ProjectNameplateService service, CancellationToken ct) => Results.Ok(await service.GetAsync(projectId, Actor(context), Role(context), ct)));
        api.MapPut("/projects/{projectId:guid}/nameplate", async (Guid projectId, SaveProjectNameplateCommand command, HttpContext context, ProjectNameplateService service, CancellationToken ct) => Results.Ok(await service.SaveAsync(projectId, command, Actor(context), Role(context), ct)));
        api.MapPut("/nameplate-template", async (NameplateTemplate command, HttpContext context, ProjectNameplateService service, CancellationToken ct) => Results.Ok(await service.SaveTemplateAsync(command, Actor(context), Role(context), ct)));
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。"));
}
