using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class QualityInspectionEndpointExtensions
{
    public static void MapQualityInspectionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/projects/{projectId:guid}/quality-upload-access", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var allowed = new List<string>();
            foreach (var kind in new[] { "quality", "incoming", "assembly", "preAcceptance", "finalAcceptance" })
                if (await QualityUploadPolicy.CanAsync(repository, projectId, kind, Actor(context), Role(context), cancellationToken)) allowed.Add(kind);
            return Results.Ok(allowed);
        });
        api.MapGet("/projects/{projectId:guid}/quality-inspections", async (Guid projectId, HttpContext context, QualityInspectionService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(projectId, Actor(context), Role(context), cancellationToken)));
        api.MapPost("/projects/{projectId:guid}/quality-inspection-uploads", async (Guid projectId, StartQualityInspectionUploadRequest request, HttpContext context, QualityInspectionService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.StartAsync(projectId, request.FileName, request.TotalLength, request.Sha256, Actor(context), Role(context), cancellationToken, request.Kind ?? "incoming")));
        api.MapPost("/projects/{projectId:guid}/quality-inspection-uploads/{sessionId:guid}/complete", async (Guid projectId, Guid sessionId, CompleteQualityInspectionUploadRequest request, HttpContext context, QualityInspectionService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.CompleteAsync(projectId, sessionId, request.Kind, request.Station, request.Title, request.Remark, Actor(context), Role(context), cancellationToken)));
        api.MapGet("/quality-inspections/{id:guid}/download", async (Guid id, HttpContext context, QualityInspectionService service, CancellationToken cancellationToken) =>
        {
            var result = await service.DownloadAsync(id, Actor(context), Role(context), cancellationToken);
            return Results.File(result.Content, "application/octet-stream", result.Record.FileName);
        });
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("登录信息无效。"));
}

public sealed record StartQualityInspectionUploadRequest(string FileName, long TotalLength, string Sha256, string? Kind = null);
public sealed record CompleteQualityInspectionUploadRequest(string Kind, string? Station, string Title, string? Remark);
