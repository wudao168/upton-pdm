using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryValidationPlanRepository : IValidationPlanRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ValidationCheckCategory> categories = [];
    private readonly Dictionary<Guid, ValidationCheckItem> items = [];
    private readonly Dictionary<Guid, ProjectValidationPlan> plans = [];
    private readonly Dictionary<Guid, ValidationPlanExecutionRecord> executionRecords = [];

    public Task<ValidationCheckCatalog> ListCatalogAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var visibleCategories = categories.Values.Where(item => includeInactive || item.IsActive).OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArray();
            var categoryIds = visibleCategories.Select(item => item.Id).ToHashSet();
            var visibleItems = items.Values.Where(item => categoryIds.Contains(item.CategoryId) && (includeInactive || item.IsActive)).OrderBy(item => item.SortOrder).ThenBy(item => item.Content).ToArray();
            return Task.FromResult(new ValidationCheckCatalog(visibleCategories, visibleItems));
        }
    }

    public Task<ValidationCheckCategory> CreateCategoryAsync(ValidationCheckCategory category, CancellationToken cancellationToken)
    {
        lock (gate) { categories.Add(category.Id, category); return Task.FromResult(category); }
    }

    public Task<ValidationCheckCategory> UpdateCategoryAsync(ValidationCheckCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = categories.GetValueOrDefault(category.Id) ?? throw new PdmNotFoundException("验证分类不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("验证分类已被其他用户修改，请刷新后重试。");
            var saved = category with { RowVersion = expectedRowVersion + 1 };
            categories[category.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = categories.GetValueOrDefault(categoryId) ?? throw new PdmNotFoundException("验证分类不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("验证分类已被其他用户修改，请刷新后重试。");
            categories.Remove(categoryId);
            return Task.CompletedTask;
        }
    }

    public Task<ValidationCheckItem> CreateItemAsync(ValidationCheckItem item, CancellationToken cancellationToken)
    {
        lock (gate) { items.Add(item.Id, item); RefreshCounts(); return Task.FromResult(items[item.Id]); }
    }

    public Task<ValidationCheckItem> UpdateItemAsync(ValidationCheckItem item, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = items.GetValueOrDefault(item.Id) ?? throw new PdmNotFoundException("检查项不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("检查项已被其他用户修改，请刷新后重试。");
            items[item.Id] = item with { RowVersion = expectedRowVersion + 1 };
            RefreshCounts();
            return Task.FromResult(items[item.Id]);
        }
    }

    public Task DeleteItemAsync(Guid itemId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = items.GetValueOrDefault(itemId) ?? throw new PdmNotFoundException("检查项不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("检查项已被其他用户修改，请刷新后重试。");
            items.Remove(itemId);
            RefreshCounts();
            return Task.CompletedTask;
        }
    }

    public Task<ProjectValidationPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) { return Task.FromResult(plans.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.RevisionNumber).FirstOrDefault()); }
    }

    public Task<ProjectValidationPlan?> FindPlanByIdAsync(Guid planId, CancellationToken cancellationToken)
    {
        lock (gate) { return Task.FromResult(plans.GetValueOrDefault(planId)); }
    }

    public Task<ProjectValidationPlan> SavePlanAsync(ProjectValidationPlan plan, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = plans.GetValueOrDefault(plan.Id);
            if (current is not null && current.RowVersion != expectedRowVersion) throw new PdmConflictException("验证计划已被其他用户修改，请刷新后重试。");
            if (current is null && expectedRowVersion is not null) throw new PdmConflictException("验证计划状态已变化，请刷新后重试。");
            var saved = plan with { RowVersion = current is null ? 1 : current.RowVersion + 1 };
            plans[plan.Id] = saved;
            foreach (var item in items.Values.ToArray())
            {
                var references = plans.Values.SelectMany(value => value.Items).Count(value => value.CatalogItemId == item.Id);
                items[item.Id] = item with { ReferenceCount = references };
            }
            RefreshCounts();
            return Task.FromResult(saved);
        }
    }

    public Task<ProjectValidationPlan> CreateRevisionAsync(ProjectValidationPlan plan, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (plans.Values.Any(item => item.ProjectId == plan.ProjectId && item.RevisionNumber == plan.RevisionNumber))
                throw new PdmConflictException("验证计划版本已存在，请刷新后重试。");
            plans.Add(plan.Id, plan);
            return Task.FromResult(plan);
        }
    }

    public Task<ProjectValidationPlan> SubmitAsync(Guid planId, long expectedRowVersion, string workflowCode, int workflowVersion, IReadOnlyList<ValidationPlanApprovalTask> tasks, string actor, DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = RequirePlan(planId);
            if (current.RowVersion != expectedRowVersion || current.State is not (ProjectValidationPlanState.Draft or ProjectValidationPlanState.Rejected))
                throw new PdmConflictException("验证计划状态已变化，请刷新后重试。");
            var saved = current with
            {
                State = ProjectValidationPlanState.PendingApproval,
                ApprovalTasks = tasks,
                UpdatedBy = actor,
                UpdatedAt = submittedAt,
                RowVersion = current.RowVersion + 1,
                WorkflowCode = workflowCode,
                WorkflowVersion = workflowVersion,
                SubmittedBy = actor,
                SubmittedAt = submittedAt,
                EffectiveBy = null,
                EffectiveAt = null
            };
            plans[planId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<ProjectValidationPlan> DecideAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = plans.Values.FirstOrDefault(plan => plan.ApprovalTasks.Any(task => task.Id == taskId))
                ?? throw new PdmNotFoundException("验证计划审批任务不存在。");
            if (current.State != ProjectValidationPlanState.PendingApproval) throw new PdmConflictException("验证计划已不在审批中。");
            var activeTask = current.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault(task => task.Decision is null);
            if (activeTask?.Id != taskId) throw new PdmConflictException("当前尚未到达该审批节点。");
            var tasks = current.ApprovalTasks.Select(task => task.Id == taskId
                ? task with { Decision = decision, DecisionBy = actor, DecisionComment = comment, DecidedAt = decidedAt }
                : task).ToArray();
            var finalApproved = decision == ApprovalDecision.Approved && tasks.All(task => task.Decision == ApprovalDecision.Approved);
            if (finalApproved)
            {
                foreach (var older in plans.Values.Where(plan => plan.ProjectId == current.ProjectId && plan.State == ProjectValidationPlanState.Effective).ToArray())
                    plans[older.Id] = older with { State = ProjectValidationPlanState.Superseded, RowVersion = older.RowVersion + 1 };
            }
            var saved = current with
            {
                ApprovalTasks = tasks,
                State = decision == ApprovalDecision.Rejected ? ProjectValidationPlanState.Rejected : finalApproved ? ProjectValidationPlanState.Effective : ProjectValidationPlanState.PendingApproval,
                UpdatedBy = actor,
                UpdatedAt = decidedAt,
                RowVersion = current.RowVersion + 1,
                EffectiveBy = finalApproved ? actor : null,
                EffectiveAt = finalApproved ? decidedAt : null
            };
            plans[current.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<IReadOnlyList<PendingValidationPlanApprovalTask>> ListPendingApprovalTasksAsync(string actor, bool includeAll, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var result = plans.Values.Where(plan => plan.State == ProjectValidationPlanState.PendingApproval)
                .Select(plan => (Plan: plan, Task: plan.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault(task => task.Decision is null)))
                .Where(item => item.Task is not null && (includeAll || string.Equals(item.Task.Assignee, actor, StringComparison.OrdinalIgnoreCase)))
                .Select(item => new PendingValidationPlanApprovalTask(item.Task!.Id, item.Plan.Id, item.Plan.ProjectId, item.Plan.RevisionNumber, item.Task.Stage, item.Task.StepName, item.Task.Assignee, item.Task.CreatedAt))
                .OrderBy(item => item.CreatedAt).ToArray();
            return Task.FromResult<IReadOnlyList<PendingValidationPlanApprovalTask>>(result);
        }
    }

    public Task<ValidationPlanAttachment> AddAttachmentAsync(ValidationPlanAttachment attachment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var plan = RequirePlan(attachment.PlanId);
            var saved = plan with { Attachments = plan.Attachments.Append(attachment).ToArray(), RowVersion = plan.RowVersion + 1 };
            plans[plan.Id] = saved;
            return Task.FromResult(attachment);
        }
    }

    public Task<IReadOnlyList<ValidationPlanExecutionRecord>> ListExecutionRecordsAsync(Guid planId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ValidationPlanExecutionRecord>>(executionRecords.Values
                .Where(item => item.PlanId == planId).OrderByDescending(item => item.ConfirmedAt).ToArray());
        }
    }

    public Task<ValidationPlanExecutionRecord> AddExecutionRecordAsync(ValidationPlanExecutionRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            RequirePlan(record.PlanId);
            if (executionRecords.ContainsKey(record.Id)) throw new PdmConflictException("验证执行记录已存在。");
            executionRecords.Add(record.Id, record);
            return Task.FromResult(record);
        }
    }

    private ProjectValidationPlan RequirePlan(Guid planId) => plans.GetValueOrDefault(planId) ?? throw new PdmNotFoundException("验证计划不存在。");

    private void RefreshCounts()
    {
        foreach (var category in categories.Values.ToArray())
        {
            categories[category.Id] = category with
            {
                ItemCount = items.Values.Count(item => item.CategoryId == category.Id),
                ReferenceCount = plans.Values.SelectMany(plan => plan.Items).Count(item => item.CatalogCategoryId == category.Id)
            };
        }
    }
}
