using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<PdmCustomer>> ListCustomersAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CustomerRow>(new CommandDefinition(
            $"SELECT id,code,name,is_active IsActive FROM pdm_customer {(includeInactive ? string.Empty : "WHERE is_active=1")} ORDER BY code",
            cancellationToken: cancellationToken));
        return rows.Select(MapCustomer).ToArray();
    }

    public async Task<PdmCustomer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CustomerRow>(new CommandDefinition(
            "SELECT id,code,name,is_active IsActive FROM pdm_customer WHERE id=@CustomerId",
            new { CustomerId = customerId }, cancellationToken: cancellationToken));
        return row is null ? null : MapCustomer(row);
    }

    public async Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, CancellationToken cancellationToken)
    {
        var id = customerId ?? Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            if (customerId is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO pdm_customer(id,code,name,is_active,row_version,created_at,updated_at) VALUES(@Id,@Code,@Name,@IsActive,1,@Now,@Now)",
                    new { Id = id, Code = code, Name = name, IsActive = isActive, Now = now }, cancellationToken: cancellationToken));
            }
            else
            {
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE pdm_customer SET code=@Code,name=@Name,is_active=@IsActive,row_version=row_version+1,updated_at=@Now WHERE id=@Id",
                    new { Id = id, Code = code, Name = name, IsActive = isActive, Now = now }, cancellationToken: cancellationToken));
                if (affected == 0) throw new PdmNotFoundException("客户不存在。");
            }
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("客户编码已经存在。");
        }
        return new(id, code, name, isActive);
    }

    public async Task<IReadOnlyList<EquipmentTypeDefinition>> ListEquipmentTypesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<EquipmentTypeDefinitionRow>(new CommandDefinition(
            $"SELECT code,name,is_active IsActive FROM equipment_type_definition {(includeInactive ? string.Empty : "WHERE is_active=1")} ORDER BY code",
            cancellationToken: cancellationToken));
        return rows.Select(row => new EquipmentTypeDefinition(row.Code, row.Name, row.IsActive)).ToArray();
    }

    public async Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO equipment_type_definition(code,name,is_active) VALUES(@Code,@Name,@IsActive)
            ON DUPLICATE KEY UPDATE name=VALUES(name),is_active=VALUES(is_active)
            """,
            new { Code = code, Name = name, IsActive = isActive }, cancellationToken: cancellationToken));
        return new(code, name, isActive);
    }

    public async Task<PdmSystemSettings> GetSystemSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<SystemSettingRow>(new CommandDefinition(
            "SELECT setting_key SettingKey,setting_value SettingValue FROM pdm_system_setting WHERE setting_key IN ('vault_root','release_root')",
            cancellationToken: cancellationToken));
        var values = rows.ToDictionary(row => row.SettingKey, row => row.SettingValue, StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("vault_root", out var vaultRoot) || !values.TryGetValue("release_root", out var releaseRoot))
            throw new PdmRuleException("系统存储根目录尚未配置。");
        return new(vaultRoot, releaseRoot);
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in new[] { new { Key = "vault_root", Value = settings.VaultRoot }, new { Key = "release_root", Value = settings.ReleaseRoot } })
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES(@Key,@Value,@Now)
                ON DUPLICATE KEY UPDATE setting_value=VALUES(setting_value),updated_at=VALUES(updated_at)
                """,
                new { item.Key, item.Value, Now = now }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return settings;
    }

    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AdministrationUserRow>(new CommandDefinition(
            "SELECT id,username,display_name DisplayName,password_hash PasswordHash,role,is_active IsActive FROM pdm_user ORDER BY username",
            cancellationToken: cancellationToken));
        return rows.Select(row => new UserAccount(row.Id, row.Username, row.DisplayName, row.PasswordHash, Enum.Parse<UserRole>(row.Role), row.IsActive)).ToArray();
    }

    public async Task<Project> SetProjectResponsibleUsersAsync(Guid projectId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        var normalized = usernames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var projectExists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project WHERE id=@ProjectId FOR UPDATE", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        if (projectExists == 0) throw new PdmNotFoundException("项目不存在。");
        var activeUsernames = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT username FROM pdm_user WHERE is_active=1 AND username IN @Usernames",
            new { Usernames = normalized }, transaction, cancellationToken: cancellationToken))).ToArray();
        if (activeUsernames.Length != normalized.Length) throw new PdmRuleException("负责人列表中包含不存在或已停用的账号。");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_responsible WHERE project_id=@ProjectId", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_responsible(project_id,username,assigned_at) VALUES(@ProjectId,@Username,@Now)",
            normalized.Select(username => new { ProjectId = projectId, Username = username, Now = now }), transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project SET owner=@Owner,row_version=row_version+1,updated_at=@Now WHERE id=@ProjectId",
            new { ProjectId = projectId, Owner = normalized[0], Now = now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
    }

    private static PdmCustomer MapCustomer(CustomerRow row) => new(row.Id, row.Code, row.Name, row.IsActive);

    private sealed class SystemSettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string SettingValue { get; init; } = string.Empty;
    }

    private sealed class AdministrationUserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }
}
