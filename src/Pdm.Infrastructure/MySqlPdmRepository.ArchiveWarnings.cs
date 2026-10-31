using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<ArchiveWarningPolicy> GetArchiveWarningPolicyAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='archive_warning_policy'", cancellationToken: ct));
        return json is null ? new() : JsonSerializer.Deserialize<ArchiveWarningPolicy>(json, jsonOptions)
            ?? throw new PdmRuleException("存档警告策略无法读取，请联系管理员。");
    }
    public async Task<ArchiveWarningPolicy> SaveArchiveWarningPolicyAsync(ArchiveWarningPolicy input, CancellationToken ct)
    {
        ArchiveWarningPolicy.Validate(input);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES('archive_warning_policy',@Value,@Now)",
            new { Value = JsonSerializer.Serialize(new ArchiveWarningPolicy(), jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: ct));
        var json = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='archive_warning_policy' FOR UPDATE", transaction: transaction, cancellationToken: ct));
        var previous = JsonSerializer.Deserialize<ArchiveWarningPolicy>(json!, jsonOptions)!;
        if (previous.Revision != input.Revision) throw new PdmConflictException("存档警告策略已被修改，请刷新后重试。");
        var result = new ArchiveWarningPolicy { Revision = previous.Revision + 1, BlockAll = input.BlockAll,
            BlockingCodes = input.BlockingCodes.OrderBy(code => code, StringComparer.Ordinal).ToArray() };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_system_setting SET setting_value=@Value,updated_at=@Now WHERE setting_key='archive_warning_policy'",
            new { Value = JsonSerializer.Serialize(result, jsonOptions), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return result;
    }
}
