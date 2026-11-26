using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private IModelDoc2[] DrawingOpenCollisions(ControlledOpenManifestDto manifest, IModelDoc2[] loaded = null)
    {
        var names = new HashSet<string>(manifest.Files.Select(file => file.FileName), StringComparer.OrdinalIgnoreCase);
        return (loaded ?? ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray())
            .Where(model => names.Contains(Path.GetFileName(model.GetPathName())) || names.Contains(model.GetTitle())).ToArray();
    }

    private void EnsureDrawingOpenHasNoConflictingWindows(ControlledOpenManifestDto manifest)
    {
        if (DrawingOpenCollisions(manifest).Length > 0)
            throw new InvalidOperationException("工程图与当前已打开模型的版本或目录需要核对，已保留全部窗口和修改，未自动切换总装。"
                + "请在原工作区打开配套工程图；需要查看其他版本时，请先保存工作，再手动关闭冲突图档后打开。");
    }

    private async Task<bool> TryOpenDrawingInLoadedContextAsync(ControlledOpenManifestDto requested, bool acquirePermission)
    {
        if (DocumentKindFromPath(requested.RootRelativePath) != CadDocumentKind.Drawing) return false;
        var loaded = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray();
        var collisions = DrawingOpenCollisions(requested, loaded);
        var modelIds = requested.Files.Where(file => DocumentKindFromPath(file.FileName) is CadDocumentKind.Part or CadDocumentKind.Assembly)
            .Select(file => file.DocumentId).ToArray();
        // Lightweight occurrences do not have a loaded model document. Their
        // open assembly's manifest still owns the physical working copies.
        var loadedPaths = new HashSet<string>(loaded.Select(model => model.GetPathName()), StringComparer.OrdinalIgnoreCase);
        var candidates = controlledOpenManifests.Where(pair => pair.Value.ProjectId == requested.ProjectId
            && !IsReadOnlyPreviewPath(pair.Key) && !IsHistoricalDerivedPath(pair.Key)
            && (collisions.Length > 0
                ? collisions.All(model => requested.Files.Any(file => FindWorkingManifestFile(pair, file.DocumentId, model.GetPathName()) != null))
                : modelIds.Length > 0 && DocumentKindFromPath(pair.Value.RootRelativePath) == CadDocumentKind.Assembly
                    && loadedPaths.Contains(ManagedCadPaths.Resolve(pair.Key, pair.Value.RootRelativePath))
                    && modelIds.All(id => pair.Value.Files.Any(file => file.DocumentId == id))))
            .ToArray();
        if (collisions.Length == 0 && candidates.Length > 1)
        {
            var activePath = (application.ActiveDoc as IModelDoc2)?.GetPathName();
            candidates = candidates.Where(pair => IsPathWithinDirectory(activePath, pair.Key)).ToArray();
            if (candidates.Length != 1) return false;
        }
        var context = candidates.OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
        if (context.Value == null)
        {
            if (collisions.Length > 0) EnsureDrawingOpenHasNoConflictingWindows(requested);
            return false;
        }
        var contextIds = await VerifiedOpenContextIdsAsync(requested);
        var files = new List<ControlledOpenFileDto>();
        var retainedHashes = new Dictionary<Guid, string>();
        ControlledOpenFileDto missingDrawing = null;
        IReadOnlyDictionary<Guid, DocumentDto> documents = null;
        foreach (var file in requested.Files)
        {
            var existing = context.Value.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            var isContext = existing != null && contextIds.Contains(file.DocumentId);
            var source = existing ?? file;
            var path = ControlledReferenceMaterializer.ManifestPath(context.Key, source);
            if (!IsPathWithinDirectory(path, context.Key)
                || context.Value.Files.Any(candidate => candidate.DocumentId != file.DocumentId
                    && PathsEqual(path, ControlledReferenceMaterializer.ManifestPath(context.Key, candidate)))
                || existing != null && (!string.Equals(existing.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !isContext && (existing.VersionId != file.VersionId || !WorkingConfigurationMatches(existing, file))))
            {
                LogOperation("工程图复用一致性未通过 file=" + file.FileName + " requestedVersion=" + file.VersionId
                    + " localVersion=" + source.VersionId + " requestedConfiguration=" + file.Configuration
                    + " localConfiguration=" + source.Configuration + " verifiedContext=" + isContext);
                throw DrawingContextMismatch(file.FileName);
            }
            if (!File.Exists(path))
            {
                if (file.DocumentId != requested.RootDocumentId)
                {
                    if (collisions.Any(model => string.Equals(Path.GetFileName(model.GetPathName()), file.FileName, StringComparison.OrdinalIgnoreCase)))
                        throw DrawingContextMismatch(file.FileName);
                    RecordMissingDrawingModel(requested, file);
                    continue;
                }
                if (existing != null) throw DrawingContextMismatch(file.FileName);
                missingDrawing = file;
            }
            else
            {
                if (!WorkingReuseBindingMatches(path, requested.ProjectId, source)) throw DrawingContextMismatch(file.FileName);
                if (!isContext && !WorkingReuseFileBytesMatch(source, file))
                {
                    var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                    if (!WorkingReuseStoredSourceVersionMatches(source, file, exact)) throw DrawingContextMismatch(file.FileName);
                }
                var model = FindLoadedDocument(path);
                var verified = ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, source, out var workingHash);
                if (!verified || !isContext && model?.GetSaveFlag() == true && !model.IsOpenedReadOnly())
                {
                    if (documents == null) documents = (await apiClient.GetDocumentsAsync(requested.ProjectId, lifetime.Token)).ToDictionary(item => item.Id);
                    documents.TryGetValue(file.DocumentId, out var document);
                    localEdits.TryGetValue(file.DocumentId, out var draft);
                    if (!WorkingReuseDraftMatches(path, source, document, draft, checkoutSessionId, authenticatedUsername))
                        throw DrawingContextMismatch(file.FileName);
                    if (!verified && workingHash != null) retainedHashes[file.DocumentId] = workingHash;
                }
            }
            files.Add(new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = isContext ? source.VersionId : file.VersionId,
                FileName = file.FileName, RelativePath = source.RelativePath ?? source.FileName, Configuration = isContext ? source.Configuration : file.Configuration,
                Revision = isContext ? source.Revision : file.Revision, Sha256 = source.Sha256, FileLength = source.FileLength,
                NativeIsolation = source.NativeIsolation, IsNativeIsolated = source.IsNativeIsolated, IsRoot = file.DocumentId == requested.RootDocumentId });
        }
        if (contextIds.Count > 0) LogOperation("工程图打开保留已核对的装配环境，未切换、保存或解锁环境图档；数量=" + contextIds.Count);
        var root = files.Single(file => file.IsRoot);
        var rootPath = ControlledReferenceMaterializer.ManifestPath(context.Key, root);
        if (!string.Equals(NormalizeDirectory(Path.GetDirectoryName(rootPath)), NormalizeDirectory(context.Key),
            StringComparison.OrdinalIgnoreCase)) throw DrawingContextMismatch(root.FileName);
        if (missingDrawing != null)
        {
            await apiClient.DownloadControlledOpenFileAsync(root, context.Key, lifetime.Token);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(rootPath, root.DocumentId, requested.ProjectId,
                root.VersionId, root.Revision, root.Sha256, root.FileLength)) throw DrawingContextMismatch(root.FileName);
            SetFileReadOnly(rootPath, true);
            PdmDocumentIdentityStore.TryRecordServerMaterialization(rootPath, DateTime.UtcNow);
        }
        var manifest = new ControlledOpenManifestDto { Id = requested.Id, ProjectId = requested.ProjectId, ProjectCode = requested.ProjectCode,
            RootDocumentId = requested.RootDocumentId, RootVersionId = requested.RootVersionId, RootRevision = requested.RootRevision,
            RootRelativePath = root.RelativePath, Files = files, Warnings = requested.Warnings, ForEdit = false,
            ReferenceIssues = OpenReferenceFindings.Merge(requested.ReferenceIssues) };
        foreach (var pair in retainedHashes)
        {
            manifest.RetainedWorkingHashes.Add(pair.Key, pair.Value);
            manifest.ResumedDocumentIds.Add(pair.Key);
        }
        var opened = OpenControlledReadOnlyDocumentCore(rootPath, (int)swDocumentTypes_e.swDocDRAWING, string.Empty, manifest, true,
            allowMissingComponents: true);
        if (opened == null) throw new IOException("工程图尚未打开，已保留原零件、总装窗口及修改，请查看本次操作日志。");
        foreach (var file in files.Where(file => context.Value.Files.All(existing => existing.DocumentId != file.DocumentId)))
        {
            file.IsRoot = false;
            context.Value.Files.Add(file);
        }
        if (acquirePermission)
        {
            var node = new CadTreeNode { DocumentId = root.DocumentId, FileName = root.FileName, FullPath = rootPath,
                Kind = CadDocumentKind.Drawing, Configuration = root.Configuration, OpenedVersionId = root.VersionId };
            await TryAcquireDrawingWorkingCopiesAsync(new[] { new BatchOperationItem(node, 0) }, requested.ProjectId);
        }
        RememberExplicitProjectPath(rootPath, requested.ProjectId);
        ScheduleTreeRefresh();
        LogOperation("工程图复用当前模型打开 path=" + rootPath + " context=" + context.Key
            + " files=" + files.Count + " closed=0 modelReplaced=0 acquire=" + acquirePermission);
        if (requested.Warnings?.Count > 0)
        {
            LogOperation("工程图已打开，引用提醒：" + string.Join("；", requested.Warnings));
            taskPaneControl?.SetCheckoutReminder("工程图已打开，引用提醒详见日志。", true);
        }
        return true;
    }

    private static void RecordMissingDrawingModel(ControlledOpenManifestDto manifest, ControlledOpenFileDto model)
    {
        var reason = "工程图配套模型在当前工作区缺失，按已保存视图继续：" + model.FileName;
        RecordOpenWarning(manifest, reason);
        var issue = new OpenReferenceIssueDto { OwnerDocumentId = manifest.RootDocumentId, InstancePath = manifest.RootRelativePath,
            FileName = model.FileName, OriginalPath = model.RelativePath ?? model.FileName, Reason = reason };
        manifest.ReferenceIssues ??= new List<OpenReferenceIssueDto>();
        if (!manifest.ReferenceIssues.Any(item => string.Equals(item.Key, issue.Key, StringComparison.OrdinalIgnoreCase)))
            manifest.ReferenceIssues.Add(issue);
    }

    private static InvalidOperationException DrawingContextMismatch(string fileName) => new InvalidOperationException(
        "工程图与当前工作副本的版本、引用或修改记录不一致：" + fileName
        + "。已保留全部窗口、文件和编辑权限，未关闭零件或总装。请先在当前工作区核对并存档实际修改，再重新打开配套工程图；查看其他版本请手动切换。");
}
