using System;
using System.Collections.Generic;

namespace Upton.Pdm.SolidWorks;

internal enum CadDocumentKind
{
    Assembly,
    Part,
    Drawing,
    Pdf,
    Dwg,
    Other
}

internal enum CadReferenceStatus
{
    Normal,
    Suppressed,
    Hidden,
    Lightweight,
    Virtual,
    Missing
}

internal enum CadWorkState
{
    None,
    Editable,
    ModifiedUnsaved,
    PendingCheckIn,
    EditingByOther
}

internal sealed class CadTreeNode
{
    public Guid NodeId { get; set; } = Guid.NewGuid();

    public Guid? DocumentId { get; set; }

    public Guid? RelatedModelDocumentId { get; set; }

    public string InstancePath { get; set; } = string.Empty;

    public string ComponentSelectionName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DrawingNumber { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Material { get; set; } = string.Empty;

    public string LifecycleState { get; set; } = "Work";

    public DateTime? UpdatedAt { get; set; }

    public CadDocumentKind Kind { get; set; }

    public string Configuration { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public CadReferenceStatus Status { get; set; }

    public string Revision { get; set; } = string.Empty;

    public string CurrentRevision { get; set; } = string.Empty;

    public string LatestRevision { get; set; } = string.Empty;

    public bool StoredVersionStateKnown { get; set; }

    public bool HasStoredVersion { get; set; }

    public Guid? OpenedVersionId { get; set; }

    public string OpenedRevision { get; set; } = string.Empty;

    public string CheckedOutBy { get; set; }

    public DateTime? CheckedOutAt { get; set; }

    public Guid? CheckoutSessionId { get; set; }

    public string CheckoutMachine { get; set; } = string.Empty;

    public DateTime? CheckoutLastHeartbeatAt { get; set; }

    public bool CheckoutSessionLost { get; set; }

    public CadWorkState WorkState { get; set; }

    public string LatestVersionSha256 { get; set; } = string.Empty;

    public string LatestStoredSha256 { get; set; } = string.Empty;

    public bool IsModifiedInSolidWorks { get; set; }

    public bool IsRenamePendingSave { get; set; }

    public bool IsHistoricalPreview { get; set; }

    public bool IsLatestReadOnlyPreview { get; set; }

    public bool IsReadOnlyPreview => IsHistoricalPreview || IsLatestReadOnlyPreview;

    public List<CadTreeNode> Children { get; } = new List<CadTreeNode>();

    public bool HasBlockingIssue
    {
        get
        {
            if (Status == CadReferenceStatus.Missing)
            {
                return true;
            }

            foreach (var child in Children)
            {
                if (child.HasBlockingIssue)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
