namespace Upton.Pdm.Domain;

public sealed record ProjectFileVersion(
    Guid Id,
    Guid ProjectFileId,
    int VersionNumber,
    string FileName,
    string StorageRoot,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    string? Comment);

public sealed record ProjectFile(
    Guid Id,
    Guid RootProjectId,
    Guid FolderId,
    string FileName,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    string? DeletedBy)
{
    public ProjectFileVersion? CurrentVersion { get; init; }
}
