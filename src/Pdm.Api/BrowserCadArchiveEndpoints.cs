using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class BrowserCadArchiveEndpoints
{
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请先登录。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("登录角色无效。")));
    public static void MapBrowserCadArchives(this WebApplication app)
    {
        var api = app.MapGroup("/api/browser-archives").RequireAuthorization();
        api.MapGet("/capabilities", async (Guid? projectId, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.CapabilityAsync(new(projectId), actor, role, ct)); });
        api.MapPut("/settings", async (BrowserCadArchiveSettings input, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); await service.SetEnabledAsync(input.Enabled, actor, role, ct); return Results.Ok(input); });
        api.MapPost("/{id:guid}/files", async (Guid id, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            // Reject disabled/no-permission requests before buffering an upload. The target is carried in the query.
            Guid? project = Guid.TryParse(context.Request.Query["projectId"], out var projectId) ? projectId : null;
            await service.RequireAsync(new(project), actor, role, ct);
            const long maximum = 2L * 1024 * 1024 * 1024;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = maximum;
            context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions { MultipartBodyLengthLimit = maximum }));
            if (!context.Request.HasFormContentType) throw new PdmRuleException("请选择图档文件后存档。");
            var form = await context.Request.ReadFormAsync(ct); BrowserCadArchiveTarget target;
            try { target = JsonSerializer.Deserialize<BrowserCadArchiveTarget>(form["target"].ToString(), BrowserCadArchiveQueue.Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new PdmRuleException("存档归属信息无效，请重新选择。"); }
            if (target.ProjectId != project) throw new PdmRuleException("上传项目与存档归属不一致，未存档。");
            return Results.Ok(await service.StageAsync(id, target, form.Files.ToArray(), actor, role, ct));
        }).DisableAntiforgery();
        api.MapGet("/{id:guid}", (Guid id, HttpContext context, BrowserCadArchiveService service) => Results.Ok(service.Read(id, User(context).Actor)));
        api.MapPost("/{id:guid}/start", async (Guid id, BrowserCadArchiveStart input, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.StartAsync(id, input.RootPath, actor, role, ct)); });
        api.MapPost("/{id:guid}/previews/retry", async (Guid id, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.RetryPreviewsAsync(id, actor, role, ct)); });
    }
}
public sealed record BrowserCadArchiveSettings(bool Enabled);
public sealed record BrowserCadArchiveStart(string RootPath);
