using System.Text.Json.Serialization;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record LoginRequest(string Username, string Password);

public sealed record ResumeSessionRequest(string ResumeToken);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string ResumeToken, string Username, string DisplayName, string Role, IReadOnlyList<string> Permissions);

public sealed record PasswordResetRequest(string Username, string DisplayName);

public sealed record ChangePasswordRequest(string CurrentPassword, string Password);

public sealed record UpdateProfileRequest(string? Landline, string? MobilePhone, string? Email, string? Gender, string? Nickname);

public sealed record CreateManagedUserRequest(string Username, string DisplayName, string Password, string Role, bool IsActive = true);

public sealed record UpdateManagedUserRequest(string DisplayName, string Role, bool IsActive = true);

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

public sealed record UpdateProjectDetailsRequest(
    Guid? OrganizationId,
    string? ProjectTypeCode,
    int? EquipmentTypeCode,
    Guid? CustomerId,
    string Name,
    string? ProjectAlias,
    DateOnly SignedDate,
    int Quantity);

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
    int CheckoutForceReleaseHours = 48,
    string BomDrawingNumberProperty = "物料编码",
    string BomNameProperty = "物料名称",
    string BomDescriptionProperty = "备注信息",
    string BomMaterialProperty = "材质",
    string BomSpecificationProperty = "型号",
    string BomUnitProperty = "单位",
    string BomBrandProperty = "品牌",
    string BomSurfaceTreatmentProperty = "表面处理",
    string BomWeightProperty = "重量",
    IReadOnlyList<BomPropertyMapping>? BomPropertyMappings = null,
    BomValidationRules? ValidationRules = null);

public sealed record SetBomEmptyDeclarationRequest(bool DeclaredEmpty);

public sealed record UpdateRolePermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record CreateRoleRequest(string Name, string Description, string SourceRoleCode);

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
    string Approver,
    string? ChangeNumber = null,
    string? ChangeReason = null,
    string? EffectiveSerialFrom = null,
    string? EffectiveSerialTo = null);

public sealed record ReplaceBomRequest(IReadOnlyList<BomItemInput> Items);

public sealed record ResolveBomItemRequest(
    string Action,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BomKind? TargetKind = null);

public sealed record BatchUpdateBomItemsRequest(
    IReadOnlyList<Guid> ItemIds,
    IReadOnlyList<string> Fields,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BomKind? TargetKind = null,
    string? Unit = null,
    string? DrawingNumber = null,
    string? Name = null,
    string? Specification = null,
    string? Remark = null,
    string? Brand = null,
    string? Material = null,
    string? SurfaceTreatment = null,
    string? Weight = null,
    decimal? Quantity = null,
    string? Revision = null,
    bool? Complete = null);

public sealed record BatchDeleteBomItemsRequest(IReadOnlyList<Guid> ItemIds, string Reason);

public sealed record BatchRestoreBomItemsRequest(IReadOnlyList<Guid> ItemIds, string Mode = "Original");

public sealed record RestoreBomItemsFromSourceRequest(IReadOnlyList<Guid> ItemIds);

public sealed record CompleteCadPropertyWritebackRequest(Guid ResultVersionId);

public sealed record FailCadPropertyWritebackRequest(string Error, bool Conflict = false);

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
