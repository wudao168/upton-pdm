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

    private void ClearReportedNativeOpenNotification(string previousMessage)
    {
        var reported = System.Threading.Volatile.Read(ref blockedControlledOpenMessage);
        if (reported == null || ReferenceEquals(reported, previousMessage)) return;
        // OpenDoc has returned and its failure will be displayed now. Consume only
        // that notification; an independent later callback keeps its own message.
        if (ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref blockedControlledOpenMessage, null, reported), reported))
            LogOperation("同名图档拦截已随本次打开失败统一提示，详细路径保留在受控打开日志中。");
    }

    private void ShowNativeOpenFailure(string message, string previousMessage)
    {
        ClearReportedNativeOpenNotification(previousMessage);
        ShowError(message);
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

    private bool HasOutdatedLoadedAssemblyContext(ControlledOpenManifestDto requested)
    {
        var desired = requested.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .ToDictionary(file => file.DocumentId);
        var loadedPaths = new HashSet<string>(CaptureLoadedCachePaths(), StringComparer.OrdinalIgnoreCase);
        return controlledOpenManifests.Any(context => context.Value.ProjectId == requested.ProjectId
            && context.Value.Files.Any(file => desired.TryGetValue(file.DocumentId, out var newer)
                && file.VersionId != newer.VersionId && !WorkingReuseFileBytesMatch(file, newer)
                && loadedPaths.Contains(ControlledReferenceMaterializer.LocalPath(context.Key, file))));
    }

    private bool TryGetCurrentReferenceOpen(CadTreeNode node, out Guid projectId, out Guid versionId)
    {
        projectId = Guid.Empty;
        versionId = Guid.Empty;
        if (currentTree == null || currentTree.IsReadOnlyPreview || node.IsReadOnlyPreview || !node.DocumentId.HasValue
            || node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly) return false;

        foreach (var context in controlledOpenManifests)
        {
            if (context.Value.ProjectId != currentProjectId || context.Value.RootDocumentId == node.DocumentId
                || DocumentKindFromPath(context.Value.RootRelativePath) != CadDocumentKind.Assembly
                || IsReadOnlyPreviewPath(context.Key) || IsHistoricalDerivedPath(context.Key)
                || !context.Value.Files.Any(file => file.DocumentId == currentTree.DocumentId
                    && PathsEqual(currentTree.FullPath, ControlledReferenceMaterializer.LocalPath(context.Key, file)))) continue;
            var source = context.Value.Files.SingleOrDefault(file => file.DocumentId == node.DocumentId
                && PathsEqual(node.FullPath, ControlledReferenceMaterializer.LocalPath(context.Key, file)));
            if (source == null || source.VersionId == Guid.Empty) continue;
            projectId = context.Value.ProjectId;
            versionId = source.VersionId;
            return true;
        }
        return false;
    }

    private Task<bool> TryOpenModelInLoadedContextAsync(ControlledOpenManifestDto requested, bool acquirePermission)
        => TryOpenModelInLoadedContextCoreAsync(requested, acquirePermission, null);

    private async Task<bool> TryOpenModelInLoadedContextCoreAsync(ControlledOpenManifestDto requested, bool acquirePermission, string referencePath)
    {
        var kind = DocumentKindFromPath(requested.RootRelativePath);
        if (kind != CadDocumentKind.Part && kind != CadDocumentKind.Assembly) return false;
        var root = requested.Files.Single(file => file.DocumentId == requested.RootDocumentId);
        var opensCurrentReference = !string.IsNullOrWhiteSpace(referencePath);
        var loadedDocuments = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray();
        var loaded = loadedDocuments
            .Where(model => string.Equals(Path.GetFileName(model.GetPathName()), root.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = controlledOpenManifests.Where(pair => pair.Value.ProjectId == requested.ProjectId
            && !IsReadOnlyPreviewPath(pair.Key) && !IsHistoricalDerivedPath(pair.Key)
            && pair.Value.Files.Any(file => file.DocumentId == root.DocumentId
                && string.Equals(file.FileName, root.FileName, StringComparison.OrdinalIgnoreCase)
                && (!opensCurrentReference || PathsEqual(referencePath, ControlledReferenceMaterializer.LocalPath(pair.Key, file))))
            && (!opensCurrentReference || loadedDocuments.Any(model => PathsEqual(model.GetPathName(),
                Path.Combine(pair.Key, pair.Value.RootRelativePath))))
            && (loaded.Length > 0
                ? loaded.All(model => pair.Value.Files.Any(file => file.DocumentId == root.DocumentId
                    && PathsEqual(model.GetPathName(), Path.Combine(pair.Key, file.RelativePath ?? file.FileName))))
                : pair.Value.Files.Any(file => file.DocumentId == pair.Value.RootDocumentId
                    && loadedDocuments.Any(model => PathsEqual(model.GetPathName(), Path.Combine(pair.Key, file.RelativePath ?? file.FileName))))))
            .OrderByDescending(pair => pair.Value.Files.Count).ToArray();
        // Lightweight/unloaded children are absent from GetDocuments. Their loaded
        // owner's verified manifest still fixes the physical workspace; making a
        // second directory would make SOLIDWORKS load a same-name file from the owner.
        if (loaded.Length == 0 && candidates.Length == 0) return false;
        if (loaded.Length == 0 && candidates.Length > 1)
            throw new InvalidOperationException("多个已打开装配包含该图档，尚未选择另一份工作副本。请从需要操作的装配中打开：" + root.FileName);
        var context = candidates.FirstOrDefault();
        if (opensCurrentReference && context.Value == null) return false;
        if (context.Value == null)
            throw new InvalidOperationException("已打开另一目录的同名模型，未新建或替换PLM工作副本。请先保存并关闭该同名图档后重试：\r\n"
                + string.Join("\r\n", loaded.Select(model => model.GetPathName())));
        var loadedRoot = loaded.SingleOrDefault();
        if (loadedRoot == null)
        {
            var owner = context.Value.Files.Single(file => file.DocumentId == context.Value.RootDocumentId);
            // Candidate discovery compares loaded paths only: an inactive old
            // manifest may legitimately refer to evicted cache files. A selected
            // loaded owner still requires an in-bound existing file and binding.
            if (!WorkingReuseBindingMatches(ControlledReferenceMaterializer.LocalPath(context.Key, owner), requested.ProjectId, owner))
                throw WorkingModelContextMismatch(owner.FileName);
        }
        if (loadedRoot != null && !string.IsNullOrWhiteSpace(root.Configuration)
            && !string.Equals(loadedRoot.ConfigurationManager?.ActiveConfiguration?.Name, root.Configuration, StringComparison.OrdinalIgnoreCase))
            throw WorkingModelContextMismatch(root.FileName);
        var contextIds = !acquirePermission && kind == CadDocumentKind.Part
            ? await VerifiedOpenContextIdsAsync(requested, opensCurrentReference) : new HashSet<Guid>();
        var files = new List<ControlledOpenFileDto>();
        var committedMemoryFiles = new Dictionary<string, ControlledOpenFileDto>(StringComparer.OrdinalIgnoreCase);
        var releasedPromotionIds = new HashSet<Guid>();
        IReadOnlyDictionary<Guid, DocumentDto> documents = null;
        // Associated drawings are opened separately against this same context. They
        // are not model dependencies and must not force a second model directory.
        foreach (var file in requested.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing))
        {
            var isContext = contextIds.Contains(file.DocumentId);
            var source = context.Value.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            // A manifest stores one file per document. Separate assembly occurrences
            // can use different configurations from those same versioned bytes.
            // Apply the same file identity rule to every open/checkout entry point;
            // actual occurrence structures are verified separately below.
            var sameReferenceFile = DocumentKindFromPath(file.FileName) == CadDocumentKind.Part
                && file.DocumentId != requested.RootDocumentId && source != null
                && source.VersionId == file.VersionId && WorkingReuseFileBytesMatch(source, file);
            if (source == null
                || !string.Equals(source.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                || !isContext && !sameReferenceFile && !(loadedRoot != null && kind == CadDocumentKind.Part && file.DocumentId == requested.RootDocumentId)
                    && !string.Equals(source.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw WorkingModelContextMismatch(file.FileName);
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, source);
            if (!WorkingReuseBindingMatches(path, requested.ProjectId, source)) throw WorkingModelContextMismatch(file.FileName);
            // A memory-copy check-in deliberately leaves the older disk binding
            // intact. Reuse only its still-loaded, unchanged committed model;
            // never label those disk bytes as the newer server version.
            var committedMemory = !acquirePermission && WorkingReuseCommittedMemoryMatches(path, file);
            if (opensCurrentReference && !isContext && source.VersionId != file.VersionId)
                throw WorkingModelContextMismatch(file.FileName, "装配引用的版本在打开期间发生变化，请刷新设计树后重试");
            if (!isContext && source.VersionId != file.VersionId && !committedMemory)
            {
                if (!WorkingReuseFileBytesMatch(source, file) || !WorkingConfigurationMatches(source, file))
                {
                    if (source.DocumentId == root.DocumentId && context.Value.RootDocumentId != root.DocumentId)
                        throw new InvalidOperationException($"当前装配引用的 {file.FileName} 与请求打开的图纸版本不同。"
                            + "\r\n查看装配中的版本请在设计树选择“打开此图档”。需要打开最新版时，请妥善保存并关闭正在引用该图档的装配及图档窗口，再从工作区打开。");
                    throw WorkingModelContextMismatch(file.FileName, "当前工作基线与请求版本的文件内容或配置不同，不能按审批发布等价复用");
                }
                var versions = await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token);
                if (versions.FirstOrDefault()?.Id != file.VersionId
                    || !WorkingReuseReleasePromotionMatches(source, file,
                        versions.FirstOrDefault(version => version.Id == source.VersionId),
                        versions.FirstOrDefault(version => version.Id == file.VersionId)))
                    throw WorkingModelContextMismatch(file.FileName, "服务器版本与当前工作基线不同，且未确认是内容及引用完全相同的审批发布版本");
                releasedPromotionIds.Add(file.DocumentId);
                LogOperation("模型打开已核对审批发布来源，复用原工作基线；图档=" + file.FileName
                    + "；当前基线=" + source.VersionId + "；发布版本=" + file.VersionId
                    + "；磁盘绑定、草稿及父装配引用保持不变。");
            }
            if (committedMemory) committedMemoryFiles.Add(path, file);
            if (!isContext && !committedMemory && !WorkingReuseFileBytesMatch(source, file))
            {
                var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                if (!WorkingReuseStoredSourceVersionMatches(source, file, exact)) throw WorkingModelContextMismatch(file.FileName);
            }
            var model = FindLoadedDocument(path);
            var promotionHasEditableState = releasedPromotionIds.Contains(file.DocumentId)
                && (localEdits.ContainsKey(file.DocumentId) || model != null && !model.IsOpenedReadOnly());
            if (!committedMemory && (!ControlledReferenceMaterializer.Matches(path, source)
                || model?.GetSaveFlag() == true && !model.IsOpenedReadOnly() || promotionHasEditableState))
            {
                if (documents == null) documents = (await apiClient.GetDocumentsAsync(requested.ProjectId, lifetime.Token)).ToDictionary(item => item.Id);
                documents.TryGetValue(file.DocumentId, out var document);
                localEdits.TryGetValue(file.DocumentId, out var draft);
                if (promotionHasEditableState && document?.ProjectId != requested.ProjectId
                    || !WorkingReuseDraftMatches(path, source, document, draft, checkoutSessionId, authenticatedUsername))
                    throw WorkingModelContextMismatch(file.FileName, promotionHasEditableState
                        ? "审批发布来源已核对，但当前编辑记录、工作基线或获取会话不完整" : null);
            }
            if (isContext) LogOperation("零件打开复用已核对的装配环境，未切换父装配版本或配置：" + source.FileName);
            files.Add(new ControlledOpenFileDto { DocumentId = source.DocumentId, VersionId = source.VersionId,
                FileName = source.FileName, RelativePath = source.RelativePath, Configuration = source.Configuration,
                Revision = source.Revision, DrawingVersion = source.DrawingVersion, Sha256 = source.Sha256, FileLength = source.FileLength,
                NativeIsolation = source.NativeIsolation, IsNativeIsolated = source.IsNativeIsolated, IsRoot = source.DocumentId == requested.RootDocumentId });
        }
        var localRoot = files.Single(file => file.IsRoot);
        var rootPath = ControlledReferenceMaterializer.LocalPath(context.Key, localRoot);
        var manifest = new ControlledOpenManifestDto { Id = requested.Id, ProjectId = requested.ProjectId,
            ProjectCode = requested.ProjectCode, RootDocumentId = requested.RootDocumentId,
            RootVersionId = releasedPromotionIds.Contains(localRoot.DocumentId) ? localRoot.VersionId : requested.RootVersionId,
            RootRelativePath = localRoot.RelativePath ?? localRoot.FileName,
            RootRevision = releasedPromotionIds.Contains(localRoot.DocumentId) ? localRoot.Revision : requested.RootRevision, Files = files,
            ReferenceIssues = OpenReferenceFindings.Merge(requested.ReferenceIssues), Warnings = requested.Warnings };
        ControlledOpenBoundary.RequireLoadedSources(application, rootPath, manifest);
        var missingComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            if (loadedRoot != null) ValidateControlledLoadedSources(loadedRoot, rootPath, manifest, application, missingComponents);
            search.VerifyLoadedSources(missingComponents);
        }
        // A part file contains all its configurations. Its assembly occurrence can
        // use another one; never rewrite that owner's occurrence configuration.
        if (kind == CadDocumentKind.Part && context.Value.RootDocumentId == requested.RootDocumentId)
            context.Value.Files.Single(file => file.DocumentId == requested.RootDocumentId).Configuration = root.Configuration;
        void VerifyReleasedPromotions()
        {
            if (releasedPromotionIds.Count == 0) return;
            // Version/permission requests can yield to document callbacks. Approval
            // equivalence never authorizes a changed local binding or edit session.
            foreach (var file in files.Where(file => releasedPromotionIds.Contains(file.DocumentId)))
            {
                var path = ControlledReferenceMaterializer.LocalPath(context.Key, file);
                if (!WorkingReuseBindingMatches(path, requested.ProjectId, file))
                    throw WorkingModelContextMismatch(file.FileName, "审批发布核对期间本地绑定发生变化，请刷新当前工作副本后重试");
                var model = FindLoadedDocument(path);
                if (localEdits.ContainsKey(file.DocumentId) || model != null && !model.IsOpenedReadOnly()
                    || !ControlledReferenceMaterializer.Matches(path, file))
                {
                    DocumentDto document = null;
                    documents?.TryGetValue(file.DocumentId, out document);
                    localEdits.TryGetValue(file.DocumentId, out var draft);
                    if (document?.ProjectId != requested.ProjectId
                        || !WorkingReuseDraftMatches(path, file, document, draft, checkoutSessionId, authenticatedUsername))
                        throw WorkingModelContextMismatch(file.FileName, "审批发布核对期间编辑记录或获取会话发生变化，请核对当前工作副本后重试");
                }
            }
            var modelRoot = FindLoadedDocument(rootPath);
            if (modelRoot != null && !string.IsNullOrWhiteSpace(root.Configuration)
                && !string.Equals(modelRoot.ConfigurationManager?.ActiveConfiguration?.Name, root.Configuration, StringComparison.OrdinalIgnoreCase))
                throw WorkingModelContextMismatch(root.FileName, "审批发布核对期间当前模型配置发生变化，请恢复所选配置后重试");
        }
        VerifyReleasedPromotions();
        if (acquirePermission)
        {
            var node = new CadTreeNode { DocumentId = localRoot.DocumentId, FullPath = rootPath, FileName = localRoot.FileName,
                Kind = kind, Configuration = localRoot.Configuration, OpenedVersionId = localRoot.VersionId };
            if (!await TryAcquireReusableWorkingCopyWithVerifiedDocumentsAsync(new[] { new BatchOperationItem(node, 0) }, requested.ProjectId,
                localRoot.DocumentId, false, verifiedDocuments => documents = verifiedDocuments))
                throw WorkingModelContextMismatch(localRoot.FileName);
        }
        VerifyReleasedPromotions();
        void VerifyCommittedMemory()
        {
            foreach (var pair in committedMemoryFiles)
                if (!WorkingReuseBindingMatches(pair.Key, requested.ProjectId,
                    context.Value.Files.Single(file => file.DocumentId == pair.Value.DocumentId))
                    || !WorkingReuseCommittedMemoryMatches(pair.Key, pair.Value)) throw WorkingModelContextMismatch(pair.Value.FileName);
        }
        VerifyCommittedMemory(); // Version/draft requests above can yield to later edits.
        void VerifyCurrentReference()
        {
            if (!opensCurrentReference) return;
            if (!controlledOpenManifests.TryGetValue(context.Key, out var current) || !ReferenceEquals(current, context.Value)
                || FindLoadedDocument(Path.Combine(context.Key, current.RootRelativePath)) == null
                || files.Any(file => !current.Files.Any(source => source.DocumentId == file.DocumentId && source.VersionId == file.VersionId
                    && PathsEqual(ControlledReferenceMaterializer.LocalPath(context.Key, source), ControlledReferenceMaterializer.LocalPath(context.Key, file)))
                    || !WorkingReuseBindingMatches(ControlledReferenceMaterializer.LocalPath(context.Key, file), requested.ProjectId, file)))
                throw WorkingModelContextMismatch(localRoot.FileName, "装配工作区或版本绑定在打开期间发生变化，请刷新设计树后重试");
        }
        VerifyCurrentReference();
        using (var search = new ControlledReferenceSearchScope(application, rootPath, manifest))
        {
            var opened = OpenOrActivateDocumentOnSolidWorksThread(rootPath, ToSolidWorksDocumentType(kind),
                loadedRoot == null ? root.Configuration : localRoot.Configuration, readOnly: loadedRoot == null && !acquirePermission);
            if (opened == null)
                throw new IOException("尚未激活当前PLM工作副本，原窗口和文件已保留：" + localRoot.FileName);
            if (loadedRoot == null && !string.IsNullOrWhiteSpace(root.Configuration)
                && !string.Equals(opened.ConfigurationManager?.ActiveConfiguration?.Name, root.Configuration, StringComparison.OrdinalIgnoreCase))
                throw WorkingModelContextMismatch(root.FileName);
            if (loadedRoot == null || missingComponents.Count > 0)
            {
                missingComponents.Clear();
                ValidateControlledLoadedSources(opened, rootPath, manifest, application, missingComponents);
            }
            search.VerifyLoadedSources(missingComponents);
        }
        VerifyCommittedMemory();
        VerifyReleasedPromotions();
        VerifyCurrentReference();
        if (acquirePermission) CompleteRecordedAcquisitions(new[] { localRoot.DocumentId });
        RememberExplicitProjectPath(rootPath, requested.ProjectId);
        ScheduleTreeRefresh();
        ReportMissingComponentsOnOpen(missingComponents, acquirePermission ? "已获取权限" : "已打开", rootPath);
        LogOperation("模型复用当前受控工作区 path=" + rootPath + " context=" + context.Key + " files=" + files.Count
            + " committedMemory=" + committedMemoryFiles.Count + " closed=0 downloaded=0 acquire=" + acquirePermission
            + (opensCurrentReference ? " currentReference=" + requested.RootVersionId : string.Empty));
        return true;
    }

    private async Task<HashSet<Guid>> VerifiedOpenContextIdsAsync(ControlledOpenManifestDto requested, bool opensCurrentReference = false)
    {
        var contextIds = new HashSet<Guid>();
        var root = requested.Files.Single(file => file.DocumentId == requested.RootDocumentId);
        if (!requested.Files.Any(file => file.DocumentId != root.DocumentId
            && DocumentKindFromPath(file.FileName) == CadDocumentKind.Assembly && string.IsNullOrWhiteSpace(file.Configuration)))
            return contextIds;
        async Task<DocumentReferenceNodeDto> Snapshot(ControlledOpenFileDto file)
        {
            var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token, opensCurrentReference ? (int?)null : 1))
                .FirstOrDefault(version => !opensCurrentReference || version.Id == file.VersionId);
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

    private static bool WorkingReuseReleasePromotionMatches(ControlledOpenFileDto source, ControlledOpenFileDto requested,
        DocumentVersionDto sourceVersion, DocumentVersionDto releasedVersion)
    {
        if (source == null || requested == null || source.DocumentId != requested.DocumentId
            || source.VersionId == requested.VersionId || sourceVersion?.Id != source.VersionId
            || releasedVersion?.Id != requested.VersionId || releasedVersion.SourceVersionId != source.VersionId
            || sourceVersion.DocumentId != source.DocumentId || releasedVersion.DocumentId != source.DocumentId
            || !WorkingReuseFileBytesMatch(source, requested) || !WorkingConfigurationMatches(source, requested)
            || !WorkingReuseSnapshotVersionMatches(source, sourceVersion)
            || !WorkingReuseSnapshotVersionMatches(requested, releasedVersion)
            || !PdmApiClient.ReleasedArchiveBaseMatches(sourceVersion, releasedVersion)) return false;
        // Approval is the only accepted identity transition. Compare the full
        // represented reference evidence too, including scope, roles and suppression.
        return WorkingReuseReleaseReferenceEvidenceMatches(sourceVersion.ReferenceSnapshot, releasedVersion.ReferenceSnapshot);
    }

    private static bool WorkingReuseReleaseReferenceEvidenceMatches(DocumentReferenceNodeDto left, DocumentReferenceNodeDto right)
    {
        if (left == null || right == null) return left == right;
        // Compare values, not serialized property order: reflection-based JSON
        // serializers may enumerate the same DTO's properties in another order.
        if (left.DocumentId != right.DocumentId || left.InstancePath != right.InstancePath
            || left.FileName != right.FileName || left.DisplayName != right.DisplayName
            || left.Kind != right.Kind || left.Role != right.Role || left.Configuration != right.Configuration
            || left.IsContextReference != right.IsContextReference || left.ReferenceTypeUnverified != right.ReferenceTypeUnverified
            || left.TreeScopeOnly != right.TreeScopeOnly || left.IsSuppressed != right.IsSuppressed
            || left.IncludesDesignDependencies != right.IncludesDesignDependencies
            || left.UsesNativeFileInventory != right.UsesNativeFileInventory || left.StructureIndexPending != right.StructureIndexPending
            || left.NativeIsolationVerified != right.NativeIsolationVerified
            || left.Quantity != right.Quantity || left.Status != right.Status
            || left.Revision?.Display != right.Revision?.Display || left.CheckedOutBy != right.CheckedOutBy
            || !WorkingReuseReleaseIsolationMatches(left.NativeIsolation, right.NativeIsolation)) return false;
        if (left.ReferenceWarnings == null || right.ReferenceWarnings == null)
        {
            if (left.ReferenceWarnings != right.ReferenceWarnings) return false;
        }
        else if (!left.ReferenceWarnings.SequenceEqual(right.ReferenceWarnings, StringComparer.Ordinal)) return false;
        if (left.Children == null || right.Children == null) return left.Children == right.Children;
        return left.Children.Count == right.Children.Count
            && left.Children.Zip(right.Children, WorkingReuseReleaseReferenceEvidenceMatches).All(equal => equal);
    }

    private static bool WorkingReuseReleaseIsolationMatches(NativeFileIsolationDto left, NativeFileIsolationDto right) =>
        left == null || right == null ? left == right
        : left.SourceVersionId == right.SourceVersionId && left.SourceSha256 == right.SourceSha256
            && left.RelativePath == right.RelativePath && left.Length == right.Length && left.Sha256 == right.Sha256
            && left.GeometryEvidenceSha256 == right.GeometryEvidenceSha256 && left.PolicyVersion == right.PolicyVersion
            && left.ReferenceSnapshotId == right.ReferenceSnapshotId && left.ReferenceScopeSha256 == right.ReferenceScopeSha256;

    private static InvalidOperationException WorkingModelContextMismatch(string fileName, string reason = null) => new InvalidOperationException(
        "已打开模型与本次PLM版本、配置或编辑记录不一致，已保留窗口和修改，未切换到另一份同名副本：" + fileName
        + "。" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : "原因：" + reason + "。")
        + "请核对当前工作副本；切换版本前请保存并关闭冲突图档。");
}
