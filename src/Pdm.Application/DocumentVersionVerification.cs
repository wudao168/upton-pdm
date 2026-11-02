using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record DocumentVersionVerificationRequest(Guid DocumentId, Guid? BoundVersionId);

public sealed record DocumentVersionVerification(
    Guid Id, Guid DocumentId, string Revision, long FileLength, string Sha256, string? SourceFileSha256);

public sealed record DocumentVersionVerificationResult(Guid DocumentId,
    DocumentVersionVerification? Latest, DocumentVersionVerification? Bound, bool Accessible = true);

public sealed partial class PdmWorkflowService
{
    public async Task<IReadOnlyList<DocumentVersionVerificationResult>> VerifyDocumentVersionsAsync(
        IReadOnlyList<DocumentVersionVerificationRequest>? requests, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count is < 1 or > 100
            || requests.Any(item => item is null || item.DocumentId == Guid.Empty || item.BoundVersionId == Guid.Empty)
            || requests.Select(item => item.DocumentId).Distinct().Count() != requests.Count)
            throw new PdmRuleException("每批须包含1至100个不重复图档，图档编号和原绑定版本编号必须有效。");
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken);
        await RequirePermissionAsync(actor, role, "document.view", cancellationToken);
        var allowed = new List<DocumentVersionVerificationRequest>();
        var readableProjects = await repository.GetReadableDocumentProjectsAsync(requests.Select(item => item.DocumentId).ToArray(),
            actor, role, cancellationToken);
        // A request has one actor/role and policy. Folder/file checks remain per
        // document; do not repeat the identical project lookup for every component.
        var projectAccess = new Dictionary<Guid, bool>();
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!readableProjects.TryGetValue(request.DocumentId, out var projectId)) continue;
            if (!projectAccess.TryGetValue(projectId, out var canViewProject))
            {
                try
                {
                    await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role, "document.view", cancellationToken);
                    canViewProject = true;
                }
                catch (UnauthorizedAccessException) { canViewProject = false; }
                projectAccess[projectId] = canViewProject;
            }
            if (!canViewProject) continue;
            allowed.Add(request);
        }
        var verified = allowed.Count == 0 ? []
            : await repository.ListDocumentVersionVerificationsAsync(allowed, cancellationToken);
        var byDocument = verified.ToDictionary(item => item.DocumentId);
        await AuditAsync(actor, "document.version.verify.batch", nameof(DocumentVersion), Guid.Empty.ToString(),
            string.Join(";", requests.Select(item => item.DocumentId + ":" + item.BoundVersionId)), cancellationToken);
        return requests.Select(item => byDocument.GetValueOrDefault(item.DocumentId)
            ?? new DocumentVersionVerificationResult(item.DocumentId, null, null, false)).ToArray();
    }
}
