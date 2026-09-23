using System.Text.Json.Serialization;

namespace Upton.Pdm.Domain;

public sealed record Project(
    Guid Id,
    string Code,
    string Name,
    string Owner,
    string VaultLocation,
    string ReleaseLocation,
    bool IsActive)
{
    public string? ProjectAlias { get; init; }

    public Guid? OrganizationId { get; init; }

    public string? OrganizationName { get; init; }

    public string? ProjectTypeCode { get; init; }

    public int? EquipmentTypeCode { get; init; }

    public string? CustomerCode { get; init; }

    public string? CustomerName { get; init; }

    public int? CustomerProjectSequence { get; init; }

    public string? DeviceModel { get; init; }

    public DateOnly? SignedDate { get; init; }

    public int Quantity { get; init; } = 1;

    public Guid? ParentProjectId { get; init; }

    public Guid? RootProjectId { get; init; }

    public int? ChildSequence { get; init; }

    public string? BomItemCategoryCode { get; init; }

    public IReadOnlyList<string> SerialNumbers { get; init; } = [];

    public IReadOnlyList<string> ResponsibleUsers { get; init; } = [];

    public Guid? ExecutionUnitId { get; init; }

    public string? ExecutionUnitName { get; init; }

    public string? PrimaryProjectManager { get; init; }

    public IReadOnlyList<string> CollaborativeProjectManagers { get; init; } = [];

    public string? DesignLead { get; init; }

    public IReadOnlyList<string> DesignLeads { get; init; } = [];

    public IReadOnlyList<string> Designers { get; init; } = [];

    public IReadOnlyDictionary<string, string> PhaseOwners { get; init; } = new Dictionary<string, string>();

    public bool CanAssignExecutionUnit { get; init; }

    public bool CanManageMainStaffing { get; init; }

    public bool CanAssignDesigners { get; init; }

    public bool CanReadContent { get; init; }

    public bool CanSubmitArchive { get; init; }

    public int? DocumentCount { get; init; }

    public int? ModelDocumentCount { get; init; }

    public int? DrawingDocumentCount { get; init; }

    public string? BusinessStatus { get; init; }

    public string? RootDocumentCheckedOutBy { get; init; }
}

public sealed record ProjectOrganization(
    Guid Id,
    string Name,
    string ProjectCompanyCode,
    string ModelCompanyCode,
    string CrmCompanyName,
    bool IsActive,
    int CurrentProjectSequence = 0,
    int CurrentSerialSequence = 0);

public sealed record ProjectTypeDefinition(string Code, string Name, bool IsActive);

public sealed record EquipmentTypeDefinition(int Code, string Name, bool IsActive);

public sealed record PdmCustomer(
    Guid Id,
    string Code,
    string Name,
    bool IsActive,
    string SourceSystem = "legacy",
    DateTimeOffset? LastSyncedAt = null);

/// <summary>
/// 图纸转换（2D工程图转PDF、3D零件/装配转STEP）的执行位置。
/// Local：由API服务器本机进程调用SolidWorks转换程序；Remote：调用独立转图电脑上的转图代理。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreviewConversionMode
{
    Local,
    Remote
}

public sealed record PreviewConversionSettings
{
    public static PreviewConversionSettings Default { get; } = new();

    public PreviewConversionMode Mode { get; init; } = PreviewConversionMode.Local;

    /// <summary>转图代理地址，例如 http://192.168.2.50:5199。</summary>
    public string AgentUrl { get; init; } = string.Empty;

    /// <summary>转图代理访问令牌；两侧配置一致即可。</summary>
    public string AgentToken { get; init; } = string.Empty;

    public int TimeoutMinutes { get; init; } = 30;

    public string NormalizedAgentUrl => AgentUrl.Trim().TrimEnd('/');
}

public sealed record PdmSystemSettings(string VaultRoot, string ReleaseRoot)
{
    public static IReadOnlyList<string> DefaultReleaseChangeReasonTypes { get; } =
        [
            "正式补充",
            "物料问题 / 交期不满足", "物料问题 / 物料下单晚", "物料问题 / 买错物料", "物料问题 / 物料漏买",
            "图纸问题 / 图纸漏下", "图纸问题 / 图纸错误",
            "设计问题 / 设计变更", "设计问题 / 设计错误",
            "客户原因 / 客户需求变更", "客户原因 / 客户信息输入错误", "客户原因 / 客户未及时确认", "客户原因 / 客户未及时提供产品",
            "其他"
        ];

