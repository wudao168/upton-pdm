using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class StandardStructureEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。")));

    public static void MapStandardStructureEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/standard-structures").RequireAuthorization();
        api.MapGet("/capabilities", async (HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.CapabilitiesAsync(actor, role, ct));
        });
        api.MapGet("/", async (HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReadAsync(actor, role, ct));
        });
        api.MapPut("/categories/{id:guid}", async (Guid id, SaveStandardStructureCategory input, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.SaveCategoryAsync(id, input, actor, role, ct));
        });
        api.MapDelete("/categories/{id:guid}", async (Guid id, long expectedRowVersion, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.DeleteCategoryAsync(id, expectedRowVersion, actor, role, ct));
        });
        api.MapPost("/categories/{id:guid}/move", async (Guid id, MoveStandardStructureCategory input, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.MoveCategoryAsync(id, input.Direction, input.ExpectedRowVersion, actor, role, ct));
        });
        api.MapPost("/classifications", async (MoveStandardStructures input, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.MoveStructuresAsync(input, actor, role, ct));
        });
        api.MapGet("/storage", async (HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReadStorageAsync(actor, role, ct));
        });
        api.MapPut("/storage", async (StandardStructureStorageRequest input, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var catalog = await service.ConfigureStorageAsync(input.StorageRoot, input.ExpectedRowVersion, actor, role, ct);
            return Results.Ok(new { catalog.StorageRoot, catalog.RowVersion });
        });
        api.MapPost("/archive", async (HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            if (!(await service.ReadAsync(actor, role, ct)).CanArchive) throw new UnauthorizedAccessException("只有标准化账号可以通过插件存档标准结构。");
            const long maximumArchiveBytes = 2L * 1024 * 1024 * 1024;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = maximumArchiveBytes;
            context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions { MultipartBodyLengthLimit = maximumArchiveBytes }));
            if (!context.Request.HasFormContentType) throw new PdmRuleException("请通过SolidWorks插件提交标准结构存档。");
            var form = await context.Request.ReadFormAsync(ct);
            ArchiveStandardStructure input;
            try { input = JsonSerializer.Deserialize<ArchiveStandardStructure>(form["manifest"].ToString(), Json) ?? throw new JsonException(); }
            catch (JsonException) { throw new PdmRuleException("存档清单无效，请更新插件并重新存档。"); }
            var uploads = form.Files.Select(f => new StandardStructureUpload(f.Name, f.FileName, f.OpenReadStream())).ToArray();
            try { return Results.Ok(await service.ArchiveAsync(input, uploads, actor, role, ct)); }
            finally { foreach (var upload in uploads) await upload.Content.DisposeAsync(); }
        }).DisableAntiforgery();
        api.MapGet("/revisions/{revisionId:guid}/preview/{fileKey}", async (Guid revisionId, string fileKey, HttpContext context, StandardStructureService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var (content, name) = await service.PreviewAsync(revisionId, fileKey, actor, role, ct);
            return Results.Stream(content, Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/step", enableRangeProcessing: true);
        });
        api.MapPost("/references", async (ReferenceStandardStructure input, HttpContext context, StandardStructureService service, PdmWorkflowService workflow, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ReferenceAsync(input, workflow, actor, role, ct));
        });
    }
}
public sealed record StandardStructureStorageRequest(string StorageRoot, long ExpectedRowVersion);
public sealed record MoveStandardStructureCategory(int Direction, long ExpectedRowVersion);
