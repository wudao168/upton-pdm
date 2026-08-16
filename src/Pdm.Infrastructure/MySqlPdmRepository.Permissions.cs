using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<RolePermissionDirectory> GetRolePermissionDirectoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<RolePermissionRow>(new CommandDefinition(
            "SELECT role_code,permission_code FROM role_permission ORDER BY role_code,permission_code",
            cancellationToken: cancellationToken))).ToArray();
        return BuildRolePermissionDirectory(rows);
    }

    public async Task<IReadOnlySet<string>> GetRolePermissionsAsync(UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return RolePermissionCatalog.Defaults[role];
        await using var connection = await OpenAsync(cancellationToken);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT permission_code FROM role_permission WHERE role_code=@RoleCode",
            new { RoleCode = role.ToString() }, cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> HasRolePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM role_permission WHERE role_code=@RoleCode AND permission_code=@PermissionCode)",
            new { RoleCode = role.ToString(), PermissionCode = permissionCode }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<RolePermissionDirectory> SetRolePermissionsAsync(UserRole role, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken)
    {
        var normalized = RolePermissionCatalog.Normalize(role, permissionCodes);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM role_permission WHERE role_code=@RoleCode",
                new { RoleCode = role.ToString() }, transaction, cancellationToken: cancellationToken));
            if (normalized.Count > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO role_permission(role_code,permission_code,updated_at) VALUES(@RoleCode,@PermissionCode,UTC_TIMESTAMP(6))",
                    normalized.Select(code => new { RoleCode = role.ToString(), PermissionCode = code }), transaction, cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return await GetRolePermissionDirectoryAsync(cancellationToken);
    }

    private static RolePermissionDirectory BuildRolePermissionDirectory(IReadOnlyList<RolePermissionRow> rows)
    {
        var byRole = rows
            .Where(row => Enum.TryParse<UserRole>(row.RoleCode, out _) && RolePermissionCatalog.IsKnown(row.PermissionCode))
            .GroupBy(row => Enum.Parse<UserRole>(row.RoleCode))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.PermissionCode).Distinct().Order().ToArray());
        return new(
            RolePermissionCatalog.Permissions,
            RolePermissionCatalog.Roles.Select(definition => new RolePermissionSettings(
                definition.Role,
                definition.Name,
                definition.Description,
                definition.IsSystemAdministrator,
                definition.Role == UserRole.Administrator
                    ? RolePermissionCatalog.Defaults[definition.Role].Order().ToArray()
                    : byRole.GetValueOrDefault(definition.Role, []))).ToArray());
    }

    private sealed record RolePermissionRow(string RoleCode, string PermissionCode);
}
