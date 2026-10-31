using System.Security.Claims;
using System.Text.RegularExpressions;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>项目中心入口、只读及文件输出的统一边界；原有业务授权继续执行。</summary>
public sealed class ProjectCenterPermissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPdmRepository repository)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        var projectResource = Regex.Match(path, @"^/api/projects/[0-9a-f-]{36}/(.*)$");
        var resource = projectResource.Success ? projectResource.Groups[1].Value : "";
        var tab = projectResource.Success ? TabFor(resource) : TabForGlobal(path);
        var projectApi = projectResource.Success || tab is not null || path is "/api/release-packages" or "/api/documents/version-verifications"
            || path.StartsWith("/api/uploads/") || path.StartsWith("/api/project-file-uploads/");
        if (!projectApi || context.User.Identity?.IsAuthenticated != true) { await next(context); return; }
        var actor = context.User.Identity.Name ?? "";
        if (!Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role))
            throw new UnauthorizedAccessException("登录信息无效，请重新登录。");
        var permissions = await repository.GetUserPermissionsAsync(actor, role, context.RequestAborted);
        bool Allows(string code) => role == UserRole.Administrator || permissions.Contains(code);
        void Require(string code, string message) { if (!Allows(code)) throw new UnauthorizedAccessException(message); }
        if (tab is not null) Require(ProjectCenterPermissions.Tab(tab), "当前角色无权查看此项目页签，请联系管理员核对角色权限。");

        var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        // 查看标记与版本校验仅记录查阅状态，不修改业务内容。
        var readPost = HttpMethods.IsPost(context.Request.Method) && (Regex.IsMatch(resource, @"^opl/[0-9a-f-]{36}/view$") || path == "/api/documents/version-verifications");
        var archive = resource == "release-preview-items/archive";
        var preview = context.Request.Query["preview"] == "true";
        var inlinePreview = preview && (resource.StartsWith("bom-files/") || resource.StartsWith("procurement-files/")
            || Regex.IsMatch(resource, @"^opl/[^/]+/attachments/[^/]+$") || path.StartsWith("/api/quality-inspections/"));
        var download = archive || context.Request.Query["download"] == "true"
            || !inlinePreview && (path.EndsWith("/download") || path.EndsWith("/content")
                || path.EndsWith("/file") && !resource.StartsWith("release-preview-items/")
                || Regex.IsMatch(resource, @"^opl/[^/]+/attachments/[^/]+$"));
        if (download) Require(ProjectCenterPermissions.Download, "当前角色仅允许在线查看，未开放文件下载。");
        if (path.EndsWith("/export") || resource == "export-metadata")
            Require(ProjectCenterPermissions.Export, "当前角色未开放数据导出。");
        if (!read && !readPost && !archive)
            Require(ProjectCenterPermissions.Write, "当前角色仅允许查看项目资料，不能执行编辑或业务处理操作。");
        await next(context);
    }

    private static string? TabFor(string resource)
    {
        var first = resource.Split('/')[0];
        return first switch
        {
            "contact-information" or "nameplate" or "position-members" or "dingtalk" => "information",
            "files" => "files",
            "plan" => "project-plan", "opl" => "opl", "budget" => "budget",
            "validation-plan" or "quality-acceptance" or "quality-inspections" or "quality-upload-access" or "quality-inspection-uploads" => "validation-plan",
            "documents" or "folder-documents" or "document-relations" or "reference-tree" or "drawing-reviews" => "documents",
            "boms" or "bom-files" or "bom-source-data" or "bom-versions" or "bom-headers" => "bom",
            "release-packages" or "release-preview-items" or "manufacturing-bom-baselines" => "release",
            "procurement-tracking" or "procurement-files" => "procurement",
            "audit" => "records",
            _ => null
        };
    }

    private static string? TabForGlobal(string path)
    {
        if (path.StartsWith("/api/documents/") || path.StartsWith("/api/drawing-reviews/")) return "documents";
        if (path.StartsWith("/api/release-packages/") || path.StartsWith("/api/approval-tasks/") && !path.EndsWith("/mine")) return "release";
        if (path.StartsWith("/api/validation-plan") || path.StartsWith("/api/quality-inspections/") || path.StartsWith("/api/quality-acceptance")) return "validation-plan";
        if (path.StartsWith("/api/production-tracking/releases/")) return "procurement";
        if (path.StartsWith("/api/browser-archives/")) return "documents";
        if (path.StartsWith("/api/workbench/risks/")) return "overview";
        return null;
    }

    public static string PreviewContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        _ => throw new UnauthorizedAccessException("此文件暂不支持在线预览，当前角色未开放下载，请联系资料负责人提供PDF或图片。")
    };
}
