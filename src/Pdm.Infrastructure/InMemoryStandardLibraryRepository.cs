using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryStandardLibraryRepository : IStandardLibraryRepository
{
    private readonly object gate = new();
    private long currentCategoryNumber;
    private readonly ConcurrentDictionary<Guid, StandardLibraryCategory> categories = new();
    private readonly ConcurrentDictionary<(Guid CategoryId, Guid MaterialId), StandardLibraryMembership> memberships = new();

    public InMemoryStandardLibraryRepository()
    {
        var now = DateTimeOffset.UtcNow;
        var category = new StandardLibraryCategory(StandardLibraryDefaults.UncategorizedId, "未分类", null, int.MinValue, true, "system", now, "system", now, 1);
        categories[category.Id] = category;
    }

    public Task<IReadOnlyList<StandardLibraryCategory>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StandardLibraryCategory>>(categories.Values.Where(item => includeInactive || item.IsActive)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<StandardLibraryCategory?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(categories.GetValueOrDefault(categoryId));

    public Task<StandardLibraryCategory> CreateCategoryAsync(StandardLibraryCategory category, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (categories.ContainsKey(category.Id)) throw new PdmConflictException("标准分类已经存在。");
            category = AssignCategoryCode(category);
            categories[category.Id] = category;
            return Task.FromResult(category);
        }
    }

    public Task<StandardLibraryCategory> UpdateCategoryAsync(StandardLibraryCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!categories.TryGetValue(category.Id, out var current)) throw new PdmNotFoundException("标准分类不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("标准分类已被其他用户修改，请刷新后重试。");
            var saved = AssignCategoryCode(category with { Code = current.Code, RowVersion = expectedRowVersion + 1 });
            categories[category.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    private StandardLibraryCategory AssignCategoryCode(StandardLibraryCategory category) =>
        category.ParentId is null && category.Code is null && !StandardLibraryDefaults.IsUncategorized(category)
            ? category with { Code = EngineeringKitNumbering.CategoryCode(++currentCategoryNumber) }
            : category;

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
        lock (gate) foreach (var group in items.GroupBy(item => item.MaterialId))
        {
            bool Uncategorized(Guid id) => categories.TryGetValue(id, out var category) && StandardLibraryDefaults.IsUncategorized(category);
            var classified = group.Where(item => !Uncategorized(item.CategoryId)).ToArray();
            if (classified.Length > 0)
            {
                foreach (var key in memberships.Keys.Where(key => key.MaterialId == group.Key && Uncategorized(key.CategoryId))) memberships.TryRemove(key, out _);
                foreach (var item in classified) memberships.TryAdd((item.CategoryId, item.MaterialId), item);
            }
            else if (!memberships.Keys.Any(key => key.MaterialId == group.Key && !Uncategorized(key.CategoryId)))
                foreach (var item in group) memberships.TryAdd((item.CategoryId, item.MaterialId), item);
        }
        return Task.CompletedTask;
    }

    public Task RemoveMembershipAsync(Guid categoryId, Guid materialId, CancellationToken cancellationToken)
    {
        memberships.TryRemove((categoryId, materialId), out _);
        return Task.CompletedTask;
    }
}
