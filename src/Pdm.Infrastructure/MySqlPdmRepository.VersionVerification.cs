using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<DocumentVersionVerificationResult>> ListDocumentVersionVerificationsAsync(
        IReadOnlyList<DocumentVersionVerificationRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        // The existing (document_id, created_at) index resolves each latest version.
        // Neither full property JSON nor structure/BOM snapshot columns leave the database.
        const string columns = "v.id,v.document_id,v.revision_label AS Revision,v.file_length,v.sha256,"
            + "NULLIF(JSON_UNQUOTE(JSON_EXTRACT(v.property_snapshot_json,'$.SourceFileSha256')),'null') AS SourceFileSha256";
        var boundIds = requests.Where(item => item.BoundVersionId.HasValue).Select(item => item.BoundVersionId!.Value).Distinct().ToArray();
        var sql = "SELECT " + columns + ",1 AS IsLatest FROM document d JOIN document_version v ON v.id="
            + "(SELECT newest.id FROM document_version newest WHERE newest.document_id=d.id ORDER BY newest.created_at DESC LIMIT 1)"
            + " WHERE d.id IN @DocumentIds";
        if (boundIds.Length > 0)
            sql += " UNION ALL SELECT " + columns + ",0 AS IsLatest FROM document_version v WHERE v.document_id IN @DocumentIds AND v.id IN @BoundIds";
        var rows = (await connection.QueryAsync<VersionVerificationRow>(new CommandDefinition(sql,
            new { DocumentIds = requests.Select(item => item.DocumentId).ToArray(), BoundIds = boundIds },
            cancellationToken: cancellationToken))).ToArray();
        var latest = rows.Where(row => row.IsLatest).ToDictionary(row => row.DocumentId);
        var bound = rows.Where(row => !row.IsLatest).ToDictionary(row => row.Id);
        return requests.Select(request =>
        {
            latest.TryGetValue(request.DocumentId, out var current);
            var historical = request.BoundVersionId.HasValue && request.BoundVersionId != current?.Id
                && bound.TryGetValue(request.BoundVersionId.Value, out var candidate)
                && candidate.DocumentId == request.DocumentId ? candidate : null;
            return new DocumentVersionVerificationResult(request.DocumentId, current?.Summary(), historical?.Summary());
        }).ToArray();
    }

    private sealed class VersionVerificationRow
    {
        public Guid Id { get; init; }
        public Guid DocumentId { get; init; }
        public string Revision { get; init; } = string.Empty;
        public long FileLength { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string? SourceFileSha256 { get; init; }
        public bool IsLatest { get; init; }
        public DocumentVersionVerification Summary() => new(Id, DocumentId, Revision, FileLength, Sha256, SourceFileSha256);
    }
}
