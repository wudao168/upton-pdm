using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryDingTalkConfigurationRepository : IDingTalkConfigurationRepository
{
    private readonly Dictionary<Guid, DingTalkConfiguration> records = [];
    private readonly object gate = new();
    public Task<DingTalkConfiguration?> FindAsync(Guid companyId, CancellationToken ct) { lock (gate) return Task.FromResult(records.GetValueOrDefault(companyId)); }
    public Task<IReadOnlyList<DingTalkConfiguration>> ListAsync(CancellationToken ct) { lock (gate) return Task.FromResult<IReadOnlyList<DingTalkConfiguration>>(records.Values.ToArray()); }
    public Task<DingTalkConfiguration> SaveAsync(DingTalkConfiguration value, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((records.GetValueOrDefault(value.CompanyId)?.RowVersion ?? 0) != expectedVersion) throw new PdmConflictException("钉钉配置已被修改，请刷新后重试。");
            var saved = value with { RowVersion = expectedVersion + 1 }; records[value.CompanyId] = saved; return Task.FromResult(saved);
        }
    }
}
public sealed class MySqlDingTalkConfigurationRepository(IOptions<PdmDatabaseOptions> options) : IDingTalkConfigurationRepository
{
    private sealed class Row
    {
        public Guid CompanyId { get; set; }
        public string ProtectedPayload { get; set; } = "";
        public long RowVersion { get; set; }
        public DingTalkConfiguration ToValue() => new(CompanyId, ProtectedPayload, RowVersion);
    }
    private const string Select = "SELECT company_id CompanyId,protected_payload ProtectedPayload,row_version RowVersion FROM dingtalk_configuration";
    public async Task<DingTalkConfiguration?> FindAsync(Guid companyId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        return (await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Select + " WHERE company_id=@companyId", new { companyId }, cancellationToken: ct)))?.ToValue();
    }
    public async Task<IReadOnlyList<DingTalkConfiguration>> ListAsync(CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        return (await connection.QueryAsync<Row>(new CommandDefinition(Select, cancellationToken: ct))).Select(row => row.ToValue()).ToArray();
    }
    public async Task<DingTalkConfiguration> SaveAsync(DingTalkConfiguration value, long expectedVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO dingtalk_configuration(company_id,protected_payload,row_version) VALUES(@CompanyId,'',0)", value, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM dingtalk_configuration WHERE company_id=@CompanyId FOR UPDATE", value, transaction, cancellationToken: ct));
        if (version != expectedVersion) throw new PdmConflictException("钉钉配置已被修改，请刷新后重试。");
        var saved = value with { RowVersion = expectedVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition("UPDATE dingtalk_configuration SET protected_payload=@ProtectedPayload,row_version=@RowVersion WHERE company_id=@CompanyId", saved, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct); return saved;
    }
}
