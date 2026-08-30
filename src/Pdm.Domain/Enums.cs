namespace Upton.Pdm.Domain;

public enum EditLockConnectionState
{
    Active,
    OfflineGrace,
    Offline
}

public enum EditLockAttentionLevel
{
    Normal,
    Reminder,
    StrongReminder,
    Overdue,
    Reclaimable
}

public enum UserRole
{
    Engineer = 0,
    PlanningManager = 1,
    ProcessReviewer = 2,
    Approver = 3,
    ProductionViewer = 4,
    Administrator = 5,
    BusinessUnitManager = 6,
    PlatformAdministrator = 7
}

public enum OrganizationUnitKind
{
    BusinessDivision,
    Department,
    Team
}

public enum ProjectAssignmentType
{
    PrimaryProjectManager,
    CollaborativeProjectManager,
    DesignLead,
    Designer
}

public enum DocumentKind
{
    Assembly,
    Part,
    Drawing,
    Pdf,
    Dwg,
    Other
}

public enum DocumentPreviewFormat
{
    Step,
    Pdf
}

public enum DocumentRegistrationMatchKind
{
    New,
    SameNameSameContent,
    SameNameDifferentContent,
    SameContentDifferentName,
    SameContentOtherProject
}

public enum DocumentLifecycleState
{
    Work,
    InReview,
    Released,
    Obsolete
}

[Flags]
public enum FolderAccess
{
    None = 0,
    View = 1,
    Download = 2,
    Upload = 4,
    Edit = 8,
    Delete = 16,
    ManagePermissions = 32,
    Publish = 64,
    All = View | Download | Upload | Edit | Delete | ManagePermissions | Publish
}

public enum FolderPrincipalType
{
    Role,
    User
}

public enum ProjectFolderPurpose
{
    Root,
    MechanicalRoot,
    ElectricalRoot,
    ProjectContainer,
    Release,
    Standard
}

public enum DocumentVersionStatus
{
    Work,
    Released
}

public enum ReferenceNodeStatus
{
    Normal,
    Suppressed,
    Hidden,
    Lightweight,
    Virtual,
    Missing
}

public enum ReferenceChangeKind
{
    Added,
    Removed,
    Replaced,
    Moved,
    ConfigurationChanged,
    QuantityChanged,
    StatusChanged
}

public enum SnapshotChangeKind
{
    Added,
    Removed,
    Modified
}

public enum BomChangeKind
{
    Added,
    Removed,
    QuantityChanged,
    MaterialChanged,
    SpecificationChanged,
    RevisionChanged
}

public enum BomKind
{
    Mechanical = 0,
    Electrical = 1,
    Standard = 2,
    NonStandard = 3,
    Unclassified = 4,
    Virtual = 5
}

public enum BomVersionState
{
    Draft = 0,
    InReview = 1,
    Released = 2,
    Obsolete = 3
}

public enum ProjectBomHeaderKind
{
    Master = 0,
    Standard = 1,
    NonStandard = 2,
    Electrical = 3
}

public enum CadPropertyWritebackStatus
{
    Pending = 0,
    InProgress = 1,
    Succeeded = 2,
    Conflict = 3,
    Failed = 4,
    Superseded = 5,
    PendingSave = 6
}

public enum ApprovalStage
{
    ProcessReview = 1,
    Approval = 2,
    MechanicalEngineer = 10,
    MainDesigner = 20,
    MechanicalSupervisor = 30,
    HardwareEngineer = 40,
    HardwareSupervisor = 50,
    StandardizationSupervisor = 60
}

public enum ApprovalAssigneeSource
{
    Submitter,
    ProjectDesignLead,
    FixedUser,
    PrimaryUnitManager,
    ParentUnitManager
}

public enum ReleaseScope
{
    LegacyCombined,
    StandardLongLead,
    StandardFormal,
    StandardSupplement,
    ElectricalFormal,
    ElectricalSupplement,
    NonStandardWithDrawing
}

public enum ApprovalDecision
{
    Approved,
    Rejected
}

public enum ReleasePackageState
{
    Draft,
    ProcessReview,
    Approval,
    Rejected,
    Publishing,
    Published,
    PublishFailed
}
