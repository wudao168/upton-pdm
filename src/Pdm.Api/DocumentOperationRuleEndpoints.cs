using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class DocumentOperationRuleEndpoints
{
    public sealed record SetRuleRequest(bool Enabled, long ExpectedRevision);

    public static void MapDocumentOperationRules(this WebApplication app)
    {
        var routes = app.MapGroup("/api/document-operation-rules").RequireAuthorization();
        routes.MapGet("", async (IPdmRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetDocumentOperationRulesAsync(ct)));
        // Read-only compatibility for older clients; new administration uses archive-warning-policy.
        routes.MapPut("/{ruleId}", () => Results.Json(new { message = "旧图档操作规则已停用，请使用存档警告策略。" }, statusCode: 410));
    }
}
