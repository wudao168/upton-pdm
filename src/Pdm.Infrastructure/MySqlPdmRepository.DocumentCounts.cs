using System.Data.Common;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private static async Task<IReadOnlyDictionary<Guid, (int All, int Model, int Drawing)>> LoadReferenceDocumentCountsAsync(
        DbConnection connection, DbTransaction? transaction, ProjectRow[] projects, CancellationToken cancellationToken)
    {
        var familyIds = projects.Select(project => project.ParentProjectId ?? project.Id).Distinct().ToArray();
        var documentRows = (await connection.QueryAsync<CountDocumentRow>(new CommandDefinition(
            """
            SELECT COALESCE(p.parent_project_id,p.id) family_id, d.id, d.project_id, d.file_name, d.kind
            FROM document d INNER JOIN project p ON p.id=d.project_id
            WHERE COALESCE(p.parent_project_id,p.id) IN @FamilyIds
            """, new { FamilyIds = familyIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        var relationRows = (await connection.QueryAsync<CountRelationRow>(new CommandDefinition(
            """
            SELECT COALESCE(p.parent_project_id,p.id) family_id, r.model_document_id, r.drawing_document_id
            FROM document_model_drawing_relation r INNER JOIN project p ON p.id=r.project_id
            WHERE COALESCE(p.parent_project_id,p.id) IN @FamilyIds
            """, new { FamilyIds = familyIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        var snapshots = (await connection.QueryAsync<WhereUsedSnapshotRow>(new CommandDefinition(
            """
            SELECT current_root.project_id, snapshot.root_json
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
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return projects.ToDictionary(project => project.Id, project =>
        {
            var familyId = project.ParentProjectId ?? project.Id;
            var root = snapshots.TryGetValue(project.Id, out var snapshot)
                ? JsonSerializer.Deserialize<DocumentReferenceNode>(snapshot.RootJson, json) : null;
            return ProjectReferenceDocumentCounts.Count(root, project.Id,
                documents.GetValueOrDefault(familyId, []), relations.GetValueOrDefault(familyId, []));
        });
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
