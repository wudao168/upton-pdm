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
        routes.MapPut("/{ruleId}", async (string ruleId, SetRuleRequest request, HttpContext context,
            PdmWorkflowService workflow, CancellationToken ct) => Results.Ok(await workflow.SetDocumentOperationRuleAsync(ruleId,
                request.Enabled, request.ExpectedRevision, context.User.Identity!.Name!,
                Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!), ct)));
    }
}
