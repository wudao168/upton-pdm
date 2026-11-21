using System.Data;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyDictionary<string, string>> GetUserDeletionReferencesAsync(IReadOnlyList<UserAccount> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return new Dictionary<string, string>();
        using var lookupBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookupBudget.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await using var connection = await OpenAsync(lookupBudget.Token);
            return await ReadUserDeletionReferencesAsync(connection, null, candidates.Select(user => user.Username).ToArray(), lookupBudget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lookupBudget.IsCancellationRequested)
        {
            return candidates.ToDictionary(user => user.Username, _ => "暂时无法核对账号引用，请稍后刷新重试。", StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<UserAccount> DeactivateManagedUserAsync(string username, string actor, Guid? companyId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var target = await RequireManagedUserAsync(connection, transaction, username, actor, companyId, false, ct);
            if (!target.IsActive) return target;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE pdm_user SET is_active=0,token_version=token_version+1,row_version=row_version+1 WHERE id=@Id",
                new { target.Id }, transaction, cancellationToken: ct));
            await WriteUserAdministrationAuditAsync(connection, transaction, target, actor, "user.deactivate", "停用账号", ct);
            await transaction.CommitAsync(ct);
            return target with { IsActive = false, TokenVersion = target.TokenVersion + 1 };
        }
        catch (MySqlException error) when (error.Number is 1213 or 1205)
        {
            throw new PdmConflictException("账号或权限配置已变化，请刷新后重试停用。");
        }
    }

    public async Task DeleteUnreferencedUserAsync(string username, string actor, Guid? companyId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var target = await RequireManagedUserAsync(connection, transaction, username, actor, companyId, true, ct);
            var references = await ReadUserDeletionReferencesAsync(connection, transaction, [target.Username], ct);
            if (references.TryGetValue(target.Username, out var reason)) throw new PdmRuleException(reason);

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM pdm_user WHERE id=@Id",
                new { target.Id }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM user_table_preference WHERE username=@Username",
                new { target.Username }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM login_security_state WHERE state_key=CONCAT('user:',LOWER(@Username))",
                new { target.Username }, transaction, cancellationToken: ct));
            await WriteUserAdministrationAuditAsync(connection, transaction, target, actor, "user.delete", "删除未被引用的账号", ct);
            await transaction.CommitAsync(ct);
        }
        catch (MySqlException error) when (error.Number is 1451 or 1213 or 1205)
        {
            throw new PdmConflictException("账号或相关业务记录已变化，请刷新后重新核对能否删除。");
        }
    }

    private async Task<UserAccount> RequireManagedUserAsync(MySqlConnection connection, MySqlTransaction transaction,
        string username, string actor, Guid? companyId, bool delete, CancellationToken ct)
    {
        var (settings, accounts) = await ReadUserAdministrationStateAsync(connection, transaction, ct);
        return UserAccountAdministration.RequireOperation(settings, accounts, actor, username, companyId, delete);
    }

    private async Task<(AuthorizationSettings Settings, IReadOnlyList<UserAccount> Accounts)> ReadUserAdministrationStateAsync(
        MySqlConnection connection, MySqlTransaction transaction, CancellationToken ct)
    {
        // 先读取权限中心配置，随后锁定账号，避免并发操作移除最后一位权限管理人员。
        var settings = await ReadAuthorizationSettingsAsync(connection, ct, transaction)
            ?? throw new PdmConflictException("权限配置尚未就绪，请刷新后重试。");
        var accounts = await ListUsersAsync(connection, ct, transaction);
        return (settings, accounts);
    }

    private Task WriteUserAdministrationAuditAsync(MySqlConnection connection, MySqlTransaction transaction, UserAccount target,
        string actor, string action, string description, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
            VALUES(@Id,@OccurredAt,@Actor,@Action,@EntityType,@EntityId,@Detail)
            """,
            new
            {
                Id = Guid.NewGuid(),
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                Actor = actor,
                Action = action,
                EntityType = nameof(UserAccount),
                EntityId = target.Username,
                Detail = System.Text.Json.JsonSerializer.Serialize(new { detail = $"{description} · {target.DisplayName}" }, jsonOptions)
            },
            transaction,
            cancellationToken: ct));

    private static async Task<IReadOnlyDictionary<string, string>> ReadUserDeletionReferencesAsync(MySqlConnection connection,
        MySqlTransaction? transaction, IReadOnlyList<string> usernames, CancellationToken ct)
    {
        // 存档回执和冻结 BOM 保存快照，相关操作人员由版本、交接、BOM 和审计记录核对。
        // 待处理的交接通知、审批分工和独立业务配置仍核对其人员字段。采购同步行来自外部订单。
        var columns = await connection.QueryAsync<UserReferenceColumn>(new CommandDefinition(
            """
            SELECT source.TABLE_NAME TableName, source.COLUMN_NAME ColumnName,
                   COALESCE(reference.REFERENCED_COLUMN_NAME, '') ReferencedColumn,
                   source.DATA_TYPE DataType
            FROM information_schema.COLUMNS source
            INNER JOIN information_schema.TABLES db_table
                ON db_table.TABLE_SCHEMA=source.TABLE_SCHEMA AND db_table.TABLE_NAME=source.TABLE_NAME AND db_table.TABLE_TYPE='BASE TABLE'
            LEFT JOIN information_schema.KEY_COLUMN_USAGE reference
                ON reference.TABLE_SCHEMA=source.TABLE_SCHEMA AND reference.TABLE_NAME=source.TABLE_NAME
                   AND reference.COLUMN_NAME=source.COLUMN_NAME AND reference.REFERENCED_TABLE_NAME='pdm_user'
                   AND reference.REFERENCED_TABLE_SCHEMA=source.TABLE_SCHEMA
            WHERE source.TABLE_SCHEMA=DATABASE()
              AND source.TABLE_NAME NOT IN ('pdm_user','pdm_user_role','user_table_preference','login_security_state')
              AND (reference.REFERENCED_COLUMN_NAME IS NOT NULL
                OR (source.DATA_TYPE IN ('varchar','char') AND (source.COLUMN_NAME IN (
                    'username','actor','owner','master_owner','assignee','reviewer','model_reviewer','drawing_reviewer','recipient',
                    'active_username','recipient_username','requester_username','from_user','to_user','checked_out_by',
                    'principal_key','project_manager','main_designer','sales_contact','presales_contact','engineer',
                    'contact_person','requested_for','assigned_to','responsible_user','submitter','user_code','manager','primary_manager','handler')
                    OR RIGHT(source.COLUMN_NAME, 3)='_by' OR RIGHT(source.COLUMN_NAME, 9)='_username'))
                OR (source.DATA_TYPE IN ('binary','varbinary') AND source.COLUMN_NAME='user_id' AND source.CHARACTER_MAXIMUM_LENGTH=16)
                OR (source.TABLE_NAME NOT IN ('audit_entry','document_version','reference_snapshot','archive_batch_receipt','purchase_analysis_line')
                    AND NOT (source.TABLE_NAME='bom_version' AND source.COLUMN_NAME='snapshot_json')
                    AND NOT (source.TABLE_NAME='release_package' AND source.COLUMN_NAME IN ('mechanical_bom_snapshot_json',
                        'electrical_bom_snapshot_json','standard_bom_snapshot_json','non_standard_bom_snapshot_json'))
                    AND (source.DATA_TYPE='json'
                        OR (source.DATA_TYPE IN ('mediumtext','longtext','text') AND (
                            RIGHT(source.COLUMN_NAME, 5)='_json' OR (source.TABLE_NAME='pdm_system_setting' AND source.COLUMN_NAME='setting_value'))))))
            ORDER BY source.TABLE_NAME, source.COLUMN_NAME
            """,
            transaction: transaction, cancellationToken: ct));
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in columns.OrderBy(column => IsStructuredUserReference(column) ? 2 : column.ReferencedColumn.Length == 0 ? 1 : 0).Chunk(16))
        {
            var statements = batch.Select(UserReferenceQuery);
            var rows = await connection.QueryAsync<UserReferenceMatch>(new CommandDefinition(string.Join("\nUNION ALL\n", statements),
                new { Usernames = usernames.Except(references.Keys, StringComparer.OrdinalIgnoreCase).ToArray() }, transaction, cancellationToken: ct));
            foreach (var match in rows) references.TryAdd(match.Username, UserReferenceReason(batch[match.TableIndex].TableName));
            if (references.Count == usernames.Count) break;
        }
        return references;
    }

    private static bool IsStructuredUserReference(UserReferenceColumn reference) =>
        reference.DataType is "json" or "text" or "mediumtext" or "longtext";

    private static string UserReferenceQuery(UserReferenceColumn reference, int index)
    {
        var table = QuoteUserReferenceIdentifier(reference.TableName);
        var column = "business_record." + QuoteUserReferenceIdentifier(reference.ColumnName);
        var select = $"SELECT DISTINCT account.username Username, {index} TableIndex FROM {table} business_record";
        if (!IsStructuredUserReference(reference))
        {
            var matches = reference.ReferencedColumn == "id" || reference.DataType is "binary" or "varbinary"
                ? $"{column}=account.id"
                : $"{column}=account.username";
            if (reference.ColumnName == "principal_key") matches += " AND business_record.principal_type='User'";
            return $"{select} INNER JOIN pdm_user account ON {matches} WHERE account.username IN @Usernames";
        }

        var paths = UserReferenceJsonPaths(reference);
        var json = reference.DataType == "json" ? column : $"CASE WHEN JSON_VALID({column}) THEN {column} ELSE NULL END";
        var settingScope = reference.TableName == "pdm_system_setting"
            ? """
                AND (business_record.setting_key IN ('material_approval_rules','project_permission_settings','document_operation_rules')
                    OR (business_record.setting_key NOT LIKE 'authorization_center_backup_%'
                        AND business_record.setting_key REGEXP 'approval|workflow|notification'))
                """
            : "";
        return $"""
            {select}
            INNER JOIN JSON_TABLE(JSON_EXTRACT({json},{paths}), '$[*]' COLUMNS (Username VARCHAR(100) PATH '$' NULL ON ERROR)) person
            INNER JOIN pdm_user account ON person.Username=account.username
            WHERE account.username IN @Usernames {settingScope}
            """;
    }

    private static string UserReferenceJsonPaths(UserReferenceColumn reference) => (reference.TableName, reference.ColumnName) switch
    {
        ("archive_batch_followup", "followup_json") => """
            '$.receipt.actor','$.receipt.results[*].handoffTo','$.receipt.results[*].document.checkedOutBy',
            '$.receipt.results[*].document.checkoutReleaseRequestedBy','$.receipt.results[*].document.deletedBy',
            '$.receipt.results[*].version.createdBy','$.previousDocuments[*].checkedOutBy',
            '$.previousDocuments[*].checkoutReleaseRequestedBy','$.previousDocuments[*].deletedBy'
            """,
        // 普通文本即使恰好出现同名账号也不属于人员引用。
        _ => """
            '$**.username','$**.userName','$**.actor','$**.owner','$**.assignee','$**.recipient','$**.reviewer','$**.approver',
            '$**.createdBy','$**.updatedBy','$**.requestedBy','$**.submittedBy','$**.decidedBy','$**.approvedBy',
            '$**.assignedBy','$**.checkedOutBy','$**.manager','$**.responsibleUser','$**.supervisor','$**.rejecter',
            '$**.primaryProjectManager','$**.designLead','$**.primaryManager','$**.salesContact','$**.presalesContact',
            '$**.members[*]','$**.recipients[*]','$**.eligibleUsernames[*]','$**.approverUsernames[*]','$**.usernames[*]',
            '$**.reviewers[*]','$**.approvers[*]','$**.designers[*]','$**.designLeads[*]','$**.responsibleUsers[*]',
            '$**.collaborativeManagers[*]','$**.collaborativeProjectManagers[*]','$**.phaseOwners.*','$**.customPositionMembers.*[*]'
            """
    };

    private static string QuoteUserReferenceIdentifier(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string UserReferenceReason(string table) => table switch
    {
        "organization_membership" or "organization_unit_manager" => "该账号仍被组织关系引用，无法删除。",
        "project_assignment" or "project_responsible" or "project" => "该账号仍被项目职责或人员分工引用，无法删除。",
        "user_company_access" => "该账号仍与跨公司授权有关，无法删除。",
        "project_user_access" or "document_user_access" or "folder_template_permission" or "project_folder_permission" => "该账号仍有项目、图档或目录授权，无法删除。",
        "password_reset_request" => "该账号仍有密码重置申请，无法删除。",
        "document" or "document_version" => "该账号仍被图档或版本记录引用，无法删除。",
        "audit_entry" => "该账号仍被操作审计记录引用，无法删除。",
        _ => "该账号仍被系统业务记录或配置引用，无法删除。"
    };

    private sealed record UserReferenceColumn(string TableName, string ColumnName, string ReferencedColumn, string DataType);
    private sealed class UserReferenceMatch
    {
        public string Username { get; init; } = "";
        public int TableIndex { get; init; }
    }
}
