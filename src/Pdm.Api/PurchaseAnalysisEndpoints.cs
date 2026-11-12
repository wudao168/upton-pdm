using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class PurchaseAnalysisEndpoints
{
    public static void MapPurchaseAnalysisEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/purchase-analysis").RequireAuthorization();
        api.MapPost("/query", async (PurchaseAnalysisFilter filter, HttpContext context, PurchaseAnalysisService service, PurchaseAnalysisSyncCoordinator sync, CancellationToken ct) =>
            Results.Ok(new { data = await service.QueryAsync(filter, Actor(context), false, ct), running = sync.Running(Company()) }));
        api.MapGet("/settings", async (PurchaseAnalysisService service, CancellationToken ct) => Results.Ok(await service.SettingsAsync(ct)));
        api.MapPut("/settings", async (PurchaseAnalysisSettings input, HttpContext context, PurchaseAnalysisService service, CancellationToken ct) => {
            await service.SaveSettingsAsync(input, Actor(context), ct); return Results.NoContent();
        });
        api.MapPost("/synchronize", async (HttpContext context, PurchaseAnalysisService service, PurchaseAnalysisSyncCoordinator sync, CancellationToken ct) => {
            var settings = await service.SettingsAsync(ct);
            if (settings.Organizations.Length == 0) throw new PdmRuleException("请先配置当前公司的U9C组织编码。");
            return Results.Ok(new { started = sync.TryStart(Company(), Actor(context)) });
        });
        api.MapPost("/export", async (PurchaseAnalysisFilter filter, HttpContext context, PurchaseAnalysisService service, IPdmRepository repository, TimeProvider clock, CancellationToken ct) => {
            var actor = Actor(context);
            var result = await service.QueryAsync(filter, actor, true, ct);
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var company = directory.Organizations.FirstOrDefault(o => o.Id == Company())?.Name ?? "UPTON 阿普顿";
            var name = directory.Users.FirstOrDefault(u => u.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))?.DisplayName;
            var metadata = new ProjectWorkbookMetadata("采购分析明细", company, $"{result.From:yyyy-MM-dd} 至 {result.Through:yyyy-MM-dd}", "版本", "V1",
                clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)), Describe(filter, result), string.IsNullOrWhiteSpace(name) ? "姓名待补充" : name);
            return Results.File(PurchaseAnalysisWorkbook.Write(result, metadata), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "采购分析.xlsx");
        });
    }
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? throw new UnauthorizedAccessException("请重新登录。");
    private static IReadOnlyList<KeyValuePair<string, string>> Describe(PurchaseAnalysisFilter f, PurchaseAnalysisResult result)
    {
        string Values(string key, string[] selected) => selected.Length == 0 ? "全部" : string.Join("、", selected.Select(s => result.Options[key].FirstOrDefault(o => o.Value == s)?.Label ?? s));
        return [new("组织", Values("organizations", f.Organizations)), new("采购类型", Values("types", f.Types)),
            new("物料分类", Values("categories", f.Categories)), new("供应商", Values("suppliers", f.Suppliers)),
            new("采购员", Values("buyers", f.Buyers)), new("项目范围", Values("projects", f.Projects)),
            new("关键词", f.Query.Length == 0 ? "全部" : f.Query), new("单位／币种", (f.Unit.Length == 0 ? "按单位分组" : f.Unit) + "／" + (f.Currency.Length == 0 ? "按币种分列" : f.Currency)),
            new("次数／数量", $"{f.MinOrders}至{f.MaxOrders}次／{f.MinQuantity}至{f.MaxQuantity}，空值不限"),
            new("金额／涨幅", $"{f.MinAmount}至{f.MaxAmount}／≥{f.MinPriceChange}%，空值不限"),
            new("数据更新", result.Status.LastSuccess?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm") ?? "未同步"),
            new("统计口径", "已核准订单／未税／当前筛选及授权范围")];
    }
    private static Guid Company() => TenantContext.CompanyId ?? throw new UnauthorizedAccessException("请先选择公司。");
}
