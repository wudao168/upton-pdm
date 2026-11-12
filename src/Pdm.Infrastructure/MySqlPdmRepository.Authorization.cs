using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private async Task<AuthorizationSettings?> ReadAuthorizationSettingsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadAuthorizationSettingsAsync(connection, ct);
    }

    private async Task<AuthorizationSettings?> ReadAuthorizationSettingsAsync(MySqlConnection connection, CancellationToken ct)
    {
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT setting_value FROM pdm_system_setting WHERE setting_key='authorization_center_v1'", cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<AuthorizationSettings>(json, jsonOptions)
            ?? throw new PdmRuleException("权限配置读取失败，请联系管理员恢复权限配置备份。");
    }

    public async Task<AuthorizationSettings> GetAuthorizationSettingsAsync(CancellationToken ct)
    {
        if (TenantContext.Current?.Authorization is { } snapshot) return snapshot;
        while (true)
        {
            var stored = await ReadAuthorizationSettingsAsync(ct);
            if (stored is null)
            {
                var directory = await GetRolePermissionDirectoryAsync(ct);
                var migrated = AuthorizationMigration.EnableDingTalkSelfBindingDefaults(AuthorizationMigration.PreserveLegacyPlatformUser(
                    AuthorizationMigration.Create(directory, await GetProjectPermissionSettingsAsync(ct)), await ListUsersAsync(ct)));
                await using var connection = await OpenAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES('authorization_center_v1',@Value,UTC_TIMESTAMP(6))",
                    new { Value = JsonSerializer.Serialize(migrated, jsonOptions) }, cancellationToken: ct));
                continue;
            }
            if (stored.RolesUnified && stored.DingTalkSelfBindingDefaultsApplied && stored.AiWebDefaultsApplied) return stored;
            var upgraded = AuthorizationMigration.EnableAiWebDefaults(AuthorizationMigration.EnableDingTalkSelfBindingDefaults(stored));
            try { return await CommitAuthorizationSettingsAsync(upgraded, stored.Revision, !stored.RolesUnified, ct); }
            catch (PdmConflictException)
            {
                // Retry with the latest revision if another request changed the configuration.
            }
        }
    }

    public async Task<AuthorizationSettings> SaveAuthorizationSettingsAsync(AuthorizationSettings settings, long expectedRevision, CancellationToken ct, bool updateRoles = false)
    {
        await GetAuthorizationSettingsAsync(ct);
        return await CommitAuthorizationSettingsAsync(settings, expectedRevision, updateRoles, ct);
    }

    private async Task<AuthorizationSettings> CommitAuthorizationSettingsAsync(AuthorizationSettings settings, long expectedRevision, bool updateRoles, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var json = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='authorization_center_v1' FOR UPDATE", transaction: transaction, cancellationToken: ct));
        var previous = JsonSerializer.Deserialize<AuthorizationSettings>(json!, jsonOptions)!;
        if (previous.Revision != expectedRevision) throw new PdmConflictException("权限配置已被其他人修改，请刷新后重新核对。");
        var saved = (updateRoles ? AuthorizationPolicy.BindRoles(settings) : settings) with
        {
            Revision = expectedRevision + 1,
            RolesUnified = previous.RolesUnified || updateRoles,
            DingTalkSelfBindingDefaultsApplied = previous.DingTalkSelfBindingDefaultsApplied || settings.DingTalkSelfBindingDefaultsApplied,
            AiWebDefaultsApplied = previous.AiWebDefaultsApplied || settings.AiWebDefaultsApplied
        };
        if (updateRoles) await SyncAuthorizationRolesAsync(connection, transaction, previous, saved, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES(@Backup,@Previous,UTC_TIMESTAMP(6))",
            new { Backup = $"authorization_center_backup_{expectedRevision}", Previous = json }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_system_setting SET setting_value=@Value,updated_at=UTC_TIMESTAMP(6) WHERE setting_key='authorization_center_v1'",
            new { Value = JsonSerializer.Serialize(saved, jsonOptions) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        if (TenantContext.Current is { } tenant) TenantContext.Set(tenant with { Authorization = saved });
        return saved;
    }

    private static async Task SyncAuthorizationRolesAsync(MySqlConnection connection, MySqlTransaction transaction, AuthorizationSettings previous, AuthorizationSettings next, CancellationToken ct)
    {
        var definitions = (await connection.QueryAsync<RoleDefinitionDataRow>(new CommandDefinition(
            "SELECT role_code RoleCode, is_system IsSystem FROM role_definition FOR UPDATE", transaction: transaction, cancellationToken: ct)))
            .ToDictionary(role => role.RoleCode, StringComparer.OrdinalIgnoreCase);
        var activeCodes = next.Templates.Select(role => role.RoleCode!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var code in previous.Templates.Select(role => role.RoleCode).OfType<string>().Where(code => !activeCodes.Contains(code)))
        {
            if (!definitions.TryGetValue(code, out var role)) continue;
            var parameters = new { RoleCode = code };
            var hasMembers = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM pdm_user_role WHERE role_code=@RoleCode)
                    OR EXISTS(SELECT 1 FROM pdm_user WHERE assigned_role_code=@RoleCode)
                """, parameters, transaction, cancellationToken: ct)) != 0;
            AuthorizationPolicy.CheckRoleDeletion(previous, next, code, role.IsSystem, hasMembers);
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM role_permission WHERE role_code=@RoleCode", parameters, transaction, cancellationToken: ct));
            try
            {
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM role_definition WHERE role_code=@RoleCode", parameters, transaction, cancellationToken: ct));
            }
            catch (MySqlException error) when (error.Number == 1451)
            {
                throw new PdmConflictException("该角色仍被使用，请先撤销相关分配。");
            }
        }
        foreach (var code in activeCodes.Where(code => !definitions.ContainsKey(code)))
        {
            // The row supplies the stable key for account membership. Names and grants come from the role in the center.
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO role_definition(role_code,role_name,description,base_role,is_system,created_at,updated_at)
                VALUES(@RoleCode,@RoleCode,'',@BaseRole,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                """, new { RoleCode = code, BaseRole = nameof(UserRole.ProductionViewer) }, transaction, cancellationToken: ct));
        }
    }
}
