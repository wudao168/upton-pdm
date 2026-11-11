using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Infrastructure;
public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<ReportDrawingVersion>> ListReportDrawingVersionsAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<ReportDrawingVersionRow>(new CommandDefinition("""
            SELECT d.project_id,d.id AS DocumentId,d.drawing_number AS Name,v.revision_label AS Revision,
                   p.published_at,p.change_reason_selections_json AS ReasonsJson
            FROM document d
            JOIN project project ON project.id=d.project_id
            JOIN document_version v ON v.document_id=d.id
            JOIN release_package p ON p.id=v.release_package_id
            WHERE d.project_id IN @ProjectIds AND d.kind='Drawing' AND v.version_status='Released'
              AND p.published_at IS NOT NULL
              AND (@CompanyId IS NULL OR project.organization_id=@CompanyId)
            """, new { ProjectIds = projectIds, CompanyId = TenantContext.CompanyId }, cancellationToken: ct));
        return rows.Select(r => new ReportDrawingVersion(r.ProjectId,r.DocumentId,r.Name,r.Revision,
            new DateTimeOffset(DateTime.SpecifyKind(r.PublishedAt, DateTimeKind.Utc)),
            string.IsNullOrWhiteSpace(r.ReasonsJson) ? [] : JsonSerializer.Deserialize<List<ReleaseChangeReasonSelection>>(r.ReasonsJson,new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [])).ToArray();
    }
    private sealed class ReportDrawingVersionRow
    {
        public Guid ProjectId { get; init; }
        public Guid DocumentId { get; init; }
        public string Name { get; init; } = "";
        public string Revision { get; init; } = "";
        public DateTime PublishedAt { get; init; }
        public string? ReasonsJson { get; init; }
    }
}
