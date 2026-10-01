using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectBudgetRepository : IProjectBudgetRepository
{
    private readonly Dictionary<Guid, ProjectBudget> budgets = new();
    private readonly object gate = new();
    private AssessmentLaborRateSettings? laborRates;
    public Task<AssessmentLaborRateSettings?> FindLaborRatesAsync(CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(laborRates);
    }
    public Task<AssessmentLaborRateSettings> SaveLaborRatesAsync(AssessmentLaborRateSettings settings, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if ((laborRates?.RowVersion ?? 0) != expectedRowVersion) throw new PdmConflictException("工时单价已被修改，请刷新后重试。");
            laborRates = settings with { RowVersion = expectedRowVersion + 1 };
            return Task.FromResult(laborRates);
        }
    }
    public Task<ProjectBudget?> FindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(budgets.GetValueOrDefault(projectId));
    }
    public Task<ProjectBudget> SaveAsync(ProjectBudget budget, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if ((budgets.GetValueOrDefault(budget.ProjectId)?.RowVersion ?? 0) != expectedRowVersion)
                throw new PdmConflictException("预算已被其他用户修改，请刷新后重试。");
            var saved = budget with { RowVersion = expectedRowVersion + 1 };
            budgets[budget.ProjectId] = saved;
            return Task.FromResult(saved);
        }
    }
}
