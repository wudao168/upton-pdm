using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectPlanningRepository : IProjectPlanningRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ProjectPlanTemplate> templates = [];
    private readonly Dictionary<Guid, ProjectPlan> plans = [];
    private readonly List<ProjectPlanVersion> versions = [];

    public Task<IReadOnlyList<ProjectPlanTemplate>> ListTemplatesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProjectPlanTemplate>>(templates.Values.Where(item => includeInactive || item.IsActive).OrderBy(item => item.Name).ToArray());
    }

    public Task<ProjectPlanTemplate?> FindTemplateAsync(Guid templateId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(templates.GetValueOrDefault(templateId));
    }

    public Task<ProjectPlanTemplate> SaveTemplateAsync(ProjectPlanTemplate template, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = templates.GetValueOrDefault(template.Id);
            if (current is null && expectedRowVersion is not null) throw new PdmConflictException("计划模板已不存在，请刷新后重试。");
            if (current is not null && current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划模板已被其他用户修改，请刷新后重试。");
            var saved = template with { RowVersion = (current?.RowVersion ?? 0) + 1 };
            templates[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<ProjectPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken, bool includeDeleted = false)
    {
        lock (gate) return Task.FromResult(plans.Values.FirstOrDefault(item => item.ProjectId == projectId && (includeDeleted || !item.IsDeleted)));
    }

    public Task<IReadOnlyList<ProjectPlan>> ListPlansAsync(IReadOnlyCollection<Guid>? projectIds, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var filter = projectIds?.ToHashSet();
            return Task.FromResult<IReadOnlyList<ProjectPlan>>(plans.Values.Where(item => !item.IsDeleted && (filter is null || filter.Contains(item.ProjectId))).ToArray());
        }
    }

    public Task<ProjectPlan> SavePlanAsync(ProjectPlan plan, long? expectedRowVersion, ProjectPlanVersion? version, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = plans.Values.FirstOrDefault(item => item.ProjectId == plan.ProjectId);
            if (current is { IsDeleted: true } && expectedRowVersion is null)
            {
                // Keep the archived history and monotonically increasing revision when a new draft is created.
                plan = plan with { Id = current.Id };
                expectedRowVersion = current.RowVersion;
            }
            else if (current is not null && (current.Id != plan.Id || current.IsDeleted)) throw new PdmConflictException("计划已不存在或已被替换，请刷新后重试。");
            if (current is null && expectedRowVersion is not null) throw new PdmConflictException("计划已不存在，请刷新后重试。");
            if (current is not null && current.RowVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
            if (version is not null) versions.Add(version with { VersionNumber = versions.Count(item => item.PlanId == plan.Id) + 1 });
            var saved = plan with { RowVersion = (current?.RowVersion ?? 0) + 1 };
            plans[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<IReadOnlyList<ProjectPlanVersion>> ListVersionsAsync(Guid planId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProjectPlanVersion>>(versions.Where(item => item.PlanId == planId).OrderByDescending(item => item.VersionNumber).ToArray());
    }

    public Task DeletePlansAsync(IReadOnlyDictionary<Guid, long> expectedRowVersions, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var selected = expectedRowVersions.Select(item => plans.Values.FirstOrDefault(plan => plan.ProjectId == item.Key) is { } plan
                ? (Plan: plan, Expected: item.Value)
                : throw new PdmConflictException("计划已不存在，请刷新后重试。")).ToArray();
            if (selected.Any(item => item.Plan.RowVersion != item.Expected))
                throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
            foreach (var item in selected)
            {
                plans.Remove(item.Plan.Id);
                versions.RemoveAll(version => version.PlanId == item.Plan.Id);
            }
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<ProjectPlan>> SavePlansAsync(IReadOnlyList<ProjectPlanWrite> writes, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var originalPlans = plans.ToArray();
            var originalVersionCount = versions.Count;
            try
            {
                return Task.FromResult<IReadOnlyList<ProjectPlan>>(writes.Select(write =>
                    SavePlanAsync(write.Plan, write.ExpectedRowVersion, write.Version, cancellationToken).GetAwaiter().GetResult()).ToArray());
            }
            catch
            {
                plans.Clear();
                foreach (var item in originalPlans) plans.Add(item.Key, item.Value);
                versions.RemoveRange(originalVersionCount, versions.Count - originalVersionCount);
                throw;
            }
        }
    }
}
