using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<CompanyBusinessDetailsStatus>> ListCompanyBusinessDetailsAsync(Guid? companyId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<CompanyBusinessDetailsStatusRow>(new CommandDefinition("""
            SELECT company.id OrganizationId, COALESCE(invoice.is_configured,0) InvoiceConfigured,
                   COALESCE(receiving.is_configured,0) ReceivingConfigured
            FROM project_organization company
            LEFT JOIN company_business_details invoice ON invoice.organization_id=company.id AND invoice.detail_kind='invoice'
            LEFT JOIN company_business_details receiving ON receiving.organization_id=company.id AND receiving.detail_kind='receiving'
            WHERE @CompanyId IS NULL OR company.id=@CompanyId
            """, new { CompanyId = companyId }, cancellationToken: ct));
        return rows.Select(row => new CompanyBusinessDetailsStatus(row.OrganizationId, row.InvoiceConfigured, row.ReceivingConfigured)).ToArray();
    }

    public async Task<CompanyBusinessDetails> ReadCompanyBusinessDetailsAsync(Guid companyId, string kind, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<CompanyBusinessDetailsRow>(new CommandDefinition("""
            SELECT details.row_version RowVersion, details.payload_json PayloadJson
            FROM project_organization company
            LEFT JOIN company_business_details details ON details.organization_id=company.id AND details.detail_kind=@Kind
            WHERE company.id=@CompanyId
            """, new { CompanyId = companyId, Kind = kind }, cancellationToken: ct));
        if (row is null) throw new PdmNotFoundException("公司不存在。");
        return row.PayloadJson is null ? new() : JsonSerializer.Deserialize<CompanyBusinessDetails>(row.PayloadJson)! with { RowVersion = row.RowVersion ?? 0 };
    }

    public async Task<CompanyBusinessDetails> SaveCompanyBusinessDetailsAsync(Guid companyId, string kind, CompanyBusinessDetails input, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project_organization WHERE id=@CompanyId FOR UPDATE",
            new { CompanyId = companyId }, transaction, cancellationToken: ct)) is null) throw new PdmNotFoundException("公司不存在。");
        var key = new { CompanyId = companyId, Kind = kind };
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT row_version FROM company_business_details WHERE organization_id=@CompanyId AND detail_kind=@Kind FOR UPDATE
            """, key, transaction, cancellationToken: ct)) ?? 0;
        if (version != input.RowVersion) throw new PdmConflictException("公司资料已更新，请重新打开后保存。");
        var saved = input with { RowVersion = version + 1 };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO company_business_details(organization_id,detail_kind,payload_json,row_version,is_configured)
            VALUES (@CompanyId,@Kind,@Payload,@Version,@Configured)
            ON DUPLICATE KEY UPDATE payload_json=@Payload,row_version=@Version,is_configured=@Configured
            """, new { key.CompanyId, key.Kind, Payload = JsonSerializer.Serialize(saved), Version = saved.RowVersion, Configured = CompanyDetailsConfigured.HasValues(saved) },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }

    private sealed class CompanyBusinessDetailsRow
    {
        public long? RowVersion { get; init; }
        public string? PayloadJson { get; init; }
    }

    private sealed class CompanyBusinessDetailsStatusRow
    {
        public Guid OrganizationId { get; init; }
        public bool InvoiceConfigured { get; init; }
        public bool ReceivingConfigured { get; init; }
    }
}

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<(Guid CompanyId, string Kind), CompanyBusinessDetails> companyBusinessDetails = [];

    public Task<IReadOnlyList<CompanyBusinessDetailsStatus>> ListCompanyBusinessDetailsAsync(Guid? companyId, CancellationToken ct)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<CompanyBusinessDetailsStatus>>(organizations.Keys.Where(id => companyId is null || id == companyId)
                .Select(id => new CompanyBusinessDetailsStatus(id, CompanyDetailsConfigured.HasValues(companyBusinessDetails.GetValueOrDefault((id, "invoice"))),
                    CompanyDetailsConfigured.HasValues(companyBusinessDetails.GetValueOrDefault((id, "receiving"))))).ToArray());
    }

    public Task<CompanyBusinessDetails> ReadCompanyBusinessDetailsAsync(Guid companyId, string kind, CancellationToken ct)
    {
        lock (gate)
        {
            if (!organizations.ContainsKey(companyId)) throw new PdmNotFoundException("公司不存在。");
            return Task.FromResult(companyBusinessDetails.GetValueOrDefault((companyId, kind)) ?? new CompanyBusinessDetails());
        }
    }

    public Task<CompanyBusinessDetails> SaveCompanyBusinessDetailsAsync(Guid companyId, string kind, CompanyBusinessDetails input, CancellationToken ct)
    {
        lock (gate)
        {
            if (!organizations.ContainsKey(companyId)) throw new PdmNotFoundException("公司不存在。");
            var key = (companyId, kind);
            var version = companyBusinessDetails.GetValueOrDefault(key)?.RowVersion ?? 0;
            if (version != input.RowVersion) throw new PdmConflictException("公司资料已更新，请重新打开后保存。");
            return Task.FromResult(companyBusinessDetails[key] = input with { RowVersion = version + 1 });
        }
    }
}

internal static class CompanyDetailsConfigured
{
    public static bool HasValues(CompanyBusinessDetails? details) =>
        details?.Invoice is { } invoice && (invoice.Title ?? invoice.TaxNumber ?? invoice.Address ?? invoice.Phone ?? invoice.Bank ?? invoice.BankAccount) is not null
        || details?.Receiving is { } receiving && (receiving.Recipient ?? receiving.Contact ?? receiving.Address) is not null;
}
