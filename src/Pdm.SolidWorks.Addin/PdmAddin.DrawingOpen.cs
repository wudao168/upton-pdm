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
    private IModelDoc2[] DrawingOpenCollisions(ControlledOpenManifestDto manifest)
    {
        var names = new HashSet<string>(manifest.Files.Select(file => file.FileName), StringComparer.OrdinalIgnoreCase);
        return ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
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
        var collisions = DrawingOpenCollisions(requested);
        if (collisions.Length == 0) return false;
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == requested.ProjectId
            && !IsReadOnlyPreviewPath(pair.Key) && !IsHistoricalDerivedPath(pair.Key)
            && collisions.All(model => requested.Files.Any(file => pair.Value.Files.Any(existing => existing.DocumentId == file.DocumentId
                && PathsEqual(model.GetPathName(), Path.Combine(pair.Key, existing.RelativePath ?? existing.FileName))))))
            .OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
        if (context.Value == null)
        {
            EnsureDrawingOpenHasNoConflictingWindows(requested);
            return false;
        }
        if (requested.Warnings?.Count > 0)
            throw new InvalidOperationException("工程图引用尚不完整，已保留原窗口，未开始打开：" + string.Join("；", requested.Warnings.Take(3)));

        var files = new List<ControlledOpenFileDto>();
        ControlledOpenFileDto missingDrawing = null;
        IReadOnlyDictionary<Guid, DocumentDto> documents = null;
        foreach (var file in requested.Files)
        {
            var existing = context.Value.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            var source = existing ?? file;
            var path = Path.GetFullPath(Path.Combine(context.Key, source.RelativePath ?? source.FileName));
            if (!IsPathWithinDirectory(path, context.Key)
                || context.Value.Files.Any(candidate => candidate.DocumentId != file.DocumentId
                    && PathsEqual(path, Path.Combine(context.Key, candidate.RelativePath ?? candidate.FileName)))
                || existing != null && (existing.VersionId != file.VersionId
                    || !string.Equals(existing.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
                throw DrawingContextMismatch(file.FileName);
            if (!File.Exists(path))
            {
                if (file.DocumentId != requested.RootDocumentId || existing != null) throw DrawingContextMismatch(file.FileName);
                missingDrawing = file;
            }
            else
            {
                if (!WorkingReuseBindingMatches(path, requested.ProjectId, source)) throw DrawingContextMismatch(file.FileName);
                if (!WorkingReuseFileBytesMatch(source, file))
                {
                    var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                    if (!WorkingReuseStoredSourceVersionMatches(source, file, exact)) throw DrawingContextMismatch(file.FileName);
                }
                var model = FindLoadedDocument(path);
                if (!ControlledReferenceMaterializer.Matches(path, source) || model?.GetSaveFlag() == true && !model.IsOpenedReadOnly())
                {
                    if (documents == null) documents = (await apiClient.GetDocumentsAsync(requested.ProjectId, lifetime.Token)).ToDictionary(item => item.Id);
                    documents.TryGetValue(file.DocumentId, out var document);
                    localEdits.TryGetValue(file.DocumentId, out var draft);
                    if (!WorkingReuseDraftMatches(path, source, document, draft, checkoutSessionId, authenticatedUsername))
                        throw DrawingContextMismatch(file.FileName);
                }
            }
            files.Add(new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = file.VersionId,
                FileName = file.FileName, RelativePath = source.RelativePath ?? source.FileName, Configuration = file.Configuration,
                Revision = file.Revision, Sha256 = source.Sha256, FileLength = source.FileLength, IsRoot = file.DocumentId == requested.RootDocumentId });
        }
        var root = files.Single(file => file.IsRoot);
        var rootPath = Path.GetFullPath(Path.Combine(context.Key, root.RelativePath));
        if (!PathsEqual(Path.GetDirectoryName(rootPath), context.Key)) throw DrawingContextMismatch(root.FileName);
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
            RootRelativePath = root.RelativePath, Files = files, Warnings = requested.Warnings, ForEdit = false };
        var opened = OpenControlledReadOnlyDocumentCore(rootPath, (int)swDocumentTypes_e.swDocDRAWING, root.Configuration, manifest, true);
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
        return true;
    }

    private static InvalidOperationException DrawingContextMismatch(string fileName) => new InvalidOperationException(
        "工程图与当前工作副本的版本、引用或修改记录不一致：" + fileName
        + "。已保留全部窗口、文件和编辑权限，未关闭零件或总装。请先在当前工作区核对并存档实际修改，再重新打开配套工程图；查看其他版本请手动切换。");
}
