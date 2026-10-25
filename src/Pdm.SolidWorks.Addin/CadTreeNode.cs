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

internal enum CadReferenceRole
{
    Component,
    ExternalFileDependency
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
    public string SuppressedLocalStructureSha256 { get; set; }
    public HistoricalReferenceRepair HistoricalRepair { get; set; }
    public bool NativeIsolationOnly { get; set; }

    public Guid NodeId { get; set; } = Guid.NewGuid();

    public Guid? DocumentId { get; set; }

    public Guid? ProvenanceDocumentId { get; set; }

    public Guid? ProvenanceProjectId { get; set; }

    public bool IsExternalProvenance { get; set; }

    public Guid? RelatedModelDocumentId { get; set; }

    public string InstancePath { get; set; } = string.Empty;

    public string ComponentSelectionName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    // The native reference spelling can differ from the verified local working-copy path.
    public string SourceReferencePath { get; set; } = string.Empty;

    // 3D Interconnect's generated native cache is backed by this imported model.
    public string ImportedSourcePath { get; set; } = string.Empty;

    public bool IsInterconnectImport => !string.IsNullOrWhiteSpace(ImportedSourcePath);

    // Managed storage ownership, not IComponent2.IsVirtual. Interconnect can persist
    // components inside a native container while exposing IsVirtual=false at runtime.
    public string ImportedStorageOwnerPath { get; set; } = string.Empty;
    public bool IsStoredImport => !string.IsNullOrWhiteSpace(ImportedStorageOwnerPath);

    public bool IsImportedSourceFile => Kind == CadDocumentKind.Other && Role == CadReferenceRole.ExternalFileDependency;

    public CadReferenceRole Role { get; set; }

    public List<string> UnmanagedReferences { get; } = new List<string>();
    public List<string> EmbeddedDesignDependencies { get; } = new List<string>();
    public IReadOnlyDictionary<string, string> EmbeddedProperties { get; set; }

    public HashSet<string> DrawingScopePaths { get; set; }

    public bool RequiresModelUpdate { get; set; }
    public bool DrawingReferenceIndexPending { get; set; }
    public string DrawingUpdatePendingReason { get; set; }

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
    public bool IsSuppressed { get; set; }

    public string Revision { get; set; } = string.Empty;

    public string CurrentRevision { get; set; } = string.Empty;

    public DocumentVersionDto SuppressedArchiveVersion { get; set; }
    public string SuppressedArchiveSha256 { get; set; }

    public string LatestRevision { get; set; } = string.Empty;
    public string DrawingVersion { get; set; } = "待核对";
    public string PublishedVersion { get; set; } = "未发布";
    public string BusinessVersionText => string.Concat(DrawingVersion, " / ", PublishedVersion);

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

    public bool DrawingReviewLocked { get; set; }

    public CadWorkState WorkState { get; set; }

    public string LatestVersionSha256 { get; set; } = string.Empty;

    public string LatestStoredSha256 { get; set; } = string.Empty;

    public bool IsModifiedInSolidWorks { get; set; }
    public string ArchiveReadError { get; set; }
    public bool NativeFileScopeVerified { get; set; }
    internal ISet<string> NativeFileScopePaths { get; set; }
    public bool IsOutsideNativeFileScope { get; set; }
    public bool StructureIndexPending { get; set; }
    // Candidate discovery is not proof that every native drawing view was read.
    public bool DrawingViewsRead { get; set; }
    // Server state is separate from the completeness of the latest local scan.
    public bool? LatestStructureIndexPending { get; set; }
    public bool HasUnverifiedDrawingIndex => Kind == CadDocumentKind.Drawing
        && !DrawingViewsRead && LatestStructureIndexPending == true;

    public bool IsRenamePendingSave { get; set; }

    public bool IsHistoricalPreview { get; set; }

    public bool IsLatestReadOnlyPreview { get; set; }

    public bool IsReadOnlyPreview => IsHistoricalPreview || IsLatestReadOnlyPreview;

    public List<CadTreeNode> Children { get; } = new List<CadTreeNode>();
    public List<CadTreeNode> ReferencedModels { get; } = new List<CadTreeNode>();

    public bool HasBlockingIssue
    {
        get
        {
            if (IsOutsideNativeFileScope) return false;
            if (Status == CadReferenceStatus.Missing || !NativeFileScopeVerified && !string.IsNullOrWhiteSpace(ArchiveReadError))
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
