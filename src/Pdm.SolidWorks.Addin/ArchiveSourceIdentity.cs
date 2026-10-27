using System;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveSourceIdentity
{
    internal static bool RestoreForProject(CadTreeNode node, Guid? projectId)
    {
        // A source-directory import is not a controlled workspace file. Restore only
        // this machine/path's successful receipt, never a copied identity stream.
        if (node == null || node.IsStoredImport || node.Status == CadReferenceStatus.Virtual
            || PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var saved)
            || !saved.ProjectId.HasValue || !saved.VersionId.HasValue
            || node.DocumentId.HasValue && node.DocumentId != saved.DocumentId
            || node.ProvenanceDocumentId.HasValue && node.ProvenanceDocumentId != saved.DocumentId) return false;
        node.DocumentId = projectId == saved.ProjectId ? saved.DocumentId : (Guid?)null;
        node.ProvenanceDocumentId = saved.DocumentId;
        node.ProvenanceProjectId = saved.ProjectId;
        node.IsExternalProvenance = !node.DocumentId.HasValue;
        if (node.DocumentId.HasValue)
        {
            node.OpenedVersionId = saved.VersionId;
            node.OpenedRevision = saved.Revision;
        }
        return true;
    }
}
