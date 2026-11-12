namespace Upton.Pdm.Application;

public sealed record PurchaseAnalysisSettings(long Revision, string[] Organizations, int HistoryMonths = 24,
    int IntervalMinutes = 1440, bool AutoSync = true);
public sealed record PurchaseAnalysisLine(string Id, string Organization, string OrderId, string OrderNumber, int LineNumber,
    DateOnly Date, string TypeCode, string TypeName, string MaterialCode, string MaterialName, string Specification,
    string Brand, string CategoryCode, string CategoryName, string SupplierCode, string SupplierName,
    string BuyerCode, string BuyerName, string ProjectCode, string SubprojectCode, string UnitCode, string UnitName,
    decimal Quantity, string Currency, decimal? NetAmount)
{
    public decimal? UnitPrice => Quantity > 0 ? NetAmount / Quantity : null;
}
public sealed record PurchaseAnalysisStatus(DateTimeOffset? LastAttempt = null, DateTimeOffset? LastSuccess = null,
    DateOnly? From = null, DateOnly? Through = null, string? Error = null, int SourceCount = 0);
public sealed record PurchaseAnalysisSnapshot(PurchaseAnalysisStatus Status, IReadOnlyList<PurchaseAnalysisLine> Lines);
public interface IPurchaseAnalysisStore
{
    Task<PurchaseAnalysisSettings> SettingsAsync(Guid company, CancellationToken ct);
    Task SaveSettingsAsync(Guid company, PurchaseAnalysisSettings settings, CancellationToken ct);
    Task<PurchaseAnalysisSnapshot> ReadAsync(Guid company, CancellationToken ct);
    Task ReplaceAsync(Guid company, long revision, PurchaseAnalysisSnapshot snapshot, CancellationToken ct);
    Task FailureAsync(Guid company, DateTimeOffset at, string message, CancellationToken ct);
    Task<IReadOnlyList<Guid>> CompaniesAsync(CancellationToken ct);
}
public sealed record PurchaseAnalysisFilter
{
    public DateOnly? From { get; init; }
    public DateOnly? Through { get; init; }
    public string[] Organizations { get; init; } = [];
    public string[] Types { get; init; } = [];
    public string[] Categories { get; init; } = [];
    public string[] Suppliers { get; init; } = [];
    public string[] Buyers { get; init; } = [];
    public string[] Projects { get; init; } = [];
    public string Query { get; init; } = "";
    public string Currency { get; init; } = "";
    public string Unit { get; init; } = "";
    public int? MinOrders { get; init; }
    public int? MaxOrders { get; init; }
    public decimal? MinQuantity { get; init; }
    public decimal? MaxQuantity { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public decimal? MinPriceChange { get; init; }
    public int FrequentOrders { get; init; } = 6;
    public int FragmentDays { get; init; } = 7;
    public decimal SmallOrderAmount { get; init; } = 1000;
    public int Top { get; init; } = 20;
    public string Dimension { get; init; } = "material";
    public string DetailKey { get; init; } = "";
    public int Page { get; init; } = 1;
}
public sealed record PurchaseAnalysisOption(string Value, string Label);
public sealed record PurchaseAnalysisMetric(string Key, string Code, string Name, string Unit, string Currency,
    int Orders, decimal Quantity, decimal? Amount, decimal? AveragePrice, decimal? LastPrice, decimal? MinPrice,
    decimal? MaxPrice, decimal? PriceChange, int Suppliers, int Projects, decimal OrdersPerMonth, decimal? AverageInterval,
    decimal AverageBatch, int FragmentOrders, int? SmallOrders, string Reason,
    decimal? Share = null, decimal? CumulativeShare = null, string? Abc = null);
public sealed record PurchaseAnalysisPeriod(string Month, int Orders, decimal Quantity, decimal? Amount, decimal? AveragePrice);
public sealed record PurchaseAnalysisResult(PurchaseAnalysisStatus Status, DateOnly From, DateOnly Through,
    IReadOnlyDictionary<string, PurchaseAnalysisOption[]> Options, IReadOnlyList<PurchaseAnalysisMetric> Frequent,
    IReadOnlyList<PurchaseAnalysisMetric> Volume, IReadOnlyList<PurchaseAnalysisMetric> Spend,
    IReadOnlyList<PurchaseAnalysisMetric> Prices, IReadOnlyList<PurchaseAnalysisMetric> Fragmented,
    IReadOnlyList<PurchaseAnalysisMetric> Suppliers, IReadOnlyList<PurchaseAnalysisPeriod> Trend,
    IReadOnlyList<PurchaseAnalysisLine> Details, int DetailCount, int OrderCount, int MaterialCount, int SupplierCount,
    int FrequentCount, IReadOnlyDictionary<string, decimal?> Amounts, int MissingAmounts,
    bool CanCost, bool CanExport, bool CanSync, bool CanConfigure, IReadOnlyList<string> Notices)
{
    public IReadOnlyList<PurchaseAnalysisMetric> PriceComparisons { get; init; } = [];
}