    public int CheckoutHeartbeatSeconds { get; init; } = 180;

    public string MaterialAttachmentRoot { get; init; } = string.Empty;

    public int CheckoutLeaseMinutes { get; init; } = 15;

    public int CheckoutOfflineGraceMinutes { get; init; } = 60;

    public int CheckoutReminderHours { get; init; } = 4;

    public int CheckoutStrongReminderHours { get; init; } = 8;

    public int CheckoutOverdueHours { get; init; } = 24;

    public int CheckoutForceReleaseHours { get; init; } = 48;

    public string BomDrawingNumberProperty { get; init; } = "物料编码";

    public string BomNameProperty { get; init; } = "物料名称";

    public string BomDescriptionProperty { get; init; } = "备注";

    public string BomMaterialProperty { get; init; } = "材质";

    public string BomSpecificationProperty { get; init; } = "型号";

    public string BomUnitProperty { get; init; } = "单位";

    public string BomBrandProperty { get; init; } = "品牌";

    public string BomSurfaceTreatmentProperty { get; init; } = "表面处理";

    public string BomWeightProperty { get; init; } = "重量";

    public IReadOnlyList<BomPropertyMapping> BomPropertyMappings { get; init; } = Array.Empty<BomPropertyMapping>();

    public BomValidationRules ValidationRules { get; init; } = BomValidationRules.Default;

    public ReleaseApprovalSettings ApprovalWorkflows { get; init; } = ReleaseApprovalSettings.Default;

    public MaterialCodeApprovalSettings MaterialCodeApproval { get; init; } = MaterialCodeApprovalSettings.Default;

    public IReadOnlyList<string> ReleaseChangeReasonTypes { get; init; } = DefaultReleaseChangeReasonTypes;

    public ProgramTemplateOptionCatalog ProgramTemplateOptions { get; init; } = ProgramTemplateOptionCatalog.Empty;

    public EngineeringKitOptionCatalog EngineeringKitOptions { get; init; } = EngineeringKitOptionCatalog.Empty;

    public FormalSupplementPolicies FormalSupplementPolicies { get; init; } = FormalSupplementPolicies.Default;

    public DrawingQrPolicy DrawingQrPolicy { get; init; } = DrawingQrPolicy.Default;

    public PreviewConversionSettings PreviewConversion { get; init; } = PreviewConversionSettings.Default;
}

public sealed record DrawingQrPolicy(
    bool Enabled,
    string SourceProperty,
    string RuleVersion,
    int SizeMillimeters,
    int MarginMillimeters,
    bool EverySheet)
{
    public static DrawingQrPolicy Default { get; } = new(true, "型号", "1", 20, 5, true);
}

public sealed record FormalSupplementPolicy(int? MaximumCount, int? ValidDays)
{
    public static FormalSupplementPolicy Default { get; } = new(2, null);
}

public sealed record FormalSupplementPolicies(FormalSupplementPolicy Standard, FormalSupplementPolicy Electrical)
{
    public static FormalSupplementPolicies Default { get; } = new(FormalSupplementPolicy.Default, FormalSupplementPolicy.Default);
}

public sealed record ReleaseChangeReasonSelection(
    string CategoryCode,
    string ReasonCode,
    string Category,
    string Reason,
    string? Detail = null);

public sealed record MaterialCodeApprovalSettings(int Version, IReadOnlyList<string> ApproverRoleCodes)
{
    public static MaterialCodeApprovalSettings Default { get; } = new(1, [UserRole.ProcessReviewer.ToString(), UserRole.Approver.ToString()]);
}

public sealed record ApprovalWorkflowStepTemplate(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ApprovalStage Stage,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ApprovalAssigneeSource AssigneeSource,
    string? FixedAssignee = null);

public sealed record ApprovalWorkflowTemplate(
    string Code,
    string Name,
    int Version,
    IReadOnlyList<ApprovalWorkflowStepTemplate> Steps);

