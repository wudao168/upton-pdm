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
    StatusChanged
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
