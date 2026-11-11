using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectBonusRules
{
    public static string Text(string? value, string label, int maximum = 2000, bool required = true)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > maximum || required && text.Length == 0) throw new PdmRuleException($"{label}不能为空，且最多{maximum}字。");
        return text;
    }
    public static void ValidateSettings(ProjectBonusSettings settings)
    {
        if (settings.Departments is null || settings.Departments.Count is < 1 or > 30 || settings.Roles is null || settings.Roles.Count is < 1 or > 60
            || settings.ProjectIndicators is null || settings.ApprovalNodes is null || settings.ApprovalNodes.Count is < 1 or > 12)
            throw new PdmRuleException("请配置评分部门、岗位指标、项目指标与审批节点。");
        Unique(settings.Departments.Select(d => d.Key)); Unique(settings.Roles.Select(r => r.Id)); Unique(settings.ApprovalNodes.Select(n => n.Id));
        foreach (var d in settings.Departments)
        {
            Text(d.Key, "部门标识", 80); Text(d.Name, "部门名称", 80);
            if (settings.Enabled && (d.UnitId is null || d.UnitId == Guid.Empty || d.Reviewers is null || d.Reviewers.Count == 0))
                throw new PdmRuleException($"启用前请为“{d.Name}”绑定实际部门及评分/审批人员。");
        }
        var departments = settings.Departments.Select(d => d.Key).ToHashSet();
        void Indicators(IReadOnlyList<BonusIndicator> indicators)
        {
            if (indicators is null || indicators.Count is < 1 or > 30 || indicators.Sum(i => i.Weight) != 100)
                throw new PdmRuleException("每套指标须有1至30项，指标权重合计须为100%。");
            Unique(indicators.Select(i => i.Id));
            foreach (var i in indicators)
            {
                Text(i.Id, "指标标识", 100); Text(i.Name, "指标名称", 120); Text(i.Guidance, "评分标准"); Text(i.Evidence, "证据要求");
                if (i.MaxScore <= 0 || i.MaxScore > 1000 || i.Weight <= 0 || i.Weight > 100 || i.Departments is null || i.Departments.Count is < 1 or > 12
                    || i.Departments.Sum(d => d.Weight) != 100 || i.Departments.Any(d => !departments.Contains(d.DepartmentKey) || d.Weight <= 0))
                    throw new PdmRuleException($"“{i.Name}”的分值或评分部门权重不正确，各部门权重合计须为100%。");
                Unique(i.Departments.Select(d => d.DepartmentKey));
            }
        }
        Indicators(settings.ProjectIndicators);
        foreach (var role in settings.Roles)
        {
            Text(role.Id, "岗位标识", 100); Text(role.Name, "岗位名称", 80); Phase(role.Phase);
            if (role.SuggestedWeight <= 0 || role.SuggestedWeight > 1000) throw new PdmRuleException("岗位建议分配权重须大于0且不超过1000。");
            Indicators(role.Indicators);
        }
        Unique(settings.ProjectIndicators.Concat(settings.Roles.SelectMany(r => r.Indicators)).Select(i => i.Id));
        foreach (var node in settings.ApprovalNodes)
        {
            Text(node.Name, "审批节点名称", 80);
            if (node.DepartmentKeys is null || node.DepartmentKeys.Count is < 1 or > 12 || !node.Parallel && node.DepartmentKeys.Count != 1
                || node.DepartmentKeys.Any(d => !departments.Contains(d))) throw new PdmRuleException("审批节点的部门配置不正确。");
            Unique(node.DepartmentKeys);
        }
        if (!departments.Contains(settings.PaymentDepartmentKey)) throw new PdmRuleException("请选择发放登记部门。");
    }
    public static void Phase(string phase) { if (phase is not ("Shipment" or "Acceptance")) throw new PdmRuleException("奖金申请仅支持发货和终验收两个阶段。"); }
    public static void Unique(IEnumerable<string> ids)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new PdmRuleException("配置标识或人员岗位存在重复，请检查后保存。");
    }
    public static BonusIndicator Indicator(BonusApplication app, string personId, string indicatorId)
    {
        var indicators = personId == "" ? app.Template!.ProjectIndicators
            : app.Template!.Roles.Single(r => r.Id == app.People.Single(p => p.Id == personId).RoleId).Indicators;
        return indicators.SingleOrDefault(i => i.Id == indicatorId) ?? throw new PdmRuleException("该评分指标不属于当前申请。");
    }
    public static (decimal ProjectScore, IReadOnlyList<BonusAward> Awards) Calculate(decimal pool, BonusApplication app)
    {
        decimal Evaluate(string person, IReadOnlyList<BonusIndicator> indicators) => indicators.Sum(indicator => indicator.Weight * indicator.Departments.Sum(dept => {
            var score = app.Scores.SingleOrDefault(s => s.PersonId == person && s.IndicatorId == indicator.Id && s.DepartmentKey == dept.DepartmentKey);
            if (score?.Score is null || score.Score < 0 || score.Score > indicator.MaxScore || score.At is null)
                throw new PdmRuleException("各评分部门尚未完成全部指标，暂不能提交审批。");
            return score.Score.Value / indicator.MaxScore * dept.Weight / 100;
        }));
        var project = Evaluate("", app.Template!.ProjectIndicators);
        var totalWeight = app.People.Sum(p => p.Weight);
        if (totalWeight <= 0) throw new PdmRuleException("人员分配权重合计须大于0。");
        var awards = app.People.Select(person => {
            var score = Evaluate(person.Id, app.Template.Roles.Single(r => r.Id == person.RoleId).Indicators);
            // Round down to cents per role: retained cents are visible and never reallocated to another person.
            var amount = decimal.Floor(pool * .5m * project / 100 * person.Weight / totalWeight * score / 100 * 100) / 100;
            return new BonusAward(person.Id, person.Username, person.Weight / totalWeight, score, amount);
        }).ToArray();
        return (project, awards);
    }
}
