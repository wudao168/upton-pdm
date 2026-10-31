using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

/// <summary>目录注册的操作自动接入接口校验；原业务、目录和流程校验继续执行。</summary>
public sealed class FunctionPermissionMiddleware(RequestDelegate next)
{
    private static readonly (PermissionDefinition Permission, FunctionRoute Route, Regex Pattern)[] Operations = RolePermissionCatalog.Permissions
        .SelectMany(permission => permission.Routes.Select(route => (permission, route, new Regex("^" + string.Join("/", route.Path.Split('/').Select(segment => segment.StartsWith('{') ? "[^/]+" : Regex.Escape(segment))) + "/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase))))
        .ToArray();

    public async Task InvokeAsync(HttpContext context, IPdmRepository repository)
    {
        if (context.User.Identity?.IsAuthenticated != true && Operations.Any(item => item.Route.Method == context.Request.Method && item.Pattern.IsMatch(context.Request.Path.Value ?? "")))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var match = Operations.FirstOrDefault(item => item.Route.Method == context.Request.Method && item.Pattern.IsMatch(context.Request.Path.Value ?? ""));
            if (match.Permission is { } permission)
            {
                if (permission.Code == "project.opl.download" && context.Request.Query["preview"] == "true") { await next(context); return; }
                var actor = context.User.Identity.Name ?? "";
                if (string.IsNullOrWhiteSpace(actor) || !Enum.TryParse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role), out var role))
                    throw new UnauthorizedAccessException("当前登录身份不能执行此功能，请使用有效的业务账号登录。");
                if (!await repository.HasUserPermissionAsync(actor, role, permission.Code, context.RequestAborted)) throw new UnauthorizedAccessException("当前角色未授权此功能操作，请联系管理员核对功能权限。");
                Guid? projectId = Guid.TryParse(context.Request.RouteValues.GetValueOrDefault("projectId")?.ToString(), out var project) ? project : null;
                if (projectId is null && Guid.TryParse(context.Request.RouteValues.GetValueOrDefault("documentId")?.ToString(), out var document)) projectId = (await repository.FindDocumentAsync(document, context.RequestAborted))?.ProjectId;
                if (projectId is null && Guid.TryParse(context.Request.RouteValues.GetValueOrDefault("releasePackageId")?.ToString(), out var package)) projectId = (await repository.FindReleasePackageAsync(package, context.RequestAborted))?.ProjectId;
                if (projectId is null && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    context.Request.EnableBuffering();
                    try
                    {
                        using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                        if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("projectId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var value)) projectId = value;
                    }
                    catch (JsonException) { throw new PdmRuleException("请求内容格式无效，请刷新页面后重试。"); }
                    finally { context.Request.Body.Position = 0; }
                }
                if (projectId is Guid target) await ProjectPermissionPolicy.RequireAsync(repository, target, actor, role, permission.Code, context.RequestAborted);
            }
        }
        await next(context);
    }
}
