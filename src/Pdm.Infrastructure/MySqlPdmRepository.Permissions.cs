using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<RolePermissionDirectory> GetRolePermissionDirectoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var definitions = (await connection.QueryAsync<RoleDefinitionDataRow>(new CommandDefinition(
            "SELECT role_code RoleCode,role_name Name,description,base_role BaseRole,is_system IsSystem FROM role_definition ORDER BY is_system DESC,created_at,role_name", cancellationToken: cancellationToken))).ToArray();
        var permissions = (await connection.QueryAsync<RolePermissionRow>(new CommandDefinition(
            "SELECT role_code RoleCode,permission_code PermissionCode FROM role_permission ORDER BY role_code,permission_code", cancellationToken: cancellationToken))).ToArray();
        var userCounts = (await connection.QueryAsync<RoleUserCountRow>(new CommandDefinition(
            "SELECT COALESCE(NULLIF(assigned_role_code,''),role) RoleCode,COUNT(*) UserCount FROM pdm_user GROUP BY COALESCE(NULLIF(assigned_role_code,''),role)", cancellationToken: cancellationToken)))
            .ToDictionary(item => item.RoleCode, item => checked((int)item.UserCount), StringComparer.OrdinalIgnoreCase);
        return BuildRolePermissionDirectory(definitions, permissions, userCounts);
    }

    public async Task<IReadOnlySet<string>> GetRolePermissionsAsync(UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return RolePermissionCatalog.Defaults[role];
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadPermissionsAsync(connection, role.ToString(), cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetUserPermissionsAsync(string username, UserRole fallbackRole, CancellationToken cancellationToken)
    {
        if (fallbackRole == UserRole.Administrator) return RolePermissionCatalog.Defaults[fallbackRole];
        await using var connection = await OpenAsync(cancellationToken);
        var roleCode = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT COALESCE(NULLIF(assigned_role_code,''),role) FROM pdm_user WHERE username=@Username LIMIT 1", new { Username = username }, cancellationToken: cancellationToken));
        return await ReadPermissionsAsync(connection, string.IsNullOrWhiteSpace(roleCode) ? fallbackRole.ToString() : roleCode, cancellationToken);
    }

    public async Task<bool> HasRolePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken) =>
        role == UserRole.Administrator || (await GetRolePermissionsAsync(role, cancellationToken)).Contains(permissionCode);

    public async Task<bool> HasUserPermissionAsync(string username, UserRole fallbackRole, string permissionCode, CancellationToken cancellationToken) =>
        fallbackRole == UserRole.Administrator || (await GetUserPermissionsAsync(username, fallbackRole, cancellationToken)).Contains(permissionCode);

    public async Task<RolePermissionDirectory> SetRolePermissionsAsync(string roleCode, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var definition = await FindRoleDefinitionAsync(connection, roleCode, cancellationToken) ?? throw new PdmNotFoundException("角色不存在。");
        if (definition.BaseRole == UserRole.Administrator) return await GetRolePermissionDirectoryAsync(cancellationToken);
        var normalized = RolePermissionCatalog.Normalize(definition.BaseRole, permissionCodes);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM role_permission WHERE role_code=@RoleCode", new { definition.RoleCode }, transaction, cancellationToken: cancellationToken));
            if (normalized.Count > 0)
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO role_permission(role_code,permission_code,updated_at) VALUES(@RoleCode,@PermissionCode,UTC_TIMESTAMP(6))",
                    normalized.Select(code => new { definition.RoleCode, PermissionCode = code }), transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
        return await GetRolePermissionDirectoryAsync(cancellationToken);
    }

    public async Task<RolePermissionDirectory> CreateRoleAsync(string name, string description, string sourceRoleCode, CancellationToken cancellationToken)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)) throw new PdmRuleException("角色名称不能为空。");
        await using var connection = await OpenAsync(cancellationToken);
        var source = await FindRoleDefinitionAsync(connection, sourceRoleCode, cancellationToken) ?? throw new PdmNotFoundException("复制来源角色不存在。");
        if (source.BaseRole == UserRole.Administrator) throw new PdmRuleException("系统管理员不能作为复制来源。");
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM role_definition WHERE role_name=@Name)", new { Name = normalizedName }, cancellationToken: cancellationToken)) == 1)
            throw new PdmConflictException("角色名称已经存在。");
        var roleCode = $"custom-{Guid.NewGuid():N}";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at) VALUES(@RoleCode,@Name,@Description,@BaseRole,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))",
                new { RoleCode = roleCode, Name = normalizedName, Description = description.Trim(), BaseRole = source.BaseRole.ToString() }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO role_permission(role_code,permission_code,updated_at) SELECT @RoleCode,permission_code,UTC_TIMESTAMP(6) FROM role_permission WHERE role_code=@SourceRoleCode",
                new { RoleCode = roleCode, SourceRoleCode = source.RoleCode }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
        return await GetRolePermissionDirectoryAsync(cancellationToken);
    }

    public async Task<RolePermissionDirectory> DeleteRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var definition = await FindRoleDefinitionAsync(connection, roleCode, cancellationToken) ?? throw new PdmNotFoundException("角色不存在。");
        if (definition.IsSystem) throw new PdmRuleException("系统角色不能删除。");
        var userCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM pdm_user WHERE assigned_role_code=@RoleCode", new { definition.RoleCode }, cancellationToken: cancellationToken));
        if (userCount > 0) throw new PdmConflictException($"该角色仍分配给 {userCount} 个用户，请先调整用户角色。");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM role_permission WHERE role_code=@RoleCode", new { definition.RoleCode }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM role_definition WHERE role_code=@RoleCode", new { definition.RoleCode }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
        return await GetRolePermissionDirectoryAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> ReadPermissionsAsync(System.Data.Common.DbConnection connection, string roleCode, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<string>(new CommandDefinition("SELECT permission_code FROM role_permission WHERE role_code=@RoleCode", new { RoleCode = roleCode }, cancellationToken: cancellationToken)))
        .Where(RolePermissionCatalog.IsKnown).ToHashSet(StringComparer.Ordinal);

    private static async Task<RoleDefinitionData?> FindRoleDefinitionAsync(System.Data.Common.DbConnection connection, string roleCode, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<RoleDefinitionDataRow>(new CommandDefinition(
            "SELECT role_code RoleCode,role_name Name,description,base_role BaseRole,is_system IsSystem FROM role_definition WHERE role_code=@RoleCode LIMIT 1",
            new { RoleCode = roleCode.Trim() }, cancellationToken: cancellationToken));
        return row is null || !Enum.TryParse<UserRole>(row.BaseRole, true, out var baseRole) ? null : new(row.RoleCode, row.Name, row.Description, baseRole, row.IsSystem);
    }

    private static RolePermissionDirectory BuildRolePermissionDirectory(IReadOnlyList<RoleDefinitionDataRow> definitions, IReadOnlyList<RolePermissionRow> rows, IReadOnlyDictionary<string, int> userCounts)
    {
        var byRole = rows.Where(row => RolePermissionCatalog.IsKnown(row.PermissionCode)).GroupBy(row => row.RoleCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.PermissionCode).Distinct().Order().ToArray(), StringComparer.OrdinalIgnoreCase);
        return new(RolePermissionCatalog.Permissions, definitions.Select(definition =>
        {
            var baseRole = Enum.Parse<UserRole>(definition.BaseRole, true);
            return new RolePermissionSettings(definition.RoleCode, definition.Name, definition.Description, baseRole, definition.IsSystem,
                baseRole == UserRole.Administrator,
                baseRole == UserRole.Administrator ? RolePermissionCatalog.Defaults[UserRole.Administrator].Order().ToArray() : byRole.GetValueOrDefault(definition.RoleCode, []),
                userCounts.GetValueOrDefault(definition.RoleCode));
        }).ToArray());
    }

    private sealed record RoleDefinitionData(string RoleCode, string Name, string Description, UserRole BaseRole, bool IsSystem);
    private sealed class RoleDefinitionDataRow { public string RoleCode { get; init; } = string.Empty; public string Name { get; init; } = string.Empty; public string Description { get; init; } = string.Empty; public string BaseRole { get; init; } = string.Empty; public bool IsSystem { get; init; } }
    private sealed record RolePermissionRow(string RoleCode, string PermissionCode);
    private sealed record RoleUserCountRow(string RoleCode, long UserCount);
}
