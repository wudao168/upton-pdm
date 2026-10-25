using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal enum ArchiveSelectionState { Pending, New, Changed, Unchanged, DrawingUpdate }

public sealed partial class PdmAddin
{
    private BatchOperationDialog CreateBatchOperationDialog(CadTreeNode root, IReadOnlyList<BatchOperationItem> items,
        IReadOnlyList<ProjectDto> projects, Guid? projectId, string username, BatchOperationKind operation,
        IReadOnlyCollection<string> selectedPaths, IReadOnlyDictionary<string, Guid> inheritedProjects,
        IReadOnlyDictionary<string, string> displayNames, bool canUseStandardLibrary, bool standardLibrary, int drawingType)
    {
        var dialog = new BatchOperationDialog(root, items, projects, projectId, username, operation,
            selectedPaths, inheritedProjects, displayNames, canUseStandardLibrary, standardLibrary, drawingType);
        dialog.LoadArchiveVersionsAsync = async (node, token) => await apiClient.GetVersionsAsync(node.DocumentId.Value, token, 1);
        return dialog;
    }

    internal static ArchiveSelectionState AnalyzeArchiveNode(CadTreeNode node, string username,
        IReadOnlyList<DocumentVersionDto> versions, string localHash)
    {
        if (!node.DocumentId.HasValue || versions != null && versions.Count == 0
            || versions == null && node.StoredVersionStateKnown && !node.HasStoredVersion) return ArchiveSelectionState.New;
        if (node.Kind == CadDocumentKind.Drawing && node.RequiresModelUpdate) return ArchiveSelectionState.DrawingUpdate;
        if (node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved || node.IsRenamePendingSave
            || !string.IsNullOrWhiteSpace(node.DrawingUpdatePendingReason)) return ArchiveSelectionState.Pending;
        var latest = versions?.FirstOrDefault();
        node.DrawingReferenceIndexPending = DrawingModelIndexMissing(latest?.ReferenceSnapshot, node);
        if (latest == null && string.IsNullOrWhiteSpace(node.LatestVersionSha256) && string.IsNullOrWhiteSpace(node.LatestStoredSha256))
            return ArchiveSelectionState.Pending;
        var matches = latest != null ? VersionMatchesLocalFile(latest, node.FullPath, localHash) : NodeMatchesLatestFile(node, localHash);
        if (!matches)
            return latest != null && node.OpenedVersionId.HasValue && node.OpenedVersionId != latest.Id
                && !string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase) ? ArchiveSelectionState.Pending : ArchiveSelectionState.Changed;
        if (node.RequiresModelUpdate) return ArchiveSelectionState.Changed;
        // Identical native model bytes are not an engineering edit. A fuller file
        // inventory/index is completed separately without revising the model.
        if (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
            return ArchiveSelectionState.Unchanged;
        if (node.Role != CadReferenceRole.ExternalFileDependency && node.Kind == CadDocumentKind.Drawing)
        {
            var archived = latest?.ReferenceSnapshot;
            // Completing a legacy drawing index does not change its saved file.
            if (node.DrawingReferenceIndexPending && archived?.DocumentId == node.DocumentId
                && archived.Kind == (int)CadDocumentKind.Drawing
                && string.Equals(archived.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)) return ArchiveSelectionState.Unchanged;
            if (archived == null || node.StructureIndexPending || node.HasUnverifiedDrawingIndex) return ArchiveSelectionState.Pending;
            if (node.Kind == CadDocumentKind.Drawing && !node.DrawingViewsRead) return ArchiveSelectionState.Pending;
            var referencesMatch = ReferenceSnapshotMatchesTree(archived, node);
            if (!referencesMatch || ArchiveIndexWasCompleted(archived, node)) return ArchiveSelectionState.Changed;
        }
        return ArchiveSelectionState.Unchanged;
    }

    private static bool DrawingModelIndexMissing(DocumentReferenceNodeDto archived, CadTreeNode drawing) =>
        drawing.Kind == CadDocumentKind.Drawing && drawing.ReferencedModels.Count > 0
        && archived != null && !(archived.Children?.Any(child => child.DocumentId.HasValue
            && (child.Kind == (int)CadDocumentKind.Part || child.Kind == (int)CadDocumentKind.Assembly)
            && !string.IsNullOrWhiteSpace(child.Revision?.Display)) ?? false);

    private static bool HasPendingDrawingModelChange(CadTreeNode model, DocumentReferenceNodeDto archived,
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>> histories)
    {
        if (model.WorkState == CadWorkState.PendingCheckIn || model.WorkState == CadWorkState.ModifiedUnsaved
            || model.IsModifiedInSolidWorks && !string.IsNullOrWhiteSpace(model.CheckedOutBy)) return true;
        if (!model.DocumentId.HasValue || string.IsNullOrWhiteSpace(model.CurrentRevision)
            || !histories.TryGetValue(model.DocumentId.Value, out var versions)) return false;
        var revision = model.CurrentRevision.TrimEnd('*');
        var before = archived?.Children?.Where(child => child.DocumentId == model.DocumentId
            && !string.IsNullOrWhiteSpace(child.Revision?.Display)).ToArray() ?? Array.Empty<DocumentReferenceNodeDto>();
        if (before.Length == 0 || before.Any(child => string.Equals(child.Revision.Display, revision, StringComparison.OrdinalIgnoreCase))) return false;
        var current = versions.FirstOrDefault(version => string.Equals(version.Revision?.Display, revision, StringComparison.OrdinalIgnoreCase));
        return current != null && before.Any(child =>
        {
            var source = versions.FirstOrDefault(version => string.Equals(version.Revision?.Display, child.Revision.Display, StringComparison.OrdinalIgnoreCase));
            return source != null && !PdmApiClient.ReleasedArchiveBaseMatches(source, current);
        });
    }
}
