using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveEngineeringKitComponentCommand(
    Guid MaterialId,
    decimal Quantity,
    bool IsOptional,
    int SortOrder);

public sealed record SaveEngineeringKitDraftCommand(
    string Name,
    string? Description,
    string? ChangeNote,
    IReadOnlyList<SaveEngineeringKitComponentCommand> Components,
    long? ExpectedRowVersion = null);

public sealed record ExpandEngineeringKitCommand(
    Guid? RevisionId,
    decimal Quantity,
    IReadOnlyList<Guid> SelectedOptionalComponentIds);

public interface IEngineeringKitRepository
{
    Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, CancellationToken cancellationToken);
    Task<EngineeringKit?> FindAsync(Guid kitId, CancellationToken cancellationToken);
    Task<EngineeringKit> SaveDraftAsync(EngineeringKit kit, EngineeringKitRevision revision, long? expectedRowVersion, CancellationToken cancellationToken);
    Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, CancellationToken cancellationToken);
}
