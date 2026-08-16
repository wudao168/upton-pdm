using System.Data.Common;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task EnsureProjectFolderTreeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var project = await connection.QuerySingleOrDefaultAsync<FolderProjectRow>(new CommandDefinition(
            "SELECT id,code,parent_project_id,child_sequence FROM project WHERE id=@ProjectId",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.ParentProjectId ?? project.Id;
        var root = project.ParentProjectId is null
            ? project
            : await connection.QuerySingleAsync<FolderProjectRow>(new CommandDefinition(
                "SELECT id,code,parent_project_id,child_sequence FROM project WHERE id=@ProjectId",
                new { ProjectId = rootId }, transaction, cancellationToken: cancellationToken));
        var projects = (await connection.QueryAsync<FolderProjectRow>(new CommandDefinition(
            "SELECT id,code,parent_project_id,child_sequence FROM project WHERE id=@RootId OR parent_project_id=@RootId ORDER BY parent_project_id,child_sequence",
            new { RootId = rootId }, transaction, cancellationToken: cancellationToken))).ToArray();
        var template = (await connection.QueryAsync<FolderTemplateRow>(new CommandDefinition(
            "SELECT folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions FROM folder_template_node ORDER BY sort_order,folder_key",
            transaction: transaction, cancellationToken: cancellationToken))).ToArray();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var actualIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var rootFolderId = await UpsertFolderAsync(connection, transaction, rootId, null, root.Id, "root", "root", root.Code,
            ProjectFolderPurpose.Root, 0, true, true, now, cancellationToken);
        actualIds["root"] = rootFolderId;

        foreach (var node in template.Where(item => item.Purpose != ProjectFolderPurpose.ProjectContainer))
        {
            var parentId = string.IsNullOrWhiteSpace(node.ParentKey) ? rootFolderId : actualIds[node.ParentKey];
            actualIds[node.FolderKey] = await UpsertFolderAsync(connection, transaction, rootId, parentId, null,
                node.FolderKey, node.FolderKey, node.Name, node.Purpose, node.SortOrder, node.IsSystem,
                node.InheritPermissions, now, cancellationToken);
        }

        foreach (var templateKey in new[] { "mechanical.project", "electrical.project" })
        {
            var node = template.Single(item => string.Equals(item.FolderKey, templateKey, StringComparison.OrdinalIgnoreCase));
            var parentId = actualIds[node.ParentKey!];
            foreach (var target in projects)
            {
                var isRoot = target.Id == root.Id;
                var folderName = isRoot ? $"{root.Code}-0" : target.Code;
                var folderKey = $"{templateKey}:{target.Id:N}";
                await UpsertFolderAsync(connection, transaction, rootId, parentId, target.Id, folderKey, templateKey,
                    folderName, ProjectFolderPurpose.ProjectContainer, 10 + (target.ChildSequence ?? 0), true,
                    node.InheritPermissions, now, cancellationToken);
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document d
            INNER JOIN project p ON p.id=d.project_id
            INNER JOIN project_folder f ON f.root_project_id=COALESCE(p.parent_project_id,p.id)
                AND f.target_project_id=d.project_id AND f.template_key='mechanical.project'
            SET d.folder_id=f.id
            WHERE d.folder_id IS NULL
            """,
            transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectFolder>> ListProjectFoldersAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await EnsureProjectFolderTreeAsync(projectId, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var rootId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT COALESCE(parent_project_id,id) FROM project WHERE id=@ProjectId",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        var rows = (await connection.QueryAsync<ProjectFolderRow>(new CommandDefinition(
            "SELECT id,root_project_id,parent_folder_id,target_project_id,folder_key,template_key,name,purpose,sort_order,is_system,inherit_permissions FROM project_folder WHERE root_project_id=@RootId ORDER BY sort_order,name",
            new { RootId = rootId }, cancellationToken: cancellationToken))).ToArray();
        var folderIds = rows.Select(item => item.Id).ToArray();
        var permissionRows = folderIds.Length == 0
            ? []
            : (await connection.QueryAsync<FolderPermissionRow>(new CommandDefinition(
                "SELECT id,folder_id,principal_type,principal_key,access_mask FROM project_folder_permission WHERE folder_id IN @FolderIds",
                new { FolderIds = folderIds }, cancellationToken: cancellationToken))).ToArray();
        var templatePermissionRows = (await connection.QueryAsync<TemplatePermissionRow>(new CommandDefinition(
            "SELECT id,folder_key,principal_type,principal_key,access_mask FROM folder_template_permission",
            cancellationToken: cancellationToken))).ToArray();
        var byId = rows.ToDictionary(item => item.Id);
        var result = new List<ProjectFolder>(rows.Length);
        foreach (var row in rows)
        {
            var explicitRules = permissionRows.Where(item => item.FolderId == row.Id).Select(MapRule).ToArray();
            var canSeeTarget = row.TargetProjectId is null || await HasProjectReadAccessAsync(row.TargetProjectId.Value, actor, role, cancellationToken);
            var access = canSeeTarget
                ? ResolveAccess(row, byId, permissionRows, templatePermissionRows, actor, role)
                : FolderAccess.None;
            result.Add(MapFolder(row) with { EffectiveAccess = access, Permissions = explicitRules });
        }
        return result;
    }

    public async Task<IReadOnlyList<ProjectFolderTemplateNode>> ListFolderTemplateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<FolderTemplateRow>(new CommandDefinition(
            "SELECT folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions FROM folder_template_node ORDER BY COALESCE(parent_key,''),sort_order,folder_key",
            cancellationToken: cancellationToken))).ToArray();
        var permissions = (await connection.QueryAsync<TemplatePermissionRow>(new CommandDefinition(
            "SELECT id,folder_key,principal_type,principal_key,access_mask FROM folder_template_permission",
            cancellationToken: cancellationToken))).ToArray();
        return rows.Select(row => new ProjectFolderTemplateNode(row.FolderKey, row.ParentKey, row.Name, row.Purpose, row.SortOrder, row.IsSystem, row.InheritPermissions)
        {
            Permissions = permissions.Where(item => item.FolderKey == row.FolderKey).Select(item => MapRule(item.Id, item.PrincipalType, item.PrincipalKey, item.AccessMask)).ToArray()
        }).ToArray();
    }

    public async Task<IReadOnlyList<ProjectFolderTemplateNode>> SaveFolderTemplateAsync(IReadOnlyList<SaveFolderTemplateNodeCommand> nodes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existingKeys = (await connection.QueryAsync<string>(new CommandDefinition("SELECT folder_key FROM folder_template_node", transaction: transaction, cancellationToken: cancellationToken))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nodes.Count != existingKeys.Count || nodes.Any(item => !existingKeys.Contains(item.FolderKey)))
            throw new PdmRuleException("目录模板节点必须完整，不能新增或删除系统目录。");
        foreach (var node in nodes)
        {
            var name = node.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 160) throw new PdmRuleException("目录名称不能为空且不能超过160个字符。");
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE folder_template_node SET name=@Name,sort_order=@SortOrder,inherit_permissions=@Inherit WHERE folder_key=@FolderKey",
                new { node.FolderKey, Name = name, node.SortOrder, Inherit = node.InheritPermissions }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM folder_template_permission WHERE folder_key=@FolderKey", new { node.FolderKey }, transaction, cancellationToken: cancellationToken));
            await InsertTemplatePermissionsAsync(connection, transaction, node.FolderKey, node.Permissions, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ListFolderTemplateAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectFolder>> SetProjectFolderPermissionsAsync(Guid projectId, Guid folderId, IReadOnlyList<SaveFolderPermissionCommand> permissions, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await EnsureProjectFolderTreeAsync(projectId, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var belongs = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM project_folder f INNER JOIN project p ON p.id=@ProjectId WHERE f.id=@FolderId AND f.root_project_id=COALESCE(p.parent_project_id,p.id)",
            new { ProjectId = projectId, FolderId = folderId }, transaction, cancellationToken: cancellationToken));
        if (belongs != 1) throw new PdmNotFoundException("项目目录不存在。");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_folder_permission WHERE folder_id=@FolderId", new { FolderId = folderId }, transaction, cancellationToken: cancellationToken));
        await InsertProjectPermissionsAsync(connection, transaction, folderId, permissions, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
    }

    private static async Task<Guid> UpsertFolderAsync(DbConnection connection, DbTransaction transaction, Guid rootProjectId, Guid? parentFolderId,
        Guid? targetProjectId, string folderKey, string templateKey, string name, ProjectFolderPurpose purpose, int sortOrder,
        bool isSystem, bool inheritPermissions, DateTime now, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_folder(id,root_project_id,parent_folder_id,target_project_id,folder_key,template_key,name,purpose,sort_order,is_system,inherit_permissions,created_at,updated_at)
            VALUES(@Id,@RootProjectId,@ParentFolderId,@TargetProjectId,@FolderKey,@TemplateKey,@Name,@Purpose,@SortOrder,@IsSystem,@InheritPermissions,@Now,@Now)
            ON DUPLICATE KEY UPDATE parent_folder_id=VALUES(parent_folder_id),target_project_id=VALUES(target_project_id),template_key=VALUES(template_key),name=VALUES(name),purpose=VALUES(purpose),sort_order=VALUES(sort_order),is_system=VALUES(is_system),inherit_permissions=VALUES(inherit_permissions),updated_at=VALUES(updated_at)
            """,
            new { Id = id, RootProjectId = rootProjectId, ParentFolderId = parentFolderId, TargetProjectId = targetProjectId, FolderKey = folderKey,
                TemplateKey = templateKey, Name = name, Purpose = purpose.ToString(), SortOrder = sortOrder, IsSystem = isSystem,
                InheritPermissions = inheritPermissions, Now = now }, transaction, cancellationToken: cancellationToken));
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM project_folder WHERE root_project_id=@RootProjectId AND folder_key=@FolderKey",
            new { RootProjectId = rootProjectId, FolderKey = folderKey }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<Guid> ResolveDocumentFolderAsync(DbConnection connection, DbTransaction transaction, Guid projectId, Guid? requestedFolderId, CancellationToken cancellationToken)
    {
        var folderId = requestedFolderId is null
            ? await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM project_folder WHERE target_project_id=@ProjectId AND template_key='mechanical.project' LIMIT 1",
                new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken))
            : await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM project_folder WHERE id=@FolderId AND target_project_id=@ProjectId AND purpose='ProjectContainer'",
                new { FolderId = requestedFolderId.Value, ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        return folderId ?? throw new PdmRuleException("图档只能登记到机械图纸或电气图纸下当前项目对应的目录。");
    }

    private static FolderAccess ResolveAccess(ProjectFolderRow row, IReadOnlyDictionary<Guid, ProjectFolderRow> byId,
        IReadOnlyList<FolderPermissionRow> explicitRules, IReadOnlyList<TemplatePermissionRow> templateRules,
        string actor, UserRole role)
    {
        if (role == UserRole.Administrator) return FolderAccess.All;
        ProjectFolderRow? current = row;
        while (current is not null)
        {
            var matches = explicitRules.Where(item => item.FolderId == current.Id && Matches(item.PrincipalType, item.PrincipalKey, actor, role)).ToArray();
            if (matches.Length == 0)
                matches = templateRules.Where(item => item.FolderKey == current.TemplateKey && Matches(item.PrincipalType, item.PrincipalKey, actor, role))
                    .Select(item => new FolderPermissionRow { Id = item.Id, FolderId = current.Id, PrincipalType = item.PrincipalType, PrincipalKey = item.PrincipalKey, AccessMask = item.AccessMask }).ToArray();
            if (matches.Length > 0) return matches.Aggregate(FolderAccess.None, (value, item) => value | (FolderAccess)item.AccessMask);
            if (!current.InheritPermissions || current.ParentFolderId is null) break;
            current = byId.GetValueOrDefault(current.ParentFolderId.Value);
        }
        if (row.Purpose == ProjectFolderPurpose.Release) return FolderAccess.View | FolderAccess.Download;
        return role == UserRole.Engineer
            ? FolderAccess.View | FolderAccess.Download | FolderAccess.Upload | FolderAccess.Edit
            : FolderAccess.View | FolderAccess.Download;
    }

    private static bool Matches(string type, string key, string actor, UserRole role) =>
        (type == FolderPrincipalType.User.ToString() && string.Equals(key, actor, StringComparison.OrdinalIgnoreCase))
        || (type == FolderPrincipalType.Role.ToString() && string.Equals(key, role.ToString(), StringComparison.OrdinalIgnoreCase));

    private static async Task InsertTemplatePermissionsAsync(DbConnection connection, DbTransaction transaction, string folderKey, IEnumerable<SaveFolderPermissionCommand> permissions, CancellationToken cancellationToken)
    {
        foreach (var permission in NormalizePermissions(permissions))
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO folder_template_permission(id,folder_key,principal_type,principal_key,access_mask) VALUES(@Id,@FolderKey,@PrincipalType,@PrincipalKey,@AccessMask)",
                new { Id = Guid.NewGuid(), FolderKey = folderKey, PrincipalType = permission.PrincipalType.ToString(), permission.PrincipalKey, AccessMask = (int)permission.Access }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task InsertProjectPermissionsAsync(DbConnection connection, DbTransaction transaction, Guid folderId, IEnumerable<SaveFolderPermissionCommand> permissions, CancellationToken cancellationToken)
    {
        foreach (var permission in NormalizePermissions(permissions))
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_folder_permission(id,folder_id,principal_type,principal_key,access_mask) VALUES(@Id,@FolderId,@PrincipalType,@PrincipalKey,@AccessMask)",
                new { Id = Guid.NewGuid(), FolderId = folderId, PrincipalType = permission.PrincipalType.ToString(), permission.PrincipalKey, AccessMask = (int)permission.Access }, transaction, cancellationToken: cancellationToken));
    }

    private static IReadOnlyList<SaveFolderPermissionCommand> NormalizePermissions(IEnumerable<SaveFolderPermissionCommand> permissions)
    {
        var result = permissions.Select(item => item with { PrincipalKey = item.PrincipalKey.Trim() }).ToArray();
        if (result.Any(item => string.IsNullOrWhiteSpace(item.PrincipalKey))) throw new PdmRuleException("权限主体不能为空。");
        if (result.GroupBy(item => (item.PrincipalType, item.PrincipalKey), new PrincipalComparer()).Any(group => group.Count() > 1))
            throw new PdmRuleException("同一目录不能重复配置相同权限主体。");
        return result;
    }

    private sealed class PrincipalComparer : IEqualityComparer<(FolderPrincipalType Type, string Key)>
    {
        public bool Equals((FolderPrincipalType Type, string Key) x, (FolderPrincipalType Type, string Key) y) => x.Type == y.Type && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((FolderPrincipalType Type, string Key) obj) => HashCode.Combine(obj.Type, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key));
    }

    private static FolderPermissionRule MapRule(FolderPermissionRow row) => MapRule(row.Id, row.PrincipalType, row.PrincipalKey, row.AccessMask);
    private static FolderPermissionRule MapRule(Guid id, string type, string key, int mask) => new(id, Enum.Parse<FolderPrincipalType>(type), key, (FolderAccess)mask);
    private static ProjectFolder MapFolder(ProjectFolderRow row) => new(row.Id, row.RootProjectId, row.ParentFolderId, row.TargetProjectId,
        row.FolderKey, row.TemplateKey, row.Name, row.Purpose, row.SortOrder, row.IsSystem, row.InheritPermissions);

    private sealed class FolderProjectRow { public Guid Id { get; init; } public string Code { get; init; } = string.Empty; public Guid? ParentProjectId { get; init; } public int? ChildSequence { get; init; } }
    private sealed class FolderTemplateRow { public string FolderKey { get; init; } = string.Empty; public string? ParentKey { get; init; } public string Name { get; init; } = string.Empty; public ProjectFolderPurpose Purpose { get; init; } public int SortOrder { get; init; } public bool IsSystem { get; init; } public bool InheritPermissions { get; init; } }
    private sealed class ProjectFolderRow { public Guid Id { get; init; } public Guid RootProjectId { get; init; } public Guid? ParentFolderId { get; init; } public Guid? TargetProjectId { get; init; } public string FolderKey { get; init; } = string.Empty; public string TemplateKey { get; init; } = string.Empty; public string Name { get; init; } = string.Empty; public ProjectFolderPurpose Purpose { get; init; } public int SortOrder { get; init; } public bool IsSystem { get; init; } public bool InheritPermissions { get; init; } }
    private sealed class FolderPermissionRow { public Guid Id { get; init; } public Guid FolderId { get; init; } public string PrincipalType { get; init; } = string.Empty; public string PrincipalKey { get; init; } = string.Empty; public int AccessMask { get; init; } }
    private sealed class TemplatePermissionRow { public Guid Id { get; init; } public string FolderKey { get; init; } = string.Empty; public string PrincipalType { get; init; } = string.Empty; public string PrincipalKey { get; init; } = string.Empty; public int AccessMask { get; init; } }
}
