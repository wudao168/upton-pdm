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
            if (CanUseDeferredArchiveCopy(node, file, expected)) continue;
            if (!PathsEqual(node.FullPath, expected) || node.DocumentId.HasValue && node.DocumentId != file.DocumentId)
                throw new InvalidOperationException("当前存档结构引用了另一份同名文件，尚未保存、上传或升版，原窗口和修改均保留。请从当前PLM工作区重新打开配套图档。"
                    + "\r\n当前引用：" + node.FullPath + "\r\n受控副本：" + expected);
        }
    }

    private bool CanUseDeferredArchiveCopy(CadTreeNode node, ControlledOpenFileDto file, string expected) =>
        !PathsEqual(node.FullPath, expected) && IsNativeModelReference(node.FullPath)
        && (node.IsSuppressed || node.Status == CadReferenceStatus.Missing && !File.Exists(node.FullPath))
        && (!node.DocumentId.HasValue || node.DocumentId.Value == file.DocumentId)
        && FindLoadedDocument(node.FullPath) == null && ControlledReferenceMaterializer.Matches(expected, file);

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
        var contextIds = !acquirePermission && kind == CadDocumentKind.Part
            ? await VerifiedOpenContextIdsAsync(requested) : new HashSet<Guid>();
        var files = new List<ControlledOpenFileDto>();
        var committedMemoryFiles = new Dictionary<string, ControlledOpenFileDto>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<Guid, DocumentDto> documents = null;
        // Associated drawings are opened separately against this same context. They
        // are not model dependencies and must not force a second model directory.
        foreach (var file in requested.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing))
        {
            var isContext = contextIds.Contains(file.DocumentId);
            var source = context.Value.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            if (source == null
                || !string.Equals(source.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                || !isContext && !(kind == CadDocumentKind.Part && file.DocumentId == requested.RootDocumentId)
                    && !string.Equals(source.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw WorkingModelContextMismatch(file.FileName);
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, source);
            if (!WorkingReuseBindingMatches(path, requested.ProjectId, source)) throw WorkingModelContextMismatch(file.FileName);
            // A memory-copy check-in deliberately leaves the older disk binding
            // intact. Reuse only its still-loaded, unchanged committed model;
            // never label those disk bytes as the newer server version.
            var committedMemory = !acquirePermission && WorkingReuseCommittedMemoryMatches(path, file);
            if (!isContext && source.VersionId != file.VersionId && !committedMemory) throw WorkingModelContextMismatch(file.FileName);
            if (committedMemory) committedMemoryFiles.Add(path, file);
            if (!isContext && !committedMemory && !WorkingReuseFileBytesMatch(source, file))
            {
                var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                if (!WorkingReuseStoredSourceVersionMatches(source, file, exact)) throw WorkingModelContextMismatch(file.FileName);
            }
            var model = FindLoadedDocument(path);
            if (!committedMemory && (!ControlledReferenceMaterializer.Matches(path, source) || model?.GetSaveFlag() == true && !model.IsOpenedReadOnly()))
            {
                if (documents == null) documents = (await apiClient.GetDocumentsAsync(requested.ProjectId, lifetime.Token)).ToDictionary(item => item.Id);
                documents.TryGetValue(file.DocumentId, out var document);
                localEdits.TryGetValue(file.DocumentId, out var draft);
                if (!WorkingReuseDraftMatches(path, source, document, draft, checkoutSessionId, authenticatedUsername))
                    throw WorkingModelContextMismatch(file.FileName);
            }
            if (isContext) LogOperation("零件打开复用已核对的装配环境，未切换父装配版本或配置：" + source.FileName);
            files.Add(new ControlledOpenFileDto { DocumentId = source.DocumentId, VersionId = source.VersionId,
                FileName = source.FileName, RelativePath = source.RelativePath, Configuration = source.Configuration,
                Revision = source.Revision, Sha256 = source.Sha256, FileLength = source.FileLength,
                NativeIsolation = source.NativeIsolation, IsNativeIsolated = source.IsNativeIsolated, IsRoot = source.DocumentId == requested.RootDocumentId });
        }
        var localRoot = files.Single(file => file.IsRoot);
        var rootPath = ControlledReferenceMaterializer.LocalPath(context.Key, localRoot);
        var manifest = new ControlledOpenManifestDto { Id = requested.Id, ProjectId = requested.ProjectId,
            ProjectCode = requested.ProjectCode, RootDocumentId = requested.RootDocumentId, RootVersionId = requested.RootVersionId,
            RootRelativePath = localRoot.RelativePath ?? localRoot.FileName, RootRevision = requested.RootRevision, Files = files,
            ReferenceIssues = OpenReferenceFindings.Merge(requested.ReferenceIssues), Warnings = requested.Warnings };
        ControlledOpenBoundary.RequireLoadedSources(application, rootPath, manifest);
        var missingComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            ValidateControlledLoadedSources(loaded.Single(), rootPath, manifest, application, missingComponents);
            search.VerifyLoadedSources(missingComponents);
        }
        // A part file contains all its configurations. Its assembly occurrence can
        // use another one; never rewrite that owner's occurrence configuration.
        if (kind == CadDocumentKind.Part && context.Value.RootDocumentId == requested.RootDocumentId)
            context.Value.Files.Single(file => file.DocumentId == requested.RootDocumentId).Configuration = root.Configuration;
        if (acquirePermission)
        {
            var node = new CadTreeNode { DocumentId = localRoot.DocumentId, FullPath = rootPath, FileName = localRoot.FileName,
                Kind = kind, Configuration = localRoot.Configuration, OpenedVersionId = localRoot.VersionId };
            if (!await TryAcquireReusableWorkingCopyCoreAsync(new[] { new BatchOperationItem(node, 0) }, requested.ProjectId, localRoot.DocumentId, false))
                throw WorkingModelContextMismatch(localRoot.FileName);
        }
        void VerifyCommittedMemory()
        {
            foreach (var pair in committedMemoryFiles)
                if (!WorkingReuseBindingMatches(pair.Key, requested.ProjectId,
                    context.Value.Files.Single(file => file.DocumentId == pair.Value.DocumentId))
                    || !WorkingReuseCommittedMemoryMatches(pair.Key, pair.Value)) throw WorkingModelContextMismatch(pair.Value.FileName);
        }
        VerifyCommittedMemory(); // Version/draft requests above can yield to later edits.
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            if (OpenOrActivateDocumentOnSolidWorksThread(rootPath, ToSolidWorksDocumentType(kind), localRoot.Configuration) == null)
                throw new IOException("尚未激活当前PLM工作副本，原窗口和文件已保留：" + localRoot.FileName);
            if (missingComponents.Count > 0)
            {
                missingComponents.Clear();
                ValidateControlledLoadedSources(loaded.Single(), rootPath, manifest, application, missingComponents);
            }
            search.VerifyLoadedSources(missingComponents);
        }
        VerifyCommittedMemory();
        if (acquirePermission) CompleteRecordedAcquisitions(new[] { localRoot.DocumentId });
        RememberExplicitProjectPath(rootPath, requested.ProjectId);
        ScheduleTreeRefresh();
        ReportMissingComponentsOnOpen(missingComponents, acquirePermission ? "已获取权限" : "已打开", rootPath);
        LogOperation("模型复用当前受控工作区 path=" + rootPath + " context=" + context.Key + " files=" + files.Count
            + " committedMemory=" + committedMemoryFiles.Count + " closed=0 downloaded=0 acquire=" + acquirePermission);
        return true;
    }

    private async Task<HashSet<Guid>> VerifiedOpenContextIdsAsync(ControlledOpenManifestDto requested)
    {
        var contextIds = new HashSet<Guid>();
        var root = requested.Files.Single(file => file.DocumentId == requested.RootDocumentId);
        if (!requested.Files.Any(file => file.DocumentId != root.DocumentId
            && DocumentKindFromPath(file.FileName) == CadDocumentKind.Assembly && string.IsNullOrWhiteSpace(file.Configuration)))
            return contextIds;
        async Task<DocumentReferenceNodeDto> Snapshot(ControlledOpenFileDto file)
        {
            var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token, 1)).FirstOrDefault();
            if (exact?.Id != file.VersionId)
                throw new InvalidOperationException("核对引用类型期间图档版本发生变化，已保留全部窗口和文件，请刷新后重试：" + file.FileName);
            var snapshot = exact.ReferenceSnapshot;
            return snapshot?.DocumentId == file.DocumentId && snapshot.Kind == (int)DocumentKindFromPath(file.FileName)
                && string.Equals(snapshot.FileName, file.FileName, StringComparison.OrdinalIgnoreCase) ? snapshot : null;
        }
        var rootSnapshot = await Snapshot(root);
        var snapshots = new List<DocumentReferenceNodeDto>();
        if (rootSnapshot != null) snapshots.Add(rootSnapshot);
        // Drawings pin their view models, but those models own the feature-context records.
        // Read each direct view model's exact manifest version, never infer context by name.
        if (DocumentKindFromPath(root.FileName) == CadDocumentKind.Drawing && rootSnapshot != null)
            foreach (var id in (rootSnapshot.Children ?? new List<DocumentReferenceNodeDto>())
                .Where(node => !node.IsContextReference && node.DocumentId.HasValue
                    && (node.Kind == (int)CadDocumentKind.Part || node.Kind == (int)CadDocumentKind.Assembly))
                .Select(node => node.DocumentId.Value).Distinct())
            {
                var model = requested.Files.SingleOrDefault(file => file.DocumentId == id);
                if (model == null) continue;
                var snapshot = await Snapshot(model);
                if (snapshot != null) snapshots.Add(snapshot);
            }
        var references = new List<DocumentReferenceNodeDto>();
        var pending = new Stack<DocumentReferenceNodeDto>(snapshots.SelectMany(snapshot => snapshot.Children ?? new List<DocumentReferenceNodeDto>()));
        while (pending.Count > 0)
        {
            var reference = pending.Pop();
            references.Add(reference);
            if (!reference.IsContextReference)
                foreach (var child in reference.Children ?? new List<DocumentReferenceNodeDto>()) pending.Push(child);
        }
        foreach (var reference in references.Where(reference => reference.IsContextReference
            && !reference.ReferenceTypeUnverified && reference.Role == (int)CadReferenceRole.ExternalFileDependency
            && reference.Kind == (int)CadDocumentKind.Assembly && reference.DocumentId.HasValue
            && reference.Status != (int)CadReferenceStatus.Virtual && reference.Children?.Count == 0))
            if (requested.Files.Any(file => file.DocumentId == reference.DocumentId
                && string.Equals(file.FileName, reference.FileName, StringComparison.OrdinalIgnoreCase)))
                contextIds.Add(reference.DocumentId.Value);
        // A view model or real feature source always wins over an additional context record.
        contextIds.ExceptWith(references.Where(reference => !reference.IsContextReference && reference.DocumentId.HasValue)
            .Select(reference => reference.DocumentId.Value));
        contextIds.Remove(requested.RootDocumentId);
        return contextIds;
    }

    private bool WorkingReuseCommittedMemoryMatches(string path, ControlledOpenFileDto file)
    {
        if (!archiveMemoryCopies.TryGetValue(path, out var copy) || copy.CommittedVersion == null
            || copy.CommittedDocumentId != file.DocumentId || copy.CommittedVersion.Id != file.VersionId
            || string.IsNullOrWhiteSpace(authenticatedUsername) || copy.CommittedUser != authenticatedUsername
            || apiClient == null || copy.CommittedScope != apiClient.EditJournalScope
            || !ReferenceEquals(copy.Model, FindLoadedDocument(path)) || !copy.MatchesMemory()
            || string.IsNullOrWhiteSpace(file.Sha256) || copy.CommittedVersion.FileLength != file.FileLength
            || !string.Equals(copy.CommittedVersion.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(copy.CopyHash, file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return new FileInfo(path).Length == copy.SourceLength && new FileInfo(copy.CopyPath).Length == file.FileLength
            && string.Equals(ComputeFileHash(path), copy.SourceHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ComputeFileHash(copy.CopyPath), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException WorkingModelContextMismatch(string fileName) => new InvalidOperationException(
        "已打开模型与本次PLM版本、配置或编辑记录不一致，已保留窗口和修改，未切换到另一份同名副本：" + fileName
        + "。请核对当前工作副本；切换版本前请保存并关闭冲突图档。");
}
