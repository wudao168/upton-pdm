namespace Upton.Pdm.Domain;

public sealed record BomFileReference(Guid Id, Guid MaterialId, string Kind, string FileName,
    int VersionNumber, long FileLength, string Sha256, string UploadedBy, DateTimeOffset UploadedAt,
    string Source, string Purpose)
{
    public static BomFileReference From(MaterialAttachment file, string source, string purpose = "资料") =>
        new(file.Id, file.MaterialId, file.Kind.ToString(), file.OriginalFileName, file.VersionNumber,
            file.FileLength, file.Sha256, file.UploadedBy, file.UploadedAt, source, purpose);
}

public sealed record BomProjectFile(MaterialAttachment File, string Purpose, bool Removed = false);
public sealed record BomFileCatalog(Guid ScopeId, long RowVersion, bool HasLibrarySelection,
    IReadOnlyList<BomFileReference> LibraryFiles, IReadOnlyList<BomProjectFile> ProjectFiles);
