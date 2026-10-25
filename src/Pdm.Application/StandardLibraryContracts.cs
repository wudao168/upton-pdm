using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class StandardLibraryDefaults
{
    public static readonly Guid UncategorizedId = Guid.Parse("53544c42-0000-0000-0000-000000000001");
    public static bool IsUncategorized(StandardLibraryCategory category) => category.ParentId is null && category.Name == "未分类";
}

public sealed record StandardLibraryImportRow(string MaterialCode, string Status, string Message, string? Name = null, string? Specification = null, string? Brand = null);
public sealed record StandardLibraryImportResult(IReadOnlyList<StandardLibraryImportRow> Rows)
{
    public int ImportedCount => Rows.Count(row => row.Status == "Imported");
    public int ExistingCount => Rows.Count(row => row.Status == "Existing");
    public int FailedCount => Rows.Count(row => row.Status == "Failed");
}

public sealed record SaveStandardLibraryCategoryCommand(
    string Name,
    Guid? ParentId,
    int SortOrder,
    bool IsActive,
    long? ExpectedRowVersion = null,
    string? Code = null);

public sealed record AddStandardLibraryMaterialsCommand(
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> MaterialIds);

public sealed record StandardLibraryQuery(
    Guid? CategoryId,
    string? Query,
    string? Brand,
    bool RecommendedOnly,
    int Page,
    int PageSize,
    bool IncludeInactive = false);

public interface IStandardLibraryRepository
{
    Task<IReadOnlyList<StandardLibraryCategory>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<StandardLibraryCategory?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken);
    Task<StandardLibraryCategory> CreateCategoryAsync(StandardLibraryCategory category, CancellationToken cancellationToken);
    Task<StandardLibraryCategory> UpdateCategoryAsync(StandardLibraryCategory category, long expectedRowVersion, CancellationToken cancellationToken);
    Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<StandardLibraryMembership>> ListMembershipsAsync(CancellationToken cancellationToken);
    Task AddMembershipsAsync(IReadOnlyList<StandardLibraryMembership> memberships, CancellationToken cancellationToken);
    Task RemoveMembershipAsync(Guid categoryId, Guid materialId, CancellationToken cancellationToken);
}
