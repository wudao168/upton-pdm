using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record UserAccount(
    Guid Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    UserRole Role,
    bool IsActive);

public sealed record UploadSession(
    Guid Id,
    Guid ProjectId,
    string FileName,
    long TotalLength,
    int ChunkSize,
    string ExpectedSha256,
    long ReceivedLength,
    DateTimeOffset ExpiresAt);

public sealed record StoredFile(
    string RelativePath,
    long Length,
    string Sha256,
    DateTimeOffset StoredAt);

public interface IPdmRepository
{
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken);
    Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken);
    Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken);
    Task<PdmDocument> CheckInAsync(Guid documentId, string actor, RevisionLabel nextRevision, CadReferenceSnapshot snapshot, CancellationToken cancellationToken);
    Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken);
    Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, CancellationToken cancellationToken);
    Task MarkPublishedAsync(Guid releasePackageId, string publishedPath, DateTimeOffset publishedAt, CancellationToken cancellationToken);
    Task MarkPublishFailedAsync(Guid releasePackageId, string error, CancellationToken cancellationToken);
    Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken);
    Task<int> CountUsersAsync(CancellationToken cancellationToken);
    Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken);
    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public interface IFileStorage
{
    Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken);
    Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken);
    Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken);
    Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken);
}

public interface IReleasePackagePublisher
{
    Task<string> PublishAsync(ReleasePackage package, Project project, CancellationToken cancellationToken);
}

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

public interface ITokenIssuer
{
    string Issue(UserAccount account, TimeSpan lifetime);
}

public sealed class PdmRuleException(string message) : InvalidOperationException(message);

public sealed class PdmNotFoundException(string message) : KeyNotFoundException(message);

public sealed class PdmConflictException(string message) : InvalidOperationException(message);
