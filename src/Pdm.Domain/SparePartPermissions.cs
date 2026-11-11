namespace Upton.Pdm.Domain;

public static class SparePartPermissions
{
    public const string View = "spare-parts.view";
    public const string Maintain = "spare-parts.maintain";
    public const string ManageCategories = "spare-parts.categories.manage";
    public const string Cost = "spare-parts.cost.view";
    public const string Pricing = "spare-parts.pricing.manage";
    public const string Drawing = "spare-parts.drawing.view";
    public const string Download = "spare-parts.drawing.download";
    public const string Export = "spare-parts.export";

    internal static IEnumerable<PermissionDefinition> Definitions()
    {
        yield return Define(View, "查看备品备件及售价", "view", false, [PermissionCodes.ProjectView], new FunctionRoute("GET", "/api/spare-parts"));
        yield return Define(Maintain, "维护备件项目关联", "edit", false, [View],
            new("POST", "/api/spare-parts/manual"), new("POST", "/api/spare-parts/from-bom"), new("PUT", "/api/spare-parts/{id}/details"));
        yield return Define(ManageCategories, "维护备件分类", "edit", true, [View, PermissionCodes.RoleSettingsEdit],
            new("GET", "/api/spare-parts/categories"), new("PUT", "/api/spare-parts/categories"));
        yield return Define(Cost, "查看备件折算成本", "view", true, [View]);
        yield return Define(Pricing, "管理备件定价与采购价同步", "edit", true, [Cost, PermissionCodes.RoleSettingsEdit],
            new("GET", "/api/spare-parts/pricing"), new("PUT", "/api/spare-parts/pricing"),
            new("PUT", "/api/spare-parts/{id}/price"), new("POST", "/api/spare-parts/synchronize"));
        yield return Define(Drawing, "预览备件图纸", "view", true, [View, "document.view"],
            new FunctionRoute("GET", "/api/spare-parts/{id}/drawings/{index}/preview"));
        yield return Define(Download, "下载备件图纸", "download", true, [Drawing, ProjectCenterPermissions.Download],
            new FunctionRoute("GET", "/api/spare-parts/{id}/drawings/{index}/download"));
        yield return Define(Export, "导出备件及报价清单", "export", true, [View], new FunctionRoute("GET", "/api/spare-parts/export"));
    }

    private static PermissionDefinition Define(string code, string name, string action, bool sensitive,
        IReadOnlyList<string> dependencies, params FunctionRoute[] routes) =>
        new(code, name, "备品备件", "仅适用当前公司及账号获准访问的项目。", sensitive)
        { Scope = "company", Action = action, Dependencies = dependencies, Routes = routes };
}
