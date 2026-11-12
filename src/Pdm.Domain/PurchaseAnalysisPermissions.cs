namespace Upton.Pdm.Domain;

public static class PurchaseAnalysisPermissions
{
    public const string View = "purchase-analysis.view";
    public const string Cost = "purchase-analysis.cost.view";
    public const string Export = "purchase-analysis.export";
    public const string Synchronize = "purchase-analysis.synchronize";
    public const string Settings = "purchase-analysis.settings.manage";

    internal static IEnumerable<PermissionDefinition> Definitions()
    {
        yield return Define(View, "查看采购分析", "project", "view", false, [], new FunctionRoute("POST", "/api/purchase-analysis/query"));
        yield return Define(Cost, "查看采购价格与金额", "project", "view", true, [View]);
        yield return Define(Export, "导出采购分析", "project", "export", true, [View], new FunctionRoute("POST", "/api/purchase-analysis/export"));
        yield return Define(Synchronize, "同步采购分析数据", "company", "edit", true, [View], new FunctionRoute("POST", "/api/purchase-analysis/synchronize"));
        yield return Define(Settings, "配置采购分析采集范围", "company", "edit", true, [View], new("GET", "/api/purchase-analysis/settings"), new("PUT", "/api/purchase-analysis/settings"));
    }

    private static PermissionDefinition Define(string code, string name, string scope, string action, bool sensitive,
        IReadOnlyList<string> dependencies, params FunctionRoute[] routes) =>
        new(code, name, "采购分析", "按授权公司和实际项目范围校验；无项目采购须取得公司范围授权。", sensitive)
        { Scope = scope, Action = action, Dependencies = dependencies, Routes = routes };
}
