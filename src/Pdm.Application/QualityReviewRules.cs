namespace Upton.Pdm.Application;

public static class QualityReviewRules
{
    public static void RequireThemeUnreferenced(QualityReviewCatalog catalog, Guid themeId)
    {
        // Check the full company catalog, including closed records and every frozen revision.
        // A filtered workspace or the current frequency window cannot establish that deletion is safe.
        var references = new List<string>();
        var profiles = catalog.Profiles.Count(p => p.ThemeId == themeId);
        if (profiles > 0) references.Add($"{profiles} 条问题闭环资料");
        var summaries = catalog.Summaries.Count(s => s.Issues.Any(i => i.Profile?.ThemeId == themeId));
        if (summaries > 0) references.Add($"{summaries} 份项目总结快照（含历史修订）");
        var referencedImprovements = catalog.Improvements.Where(i => i.ThemeId == themeId).ToArray();
        var improvements = referencedImprovements.Select(i => i.Id).ToHashSet();
        if (improvements.Count > 0) references.Add($"{improvements.Count} 项改善记录（含已关闭或已撤销记录）");
        var experiences = catalog.Experiences.Where(e => improvements.Contains(e.ImprovementId)).Select(e => e.Id).ToHashSet();
        if (experiences.Count > 0) references.Add($"{experiences.Count} 条发布经验");
        var adoptions = catalog.Adoptions.Count(a => experiences.Contains(a.ExperienceId));
        if (adoptions > 0) references.Add($"{adoptions} 项经验采用任务");
        if (references.Count > 0)
        {
            var suggestion = referencedImprovements.Any(i => i.State is not ("已关闭" or "已撤销"))
                ? "请先结束进行中的改善，再按需停用" : "请改用停用";
            throw new PdmRuleException($"此共性问题主题已被{string.Join("、", references)}引用，不能删除。{suggestion}，保留已有业务记录和历史追溯。");
        }
    }

    public static IReadOnlyList<QualityThemeMetric> Metrics(QualityReviewCatalog catalog,
        IReadOnlyList<QualityIssueRow> rows, DateOnly today)
    {
        var start = today.AddDays(1 - catalog.Settings.WindowDays);
        return catalog.Themes.Select(theme =>
        {
            var all = rows.Where(r => r.Profile?.ThemeId == theme.Id && r.Issue.Status != "已作废"
                && r.Profile.DuplicateOfIssueId is null).DistinctBy(r => r.Issue.Id).ToArray();
            var dated = all.Where(r => r.Profile!.OccurredOn is { } day && day >= start && day <= today).ToArray();
            var major = all.Count(r => r.Profile!.Severity is "重大" or "严重");
            var projects = dated.Select(r => r.RootProjectId).Distinct().Count();
            var active = catalog.Improvements.FirstOrDefault(i => i.ThemeId == theme.Id && i.State is not ("已关闭" or "已撤销"));
            var closedAt = catalog.Improvements.Where(i => i.ThemeId == theme.Id && i.State == "已关闭")
                .Select(i => i.ClosedAt).Max();
            bool AfterClosure(QualityIssueRow row) => closedAt is null || row.Issue.CreatedAt > closedAt
                && (row.Profile!.OccurredOn is null || row.Profile.OccurredOn >= DateOnly.FromDateTime(closedAt.Value.ToOffset(TimeSpan.FromHours(8)).Date));
            var pending = dated.Where(AfterClosure).ToArray();
            var pendingMajor = all.Any(r => (r.Profile!.Severity is "重大" or "严重") && AfterClosure(r));
            return new QualityThemeMetric(theme.Id, theme.Name, dated.Length, projects, major,
                theme.IsActive && (pendingMajor || pending.Length >= catalog.Settings.EventThreshold
                    || pending.Select(r => r.RootProjectId).Distinct().Count() >= catalog.Settings.ProjectThreshold
                    || pending.Any(r => r.Profile!.RecurrenceOfIssueId is not null)),
                active?.Id, all.Count(r => r.Profile!.OccurredOn is null));
        }).ToArray();
    }

    public static void RequireEffective(QualityImprovement improvement, DateOnly today)
    {
        if (improvement.Actions.Count == 0 || improvement.Actions.Any(a => !a.Completed || string.IsNullOrWhiteSpace(a.Evidence)))
            throw new PdmRuleException("请先完成全部改善措施并填写实施证据。");
        if (improvement.ObservationStartedAt is not { } started)
            throw new PdmRuleException("请先开始效果观察。");
        var start = DateOnly.FromDateTime(started.ToOffset(TimeSpan.FromHours(8)).Date);
        if (today.DayNumber - start.DayNumber < improvement.ObservationDays)
            throw new PdmRuleException("尚未达到约定的观察天数，请继续观察。");
        var samples = improvement.Observations.Where(o => o.AppliedOn >= start && o.ObservedOn >= o.AppliedOn
            && o.ObservedOn <= today && !string.IsNullOrWhiteSpace(o.Evidence)).DistinctBy(o => o.ProjectId).ToArray();
        if (samples.Length < improvement.MinimumSamples)
            throw new PdmRuleException("适用项目的有效样本不足，不能验证关闭改善。");
        if (samples.Count(o => !o.Passed) > improvement.MaximumFailures)
            throw new PdmRuleException("复发或失败样本超过既定目标，请重新分析并补充改善措施。");
    }

    public static void RequireIndependentVerifier(QualityImprovement item, string actor)
    {
        if (string.Equals(item.Owner, actor, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            || item.Actions.Any(a => string.Equals(a.Owner, actor, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.CompletedBy, actor, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("改善创建人、负责人及措施执行人不能独立验证本改善，请由另一名已授权人员验证。");
    }
}
