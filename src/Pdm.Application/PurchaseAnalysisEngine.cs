using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class PurchaseAnalysisEngine
{
    public static string MaterialKey(PurchaseAnalysisLine r) => JsonSerializer.Serialize(new[] { "material", r.MaterialCode, r.UnitCode, r.Currency });
    public static string GroupKey(PurchaseAnalysisLine r, string dimension) => dimension switch {
        "supplier" => JsonSerializer.Serialize(new[] { dimension, r.SupplierCode, r.Currency }),
        "category" => JsonSerializer.Serialize(new[] { dimension, r.CategoryCode, r.Currency }), _ => MaterialKey(r) };
    private static string OrderKey(PurchaseAnalysisLine r) => r.Organization + ":" + r.OrderId;
    private static decimal? Sum(IEnumerable<PurchaseAnalysisLine> rows) => rows.All(r => r.NetAmount.HasValue) ? rows.Sum(r => r.NetAmount!.Value) : null;

    public static PurchaseAnalysisResult Build(IReadOnlyList<PurchaseAnalysisLine> authorized, PurchaseAnalysisStatus status,
        PurchaseAnalysisFilter f, DateOnly today, bool canCost, bool canExport, bool canSync, bool canConfigure, bool export = false)
    {
        if (new[] { f.Organizations, f.Types, f.Categories, f.Suppliers, f.Buyers, f.Projects }.Any(a => a is null || a.Length > 1000 || a.Any(s => s is null || s.Length > 200))
            || f.Query is null || f.Query.Length > 200 || f.Currency is null || f.Unit is null || f.DetailKey is null)
            throw new PdmRuleException("筛选条件无效或过长，请清空后重新选择。");
        var from = f.From ?? today.AddYears(-1).AddDays(1); var through = f.Through ?? today;
        if (from > through || through.DayNumber - from.DayNumber > 3660 || f.Top is < 1 or > 200 || f.Page < 1
            || f.FrequentOrders < 2 || f.FragmentDays is < 1 or > 365 || f.SmallOrderAmount < 0
            || f.MinOrders < 0 || f.MinQuantity < 0 || f.MinAmount < 0 || f.MaxOrders < 0 || f.MaxQuantity < 0 || f.MaxAmount < 0
            || f.MaxOrders < f.MinOrders || f.MaxQuantity < f.MinQuantity || f.MaxAmount < f.MinAmount)
            throw new PdmRuleException("请核对日期范围、上下限和分析阈值；采购次数至少为2次，排名范围为1至200项。");
        if (!canCost && (f.MinAmount.HasValue || f.MaxAmount.HasValue || f.MinPriceChange.HasValue))
            throw new UnauthorizedAccessException("当前账号未获采购金额权限，不能使用金额或价格条件。");
        bool Selected(string[] selection, string value) => selection.Length == 0 || selection.Contains(value, StringComparer.OrdinalIgnoreCase);
        PurchaseAnalysisOption[] Options(Func<PurchaseAnalysisLine, string> key, Func<PurchaseAnalysisLine, string> name) => authorized
            .GroupBy(key).Select(g => new PurchaseAnalysisOption(g.Key, name(g.First()))).OrderBy(o => o.Label, StringComparer.Ordinal).ToArray();
        var options = new Dictionary<string, PurchaseAnalysisOption[]> {
            ["organizations"] = Options(r => r.Organization, r => r.Organization), ["types"] = Options(r => r.TypeCode, r => r.TypeName),
            ["categories"] = Options(r => r.CategoryCode, r => r.CategoryName), ["suppliers"] = Options(r => r.SupplierCode, r => r.SupplierName),
            ["buyers"] = Options(r => r.BuyerCode, r => r.BuyerName), ["projects"] = authorized.SelectMany(r => r.ProjectCode.Length == 0 && r.SubprojectCode.Length == 0
                ? new[] { "" } : new[] { r.ProjectCode, r.SubprojectCode }.Where(s => s.Length > 0)).Distinct()
                .Select(s => new PurchaseAnalysisOption(s, s.Length == 0 ? "未关联项目" : s)).OrderBy(o => o.Label).ToArray(),
            ["currencies"] = Options(r => r.Currency, r => r.Currency.Length == 0 ? "币种待核对" : r.Currency),
            ["units"] = Options(r => r.UnitCode, r => r.UnitName) };
        var rows = authorized.Where(r => r.Date >= from && r.Date <= through && Selected(f.Organizations, r.Organization)
            && Selected(f.Types, r.TypeCode) && Selected(f.Categories, r.CategoryCode) && Selected(f.Suppliers, r.SupplierCode)
            && Selected(f.Buyers, r.BuyerCode) && (f.Projects.Length == 0 || f.Projects.Any(p => p.Length == 0
                ? r.ProjectCode.Length == 0 && r.SubprojectCode.Length == 0 : p == r.ProjectCode || p == r.SubprojectCode))
            && (f.Currency.Length == 0 || r.Currency == f.Currency) && (f.Unit.Length == 0 || r.UnitCode == f.Unit)
            && (string.IsNullOrWhiteSpace(f.Query) || new[] { r.MaterialCode, r.MaterialName, r.Specification, r.Brand, r.OrderNumber }.Any(s => s.Contains(f.Query.Trim(), StringComparison.OrdinalIgnoreCase))))
            .GroupBy(r => (r.Organization, r.Id)).Select(g => g.Last()).ToArray();
        var months = Math.Max(1m, (through.DayNumber - from.DayNumber + 1) / 30.4375m);
        PurchaseAnalysisMetric Metric(IGrouping<string, PurchaseAnalysisLine> group, string dimension)
        {
            var items = group.OrderBy(r => r.Date).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray(); var first = items[0];
            var material = dimension == "material"; var qty = material ? items.Sum(r => r.Quantity) : 0;
            var orders = items.GroupBy(OrderKey).Select(g => new { Date = g.Min(r => r.Date), Amount = Sum(g) }).OrderBy(o => o.Date).ToArray();
            // With dates sorted, an order has a nearby companion exactly when either adjacent order is within the window.
            var fragment = orders.Where((o, i) => i > 0 && o.Date.DayNumber - orders[i - 1].Date.DayNumber <= f.FragmentDays
                || i + 1 < orders.Length && orders[i + 1].Date.DayNumber - o.Date.DayNumber <= f.FragmentDays).Count();
            var amount = Sum(items); var validPrices = items.Where(r => r.UnitPrice.HasValue).Select(r => r.UnitPrice!.Value).ToArray();
            // Do not report a partial average as a complete price. Daily weighted prices avoid arbitrary same-day row ordering.
            decimal? DailyPrice(DateOnly date) { var daily = items.Where(r => r.Date == date).ToArray(); var q = daily.Sum(r => r.Quantity); return q > 0 ? Sum(daily) / q : null; }
            var lastPrice = material ? DailyPrice(items[^1].Date) : null; var firstPrice = material ? DailyPrice(first.Date) : null;
            var change = firstPrice is > 0 && first.Date != items[^1].Date ? (lastPrice / firstPrice - 1) * 100 : null;
            var supplierCount = items.Select(r => r.SupplierCode).Where(s => s.Length > 0).Distinct().Count();
            var projectCount = items.Select(r => r.SubprojectCode.Length > 0 ? r.SubprojectCode : r.ProjectCode).Where(s => s.Length > 0).Distinct().Count();
            var reason = $"范围内采购{orders.Length}次，涉及{projectCount}个项目、{supplierCount}家供应商；{f.FragmentDays}天内相邻下单{fragment}次。";
            var code = dimension == "supplier" ? first.SupplierCode : dimension == "category" ? first.CategoryCode : first.MaterialCode;
            var name = dimension == "supplier" ? first.SupplierName : dimension == "category" ? first.CategoryName : first.MaterialName;
            return new(group.Key, code, name, material ? first.UnitName : "—", first.Currency, orders.Length, qty, amount,
                qty > 0 ? amount / qty : null, lastPrice, material && validPrices.Length == items.Length ? validPrices.Min() : null,
                material && validPrices.Length == items.Length ? validPrices.Max() : null, change, supplierCount, projectCount,
                orders.Length / months, orders.Length > 1 ? (decimal)(orders[^1].Date.DayNumber - orders[0].Date.DayNumber) / (orders.Length - 1) : null,
                material && orders.Length > 0 ? qty / orders.Length : 0, fragment,
                canCost && orders.All(o => o.Amount.HasValue) ? orders.Count(o => o.Amount <= f.SmallOrderAmount) : null, reason);
        }
        var metrics = rows.GroupBy(MaterialKey).Select(g => Metric(g, "material")).Where(m =>
            (!f.MinOrders.HasValue || m.Orders >= f.MinOrders) && (!f.MaxOrders.HasValue || m.Orders <= f.MaxOrders)
            && (!f.MinQuantity.HasValue || m.Quantity >= f.MinQuantity) && (!f.MaxQuantity.HasValue || m.Quantity <= f.MaxQuantity)
            && (!f.MinAmount.HasValue || m.Amount >= f.MinAmount) && (!f.MaxAmount.HasValue || m.Amount <= f.MaxAmount)
            && (!f.MinPriceChange.HasValue || m.PriceChange >= f.MinPriceChange)).ToArray();
        var keys = metrics.Select(m => m.Key).ToHashSet(); rows = rows.Where(r => keys.Contains(MaterialKey(r))).ToArray();
        PurchaseAnalysisMetric[] Spend(string dimension) => rows.GroupBy(r => GroupKey(r, dimension)).Select(g => Metric(g, dimension))
            .Where(m => m.Currency.Length > 0).GroupBy(m => m.Currency).SelectMany(currency => {
                decimal? total = currency.All(m => m.Amount.HasValue) ? currency.Sum(m => m.Amount!.Value) : null; decimal cumulative = 0;
                return currency.OrderByDescending(m => m.Amount).ThenBy(m => m.Code).Select(m => {
                    decimal? share = total is > 0 ? m.Amount / total * 100 : null; var before = cumulative; cumulative += share ?? 0;
                    return m with { Share = share, CumulativeShare = share.HasValue ? cumulative : null, Abc = share.HasValue ? before < 80 ? "A" : before < 95 ? "B" : "C" : null };
                }).Take(f.Top);
            }).ToArray();
        var detailRows = rows.Where(r => f.DetailKey.Length == 0 || MaterialKey(r) == f.DetailKey || GroupKey(r, "supplier") == f.DetailKey || GroupKey(r, "category") == f.DetailKey)
            .OrderByDescending(r => r.Date).ThenBy(r => r.OrderNumber).ThenBy(r => r.LineNumber).ToArray();
        var trend = detailRows.GroupBy(r => r.Date.ToString("yyyy-MM")).OrderBy(g => g.Key).Select(g => {
            var sameUnit = g.Select(r => (r.MaterialCode, r.UnitCode)).Distinct().Count() == 1; var q = sameUnit ? g.Sum(r => r.Quantity) : 0;
            var a = g.Select(r => r.Currency).Distinct().Count() == 1 ? Sum(g) : null;
            return new PurchaseAnalysisPeriod(g.Key, g.Select(OrderKey).Distinct().Count(), q, a, q > 0 ? a / q : null);
        }).ToArray();
        var notices = new List<string> { "金额为已核准采购订单未税金额，非结算支出；取消单排除，退货不混入订单金额。",
            "同一物料同一订单只计一次采购；数量及价格按物料、交易单位、币种分组，未做未经确认的单位或汇率换算。",
            "分析结果为降本机会，需结合交期、质量、批量及库存成本评估；价格差额不代表已实现节省。" };
        if (status.LastSuccess is null) notices.Add("尚无完整采集数据，请由获授权人员配置组织并执行同步。");
        if (status.From > from || status.Through < through) notices.Add("所选日期超出已采集覆盖范围，当前结果不完整，请调整日期或扩大采集范围。");
        if (rows.Any(r => r.Currency.Length == 0 || r.UnitCode.Length == 0)) notices.Add("部分记录币种或单位待核对，未纳入相应金额或数量比较。");
        return new(status, from, through, options, metrics.Where(m => m.Orders >= f.FrequentOrders).OrderByDescending(m => m.Orders).Take(f.Top).ToArray(),
            // Separate units in the volume view; user can narrow to one material/category for meaningful comparisons.
            metrics.GroupBy(m => m.Unit).OrderBy(g => g.Key).SelectMany(g => g.OrderByDescending(m => m.Quantity).Take(f.Top)).ToArray(),
            canCost ? Spend(f.Dimension is "supplier" or "category" ? f.Dimension : "material") : [],
            canCost ? metrics.Where(m => m.AveragePrice.HasValue).OrderByDescending(m => m.PriceChange ?? 0).Take(f.Top).ToArray() : [],
            metrics.Where(m => m.FragmentOrders > 0).OrderByDescending(m => m.FragmentOrders).Take(f.Top).ToArray(),
            canCost ? Spend("supplier") : [], trend, export ? detailRows : detailRows.Skip((f.Page - 1) * 50).Take(50).ToArray(), detailRows.Length,
            rows.Select(OrderKey).Distinct().Count(), rows.Select(r => r.MaterialCode).Distinct().Count(), rows.Select(r => r.SupplierCode).Where(s => s.Length > 0).Distinct().Count(),
            metrics.Count(m => m.Orders >= f.FrequentOrders), canCost ? rows.GroupBy(r => r.Currency).ToDictionary(g => g.Key, Sum) : new Dictionary<string, decimal?>(),
            canCost ? rows.Count(r => !r.NetAmount.HasValue) : 0, canCost, canExport, canSync, canConfigure, notices)
        {
            PriceComparisons = canCost && detailRows.Select(MaterialKey).Distinct().Count() == 1 ? detailRows
                .GroupBy(r => r.SupplierCode).Select(g => Metric(g, "material") with { Code = g.First().SupplierCode, Name = g.First().SupplierName })
                .OrderBy(m => m.AveragePrice).ToArray() : []
        };
    }
}
