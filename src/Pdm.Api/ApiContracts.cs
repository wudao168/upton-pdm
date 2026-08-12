using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string Username, string DisplayName, string Role);

public sealed record RegisterDocumentRequest(
    string DrawingNumber,
    string Name,
    string FileName,
    DocumentKind Kind);

public sealed record CheckInRequest(
    Guid ProjectId,
    DocumentReferenceNode Root,
    string Comment,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    IReadOnlyDictionary<string, string?>? Properties);

public sealed record CompleteEditRequest(string Sha256);

public sealed record RestoreVersionRequest(string ChangeNote);

public sealed record PublishDocumentVersionRequest(Guid SourceVersionId, Guid ReleasePackageId, Guid ApprovalTaskId);

public sealed record CreateReleasePackageRequest(
    Guid ProjectId,
    Guid? ReferenceSnapshotId,
    string Number,
    string ProcessReviewer,
    string Approver);

public sealed record ReplaceBomRequest(IReadOnlyList<BomItemInput> Items);

public sealed record ApprovalRequest(ApprovalDecision Decision, string? Comment);

public sealed record StartUploadRequest(Guid ProjectId, string FileName, long TotalLength, string Sha256);

public sealed record CompleteUploadRequest(string RelativeTargetPath);
