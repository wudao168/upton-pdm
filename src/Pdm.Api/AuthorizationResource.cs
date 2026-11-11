using System.Text.Json;
using System.Text.RegularExpressions;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

internal static class AuthorizationResource
{
    public static async Task<Guid?> ProjectAsync(HttpContext http, IPdmRepository repository)
    {
        const string key = "authorization.resource.project";
        if (http.Items.TryGetValue(key, out var cached)) return cached as Guid?;
        var ct = http.RequestAborted;
        var path = http.Request.Path.Value ?? "";
        Guid? id = null;
        Guid? Route(string name) => Guid.TryParse(http.Request.RouteValues.GetValueOrDefault(name)?.ToString(), out var value) ? value : null;
        id = Route("projectId");
        var match = Regex.Match(path, @"^/api/(projects|documents|drawing-reviews|release-packages|approval-tasks)/([0-9a-f-]{36})(/|$)", RegexOptions.IgnoreCase);
        if (id is null && match.Success && Guid.TryParse(match.Groups[2].Value, out var value))
            id = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "projects" => value,
                "documents" => (await repository.FindDocumentIncludingDeletedAsync(value, ct))?.ProjectId,
                "drawing-reviews" => (await repository.FindDrawingReviewPackageAsync(value, ct))?.ProjectId,
                "release-packages" => (await repository.FindReleasePackageAsync(value, ct))?.ProjectId,
                "approval-tasks" => (await repository.FindReleasePackageByApprovalTaskAsync(value, ct))?.ProjectId,
                _ => null
            };
        if (id is null && Route("documentId") is Guid document) id = (await repository.FindDocumentIncludingDeletedAsync(document, ct))?.ProjectId;
        if (id is null && Route("releasePackageId") is Guid package) id = (await repository.FindReleasePackageAsync(package, ct))?.ProjectId;
        if (id is null && Guid.TryParse(http.Request.Query["projectId"], out var query)) id = query;
        if (id is null && http.Request.ContentLength != 0 && http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            http.Request.EnableBuffering();
            try
            {
                using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("projectId", out var field) && field.ValueKind == JsonValueKind.String && Guid.TryParse(field.GetString(), out var bodyProject)) id = bodyProject;
            }
            catch (JsonException) { throw new PdmRuleException("请求内容格式无效，请刷新页面后重试。"); }
            finally { http.Request.Body.Position = 0; }
        }
        http.Items[key] = id;
        return id;
    }
}
