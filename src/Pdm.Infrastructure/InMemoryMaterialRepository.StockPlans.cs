using System.Collections.Concurrent;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryMaterialRepository
{
    private readonly ConcurrentDictionary<(string Enterprise, string Organization, string Code), MaterialStockPlan> stockPlans = new();

    public Task SaveStockPlansAsync(IReadOnlyList<MaterialStockPlan> plans, CancellationToken cancellationToken)
    {
        foreach (var plan in plans) stockPlans.AddOrUpdate((plan.EnterpriseCode, plan.OrganizationCode, plan.MaterialCode),
            plan, (_, previous) => plan.SyncedAt >= previous.SyncedAt ? plan : previous);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MaterialStockPlan>> ListStockedMaterialPlansAsync(string enterpriseCode, string organizationCode, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialStockPlan>>(stockPlans.Values.Where(plan => plan.EnterpriseCode == enterpriseCode
            && plan.OrganizationCode == organizationCode && plan.IsStocked).OrderBy(plan => plan.MaterialCode, StringComparer.OrdinalIgnoreCase).ToArray());
}
