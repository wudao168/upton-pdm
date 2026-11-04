using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    // 普通目录读取不申请根项目写锁；需要补建时仍走原有加锁、复核及事务流程。
    private async Task<bool> ProjectFolderTreeIsCurrentAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var values = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT p.id,p.code,p.parent_project_id,p.root_project_id,p.child_sequence
            FROM project p INNER JOIN project requested ON requested.id=@ProjectId
            WHERE p.id=COALESCE(requested.root_project_id,requested.id)
               OR p.root_project_id=COALESCE(requested.root_project_id,requested.id);
            SELECT folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions
            FROM folder_template_node ORDER BY sort_order,folder_key;
            SELECT f.id,f.root_project_id,f.parent_folder_id,f.target_project_id,f.folder_key,f.template_key,
                   f.name,f.purpose,f.sort_order,f.is_system,f.inherit_permissions
            FROM project_folder f INNER JOIN project requested ON requested.id=@ProjectId
            WHERE f.root_project_id=COALESCE(requested.root_project_id,requested.id);
            SELECT EXISTS(SELECT 1 FROM document d INNER JOIN project p ON p.id=d.project_id
                INNER JOIN project requested ON requested.id=@ProjectId
                WHERE (p.id=COALESCE(requested.root_project_id,requested.id)
                    OR p.root_project_id=COALESCE(requested.root_project_id,requested.id)) AND d.folder_id IS NULL);
            """, new { ProjectId = projectId }, cancellationToken: ct));
        var projects = (await values.ReadAsync<FolderProjectRow>()).ToArray();
        var template = (await values.ReadAsync<FolderTemplateRow>()).ToArray();
        var folders = (await values.ReadAsync<ProjectFolderRow>()).ToDictionary(item => item.FolderKey, StringComparer.OrdinalIgnoreCase);
        var hasUnassignedDocuments = await values.ReadSingleAsync<bool>();
        var requested = projects.FirstOrDefault(item => item.Id == projectId);
        if (requested is null || hasUnassignedDocuments) return false;
        var root = projects.FirstOrDefault(item => item.Id == (requested.RootProjectId ?? requested.Id));
        if (root is null || !Match("root", null, root.Id, "root", root.Code, ProjectFolderPurpose.Root, 0, true, true)) return false;
        var ids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["root"] = folders["root"].Id };
        var pending = template.Where(item => item.Purpose != ProjectFolderPurpose.ProjectContainer).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(item => string.IsNullOrWhiteSpace(item.ParentKey) || ids.ContainsKey(item.ParentKey)).ToArray();
            if (ready.Length == 0) return false;
            foreach (var node in ready)
            {
                var parent = string.IsNullOrWhiteSpace(node.ParentKey) ? folders["root"].Id : ids[node.ParentKey];
                if (!Match(node.FolderKey, parent, null, node.FolderKey, node.Name, node.Purpose, node.SortOrder, node.IsSystem, node.InheritPermissions)) return false;
                ids[node.FolderKey] = folders[node.FolderKey].Id;
                pending.Remove(node);
            }
        }
        foreach (var key in new[] { "mechanical.project", "electrical.project" })
        {
            var node = template.FirstOrDefault(item => item.FolderKey == key);
            if (node is null || !ids.TryGetValue(node.ParentKey ?? "", out var parent)) return false;
            foreach (var project in projects)
                if (!Match($"{key}:{project.Id:N}", parent, project.Id, key, project.Id == root.Id ? $"{root.Code}-0" : project.Code,
                    ProjectFolderPurpose.ProjectContainer, 10 + (project.ChildSequence ?? 0), true, node.InheritPermissions)) return false;
        }
        return true;

        bool Match(string key, Guid? parent, Guid? target, string templateKey, string name, ProjectFolderPurpose purpose, int order, bool system, bool inherit) =>
            folders.TryGetValue(key, out var folder) && folder.ParentFolderId == parent && folder.TargetProjectId == target
            && folder.TemplateKey == templateKey && folder.Name == name && folder.Purpose == purpose
            && folder.SortOrder == order && folder.IsSystem == system && folder.InheritPermissions == inherit;
    }
}
