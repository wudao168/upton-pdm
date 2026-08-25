using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public enum ProgramTemplateVersionBump
{
    Major,
    Minor,
    Patch
}

public sealed record ProgramTemplateParameterInput(
    ProgramTemplateParameterDirection Direction,
    int SortOrder,
    string Name,
    string DataType,
    string? DefaultValue,
    string? Unit,
    string? Description);

public sealed record CreateProgramTemplateCommand(
    ProgramTemplateAssetType AssetType,
    string Name,
    string Category,
    string Description,
    string Vendor,
    string Platform,
    string SoftwareVersion,
    string ApplicableSeries,
    IReadOnlyList<string> Tags,
    string ChangeNote,
    IReadOnlyList<ProgramTemplateParameterInput> Parameters);

public sealed record UpdateProgramTemplateDraftCommand(
    string Name,
    string Category,
    string Description,
    string Vendor,
    string Platform,
    string SoftwareVersion,
    string ApplicableSeries,
    IReadOnlyList<string> Tags,
    string ChangeNote,
    IReadOnlyList<ProgramTemplateParameterInput> Parameters,
    long ExpectedRowVersion);

public sealed record ProgramTemplateUploadSession(
    Guid Id,
    Guid RevisionId,
    string TemplateCode,
    ProgramTemplateAttachmentKind Kind,
    string FileName,
    long TotalLength,
    int ChunkSize,
    string ExpectedSha256,
    long ReceivedLength,
    string Owner,
    DateTimeOffset ExpiresAt);

public sealed record StoredProgramTemplateFile(
    Guid RevisionId,
    ProgramTemplateAttachmentKind Kind,
    string OriginalFileName,
    string RelativePath,
    long Length,
    string Sha256,
    DateTimeOffset StoredAt);

public sealed record ProgramTemplateDownload(
    string FileName,
    long FileLength,
    string Sha256,
    Stream Content);

public sealed record ProgramTemplateDecisionCommand(
    ProgramTemplateApprovalDecision Decision,
    string? Comment,
    IReadOnlyList<string> ChecklistItems,
    long ExpectedRowVersion);

public sealed record ProgramTemplateDecisionResult(
    ProgramTemplateRevision Revision,
    ProgramTemplateApprovalTask Task);

public interface IProgramTemplateStorage
{
    Task<ProgramTemplateUploadSession> StartUploadAsync(
        Guid revisionId,
        string templateCode,
        ProgramTemplateAttachmentKind kind,
        string fileName,
        long totalLength,
        string expectedSha256,
        string owner,
        CancellationToken cancellationToken);
    Task<ProgramTemplateUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken);
    Task<StoredProgramTemplateFile> CompleteUploadAsync(Guid sessionId, string actor, CancellationToken cancellationToken);
    Task VerifyAsync(string relativePath, long length, string sha256, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    Task DiscardAsync(StoredProgramTemplateFile file, CancellationToken cancellationToken);
}

public interface IProgramTemplateRepository
{
    Task<string> ReserveCodeAsync(ProgramTemplateAssetType assetType, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProgramTemplate>> ListPublishedAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProgramTemplate>> ListMineAsync(string actor, CancellationToken cancellationToken);
    Task<ProgramTemplate?> FindAsync(Guid templateId, CancellationToken cancellationToken);
    Task<ProgramTemplateRevision?> FindRevisionAsync(Guid revisionId, CancellationToken cancellationToken);
    Task<ProgramTemplate> CreateAsync(ProgramTemplate template, CancellationToken cancellationToken);
    Task<ProgramTemplateRevision> CreateRevisionAsync(ProgramTemplateRevision revision, CancellationToken cancellationToken);
    Task<ProgramTemplateRevision> UpdateDraftAsync(ProgramTemplateRevision revision, long expectedRowVersion, CancellationToken cancellationToken);
    Task<ProgramTemplateRevision> AttachFileAsync(Guid revisionId, StoredProgramTemplateFile file, long expectedRowVersion, CancellationToken cancellationToken);
    Task<ProgramTemplateRevision> SubmitAsync(
        Guid revisionId,
        ProgramTemplateApprovalTask reviewTask,
        ProgramTemplateApprovalTask approvalTask,
        long expectedRowVersion,
        CancellationToken cancellationToken);
    Task<ProgramTemplateApprovalTask?> FindTaskAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListRevisionTasksAsync(Guid revisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListTasksAsync(string actor, string roleCode, CancellationToken cancellationToken);
    Task<ProgramTemplateDecisionResult> DecideAsync(
        Guid taskId,
        string actor,
        ProgramTemplateApprovalDecision decision,
        string? comment,
        IReadOnlyList<string> checklistItems,
        long expectedRowVersion,
        CancellationToken cancellationToken);
    Task<ProgramTemplate> SetArchivedAsync(Guid templateId, bool archived, string actor, CancellationToken cancellationToken);
}
