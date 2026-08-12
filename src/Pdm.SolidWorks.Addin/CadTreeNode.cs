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

    public string InstancePath { get; set; } = string.Empty;

    public string ComponentSelectionName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public CadDocumentKind Kind { get; set; }

    public string Configuration { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    public CadReferenceStatus Status { get; set; }

    public string Revision { get; set; } = string.Empty;

    public string CurrentRevision { get; set; } = string.Empty;

    public string LatestRevision { get; set; } = string.Empty;

    public string CheckedOutBy { get; set; }

    public CadWorkState WorkState { get; set; }

    public string LatestVersionSha256 { get; set; } = string.Empty;

    public bool IsModifiedInSolidWorks { get; set; }

    public bool IsHistoricalPreview { get; set; }

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
