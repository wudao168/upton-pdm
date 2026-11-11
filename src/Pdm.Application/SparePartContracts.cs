using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SparePriceBand(decimal From, decimal? To, decimal Multiplier);

public sealed record SparePricing(long RowVersion, decimal CostMultiplier, IReadOnlyList<SparePriceBand> Bands,
    string U9OrganizationCode = "", string Currency = "CNY")
{
    public static SparePricing Default => new(0, 1, []);

    public SparePricing Validate()
    {
        if (CostMultiplier is <= 0 or > 10000 || Bands is null || Bands.Count is < 1 or > 100)
            throw new PdmRuleException("请填写大于0的成本倍率，并设置1至100个报价区间。");
        var sorted = Bands.OrderBy(band => band.From).ToArray();
        decimal? next = 0;
        foreach (var band in sorted)
        {
            if (next != band.From || band.To <= band.From || band.Multiplier is <= 0 or > 10000 ||
                decimal.Round(band.From, 2) != band.From || (band.To is decimal upper && decimal.Round(upper, 2) != upper))
                throw new PdmRuleException("报价区间须从0开始连续覆盖，上限大于下限，倍率须大于0。区间金额最多两位小数。");
            next = band.To;
        }
        if (next is not null) throw new PdmRuleException("最后一个报价区间必须不设上限。");
        var organization = U9OrganizationCode?.Trim() ?? "";
        if (organization.Length > 50 || organization.Any(c => char.IsControl(c) || c == ','))
            throw new PdmRuleException("请填写有效的U9C组织编码。");
        var currency = Currency?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new PdmRuleException("请填写三位本位币编码，例如CNY。");
        return this with { Bands = sorted, U9OrganizationCode = organization, Currency = currency };
    }

    public (decimal? Cost, decimal? Price) Calculate(decimal? purchase, decimal? manual)
    {
        var cost = purchase is decimal value ? Round(value * CostMultiplier) : (decimal?)null;
        var band = cost is decimal displayed ? Bands.FirstOrDefault(b => displayed >= b.From && (b.To is null || displayed < b.To)) : null;
        return (cost, manual ?? (band is null ? null : Round(cost!.Value * band.Multiplier)));
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public sealed record SparePurchasePrice(string MaterialCode, decimal? UnitPrice, string? UnitCode, string? UnitName,
    string? DocumentNumber, DateTimeOffset? PurchasedAt, DateTimeOffset SynchronizedAt);

public sealed record SpareDrawing(string Name, string Revision, Guid? DocumentId, Guid? VersionId,
    BomFileReference? BomFile, bool CanPreview);

public sealed record SparePart(Guid Id, Guid ProjectId, string Identity, string MaterialCode, string Name,
    string? Specification, string? Brand, string Unit, decimal Quantity, string Category, bool Machined,
    string? Material, string? SurfaceTreatment, string? Note, string? SourceKind, string? BomVersion,
    string? Configuration, IReadOnlyList<SpareDrawing> Drawings, decimal? ManualPrice = null, long RowVersion = 1,
    decimal? QuantityPerSet = null);

public sealed record SpareManualInput(Guid ProjectId, string? MaterialCode, string Name, string? Specification,
    string? Brand, string Unit, decimal Quantity, string Category, bool Machined, string? Material,
    string? SurfaceTreatment, string? Note, decimal? QuantityPerSet = null);

public sealed record SpareBomInput(Guid ProjectId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    BomKind Kind, Guid? VersionId, IReadOnlyList<Guid> ItemIds, string? Category);
public sealed record SpareDetailsInput(long RowVersion, string? MaterialCode, string Category, decimal Quantity, string? Note,
    decimal? QuantityPerSet = null, bool ClearQuantityPerSet = false);
public sealed record SparePriceInput(long RowVersion, decimal? Price);
public sealed record SpareCategory(string Id, string Name, bool IsActive);
public sealed record SpareCategorySettings(long RowVersion, IReadOnlyList<SpareCategory> Categories)
{
    public static SpareCategorySettings Default => new(0,
        [new("Wear", "易损件", true), new("Consumable", "耗材", true), new("Spare", "备件", true), new("Other", "其他", true)]);

    public string RequireActive(string? id) => Categories.Any(category => category.Id == id && category.IsActive)
        ? id! : throw new PdmRuleException("请选择已启用的备件分类。");

    public string Preferred(string id) => Categories.FirstOrDefault(category => category.Id == id && category.IsActive)?.Id
        ?? Categories.First(category => category.IsActive).Id;
}

public sealed record SpareProject(Guid Id, Guid? ParentId, Guid RootId, string Code, string Name,
    string? DeviceModel, IReadOnlyList<string> SerialNumbers);
public sealed record SparePartRow(SparePart Part, SpareProject Project, SpareProject Root, decimal? Cost, decimal? Price,
    string PriceSource, string CostStatus, string Currency, DateTimeOffset? PurchasedAt, DateTimeOffset? SynchronizedAt,
    decimal? RawCost, string? PurchaseDocument, string CategoryName);
public sealed record SparePartPage(IReadOnlyList<SparePartRow> Items, IReadOnlyList<SpareProject> Projects, int Total,
    int MissingPrice, int MissingDrawing, int Page, int PageSize, bool CanSeeCost, bool CanMaintain, bool CanPrice,
    bool CanPreview, bool CanDownload, bool CanExport, IReadOnlyList<SpareCategory> Categories, bool CanManageCategories);

public interface ISparePartStore
{
    Task<IReadOnlyList<SparePart>> ListAsync(Guid company, IReadOnlyCollection<Guid> projects, CancellationToken ct);
    Task<SparePart?> FindAsync(Guid company, Guid id, CancellationToken ct);
    Task<int> AddAsync(Guid company, IReadOnlyList<SparePart> parts, CancellationToken ct);
    Task SaveAsync(Guid company, SparePart part, CancellationToken ct);
    Task<SpareCategorySettings> CategoriesAsync(Guid company, CancellationToken ct);
    Task SaveCategoriesAsync(Guid company, SpareCategorySettings settings, CancellationToken ct);
    Task<SparePricing> PricingAsync(Guid company, CancellationToken ct);
    Task SavePricingAsync(Guid company, SparePricing pricing, CancellationToken ct);
    Task<IReadOnlyList<SparePurchasePrice>> PricesAsync(Guid company, string organization, string currency, IReadOnlyCollection<string> codes, CancellationToken ct);
    Task SavePricesAsync(Guid company, string organization, string currency, IReadOnlyList<SparePurchasePrice> prices, CancellationToken ct);
}
