using System.Text.Json;
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
            $"SELECT id,code,name,is_active IsActive,source_system SourceSystem,last_synced_at LastSyncedAt FROM pdm_customer WHERE source_system='u9c' {(includeInactive ? string.Empty : "AND is_active=1")} ORDER BY code",
            cancellationToken: cancellationToken));
        return rows.Select(MapCustomer).ToArray();
    }

    public async Task<PdmCustomer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CustomerRow>(new CommandDefinition(
            "SELECT id,code,name,is_active IsActive,source_system SourceSystem,last_synced_at LastSyncedAt FROM pdm_customer WHERE id=@CustomerId",
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
        return new(id, code, name, isActive, "legacy");
    }

    public async Task<CrmIntegrationConfiguration> GetCrmIntegrationConfigurationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CrmIntegrationRow>(new CommandDefinition(
            "SELECT base_url BaseUrl,username,password_ciphertext PasswordCiphertext,auto_sync_enabled AutoSyncEnabled,auto_sync_interval_minutes AutoSyncIntervalMinutes,last_sync_at LastSyncAt,last_sync_count LastSyncCount,last_auto_sync_attempt_at LastAutoSyncAttemptAt,last_auto_sync_error LastAutoSyncError FROM crm_integration_setting WHERE id=1",
            cancellationToken: cancellationToken));
        return row is null
            ? new(string.Empty, string.Empty, string.Empty, false, 60, null, 0, null, null)
            : new(row.BaseUrl, row.Username, row.PasswordCiphertext, row.AutoSyncEnabled, row.AutoSyncIntervalMinutes, AsUtc(row.LastSyncAt), row.LastSyncCount, AsUtc(row.LastAutoSyncAttemptAt), row.LastAutoSyncError);
    }

    public async Task<CrmIntegrationConfiguration> SaveCrmIntegrationConfigurationAsync(CrmIntegrationConfiguration configuration, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO crm_integration_setting(id,base_url,username,password_ciphertext,auto_sync_enabled,auto_sync_interval_minutes,last_sync_at,last_sync_count,last_auto_sync_attempt_at,last_auto_sync_error,updated_at,updated_by)
            VALUES(1,@BaseUrl,@Username,@PasswordCiphertext,@AutoSyncEnabled,@AutoSyncIntervalMinutes,@LastSyncAt,@LastSyncCount,@LastAutoSyncAttemptAt,@LastAutoSyncError,@Now,@Actor)
            ON DUPLICATE KEY UPDATE base_url=VALUES(base_url),username=VALUES(username),password_ciphertext=VALUES(password_ciphertext),auto_sync_enabled=VALUES(auto_sync_enabled),auto_sync_interval_minutes=VALUES(auto_sync_interval_minutes),updated_at=VALUES(updated_at),updated_by=VALUES(updated_by)
            """,
            new
            {
                configuration.BaseUrl,
                configuration.Username,
                configuration.PasswordCiphertext,
                configuration.AutoSyncEnabled,
                configuration.AutoSyncIntervalMinutes,
                LastSyncAt = configuration.LastSyncAt?.UtcDateTime,
                configuration.LastSyncCount,
                LastAutoSyncAttemptAt = configuration.LastAutoSyncAttemptAt?.UtcDateTime,
                configuration.LastAutoSyncError,
                Now = now,
                Actor = actor
            },
            cancellationToken: cancellationToken));
        return await GetCrmIntegrationConfigurationAsync(cancellationToken);
    }

    public async Task RecordCrmAutomaticSyncAttemptAsync(DateTimeOffset attemptedAt, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE crm_integration_setting SET last_auto_sync_attempt_at=@AttemptedAt,last_auto_sync_error=@Error,updated_at=@AttemptedAt WHERE id=1",
            new { AttemptedAt = attemptedAt.UtcDateTime, Error = error },
            cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmRuleException("U9C客户同步计划不存在，请重新保存同步计划。");
    }

    public async Task<IReadOnlyList<PdmCustomer>> ApplyCrmCustomerSyncAsync(IReadOnlyList<CrmCustomerRecord> customers, DateTimeOffset syncedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_customer SET is_active=0,row_version=row_version+1,updated_at=@SyncedAt WHERE source_system='u9c' AND is_active=1",
            new { SyncedAt = syncedAt.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var customer in customers)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO pdm_customer(id,code,name,is_active,source_system,last_synced_at,row_version,created_at,updated_at)
                VALUES(@Id,@Code,@Name,1,'u9c',@SyncedAt,1,@SyncedAt,@SyncedAt)
                ON DUPLICATE KEY UPDATE name=VALUES(name),is_active=1,source_system='u9c',last_synced_at=VALUES(last_synced_at),row_version=row_version+1,updated_at=VALUES(updated_at)
                """,
                new { Id = Guid.NewGuid(), customer.Code, customer.Name, SyncedAt = syncedAt.UtcDateTime },
                transaction,
                cancellationToken: cancellationToken));
        }
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE crm_integration_setting SET last_sync_at=@SyncedAt,last_sync_count=@Count,updated_at=@SyncedAt WHERE id=1",
            new { SyncedAt = syncedAt.UtcDateTime, Count = customers.Count },
            transaction,
            cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmRuleException("U9C客户同步计划不存在，请重新保存同步计划。");
        await transaction.CommitAsync(cancellationToken);
        return await ListCustomersAsync(true, cancellationToken);
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
            "SELECT setting_key SettingKey,setting_value SettingValue FROM pdm_system_setting",
            cancellationToken: cancellationToken));
        var values = rows.ToDictionary(row => row.SettingKey, row => row.SettingValue, StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("vault_root", out var vaultRoot) || !values.TryGetValue("release_root", out var releaseRoot))
            throw new PdmRuleException("系统存储根目录尚未配置。");
        var settings = new PdmSystemSettings(vaultRoot, releaseRoot)
        {
            CheckoutHeartbeatSeconds = ReadInt(values, "checkout_heartbeat_seconds", 180),
            CheckoutLeaseMinutes = ReadInt(values, "checkout_lease_minutes", 15),
            CheckoutOfflineGraceMinutes = ReadInt(values, "checkout_offline_grace_minutes", 60),
            CheckoutReminderHours = ReadInt(values, "checkout_reminder_hours", 4),
            CheckoutStrongReminderHours = ReadInt(values, "checkout_strong_reminder_hours", 8),
            CheckoutOverdueHours = ReadInt(values, "checkout_overdue_hours", 24),
            CheckoutForceReleaseHours = ReadInt(values, "checkout_force_release_hours", 48),
            BomDrawingNumberProperty = ReadString(values, "bom_drawing_number_property", "物料编码"),
            BomNameProperty = ReadString(values, "bom_name_property", "物料名称"),
            BomDescriptionProperty = ReadString(values, "bom_description_property", "备注信息"),
            BomMaterialProperty = ReadString(values, "bom_material_property", "材质"),
            BomSpecificationProperty = ReadString(values, "bom_specification_property", "型号"),
            BomUnitProperty = ReadString(values, "bom_unit_property", "单位"),
            BomBrandProperty = ReadString(values, "bom_brand_property", "品牌"),
            BomSurfaceTreatmentProperty = ReadString(values, "bom_surface_treatment_property", "表面处理"),
            BomWeightProperty = ReadString(values, "bom_weight_property", "重量"),
            BomPropertyMappings = ReadBomPropertyMappings(values),
            ValidationRules = new(
                ReadStringList(values, "bom_standard_required_fields", BomValidationFieldCatalog.StandardDefaults),
                ReadStringList(values, "bom_nonstandard_required_fields", BomValidationFieldCatalog.NonStandardDefaults),
                ReadStringList(values, "bom_electrical_required_fields", BomValidationFieldCatalog.ElectricalDefaults)),
            ApprovalWorkflows = ReadApprovalWorkflows(values),
            MaterialCodeApproval = ReadMaterialCodeApproval(values),
            ReleaseChangeReasonTypes = ReadStringList(values, "release_change_reason_types", PdmSystemSettings.DefaultReleaseChangeReasonTypes)
        };
        return BomPropertyMappingCatalog.Apply(settings);
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in new[]
        {
            new { Key = "vault_root", Value = settings.VaultRoot },
            new { Key = "release_root", Value = settings.ReleaseRoot },
            new { Key = "checkout_heartbeat_seconds", Value = settings.CheckoutHeartbeatSeconds.ToString() },
            new { Key = "checkout_lease_minutes", Value = settings.CheckoutLeaseMinutes.ToString() },
            new { Key = "checkout_offline_grace_minutes", Value = settings.CheckoutOfflineGraceMinutes.ToString() },
            new { Key = "checkout_reminder_hours", Value = settings.CheckoutReminderHours.ToString() },
            new { Key = "checkout_strong_reminder_hours", Value = settings.CheckoutStrongReminderHours.ToString() },
            new { Key = "checkout_overdue_hours", Value = settings.CheckoutOverdueHours.ToString() },
            new { Key = "checkout_force_release_hours", Value = settings.CheckoutForceReleaseHours.ToString() },
            new { Key = "bom_drawing_number_property", Value = settings.BomDrawingNumberProperty },
            new { Key = "bom_name_property", Value = settings.BomNameProperty },
            new { Key = "bom_description_property", Value = settings.BomDescriptionProperty },
            new { Key = "bom_material_property", Value = settings.BomMaterialProperty },
            new { Key = "bom_specification_property", Value = settings.BomSpecificationProperty },
            new { Key = "bom_unit_property", Value = settings.BomUnitProperty },
            new { Key = "bom_brand_property", Value = settings.BomBrandProperty },
            new { Key = "bom_surface_treatment_property", Value = settings.BomSurfaceTreatmentProperty },
            new { Key = "bom_weight_property", Value = settings.BomWeightProperty },
            new { Key = "bom_property_mappings", Value = JsonSerializer.Serialize(BomPropertyMappingCatalog.Normalize(settings), jsonOptions) },
            new { Key = "bom_standard_required_fields", Value = JsonSerializer.Serialize(settings.ValidationRules.Standard, jsonOptions) },
            new { Key = "bom_nonstandard_required_fields", Value = JsonSerializer.Serialize(settings.ValidationRules.NonStandard, jsonOptions) },
            new { Key = "bom_electrical_required_fields", Value = JsonSerializer.Serialize(settings.ValidationRules.Electrical, jsonOptions) },
            new { Key = "release_approval_workflows", Value = JsonSerializer.Serialize(settings.ApprovalWorkflows, jsonOptions) },
            new { Key = "material_code_approval", Value = JsonSerializer.Serialize(settings.MaterialCodeApproval, jsonOptions) },
            new { Key = "release_change_reason_types", Value = JsonSerializer.Serialize(settings.ReleaseChangeReasonTypes, jsonOptions) }
        })
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

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key, string defaultValue) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

    private IReadOnlyList<BomPropertyMapping> ReadBomPropertyMappings(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("bom_property_mappings", out var json) || string.IsNullOrWhiteSpace(json))
            return Array.Empty<BomPropertyMapping>();
        try
        {
            var mappings = JsonSerializer.Deserialize<List<BomPropertyMapping>>(json, jsonOptions);
            return mappings ?? (IReadOnlyList<BomPropertyMapping>)Array.Empty<BomPropertyMapping>();
        }
        catch (JsonException)
        {
            return Array.Empty<BomPropertyMapping>();
        }
    }

    private IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, string> values, string key, IReadOnlyList<string> defaultValue)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return defaultValue;
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(value, jsonOptions);
            return parsed is { Length: > 0 } ? BomValidationFieldCatalog.Normalize(parsed) : defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    private ReleaseApprovalSettings ReadApprovalWorkflows(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("release_approval_workflows", out var value) || string.IsNullOrWhiteSpace(value))
            return ReleaseApprovalSettings.Default;
        try
        {
            return ReleaseApprovalSettings.UseOrganizationHierarchy(
                JsonSerializer.Deserialize<ReleaseApprovalSettings>(value, jsonOptions));
        }
        catch (JsonException)
        {
            return ReleaseApprovalSettings.Default;
        }
    }

    private MaterialCodeApprovalSettings ReadMaterialCodeApproval(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("material_code_approval", out var value) || string.IsNullOrWhiteSpace(value)) return MaterialCodeApprovalSettings.Default;
        try { return JsonSerializer.Deserialize<MaterialCodeApprovalSettings>(value, jsonOptions) ?? MaterialCodeApprovalSettings.Default; }
        catch (JsonException) { return MaterialCodeApprovalSettings.Default; }
    }

    public async Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AdministrationUserRow>(new CommandDefinition(
            "SELECT id,username,display_name DisplayName,password_hash PasswordHash,role,assigned_role_code RoleCode,company_id CompanyId,cross_company_view CrossCompanyView,is_active IsActive,token_version TokenVersion FROM pdm_user ORDER BY username",
            cancellationToken: cancellationToken));
        return rows.Select(row => new UserAccount(row.Id, row.Username, row.DisplayName, row.PasswordHash, Enum.Parse<UserRole>(row.Role), row.IsActive, row.TokenVersion, row.RoleCode, row.CompanyId, row.CrossCompanyView)).ToArray();
    }

    public async Task<UserCompanyScope?> GetUserCompanyScopeAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var user = await connection.QuerySingleOrDefaultAsync<UserCompanyRow>(new CommandDefinition(
            "SELECT id,company_id CompanyId,cross_company_view CrossCompanyView FROM pdm_user WHERE username=@Username LIMIT 1",
            new { Username = username }, cancellationToken: cancellationToken));
        if (user is null) return null;
        var companyIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT accessible_company_id FROM user_company_access WHERE user_id=@UserId ORDER BY accessible_company_id",
            new { UserId = user.Id }, cancellationToken: cancellationToken))).ToArray();
        return new UserCompanyScope(user.Id, user.CompanyId, user.CrossCompanyView, companyIds);
    }

    public async Task<UserCompanyScope> SetUserCompanyScopeAsync(string username, Guid companyId, bool crossCompanyView, IReadOnlyList<Guid> accessibleCompanyIds, string actor, CancellationToken cancellationToken)
    {
        var normalizedCompanyIds = accessibleCompanyIds.Where(id => id != companyId).Distinct().ToArray();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var userId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM pdm_user WHERE username=@Username FOR UPDATE", new { Username = username }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("用户不存在。");
        var validCompanyCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_organization WHERE is_active=1 AND id IN @CompanyIds",
            new { CompanyIds = new[] { companyId }.Concat(normalizedCompanyIds).Distinct().ToArray() }, transaction, cancellationToken: cancellationToken));
        if (validCompanyCount != normalizedCompanyIds.Length + 1) throw new PdmRuleException("所属公司或跨公司授权目标不存在或已停用。");
        var actorId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM pdm_user WHERE username=@Actor LIMIT 1", new { Actor = actor }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_user SET company_id=@CompanyId,cross_company_view=@CrossCompanyView,token_version=token_version+1,row_version=row_version+1 WHERE id=@UserId",
            new { UserId = userId, CompanyId = companyId, CrossCompanyView = crossCompanyView && normalizedCompanyIds.Length > 0 }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM user_company_access WHERE user_id=@UserId", new { UserId = userId }, transaction, cancellationToken: cancellationToken));
        if (crossCompanyView && normalizedCompanyIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO user_company_access(id,user_id,accessible_company_id,created_by,created_at) VALUES(@Id,@UserId,@CompanyId,@CreatedBy,@Now)",
                normalizedCompanyIds.Select(id => new { Id = Guid.NewGuid(), UserId = userId, CompanyId = id, CreatedBy = actorId, Now = timeProvider.GetUtcNow().UtcDateTime }), transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE membership FROM organization_membership membership INNER JOIN organization_unit unit ON unit.id=membership.unit_id WHERE membership.username=@Username AND unit.organization_id<>@CompanyId",
            new { Username = username, CompanyId = companyId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE manager FROM organization_unit_manager manager INNER JOIN organization_unit unit ON unit.id=manager.unit_id WHERE manager.username=@Username AND unit.organization_id<>@CompanyId",
            new { Username = username, CompanyId = companyId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await GetUserCompanyScopeAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
    }

    private static PdmCustomer MapCustomer(CustomerRow row) => new(row.Id, row.Code, row.Name, row.IsActive, row.SourceSystem, AsUtc(row.LastSyncedAt));

    private static DateTimeOffset? AsUtc(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed class SystemSettingRow
    {
        public string SettingKey { get; init; } = string.Empty;
        public string SettingValue { get; init; } = string.Empty;
    }

    private sealed class CrmIntegrationRow
    {
        public string BaseUrl { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string PasswordCiphertext { get; init; } = string.Empty;
        public bool AutoSyncEnabled { get; init; }
        public int AutoSyncIntervalMinutes { get; init; } = 60;
        public DateTime? LastSyncAt { get; init; }
        public int LastSyncCount { get; init; }
        public DateTime? LastAutoSyncAttemptAt { get; init; }
        public string? LastAutoSyncError { get; init; }
    }

    private sealed class AdministrationUserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string? RoleCode { get; init; }
        public Guid? CompanyId { get; init; }
        public bool CrossCompanyView { get; init; }
        public bool IsActive { get; init; }
        public long TokenVersion { get; init; }
    }

    private sealed class UserCompanyRow
    {
        public Guid Id { get; init; }
        public Guid? CompanyId { get; init; }
        public bool CrossCompanyView { get; init; }
    }
}
