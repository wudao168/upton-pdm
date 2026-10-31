using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public interface IBomFileStore
{
    Task<BomFileCatalog> ReadAsync(Guid scopeId, CancellationToken ct);
    Task<bool> SaveAsync(BomFileCatalog catalog, long expectedVersion, CancellationToken ct);
}

public sealed record BomFileView(IReadOnlyList<BomFileReference> Files, bool LibraryUpdateAvailable, long RowVersion);
