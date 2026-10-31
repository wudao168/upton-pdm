using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ArchiveWarningEndpoints
{
    public static void MapArchiveWarningPolicy(this WebApplication app)
    {
        var routes = app.MapGroup("/api/archive-warning-policy").RequireAuthorization();
        routes.MapGet("", async (IPdmRepository repository, CancellationToken ct) => Results.Ok(await repository.GetArchiveWarningPolicyAsync(ct)));
        routes.MapGet("/catalog", () => Results.Ok(ArchiveWarningPolicy.Catalog));
        routes.MapPut("", async (ArchiveWarningPolicy input, HttpContext context, PdmWorkflowService workflow, CancellationToken ct) =>
            Results.Ok(await workflow.SaveArchiveWarningPolicyAsync(input, context.User.Identity!.Name!,
                Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role)!), ct)));
    }
}
