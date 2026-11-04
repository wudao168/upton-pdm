using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMaterialBrandRepository : IMaterialBrandRepository
{
    private readonly Dictionary<Guid, MaterialBrand> values = new();
    public Task<IReadOnlyList<MaterialBrand>> ListAsync(CancellationToken ct) { lock (values) return Task.FromResult<IReadOnlyList<MaterialBrand>>(values.Values.OrderBy(brand => brand.Name).ToArray()); }
    public Task<MaterialBrand> SaveAsync(MaterialBrand brand, long? expectedVersion, CancellationToken ct)
    {
        lock (values)
        {
            var current = values.GetValueOrDefault(brand.Id);
            if (current is not null && current.RowVersion != expectedVersion) throw new PdmConflictException("品牌已被其他人员修改，请刷新后重试。");
            var saved = brand with { RowVersion = (current?.RowVersion ?? 0) + 1 };
            values[brand.Id] = saved;
            return Task.FromResult(saved);
        }
    }
}

public sealed class MySqlMaterialBrandRepository(IOptions<PdmDatabaseOptions> options) : IMaterialBrandRepository
{
    public async Task<IReadOnlyList<MaterialBrand>> ListAsync(CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<BrandRow>(new CommandDefinition("SELECT id Id,name Name,is_active IsActive,row_version RowVersion,previous_names PreviousNames FROM material_brand ORDER BY name", cancellationToken: ct));
        return rows.Select(row => new MaterialBrand(row.Id, row.Name, row.IsActive, row.RowVersion, JsonSerializer.Deserialize<string[]>(row.PreviousNames) ?? [])).ToArray();
    }
    public async Task<MaterialBrand> SaveAsync(MaterialBrand brand, long? expectedVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var parameters = new { brand.Id, brand.Name, brand.IsActive, PreviousNames = JsonSerializer.Serialize(brand.PreviousNames), ExpectedVersion = expectedVersion };
        try
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(expectedVersion.HasValue
                ? "UPDATE material_brand SET name=@Name,is_active=@IsActive,previous_names=@PreviousNames,row_version=row_version+1 WHERE id=@Id AND row_version=@ExpectedVersion"
                : "INSERT INTO material_brand(id,name,is_active,previous_names,row_version) VALUES(@Id,@Name,@IsActive,@PreviousNames,1)", parameters, cancellationToken: ct));
            if (changed == 0) throw new PdmConflictException("品牌已被其他人员修改，请刷新后重试。");
        }
        catch (MySqlException error) when (error.Number == 1062) { throw new PdmConflictException("品牌名称已存在，请选择已有品牌。"); }
        return brand with { RowVersion = (expectedVersion ?? 0) + 1 };
    }
    private sealed record BrandRow(Guid Id, string Name, bool IsActive, long RowVersion, string PreviousNames);
}
