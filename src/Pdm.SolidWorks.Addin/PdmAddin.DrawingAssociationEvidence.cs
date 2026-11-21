using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // This proof is collected only by foreground open/permission operations.
    // Background tree refresh consumes it without native CAD calls.
    private readonly Dictionary<string, DrawingAssociationProof> drawingAssociationProofs =
        new Dictionary<string, DrawingAssociationProof>(StringComparer.OrdinalIgnoreCase);

    private sealed class DrawingAssociationProof
    {
        internal long Length;
        internal DateTime Written;
        internal string[] Models;
    }

    private bool DrawingAssociationDisproved(string drawingPath, IEnumerable<string> modelPaths, bool readNative)
    {
        if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath)) return false;
        var models = modelPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (models.Length == 0) return false;
        var file = new FileInfo(drawingPath);
        if (readNative && scanner != null)
        {
            var beforeLength = file.Length; var beforeWritten = file.LastWriteTimeUtc;
            var references = scanner.ReadDrawingReferences(drawingPath, out var error, true)
                .Where(path => DocumentKindFromPath(path) == CadDocumentKind.Part || DocumentKindFromPath(path) == CadDocumentKind.Assembly).ToArray();
            file.Refresh();
            // Unknown, missing and pure-2D candidates are not negative evidence.
            if (error != null || references.Length == 0 || references.Any(path => !File.Exists(path))
                || file.Length != beforeLength || file.LastWriteTimeUtc != beforeWritten)
            { drawingAssociationProofs.Remove(drawingPath); return false; }
            if (drawingAssociationProofs.Count >= 128 && !drawingAssociationProofs.ContainsKey(drawingPath)) drawingAssociationProofs.Clear();
            drawingAssociationProofs[drawingPath] = new DrawingAssociationProof { Length = file.Length,
                Written = file.LastWriteTimeUtc, Models = references };
        }
        if (!drawingAssociationProofs.TryGetValue(drawingPath, out var proof)
            || proof.Length != file.Length || proof.Written != file.LastWriteTimeUtc) return false;
        return !proof.Models.Any(reference => models.Any(model => PathsEqual(reference, model)
            || PdmDocumentIdentityStore.TryReadLocalMetadata(reference, out var left)
                && PdmDocumentIdentityStore.TryReadLocalMetadata(model, out var right)
                && left.DocumentId == right.DocumentId && left.ProjectId == right.ProjectId));
    }

    private IReadOnlyList<DocumentModelDrawingRelationDto> FilterDrawingRelations(
        IReadOnlyList<DocumentModelDrawingRelationDto> relations, string directory, bool readNative,
        ISet<Guid> selectedModels = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) return relations;
        var normalizedDirectory = NormalizeDirectory(directory);
        var files = controlledOpenManifests.Where(pair => PathsEqual(NormalizeDirectory(pair.Key), normalizedDirectory))
            .SelectMany(pair => pair.Value.Files).GroupBy(file => file.DocumentId).ToDictionary(group => group.Key, group => group.First());
        var result = new List<DocumentModelDrawingRelationDto>();
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in relations)
        {
            if (selectedModels != null && !selectedModels.Contains(relation.ModelDocumentId)
                || !files.TryGetValue(relation.DrawingDocumentId, out var drawing)
                || !files.TryGetValue(relation.ModelDocumentId, out var model)) { result.Add(relation); continue; }
            var drawingPath = ControlledReferenceMaterializer.ManifestPath(directory, drawing);
            var modelPath = ControlledReferenceMaterializer.ManifestPath(directory, model);
            if (!IsPathWithinDirectory(drawingPath, directory) || !IsPathWithinDirectory(modelPath, directory)
                || !DrawingAssociationDisproved(drawingPath, new[] { modelPath }, readNative && inspected.Add(drawingPath))) result.Add(relation);
            else if (readNative) LogOperation("已排除与实际直接引用不符的配套工程图：" + model.FileName + " -> " + drawing.FileName);
        }
        return result;
    }
}
