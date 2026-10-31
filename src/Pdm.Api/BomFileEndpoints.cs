using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class BomFileEndpoints
{
    public static IEndpointRouteBuilder MapBomFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/projects/{projectId:guid}/bom-files/{itemId:guid}").RequireAuthorization();
        api.MapGet("", async (Guid projectId, Guid itemId, Guid? bomVersionId, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.ReadAsync(projectId, itemId, actor, role, ct, bomVersionId)); });
        api.MapPost("/adopt-library", async (Guid projectId, Guid itemId, FileVersionRequest input, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.AdoptLibraryAsync(projectId, itemId, input.ExpectedVersion, actor, role, ct)); });
        api.MapPost("/uploads", async (Guid projectId, Guid itemId, StartBomFileRequest input, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); var session = await service.StartAsync(projectId, itemId, input.FileName, input.TotalLength, input.Sha256, actor, role, ct); return Results.Ok(new { session.Id, session.ChunkSize }); });
        api.MapPut("/uploads/{sessionId:guid}/chunks/{index:int}", async (Guid projectId, Guid itemId, Guid sessionId, int index, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); await service.ChunkAsync(projectId, itemId, sessionId, index, context.Request.Body, actor, role, ct); return Results.NoContent(); });
        api.MapPost("/uploads/{sessionId:guid}/complete", async (Guid projectId, Guid itemId, Guid sessionId, FilePurposeRequest input, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.CompleteAsync(projectId, itemId, sessionId, input.Purpose, actor, role, ct)); });
        api.MapDelete("/{fileId:guid}", async (Guid projectId, Guid itemId, Guid fileId, long expectedVersion, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); await service.RemoveAsync(projectId, itemId, fileId, expectedVersion, actor, role, ct); return Results.NoContent(); });
        api.MapGet("/{fileId:guid}/file", async (Guid projectId, Guid itemId, Guid fileId, Guid? bomVersionId, HttpContext context, BomFileService service, CancellationToken ct) =>
        { var (actor, role) = User(context); var download = await service.DownloadAsync(projectId, itemId, fileId, actor, role, ct, bomVersionId); var preview = context.Request.Query["preview"] == "true"; return Results.File(download.Content, preview ? ProjectCenterPermissionMiddleware.PreviewContentType(download.Attachment.OriginalFileName) : Path.GetExtension(download.Attachment.OriginalFileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream", preview ? null : download.Attachment.OriginalFileName, enableRangeProcessing: true); });
        return endpoints;
    }
    private static (string, UserRole) User(HttpContext context) => (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效，请重新登录。"),
        Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role) ? role : throw new UnauthorizedAccessException("角色信息无效，请重新登录。"));
    public sealed record FileVersionRequest(long ExpectedVersion);
    public sealed record FilePurposeRequest(string Purpose);
    public sealed record StartBomFileRequest(string FileName, long TotalLength, string Sha256);
}
