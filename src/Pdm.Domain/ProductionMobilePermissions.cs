namespace Upton.Pdm.Domain;

public static class ProductionMobilePermissions
{
    public const string Use = "production.mobile.use";
    public const string Print = "production.labels.print";
    public const string Reprint = "production.labels.reprint";
    public const string Printers = "production.printers.manage";
    public const string Agent = "production.printers.dispatch";
    public const string Obsolete = "document.obsolete";

    public static bool RequiresExplicitGrant(string code) => code == Use || code == Obsolete
        || code.StartsWith("production.labels.", StringComparison.Ordinal)
        || code.StartsWith("production.printers.", StringComparison.Ordinal);

    internal static IEnumerable<PermissionDefinition> Definitions()
    {
        yield return new(Use, "使用生产执行 App", "生产执行", "仅访问当前公司内获准访问的项目；业务操作仍须具备对应工序权限。")
        { Scope = "project", Action = "view", Dependencies = [PermissionCodes.ProjectContentView] };
        yield return new(Print, "打印包装标签", "生产执行", "仅打印授权项目的包装记录，一箱或一袋一张。")
        { Scope = "project", Action = "print", Dependencies = [PermissionCodes.ProjectContentView] };
        yield return new(Reprint, "补打包装标签", "生产执行", "按原箱袋记录补打，不增加生产数量，必须填写原因。", true)
        { Scope = "project", Action = "print", Dependencies = [Print] };
        yield return new(Printers, "配置包装打印机", "生产执行", "维护当前公司的打印工作站及绑定账号。", true)
        { Scope = "company", Action = "edit", Dependencies = [PermissionCodes.RoleSettingsEdit] };
        yield return new(Agent, "发送包装打印任务", "生产执行", "只领取绑定当前账号的工作站任务，并校验项目访问和标签打印权限。", true)
        { Scope = "company", Action = "print", Dependencies = [] };
        yield return new(Obsolete, "作废图档", "项目图档", "作废所选图档，关联发布记录的生产使用同时停止；需要发布管理及文件夹发布权限。", true)
        { Scope = "project", Action = "delete", Dependencies = ["document.view", PermissionCodes.ReleaseManage],
            Routes = [new("POST", "/api/documents/{documentId}/obsolete")] };
    }
}
