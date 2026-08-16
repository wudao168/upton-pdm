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

public sealed record DocumentVersionCommit(
    StoredFile File,
    string ChangeNote,
    IReadOnlyDictionary<string, string?> Properties,
    CadReferenceSnapshot ReferenceSnapshot,
    IReadOnlyList<BomItem> MechanicalBomSnapshot,
    IReadOnlyList<BomItem> ElectricalBomSnapshot,
    Guid? SourceVersionId = null,
    string? SourceDescription = null,
    bool IsProjectRoot = false,
    bool ForceVersion = false,
    string? DrawingNumber = null,
    string? Name = null,
    string? FileName = null);

public sealed record DocumentCheckInResult(
    PdmDocument Document,
    DocumentVersion? Version,
    bool VersionCreated);

public sealed record ControlledOpenFile(
    Guid DocumentId,
    Guid VersionId,
    string Revision,
    string FileName,
    string RelativePath,
    long FileLength,
    string Sha256,
    string Configuration,
    bool IsRoot);

public sealed record ControlledOpenManifest(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    Guid RootDocumentId,
    Guid RootVersionId,
    string RootRevision,
    string RootRelativePath,
    bool ForEdit,
    IReadOnlyList<ControlledOpenFile> Files);

public sealed record RegisterDocumentCommand(
    Guid ProjectId,
    string DrawingNumber,
    string Name,
    string FileName,
    DocumentKind Kind,
    Guid? FolderId = null,
    Guid? RelatedModelDocumentId = null,
    string? SourceSha256 = null,
    bool AllowDuplicateContent = false,
    string? DuplicateReason = null);

public sealed record DocumentContentFingerprint(
    PdmDocument Document,
    string SourceSha256);

public sealed record DocumentRegistrationCandidate(
    string CandidateKey,
    string FileName,
    DocumentKind Kind,
    string SourceSha256);

public sealed record DocumentRegistrationMatch(
    string CandidateKey,
    DocumentRegistrationMatchKind MatchKind,
    Guid? ExistingDocumentId,
    Guid? ExistingProjectId,
    string? ExistingProjectCode,
    string? ExistingProjectName,
    string? ExistingDrawingNumber,
    string? ExistingFileName,
    string? ExistingRevision);

public sealed record SaveFolderPermissionCommand(
    FolderPrincipalType PrincipalType,
    string PrincipalKey,
    FolderAccess Access);

public sealed record SaveFolderTemplateNodeCommand(
    string FolderKey,
    string Name,
    int SortOrder,
    bool InheritPermissions,
    IReadOnlyList<SaveFolderPermissionCommand> Permissions);

public sealed record CreateProjectCommand(
    string Code,
    string Name,
    string Owner,
    string VaultLocation,
    string ReleaseLocation);

public sealed record CreateNumberedProjectCommand(
    Guid OrganizationId,
    string ProjectTypeCode,
    int EquipmentTypeCode,
    Guid CustomerId,
    string Name,
    string? ProjectAlias,
    DateOnly SignedDate,
    int Quantity,
    string Owner,
    string VaultLocation,
    string ReleaseLocation);

public sealed record CreateSubprojectCommand(
    Guid ParentProjectId,
    string Name,
    string? ProjectAlias,
    int Quantity,
    string? VaultRoot = null,
    string? ReleaseRoot = null);

public sealed record SaveProjectOrganizationCommand(
    Guid? Id,
    string Name,
    string ProjectCompanyCode,
    string ModelCompanyCode,
    bool IsActive);

public sealed record SaveOrganizationUnitCommand(
    Guid? Id,
    Guid OrganizationId,
    Guid? ParentUnitId,
    string Code,
    string Name,
    OrganizationUnitKind Kind,
    bool IsActive,
    int SortOrder);

public sealed record SetMainProjectStaffingCommand(
    string PrimaryProjectManager,
    IReadOnlyList<string> CollaborativeProjectManagers,
    string DesignLead);

public sealed record BomItemInput(
    int Sequence,
    string DrawingNumber,
    string Name,
    decimal Quantity,
    string Unit,
    string? Material,
    string? Specification,
    string Revision,
    bool IsComplete);

public sealed record CrmCustomerRecord(string Code, string Name);

public sealed record CrmCustomerBatch(
    IReadOnlyList<CrmCustomerRecord> Customers,
    int SkippedCount);

public sealed record CrmIntegrationConfiguration(
    string BaseUrl,
    string Username,
    string PasswordCiphertext,
    bool AutoSyncEnabled,
    int AutoSyncIntervalMinutes,
    DateTimeOffset? LastSyncAt,
    int LastSyncCount,
    DateTimeOffset? LastAutoSyncAttemptAt,
    string? LastAutoSyncError);

public sealed record CrmIntegrationSettings(
    string BaseUrl,
    string Username,
    bool PasswordConfigured,
    bool AutoSyncEnabled,
    int AutoSyncIntervalMinutes,
    DateTimeOffset? LastSyncAt,
    int LastSyncCount,
    DateTimeOffset? LastAutoSyncAttemptAt,
    string? LastAutoSyncError);

public sealed record CrmConnectionTestResult(int CustomerCount, int SkippedCount, DateTimeOffset TestedAt);

public sealed record CrmCustomerSyncResult(
    int CustomerCount,
    int SkippedCount,
    DateTimeOffset SyncedAt,
    CrmIntegrationSettings Settings,
    IReadOnlyList<PdmCustomer> Customers);

public interface ICrmCustomerClient
{
    Task<CrmCustomerBatch> ListCustomersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken);
}

public interface ICrmCredentialProtector
{
    string Protect(string password);
    string Unprotect(string ciphertext);
}

