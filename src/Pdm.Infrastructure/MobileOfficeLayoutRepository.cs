using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMobileOfficeLayoutRepository : IMobileOfficeLayoutRepository
{
    private readonly Dictionary<Guid, MobileOfficeLayoutRecord> records = [];
    private readonly object gate = new();
    public Task<MobileOfficeLayoutRecord?> FindAsync(Guid companyId, CancellationToken ct)
    { lock (gate) return Task.FromResult(records.GetValueOrDefault(companyId)); }
    public Task<MobileOfficeLayoutRecord> SaveAsync(MobileOfficeLayoutRecord value, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((records.GetValueOrDefault(value.CompanyId)?.RowVersion ?? 0) != expectedVersion)
                throw new PdmConflictException("移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。");
            var saved = value with { RowVersion = expectedVersion + 1 };
            records[value.CompanyId] = saved;
            return Task.FromResult(saved);
        }
    }
}

public sealed class MySqlMobileOfficeLayoutRepository(IOptions<PdmDatabaseOptions> options) : IMobileOfficeLayoutRepository
{
    private sealed class Row
    {
        public Guid CompanyId { get; set; }
        public string Payload { get; set; } = "";
        public long RowVersion { get; set; }
    }
    public async Task<MobileOfficeLayoutRecord?> FindAsync(Guid companyId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT company_id CompanyId,payload Payload,row_version RowVersion FROM mobile_office_layouts WHERE company_id=@companyId",
            new { companyId }, cancellationToken: ct));
        return row is null ? null : new(row.CompanyId, row.Payload, row.RowVersion);
    }
    public async Task<MobileOfficeLayoutRecord> SaveAsync(MobileOfficeLayoutRecord value, long expectedVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO mobile_office_layouts(company_id,payload,row_version) VALUES(@CompanyId,'',0)", value, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT row_version FROM mobile_office_layouts WHERE company_id=@CompanyId FOR UPDATE", value, transaction, cancellationToken: ct));
        if (version != expectedVersion)
            throw new PdmConflictException("移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。");
        var saved = value with { RowVersion = expectedVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE mobile_office_layouts SET payload=@Payload,row_version=@RowVersion WHERE company_id=@CompanyId", saved, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
}
