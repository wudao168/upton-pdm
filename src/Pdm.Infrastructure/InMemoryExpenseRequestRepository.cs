using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryExpenseRequestRepository(IPdmRepository repository) : IExpenseRequestRepository
{
    private readonly Dictionary<Guid, ExpenseRequest> requests = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ExpenseRequest?> FindAsync(Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return requests.GetValueOrDefault(id); }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<ExpenseRequest>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return requests.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.UpdatedAt).ToArray(); }
        finally { gate.Release(); }
    }
    public async Task<ExpenseRequest> SaveAsync(ExpenseRequest request, long expectedRowVersion, IReadOnlyList<UserNotification> notifications, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if ((requests.GetValueOrDefault(request.Id)?.RowVersion ?? 0) != expectedRowVersion)
                throw new PdmConflictException("费用申请已更新，请刷新后再操作。");
            var saved = request with { RowVersion = expectedRowVersion + 1 };
            var pending = await repository.ListUnreadUserNotificationsAsync(null, ct);
            var keys = pending.Where(item => item.Category == "ExpenseApprovalPending" && item.SourceKey.StartsWith($"expense-request:{request.Id:N}:")).Select(item => item.SourceKey).ToArray();
            await repository.ResolveUserNotificationsAsync(keys, saved.UpdatedAt, ct);
            await repository.CreateUserNotificationsAsync(notifications, ct);
            requests[saved.Id] = saved;
            return saved;
        }
        finally { gate.Release(); }
    }
}
