using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyDictionary<Guid, Guid>> GetReadableDocumentProjectsAsync(IReadOnlyCollection<Guid> documentIds,
        string actor, UserRole role, CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        if (ids.Length > 100) throw new PdmRuleException("每批最多核对100份图档权限。");
        var result = new Dictionary<Guid, Guid>();
        if (ids.Length == 0) return result;
        Dictionary<Guid, BatchDocumentAccessRow> documents;
        Dictionary<Guid, Guid> roots;
        await using (var connection = await OpenAsync(cancellationToken))
        {
            documents = (await connection.QueryAsync<BatchDocumentAccessRow>(new CommandDefinition(
                "SELECT id,project_id,folder_id FROM document WHERE id IN @Ids AND deleted_at IS NULL AND purged_at IS NULL",
                new { Ids = ids }, cancellationToken: cancellationToken))).ToDictionary(row => row.Id);
            var missing = ids.Where(id => !documents.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                var pending = await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT pending_json FROM pending_archive_document WHERE document_id IN @Ids",
                    new { Ids = missing }, cancellationToken: cancellationToken));
                foreach (var json in pending)
                {
                    var document = JsonSerializer.Deserialize<PendingArchiveDocument>(json, jsonOptions)?.Document
                        ?? throw new PdmRuleException("暂存图档记录无效，未继续版本核对。");
                    if (!missing.Contains(document.Id)) throw new PdmRuleException("暂存图档身份不一致，未继续版本核对。");
                    documents[document.Id] = new() { Id = document.Id, ProjectId = document.ProjectId, FolderId = document.FolderId };
                }
            }
            var projects = documents.Values.Select(row => row.ProjectId).Distinct().ToArray();
            roots = projects.Length == 0 ? [] : (await connection.QueryAsync<BatchProjectRootRow>(new CommandDefinition(
                "SELECT id,COALESCE(root_project_id,id) AS RootId FROM project WHERE id IN @Ids",
                new { Ids = projects }, cancellationToken: cancellationToken))).ToDictionary(row => row.Id, row => row.RootId);
        }
        // Scope is one HTTP request/actor/company. Reuse the existing permission
        // resolver once per root; do not cache permissions across requests.
        var foldersByRoot = new Dictionary<Guid, IReadOnlyList<ProjectFolder>>();
        foreach (var project in documents.Values.GroupBy(row => row.ProjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(project.Key, out var rootId)
                || !await HasProjectContentReadAccessAsync(project.Key, actor, role, cancellationToken)) continue;
            if (!foldersByRoot.TryGetValue(rootId, out var folders))
            {
                folders = await ListProjectFoldersAsync(project.Key, actor, role, cancellationToken);
                foldersByRoot[rootId] = folders;
            }
            foreach (var document in project)
            {
                var folder = document.FolderId is null
                    ? folders.FirstOrDefault(item => item.TargetProjectId == document.ProjectId && item.TemplateKey == "mechanical.project")
                    : folders.FirstOrDefault(item => item.Id == document.FolderId.Value);
                if (folder is not null && (folder.EffectiveAccess & FolderAccess.View) == FolderAccess.View)
                    result[document.Id] = document.ProjectId;
            }
        }
        return result;
    }

    private sealed class BatchDocumentAccessRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid? FolderId { get; init; }
    }
    private sealed class BatchProjectRootRow
    {
        public Guid Id { get; init; }
        public Guid RootId { get; init; }
    }
}
