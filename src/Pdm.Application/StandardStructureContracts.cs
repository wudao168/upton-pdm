using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record StandardStructureCategory(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive);
public sealed record StandardStructureNode(string Id, string DrawingNumber, string Name, string FileName,
    string Kind, string? FileKey, string? PreviewFileKey, bool Embedded, decimal Quantity,
    IReadOnlyList<StandardStructureNode> Children);
public sealed record StandardStructureBomLine(string Id, BomKind Kind, string DrawingNumber, string Name,
    decimal Quantity, string Unit, string Revision, string? Material = null, string? Specification = null,
    string? Brand = null, string? SurfaceTreatment = null, string? HeatTreatment = null, string? Remark = null);
public sealed record StandardStructureFile(string Key, string FileName, long Length, string Sha256);
public sealed record StandardStructureRevision(Guid Id, int Version, DateTimeOffset ArchivedAt, string ArchivedBy,
    StandardStructureNode Root, IReadOnlyList<StandardStructureFile> Files, IReadOnlyList<StandardStructureBomLine> Bom)
{
    public Guid? StorageCompanyId { get; init; }
    public string? StorageRoot { get; init; }
    public Guid RequestId { get; init; }
    public string Fingerprint { get; init; } = "";
    public string ChangeNote { get; init; } = "";
    public string? BrowserSourceFingerprint { get; init; }
}
public sealed record StandardStructure(Guid Id, Guid CategoryId, string Code, string Name,
    IReadOnlyList<StandardStructureRevision> Revisions, int DrawingType = 9)
{
    public bool IsActive { get; init; } = true;
    public DateTimeOffset? DeletedAt { get; init; }
    public IReadOnlyList<StandardStructureReference> References { get; init; } = [];
}
public sealed record StandardStructureReference(Guid ProjectId, Guid RevisionId, int Version, BomKind Kind,
    decimal Quantity, Guid RequestId, DateTimeOffset ReferencedAt, string Actor, bool Pending = false)
{
    public string? ProjectCode { get; init; }
}
public sealed record ChangeStandardStructureState(string Action, long ExpectedRowVersion);
public sealed record StandardStructureCatalog(Guid CompanyId, IReadOnlyList<StandardStructureCategory> Categories,
    IReadOnlyList<StandardStructure> Structures, string StorageRoot = "", long RowVersion = 0)
{
    public IReadOnlyDictionary<int, int> Sequences { get; init; } = new Dictionary<int, int>();
}
public sealed record StandardStructureView(StandardStructureCatalog Catalog, bool CanManage, bool CanArchive, bool CanConfigureStorage);
public sealed record SaveStandardStructureCategory(string Name, Guid? ParentId, int SortOrder, bool IsActive, long ExpectedRowVersion);
public sealed record ArchiveStandardStructure(Guid StructureId, int DrawingType, string Name,
    StandardStructureNode Root, IReadOnlyList<StandardStructureBomLine> Bom, Guid RequestId,
    Guid? ExpectedRevisionId = null, string ChangeNote = "");
public sealed record MoveStandardStructures(IReadOnlyList<Guid> StructureIds, Guid CategoryId, long ExpectedRowVersion);
public sealed record StandardStructureUpload(string Key, string FileName, Stream Content);
public sealed record ReferenceStandardStructure(Guid RevisionId, Guid ProjectId, BomKind Kind, decimal Quantity, Guid RequestId);
public sealed record StandardStructureReferenceResult(int AddedCount, Guid RevisionId, int Version);

public interface IStandardStructureStore
{
    Task<StandardStructureCatalog> ReadAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveAsync(StandardStructureCatalog catalog, long expectedVersion, CancellationToken ct);
}

public interface IStandardStructureFiles
{
    string DefaultRoot { get; }
    Task<IReadOnlyList<StandardStructureFile>> WriteAsync(string root, Guid companyId, Guid revisionId,
        IReadOnlyList<StandardStructureUpload> files, CancellationToken ct);
    Task<Stream> OpenAsync(string root, Guid companyId, Guid revisionId, StandardStructureFile file, CancellationToken ct);
    void RemoveRevision(string root, Guid companyId, Guid revisionId);
}