public sealed record ReleaseApprovalSettings(
    ApprovalWorkflowTemplate Mechanical,
    ApprovalWorkflowTemplate Electrical,
    string EmergencySubstituteRoleCode,
    ApprovalWorkflowTemplate? ValidationPlan = null)
{
    public static ReleaseApprovalSettings Default { get; } = new(
        new ApprovalWorkflowTemplate(
            "mechanical-release",
            "机械发布审批",
            1,
            [
                new(ApprovalStage.MechanicalEngineer, "机械工程师自检", ApprovalAssigneeSource.Submitter),
                new(ApprovalStage.MainDesigner, "主设审核", ApprovalAssigneeSource.ProjectDesignLead),
                new(ApprovalStage.MechanicalSupervisor, "机械主管批准", ApprovalAssigneeSource.PrimaryUnitManager)
            ]),
        new ApprovalWorkflowTemplate(
            "electrical-release",
            "电气发布审批",
            1,
            [
                new(ApprovalStage.HardwareEngineer, "硬件工程师自检", ApprovalAssigneeSource.Submitter),
                new(ApprovalStage.HardwareSupervisor, "硬件主管审核", ApprovalAssigneeSource.PrimaryUnitManager),
                new(ApprovalStage.StandardizationSupervisor, "标准化主管批准", ApprovalAssigneeSource.ParentUnitManager)
            ]),
        UserRole.BusinessUnitManager.ToString(),
        new ApprovalWorkflowTemplate(
            "validation-plan",
            "验证计划审批",
            1,
            [
                new(ApprovalStage.MechanicalEngineer, "编制人自检", ApprovalAssigneeSource.Submitter),
                new(ApprovalStage.MainDesigner, "主设审核", ApprovalAssigneeSource.ProjectDesignLead),
                new(ApprovalStage.MechanicalSupervisor, "机械主管批准", ApprovalAssigneeSource.PrimaryUnitManager)
            ]));

    public static ReleaseApprovalSettings UseOrganizationHierarchy(ReleaseApprovalSettings? settings)
    {
        settings ??= Default;

        static ApprovalWorkflowTemplate Upgrade(
            ApprovalWorkflowTemplate template,
            IReadOnlyDictionary<ApprovalStage, ApprovalAssigneeSource> sources)
        {
            var changed = false;
            var steps = template.Steps.Select(step =>
            {
                if (!sources.TryGetValue(step.Stage, out var source) || step.AssigneeSource == source)
                    return step;
                changed = true;
                return step with { AssigneeSource = source, FixedAssignee = null };
            }).ToArray();
            return changed ? template with { Version = template.Version + 1, Steps = steps } : template;
        }

        return settings with
        {
            Mechanical = Upgrade(settings.Mechanical, new Dictionary<ApprovalStage, ApprovalAssigneeSource>
            {
                [ApprovalStage.MechanicalSupervisor] = ApprovalAssigneeSource.PrimaryUnitManager
            }),
            Electrical = Upgrade(settings.Electrical, new Dictionary<ApprovalStage, ApprovalAssigneeSource>
            {
                [ApprovalStage.HardwareSupervisor] = ApprovalAssigneeSource.PrimaryUnitManager,
                [ApprovalStage.StandardizationSupervisor] = ApprovalAssigneeSource.ParentUnitManager
            }),
            ValidationPlan = Upgrade(settings.ValidationPlan ?? Default.ValidationPlan!, new Dictionary<ApprovalStage, ApprovalAssigneeSource>
            {
                [ApprovalStage.MechanicalEngineer] = ApprovalAssigneeSource.Submitter,
                [ApprovalStage.MainDesigner] = ApprovalAssigneeSource.ProjectDesignLead,
                [ApprovalStage.MechanicalSupervisor] = ApprovalAssigneeSource.PrimaryUnitManager
            })
        };
    }
}

public sealed record ProjectNumberingOptions(
    IReadOnlyList<ProjectOrganization> Organizations,
    IReadOnlyList<ProjectTypeDefinition> ProjectTypes,
    IReadOnlyList<EquipmentTypeDefinition> EquipmentTypes);

public sealed record OrganizationUnit(
    Guid Id,
    Guid OrganizationId,
    Guid? ParentUnitId,
    string Code,
    string Name,
    OrganizationUnitKind Kind,
    bool IsActive,
    int SortOrder,
    bool CanManufacture = false);

public sealed record OrganizationMembership(Guid UnitId, string Username, bool IsPrimary);

public sealed record OrganizationUnitManagers(Guid UnitId, string PrimaryManager, IReadOnlyList<string> CollaborativeManagers);

