using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    // Immutable version evidence only; current project scope, relation rows and
    // authorization are still queried for every request. Bound memory and avoid
    // repeatedly loading large reference trees during task-pane refreshes.
    private static readonly ConcurrentDictionary<(string Database, Guid Version), PendingDrawingRelationSource> drawingRelationEvidence = new();
    private static readonly ConcurrentQueue<(string Database, Guid Version)> drawingRelationEvidenceOrder = new();

    private async Task<PendingDrawingRelationSource[]> ReadDrawingRelationEvidenceAsync(DbConnection connection,
        Guid[] versionIds, CancellationToken token)
    {
        var database = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionString)));
        var found = new Dictionary<Guid, PendingDrawingRelationSource>();
        foreach (var id in versionIds)
            if (drawingRelationEvidence.TryGetValue((database, id), out var cached)) found[id] = cached;
        var missing = versionIds.Where(id => !found.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            var rows = await connection.QueryAsync<DrawingRelationEvidenceRow>(new CommandDefinition("""
                SELECT v.id VersionId,d.id DocumentId,d.project_id ProjectId,d.file_name FileName,
                       v.reference_snapshot_json ReferenceSnapshotJson
                FROM document_version v INNER JOIN document d ON d.id=v.document_id
                WHERE v.id IN @VersionIds
                """, new { VersionIds = missing }, cancellationToken: token));
            foreach (var row in rows)
            {
                var root = JsonSerializer.Deserialize<DocumentReferenceNode>(row.ReferenceSnapshotJson, jsonOptions);
                if (root is null) continue;
                var compact = root with { Children = root.Children.Select(node => node with { Children = [] }).ToArray() };
                var source = new PendingDrawingRelationSource(row.DocumentId, row.ProjectId, row.FileName, compact);
                found[row.VersionId] = source;
                if (drawingRelationEvidence.TryAdd((database, row.VersionId), source))
                    drawingRelationEvidenceOrder.Enqueue((database, row.VersionId));
            }
            while (drawingRelationEvidence.Count > 256 && drawingRelationEvidenceOrder.TryDequeue(out var oldest))
                drawingRelationEvidence.TryRemove(oldest, out _);
        }
        return found.Values.ToArray();
    }

    private sealed class DrawingRelationEvidenceRow
    {
        public Guid VersionId { get; init; }
        public Guid DocumentId { get; init; }
        public Guid ProjectId { get; init; }
        public string FileName { get; init; } = "";
        public string ReferenceSnapshotJson { get; init; } = "{}";
    }
}
