using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<IReadOnlyList<DocumentVersionVerificationResult>> ListDocumentVersionVerificationsAsync(
        IReadOnlyList<DocumentVersionVerificationRequest> requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            DocumentVersionVerification? Summary(DocumentVersion? version) => version is null ? null : new(
                version.Id, version.DocumentId, version.Revision.Display, version.FileLength, version.Sha256,
                version.PropertySnapshot.GetValueOrDefault("SourceFileSha256"));
            return Task.FromResult<IReadOnlyList<DocumentVersionVerificationResult>>(requests.Select(request =>
            {
                var latest = versions.Values.Where(version => version.DocumentId == request.DocumentId)
                    .OrderByDescending(version => version.CreatedAt).FirstOrDefault();
                var bound = request.BoundVersionId.HasValue && request.BoundVersionId != latest?.Id
                    && versions.TryGetValue(request.BoundVersionId.Value, out var candidate)
                    && candidate.DocumentId == request.DocumentId ? candidate : null;
                return new DocumentVersionVerificationResult(request.DocumentId, Summary(latest), Summary(bound));
            }).ToArray());
        }
    }
}
