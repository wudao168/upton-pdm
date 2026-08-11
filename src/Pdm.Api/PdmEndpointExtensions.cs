using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class PdmEndpointExtensions
{
    public static void MapPdmEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HealthAsync).AllowAnonymous();

        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            IPdmRepository repository,
            IPasswordService passwords,
            ITokenIssuer tokenIssuer,
            IOptions<AuthenticationOptions> authenticationOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var account = await repository.FindUserAsync(request.Username.Trim(), cancellationToken);
            if (account is null || !account.IsActive || !passwords.Verify(request.Password, account.PasswordHash))
            {
                return Results.Unauthorized();
            }

            var lifetime = TimeSpan.FromHours(authenticationOptions.Value.TokenLifetimeHours);
            var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(account, lifetime),
                expiresAt,
                account.Username,
                account.DisplayName,
                account.Role.ToString()));
        }).AllowAnonymous();

        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/projects", async (IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListProjectsAsync(cancellationToken)));

        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        api.MapGet("/projects/{projectId:guid}/documents", async (Guid projectId, IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListDocumentsAsync(projectId, cancellationToken)));

        api.MapGet("/projects/{projectId:guid}/reference-tree", async (Guid projectId, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var tree = await repository.GetReferenceTreeAsync(projectId, cancellationToken);
            return tree is null ? Results.NotFound() : Results.Ok(tree);
        });

        api.MapGet("/projects/{projectId:guid}/boms/{kind}", async (Guid projectId, string kind, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind))
            {
                return Results.BadRequest(new { message = "BOM类型必须是Mechanical或Electrical。" });
            }

            return Results.Ok(await repository.GetBomAsync(projectId, bomKind, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/release-packages", async (Guid projectId, IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListReleasePackagesAsync(projectId, cancellationToken)));

        api.MapPost("/documents/{documentId:guid}/checkout", async (Guid documentId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CheckoutAsync(documentId, actor, role, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/checkin", async (Guid documentId, CheckInRequest request, HttpContext context, PdmWorkflowService workflow, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var rootJson = JsonSerializer.Serialize(request.Root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var snapshot = new CadReferenceSnapshot(
                Guid.NewGuid(),
                request.ProjectId,
                documentId,
                timeProvider.GetUtcNow(),
                actor,
                request.Root,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootJson))));
            return Results.Ok(await workflow.CheckInAsync(documentId, actor, role, snapshot, cancellationToken));
        });

        api.MapPost("/release-packages", async (CreateReleasePackageRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CreateReleasePackageAsync(
                request.ProjectId,
                request.ReferenceSnapshotId,
                request.Number,
                request.MechanicalBomRevision,
                request.ElectricalBomRevision,
                request.ProcessReviewer,
                request.Approver,
                actor,
                role,
                cancellationToken));
        });

        api.MapPost("/approval-tasks/{taskId:guid}/decision", async (Guid taskId, ApprovalRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.DecideAsync(taskId, actor, role, request.Decision, request.Comment, cancellationToken));
        });

        api.MapPost("/uploads/sessions", async (StartUploadRequest request, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.StartUploadAsync(request.ProjectId, request.FileName, request.TotalLength, request.Sha256, cancellationToken)));

        api.MapPut("/uploads/sessions/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.WriteChunkAsync(sessionId, chunkIndex, request.Body, cancellationToken)));

        api.MapPost("/uploads/sessions/{sessionId:guid}/complete", async (Guid sessionId, CompleteUploadRequest request, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.CompleteUploadAsync(sessionId, request.RelativeTargetPath, cancellationToken)));

        api.MapGet("/projects/{projectId:guid}/storage-status", async (Guid projectId, IPdmRepository repository, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            var vaultAvailable = await storage.IsAvailableAsync(project.VaultLocation, cancellationToken);
            var releaseAvailable = await storage.IsAvailableAsync(project.ReleaseLocation, cancellationToken);
            return Results.Ok(new { projectId, vaultAvailable, releaseAvailable });
        });
    }

    private static async Task<IResult> HealthAsync(IOptions<PdmDatabaseOptions> options, CancellationToken cancellationToken)
    {
        var provider = options.Value.Provider;
        if (!string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { status = "ok", service = "upton-pdm-api", database = provider, apiPort = 5080, mysqlPort = 3308 });
        }

        try
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return Results.Ok(new { status = "ok", service = "upton-pdm-api", database = "MySql", apiPort = 5080, mysqlPort = 3308 });
        }
        catch (Exception exception)
        {
            return Results.Json(new { status = "degraded", service = "upton-pdm-api", database = "MySql", error = exception.Message, apiPort = 5080, mysqlPort = 3308 }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}
