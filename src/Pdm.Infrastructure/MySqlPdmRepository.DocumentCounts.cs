using System.Data.Common;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private static readonly ProjectReferenceCountsCache ReferenceCounts = new();
    private static async Task<IReadOnlyDictionary<Guid, (int All, int Model, int Drawing)>> LoadReferenceDocumentCountsAsync(
        DbConnection connection, DbTransaction? transaction, ProjectRow[] projects, CancellationToken cancellationToken, string connectionString)
    {
        var familyIds = projects.Select(project => project.RootProjectId ?? project.ParentProjectId ?? project.Id).Distinct().ToArray();
        var documentRows = (await connection.QueryAsync<CountDocumentRow>(new CommandDefinition(
            """
            SELECT COALESCE(p.root_project_id,p.parent_project_id,p.id) family_id, d.id, d.project_id, d.file_name, d.kind
            FROM document d INNER JOIN project p ON p.id=d.project_id
            WHERE COALESCE(p.root_project_id,p.parent_project_id,p.id) IN @FamilyIds AND d.deleted_at IS NULL AND d.purged_at IS NULL
            """, new { FamilyIds = familyIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        var relationRows = (await connection.QueryAsync<CountRelationRow>(new CommandDefinition(
            """
            SELECT COALESCE(p.root_project_id,p.parent_project_id,p.id) family_id, r.model_document_id, r.drawing_document_id
            FROM document_model_drawing_relation r INNER JOIN project p ON p.id=r.project_id
            WHERE COALESCE(p.root_project_id,p.parent_project_id,p.id) IN @FamilyIds
            """, new { FamilyIds = familyIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        var snapshots = (await connection.QueryAsync<CountSnapshotRow>(new CommandDefinition(
            """
            SELECT current_root.project_id, snapshot.id, snapshot.sha256
            FROM project_reference_root current_root
            INNER JOIN reference_snapshot snapshot ON snapshot.id=current_root.reference_snapshot_id
            WHERE current_root.project_id IN @ProjectIds
            """, new { ProjectIds = projects.Select(project => project.Id).ToArray() }, transaction,
            cancellationToken: cancellationToken))).ToDictionary(row => row.ProjectId);
        var documents = documentRows.GroupBy(row => row.FamilyId).ToDictionary(group => group.Key,
            group => group.Select(row => new ProjectReferenceDocumentCounts.Document(row.Id, row.ProjectId, row.FileName,
                Enum.Parse<DocumentKind>(row.Kind))).ToArray());
        var relations = relationRows.GroupBy(row => row.FamilyId).ToDictionary(group => group.Key,
            group => group.Select(row => new DocumentModelDrawingRelation(row.ModelDocumentId, row.DrawingDocumentId)).ToArray());
        var fingerprints = documents.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new {
                Documents = pair.Value.OrderBy(item => item.Id),
                Relations = relations.GetValueOrDefault(pair.Key, []).OrderBy(item => item.ModelDocumentId).ThenBy(item => item.DrawingDocumentId)
            })))));
        // 等待共用任务前归还连接，防止并发请求各占一个连接再等待第二个连接。
        if (transaction is null) await connection.CloseAsync();
        var source = new MySqlConnectionStringBuilder(connectionString);
        var cacheSource = $"{source.Server}:{source.Port}/{source.Database}";
        var result = new Dictionary<Guid, (int All, int Model, int Drawing)>();
        foreach (var project in projects)
        {
            var familyId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
            snapshots.TryGetValue(project.Id, out var snapshot);
            var key = $"{cacheSource}:{TenantContext.CompanyId}:{project.Id}:{snapshot?.Id}:{snapshot?.Sha256}:{fingerprints.GetValueOrDefault(familyId, "empty")}";
            result[project.Id] = transaction is null
                ? await ReferenceCounts.GetAsync(key, token => ReadCountsAsync(project, familyId, snapshot, null, token), cancellationToken)
                : (await ReadCountsAsync(project, familyId, snapshot, connection, cancellationToken)).Counts;
        }
        return result;

        async Task<ReferenceCountResult> ReadCountsAsync(ProjectRow project, Guid familyId, CountSnapshotRow? snapshot, DbConnection? existing, CancellationToken ct)
        {
            CountSnapshotRow? value = null;
            if (snapshot is not null)
            {
                const string sql = "SELECT id,sha256,root_json FROM reference_snapshot WHERE id=@Id";
                if (existing is not null) value = await existing.QuerySingleOrDefaultAsync<CountSnapshotRow>(new CommandDefinition(sql, new { snapshot.Id }, transaction, cancellationToken: ct));
                else
                {
                    await using var read = new MySqlConnection(connectionString);
                    value = await read.QuerySingleOrDefaultAsync<CountSnapshotRow>(new CommandDefinition(sql, new { snapshot.Id }, cancellationToken: ct));
                }
            }
            var root = value is null ? null : JsonSerializer.Deserialize<DocumentReferenceNode>(value.RootJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var counts = ProjectReferenceDocumentCounts.Count(root, project.Id, documents.GetValueOrDefault(familyId, []), relations.GetValueOrDefault(familyId, []));
            return new(counts, snapshot is null || value?.Sha256 == snapshot.Sha256);
        }
    }

    private sealed class CountSnapshotRow
    {
        public Guid ProjectId { get; init; }
        public Guid Id { get; init; }
        public string Sha256 { get; init; } = "";
        public string RootJson { get; init; } = "";
    }

    private sealed class CountDocumentRow
    {
        public Guid FamilyId { get; init; }
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string FileName { get; init; } = "";
        public string Kind { get; init; } = "";
    }

    private sealed class CountRelationRow
    {
        public Guid FamilyId { get; init; }
        public Guid ModelDocumentId { get; init; }
        public Guid DrawingDocumentId { get; init; }
    }
}
