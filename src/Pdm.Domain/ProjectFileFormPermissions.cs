namespace Upton.Pdm.Domain;

public static class ProjectFileFormPermissions
{
    public const string Sample = "sample-demand";
    public const string Change = "customer-change";
    public const string PreAcceptance = "pre-acceptance";
    public const string FinalAcceptance = "final-acceptance";
    public const string FolderKey = "project-files";
    public static IReadOnlyList<string> Kinds { get; } = [Sample, Change, PreAcceptance, FinalAcceptance];
    public static bool IsAcceptance(string kind) => kind is PreAcceptance or FinalAcceptance;

    public static string Title(string kind) => kind switch
    {
        Sample => "样件需求计划",
        Change => "客户需求变更单",
        PreAcceptance => "预验收单",
        FinalAcceptance => "终验收单",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Code(string kind, string operation) => $"project.file.{kind}.{operation}";
    public static bool RequiresExplicitGrant(string code) =>
        Kinds.Any(kind => code.StartsWith($"project.file.{kind}.", StringComparison.Ordinal));

    public static IEnumerable<PermissionDefinition> Definitions()
    {
        foreach (var kind in Kinds)
        {
            var title = Title(kind);
            var path = $"/api/projects/{{projectId}}/files/{kind}";
            var view = Code(kind, "view");
            var edit = Code(kind, "edit");
            yield return new(view, $"查看{title}", "项目文件", "按实际项目和项目文件目录查看单据。", Sensitive: true)
            {
                Scope = "project", Action = "view", ProjectAuthorization = "position",
                Dependencies = IsAcceptance(kind)
                    ? ["project.file.view", ProjectCenterPermissions.Tab("files"), ProjectCenterPermissions.Tab("validation-plan")]
                    : ["project.file.view", ProjectCenterPermissions.Tab("files")],
                Routes = IsAcceptance(kind)
                    ? [new("GET", path), new("GET", path + "/{formId}"), new("GET", path + "/{formId}/excel")]
                    : [new("GET", path), new("GET", path + "/{formId}")]
            };
            yield return new(edit, $"编制{title}", "项目文件", "按授权项目岗位维护单据草稿。", Sensitive: true)
            {
                Scope = "project", Action = "edit", ProjectAuthorization = "position",
                Dependencies = [view, ProjectCenterPermissions.Write],
                Routes = [new("PUT", path + "/{formId}")]
            };
            yield return new(Code(kind, "archive"), $"生成并存档{title}", "项目文件", "生成 Excel 并按目标目录上传权限追加文件版本。", Sensitive: true)
            {
                Scope = "project", Action = "export", ProjectAuthorization = "position",
                Dependencies = [edit, ProjectCenterPermissions.Export, "project.file.upload"],
                Routes = [new("POST", path + "/{formId}/archive")]
            };
        }
    }
}
