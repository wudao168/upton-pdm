using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryStandardLibraryRepository : IStandardLibraryRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, StandardLibraryCategory> categories = new();
    private readonly ConcurrentDictionary<(Guid CategoryId, Guid MaterialId), StandardLibraryMembership> memberships = new();

    public Task<IReadOnlyList<StandardLibraryCategory>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StandardLibraryCategory>>(categories.Values.Where(item => includeInactive || item.IsActive)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<StandardLibraryCategory?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(categories.GetValueOrDefault(categoryId));

    public Task<StandardLibraryCategory> CreateCategoryAsync(StandardLibraryCategory category, CancellationToken cancellationToken)
    {
        if (!categories.TryAdd(category.Id, category)) throw new PdmConflictException("标准分类已经存在。");
        return Task.FromResult(category);
    }

    public Task<StandardLibraryCategory> UpdateCategoryAsync(StandardLibraryCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!categories.TryGetValue(category.Id, out var current)) throw new PdmNotFoundException("标准分类不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("标准分类已被其他用户修改，请刷新后重试。");
            var saved = category with { RowVersion = expectedRowVersion + 1 };
            categories[category.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!categories.TryGetValue(categoryId, out var current) || current.RowVersion != expectedRowVersion)
                throw new PdmConflictException("标准分类不存在或已被其他用户修改。");
            if (categories.Values.Any(item => item.ParentId == categoryId) || memberships.Keys.Any(key => key.CategoryId == categoryId))
                throw new PdmRuleException("仅能删除无子分类且无成员的标准分类。");
            categories.TryRemove(categoryId, out _);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<StandardLibraryMembership>> ListMembershipsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StandardLibraryMembership>>(memberships.Values.ToArray());

    public Task AddMembershipsAsync(IReadOnlyList<StandardLibraryMembership> items, CancellationToken cancellationToken)
    {
        foreach (var item in items) memberships.TryAdd((item.CategoryId, item.MaterialId), item);
        return Task.CompletedTask;
    }

    public Task RemoveMembershipAsync(Guid categoryId, Guid materialId, CancellationToken cancellationToken)
    {
        memberships.TryRemove((categoryId, materialId), out _);
        return Task.CompletedTask;
    }
}
