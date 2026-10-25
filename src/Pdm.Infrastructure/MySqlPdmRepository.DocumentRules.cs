using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<DocumentOperationRuleSettings> GetDocumentOperationRulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='document_operation_rules'", cancellationToken: cancellationToken));
        return (string.IsNullOrWhiteSpace(json) ? DocumentOperationRuleSettings.Default
            : JsonSerializer.Deserialize<DocumentOperationRuleSettings>(json, jsonOptions) ?? DocumentOperationRuleSettings.Default).Normalize();
    }

    public async Task<DocumentOperationRuleSettings> SetDocumentOperationRuleAsync(string ruleId, bool enabled, long expectedRevision, CancellationToken cancellationToken)
    {
        if (!DocumentOperationRuleIds.Contains(ruleId)) throw new PdmRuleException("图档规则编号不存在，请刷新规则列表。");
        if (enabled && !DocumentOperationRuleCatalog.CanBlock(ruleId)) throw new PdmRuleException("该项已改为自动处理和提示，不再提供拦截开关。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES('document_operation_rules',@Value,@Now)",
            new { Value = JsonSerializer.Serialize(DocumentOperationRuleSettings.Default, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var json = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='document_operation_rules' FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken));
        var current = string.IsNullOrWhiteSpace(json) ? DocumentOperationRuleSettings.Default
            : JsonSerializer.Deserialize<DocumentOperationRuleSettings>(json, jsonOptions) ?? DocumentOperationRuleSettings.Default;
        if (current.Revision != expectedRevision) throw new PdmConflictException("规则设置已被其他人修改，请刷新后重试。");
        var disabled = current.Normalize().DisabledRuleIds.ToHashSet(StringComparer.Ordinal);
        if (enabled) disabled.Remove(ruleId); else disabled.Add(ruleId);
        var updated = new DocumentOperationRuleSettings(current.Revision + 1, disabled.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_system_setting SET setting_value=@Value,updated_at=@Now WHERE setting_key='document_operation_rules'",
            new { Value = JsonSerializer.Serialize(updated, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }
}