public interface IPdmRepository
{
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken);
    Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken);
    Task<bool> HasProjectContentReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken);
    Task<bool> HasChildProjectsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, CancellationToken cancellationToken);
    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ProjectNumberingOptions> GetProjectNumberingOptionsAsync(CancellationToken cancellationToken);
    Task<ProjectNumberingOptions> AdvanceOrganizationCountersAsync(Guid organizationId, int currentProjectSequence, int currentSerialSequence, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmCustomer>> ListCustomersAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<PdmCustomer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken);
    Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, CancellationToken cancellationToken);
    Task<CrmIntegrationConfiguration> GetCrmIntegrationConfigurationAsync(CancellationToken cancellationToken);
    Task<CrmIntegrationConfiguration> SaveCrmIntegrationConfigurationAsync(CrmIntegrationConfiguration configuration, string actor, CancellationToken cancellationToken);
    Task RecordCrmAutomaticSyncAttemptAsync(DateTimeOffset attemptedAt, string? error, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmCustomer>> ApplyCrmCustomerSyncAsync(IReadOnlyList<CrmCustomerRecord> customers, DateTimeOffset syncedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<EquipmentTypeDefinition>> ListEquipmentTypesAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, CancellationToken cancellationToken);
    Task<PdmSystemSettings> GetSystemSettingsAsync(CancellationToken cancellationToken);
    Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken);
    Task<RolePermissionDirectory> GetRolePermissionDirectoryAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetRolePermissionsAsync(UserRole role, CancellationToken cancellationToken);
    Task<bool> HasRolePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken);
    Task<RolePermissionDirectory> SetRolePermissionsAsync(UserRole role, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken);
    Task<OrganizationDirectory> GetOrganizationDirectoryAsync(CancellationToken cancellationToken);
    Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, CancellationToken cancellationToken);
    Task<OrganizationUnit> SaveOrganizationUnitAsync(SaveOrganizationUnitCommand command, CancellationToken cancellationToken);
    Task<OrganizationDirectory> SetOrganizationMembershipsAsync(string username, IReadOnlyList<Guid> unitIds, Guid primaryUnitId, CancellationToken cancellationToken);
    Task<OrganizationDirectory> SetOrganizationUnitManagersAsync(Guid unitId, string primaryManager, IReadOnlyList<string> collaborativeManagers, CancellationToken cancellationToken);
    Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, CancellationToken cancellationToken);
    Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, CancellationToken cancellationToken);
    Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, CancellationToken cancellationToken);
    Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, CancellationToken cancellationToken);
    Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, CancellationToken cancellationToken);
    Task EnsureProjectFolderTreeAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFolder>> ListProjectFoldersAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFolderTemplateNode>> ListFolderTemplateAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFolderTemplateNode>> SaveFolderTemplateAsync(IReadOnlyList<SaveFolderTemplateNodeCommand> nodes, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectFolder>> SetProjectFolderPermissionsAsync(Guid projectId, Guid folderId, IReadOnlyList<SaveFolderPermissionCommand> permissions, string actor, UserRole role, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmDocument>> ListProjectTreeDocumentsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentModelDrawingRelation>> ListDocumentRelationsAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentWhereUsed>> ListWhereUsedAsync(Guid documentId, CancellationToken cancellationToken);
    Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentContentFingerprint>> ListDocumentContentFingerprintsAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken);
    Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken);
    Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken);
    Task<bool> HasDocumentAccessAsync(Guid documentId, string actor, UserRole role, FolderAccess requiredAccess, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentVersion>> ListDocumentVersionsAsync(Guid documentId, CancellationToken cancellationToken);
    Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken);
    Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken);
    Task<CadReferenceSnapshot?> GetLatestReferenceSnapshotAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<BomItem>> ReplaceBomAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PdmDocument>> ListCheckedOutDocumentsAsync(CancellationToken cancellationToken);
    Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken);
    Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> HeartbeatCheckoutSessionAsync(Guid sessionId, string actor, string machineName, IReadOnlyList<Guid> documentIds, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken);
    Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, string sha256, CancellationToken cancellationToken);
    Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, Guid sessionId, string sha256, CancellationToken cancellationToken);
    Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken);
    Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, Guid sessionId, CancellationToken cancellationToken);
    Task<PdmDocument> RequestCheckoutReleaseAsync(Guid documentId, string requestedBy, string reason, CancellationToken cancellationToken);
    Task<PdmDocument> ForceReleaseCheckoutAsync(Guid documentId, string releasedBy, string reason, CancellationToken cancellationToken);
    Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, DocumentVersionCommit commit, CancellationToken cancellationToken);
    Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, CancellationToken cancellationToken);
    Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(Guid documentId, Guid sourceVersionId, string actor, StoredFile restoredFile, string changeNote, CancellationToken cancellationToken);
    Task<DocumentVersion> PublishDocumentVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken);
    Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken);
    Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken);
    Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken);
    Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, CancellationToken cancellationToken);
    Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, CancellationToken cancellationToken);
    Task MarkPublishedAsync(Guid releasePackageId, string publishedPath, DateTimeOffset publishedAt, CancellationToken cancellationToken);
    Task MarkPublishFailedAsync(Guid releasePackageId, string error, CancellationToken cancellationToken);
    Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken);
    Task<int> CountUsersAsync(CancellationToken cancellationToken);
    Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken);
    Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> ListProjectAuditAsync(Guid projectId, int take, CancellationToken cancellationToken);
}

public interface IFileStorage
{
    Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken);
    Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken);
    Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken);
    Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken);
    Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken);
    Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken);
}

public interface IReleasePackagePublisher
{
    Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken);
    Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken);
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
