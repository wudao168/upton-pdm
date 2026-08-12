namespace Upton.Pdm.Domain;

public enum UserRole
{
    Engineer,
    ProcessReviewer,
    Approver,
    ProductionViewer,
    Administrator
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

public enum DocumentLifecycleState
{
    Work,
    InReview,
    Released,
    Obsolete
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
