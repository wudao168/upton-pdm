using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ControlledDocumentRecycleEndpointExtensions
{
    public static void MapControlledDocumentRecycleEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/projects/{projectId:guid}/documents").RequireAuthorization();
        api.MapGet("/archive-batches", async (Guid projectId, HttpContext context, ArchiveRecycleService service, CancellationToken ct) =>
        { var (actor, role) = CurrentUser(context.User); return Results.Ok(await service.ListBatchesAsync(projectId, actor, role, ct)); });
        api.MapGet("/archive-batches/{batchId:guid}/recycle-preview", async (Guid projectId, Guid batchId, HttpContext context, ArchiveRecycleService service, CancellationToken ct) =>
        { var (actor, role) = CurrentUser(context.User); return Results.Ok(await service.PreviewAsync(projectId, batchId, actor, role, ct)); });
        api.MapPost("/archive-batches/{batchId:guid}/recycle", async (Guid projectId, Guid batchId, RecycleArchiveSetRequest request, HttpContext context, ArchiveRecycleService service, CancellationToken ct) =>
        { var (actor, role) = CurrentUser(context.User); return Results.Ok(await service.RecycleAsync(projectId, batchId, request.ScopeHash, request.Reason, actor, role, ct)); });
        api.MapGet("/recycle-sets", async (Guid projectId, HttpContext context, ArchiveRecycleService service, CancellationToken ct) =>
        { var (actor, role) = CurrentUser(context.User); return Results.Ok(await service.ListSetsAsync(projectId, actor, role, ct)); });
        api.MapPost("/recycle-sets/{setId:guid}/restore", async (Guid projectId, Guid setId, HttpContext context, ArchiveRecycleService service, CancellationToken ct) =>
        { var (actor, role) = CurrentUser(context.User); return Results.Ok(await service.RestoreAsync(projectId, setId, actor, role, ct)); });
        api.MapGet("/recycle-bin", async (Guid projectId, HttpContext context, ControlledDocumentRecycleService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListAsync(projectId, actor, role, cancellationToken));
        });
        api.MapGet("/{documentId:guid}/recycle-readiness", async (Guid projectId, Guid documentId, HttpContext context, ControlledDocumentRecycleService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetReadinessAsync(projectId, documentId, actor, role, cancellationToken));
        });
        api.MapPost("/{documentId:guid}/recycle", async (Guid projectId, Guid documentId, RecycleControlledDocumentRequest request, HttpContext context, ControlledDocumentRecycleService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.RecycleAsync(projectId, documentId, request.ExpectedRowVersion, request.Reason, request.Confirmation, actor, role, cancellationToken));
        });
        api.MapPost("/{documentId:guid}/restore", async (Guid projectId, Guid documentId, RestoreControlledDocumentRequest request, HttpContext context, ControlledDocumentRecycleService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.RestoreAsync(projectId, documentId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });
        api.MapPost("/recycle-batch", async (Guid projectId, RecycleControlledDocumentBatchRequest request, HttpContext context, ControlledDocumentRecycleService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.RecycleBatchAsync(projectId, request.Items, request.Reason, actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(role));
    }
}

public sealed record RecycleControlledDocumentRequest(long ExpectedRowVersion, string Reason, string Confirmation);
public sealed record RestoreControlledDocumentRequest(long ExpectedRowVersion);
public sealed record RecycleControlledDocumentBatchRequest(IReadOnlyList<ControlledDocumentRecycleBatchItem> Items, string Reason);
public sealed record RecycleArchiveSetRequest(string ScopeHash, string Reason);
