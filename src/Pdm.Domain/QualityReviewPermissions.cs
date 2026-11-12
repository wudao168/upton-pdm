namespace Upton.Pdm.Domain;

/// <summary>质量闭环、项目总结与公司改善使用同一目录；新增条目不继承历史角色授权。</summary>
public static class QualityReviewPermissions
{
    public const string IssueView = "project.quality.view";
    public const string IssueManage = "project.quality.manage";
    public const string IssueVerify = "project.quality.verify";
    public const string IssueClose = "project.quality.close";
    public const string IssueReopen = "project.quality.reopen";
    public const string IssueVoid = "project.quality.void";
    public const string SummaryView = "project.summary.view";
    public const string SummaryManage = "project.summary.manage";
    public const string SummaryReview = "quality.summary.review";
    public const string SummaryPublish = "project.summary.publish";
    public const string AdoptionConfirm = "project.summary.adoption.confirm";
    public const string View = "quality.review.view";
    public const string Detail = "quality.review.detail";
    public const string Manage = "quality.review.manage";
    public const string Improve = "quality.improvement.manage";
    public const string ImprovementApprove = "quality.improvement.approve";
    public const string ImprovementExecute = "quality.improvement.execute";
    public const string Verify = "quality.improvement.verify";
    public const string ImprovementClose = "quality.improvement.close";
    public const string Experience = "quality.experience.manage";
    public const string Settings = "quality.review.settings.manage";

    public static bool RequiresExplicitGrant(string code) => code.StartsWith("project.quality.", StringComparison.Ordinal)
        || code.StartsWith("project.summary.", StringComparison.Ordinal) || code == SummaryReview
        || code.StartsWith("quality.review.", StringComparison.Ordinal)
        || code.StartsWith("quality.improvement.", StringComparison.Ordinal)
        || code.StartsWith("quality.experience.", StringComparison.Ordinal);

    public static IEnumerable<PermissionDefinition> Definitions()
    {
        yield return Project(IssueView, "查看质量问题闭环", "项目质量闭环", "view", ["project.opl.view", ProjectCenterPermissions.Tab("opl")]);
        yield return Project(IssueManage, "维护质量原因与改善措施", "项目质量闭环", "edit", [IssueView, ProjectCenterPermissions.Write]);
        yield return Project(IssueVerify, "验证质量问题改善结果", "项目质量闭环", "edit", [IssueView, ProjectCenterPermissions.Write]);
        yield return Project(IssueClose, "关闭质量问题", "项目质量闭环", "edit", [IssueView, "project.opl.complete", ProjectCenterPermissions.Write]);
        yield return Project(IssueReopen, "重开质量问题", "项目质量闭环", "edit", [IssueView, "project.opl.edit", ProjectCenterPermissions.Write]);
        yield return Project(IssueVoid, "作废质量问题", "项目质量闭环", "delete", [IssueView, "project.opl.edit", ProjectCenterPermissions.Write]);
        yield return Project(SummaryView, "查看项目总结", "项目总结", "view", [PermissionCodes.ProjectContentView]);
        yield return Project(SummaryManage, "编制与提交项目总结", "项目总结", "edit", [SummaryView, ProjectCenterPermissions.Write]);
        yield return Project(SummaryReview, "复核项目总结", "项目总结", "edit", [SummaryView, ProjectCenterPermissions.Write]);
        yield return Project(SummaryPublish, "发布项目总结", "项目总结", "edit", [SummaryView, ProjectCenterPermissions.Write]);
        yield return Project(AdoptionConfirm, "确认项目经验采纳", "项目总结", "edit", [SummaryView, ProjectCenterPermissions.Write]);
        yield return Company(View, "查看公司问题汇总", "view", []);
        yield return Company(Detail, "查看公司问题明细", "view", [View]);
        yield return Company(Manage, "维护公司问题主题", "edit", [View]);
        yield return Company(Improve, "制定公司改善计划", "edit", [View]);
        yield return Company(ImprovementApprove, "批准公司改善计划", "edit", [View]);
        yield return Company(ImprovementExecute, "执行公司改善措施", "edit", [View]);
        yield return Company(Verify, "验证公司改善成效", "edit", [View]);
        yield return Company(ImprovementClose, "关闭公司改善计划", "edit", [View]);
        yield return Company(Experience, "发布公司经验知识", "edit", [View]);
        yield return Company(Settings, "配置公司质量汇总规则", "edit", [View]);
    }

    private static PermissionDefinition Project(string code, string name, string module, string action, IReadOnlyList<string> dependencies) =>
        new(code, name, module, "按问题或总结所属的实际项目范围校验；需在权限中心明确授权。", Sensitive: true)
        { Scope = "project", Action = action, ProjectAuthorization = "role", Dependencies = dependencies };
    private static PermissionDefinition Company(string code, string name, string action, IReadOnlyList<string> dependencies) =>
        new(code, name, "公司质量改善", "仅限当前授权公司；公司明细不替代项目内容访问授权。", Sensitive: true)
        { Scope = "company", Action = action, ProjectAuthorization = "role", Dependencies = dependencies };
}
