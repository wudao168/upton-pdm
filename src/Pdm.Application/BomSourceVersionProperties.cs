using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record BomSourceVersionProperties(Guid DocumentId, RevisionLabel Revision, IReadOnlyDictionary<string, string?> PropertySnapshot)
{
    public static BomSourceVersionProperties From(DocumentVersion version) => new(version.DocumentId, version.Revision, version.PropertySnapshot);
}
