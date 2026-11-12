using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class QualityReviewService
{
    public async Task<QualityReviewCatalog> SaveSummaryAsync(Guid projectId, Guid id, SaveProjectRetrospective input,
        string actor, UserRole role, CancellationToken ct)
    {
        Id(id);
        var project = await RequireProjectAsync(projectId, actor, QualityReviewPermissions.SummaryManage, ct);
        var catalog = await LoadAsync(project.OrganizationId!.Value, input.ExpectedRowVersion, ct);
        var previous = catalog.Summaries.FirstOrDefault(s => s.Id == id);
        if (previous is not null && (previous.ProjectId != projectId || previous.State != "草稿"))
            throw new PdmRuleException("只能修改本项目的草稿总结；已发布总结请创建修订。");
        if (previous is not null && previous.Kind != input.Kind) throw new PdmRuleException("已有总结不能变更总结类型，请为另一类型新建记录。");
        if (input.Kind is not ("阶段总结" or "最终总结")) throw new PdmRuleException("请选择阶段总结或最终总结。");
        if (input.CoverageFrom > input.CoverageTo || input.CoverageTo > Today) throw new PdmRuleException("总结覆盖日期无效，结束日期不能早于开始日期或晚于今天。");
        if (previous is null && catalog.Summaries.Any(s => s.ProjectId == projectId && s.Kind == input.Kind && s.State != "已发布"))
            throw new PdmRuleException("该类型已有待完成总结，请继续编辑已有记录。");
        var item = new ProjectRetrospective(id, projectId, previous?.Revision ?? catalog.Summaries.Where(s => s.ProjectId == projectId && s.Kind == input.Kind).Select(s => s.Revision).DefaultIfEmpty().Max() + 1,
            input.Kind, "草稿", Text(input.Title, 200, "总结标题", true), Text(input.Achievements, 12000, "成果与完成情况"),
            Text(input.Lessons, 12000, "经验总结"), Text(input.RemainingPlan, 12000, "遗留项承接计划"), previous?.CreatedBy ?? actor,
            previous?.CreatedAt ?? clock.GetUtcNow(), null, null, [])
        { CoverageFrom = input.CoverageFrom, CoverageTo = input.CoverageTo, TargetResults = Text(input.TargetResults, 12000, "进度、预算与质量目标结果") };
        return await SaveAsync(catalog with { Summaries = catalog.Summaries.Where(s => s.Id != id).Append(item).ToArray() }, actor, "quality.summary.save", id, ct);
    }
    public async Task<QualityReviewCatalog> TransitionSummaryAsync(Guid projectId, Guid id, QualityTransition input,
        string actor, UserRole role, CancellationToken ct)
    {
        var permission = input.Action is "review" or "reject" ? QualityReviewPermissions.SummaryReview
            : input.Action == "publish" ? QualityReviewPermissions.SummaryPublish : QualityReviewPermissions.SummaryManage;
        var project = await RequireProjectAsync(projectId, actor, permission, ct);
        var catalog = await LoadAsync(project.OrganizationId!.Value, input.ExpectedRowVersion, ct);
        var item = catalog.Summaries.SingleOrDefault(s => s.Id == id && s.ProjectId == projectId) ?? throw new PdmNotFoundException("项目总结不存在。");
        switch (input.Action)
        {
            case "submit" when item.State == "草稿":
                var allProjects = await ProjectsAsync(catalog.CompanyId, ct);
                var allRows = await RowsAsync(catalog, allProjects, actor, ct);
                var relevant = allRows.Where(r => projectId == Root(project) ? r.RootProjectId == projectId : (r.Issue.RelatedProjectId ?? r.Issue.ProjectId) == projectId).ToArray();
                if (relevant.Any(r => !r.CanOpenProject)) throw new UnauthorizedAccessException("尚未获得总结所覆盖全部子项目的质量问题查看权限，请联系授权人员配置。");
                if (relevant.Any(r => r.Issue.Status is not ("已完成" or "已作废")) && string.IsNullOrWhiteSpace(item.RemainingPlan))
                    throw new PdmRuleException("项目仍有未结问题，请填写遗留项承接人、期限和跟踪计划。");
                if (item.CoverageFrom is null || item.CoverageTo is null || string.IsNullOrWhiteSpace(item.TargetResults))
                    throw new PdmRuleException("提交前请填写覆盖起止日期，以及进度、预算与质量目标完成结果。");
                item = item with { State = "待复核", Issues = relevant.Select(r => new QualityIssueSnapshot(r.Issue.Id, r.Issue.Number,
                    r.Issue.RelatedProjectId ?? r.Issue.ProjectId, r.ProjectCode, r.Issue.Description, r.Issue.Status, r.OwnerName,
                    r.Issue.Solution, r.Profile, r.Issue.RowVersion)).ToArray() };
                break;
            case "review" when item.State == "待复核": item = item with { State = "已复核", ReviewedBy = actor }; break;
            case "reject" when item.State is "待复核" or "已复核":
                Text(input.Note, 2000, "退回原因", true);
                item = item with { State = "草稿", ReviewedBy = null }; break;
            case "publish" when item.State == "已复核": item = item with { State = "已发布", PublishedAt = clock.GetUtcNow() }; break;
            case "revise" when item.State == "已发布":
                if (catalog.Summaries.Any(s => s.ProjectId == projectId && s.Kind == item.Kind && s.State != "已发布"))
                    throw new PdmRuleException("已有进行中的修订，请先完成已有草稿。");
                var revision = catalog.Summaries.Where(s => s.ProjectId == projectId && s.Kind == item.Kind).Max(s => s.Revision) + 1;
                var revised = item with { Id = Guid.NewGuid(), Revision = revision, State = "草稿", CreatedBy = actor,
                    CreatedAt = clock.GetUtcNow(), ReviewedBy = null, PublishedAt = null, Issues = [] };
                return await SaveAsync(catalog with { Summaries = catalog.Summaries.Append(revised).ToArray() }, actor, "quality.summary.revise", revised.Id, ct);
            default: throw new PdmRuleException("当前总结状态不能执行此操作，请刷新后重试。");
        }
        return await SaveAsync(catalog with { Summaries = catalog.Summaries.Select(s => s.Id == id ? item : s).ToArray() }, actor, "quality.summary." + input.Action, id, ct);
    }
    public async Task<QualityReviewCatalog> PublishExperienceAsync(Guid id, PublishQualityExperience input, string actor, UserRole role, CancellationToken ct)
    {
        Id(id);
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.Experience, ct), input.ExpectedRowVersion, ct);
        var improvement = catalog.Improvements.FirstOrDefault(i => i.Id == input.ImprovementId)
            ?? throw new PdmNotFoundException("改善记录不存在。");
        if (improvement.State != "已关闭" || improvement.VerifiedBy is null) throw new PdmRuleException("请先完成改善的独立验证及关闭，再发布经验。");
        if (catalog.Experiences.Any(e => e.Id == id)) throw new PdmRuleException("已发布经验不可覆盖，请使用新的记录标识发布修订。");
        var tags = (input.ProjectTags ?? []).Select(t => Text(t, 60, "适用标签", true)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length > 20) throw new PdmRuleException("适用标签最多20个。");
        var item = new QualityExperience(id, improvement.Id, Text(input.Title, 200, "经验标题", true), Text(input.Content, 12000, "有效措施及检查要点", true),
            Text(input.Applicability, 4000, "适用范围", true), actor, clock.GetUtcNow(),
            catalog.Experiences.Where(e => e.ImprovementId == improvement.Id).Select(e => e.Revision).DefaultIfEmpty().Max() + 1) { ProjectTags = tags };
        return await SaveAsync(catalog with { Experiences = catalog.Experiences.Append(item).ToArray() }, actor, "quality.experience.publish", id, ct);
    }
    public async Task<QualityReviewCatalog> AssignAdoptionAsync(AssignQualityAdoption input, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await LoadAsync(await RequireCompanyAsync(actor, QualityReviewPermissions.Experience, ct), input.ExpectedRowVersion, ct);
        if (!catalog.Experiences.Any(e => e.Id == input.ExperienceId)) throw new PdmNotFoundException("经验记录不存在。");
        var owner = await PersonAsync(input.Owner, catalog.CompanyId, ct);
        var ids = input.ProjectIds?.Distinct().ToArray() ?? [];
        if (ids.Length is 0 or > 200 || input.DueOn < Today) throw new PdmRuleException("请选择1至200个适用项目，并设置今天或以后的完成日期。");
        var projects = await ProjectsAsync(catalog.CompanyId, ct);
        var next = catalog.Adoptions.ToList();
        foreach (var projectId in ids)
        {
            if (!projects.Any(p => p.Id == projectId)) throw new PdmRuleException("适用项目不存在或不属于当前公司。");
            if (!await ProjectCanAsync(projectId, owner, QualityReviewPermissions.AdoptionConfirm, ct)) throw new PdmRuleException("所选负责人没有目标项目的经验采用确认权限，请调整负责人或授权。");
            if (!next.Any(a => a.ProjectId == projectId && a.ExperienceId == input.ExperienceId))
                next.Add(new(Guid.NewGuid(), input.ExperienceId, projectId, owner, input.DueOn, "待检查", ""));
        }
        var saved = await SaveAsync(catalog with { Adoptions = next }, actor, "quality.experience.assign", input.ExperienceId, ct);
        await NotifyPendingAsync(catalog.CompanyId, ct);
        return saved;
    }
    public async Task<QualityReviewCatalog> ConfirmAdoptionAsync(Guid id, ConfirmQualityAdoption input, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await LoadAsync(await CompanyAsync(actor, ct), input.ExpectedRowVersion, ct);
        var item = catalog.Adoptions.FirstOrDefault(a => a.Id == id) ?? throw new PdmNotFoundException("经验检查任务不存在。");
        await RequireProjectAsync(item.ProjectId, actor, QualityReviewPermissions.AdoptionConfirm, ct);
        if (item.State != "待检查") throw new PdmRuleException("此检查任务已确认；新经验版本应建立新的检查任务。");
        if (input.State is not ("已采用" or "不适用")) throw new PdmRuleException("请选择已采用或不适用。");
        item = item with { State = input.State, Evidence = Text(input.Evidence, 4000, input.State == "不适用" ? "不适用原因" : "采用证据", true), ConfirmedBy = actor, ConfirmedAt = clock.GetUtcNow() };
        var saved = await SaveAsync(catalog with { Adoptions = catalog.Adoptions.Select(a => a.Id == id ? item : a).ToArray() }, actor, "quality.experience.confirm", id, ct);
        await NotifyPendingAsync(catalog.CompanyId, ct);
        return saved;
    }
}
