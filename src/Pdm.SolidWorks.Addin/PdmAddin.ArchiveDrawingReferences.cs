using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private IModelDoc2 ReadLinkedDrawingForArchive(CadTreeNode drawing, BatchCheckInPlan plan, Guid projectId, out bool opened)
    {
        var path = drawing.FullPath;
        var loaded = FindLoadedDocument(path);
        opened = loaded == null;
        if (plan.NewReferences == null || plan.NewReferences.Count == 0)
            return loaded ?? OpenDrawingForArchive(path);
        var context = FindControlledDrawingContext(path);
        if (context.Value == null) return loaded ?? OpenDrawingForArchive(path);
        var scope = CreateArchiveDrawingManifest(context.Key, context.Value, drawing, plan, projectId);
        if (loaded == null)
            return OpenVisibleDrawingForArchive(() => OpenControlledReadOnlyDocumentCore(path,
                ToSolidWorksDocumentType(CadDocumentKind.Drawing), string.Empty, scope, true, true));
        using var working = new ControlledWorkingCopyScope(path, scope);
        ControlledOpenBoundary.RequireLoadedSources(application, path, scope);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _ = ValidateHistoricalControlledOpenReferences(loaded, path, scope, application, missing)
            ?? ValidateControlledLoadedSources(loaded, path, scope, application, missing);
        return loaded;
    }

    // The server manifest describes a frozen version. Only this drawing preparation
    // may see an unregistered child that belongs to the current confirmed check-in.
    private ControlledOpenManifestDto CreateArchiveDrawingManifest(string directory, ControlledOpenManifestDto source,
        CadTreeNode drawing, BatchCheckInPlan plan, Guid projectId)
    {
        if (projectId == Guid.Empty || source.ProjectId != projectId || plan.RegistrationProjectId != projectId
            || plan.NewReferences == null || plan.NewReferences.Count == 0 || drawing.Kind != CadDocumentKind.Drawing
            || IsReadOnlyPreviewPath(drawing.FullPath) || IsHistoricalDerivedPath(drawing.FullPath)
            || !IsManifestMember(drawing)) return source;

        var models = plan.Items.Select(item => item.Node).Where(node => node.Kind == CadDocumentKind.Assembly)
            .ToDictionary(node => node.FullPath, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<CadTreeNode>(drawing.ReferencedModels.Where(model =>
            model.Kind == CadDocumentKind.Assembly && IsManifestMember(model)));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions = new Dictionary<string, RetainedLocalReference>(StringComparer.OrdinalIgnoreCase);
        var selectedNewModels = new HashSet<string>(plan.Items.Select(item => item.Node)
            .Where(node => IsNewArchiveModel(node, projectId)).Select(node => node.FullPath),
            StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var occurrence = pending.Pop();
            if (!visited.Add(occurrence.FullPath)) continue;
            if (!models.TryGetValue(occurrence.FullPath, out var owner)) owner = occurrence;
            foreach (var child in owner.Children.Where(child => child.Kind == CadDocumentKind.Assembly
                && child.Role == CadReferenceRole.Component && IsManifestMember(child))) pending.Push(child);

            if (!models.ContainsKey(owner.FullPath) || !IsManifestMember(owner) || !IsCurrentArchiveOwner(owner, projectId)) continue;
            var newChildren = owner.Children.Where(child => IsNewArchiveModel(child, projectId) && selectedNewModels.Contains(child.FullPath)
                && plan.NewReferences.ContainsKey(child.FullPath) && owner.ConfirmedArchivePaths?.Contains(child.FullPath) == true
                && IsPathWithinDirectory(child.FullPath, directory)).ToArray();
            if (newChildren.Length == 0 || !(FindLoadedDocument(owner.FullPath) is IAssemblyDoc assembly)) continue;
            var nativePaths = ReadDirectNativeComponentPaths(assembly);
            foreach (var child in newChildren.Where(child => nativePaths.Contains(child.FullPath)))
            {
                var path = Path.GetFullPath(child.FullPath);
                ControlledCacheMaintenance.RequireOrdinaryPath(path, directory);
                if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
                    && (metadata.DocumentId != Guid.Empty || metadata.ProjectId.HasValue && metadata.ProjectId != projectId)) continue;
                var name = Path.GetFileName(path);
                if (source.Files.Any(file => string.Equals(file.FileName, name, StringComparison.OrdinalIgnoreCase))
                    || source.RetainedLocalReferences.TryGetValue(name, out var retained) && !retained.Matches(path)
                    || additions.TryGetValue(name, out var existing) && !existing.Allows(path))
                    throw new InvalidOperationException("本次新增模型与受控引用存在同名冲突，请核对两个文件的路径：" + path);
                var record = new RetainedLocalReference { SourcePath = path, FileLength = new FileInfo(path).Length,
                    Sha256 = plan.NewReferences[path].SourceSha256, Editable = true, RequireExactDeferredPath = true };
                if (!record.Matches(path))
                    throw new InvalidOperationException("本次新增模型的内容在存档确认后发生变化，请重新检查存档范围：" + path);
                additions[name] = record;
            }
        }
        if (additions.Count == 0) return source;
        var scope = WithWorkingFiles(source, source.Files);
        scope.LatestReferenceVersionsConfirmed = source.LatestReferenceVersionsConfirmed;
        scope.ResumedDocumentIds.UnionWith(source.ResumedDocumentIds);
        scope.LocalCompatibilityNotes.AddRange(source.LocalCompatibilityNotes);
        foreach (var pair in source.RetainedWorkingHashes) scope.RetainedWorkingHashes.Add(pair.Key, pair.Value);
        foreach (var pair in source.RetainedLocalReferences) scope.RetainedLocalReferences.Add(pair.Key, pair.Value);
        foreach (var pair in additions) scope.RetainedLocalReferences[pair.Key] = pair.Value;
        return scope;

        bool IsManifestMember(CadTreeNode node) => node.DocumentId.HasValue && source.Files.Any(file =>
            file.DocumentId == node.DocumentId.Value && PathsEqual(node.FullPath, ControlledReferenceMaterializer.ManifestPath(directory, file)));
    }

    private static HashSet<string> ReadDirectNativeComponentPaths(IAssemblyDoc assembly)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in (assembly.GetComponents(true) as Array ?? Array.Empty<object>()).OfType<IComponent2>())
        {
            if (component.IsVirtual) continue;
            var path = (component.GetModelDoc2() as IModelDoc2)?.GetPathName();
            if (string.IsNullOrWhiteSpace(path)) path = component.GetPathName();
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) paths.Add(Path.GetFullPath(path));
        }
        return paths;
    }

    private static bool IsNewArchiveModel(CadTreeNode node, Guid projectId) =>
        !node.DocumentId.HasValue && (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
        && node.Role == CadReferenceRole.Component && !node.IsExternalProvenance && !node.IsStoredImport
        && (!node.ProvenanceProjectId.HasValue || node.ProvenanceProjectId == projectId)
        && node.Status != CadReferenceStatus.Virtual && IsNativeModelReference(node.FullPath);

    private bool IsCurrentArchiveOwner(CadTreeNode owner, Guid projectId) =>
        IsCheckedOutByCurrentUser(owner) && localEdits.TryGetValue(owner.DocumentId.Value, out var draft)
        && draft.ProjectId == projectId && draft.SessionId == checkoutSessionId && PathsEqual(draft.SourcePath, owner.FullPath)
        && !draft.Closed && !draft.PendingClose && !draft.RecoveryRequired && !draft.DiscardPending
        && !draft.CheckoutSessionLost && !draft.AcquisitionRollbackPending;
}
