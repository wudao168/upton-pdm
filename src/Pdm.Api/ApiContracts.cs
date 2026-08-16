using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string Username, string DisplayName, string Role, IReadOnlyList<string> Permissions);

public sealed record CreateProjectRequest(
    Guid OrganizationId,
    string ProjectTypeCode,
    int EquipmentTypeCode,
    Guid CustomerId,
    string Name,
    string? ProjectAlias,
    DateOnly SignedDate,
    int Quantity);

public sealed record CreateSubprojectRequest(string Name, string? ProjectAlias, int Quantity);

public sealed record UpdateOrganizationCountersRequest(int CurrentProjectSequence, int CurrentSerialSequence);

public sealed record UpdateCrmIntegrationRequest(
    string BaseUrl,
    string Username,
    string? Password,
    bool AutoSyncEnabled = false,
    int AutoSyncIntervalMinutes = 60);

public sealed record SaveEquipmentTypeRequest(string Name, bool IsActive = true);

public sealed record UpdateSystemSettingsRequest(
    string VaultRoot,
    string ReleaseRoot,
    int CheckoutHeartbeatSeconds = 180,
    int CheckoutLeaseMinutes = 15,
    int CheckoutOfflineGraceMinutes = 60,
    int CheckoutReminderHours = 4,
    int CheckoutStrongReminderHours = 8,
    int CheckoutOverdueHours = 24,
    int CheckoutForceReleaseHours = 48);

public sealed record UpdateRolePermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record SaveProjectOrganizationRequest(string Name, string ProjectCompanyCode, string ModelCompanyCode, bool IsActive = true);

public sealed record SaveOrganizationUnitRequest(
    Guid OrganizationId,
    Guid? ParentUnitId,
    string Code,
    string Name,
    OrganizationUnitKind Kind,
    bool IsActive = true,
    int SortOrder = 0);

public sealed record UpdateOrganizationMembershipsRequest(IReadOnlyList<Guid> UnitIds, Guid PrimaryUnitId);

public sealed record UpdateOrganizationUnitManagersRequest(string PrimaryManager, IReadOnlyList<string> CollaborativeManagers);

public sealed record UpdateProjectExecutionUnitRequest(Guid ExecutionUnitId);

public sealed record UpdateMainProjectStaffingRequest(string PrimaryProjectManager, IReadOnlyList<string> CollaborativeProjectManagers, string DesignLead);

public sealed record UpdateChildProjectDesignersRequest(IReadOnlyList<string> Designers);

public sealed record RegisterDocumentRequest(
    string DrawingNumber,
    string Name,
    string FileName,
    DocumentKind Kind,
    Guid? FolderId = null,
    Guid? RelatedModelDocumentId = null,
    string? SourceSha256 = null,
    bool AllowDuplicateContent = false,
    string? DuplicateReason = null);

public sealed record DocumentRegistrationCandidateRequest(
    string CandidateKey,
    string FileName,
    DocumentKind Kind,
    string SourceSha256);

public sealed record DocumentRegistrationPreflightRequest(
    IReadOnlyList<DocumentRegistrationCandidateRequest> Candidates);

public sealed record SaveFolderPermissionRequest(FolderPrincipalType PrincipalType, string PrincipalKey, FolderAccess Access);

public sealed record SaveFolderTemplateNodeRequest(
    string FolderKey,
    string Name,
    int SortOrder,
    bool InheritPermissions,
    IReadOnlyList<SaveFolderPermissionRequest> Permissions);

public sealed record SaveFolderTemplateRequest(IReadOnlyList<SaveFolderTemplateNodeRequest> Nodes);

public sealed record SaveProjectFolderPermissionsRequest(IReadOnlyList<SaveFolderPermissionRequest> Permissions);

public sealed record CheckoutRequest(Guid SessionId, string MachineName);

public sealed record EditSessionHeartbeatRequest(string MachineName, IReadOnlyList<Guid> DocumentIds);

public sealed record EditLockActionRequest(string Reason);

public sealed record LifecycleActionRequest(string Comment);

public sealed record CheckInRequest(
    Guid ProjectId,
    DocumentReferenceNode Root,
    string Comment,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    IReadOnlyDictionary<string, string?>? Properties,
    Guid CheckoutSessionId,
    bool IsProjectRoot = false,
    bool ForceVersion = false,
    string? DrawingNumber = null,
    string? Name = null,
    string? FileName = null);

public sealed record CompleteEditRequest(string Sha256, Guid CheckoutSessionId);

public sealed record DiscardCheckoutRequest(Guid CheckoutSessionId);

public sealed record RestoreVersionRequest(string ChangeNote);

public sealed record PublishDocumentVersionRequest(Guid SourceVersionId, Guid ReleasePackageId, Guid ApprovalTaskId);

public sealed record CreateControlledOpenManifestRequest(Guid? VersionId, bool ReleasedOnly, bool ForEdit);

public sealed record CreateReleasePackageRequest(
    Guid ProjectId,
    Guid? ReferenceSnapshotId,
    string Number,
    string ProcessReviewer,
    string Approver);

public sealed record ReplaceBomRequest(IReadOnlyList<BomItemInput> Items);

public sealed record ApprovalRequest(ApprovalDecision Decision, string? Comment);

public sealed record MyApprovalTaskResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid ReleasePackageId,
    string ReleasePackageNumber,
    ApprovalStage Stage,
    ReleasePackageState PackageState,
    DateTimeOffset CreatedAt);

public sealed record ProjectVersionResponse(
    Guid Id,
    Guid DocumentId,
    string DrawingNumber,
    string DocumentName,
    string FileName,
    RevisionLabel Revision,
    DocumentVersionStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string ChangeNote);

public sealed record StartUploadRequest(Guid ProjectId, string FileName, long TotalLength, string Sha256);

public sealed record CompleteUploadRequest(string RelativeTargetPath);
