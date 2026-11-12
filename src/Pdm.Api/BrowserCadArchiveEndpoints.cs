using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class BrowserCadArchiveEndpoints
{
    private const string ProjectArchiveRemoved = "项目网页存档已停用，请使用 SolidWorks 插件存档。";

    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请先登录。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("登录角色无效。")));
    public static void MapBrowserCadArchives(this WebApplication app)
    {
        var api = app.MapGroup("/api/browser-archives").RequireAuthorization();
        api.MapGet("/capabilities", async (Guid? projectId, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        {
            if (projectId.HasValue) return Results.Ok(new BrowserCadArchiveCapability(false, false, false, ProjectArchiveRemoved));
            var (actor, role) = User(context);
            return Results.Ok(await service.CapabilityAsync(new(), actor, role, ct));
        });
        api.MapPut("/settings", async (BrowserCadArchiveSettings input, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); await service.SetEnabledAsync(input.Enabled, actor, role, ct); return Results.Ok(input); });
        api.MapPost("/{id:guid}/files", async (Guid id, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            // Reject old project uploads before buffering the multipart body.
            if (context.Request.Query.ContainsKey("projectId")) throw new PdmRuleException(ProjectArchiveRemoved);
            await service.RequireAsync(new(), actor, role, ct);
            const long maximum = 2L * 1024 * 1024 * 1024;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = maximum;
            context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions { MultipartBodyLengthLimit = maximum }));
            if (!context.Request.HasFormContentType) throw new PdmRuleException("请选择图档文件后存档。");
            var form = await context.Request.ReadFormAsync(ct); BrowserCadArchiveTarget target;
            try { target = JsonSerializer.Deserialize<BrowserCadArchiveTarget>(form["target"].ToString(), BrowserCadArchiveQueue.Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new PdmRuleException("存档归属信息无效，请重新选择。"); }
            if (target.ProjectId.HasValue) throw new PdmRuleException(ProjectArchiveRemoved);
            return Results.Ok(await service.StageAsync(id, target, form.Files.ToArray(), actor, role, ct));
        }).DisableAntiforgery();
        api.MapGet("/{id:guid}", (Guid id, HttpContext context, BrowserCadArchiveService service) => Results.Ok(service.Read(id, User(context).Actor)));
        api.MapPost("/{id:guid}/start", async (Guid id, BrowserCadArchiveStart input, HttpContext context, BrowserCadArchiveQueue queue, BrowserCadArchiveService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var job = queue.Read(id) ?? throw new PdmNotFoundException("存档任务不存在。");
            BrowserCadArchiveQueue.RequireOwner(job, actor);
            if (job.Target.ProjectId.HasValue) throw new PdmRuleException(ProjectArchiveRemoved);
            return Results.Ok(await service.StartAsync(id, input.RootPath, actor, role, ct));
        });
        api.MapPost("/{id:guid}/previews/retry", async (Guid id, HttpContext context, BrowserCadArchiveService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.RetryPreviewsAsync(id, actor, role, ct)); });
    }
}
public sealed record BrowserCadArchiveSettings(bool Enabled);
public sealed record BrowserCadArchiveStart(string RootPath);
