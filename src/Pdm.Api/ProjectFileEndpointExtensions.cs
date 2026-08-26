using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ProjectFileEndpointExtensions
{
    public static void MapProjectFileEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/projects/{projectId:guid}/files", async (Guid projectId, Guid? folderId, bool? includeDeleted, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListAsync(projectId, folderId, includeDeleted ?? false, actor, role, cancellationToken)).Select(MapFile));
        });
        api.MapPost("/projects/{projectId:guid}/folders/{folderId:guid}/children", async (Guid projectId, Guid folderId, CreateProjectFolderRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFolder(await service.CreateFolderAsync(projectId, folderId, request.Name, actor, role, cancellationToken)));
        });
        api.MapPatch("/projects/{projectId:guid}/folders/{folderId:guid}", async (Guid projectId, Guid folderId, RenameProjectEntryRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFolder(await service.RenameFolderAsync(projectId, folderId, request.Name, actor, role, cancellationToken)));
        });
        api.MapPost("/projects/{projectId:guid}/folders/{folderId:guid}/move", async (Guid projectId, Guid folderId, MoveProjectEntryRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFolder(await service.MoveFolderAsync(projectId, folderId, request.FolderId, actor, role, cancellationToken)));
        });
        api.MapDelete("/projects/{projectId:guid}/folders/{folderId:guid}", async (Guid projectId, Guid folderId, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await service.DeleteFolderAsync(projectId, folderId, actor, role, cancellationToken);
            return Results.NoContent();
        });
        api.MapPost("/projects/{projectId:guid}/folders/{folderId:guid}/file-uploads", async (Guid projectId, Guid folderId, StartProjectFileUploadRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.StartUploadAsync(projectId, folderId, request.FileName, request.TotalLength, request.Sha256, actor, role, cancellationToken));
        });
        api.MapPut("/project-file-uploads/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            return Results.Ok(await service.WriteChunkAsync(sessionId, chunkIndex, request.Body, actor, cancellationToken));
        });
        api.MapPost("/project-file-uploads/{sessionId:guid}/complete", async (Guid sessionId, CompleteProjectFileUploadRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFile(await service.CompleteUploadAsync(sessionId, request.Comment, actor, role, cancellationToken)));
        });
        api.MapDelete("/project-file-uploads/{sessionId:guid}", async (Guid sessionId, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            await service.CancelUploadAsync(sessionId, actor, cancellationToken);
            return Results.NoContent();
        });
        api.MapGet("/projects/{projectId:guid}/files/{fileId:guid}/versions", async (Guid projectId, Guid fileId, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok((await service.ListVersionsAsync(projectId, fileId, actor, role, cancellationToken)).Select(MapVersion));
        });
        api.MapGet("/projects/{projectId:guid}/files/{fileId:guid}/content", async (Guid projectId, Guid fileId, Guid? versionId, bool? download, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await service.OpenDownloadAsync(projectId, fileId, versionId, actor, role, cancellationToken);
            return Results.File(result.Content, ContentType(result.Version.FileName), download == false ? null : result.File.FileName, enableRangeProcessing: true);
        });
        api.MapPatch("/projects/{projectId:guid}/files/{fileId:guid}", async (Guid projectId, Guid fileId, RenameProjectEntryRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFile(await service.RenameAsync(projectId, fileId, request.Name, actor, role, cancellationToken)));
        });
        api.MapPost("/projects/{projectId:guid}/files/{fileId:guid}/move", async (Guid projectId, Guid fileId, MoveProjectEntryRequest request, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFile(await service.MoveAsync(projectId, fileId, request.FolderId, actor, role, cancellationToken)));
        });
        api.MapDelete("/projects/{projectId:guid}/files/{fileId:guid}", async (Guid projectId, Guid fileId, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFile(await service.SetDeletedAsync(projectId, fileId, true, actor, role, cancellationToken)));
        });
        api.MapPost("/projects/{projectId:guid}/files/{fileId:guid}/restore", async (Guid projectId, Guid fileId, HttpContext context, ProjectFileService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapFile(await service.SetDeletedAsync(projectId, fileId, false, actor, role, cancellationToken)));
        });
    }

    private static object MapFile(ProjectFile file) => new { file.Id, file.RootProjectId, file.FolderId, file.FileName, file.CreatedBy, file.CreatedAt, file.UpdatedBy, file.UpdatedAt, file.DeletedAt, file.DeletedBy, CurrentVersion = file.CurrentVersion is null ? null : MapVersion(file.CurrentVersion) };
    private static object MapVersion(ProjectFileVersion version) => new { version.Id, version.ProjectFileId, version.VersionNumber, version.FileName, version.FileLength, version.Sha256, version.UploadedBy, version.UploadedAt, version.Comment };
    private static object MapFolder(ProjectFolder folder) => new { folder.Id, folder.RootProjectId, folder.ParentFolderId, folder.TargetProjectId, folder.FolderKey, folder.TemplateKey, folder.Name, Purpose = folder.Purpose.ToString(), folder.SortOrder, folder.IsSystem, folder.InheritPermissions };
    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".txt" or ".csv" or ".md" => "text/plain; charset=utf-8", ".mp4" => "video/mp4", _ => "application/octet-stream" };
    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
        var role = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。");
        return (actor, Enum.Parse<UserRole>(role));
    }
}
