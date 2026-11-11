using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectBonusRepository(IPdmRepository repository) : IProjectBonusRepository
{
    private readonly Dictionary<Guid, ProjectBonusSettings> settings = [];
    private readonly Dictionary<Guid, ProjectBonusBook> books = [];
    private readonly Dictionary<Guid, List<ProjectBonusBook>> history = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public async Task<ProjectBonusSettings?> GetSettingsAsync(Guid companyId, CancellationToken ct)
    { await gate.WaitAsync(ct); try { return Clone(settings.GetValueOrDefault(companyId)); } finally { gate.Release(); } }
    public async Task<ProjectBonusSettings> SaveSettingsAsync(Guid companyId, ProjectBonusSettings value, long expectedVersion, CancellationToken ct)
    {
        await gate.WaitAsync(ct); try {
            if ((settings.GetValueOrDefault(companyId)?.Version ?? 0) != expectedVersion) throw new PdmConflictException("奖金规则已更新，请刷新。");
            var saved = value with { Version = expectedVersion + 1 }; settings[companyId] = Clone(saved); return Clone(saved);
        } finally { gate.Release(); }
    }
    public async Task<ProjectBonusBook?> FindAsync(Guid projectId, CancellationToken ct)
    { await gate.WaitAsync(ct); try { return Clone(books.GetValueOrDefault(projectId)); } finally { gate.Release(); } }
    public async Task<IReadOnlyList<ProjectBonusBook>> HistoryAsync(Guid projectId, CancellationToken ct)
    { await gate.WaitAsync(ct); try { return Clone((history.GetValueOrDefault(projectId) ?? []).AsEnumerable().Reverse().Take(100).ToArray()); } finally { gate.Release(); } }
    public async Task<ProjectBonusBook> SaveAsync(ProjectBonusBook book, long expectedVersion, IReadOnlyList<UserNotification> notifications, CancellationToken ct)
    {
        await gate.WaitAsync(ct); try {
            if ((books.GetValueOrDefault(book.ProjectId)?.RowVersion ?? 0) != expectedVersion) throw new PdmConflictException("奖金申请已更新，请刷新。");
            var saved = Clone(book with { RowVersion = expectedVersion + 1 });
            var pending = await repository.ListUnreadUserNotificationsAsync(null, ct);
            await repository.ResolveUserNotificationsAsync(pending.Where(n => n.Category == "ProjectBonusPending" && n.ProjectId == book.ProjectId).Select(n => n.SourceKey).ToArray(), DateTimeOffset.UtcNow, ct);
            await repository.CreateUserNotificationsAsync(notifications, ct);
            books[book.ProjectId] = saved;
            if (!history.ContainsKey(book.ProjectId)) history[book.ProjectId] = [];
            history[book.ProjectId].Add(Clone(saved)); return Clone(saved);
        } finally { gate.Release(); }
    }
}
