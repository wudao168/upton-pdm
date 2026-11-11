using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : ISparePartStore
{
    async Task<IReadOnlyList<SparePart>> ISparePartStore.ListAsync(Guid company, IReadOnlyCollection<Guid> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return [];
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM spare_part WHERE company_id=@Company AND project_id IN @Projects",
            new { Company = company, Projects = projects }, cancellationToken: ct));
        return rows.Select(SparePartPersistence.Read<SparePart>).ToArray();
    }

    async Task<SparePart?> ISparePartStore.FindAsync(Guid company, Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM spare_part WHERE company_id=@Company AND id=@Id",
            new { Company = company, Id = id }, cancellationToken: ct));
        return json is null ? null : SparePartPersistence.Read<SparePart>(json);
    }

    async Task<int> ISparePartStore.AddAsync(Guid company, IReadOnlyList<SparePart> parts, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var added = 0;
        foreach (var part in parts.OrderBy(item => item.ProjectId).ThenBy(item => item.Identity, StringComparer.Ordinal))
        {
            added += await connection.ExecuteAsync(new CommandDefinition("""
                INSERT IGNORE INTO spare_part(company_id,id,project_id,identity_hash,row_version,payload_json)
                VALUES (@Company,@Id,@ProjectId,@Hash,1,@Payload)
                """, new { Company = company, part.Id, part.ProjectId,
                    Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(part.Identity))),
                    Payload = JsonSerializer.Serialize(part) }, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
        return added;
    }

    async Task ISparePartStore.SaveAsync(Guid company, SparePart part, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE spare_part SET payload_json=@Payload, row_version=row_version+1
            WHERE company_id=@Company AND id=@Id AND row_version=@Expected
            """, new { Company = company, part.Id, Expected = part.RowVersion,
                Payload = JsonSerializer.Serialize(part with { RowVersion = part.RowVersion + 1 }) }, cancellationToken: ct));
        if (affected != 1) throw SparePartPersistence.Conflict();
    }

    async Task<SparePricing> ISparePartStore.PricingAsync(Guid company, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM spare_part_pricing WHERE company_id=@Company",
            new { Company = company }, cancellationToken: ct));
        return json is null ? SparePricing.Default : SparePartPersistence.Read<SparePricing>(json);
    }

    async Task<SpareCategorySettings> ISparePartStore.CategoriesAsync(Guid company, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM spare_part_categories WHERE company_id=@Company",
            new { Company = company }, cancellationToken: ct));
        return json is null ? SpareCategorySettings.Default : SparePartPersistence.Read<SpareCategorySettings>(json);
    }

    async Task ISparePartStore.SaveCategoriesAsync(Guid company, SpareCategorySettings settings, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var sql = settings.RowVersion == 0
            ? "INSERT IGNORE INTO spare_part_categories(company_id,row_version,payload_json) VALUES (@Company,1,@Payload)"
            : "UPDATE spare_part_categories SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@Company AND row_version=@Expected";
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new { Company = company,
            Expected = settings.RowVersion, Payload = JsonSerializer.Serialize(settings with { RowVersion = settings.RowVersion + 1 }) },
            cancellationToken: ct));
        if (affected != 1) throw new PdmConflictException("备件分类已被更新，请重新打开后保存。");
    }

    async Task ISparePartStore.SavePricingAsync(Guid company, SparePricing pricing, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var sql = pricing.RowVersion == 0
            ? "INSERT IGNORE INTO spare_part_pricing(company_id,row_version,payload_json) VALUES (@Company,1,@Payload)"
            : "UPDATE spare_part_pricing SET payload_json=@Payload,row_version=row_version+1 WHERE company_id=@Company AND row_version=@Expected";
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new { Company = company,
            Expected = pricing.RowVersion, Payload = JsonSerializer.Serialize(pricing with { RowVersion = pricing.RowVersion + 1 }) },
            cancellationToken: ct));
        if (affected != 1) throw SparePartPersistence.Conflict();
    }

    async Task<IReadOnlyList<SparePurchasePrice>> ISparePartStore.PricesAsync(Guid company, string organization, string currency,
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0) return [];
        await using var connection = await OpenAsync(ct);
        var output = new List<SparePurchasePrice>();
        foreach (var batch in codes.Chunk(500))
        {
            var rows = await connection.QueryAsync<string>(new CommandDefinition("""
                SELECT payload_json FROM spare_part_purchase WHERE company_id=@Company AND organization_code=@Organization
                AND currency=@Currency AND material_code IN @Codes
                """, new { Company = company, Organization = organization, Currency = currency, Codes = batch }, cancellationToken: ct));
            output.AddRange(rows.Select(SparePartPersistence.Read<SparePurchasePrice>));
        }
        return output;
    }

    async Task ISparePartStore.SavePricesAsync(Guid company, string organization, string currency,
        IReadOnlyList<SparePurchasePrice> prices, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var price in prices.OrderBy(row => row.MaterialCode, StringComparer.OrdinalIgnoreCase))
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO spare_part_purchase(company_id,organization_code,currency,material_code,synced_at,payload_json)
                VALUES (@Company,@Organization,@Currency,@Code,@Synced,@Payload)
                ON DUPLICATE KEY UPDATE payload_json=IF(synced_at<=@Synced,@Payload,payload_json),synced_at=GREATEST(synced_at,@Synced)
                """, new { Company = company, Organization = organization, Currency = currency, Code = price.MaterialCode,
                    Synced = price.SynchronizedAt.UtcDateTime, Payload = JsonSerializer.Serialize(price) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }
}

public sealed partial class InMemoryPdmRepository : ISparePartStore
{
    private readonly Dictionary<(Guid Company, Guid Id), SparePart> spareParts = [];
    private readonly Dictionary<Guid, SparePricing> sparePricing = [];
    private readonly Dictionary<Guid, SpareCategorySettings> spareCategories = [];
    private readonly Dictionary<(Guid Company, string Organization, string Currency, string Code), SparePurchasePrice> sparePrices = [];

    Task<IReadOnlyList<SparePart>> ISparePartStore.ListAsync(Guid company, IReadOnlyCollection<Guid> projects, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<SparePart>>(spareParts.Where(item => item.Key.Company == company
            && projects.Contains(item.Value.ProjectId)).Select(item => item.Value).ToArray());
    }

    Task<SparePart?> ISparePartStore.FindAsync(Guid company, Guid id, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(spareParts.GetValueOrDefault((company, id)));
    }

    Task<int> ISparePartStore.AddAsync(Guid company, IReadOnlyList<SparePart> parts, CancellationToken ct)
    {
        lock (gate)
        {
            var added = 0;
            foreach (var part in parts)
            {
                if (spareParts.Any(item => item.Key.Company == company && item.Value.ProjectId == part.ProjectId
                    && item.Value.Identity == part.Identity)) continue;
                spareParts.Add((company, part.Id), part);
                added++;
            }
            return Task.FromResult(added);
        }
    }

    Task ISparePartStore.SaveAsync(Guid company, SparePart part, CancellationToken ct)
    {
        lock (gate)
        {
            var key = (company, part.Id);
            if (spareParts.GetValueOrDefault(key)?.RowVersion != part.RowVersion) throw SparePartPersistence.Conflict();
            spareParts[key] = part with { RowVersion = part.RowVersion + 1 };
            return Task.CompletedTask;
        }
    }

    Task<SparePricing> ISparePartStore.PricingAsync(Guid company, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(sparePricing.GetValueOrDefault(company) ?? SparePricing.Default);
    }

    Task<SpareCategorySettings> ISparePartStore.CategoriesAsync(Guid company, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(spareCategories.GetValueOrDefault(company) ?? SpareCategorySettings.Default);
    }

    Task ISparePartStore.SaveCategoriesAsync(Guid company, SpareCategorySettings settings, CancellationToken ct)
    {
        lock (gate)
        {
            if ((spareCategories.GetValueOrDefault(company)?.RowVersion ?? 0) != settings.RowVersion)
                throw new PdmConflictException("备件分类已被更新，请重新打开后保存。");
            spareCategories[company] = settings with { RowVersion = settings.RowVersion + 1 };
            return Task.CompletedTask;
        }
    }

    Task ISparePartStore.SavePricingAsync(Guid company, SparePricing pricing, CancellationToken ct)
    {
        lock (gate)
        {
            if ((sparePricing.GetValueOrDefault(company)?.RowVersion ?? 0) != pricing.RowVersion) throw SparePartPersistence.Conflict();
            sparePricing[company] = pricing with { RowVersion = pricing.RowVersion + 1 };
            return Task.CompletedTask;
        }
    }

    Task<IReadOnlyList<SparePurchasePrice>> ISparePartStore.PricesAsync(Guid company, string organization, string currency,
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<SparePurchasePrice>>(codes
            .Select(code => sparePrices.GetValueOrDefault((company, organization, currency, code.ToUpperInvariant())))
            .OfType<SparePurchasePrice>().ToArray());
    }

    Task ISparePartStore.SavePricesAsync(Guid company, string organization, string currency,
        IReadOnlyList<SparePurchasePrice> prices, CancellationToken ct)
    {
        lock (gate)
        {
            foreach (var price in prices)
            {
                var key = (company, organization, currency, price.MaterialCode.ToUpperInvariant());
                if (!sparePrices.TryGetValue(key, out var previous) || previous.SynchronizedAt <= price.SynchronizedAt)
                    sparePrices[key] = price;
            }
            return Task.CompletedTask;
        }
    }
}

internal static class SparePartPersistence
{
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
    public static PdmConflictException Conflict() => new("备件资料已被更新，请刷新后重新保存。");
}
