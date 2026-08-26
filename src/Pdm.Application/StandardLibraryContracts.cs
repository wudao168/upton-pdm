using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveStandardLibraryCategoryCommand(
    string Name,
    Guid? ParentId,
    int SortOrder,
    bool IsActive,
    long? ExpectedRowVersion = null);

public sealed record AddStandardLibraryMaterialsCommand(
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<Guid> MaterialIds);

public sealed record StandardLibraryQuery(
    Guid? CategoryId,
    string? Query,
    string? Brand,
    bool RecommendedOnly,
    int Page,
    int PageSize);

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
