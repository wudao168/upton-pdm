using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectPlanningService(
    IProjectPlanningRepository plans,
    IPdmRepository repository,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ProjectPlanTemplate>> ListTemplatesAsync(bool includeInactive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (includeInactive && !CanManageTemplates(role)) throw new UnauthorizedAccessException("仅开发者和管理员可查看停用模板。");
        return await EnsureDefaultTemplateAsync(includeInactive, actor, cancellationToken);
    }

    public async Task<ProjectPlanTemplate> SaveTemplateAsync(Guid? templateId, SaveProjectPlanTemplateCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!CanManageTemplates(role)) throw new UnauthorizedAccessException("仅开发者和管理员可修改计划模板。");
        var name = Required(command.Name, 120, "模板名称");
        ValidateTemplateTasks(command.Tasks);
        var now = timeProvider.GetUtcNow();
        var current = templateId is Guid id ? await plans.FindTemplateAsync(id, cancellationToken) : null;
        if (templateId is not null && current is null) throw new PdmNotFoundException("计划模板不存在。");
        var stages = ValidateStages(command.Stages ?? current?.Stages ?? ProjectPlanStage.Defaults, command.Tasks.Select(item => item.Stage));
        ValidateAllocation(stages, command.Tasks);
        var template = new ProjectPlanTemplate(
            templateId ?? Guid.NewGuid(), name, Optional(command.ProjectTypeCode, 64), command.IsActive,
            command.Tasks.OrderBy(item => item.SortOrder).Select(item => item with
            {
                Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
                Name = Required(item.Name, 160, "任务名称"),
                DurationRatio = Math.Round(item.DurationRatio, 4),
                Weight = Math.Round(item.Weight, 4)
            }).ToArray(),
            current?.CreatedBy ?? actor, current?.CreatedAt ?? now, actor, now, current?.RowVersion ?? 0) { Stages = stages };
        var saved = await plans.SaveTemplateAsync(template, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, templateId is null ? "project-plan.template.create" : "project-plan.template.update", nameof(ProjectPlanTemplate), saved.Id, $"计划模板：{saved.Name}；{saved.Tasks.Count}项", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan?> GetPlanAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireReadAsync(projectId, actor, role, cancellationToken);
        var plan = await plans.FindPlanAsync(projectId, cancellationToken);
        return WithTaskBounds(plan is null ? null : VisiblePlan(plan, actor));
    }

    public async Task<ProjectPlan> GenerateAsync(Guid projectId, GenerateProjectPlanCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        if (command.TotalDurationDays is < 1 or > 3650) throw new PdmRuleException("计划总工期必须为1至3650个自然日。");
        var template = await plans.FindTemplateAsync(command.TemplateId, cancellationToken)
            ?? (await EnsureDefaultTemplateAsync(false, actor, cancellationToken)).FirstOrDefault(item => item.Id == command.TemplateId)
            ?? throw new PdmNotFoundException("计划模板不存在。");
        if (!template.IsActive) throw new PdmRuleException("停用模板不能用于生成计划。");
        var current = await plans.FindPlanAsync(projectId, cancellationToken);
        if (current?.ApprovalStatus == ProjectPlanApprovalStatus.Approved) throw new PdmRuleException("已生效计划请通过任务编辑或批量调整修改，不能重新生成首版计划。");
        if (current is not null && !command.ReplaceExisting) throw new PdmConflictException("项目已经有计划。如需重新生成，请选择替换并填写变更原因。");
        var reason = current is null ? "生成初始草稿" : Optional(command.ChangeReason, 300) ?? "重新生成草稿";
        var now = timeProvider.GetUtcNow();
        ValidateAllocation(template.Stages, template.Tasks);
        var activeTemplate = SelectStagesToGenerate(template, command.DeferredStages);
        var generatedTasks = Schedule(activeTemplate, command.StartDate, command.TotalDurationDays, project, command.IndependentStages);
        var plan = new ProjectPlan(
            current?.Id ?? Guid.NewGuid(), projectId, template.Id, template.Name,
            EvaluateStage(generatedTasks, null, activeTemplate.Stages), null, null,
            generatedTasks.Min(item => item.PlannedStart), generatedTasks.Max(item => item.PlannedFinish), ForecastFinish(generatedTasks),
            current?.BaselineVersion ?? 0, generatedTasks,
            current?.CreatedBy ?? actor, current?.CreatedAt ?? now, actor, now, current?.RowVersion ?? 0)
        {
            Stages = activeTemplate.Stages,
            StageSchedules = AllocateStageSchedules(activeTemplate.Stages, command.StartDate, command.TotalDurationDays, command.IndependentStages)
        };
        var saved = await SaveWithChildrenAsync(project, plan, current?.RowVersion, null, actor, role, cancellationToken);
        await AuditAsync(actor, current is null ? "project-plan.generate" : "project-plan.regenerate", nameof(ProjectPlan), saved.Id, $"{project.Code}；模板{template.Name}；{reason}", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> SupplementStageScheduleAsync(Guid projectId, DateOnly startDate, int totalDurationDays, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        if (current.ApprovalStatus == ProjectPlanApprovalStatus.Approved)
            throw new PdmRuleException("已生效计划不能直接补充排期，请发起变更申请。");
        if (current.StageSchedules.Count > 0) throw new PdmRuleException("计划已保存阶段排期，不能重复补充。");
        var stages = current.Stages.Where(stage => stage.ParticipatesInDelivery == true).ToArray();
        if (totalDurationDays is < 1 or > 3650 || stages.Length == 0 || stages.Sum(stage => stage.DurationRatio) != 1)
            throw new PdmRuleException("请确认总工期及原计划的阶段工期比例（合计须为100%）。");
        var schedules = AllocateStageSchedules(stages, startDate, totalDurationDays, null);
        var now = timeProvider.GetUtcNow();
        var reason = $"补充原始交付阶段排期：{startDate:yyyy-MM-dd}开始，{totalDurationDays}自然日；不修改子任务";
        var saved = await plans.SavePlanAsync(current with
        {
            StageSchedules = schedules, UpdatedBy = actor, UpdatedAt = now,
            ApprovalStatus = ProjectPlanApprovalStatus.Draft,
            ApprovalAssignee = null, SubmittedBy = null, SubmittedAt = null
        }, expectedRowVersion, null, cancellationToken);
        await AuditAsync(actor, "project-plan.stage-schedule", nameof(ProjectPlan), saved.Id, $"{project.Code}；{reason}", cancellationToken);
        return saved;
    }

    public async Task DeleteDraftAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken, bool includeIndependentChildren = false)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        var deletingApprovedPlan = current.ApprovalStatus == ProjectPlanApprovalStatus.Approved;
        if (deletingApprovedPlan && (current.ChangeDraftSource is null || current.ChangeRequest?.Status != ProjectPlanApprovalStatus.Approved))
            throw new PdmRuleException("已生效计划不能直接删除，请先申请并取得变更权限。");
        if (deletingApprovedPlan && !string.Equals(actor, current.ChangeRequest!.SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅变更申请人可以删除本次变更草稿及原计划。");
        var deleted = new List<(Project Project, ProjectPlan Plan)> { (project, current) };
        if (project.ParentProjectId is null)
        {
            var children = (await repository.ListProjectsAsync(cancellationToken)).Where(item => item.ParentProjectId == project.Id).ToArray();
            var childPlans = await plans.ListPlansAsync(children.Select(item => item.Id).ToArray(), cancellationToken);
            foreach (var childPlan in childPlans.Where(plan => plan.ApprovalStatus != ProjectPlanApprovalStatus.Approved
                && ((plan.FollowsParentPlan && plan.ParentPlanId == current.Id) || (!deletingApprovedPlan && includeIndependentChildren))))
            {
                var child = children.Single(item => item.Id == childPlan.ProjectId);
                if (!CanManage(child, actor, role)) throw new UnauthorizedAccessException($"无权删除子项目“{child.Code}”的计划。");
                deleted.Add((child, childPlan));
            }
        }
        await plans.DeletePlansAsync(deleted.ToDictionary(item => item.Plan.ProjectId, item => item.Plan.RowVersion), cancellationToken);
        foreach (var item in deleted)
            await AuditAsync(actor, "project-plan.delete", nameof(ProjectPlan), item.Plan.Id,
                $"{item.Project.Code}；{(item.Plan.ApprovalStatus == ProjectPlanApprovalStatus.Approved ? "经批准变更权限删除现行计划及变更草稿" : "未批准计划已永久删除")}", cancellationToken);
    }

    public async Task<ProjectPlan> SaveAsync(Guid projectId, SaveProjectPlanCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        var editingChangeDraft = current.ApprovalStatus == ProjectPlanApprovalStatus.Approved && current.ChangeDraftSource is not null
            && current.ChangeRequest?.Status == ProjectPlanApprovalStatus.Approved;
        if (current.ApprovalStatus == ProjectPlanApprovalStatus.Approved && !editingChangeDraft)
            throw new PdmRuleException("已生效计划不能直接修改，请先申请并取得变更权限。");
        if (editingChangeDraft && !string.Equals(actor, current.ChangeRequest!.SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅变更申请人可以编辑本次变更草稿。");
        var reason = editingChangeDraft ? "保存计划变更草稿" : "修改未批准计划";
        var tasks = NormalizeTasks(command.Tasks);
        ValidatePlanTasks(tasks);
        if (tasks.Count != current.Tasks.Count || tasks.Any(task => !current.Tasks.Any(old => old.Id == task.Id)))
            throw new PdmRuleException("不能通过任务编辑新增、删除或替换模板任务。");
        if (tasks.Any(task => current.Tasks.Any(old => old.Id == task.Id && (old.Name != task.Name || old.Stage != task.Stage || old.Weight != task.Weight))))
            throw new PdmRuleException("任务名称、所属阶段和进度权重采用生成计划时的模板配置，不能在项目计划中修改。");
        if (editingChangeDraft && tasks.Any(task => current.Tasks.Any(old => old.Id == task.Id && old.Status == ProjectPlanTaskStatus.Completed
            && (task.PlannedStart != old.PlannedStart || task.PlannedFinish != old.PlannedFinish || task.Assignee != old.Assignee))))
            throw new PdmRuleException("已完成任务的计划日期和责任人不能在变更草稿中调整。");
        var stages = ValidateStages(command.Stages ?? current.Stages, tasks.Select(item => item.Stage));
        if (current.Stages.All(stage => stage.ParticipatesInDelivery is not null))
        {
            if (stages.Count != current.Stages.Count || stages.Any(stage => !current.Stages.Any(old => old.Code == stage.Code
                && old.ParticipatesInDelivery == stage.ParticipatesInDelivery && old.DurationRatio == stage.DurationRatio
                && old.ProgressRatio == stage.ProgressRatio && old.IndependentDurationDays == stage.IndependentDurationDays)))
                throw new PdmRuleException("阶段分配采用生成计划时的模板快照，不能通过任务编辑修改分配规则。");
            if (stages.Any(stage => tasks.Where(task => task.Stage == stage.Code).Sum(task => task.Weight) <= 0))
                throw new PdmRuleException("每个阶段的子任务总权重必须大于0。");
        }
        if (tasks.Any(item => item.CompletionPercent != (current.Tasks.FirstOrDefault(old => old.Id == item.Id)?.CompletionPercent ?? 0)
            || item.ActualStart != current.Tasks.FirstOrDefault(old => old.Id == item.Id)?.ActualStart
            || item.ActualFinish != current.Tasks.FirstOrDefault(old => old.Id == item.Id)?.ActualFinish))
            throw new PdmRuleException("首版计划批准生效后才能填报实际进度。");
        tasks = tasks.Select(item => item with
        {
            TemplateTaskId = current.Tasks.First(old => old.Id == item.Id).TemplateTaskId,
            SourceTaskId = current.Tasks.First(old => old.Id == item.Id).SourceTaskId,
            BaselineStart = current.Tasks.FirstOrDefault(old => old.Id == item.Id)?.BaselineStart,
            BaselineFinish = current.Tasks.FirstOrDefault(old => old.Id == item.Id)?.BaselineFinish
        }).ToArray();
        tasks = CascadeDependentTasks(tasks);
        var shiftDays = tasks[0].PlannedStart.DayNumber - current.Tasks.First(old => old.Id == tasks[0].Id).PlannedStart.DayNumber;
        var shiftedTogether = shiftDays != 0 && tasks.All(task => current.Tasks.Any(old => old.Id == task.Id
            && task.PlannedStart == old.PlannedStart.AddDays(shiftDays) && task.PlannedFinish == old.PlannedFinish.AddDays(shiftDays)));
        var now = timeProvider.GetUtcNow();
        var planningChanged = tasks.Any(task => current.Tasks.Any(old => old.Id == task.Id
            && (task.PlannedStart != old.PlannedStart || task.PlannedFinish != old.PlannedFinish
                || task.IsRequired != old.IsRequired || task.IsMilestone != old.IsMilestone
                || !task.PredecessorTaskIds.SequenceEqual(old.PredecessorTaskIds)))) || !stages.SequenceEqual(current.Stages);
        var next = current with
        {
            FollowsParentPlan = current.FollowsParentPlan && !planningChanged,
            CurrentStage = EvaluateStage(tasks, current.ManualStage, stages),
            Stages = stages,
            StageSchedules = shiftedTogether ? current.StageSchedules.Select(stage => stage with { StartDate = stage.StartDate.AddDays(shiftDays) }).ToArray()
                : current.StageSchedules,
            PlannedStart = tasks.Min(item => item.PlannedStart),
            PlannedFinish = tasks.Max(item => item.PlannedFinish),
            ForecastFinish = ForecastFinish(tasks),
            Tasks = tasks,
            ApprovalStatus = editingChangeDraft ? ProjectPlanApprovalStatus.Approved : ProjectPlanApprovalStatus.Draft,
            ApprovalAssignee = editingChangeDraft ? current.ApprovalAssignee : null,
            SubmittedBy = editingChangeDraft ? current.SubmittedBy : null,
            SubmittedAt = editingChangeDraft ? current.SubmittedAt : null,
            ApprovalComment = editingChangeDraft ? current.ApprovalComment : null,
            UpdatedBy = actor,
            UpdatedAt = now
        };
        // A change draft is deliberately isolated: following child plans are synchronized only when it is activated.
        var saved = editingChangeDraft
            ? await plans.SavePlanAsync(next, command.ExpectedRowVersion, null, cancellationToken)
            : await SaveWithChildrenAsync(project, next, command.ExpectedRowVersion, null, actor, role, cancellationToken, command.CreateMissingFollowers);
        await AuditAsync(actor, "project-plan.update", nameof(ProjectPlan), saved.Id, $"{project.Code}；{reason}", cancellationToken);
        return saved;
    }

    private async Task<ProjectPlan> SaveWithChildrenAsync(Project project, ProjectPlan source, long? expectedRowVersion,
        ProjectPlanVersion? version, string actor, UserRole role, CancellationToken cancellationToken, bool createMissingFollowers = false)
    {
        if (project.ParentProjectId is not null)
            return await plans.SavePlanAsync(source, expectedRowVersion, version, cancellationToken);
        var writes = new List<ProjectPlanWrite>();
        var results = new List<ProjectPlanSyncResult>();
        var children = (await repository.ListProjectsAsync(cancellationToken)).Where(item => item.ParentProjectId == project.Id);
        foreach (var child in children)
        {
            var current = await plans.FindPlanAsync(child.Id, cancellationToken, includeDeleted: true);
            var skip = !CanManage(child, actor, role) ? "无子项目编辑权限"
                : current is null && expectedRowVersion is not null && !createMissingFollowers ? "尚未建立计划，保留当前状态"
                : current?.IsDeleted == true ? "已删除的计划不自动恢复"
                : current?.ApprovalStatus == ProjectPlanApprovalStatus.Approved ? "已生效，保留原计划"
                : current is not null && (!current.FollowsParentPlan || current.ParentPlanId != source.Id) ? "独立计划，保留原计划" : null;
            var matches = source.Tasks.ToDictionary(task => task.Id, task => current?.Tasks.FirstOrDefault(old =>
                old.SourceTaskId == task.Id || (task.TemplateTaskId is not null && old.TemplateTaskId == task.TemplateTaskId)));
            var retained = matches.Values.Where(task => task is not null).Select(task => task!.Id).ToHashSet();
            if (skip is null && current is not null && current.Tasks.Any(task => !retained.Contains(task.Id)
                && (task.ActualStart is not null || task.ActualFinish is not null || task.CompletionPercent != 0)))
                skip = "任务结构变化涉及实际记录，保留原计划";
            if (skip is not null)
            {
                results.Add(new(child.Id, child.Code, skip, PlanDifferences(source, current)));
                continue;
            }
            var taskIds = source.Tasks.ToDictionary(task => task.Id, task => matches[task.Id]?.Id ?? Guid.NewGuid());
            var tasks = source.Tasks.Select(task =>
            {
                var old = matches[task.Id];
                return task with
                {
                    Id = taskIds[task.Id], SourceTaskId = task.Id,
                    Assignee = old is not null ? old.Assignee : ResolveAssigneeForStage(task.Stage, child),
                    ActualStart = old?.ActualStart, ActualFinish = old?.ActualFinish,
                    CompletionPercent = old?.CompletionPercent ?? 0, Status = old?.Status ?? ProjectPlanTaskStatus.NotStarted,
                    BaselineStart = old?.BaselineStart, BaselineFinish = old?.BaselineFinish,
                    PredecessorTaskIds = task.PredecessorTaskIds.Select(id => taskIds[id]).ToArray()
                };
            }).ToArray();
            var copied = source with
            {
                Id = current?.Id ?? Guid.NewGuid(), ProjectId = child.Id, Tasks = tasks,
                FollowsParentPlan = true, ParentPlanId = source.Id, ParentPlanRowVersion = (expectedRowVersion ?? 0) + 1,
                ChildSyncResults = [], CurrentStage = EvaluateStage(tasks, current?.ManualStage, source.Stages),
                ManualStage = current?.ManualStage, ManualStageReason = current?.ManualStageReason,
                PlannedStart = tasks.Min(item => item.PlannedStart), PlannedFinish = tasks.Max(item => item.PlannedFinish),
                ForecastFinish = ForecastFinish(tasks), BaselineVersion = current?.BaselineVersion ?? 0,
                ApprovalStatus = ProjectPlanApprovalStatus.Draft, ApprovalAssignee = null,
                SubmittedBy = null, SubmittedAt = null, ApprovedBy = null, ApprovedAt = null, ApprovalComment = null,
                CreatedBy = current?.CreatedBy ?? actor, CreatedAt = current?.CreatedAt ?? source.UpdatedAt,
                RowVersion = current?.RowVersion ?? 0
            };
            writes.Add(new(copied, current?.RowVersion, null));
            results.Add(new(child.Id, child.Code, current is null ? "已新建跟随草稿" : "已同步（草稿）", []));
        }
        writes.Insert(0, new(source with { ChildSyncResults = results }, expectedRowVersion, version));
        // Parent, children and their version snapshots succeed or roll back together.
        return (await plans.SavePlansAsync(writes, cancellationToken))[0];
    }

    private static IReadOnlyList<string> PlanDifferences(ProjectPlan source, ProjectPlan? target)
    {
        if (target is null || target.IsDeleted) return ["未建立有效计划"];
        var differences = new List<string>();
        foreach (var task in source.Tasks)
        {
            var old = target.Tasks.FirstOrDefault(item => item.SourceTaskId == task.Id
                || (task.TemplateTaskId is not null && item.TemplateTaskId == task.TemplateTaskId)
                || (item.Name == task.Name && item.Stage == task.Stage));
            if (old is null) differences.Add($"缺少任务：{task.Name}");
            else if (old.PlannedStart != task.PlannedStart || old.PlannedFinish != task.PlannedFinish)
                differences.Add($"{task.Name}：子项目{old.PlannedStart:yyyy-MM-dd}～{old.PlannedFinish:yyyy-MM-dd}；主项目{task.PlannedStart:yyyy-MM-dd}～{task.PlannedFinish:yyyy-MM-dd}");
        }
        foreach (var task in target.Tasks.Where(task => !source.Tasks.Any(item => item.Id == task.SourceTaskId
            || (task.TemplateTaskId is not null && item.TemplateTaskId == task.TemplateTaskId) || (item.Name == task.Name && item.Stage == task.Stage))))
            differences.Add($"子项目独有任务：{task.Name}");
        if (!source.StageSchedules.SequenceEqual(target.StageSchedules)) differences.Add("阶段计划区间不同");
        return differences.Count == 0 ? ["任务日期与主项目一致，仍保留独立/审批状态"] : differences;
    }

    public async Task<ProjectPlan> SetBaselineAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        RequireEffective(current);
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        var now = timeProvider.GetUtcNow();
        var nextBaseline = current.BaselineVersion + 1;
        var saved = await plans.SavePlanAsync(current with
        {
            BaselineVersion = nextBaseline,
            Tasks = current.Tasks.Select(item => item with { BaselineStart = item.PlannedStart, BaselineFinish = item.PlannedFinish }).ToArray(),
            UpdatedBy = actor,
            UpdatedAt = now
        }, expectedRowVersion, NewVersion(current, $"设置基线V{nextBaseline}", actor, now), cancellationToken);
        await AuditAsync(actor, "project-plan.baseline", nameof(ProjectPlan), saved.Id, $"{project.Code}；基线V{nextBaseline}", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> UpdateProgressAsync(Guid projectId, Guid taskId, UpdateProjectPlanTaskProgressCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireReadProjectAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        RequireEffective(current);
        if (current.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        var task = current.Tasks.FirstOrDefault(item => item.Id == taskId) ?? throw new PdmNotFoundException("计划任务不存在。");
        if (!CanManage(project, actor, role) && !string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅任务责任人或项目经理可以填报实际进度。");
        if (command.CompletionPercent is < 0 or > 100) throw new PdmRuleException("完成比例必须为0至100。");
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        if (command.ActualStart > today || command.ActualFinish > today) throw new PdmRuleException("实际开始和实际完成不能晚于今天。");
        if (command.ActualFinish is not null && command.ActualStart is null) throw new PdmRuleException("填写实际完成日期前必须填写实际开始日期。");
        if (command.ActualStart is not null && command.ActualFinish < command.ActualStart) throw new PdmRuleException("实际完成日期不能早于实际开始日期。");
        var status = command.CompletionPercent switch { 0 => ProjectPlanTaskStatus.NotStarted, 100 => ProjectPlanTaskStatus.Completed, _ => ProjectPlanTaskStatus.InProgress };
        var updatedTask = task with
        {
            CompletionPercent = command.CompletionPercent,
            ActualStart = command.ActualStart ?? (command.CompletionPercent > 0 ? DateOnly.FromDateTime(timeProvider.GetUtcNow().LocalDateTime) : null),
            ActualFinish = command.CompletionPercent == 100 ? command.ActualFinish ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().LocalDateTime) : null,
            Status = status
        };
        var tasks = current.Tasks.Select(item => item.Id == taskId ? updatedTask : item).ToArray();
        var now = timeProvider.GetUtcNow();
        var source = current.ChangeDraftSource;
        if (source is not null)
        {
            var sourceTasks = source.Tasks.Select(item => item.Id == taskId ? item with
            {
                CompletionPercent = updatedTask.CompletionPercent, ActualStart = updatedTask.ActualStart,
                ActualFinish = updatedTask.ActualFinish, Status = updatedTask.Status
            } : item).ToArray();
            source = source with { Tasks = sourceTasks, CurrentStage = EvaluateStage(sourceTasks, source.ManualStage, source.Stages),
                ForecastFinish = ForecastFinish(sourceTasks), UpdatedBy = actor, UpdatedAt = now, ChangeDraftSource = null };
        }
        var saved = await plans.SavePlanAsync(current with
        {
            Tasks = tasks,
            CurrentStage = EvaluateStage(tasks, current.ManualStage, current.Stages),
            ForecastFinish = ForecastFinish(tasks),
            ChangeDraftSource = source,
            UpdatedBy = actor,
            UpdatedAt = now
        }, command.ExpectedRowVersion, NewVersion(EffectivePlan(current), $"填报“{task.Name}”实际进度{command.CompletionPercent}%", actor, now), cancellationToken);
        await AuditAsync(actor, "project-plan.progress", nameof(ProjectPlanTask), taskId, $"{project.Code}；{task.Name}；{command.CompletionPercent}%", cancellationToken);
        return saved;
    }

    public async Task SyncReleasedBomProgressAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var current = await plans.FindPlanAsync(projectId, cancellationToken);
        if (current?.ApprovalStatus != ProjectPlanApprovalStatus.Approved) return;
        var published = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
            .Where(item => item.State == ReleasePackageState.Published && item.PublishedAt.HasValue).ToArray();
        DateOnly? FirstPublication(ReleaseScope scope, Func<ReleasePackage, bool> legacyHasKind) => published
            .Where(item => item.Scope == scope || (item.Scope == ReleaseScope.LegacyCombined && legacyHasKind(item)))
            .Select(item => (DateOnly?)DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.PublishedAt!.Value, timeProvider.LocalTimeZone).DateTime))
            .OrderBy(date => date).FirstOrDefault();
        var standard = FirstPublication(ReleaseScope.StandardFormal, item => item.StandardBomVersionId.HasValue);
        var nonStandard = FirstPublication(ReleaseScope.NonStandardWithDrawing, item => item.NonStandardBomVersionId.HasValue);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        ProjectPlanTask[] ApplyBomProgress(IReadOnlyList<ProjectPlanTask> sourceTasks) => sourceTasks.Select(task =>
        {
            DateOnly? finish = task.Name.Trim() switch
            {
                "标准件BOM" => standard,
                "非标件BOM" or "非标件BOM+图纸" => nonStandard,
                "机械设计" when standard.HasValue && nonStandard.HasValue => standard > nonStandard ? standard : nonStandard,
                _ => null
            };
            // Keep existing actual records; do not invent completion for unrelated or conflicting tasks.
            if (finish is null || finish > today || task.CompletionPercent == 100 || task.ActualStart > finish) return task;
            return task with { CompletionPercent = 100, Status = ProjectPlanTaskStatus.Completed,
                ActualStart = task.ActualStart ?? finish, ActualFinish = finish };
        }).ToArray();
        var tasks = ApplyBomProgress(current.Tasks);
        if (tasks.SequenceEqual(current.Tasks)) return;
        var now = timeProvider.GetUtcNow();
        const string actor = "system:bom-release";
        var changed = string.Join("、", tasks.Where((task, index) => task != current.Tasks[index]).Select(task => task.Name));
        var source = current.ChangeDraftSource;
        if (source is not null)
        {
            var sourceTasks = ApplyBomProgress(source.Tasks);
            source = source with { Tasks = sourceTasks, CurrentStage = EvaluateStage(sourceTasks, source.ManualStage, source.Stages),
                ForecastFinish = ForecastFinish(sourceTasks), UpdatedBy = actor, UpdatedAt = now, ChangeDraftSource = null };
        }
        var saved = await plans.SavePlanAsync(current with { Tasks = tasks,
            CurrentStage = EvaluateStage(tasks, current.ManualStage, current.Stages), ForecastFinish = ForecastFinish(tasks),
            ChangeDraftSource = source, UpdatedBy = actor, UpdatedAt = now }, current.RowVersion,
            NewVersion(EffectivePlan(current), $"BOM正式发布自动完成：{changed}", actor, now), cancellationToken);
        await AuditAsync(actor, "project-plan.bom-release", nameof(ProjectPlan), saved.Id, $"BOM正式发布自动完成：{changed}", cancellationToken);
    }

    public async Task<ProjectPlan> SetStageAsync(Guid projectId, SetProjectPlanStageCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        RequireEffective(current);
        if (current.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        if (command.Stage is not null && command.Stage is not (ProjectPlanStage.Paused or ProjectPlanStage.Cancelled or ProjectPlanStage.Terminated))
            throw new PdmRuleException("人工状态仅用于暂停、取消或终止；正常阶段由任务完成条件自动判断。");
        var reason = Required(command.Reason, 300, "阶段调整原因");
        var now = timeProvider.GetUtcNow();
        var source = current.ChangeDraftSource is null ? null : current.ChangeDraftSource with
        {
            ManualStage = command.Stage, ManualStageReason = command.Stage is null ? null : reason,
            CurrentStage = EvaluateStage(current.ChangeDraftSource.Tasks, command.Stage, current.ChangeDraftSource.Stages),
            UpdatedBy = actor, UpdatedAt = now, ChangeDraftSource = null
        };
        var saved = await plans.SavePlanAsync(current with
        {
            ManualStage = command.Stage,
            ManualStageReason = command.Stage is null ? null : reason,
            CurrentStage = EvaluateStage(current.Tasks, command.Stage, current.Stages),
            ChangeDraftSource = source,
            UpdatedBy = actor,
            UpdatedAt = now
        }, command.ExpectedRowVersion, NewVersion(EffectivePlan(current), reason, actor, now), cancellationToken);
        await AuditAsync(actor, "project-plan.stage", nameof(ProjectPlan), saved.Id, $"{project.Code}；{saved.CurrentStage}；{reason}", cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<ProjectPlan>> ReuseAsync(Guid rootProjectId, ReuseProjectPlanCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var root = await RequireManageAsync(rootProjectId, actor, role, cancellationToken);
        if (root.ParentProjectId is not null) throw new PdmRuleException("请在主项目中批量套用子项目计划。");
        var allProjects = await repository.ListProjectsAsync(cancellationToken);
        var children = allProjects.Where(item => item.ParentProjectId == rootProjectId).ToDictionary(item => item.Id);
        if (!children.TryGetValue(command.SourceProjectId, out var sourceProject)) throw new PdmRuleException("来源必须是当前主项目下的子项目。");
        var source = await plans.FindPlanAsync(command.SourceProjectId, cancellationToken) ?? throw new PdmRuleException("来源子项目尚未生成计划。");
        var targetIds = command.TargetProjectIds.Distinct().Where(item => item != command.SourceProjectId).ToArray();
        if (targetIds.Length == 0) throw new PdmRuleException("请至少选择一个目标子项目。");
        var reason = Required(command.ChangeReason, 300, "套用原因");
        var targets = new List<(Project Project, ProjectPlan? Current)>();
        foreach (var targetId in targetIds)
        {
            if (!children.TryGetValue(targetId, out var target)) throw new PdmRuleException("目标项目必须属于当前主项目。");
            _ = await RequireManageAsync(targetId, actor, role, cancellationToken);
            var current = await plans.FindPlanAsync(targetId, cancellationToken);
            if (current?.ApprovalStatus == ProjectPlanApprovalStatus.Approved) throw new PdmRuleException($"子项目“{target.Code}”已批准生效，不能覆盖为首版草稿。");
            if (current is not null && !command.ReplaceExisting) throw new PdmConflictException($"子项目“{target.Code}”已有计划；如需替换请勾选覆盖现有计划。");
            targets.Add((target, current));
        }

        var now = timeProvider.GetUtcNow();
        var savedPlans = new List<ProjectPlan>();
        foreach (var (target, current) in targets)
        {
            var taskIds = source.Tasks.ToDictionary(item => item.Id, _ => Guid.NewGuid());
            var tasks = source.Tasks.Select(item => item with
            {
                Id = taskIds[item.Id],
                Assignee = ResolveAssigneeForStage(item.Stage, target),
                BaselineStart = null,
                BaselineFinish = null,
                ActualStart = null,
                ActualFinish = null,
                CompletionPercent = 0,
                Status = ProjectPlanTaskStatus.NotStarted,
                PredecessorTaskIds = item.PredecessorTaskIds.Select(id => taskIds[id]).ToArray()
            }).ToArray();
            var copied = new ProjectPlan(
                current?.Id ?? Guid.NewGuid(), target.Id, source.TemplateId, $"{source.TemplateName}（套用自{sourceProject.Code}）",
                EvaluateStage(tasks, null, source.Stages), null, null,
                tasks.Min(item => item.PlannedStart), tasks.Max(item => item.PlannedFinish), ForecastFinish(tasks), 0, tasks,
                current?.CreatedBy ?? actor, current?.CreatedAt ?? now, actor, now, current?.RowVersion ?? 0) { Stages = source.Stages, StageSchedules = source.StageSchedules };
            var saved = await plans.SavePlanAsync(copied, current?.RowVersion, null, cancellationToken);
            savedPlans.Add(saved);
            await AuditAsync(actor, "project-plan.reuse", nameof(ProjectPlan), saved.Id, $"{target.Code}；来源{sourceProject.Code}；{reason}", cancellationToken);
        }
        return savedPlans;
    }

    public async Task<IReadOnlyList<ProjectPlanVersion>> ListVersionsAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireReadAsync(projectId, actor, role, cancellationToken);
        var plan = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        return await plans.ListVersionsAsync(plan.Id, cancellationToken);
    }

    public async Task<ProjectPlanPortfolio> GetPortfolioAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var selected = await RequireReadProjectAsync(projectId, actor, role, cancellationToken);
        var rootId = selected.ParentProjectId is null ? selected.Id : selected.RootProjectId ?? selected.ParentProjectId.Value;
        await RequireReadAsync(rootId, actor, role, cancellationToken);
        var projects = (await repository.ListProjectsAsync(cancellationToken))
            .Where(item => item.Id == rootId || item.ParentProjectId == rootId)
            .OrderBy(item => item.ParentProjectId is null ? 0 : 1)
            .ThenBy(item => item.ChildSequence ?? 0)
            .ToArray();
        var loaded = (await plans.ListPlansAsync(projects.Select(item => item.Id).ToArray(), cancellationToken))
            .Select(plan => WithTaskBounds(plan)!).ToDictionary(item => item.ProjectId);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().LocalDateTime);
        var items = projects.Select(project =>
        {
            loaded.TryGetValue(project.Id, out var plan);
            var effective = plan?.ApprovalStatus == ProjectPlanApprovalStatus.Approved;
            var operational = plan is null ? null : EffectivePlan(plan);
            var percent = effective ? CompletionPercent(operational!.Tasks, operational.Stages) : 0;
            var lagging = effective && operational!.Tasks.Any(task => task.Status != ProjectPlanTaskStatus.Completed && task.PlannedFinish < today);
            var atRisk = !lagging && effective && operational!.Tasks.Any(task => task.Status != ProjectPlanTaskStatus.Completed && task.PlannedFinish >= today && task.PlannedFinish <= today.AddDays(7));
            return new ProjectPlanPortfolioItem(project.Id, project.Code, project.Name, project.Id == rootId, plan is not null, effective ? operational!.CurrentStage : null, percent,
                operational?.PlannedStart, operational?.PlannedFinish, operational?.ForecastFinish, lagging, atRisk, plan is null ? null : VisiblePlan(plan, actor));
        }).ToArray();
        var active = items.Where(item => item.Plan?.ApprovalStatus == ProjectPlanApprovalStatus.Approved && item.CurrentStage is not (ProjectPlanStage.Cancelled or ProjectPlanStage.Terminated)).ToArray();
        var stage = active.Any(item => item.CurrentStage == ProjectPlanStage.Paused)
            ? ProjectPlanStage.Paused
            : active.OrderBy(item => Array.FindIndex(item.Plan!.Stages.ToArray(), stage => stage.Code == item.CurrentStage)).Select(item => item.CurrentStage!).FirstOrDefault(ProjectPlanStage.Design);
        var weighted = active.Length == 0 ? 0 : (int)Math.Round(active.Average(item => item.CompletionPercent));
        return new ProjectPlanPortfolio(rootId, stage, weighted, items.Count(item => item.IsLagging), items.Count(item => item.IsAtRisk), active.MinOrDefault(item => item.PlannedStart), active.MaxOrDefault(item => item.PlannedFinish), items);
    }

    public async Task SendDueRemindersAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().LocalDateTime);
        var now = timeProvider.GetUtcNow();
        foreach (var plan in await plans.ListPlansAsync(null, cancellationToken))
        {
            if (plan.ApprovalStatus != ProjectPlanApprovalStatus.Approved) continue;
            var project = await repository.FindProjectAsync(plan.ProjectId, cancellationToken);
            if (project is null) continue;
            foreach (var task in EffectivePlan(plan).Tasks.Where(item => item.Status != ProjectPlanTaskStatus.Completed && !string.IsNullOrWhiteSpace(item.Assignee)))
            {
                var days = task.PlannedFinish.DayNumber - today.DayNumber;
                if (days is not (7 or 3 or 0) && days >= 0) continue;
                var kind = days < 0 ? "overdue" : $"d{days}";
                var title = days < 0 ? $"项目计划任务已逾期{-days}天" : days == 0 ? "项目计划任务今天到期" : $"项目计划任务还有{days}天到期";
                var notification = new UserNotification(Guid.NewGuid(), task.Assignee!, "project-plan", title,
                    $"{project.Code} · {task.Name}，计划完成日期 {task.PlannedFinish:yyyy-MM-dd}。", project.Id, null,
                    $"project-plan:{task.Id:N}:{kind}:{today:yyyyMMdd}", now, null);
                await repository.CreateUserNotificationsAsync([notification], cancellationToken);
            }
        }
    }

    public async Task<ProjectPlan> SubmitChangeAsync(Guid projectId, SubmitProjectPlanChangeCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        RequireEffective(current);
        if (current.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("计划已被修改，请刷新后重新申请。");
        if (current.ChangeRequest?.Status == ProjectPlanApprovalStatus.Pending) throw new PdmRuleException("已有变更申请待审批，请勿重复提交。");
        if (current.ChangeDraftSource is not null) throw new PdmRuleException("已有变更草稿，请先完成或放弃后再发起新的申请。");
        var reason = Required(command.Reason, 300, "变更原因");
        // New requests only ask for permission. Retain valid task changes solely for backward compatibility
        // with requests submitted by an older UI before this workflow was introduced.
        if (command.Tasks.Select(task => task.TaskId).Distinct().Count() != command.Tasks.Count
            || command.Tasks.Any(task => !current.Tasks.Any(old => old.Id == task.TaskId)))
            throw new PdmRuleException("变更申请包含无效或重复的计划任务。");
        var assignee = current.ApprovedBy ?? await ResolveApprovalAssigneeAsync(project, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var request = new ProjectPlanChangeRequest(Guid.NewGuid(), command.Tasks, reason, actor, now, assignee, ProjectPlanApprovalStatus.Pending);
        var saved = await plans.SavePlanAsync(current with { ChangeRequest = request, UpdatedBy = actor, UpdatedAt = now },
            current.RowVersion, null, cancellationToken);
        await repository.CreateUserNotificationsAsync([new UserNotification(Guid.NewGuid(), assignee, "project-plan-approval", "项目计划变更权限待审批",
            $"{project.Code} · {reason}；批准后申请人可基于现行计划编辑草稿，原计划继续生效。", projectId, null, $"project-plan-change:{request.Id:N}", now, null)], cancellationToken);
        await AuditAsync(actor, "project-plan.change.submit", nameof(ProjectPlan), saved.Id, reason, cancellationToken);
        return saved;
    }

    private static ProjectPlanTask[] ApplyScheduleChanges(IReadOnlyList<ProjectPlanTask> tasks, IReadOnlyList<ProjectPlanScheduleChange> changes)
        => tasks.Select(task => changes.FirstOrDefault(change => change.TaskId == task.Id) is { } change
            ? task with { PlannedStart = change.PlannedStart, PlannedFinish = change.PlannedFinish,
                DurationDays = task.IsMilestone ? 0 : change.PlannedFinish.DayNumber - change.PlannedStart.DayNumber + 1,
                Assignee = Optional(change.Assignee, 120) }
            : task).ToArray();

    private async Task<ProjectPlan> DecideChangeAsync(Project project, ProjectPlan current, bool approve, string? comment,
        string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = current.ChangeRequest!;
        if (!string.Equals(actor, request.ApprovalAssignee, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("仅原计划审批负责人可审批此变更。");
        var reason = approve ? Optional(comment, 280) ?? "同意授予计划变更权限" : Required(comment, 280, "退回原因");
        var tasks = approve ? CascadeDependentTasks(ApplyScheduleChanges(current.Tasks, request.Tasks)) : current.Tasks;
        ValidatePlanTasks(tasks);
        if (approve && tasks.Any(task => current.Tasks.Any(old => old.Id == task.Id && old.Status == ProjectPlanTaskStatus.Completed
            && (task.PlannedStart != old.PlannedStart || task.PlannedFinish != old.PlannedFinish || task.Assignee != old.Assignee))))
            throw new PdmRuleException("旧版申请包含已完成任务的排期变更，请退回后重新申请变更权限。");
        var now = timeProvider.GetUtcNow();
        var decided = request with { Status = approve ? ProjectPlanApprovalStatus.Approved : ProjectPlanApprovalStatus.Rejected,
            DecidedBy = actor, DecidedAt = now, Comment = reason };
        var source = current with { ChangeRequest = decided, ChangeDraftSource = null };
        var next = current with { Tasks = tasks, PlannedStart = tasks.Min(task => task.PlannedStart), PlannedFinish = tasks.Max(task => task.PlannedFinish),
            ForecastFinish = ForecastFinish(tasks), CurrentStage = EvaluateStage(tasks, current.ManualStage, current.Stages),
            ChangeDraftSource = approve ? source : null, ChangeRequest = decided, UpdatedBy = actor, UpdatedAt = now };
        var saved = await plans.SavePlanAsync(next, current.RowVersion,
            approve ? NewVersion(source, $"批准变更权限并保留原计划：{request.Reason}", actor, now) : null, cancellationToken);
        await repository.CreateUserNotificationsAsync([new UserNotification(Guid.NewGuid(), request.SubmittedBy, "project-plan-approval",
            approve ? "计划变更权限已批准" : "计划变更权限已退回", approve ? $"{project.Code} · {reason}；请进入变更草稿编辑，完成前原计划继续生效。" : $"{project.Code} · {reason}", project.Id, null,
            $"project-plan-change-decision:{request.Id:N}", now, null)], cancellationToken);
        await AuditAsync(actor, approve ? "project-plan.change.approve" : "project-plan.change.reject", nameof(ProjectPlan), saved.Id, reason, cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> CompleteChangeAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请刷新后重新确认。");
        var source = current.ChangeDraftSource ?? throw new PdmRuleException("当前没有可完成的变更草稿。");
        var request = current.ChangeRequest ?? throw new PdmRuleException("变更申请记录不存在。");
        if (!string.Equals(actor, request.SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅变更申请人可以完成本次变更。");
        var sourceById = source.Tasks.ToDictionary(task => task.Id);
        var tasks = current.Tasks.Select(task => sourceById.TryGetValue(task.Id, out var active) ? task with
        {
            ActualStart = active.ActualStart, ActualFinish = active.ActualFinish,
            CompletionPercent = active.CompletionPercent, Status = active.Status
        } : task).ToArray();
        ValidatePlanTasks(tasks);
        var now = timeProvider.GetUtcNow();
        var nextBaseline = current.BaselineVersion + 1;
        tasks = tasks.Select(task => task with { BaselineStart = task.PlannedStart, BaselineFinish = task.PlannedFinish }).ToArray();
        var activated = current with
        {
            Tasks = tasks, PlannedStart = tasks.Min(task => task.PlannedStart), PlannedFinish = tasks.Max(task => task.PlannedFinish),
            ForecastFinish = ForecastFinish(tasks), CurrentStage = EvaluateStage(tasks, current.ManualStage, current.Stages),
            BaselineVersion = nextBaseline,
            ChangeDraftSource = null, FollowsParentPlan = false,
            ChangeRequest = request with { Comment = $"变更已完成并生效，形成基线V{nextBaseline}" }, UpdatedBy = actor, UpdatedAt = now
        };
        var saved = await SaveWithChildrenAsync(project, activated, expectedRowVersion,
            NewVersion(source, $"变更计划生效并形成基线V{nextBaseline}：{request.Reason}", actor, now), actor, role, cancellationToken);
        await repository.CreateUserNotificationsAsync([new UserNotification(Guid.NewGuid(), request.ApprovalAssignee, "project-plan-approval",
            "计划变更已完成并生效", $"{project.Code} · 基线V{nextBaseline} · {request.Reason}", project.Id, null,
            $"project-plan-change-complete:{request.Id:N}", now, null)], cancellationToken);
        await AuditAsync(actor, "project-plan.change.complete", nameof(ProjectPlan), saved.Id, $"基线V{nextBaseline}；{request.Reason}", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> AbandonChangeAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请刷新后重新确认。");
        var source = current.ChangeDraftSource ?? throw new PdmRuleException("当前没有可放弃的变更草稿。");
        var request = current.ChangeRequest ?? throw new PdmRuleException("变更申请记录不存在。");
        if (!string.Equals(actor, request.SubmittedBy, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅变更申请人可以放弃本次变更。");
        var now = timeProvider.GetUtcNow();
        var restored = source with { ChangeDraftSource = null,
            ChangeRequest = request with { Comment = "申请人已放弃本次变更" }, UpdatedBy = actor, UpdatedAt = now };
        var saved = await plans.SavePlanAsync(restored, expectedRowVersion, null, cancellationToken);
        await AuditAsync(actor, "project-plan.change.abandon", nameof(ProjectPlan), saved.Id, $"{project.Code}；{request.Reason}", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> SubmitApprovalAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireManageAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划已被修改，请刷新后重新提交。");
        if (current.ApprovalStatus is ProjectPlanApprovalStatus.Approved or ProjectPlanApprovalStatus.Pending) throw new PdmRuleException("仅草稿或已退回的首版计划可以提交审批。");
        ValidatePlanTasks(current.Tasks);
        var assignee = await ResolveApprovalAssigneeAsync(project, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var saved = await plans.SavePlanAsync(current with
        {
            ApprovalStatus = ProjectPlanApprovalStatus.Pending, ApprovalAssignee = assignee,
            SubmittedBy = actor, SubmittedAt = now, ApprovalComment = null, UpdatedBy = actor, UpdatedAt = now
        }, expectedRowVersion, null, cancellationToken);
        await repository.CreateUserNotificationsAsync([new UserNotification(Guid.NewGuid(), assignee, "project-plan-approval", "首版项目计划待审批",
            $"{project.Code} · {project.Name}，请进入项目计划审阅并批准或退回。", project.Id, null, $"project-plan-approval:{saved.Id:N}:{saved.RowVersion}", now, null)], cancellationToken);
        await AuditAsync(actor, "project-plan.approval.submit", nameof(ProjectPlan), saved.Id, $"{project.Code}；审批人{assignee}", cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan> DecideApprovalAsync(Guid projectId, long expectedRowVersion, bool approve, string? comment, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireReadProjectAsync(projectId, actor, role, cancellationToken);
        var current = await plans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目计划不存在。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划已被修改，请刷新后重新审阅。");
        if (current.ApprovalStatus == ProjectPlanApprovalStatus.Approved && current.ChangeRequest?.Status == ProjectPlanApprovalStatus.Pending)
            return await DecideChangeAsync(project, current, approve, comment, actor, role, cancellationToken);
        if (current.ApprovalStatus != ProjectPlanApprovalStatus.Pending) throw new PdmRuleException("当前计划不在待审批状态。");
        var assignee = await ResolveApprovalAssigneeAsync(project, cancellationToken);
        if (!string.Equals(actor, assignee, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actor, current.ApprovalAssignee, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("仅当前项目执行事业部的主负责人可以审批首版计划。负责人变更后请修改草稿并重新提交。");
        var reason = approve ? Optional(comment, 280) ?? "批准首版计划" : Required(comment, 280, "退回原因");
        var now = timeProvider.GetUtcNow();
        var saved = await plans.SavePlanAsync(current with
        {
            ApprovalStatus = approve ? ProjectPlanApprovalStatus.Approved : ProjectPlanApprovalStatus.Rejected,
            ApprovedBy = approve ? actor : null, ApprovedAt = approve ? now : null, ApprovalComment = reason,
            BaselineVersion = approve ? 1 : current.BaselineVersion,
            Tasks = approve ? current.Tasks.Select(item => item with { BaselineStart = item.PlannedStart, BaselineFinish = item.PlannedFinish }).ToArray() : current.Tasks,
            UpdatedBy = actor, UpdatedAt = now
        }, expectedRowVersion, NewVersion(current, approve ? "批准首版计划并冻结基线V1" : $"退回首版计划：{reason}", actor, now), cancellationToken);
        await repository.CreateUserNotificationsAsync([new UserNotification(Guid.NewGuid(), current.SubmittedBy!, "project-plan-approval",
            approve ? "首版项目计划已生效" : "首版项目计划已退回", $"{project.Code} · {reason}", project.Id, null,
            $"project-plan-decision:{saved.Id:N}:{saved.RowVersion}", now, null)], cancellationToken);
        await AuditAsync(actor, approve ? "project-plan.approval.approve" : "project-plan.approval.reject", nameof(ProjectPlan), saved.Id, $"{project.Code}；{reason}", cancellationToken);
        if (approve)
        {
            try
            {
                await SyncReleasedBomProgressAsync(projectId, cancellationToken);
                return (await plans.FindPlanAsync(projectId, cancellationToken))!;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                await AuditAsync(actor, "project-plan.bom-release-failed", nameof(ProjectPlan), saved.Id, $"计划已生效，BOM进度待后台重试：{exception.Message}", cancellationToken);
            }
        }
        return saved;
    }

    private async Task<string> ResolveApprovalAssigneeAsync(Project project, CancellationToken cancellationToken)
    {
        var root = project.ParentProjectId is Guid parentId
            ? await repository.FindProjectAsync(project.RootProjectId ?? parentId, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。")
            : project;
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.FirstOrDefault(item => item.Id == root.ExecutionUnitId && item.IsActive && item.Kind == OrganizationUnitKind.BusinessDivision);
        var assignee = unit is null ? null : directory.Managers.FirstOrDefault(item => item.UnitId == unit.Id)?.PrimaryManager;
        if (string.IsNullOrWhiteSpace(assignee) || !directory.Users.Any(item => item.IsActive && string.Equals(item.Username, assignee, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("请先配置主项目执行事业部及其启用的主负责人（事业部总经理），再提交计划审批。");
        return assignee;
    }

    private static void RequireEffective(ProjectPlan plan)
    {
        if (plan.ApprovalStatus != ProjectPlanApprovalStatus.Approved) throw new PdmRuleException("首版计划须经事业部总经理批准生效后才能执行或设置基线。");
    }

    private async Task<IReadOnlyList<ProjectPlanTemplate>> EnsureDefaultTemplateAsync(bool includeInactive, string actor, CancellationToken cancellationToken)
    {
        var templates = await plans.ListTemplatesAsync(includeInactive, cancellationToken);
        if (templates.Count > 0) return templates;
        await plans.SaveTemplateAsync(DefaultTemplate(actor, timeProvider.GetUtcNow()), null, cancellationToken);
        return await plans.ListTemplatesAsync(includeInactive, cancellationToken);
    }

    private static ProjectPlanTemplate DefaultTemplate(string actor, DateTimeOffset now)
    {
        ProjectPlanTemplateTask Task(int order, string name, string stage, decimal ratio, int[] predecessors, string role, decimal weight, bool milestone = false) =>
            new(Guid.NewGuid(), name, stage, ratio, predecessors, role, weight, milestone, true, order);
        return new ProjectPlanTemplate(Guid.NewGuid(), "专业设备交付标准模板", null, true,
        [
            Task(10, "项目启动会", ProjectPlanStage.Design, 0, [], "ProjectManager", 2) with { FixedDurationDays = 1 },
            Task(20, "机械设计与内部评审", ProjectPlanStage.Design, .6m, [], "DesignLead", 15),
            Task(30, "电气设计与图纸", ProjectPlanStage.Design, .6m, [], "Designer", 10),
            Task(40, "BOM与长交期物料下发", ProjectPlanStage.Design, .2m, [20, 30], "DesignLead", 8),
            Task(50, "标准件采购", ProjectPlanStage.MaterialPreparation, 1, [], "ProjectManager", 14),
            Task(60, "加工件采购", ProjectPlanStage.MaterialPreparation, 1, [], "ProjectManager", 14),
            Task(70, "设备装配", ProjectPlanStage.Assembly, 1, [], "ProjectManager", 12),
            Task(80, "厂内调试", ProjectPlanStage.Commissioning, 1, [], "ProjectManager", 10),
            Task(90, "验收发货", ProjectPlanStage.FinalAcceptance, 1, [], "ProjectManager", 2),
            Task(100, "客户端调试", ProjectPlanStage.ClientCommissioning, 1, [], "ProjectManager", 6),
            Task(110, "验收推进与终验收", ProjectPlanStage.AcceptanceProgress, 1, [], "ProjectManager", 7)
        ], actor, now, actor, now, 0) { Stages = [
            new(ProjectPlanStage.Design, "设计") { ParticipatesInDelivery = true, DurationRatio = .3m, ProgressRatio = .3m },
            new(ProjectPlanStage.MaterialPreparation, "备料") { ParticipatesInDelivery = true, DurationRatio = .3m, ProgressRatio = .2m },
            new(ProjectPlanStage.Assembly, "装配") { ParticipatesInDelivery = true, DurationRatio = .2m, ProgressRatio = .25m },
            new(ProjectPlanStage.Commissioning, "调试") { ParticipatesInDelivery = true, DurationRatio = .1m, ProgressRatio = .15m },
            new(ProjectPlanStage.FinalAcceptance, "验收发货") { ParticipatesInDelivery = true, DurationRatio = .1m, ProgressRatio = .1m },
            new(ProjectPlanStage.ClientCommissioning, "客户端调试") { ParticipatesInDelivery = false, IndependentDurationDays = 15 },
            new(ProjectPlanStage.AcceptanceProgress, "验收推进") { ParticipatesInDelivery = false, IndependentDurationDays = 15 }
        ] };
    }

    private static ProjectPlanTemplate SelectStagesToGenerate(ProjectPlanTemplate template, IReadOnlyList<string>? deferredStages)
    {
        var deferred = (deferredStages ?? []).ToHashSet();
        if (deferred.Count != (deferredStages?.Count ?? 0) || deferred.Any(code => !template.Stages.Any(stage => stage.Code == code && stage.ParticipatesInDelivery == false)))
            throw new PdmRuleException("暂不建立仅适用于交付后阶段，且不能重复或包含未知阶段。");
        var skipped = false;
        foreach (var stage in template.Stages.Where(stage => stage.ParticipatesInDelivery == false))
        {
            if (deferred.Contains(stage.Code)) skipped = true;
            else if (skipped) throw new PdmRuleException("交付后阶段连续推进，前一阶段暂不建立时，后续阶段也须暂不建立。");
        }
        var tasks = template.Tasks.Where(task => !deferred.Contains(task.Stage)).ToArray();
        var includedOrders = tasks.Select(task => task.SortOrder).ToHashSet();
        if (tasks.Any(task => task.PredecessorSortOrders.Any(order => !includedOrders.Contains(order))))
            throw new PdmRuleException("保留任务依赖暂不建立阶段中的任务，请先调整模板前置关系，不能忽略依赖。");
        return template with { Stages = template.Stages.Where(stage => !deferred.Contains(stage.Code)).ToArray(), Tasks = tasks };
    }

    private static IReadOnlyList<ProjectPlanStageSchedule> AllocateStageSchedules(IReadOnlyList<ProjectPlanStageDefinition> stages, DateOnly start, int totalDays, IReadOnlyList<ProjectPlanStageSchedule>? independentStages)
    {
        var overrides = independentStages ?? [];
        if (overrides.GroupBy(item => item.Stage).Any(group => group.Count() > 1)
            || overrides.Any(item => !stages.Any(stage => stage.Code == item.Stage && stage.ParticipatesInDelivery == false) || item.DurationDays is < 1 or > 3650))
            throw new PdmRuleException("交付后阶段排期无效，请检查阶段、工期及重复配置（暂不建立阶段不能同时提交排期）。");
        var windows = new Dictionary<string, (DateOnly Start, int Days)>();
        decimal cumulative = 0;
        var allocated = 0;
        var nextPostDeliveryStart = start.AddDays(totalDays);
        var firstPostDeliveryStage = true;
        foreach (var stage in stages)
        {
            if (stage.ParticipatesInDelivery == true)
            {
                cumulative += stage.DurationRatio;
                var boundary = (int)Math.Round(totalDays * cumulative, MidpointRounding.AwayFromZero);
                var days = boundary - allocated;
                if (days < 1) throw new PdmRuleException($"交付总工期过短，阶段“{stage.Name}”不足1天，请增加总工期或调整阶段比例。");
                windows[stage.Code] = (start.AddDays(allocated), days);
                allocated = boundary;
            }
            else
            {
                var custom = overrides.FirstOrDefault(item => item.Stage == stage.Code);
                var stageStart = custom?.StartDate ?? nextPostDeliveryStart;
                if (firstPostDeliveryStage ? stageStart < nextPostDeliveryStart : stageStart != nextPostDeliveryStart)
                    throw new PdmRuleException(firstPostDeliveryStage ? "客户端调试等交付后阶段不能早于交付完成次日。" : $"阶段“{stage.Name}”须接在上一交付后阶段完成次日开始。");
                var days = custom?.DurationDays ?? stage.IndependentDurationDays;
                windows[stage.Code] = (stageStart, days);
                nextPostDeliveryStart = stageStart.AddDays(days);
                firstPostDeliveryStage = false;
            }
        }
        return windows.Select(item => new ProjectPlanStageSchedule(item.Key, item.Value.Start, item.Value.Days)).ToArray();
    }

    private static IReadOnlyList<ProjectPlanTask> Schedule(ProjectPlanTemplate template, DateOnly start, int totalDays, Project project, IReadOnlyList<ProjectPlanStageSchedule>? independentStages)
    {
        var windows = AllocateStageSchedules(template.Stages, start, totalDays, independentStages)
            .ToDictionary(item => item.Stage, item => (Start: item.StartDate, Days: item.DurationDays));
        var result = new List<ProjectPlanTask>();
        var scheduled = new Dictionary<int, ProjectPlanTask>();
        var sources = template.Tasks.ToDictionary(item => item.SortOrder);
        var visiting = new HashSet<int>();
        ProjectPlanTask ScheduleTask(ProjectPlanTemplateTask source)
        {
            if (scheduled.TryGetValue(source.SortOrder, out var existing)) return existing;
            if (!visiting.Add(source.SortOrder)) throw new PdmRuleException("模板任务存在循环前置依赖。");
            var predecessors = source.PredecessorSortOrders.Select(order => sources.TryGetValue(order, out var predecessor)
                ? ScheduleTask(predecessor) : throw new PdmRuleException($"模板任务“{source.Name}”的前置任务不存在。")).ToArray();
            var window = windows[source.Stage];
            var taskStart = window.Start.AddDays(source.StartOffsetDays);
            if (predecessors.Length > 0) taskStart = taskStart > predecessors.Max(item => item.PlannedFinish)
                ? taskStart : predecessors.Max(item => item.PlannedFinish).AddDays(1);
            var duration = source.IsMilestone ? 0 : source.FixedDurationDays ?? Math.Max(1, (int)Math.Floor(window.Days * source.DurationRatio));
            var finish = source.IsMilestone ? taskStart : taskStart.AddDays(duration - 1);
            if (finish > window.Start.AddDays(window.Days - 1)) throw new PdmRuleException($"任务“{source.Name}”超出所属阶段的分配工期，请调整阶段工期、任务固定天数/比例、开始偏移或前置关系（并行任务不必设置前置）。");
            var id = Guid.NewGuid();
            var task = new ProjectPlanTask(id, source.Name, source.Stage, ResolveAssignee(source.DefaultAssigneeRole, project), duration, taskStart, finish, null, null, null, null, 0,
                ProjectPlanTaskStatus.NotStarted, predecessors.Select(item => item.Id).ToArray(), source.Weight, source.IsMilestone, source.IsRequired, source.SortOrder) { TemplateTaskId = source.Id };
            scheduled.Add(source.SortOrder, task);
            visiting.Remove(source.SortOrder);
            return task;
        }
        foreach (var source in template.Tasks.OrderBy(item => item.SortOrder)) result.Add(ScheduleTask(source));
        return result;
    }

    private static string? ResolveAssignee(string? role, Project project) => role switch
    {
        "DesignLead" => project.DesignLead ?? project.DesignLeads.FirstOrDefault() ?? project.Designers.FirstOrDefault() ?? project.PrimaryProjectManager,
        "Designer" => project.Designers.FirstOrDefault() ?? project.DesignLead ?? project.PrimaryProjectManager,
        _ => project.PrimaryProjectManager ?? project.CollaborativeProjectManagers.FirstOrDefault() ?? project.Owner
    };

    private static string? ResolveAssigneeForStage(string stage, Project project) => stage == ProjectPlanStage.Design
        ? project.DesignLead ?? project.DesignLeads.FirstOrDefault() ?? project.Designers.FirstOrDefault() ?? project.PrimaryProjectManager ?? project.Owner
        : project.PrimaryProjectManager ?? project.CollaborativeProjectManagers.FirstOrDefault() ?? project.Owner;

    private static IReadOnlyList<ProjectPlanTask> NormalizeTasks(IReadOnlyList<ProjectPlanTask> tasks) => tasks.OrderBy(item => item.SortOrder).Select(item => item with
    {
        Name = Required(item.Name, 160, "任务名称"),
        DurationDays = item.IsMilestone ? 0 : item.PlannedFinish.DayNumber - item.PlannedStart.DayNumber + 1,
        CompletionPercent = Math.Clamp(item.CompletionPercent, 0, 100),
        Status = item.CompletionPercent switch { 0 => ProjectPlanTaskStatus.NotStarted, 100 => ProjectPlanTaskStatus.Completed, _ => ProjectPlanTaskStatus.InProgress },
        Weight = Math.Round(item.Weight, 4)
    }).ToArray();

    private static void ValidateTemplateTasks(IReadOnlyList<ProjectPlanTemplateTask> tasks)
    {
        if (tasks.Count == 0) throw new PdmRuleException("计划模板至少需要一个任务。");
        if (tasks.Count > 200) throw new PdmRuleException("计划模板最多包含200个任务。");
        if (tasks.GroupBy(item => item.SortOrder).Any(group => group.Count() > 1)) throw new PdmRuleException("模板任务排序号不能重复。");
        if (tasks.Where(item => item.Id != Guid.Empty).GroupBy(item => item.Id).Any(group => group.Count() > 1)) throw new PdmRuleException("模板任务标识不能重复。");
        foreach (var task in tasks)
        {
            if (task.DurationRatio < 0 || task.DurationRatio > 1) throw new PdmRuleException("任务工期比例必须在0至1之间。");
            if (task.Weight < 0) throw new PdmRuleException("任务权重不能为负数。");
            if (task.StartOffsetDays is < 0 or > 3650) throw new PdmRuleException("任务开始偏移必须为0至3650天。");
            if (task.FixedDurationDays is < 1 or > 3650) throw new PdmRuleException("固定工期必须为1至3650天；里程碑按0天计算。");
            if (task.PredecessorSortOrders.Contains(task.SortOrder)) throw new PdmRuleException("任务不能将自身设为前置任务。");
            if (task.PredecessorSortOrders.Any(order => !tasks.Any(item => item.SortOrder == order))) throw new PdmRuleException("前置任务序号不存在。");
        }
        var byOrder = tasks.ToDictionary(item => item.SortOrder);
        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();
        void Visit(int order)
        {
            if (visited.Contains(order)) return;
            if (!visiting.Add(order)) throw new PdmRuleException("模板任务存在循环前置依赖。");
            foreach (var predecessor in byOrder[order].PredecessorSortOrders) Visit(predecessor);
            visiting.Remove(order);
            visited.Add(order);
        }
        foreach (var task in tasks) Visit(task.SortOrder);
    }

    private static void ValidatePlanTasks(IReadOnlyList<ProjectPlanTask> tasks)
    {
        if (tasks.Count == 0) throw new PdmRuleException("计划至少需要一个任务。");
        if (tasks.GroupBy(item => item.Id).Any(group => group.Count() > 1)) throw new PdmRuleException("计划任务标识不能重复。");
        var ids = tasks.Select(item => item.Id).ToHashSet();
        foreach (var task in tasks)
        {
            if (task.Weight < 0) throw new PdmRuleException("任务权重不能为负数。");
            if (task.PlannedFinish < task.PlannedStart) throw new PdmRuleException($"任务“{task.Name}”的计划完成日期不能早于开始日期。");
            if (task.PredecessorTaskIds.Any(id => !ids.Contains(id))) throw new PdmRuleException($"任务“{task.Name}”存在无效前置任务。");
        }
    }

    private static ProjectPlanTask[] CascadeDependentTasks(IReadOnlyList<ProjectPlanTask> tasks)
    {
        var byId = tasks.ToDictionary(task => task.Id);
        var resolved = new Dictionary<Guid, ProjectPlanTask>();
        var visiting = new HashSet<Guid>();
        ProjectPlanTask Resolve(ProjectPlanTask task)
        {
            if (resolved.TryGetValue(task.Id, out var existing)) return existing;
            if (!visiting.Add(task.Id)) throw new PdmRuleException("计划任务存在循环前置依赖。");
            var predecessors = task.PredecessorTaskIds.Select(id => Resolve(byId[id])).ToArray();
            var result = task;
            if (predecessors.Length > 0)
            {
                var earliestStart = predecessors.Max(item => item.PlannedFinish).AddDays(1);
                if (result.PlannedStart < earliestStart)
                {
                    var duration = result.IsMilestone ? 0 : result.PlannedFinish.DayNumber - result.PlannedStart.DayNumber + 1;
                    result = result with
                    {
                        PlannedStart = earliestStart,
                        PlannedFinish = result.IsMilestone ? earliestStart : earliestStart.AddDays(duration - 1),
                        DurationDays = duration
                    };
                }
            }
            visiting.Remove(task.Id);
            resolved.Add(task.Id, result);
            return result;
        }
        return tasks.Select(Resolve).ToArray();
    }

    private static string EvaluateStage(IReadOnlyList<ProjectPlanTask> tasks, string? manualStage, IReadOnlyList<ProjectPlanStageDefinition> stages)
    {
        if (manualStage is not null) return manualStage;
        foreach (var stage in stages)
        {
            if (tasks.Any(item => item.IsRequired && item.Stage == stage.Code && item.Status != ProjectPlanTaskStatus.Completed)) return stage.Code;
        }
        return stages.Last().Code;
    }

    private DateOnly ForecastFinish(IReadOnlyList<ProjectPlanTask> tasks)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().LocalDateTime);
        return tasks.Max(item => item.Status == ProjectPlanTaskStatus.Completed ? item.ActualFinish ?? item.PlannedFinish : item.PlannedFinish < today ? today.AddDays(item.PlannedFinish.DayNumber - item.PlannedStart.DayNumber) : item.PlannedFinish);
    }

    private static int CompletionPercent(IReadOnlyList<ProjectPlanTask> tasks, IReadOnlyList<ProjectPlanStageDefinition> stages)
    {
        if (stages.All(stage => stage.ParticipatesInDelivery is not null))
            return (int)Math.Round(stages.Where(stage => stage.ParticipatesInDelivery == true).Sum(stage =>
            {
                var children = tasks.Where(task => task.Stage == stage.Code).ToArray();
                var weight = children.Sum(task => task.Weight);
                return weight > 0 ? stage.ProgressRatio * children.Sum(task => task.Weight * task.CompletionPercent) / weight : 0;
            }), MidpointRounding.AwayFromZero);
        var totalWeight = tasks.Sum(item => item.Weight > 0 ? item.Weight : 1);
        return totalWeight <= 0 ? 0 : (int)Math.Round(tasks.Sum(item => (item.Weight > 0 ? item.Weight : 1) * item.CompletionPercent) / totalWeight);
    }

    private static IReadOnlyList<ProjectPlanStageDefinition> ValidateStages(IReadOnlyList<ProjectPlanStageDefinition> stages, IEnumerable<string> taskStages)
    {
        if (stages.Count is < 1 or > 50) throw new PdmRuleException("请配置1至50个项目阶段。");
        var normalized = stages.Select(item => item with { Code = Required(item.Code, 40, "阶段标识"), Name = Required(item.Name, 60, "阶段名称") }).ToArray();
        if (normalized.Select(item => item.Code).Distinct().Count() != normalized.Length || normalized.Select(item => item.Name).Distinct().Count() != normalized.Length)
            throw new PdmRuleException("阶段名称或标识不能重复。");
        if (normalized.Any(item => item.Code is ProjectPlanStage.Paused or ProjectPlanStage.Cancelled or ProjectPlanStage.Terminated)) throw new PdmRuleException("暂停、取消、终止为保留的例外状态。");
        if (taskStages.Any(code => !normalized.Any(item => item.Code == code))) throw new PdmRuleException("任务引用了未配置的阶段，请先调整任务阶段再删除阶段。");
        return normalized;
    }

    private static void ValidateAllocation(IReadOnlyList<ProjectPlanStageDefinition> stages, IReadOnlyList<ProjectPlanTemplateTask> tasks)
    {
        if (stages.Any(stage => stage.ParticipatesInDelivery is null)) throw new PdmRuleException("请管理员在项目计划模板中补齐大阶段工期占比、进度占比及交付后排期配置。");
        var delivery = stages.Where(stage => stage.ParticipatesInDelivery == true).ToArray();
        if (delivery.Length == 0 || delivery.Sum(stage => stage.DurationRatio) != 1 || delivery.Sum(stage => stage.ProgressRatio) != 1)
            throw new PdmRuleException("交付阶段工期占比和进度占比必须分别合计100%。");
        foreach (var stage in stages)
        {
            if (stage.ParticipatesInDelivery == true && (stage.DurationRatio <= 0 || stage.DurationRatio > 1 || stage.ProgressRatio <= 0 || stage.ProgressRatio > 1))
                throw new PdmRuleException($"阶段“{stage.Name}”的工期占比和进度占比必须大于0且不超过100%。");
            if (stage.ParticipatesInDelivery == false && (stage.IndependentDurationDays is < 1 or > 3650 || stage.DurationRatio != 0 || stage.ProgressRatio != 0))
                throw new PdmRuleException($"交付后阶段“{stage.Name}”须设置1至3650天工期，且不占用交付工期和进度比例。");
            if (tasks.Where(task => task.Stage == stage.Code).Sum(task => task.Weight) <= 0)
                throw new PdmRuleException($"阶段“{stage.Name}”的子任务总权重必须大于0。");
        }
    }

    private static ProjectPlan EffectivePlan(ProjectPlan plan) => plan.ChangeDraftSource ?? plan;

    private static ProjectPlan VisiblePlan(ProjectPlan plan, string actor)
    {
        if (plan.ChangeDraftSource is null || string.Equals(plan.ChangeRequest?.SubmittedBy, actor, StringComparison.OrdinalIgnoreCase)) return plan;
        return plan.ChangeDraftSource with
        {
            RowVersion = plan.RowVersion, ChangeRequest = plan.ChangeRequest, ChangeDraftSource = null,
            UpdatedBy = plan.UpdatedBy, UpdatedAt = plan.UpdatedAt
        };
    }

    private static ProjectPlanVersion NewVersion(ProjectPlan snapshot, string reason, string actor, DateTimeOffset now) =>
        new(Guid.NewGuid(), snapshot.Id, 0, reason, snapshot with { ChangeDraftSource = null }, actor, now);

    private static ProjectPlan? WithTaskBounds(ProjectPlan? plan)
        => plan is null || plan.Tasks.Count == 0 ? plan : plan with
        {
            PlannedStart = plan.Tasks.Min(task => task.PlannedStart),
            PlannedFinish = plan.Tasks.Max(task => task.PlannedFinish)
        };

    private async Task<Project> RequireReadProjectAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireReadAsync(projectId, actor, role, cancellationToken);
        return await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
    }

    private async Task RequireReadAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken)) throw new UnauthorizedAccessException("无权查看该项目计划。");
    }

    private async Task<Project> RequireManageAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = await RequireReadProjectAsync(projectId, actor, role, cancellationToken);
        if (!CanManage(project, actor, role)) throw new UnauthorizedAccessException("仅该项目的主项目经理或开发者可以维护项目计划。");
        return project;
    }

    private static bool CanManage(Project project, string actor, UserRole role) =>
        TenantContext.Current?.HasRole("developer") == true
        || string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase);

    private static bool CanManageTemplates(UserRole role) => TenantContext.Current is { } tenant
        ? tenant.HasRole("developer") || tenant.HasRole("platform_admin") || tenant.HasRole(nameof(UserRole.Administrator))
        : role is UserRole.Administrator or UserRole.PlatformAdministrator;

    private static string Required(string? value, int maxLength, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new PdmRuleException($"{field}必须为1至{maxLength}个字符。");
        return normalized;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Length > maxLength) throw new PdmRuleException($"内容不能超过{maxLength}个字符。");
        return normalized;
    }

    private Task AuditAsync(string actor, string action, string entityType, Guid id, string details, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, id.ToString(), details), cancellationToken);
}

internal static class ProjectPlanningEnumerableExtensions
{
    public static DateOnly? MinOrDefault<T>(this IEnumerable<T> source, Func<T, DateOnly?> selector)
    {
        var values = source.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    public static DateOnly? MaxOrDefault<T>(this IEnumerable<T> source, Func<T, DateOnly?> selector)
    {
        var values = source.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }
}
