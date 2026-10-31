namespace Upton.Pdm.Domain;

public static class ProjectCenterPermissions
{
    public const string SchemaMarker = "system.project-center-permissions.v1";
    public const string Write = "project.content.write";
    public const string Download = "project.content.download";
    public const string Export = "project.content.export";
    public const string Print = "project.content.print";
    public static readonly (string Key, string Name)[] Tabs =
    [
        ("overview", "概览"), ("information", "信息"), ("files", "文件"), ("project-plan", "计划"),
        ("opl", "OPL"), ("budget", "预算"), ("validation-plan", "质量"), ("documents", "图档"),
        ("bom", "BOM"), ("release", "发布"), ("procurement", "备料"), ("records", "记录")
    ];
    public static readonly string[] AssemblyRoles = ["AssemblySupervisor", "AssemblyFitter", "ElectricalSupervisor", "AssemblyElectrician"];
    public static string Tab(string key) => $"project.tab.{key}.view";

    public static IReadOnlySet<string> AssemblyReadOnly() => new[]
    {
        SchemaMarker, FunctionPermissionCatalog.SchemaMarker, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
        "project.plan.view", "project.opl.view", "document.view", "release.view", "bom.mechanical.row.view", "bom.electrical.row.view"
    }.Concat(Tabs.Where(tab => tab.Key is not ("information" or "files" or "budget" or "records")).Select(tab => Tab(tab.Key)))
        .ToHashSet(StringComparer.Ordinal);

    // 旧角色首次升级保留原有入口与操作；保存新配置后按逐项授权执行。
    public static void ExpandLegacy(HashSet<string> values)
    {
        if (values.Contains(SchemaMarker)) return;
        if (values.Contains(PermissionCodes.ProjectView)) values.UnionWith([Tab("overview"), Tab("records")]);
        if (values.Contains(PermissionCodes.ProjectContentView))
        {
            values.UnionWith(Tabs.Select(tab => Tab(tab.Key)));
            values.UnionWith([Write, Download, Export, Print]);
        }
    }
}
