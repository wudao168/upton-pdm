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
    Engineer,
    PlanningManager,
    ProcessReviewer,
    Approver,
    ProductionViewer,
    Administrator
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
    Mechanical,
    Electrical
}

public enum ApprovalStage
{
    ProcessReview = 1,
    Approval = 2
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
