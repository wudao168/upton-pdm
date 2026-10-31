using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private string blockedControlledOpenMessage;

    private IModelDoc2 OpenControlledArchiveDrawing(string fullPath, bool readOnly)
    {
        var context = controlledOpenManifests.FirstOrDefault(pair => pair.Value.Files.Any(file =>
            PathsEqual(fullPath, Path.Combine(pair.Key, file.RelativePath ?? file.FileName))));
        if (context.Value != null)
            return OpenControlledReadOnlyDocumentCore(fullPath, ToSolidWorksDocumentType(CadDocumentKind.Drawing),
                string.Empty, context.Value, true, true);
        return readOnly ? OpenDocumentSilentlyForBatch(fullPath, ToSolidWorksDocumentType(CadDocumentKind.Drawing), string.Empty, true)
            : OpenOrActivateDocumentOnSolidWorksThread(fullPath, ToSolidWorksDocumentType(CadDocumentKind.Drawing), string.Empty);
    }

    private bool ShowBlockedControlledOpen()
    {
        var message = System.Threading.Interlocked.Exchange(ref blockedControlledOpenMessage, null);
        if (message == null) return false;
        ShowError(message);
        return true;
    }

    private void ValidateControlledArchiveSources(CadTreeNode root)
    {
        if (root == null || string.IsNullOrWhiteSpace(root.FullPath)) return;
        var context = controlledOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(root.FullPath, pair.Key));
        if (context.Value == null) return; // Initial local imports retain their source-directory workflow.
        var known = context.Value.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateArchiveNodes(root))
        {
            if (node.Status == CadReferenceStatus.Virtual || node.IsStoredImport || string.IsNullOrWhiteSpace(node.FullPath)
                || !known.TryGetValue(Path.GetFileName(node.FullPath), out var file)) continue;
            var expected = Path.GetFullPath(Path.Combine(context.Key, file.RelativePath ?? file.FileName));
            if (!PathsEqual(node.FullPath, expected) || node.DocumentId.HasValue && node.DocumentId != file.DocumentId)
                throw new InvalidOperationException("当前存档结构引用了另一份同名文件，尚未保存、上传或升版，原窗口和修改均保留。请从当前PLM工作区重新打开配套图档。"
                    + "\r\n当前引用：" + node.FullPath + "\r\n受控副本：" + expected);
        }
    }

    private bool RejectUncontrolledNativeOpen(string fileName)
    {
        if (ControlledOpenBoundary.IsActive) return ControlledOpenBoundary.Reject(fileName);
        // Native File > Open/Open Drawing can run outside our explicit OpenDoc scope.
        // Use cached context only: querying COM while a document loads is unsafe.
        if (string.IsNullOrWhiteSpace(fileName) || currentTree == null) return false;
        foreach (var context in controlledOpenManifests.Where(pair => IsPathWithinDirectory(currentTree.FullPath, pair.Key)))
        {
            var file = context.Value.Files.FirstOrDefault(candidate => string.Equals(candidate.FileName, Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase));
            if (file == null) continue;
            var expected = Path.GetFullPath(Path.Combine(context.Key, file.RelativePath ?? file.FileName));
            if (PathsEqual(fileName, expected)) return false;
            PluginDiagnosticLog.Record("受控打开拦截", "当前受控图档禁止加载另一目录的同名文件；请求=" + fileName + "；工作副本=" + expected);
            blockedControlledOpenMessage = "已阻止打开原目录或另一工作区的同名图档，当前PLM图档和修改均保留。请使用当前工作区的配套图档。"
                + "\r\n请求路径：" + fileName + "\r\n受控路径：" + expected;
            return true;
        }
        return false;
    }

    private async Task<bool> TryOpenModelInLoadedContextAsync(ControlledOpenManifestDto requested, bool acquirePermission)
    {
        var kind = DocumentKindFromPath(requested.RootRelativePath);
        if (kind != CadDocumentKind.Part && kind != CadDocumentKind.Assembly) return false;
        var root = requested.Files.Single(file => file.DocumentId == requested.RootDocumentId);
        var loaded = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Where(model => string.Equals(Path.GetFileName(model.GetPathName()), root.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (loaded.Length == 0) return false;
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == requested.ProjectId
            && !IsReadOnlyPreviewPath(pair.Key) && !IsHistoricalDerivedPath(pair.Key)
            && loaded.All(model => pair.Value.Files.Any(file => file.DocumentId == root.DocumentId
                && PathsEqual(model.GetPathName(), Path.Combine(pair.Key, file.RelativePath ?? file.FileName)))))
            .OrderByDescending(pair => pair.Value.Files.Count).FirstOrDefault();
        if (context.Value == null)
            throw new InvalidOperationException("已打开另一目录的同名模型，未新建或替换PLM工作副本。请先保存并关闭该同名图档后重试：\r\n"
                + string.Join("\r\n", loaded.Select(model => model.GetPathName())));
        if (!string.IsNullOrWhiteSpace(root.Configuration)
            && !string.Equals(loaded.Single().ConfigurationManager?.ActiveConfiguration?.Name, root.Configuration, StringComparison.OrdinalIgnoreCase))
            throw WorkingModelContextMismatch(root.FileName);
        var files = new List<ControlledOpenFileDto>();
        IReadOnlyDictionary<Guid, DocumentDto> documents = null;
        // Associated drawings are opened separately against this same context. They
        // are not model dependencies and must not force a second model directory.
        foreach (var file in requested.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing))
        {
            var source = context.Value.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            if (source == null || source.VersionId != file.VersionId
                || !string.Equals(source.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                || !(kind == CadDocumentKind.Part && file.DocumentId == requested.RootDocumentId)
                    && !string.Equals(source.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw WorkingModelContextMismatch(file.FileName);
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, source);
            if (!WorkingReuseBindingMatches(path, requested.ProjectId, source)) throw WorkingModelContextMismatch(file.FileName);
            if (!WorkingReuseFileBytesMatch(source, file))
            {
                var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                if (!WorkingReuseStoredSourceVersionMatches(source, file, exact)) throw WorkingModelContextMismatch(file.FileName);
            }
            var model = FindLoadedDocument(path);
            if (!ControlledReferenceMaterializer.Matches(path, source) || model?.GetSaveFlag() == true && !model.IsOpenedReadOnly())
            {
                if (documents == null) documents = (await apiClient.GetDocumentsAsync(requested.ProjectId, lifetime.Token)).ToDictionary(item => item.Id);
                documents.TryGetValue(file.DocumentId, out var document);
                localEdits.TryGetValue(file.DocumentId, out var draft);
                if (!WorkingReuseDraftMatches(path, source, document, draft, checkoutSessionId, authenticatedUsername))
                    throw WorkingModelContextMismatch(file.FileName);
            }
            files.Add(new ControlledOpenFileDto { DocumentId = source.DocumentId, VersionId = source.VersionId,
                FileName = source.FileName, RelativePath = source.RelativePath, Configuration = source.Configuration,
                Revision = source.Revision, Sha256 = source.Sha256, FileLength = source.FileLength,
                NativeIsolation = source.NativeIsolation, IsNativeIsolated = source.IsNativeIsolated, IsRoot = source.DocumentId == requested.RootDocumentId });
        }
        var localRoot = files.Single(file => file.IsRoot);
        var rootPath = ControlledReferenceMaterializer.LocalPath(context.Key, localRoot);
        var manifest = new ControlledOpenManifestDto { Id = requested.Id, ProjectId = requested.ProjectId,
            ProjectCode = requested.ProjectCode, RootDocumentId = requested.RootDocumentId, RootVersionId = requested.RootVersionId,
            RootRelativePath = localRoot.RelativePath ?? localRoot.FileName, RootRevision = requested.RootRevision, Files = files };
        ControlledOpenBoundary.RequireLoadedSources(application, rootPath, manifest);
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            ValidateControlledLoadedSources(loaded.Single(), rootPath, manifest, application);
            search.VerifyLoadedSources();
        }
        // A part file contains all its configurations. Its assembly occurrence can
        // use another one; never rewrite that owner's occurrence configuration.
        if (kind == CadDocumentKind.Part && context.Value.RootDocumentId == requested.RootDocumentId)
            context.Value.Files.Single(file => file.DocumentId == requested.RootDocumentId).Configuration = root.Configuration;
        if (acquirePermission)
        {
            var node = new CadTreeNode { DocumentId = localRoot.DocumentId, FullPath = rootPath, FileName = localRoot.FileName,
                Kind = kind, Configuration = localRoot.Configuration, OpenedVersionId = localRoot.VersionId };
            if (!await TryAcquireReusableWorkingCopyAsync(new[] { new BatchOperationItem(node, 0) }, requested.ProjectId, localRoot.DocumentId))
                throw WorkingModelContextMismatch(localRoot.FileName);
        }
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            if (OpenOrActivateDocumentOnSolidWorksThread(rootPath, ToSolidWorksDocumentType(kind), localRoot.Configuration) == null)
                throw new IOException("尚未激活当前PLM工作副本，原窗口和文件已保留：" + localRoot.FileName);
            search.VerifyLoadedSources();
        }
        RememberExplicitProjectPath(rootPath, requested.ProjectId);
        ScheduleTreeRefresh();
        LogOperation("模型复用当前受控工作区 path=" + rootPath + " context=" + context.Key + " files=" + files.Count + " closed=0 downloaded=0 acquire=" + acquirePermission);
        return true;
    }

    private static InvalidOperationException WorkingModelContextMismatch(string fileName) => new InvalidOperationException(
        "已打开模型与本次PLM版本、配置或编辑记录不一致，已保留窗口和修改，未切换到另一份同名副本：" + fileName
        + "。请核对当前工作副本；切换版本前请保存并关闭冲突图档。");
}
