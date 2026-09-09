using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class EngineeringKitEndpointExtensions
{
    public static void MapEngineeringKitEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/engineering-kits").RequireAuthorization();

        api.MapGet("", async (bool? releasedOnly, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListAsync(releasedOnly ?? true, actor, role, cancellationToken));
        });

        api.MapGet("/{kitId:guid}", async (Guid kitId, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetAsync(kitId, actor, role, cancellationToken));
        });

        api.MapPost("/", async (SaveEngineeringKitRequest request, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveDraftAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/{kitId:guid}", async (Guid kitId, SaveEngineeringKitRequest request, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveDraftAsync(kitId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPost("/{kitId:guid}/publish", async (Guid kitId, PublishEngineeringKitRequest request, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PublishAsync(kitId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapPost("/{kitId:guid}/expand", async (Guid kitId, ExpandEngineeringKitRequest request, HttpContext context, EngineeringKitService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ExpandAsync(kitId, request.ToCommand(), actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}

public sealed record SaveEngineeringKitComponentRequest(Guid MaterialId, decimal Quantity, bool IsOptional, int SortOrder)
{
    public SaveEngineeringKitComponentCommand ToCommand() => new(MaterialId, Quantity, IsOptional, SortOrder);
}

public sealed record SaveEngineeringKitRequest(
    string Name,
    string? Description,
    string? ChangeNote,
    IReadOnlyList<SaveEngineeringKitComponentRequest> Components,
    long? ExpectedRowVersion = null)
{
    public SaveEngineeringKitDraftCommand ToCommand() => new(Name, Description, ChangeNote, Components.Select(item => item.ToCommand()).ToArray(), ExpectedRowVersion);
}

public sealed record PublishEngineeringKitRequest(long ExpectedRowVersion);

public sealed record ExpandEngineeringKitRequest(Guid? RevisionId, decimal Quantity, IReadOnlyList<Guid>? SelectedOptionalComponentIds)
{
    public ExpandEngineeringKitCommand ToCommand() => new(RevisionId, Quantity, SelectedOptionalComponentIds ?? []);
}
