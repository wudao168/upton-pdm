using System;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveSourceIdentity
{
    internal static bool RestoreForProject(CadTreeNode node, Guid? projectId)
    {
        // A source-directory import is not a controlled workspace file. Restore only
        // this machine/path's successful receipt, never a copied identity stream.
        if (node == null || node.IsStoredImport || node.Status == CadReferenceStatus.Virtual
            || PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath) || !projectId.HasValue) return false;
        if (!PdmDocumentIdentityStore.TryReadArchiveMetadata(node.FullPath, projectId.Value, out var saved)
            || !saved.ProjectId.HasValue || !saved.VersionId.HasValue)
        {
            if (!node.ProvenanceProjectId.HasValue || node.ProvenanceProjectId == projectId) return false;
            ClearForeignEditState(node);
            node.DocumentId = null; node.OpenedVersionId = null; node.OpenedRevision = string.Empty;
            node.IsExternalProvenance = true;
            return true;
        }
        if (saved.ProjectId != projectId || node.ProvenanceProjectId.HasValue && node.ProvenanceProjectId != projectId)
            ClearForeignEditState(node);
        var keepOpenedBase = node.DocumentId == saved.DocumentId && node.ProvenanceProjectId == projectId
            && node.OpenedVersionId.HasValue;
        node.DocumentId = projectId == saved.ProjectId ? saved.DocumentId : (Guid?)null;
        node.ProvenanceDocumentId = saved.DocumentId;
        node.ProvenanceProjectId = saved.ProjectId;
        node.IsExternalProvenance = !node.DocumentId.HasValue;
        // An established working copy can be based on a newer committed memory
        // snapshot while the untouched source file still has its older receipt.
        if (!keepOpenedBase)
        {
            node.OpenedVersionId = node.DocumentId.HasValue ? saved.VersionId : null;
            node.OpenedRevision = node.DocumentId.HasValue ? saved.Revision : string.Empty;
        }
        return true;
    }

    private static void ClearForeignEditState(CadTreeNode node)
    {
        // The copied source keeps its filesystem permissions. A former project's
        // checkout/review state is not authority over this independent import.
        node.CheckedOutBy = null; node.CheckedOutAt = null; node.CheckoutSessionId = null;
        node.CheckoutMachine = string.Empty; node.CheckoutLastHeartbeatAt = null;
        node.CheckoutSessionLost = false; node.DrawingReviewLocked = false;
        node.WorkState = node.IsModifiedInSolidWorks ? CadWorkState.ModifiedUnsaved : CadWorkState.None;
    }
}
