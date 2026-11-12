using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class QualityReviewService
{
    private async Task<QualityImprovementEvent> EventAsync(string action, string actor, string note, CancellationToken ct) =>
        new(action, actor, Name((await repository.FindUserAsync(actor, ct))?.DisplayName), clock.GetUtcNow(), note);
    public async Task<QualityReviewCatalog> SaveImprovementAsync(Guid id, SaveQualityImprovement input, string actor, UserRole role, CancellationToken ct)
    {
        Id(id);
        var company = await CompanyAsync(actor, ct);
        var catalog = await LoadAsync(company, input.ExpectedRowVersion, ct);
        var previous = catalog.Improvements.FirstOrDefault(i => i.Id == id);
        var drafting = previous is null || previous.State == "草稿";
        await RequireCompanyAsync(actor, drafting ? QualityReviewPermissions.Improve : QualityReviewPermissions.ImprovementExecute, ct);
        if (!catalog.Themes.Any(t => t.Id == input.ThemeId && t.IsActive)) throw new PdmRuleException("请选择已启用的问题主题。");
        var active = catalog.Improvements.FirstOrDefault(i => i.Id != id && i.ThemeId == input.ThemeId && i.State is not ("已关闭" or "已撤销"));
        if (active is not null) throw new PdmRuleException($"该主题已有进行中的改善“{active.Title}”，请继续维护该记录。");
        if (input.MinimumSamples is < 1 or > 10000 || input.ObservationDays is < 1 or > 3650 || input.MaximumFailures < 0 || input.MaximumFailures >= input.MinimumSamples)
            throw new PdmRuleException("最少样本须为1至10000个项目，观察期为1至3650天，允许失败数须小于最少样本数。");
        var owner = await PersonAsync(input.Owner, company, ct);
        var actions = input.Actions?.ToArray() ?? [];
        if (actions.Length > 100 || actions.Select(a => a.Id).Distinct().Count() != actions.Length) throw new PdmRuleException("措施最多100条且标识不能重复。");
        var cleaned = new List<QualityAction>();
        foreach (var action in actions)
        {
            Id(action.Id);
            var actionOwner = await PersonAsync(action.Owner, company, ct);
            var old = previous?.Actions.FirstOrDefault(a => a.Id == action.Id);
            var completionChanged = old?.Completed != action.Completed || (old?.Evidence ?? "") != (action.Evidence?.Trim() ?? "");
            if (!drafting && (old is null || old.Title != action.Title.Trim() || !Same(old.Owner, actionOwner) || old.DueOn != action.DueOn))
                throw new PdmRuleException("已批准措施的内容、负责人和日期不能直接修改，请重开改善后重新审批。");
            if (drafting && action.Completed) throw new PdmRuleException("草稿措施不能直接标为完成，请批准后登记执行证据。");
            if (!drafting && previous!.State != "实施中") throw new PdmRuleException("只能在实施中登记措施结果；观察或验证阶段需要先退回实施。");
            cleaned.Add(new(action.Id, Text(action.Title, 500, "措施内容", true), actionOwner, action.DueOn, action.Completed,
                Text(action.Evidence, 4000, "措施证据", action.Completed), action.Completed ? completionChanged ? actor : old?.CompletedBy : null,
                action.Completed ? completionChanged ? clock.GetUtcNow() : old?.CompletedAt : null));
        }
        var item = new QualityImprovement(id, input.ThemeId, Text(input.Title, 200, "改善标题", true), previous?.State ?? "草稿", owner,
            Text(input.RootCause, 4000, "根因分析"), Text(input.Goal, 4000, "改善目标", true), input.DueOn, input.MinimumSamples,
            input.ObservationDays, input.MaximumFailures, cleaned, previous?.Observations ?? [], previous?.ObservationStartedAt,
            previous?.CreatedBy ?? actor, previous?.CreatedAt ?? clock.GetUtcNow(), previous?.History ?? [])
        { PreviousCycles = previous?.PreviousCycles ?? [] };
        if (!drafting && (previous!.ThemeId != item.ThemeId || previous.Title != item.Title || previous.Owner != item.Owner
            || previous.RootCause != item.RootCause || previous.Goal != item.Goal || previous.DueOn != item.DueOn
            || previous.MinimumSamples != item.MinimumSamples || previous.ObservationDays != item.ObservationDays
            || previous.MaximumFailures != item.MaximumFailures || previous.Actions.Count != cleaned.Count))
            throw new PdmRuleException("批准后的改善范围和效果标准不能修改，请重开为草稿并重新审批。");
        item = item with { History = item.History.Append(await EventAsync(drafting ? "保存计划" : "登记措施", actor, "", ct)).ToArray() };
        var saved = await SaveAsync(catalog with { Improvements = catalog.Improvements.Where(i => i.Id != id).Append(item).ToArray() }, actor, "quality.improvement.save", id, ct);
        await NotifyPendingAsync(company, ct);
        return saved;
    }
    public async Task<QualityReviewCatalog> TransitionImprovementAsync(Guid id, QualityTransition input, string actor, UserRole role, CancellationToken ct)
    {
        var permission = input.Action switch
        {
            "approve" => QualityReviewPermissions.ImprovementApprove,
            "observe" or "submit-verification" or "return-to-implementation" => QualityReviewPermissions.ImprovementExecute,
            "verify" => QualityReviewPermissions.Verify,
            "close" => QualityReviewPermissions.ImprovementClose,
            _ => QualityReviewPermissions.Improve
        };
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, permission, ct), input.ExpectedRowVersion, ct);
        var item = catalog.Improvements.FirstOrDefault(i => i.Id == id) ?? throw new PdmNotFoundException("改善记录不存在。");
        var note = Text(input.Note, 4000, "操作说明", input.Action is "verify" or "close" or "reopen" or "cancel" or "return-to-implementation");
        switch (input.Action)
        {
            case "approve" when item.State == "草稿":
                if (string.IsNullOrWhiteSpace(item.RootCause) || item.Actions.Count == 0) throw new PdmRuleException("请完成根因分析和实施措施，再批准改善。");
                item = item with { State = "实施中" }; break;
            case "observe" when item.State == "实施中":
                if (item.Actions.Any(a => !a.Completed || string.IsNullOrWhiteSpace(a.Evidence))) throw new PdmRuleException("请先完成全部措施并提供证据，再开始观察。");
                item = item with { State = "观察中", ObservationStartedAt = clock.GetUtcNow() }; break;
            case "submit-verification" when item.State == "观察中":
                QualityReviewRules.RequireEffective(item, Today);
                item = item with { State = "待验证" }; break;
            case "verify" when item.State == "待验证":
                QualityReviewRules.RequireIndependentVerifier(item, actor);
                QualityReviewRules.RequireEffective(item, Today);
                item = item with { State = "已验证", VerifiedBy = actor }; break;
            case "close" when item.State == "已验证":
                QualityReviewRules.RequireEffective(item, Today);
                item = item with { State = "已关闭", ClosedAt = clock.GetUtcNow() }; break;
            case "reopen" when item.State is not ("草稿" or "已撤销"):
                if (catalog.Improvements.Any(i => i.Id != id && i.ThemeId == item.ThemeId && i.State is not ("已关闭" or "已撤销")))
                    throw new PdmRuleException("此主题已有进行中的改善，请关联该改善继续处理，避免重复立项。");
                item = Restart(item, "草稿", note); break;
            case "return-to-implementation" when item.State is "观察中" or "待验证" or "已验证":
                item = Restart(item, "实施中", note); break;
            case "cancel" when item.State is "草稿" or "实施中": item = item with { State = "已撤销" }; break;
            default: throw new PdmRuleException("当前改善状态不能执行此操作，请刷新后重试。");
        }
        var label = input.Action switch { "approve" => "批准实施", "observe" => "开始观察", "submit-verification" => "提交验证",
            "verify" => "独立验证通过", "close" => "验证关闭", "reopen" => "重开并重新审批", "cancel" => "撤销改善", "return-to-implementation" => "退回实施", _ => "状态更新" };
        item = item with { History = item.History.Append(await EventAsync(label, actor, note, ct)).ToArray() };
        var saved = await SaveAsync(catalog with { Improvements = catalog.Improvements.Select(i => i.Id == id ? item : i).ToArray() }, actor, "quality.improvement." + input.Action, id, ct);
        await NotifyPendingAsync(catalog.CompanyId, ct);
        return saved;
    }
    private QualityImprovement Restart(QualityImprovement item, string state, string reason) => item with
    {
        State = state, VerifiedBy = null, ClosedAt = null, ObservationStartedAt = null, Observations = [],
        PreviousCycles = item.PreviousCycles.Append(new(clock.GetUtcNow(), reason, item.State, item.Actions, item.Observations,
            item.ObservationStartedAt, item.VerifiedBy, item.ClosedAt, item.MinimumSamples, item.ObservationDays, item.MaximumFailures)).ToArray(),
        Actions = item.Actions.Select(a => a with { Completed = false, CompletedAt = null, CompletedBy = null, Evidence = "" }).ToArray()
    };
    public async Task<QualityReviewCatalog> SaveObservationAsync(Guid id, SaveQualityObservation input, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.ImprovementExecute, ct), input.ExpectedRowVersion, ct);
        var item = catalog.Improvements.FirstOrDefault(i => i.Id == id) ?? throw new PdmNotFoundException("改善记录不存在。");
        if (item.State != "观察中" || item.ObservationStartedAt is not { } started) throw new PdmRuleException("请先进入观察阶段，再登记效果样本。");
        var project = (await ProjectsAsync(catalog.CompanyId, ct)).FirstOrDefault(p => p.Id == input.ProjectId)
            ?? throw new PdmRuleException("样本项目不存在或不属于当前公司。");
        if (project.Id != Root(project)) throw new PdmRuleException("效果统计以主项目去重，请选择对应主项目。");
        var start = DateOnly.FromDateTime(started.ToOffset(TimeSpan.FromHours(8)).Date);
        if (input.AppliedOn < start || input.ObservedOn < input.AppliedOn || input.ObservedOn > Today)
            throw new PdmRuleException("样本采用日期不能早于本轮观察开始，观察日期不能早于采用日期或晚于今天。");
        if (item.Observations.Any(o => o.ProjectId == input.ProjectId)) throw new PdmRuleException("该主项目已作为本轮样本登记，不能重复计数或覆盖原证据；记录错误请退回实施并说明原因。");
        var observation = new QualityObservation(input.ProjectId, input.AppliedOn, input.ObservedOn, input.Passed,
            Text(input.Evidence, 4000, "采用措施和效果观察证据", true), actor, clock.GetUtcNow());
        item = item with { Observations = item.Observations.Append(observation).ToArray(),
            History = item.History.Append(await EventAsync("登记样本", actor, input.Passed ? "适用项目样本通过" : "适用项目样本失败，需继续分析", ct)).ToArray() };
        return await SaveAsync(catalog with { Improvements = catalog.Improvements.Select(i => i.Id == id ? item : i).ToArray() }, actor, "quality.improvement.observation", id, ct);
    }
}