public sealed record OrganizationDirectoryUser(
    string Username,
    string DisplayName,
    UserRole Role,
    bool IsActive,
    string? RoleCode = null,
    Guid? CompanyId = null,
    bool CrossCompanyView = false,
    IReadOnlyList<Guid>? AccessibleCompanyIds = null,
    IReadOnlyList<string>? RoleCodes = null)
{
    public string EffectiveRoleCode => string.IsNullOrWhiteSpace(RoleCode) ? Role.ToString() : RoleCode;
    public IReadOnlyList<string> EffectiveRoleCodes => (RoleCodes ?? [])
        .Prepend(EffectiveRoleCode)
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record OrganizationDirectory(
    IReadOnlyList<ProjectOrganization> Organizations,
    IReadOnlyList<OrganizationUnit> Units,
    IReadOnlyList<OrganizationMembership> Memberships,
    IReadOnlyList<OrganizationUnitManagers> Managers,
    IReadOnlyList<OrganizationDirectoryUser> Users);

public sealed record PermissionDefinition(
    string Code,
    string Name,
    string Module,
    string? Description = null,
    bool Sensitive = false);

public sealed record RoleDefinition(
    string RoleCode,
    string Name,
    string Description,
    UserRole BaseRole,
    bool IsSystem = false,
    bool IsSystemAdministrator = false);

public sealed record RolePermissionSettings(
    string Role,
    string Name,
    string Description,
    UserRole BaseRole,
    bool IsSystem,
    bool IsSystemAdministrator,
    IReadOnlyList<string> Permissions,
    int UserCount = 0);

public sealed record RolePermissionDirectory(
    IReadOnlyList<PermissionDefinition> Permissions,
    IReadOnlyList<RolePermissionSettings> Roles);

public sealed record PdmDocument(
    Guid Id,
    Guid ProjectId,
    string DrawingNumber,
    string Name,
    string FileName,
    DocumentKind Kind,
    DocumentLifecycleState State,
    RevisionLabel Revision,
    string? CheckedOutBy,
    DateTimeOffset UpdatedAt)
{
    public long RowVersion { get; init; } = 1;

    public Guid? FolderId { get; init; }

    public int? StoredVersionCount { get; init; }

    public DateTimeOffset? CheckedOutAt { get; init; }

    public Guid? CheckoutSessionId { get; init; }

    public string? CheckoutMachine { get; init; }

    public DateTimeOffset? CheckoutLastHeartbeatAt { get; init; }

    public DateTimeOffset? CheckoutLeaseExpiresAt { get; init; }

    public string? CheckoutReleaseRequestedBy { get; init; }

    public DateTimeOffset? CheckoutReleaseRequestedAt { get; init; }

    public string? CheckoutReleaseRequestReason { get; init; }

    public bool DrawingReviewLocked { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public string? DeletedBy { get; init; }

    public string? DeleteReason { get; init; }

    public DateTimeOffset? PurgedAt { get; init; }
}

public sealed record DocumentModelDrawingRelation(
    Guid ModelDocumentId,
    Guid DrawingDocumentId);

public sealed record DocumentWhereUsed(
    Guid DocumentId,
    Guid ParentDocumentId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string ParentDrawingNumber,
    string ParentName,
    string ParentFileName,
    DocumentKind ParentKind,
    DocumentLifecycleState ParentState,
    RevisionLabel ParentRevision,
    string InstancePath,
    string Configuration,
    int Quantity);

public sealed record EditSessionHeartbeat(
    Guid SessionId,
    DateTimeOffset ServerTime,
    DateTimeOffset LeaseExpiresAt,
    IReadOnlyList<Guid> ActiveDocumentIds,
    IReadOnlyList<Guid> LostDocumentIds,
    PdmSystemSettings Settings);

public sealed record EditLockSummary(
    Guid DocumentId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string DrawingNumber,
    string DocumentName,
    string FileName,
    string CheckedOutBy,
    DateTimeOffset CheckedOutAt,
    string? CheckoutMachine,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset LeaseExpiresAt,
    EditLockConnectionState ConnectionState,
    EditLockAttentionLevel AttentionLevel,
    string? ReleaseRequestedBy,
    DateTimeOffset? ReleaseRequestedAt,
    string? ReleaseRequestReason,
    bool OwnedByCurrentUser,
    bool CanRequestRelease,
    bool CanForceRelease);

public sealed record FolderPermissionRule(
    Guid Id,
    FolderPrincipalType PrincipalType,
    string PrincipalKey,
    FolderAccess Access);

public sealed record ProjectFolder(
    Guid Id,
    Guid RootProjectId,
    Guid? ParentFolderId,
    Guid? TargetProjectId,
    string FolderKey,
    string TemplateKey,
    string Name,
    ProjectFolderPurpose Purpose,
    int SortOrder,
    bool IsSystem,
    bool InheritPermissions)
{
    public FolderAccess EffectiveAccess { get; init; }

    public IReadOnlyList<FolderPermissionRule> Permissions { get; init; } = [];
}

public sealed record ProjectFolderTemplateNode(
    string FolderKey,
    string? ParentKey,
    string Name,
    ProjectFolderPurpose Purpose,
    int SortOrder,
    bool IsSystem,
    bool InheritPermissions)
{
    public IReadOnlyList<FolderPermissionRule> Permissions { get; init; } = [];
}

public sealed record BomItem(
    Guid Id,
    Guid ProjectId,
    BomKind Kind,
    int Sequence,
    string DrawingNumber,
    string Name,
    decimal Quantity,
    string Unit,
    string? Material,
    string? Specification,
    string Revision,
    bool IsComplete)
{
    public Guid ReleaseTrackingId { get; init; } = Id;

    public string? Remark { get; init; }

    public string? Brand { get; init; }

    public string? SurfaceTreatment { get; init; }

    public string? HeatTreatment { get; init; }

    public string? Weight { get; init; }

    public bool IsWearPart { get; init; }

    public string? ImpactStage { get; init; }

    public Guid? SourceDocumentId { get; init; }

    public string? SourceConfiguration { get; init; }

    public string? SourceInstancePath { get; init; }

    public string? ParentDrawingNumber { get; init; }

    public Guid? EngineeringKitReferenceId { get; init; }

    public Guid? EngineeringKitId { get; init; }

    public Guid? EngineeringKitRevisionId { get; init; }

    public string? EngineeringKitCode { get; init; }

    public int? EngineeringKitVersionNumber { get; init; }

    public Guid? EngineeringKitComponentId { get; init; }

    public bool EngineeringKitComponentOptional { get; init; }

    public string Source { get; init; } = "Manual";

    public bool IsManuallyOverridden { get; init; }

    public bool IsPendingRemoval { get; init; }

    public bool IsPendingClassification { get; init; }

    public bool IsManualUnmatched { get; init; }

    public bool IsManuallyRetained { get; init; }

    public bool IsManuallyExcluded { get; init; }

    public bool IsReleaseExcluded { get; init; }

    public string? ReleaseExclusionReason { get; init; }

    public string? ReconciliationStatus { get; init; }

    public string? ReconciliationNote { get; init; }

    public string? ReconciliationUpdatedBy { get; init; }

    public DateTimeOffset? ReconciliationUpdatedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public string? DeletedBy { get; init; }

    public string? DeleteReason { get; init; }

    public CadPropertyWritebackStatus? PropertyWritebackStatus { get; init; }
}

public sealed record BomEmptyDeclaration(BomKind Kind, bool DeclaredEmpty, string? UpdatedBy, DateTimeOffset? UpdatedAt);

public sealed record ProjectBomHeaderBinding(
    Guid ProjectId,
    ProjectBomHeaderKind Kind,
    ProjectBomHeaderKind? ParentKind,
    Guid MaterialId,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion);

public sealed record BomVersion(
    Guid Id,
    Guid ProjectId,
    BomKind Kind,
    int VersionNumber,
    string Label,
    BomVersionState State,
    Guid? BaseVersionId,
    string? ChangeNumber,
    string? ChangeReason,
    string? EffectiveSerialFrom,
    string? EffectiveSerialTo,
    IReadOnlyList<BomItem> Items,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReleasedAt)
{
    public IReadOnlyList<string> ValidationRequiredFields { get; init; } = [];

    public Guid? MotherMaterialId { get; init; }
}

public sealed record ManufacturingBomBaseline(
    Guid Id,
    Guid ProjectId,
    int Sequence,
    string Label,
    Guid StandardBomVersionId,
    Guid NonStandardBomVersionId,
    Guid ElectricalBomVersionId,
    string ChangeNumber,
    string ChangeReason,
    string EffectiveSerialFrom,
    string? EffectiveSerialTo,
    Guid ReleasePackageId,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record CadPropertyWriteback(
    Guid Id,
    Guid ProjectId,
    Guid BomItemId,
    Guid SourceDocumentId,
    string? SourceConfiguration,
    Guid ExpectedVersionId,
    string ExpectedRevision,
    IReadOnlyDictionary<string, string?> Properties,
    CadPropertyWritebackStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt)
{
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public Guid? ResultVersionId { get; init; }

    public string? LastError { get; init; }
}

/// <summary>属性回写产生的版本：用于把"仅属性写入"的版本变化与真实内容修改区分开。</summary>
public sealed record CadPropertyWritebackVersion(
    Guid DocumentId,
    Guid VersionId,
    string Revision);

public sealed record ApprovalTask(
    Guid Id,
    Guid ReleasePackageId,
    ApprovalStage Stage,
    string Assignee,
    string? DecisionBy,
    ApprovalDecision? Decision,
    string? Comment,
    DateTimeOffset? DecidedAt)
{
    public int StepOrder { get; init; }

    public string? StepName { get; init; }

    public bool IsEmergencySubstitute { get; init; }

    public string? EmergencyReason { get; init; }
}

public sealed record PendingApprovalTask(
    Guid Id,
    Guid ReleasePackageId,
    string ReleasePackageNumber,
    ApprovalStage Stage,
    ReleasePackageState PackageState,
    string Assignee,
    DateTimeOffset CreatedAt);

/// <summary>发布预览（转图）状态：转图是发布之后的独立事项，失败不影响发布结果。</summary>
public static class ReleasePreviewState
{
    public const string None = "None";
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public sealed record ReleasePackage(
    Guid Id,
    Guid ProjectId,
    string Number,
    ReleasePackageState State,
    Guid? ReferenceSnapshotId,
    string MechanicalBomRevision,
    string ElectricalBomRevision,
    IReadOnlyList<ApprovalTask> ApprovalTasks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedPath)
{
    public Guid? StandardBomVersionId { get; init; }

    public Guid? NonStandardBomVersionId { get; init; }

    public Guid? ElectricalBomVersionId { get; init; }

    public string? StandardBomRevision { get; init; }

    public string? NonStandardBomRevision { get; init; }

    public IReadOnlyList<BomItem> StandardBomSnapshot { get; init; } = [];

    public IReadOnlyList<BomItem> NonStandardBomSnapshot { get; init; } = [];

    public string? ChangeNumber { get; init; }

    public string? ChangeReason { get; init; }

    public string? EffectiveSerialFrom { get; init; }

    public string? EffectiveSerialTo { get; init; }

    public IReadOnlyList<BomItem> MechanicalBomSnapshot { get; init; } = [];

    public IReadOnlyList<BomItem> ElectricalBomSnapshot { get; init; } = [];

    public string? PublishError { get; init; }

    /// <summary>
    /// 转图（发布预览 STEP/PDF 生成）状态：与发布解耦，转图失败不影响发布结果，由后台单独重试并把结果反馈给相关人。
    /// </summary>
    public string PreviewState { get; init; } = ReleasePreviewState.None;

    public string? PreviewError { get; init; }

    public int PreviewAttempts { get; init; }

    public DateTimeOffset? PreviewUpdatedAt { get; init; }

    public ReleaseScope Scope { get; init; } = ReleaseScope.LegacyCombined;

    public string? WorkflowCode { get; init; }

    public int WorkflowVersion { get; init; }

    public IReadOnlyList<Guid> SelectedBomItemIds { get; init; } = [];

    public bool CreatesManufacturingBaseline { get; init; } = true;

    public bool LocksDocuments { get; init; } = true;

    public int WholeSetMultiplier { get; init; } = 1;

    public IReadOnlyList<ReleaseChangeReasonSelection> ChangeReasonSelections { get; init; } = [];

    public bool FormalSupplementPolicySnapshotted { get; init; }

    public int? FormalSupplementMaximumCount { get; init; }

    public int? FormalSupplementValidDays { get; init; }

    /// <summary>发图提示信息，不属于不可变的图档内容或生产排产状态。</summary>
    public string DrawingPriority { get; init; } = "Normal";

    public DateOnly? DrawingRequiredOn { get; init; }

    public IReadOnlyDictionary<Guid, DrawingDeliveryOverride> DrawingDeliveryOverrides { get; init; } =
        new Dictionary<Guid, DrawingDeliveryOverride>();
}

public sealed record DrawingDeliveryOverride(string Priority, DateOnly RequiredOn);

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string EntityType,
    string EntityId,
    string Detail);
