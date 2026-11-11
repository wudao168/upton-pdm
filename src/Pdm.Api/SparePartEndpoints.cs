using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class SparePartEndpoints
{
    public static void MapSparePartEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/spare-parts").RequireAuthorization();
        api.MapGet("", async (Guid? projectId, string? query, string? category, int? page, int? pageSize,
            HttpContext context, SparePartService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(await service.ListAsync(projectId, query, category, page ?? 1, pageSize ?? 50, actor, role, ct));
        });
        api.MapPost("/manual", async (SpareManualInput input, HttpContext context, SparePartService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(new { added = await service.AddManualAsync(input, actor, role, ct) });
        });
        api.MapPost("/from-bom", async (SpareBomInput input, HttpContext context, SparePartService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            return Results.Ok(new { added = await service.AddBomAsync(input, actor, role, ct) });
        });
        api.MapPut("/{id:guid}/details", async (Guid id, SpareDetailsInput input, HttpContext context, SparePartService service, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            await service.SaveDetailsAsync(id, input, actor, role, ct);
            return Results.NoContent();
        });
        api.MapGet("/categories", async (HttpContext context, IPdmRepository repository, SparePartService service, CancellationToken ct) =>
        {
            await RequireAdministratorAsync(context, repository, ct);
            return Results.Ok(await service.CategoriesAsync(ct));
        });
        api.MapPut("/categories", async (SpareCategorySettings input, HttpContext context, IPdmRepository repository, SparePartService service, CancellationToken ct) =>
        {
            var (actor, _) = await RequireAdministratorAsync(context, repository, ct);
            await service.SaveCategoriesAsync(input, actor, ct);
            return Results.NoContent();
        });
        api.MapGet("/pricing", async (HttpContext context, IPdmRepository repository, SparePartService service, CancellationToken ct) =>
        {
            await RequireAdministratorAsync(context, repository, ct);
            return Results.Ok(await service.PricingAsync(ct));
        });
        api.MapPut("/pricing", async (SparePricing input, HttpContext context, IPdmRepository repository, SparePartService service, CancellationToken ct) =>
        {
            var (actor, _) = await RequireAdministratorAsync(context, repository, ct);
            await service.SavePricingAsync(input, actor, ct);
            return Results.NoContent();
        });
        api.MapPut("/{id:guid}/price", async (Guid id, SparePriceInput input, HttpContext context, IPdmRepository repository, SparePartService service, CancellationToken ct) =>
        {
            var (actor, role) = await RequireAdministratorAsync(context, repository, ct);
            await service.SavePriceAsync(id, input, actor, role, ct);
            return Results.NoContent();
        });
        api.MapPost("/synchronize", async (HttpContext context, IPdmRepository repository, SparePartService service, ILogger<SparePartService> logger, CancellationToken ct) =>
        {
            var (actor, role) = await RequireAdministratorAsync(context, repository, ct);
            try { return Results.Ok(new { matched = await service.SynchronizeAsync(actor, role, ct) }); }
            catch (Exception ex) when (ex is not OperationCanceledException and not UnauthorizedAccessException)
            {
                logger.LogError(ex, "Spare part purchase synchronization failed");
                throw new PdmRuleException("未能同步采购价。请核对公司U9C组织、采购接口及返回数据，再重新同步。");
            }
        });
        api.MapGet("/{id:guid}/drawings/{index:int}/preview", (Guid id, int index, HttpContext context, SparePartService service,
            IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow, CancellationToken ct) =>
            DrawingAsync(id, index, false, context, service, repository, storage, workflow, ct));
        api.MapGet("/{id:guid}/drawings/{index:int}/download", (Guid id, int index, HttpContext context, SparePartService service,
            IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow, CancellationToken ct) =>
            DrawingAsync(id, index, true, context, service, repository, storage, workflow, ct));
        api.MapGet("/export", async (Guid? projectId, string? query, string? category, bool? quotation, Guid[]? ids,
            HttpContext context, SparePartService service, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var page = await service.ListAsync(projectId, query, category, 1, 50, actor, role, ct,
                exportIds: (ids ?? []).ToHashSet(), quotation: quotation == true);
            if (quotation != true && !page.CanSeeCost) throw new UnauthorizedAccessException("当前账号无权导出包含成本的备件清单。");
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var user = directory.Users.FirstOrDefault(value => value.Username.Equals(actor, StringComparison.OrdinalIgnoreCase));
            var company = directory.Organizations.FirstOrDefault(value => value.Id == TenantContext.CompanyId)?.Name ?? "UPTON 阿普顿";
            var title = quotation == true ? "备品备件报价清单" : "备品备件内部清单";
            var project = page.Projects.FirstOrDefault(value => value.Id == projectId);
            var scope = project is null ? "已勾选备件" : $"{project.Code} {project.Name} · 已勾选备件";
            var metadata = new ProjectWorkbookMetadata(title, company, scope, "版本", "V1", clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)),
                [new("导出范围", scope), new("计价口径", "未税单价"), new("记录数", page.Total.ToString())],
                string.IsNullOrWhiteSpace(user?.DisplayName) ? "姓名待补充" : user.DisplayName);
            return Results.File(SparePartWorkbook.Write(page.Items, metadata, quotation == true),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{title}.xlsx");
        });
    }

    private static async Task<IResult> DrawingAsync(Guid id, int index, bool download, HttpContext context, SparePartService service,
        IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow, CancellationToken ct)
    {
        var (actor, role) = User(context);
        var part = await service.FindAsync(id, actor, role, ct);
        await ProjectPermissionPolicy.RequireAsync(repository, part.ProjectId, actor, role, "document.view", ct);
        if (download) await ProjectPermissionPolicy.RequireAsync(repository, part.ProjectId, actor, role, ProjectCenterPermissions.Download, ct);
        if (index < 0 || index >= part.Drawings.Count) return Results.NotFound();
        var drawing = part.Drawings[index];
        if (!download && !drawing.CanPreview) return Results.NotFound();
        if (drawing.BomFile is not null)
        {
            var attachment = await service.OpenAttachmentAsync(part, drawing, actor, ct);
            return Results.File(attachment.Content, download ? "application/octet-stream" : ProjectCenterPermissionMiddleware.PreviewContentType(drawing.Name),
                download ? drawing.Name : null, enableRangeProcessing: true);
        }
        if (drawing.DocumentId is not Guid documentId || drawing.VersionId is not Guid versionId) return Results.NotFound();
        var document = await repository.FindDocumentAsync(documentId, ct);
        var version = await repository.FindDocumentVersionAsync(documentId, versionId, ct);
        var project = await repository.FindProjectAsync(part.ProjectId, ct);
        if (document?.ProjectId != part.ProjectId || version is null || project is null) return Results.NotFound();
        if (!download && (version.Preview?.Format != DocumentPreviewFormat.Pdf || !string.Equals(version.Preview.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase)))
            return Results.NotFound();
        await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, download ? "document.version.download" : "document.preview.read", ct);
        var relative = download ? version.StorageRelativePath : version.Preview!.StorageRelativePath;
        if (download)
            await storage.VerifyVersionReadAsync(project, new StoredFile(relative, version.FileLength, version.Sha256, version.CreatedAt), ct);
        else
            await storage.VerifyPreviewFileAsync(project, version.Preview!, ct);
        var stream = await storage.OpenReadAsync(StorageLocationPolicy.ResolveUnder(project.VaultLocation, relative), ct);
        return Results.File(stream, download ? "application/octet-stream" : "application/pdf", download ? drawing.Name : null, enableRangeProcessing: true);
    }

    private static async Task<(string Actor, UserRole Role)> RequireAdministratorAsync(HttpContext context, IPdmRepository repository, CancellationToken ct)
    {
        var user = User(context);
        if (!await repository.HasUserPermissionAsync(user.Actor, user.Role, PermissionCodes.RoleSettingsEdit, ct))
            throw new UnauthorizedAccessException("仅获授权的管理员可维护备件设置。");
        return user;
    }

    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请重新登录。"),
         Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role)
            ? role : throw new UnauthorizedAccessException("角色信息无效，请重新登录。"));
}
