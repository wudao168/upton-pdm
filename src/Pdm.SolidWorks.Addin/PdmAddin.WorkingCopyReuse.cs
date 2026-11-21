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
    private readonly Dictionary<string, CompletedWorkingArchive> completedWorkingArchives = new Dictionary<string, CompletedWorkingArchive>(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> TryAcquireReusableWorkingCopyAsync(IReadOnlyList<BatchOperationItem> selected,
        Guid projectId, Guid rootId)
        => await TryAcquireReusableWorkingCopyCoreAsync(selected, projectId, rootId, true);

    private async Task<bool> TryAcquireReusableWorkingCopyCoreAsync(IReadOnlyList<BatchOperationItem> selected,
        Guid projectId, Guid rootId, bool completeAcquisition)
    {
        var load = BeginAssemblyReuseReferenceLoad(projectId, rootId);
        var acquired = false;
        try
        {
            acquired = await TryAcquireReusableWorkingCopyWithVerifiedDocumentsAsync(selected, projectId, rootId, completeAcquisition, null);
            return acquired;
        }
        finally { FinishAssemblyReferenceOperation(load, acquired); }
    }

    private async Task<bool> TryAcquireReusableWorkingCopyWithVerifiedDocumentsAsync(IReadOnlyList<BatchOperationItem> selected,
        Guid projectId, Guid rootId, bool completeAcquisition, Action<IReadOnlyDictionary<Guid, DocumentDto>> verifiedDocuments,
        Action<string> reportNativeConflict = null)
    {
        var rootPath = selected.FirstOrDefault(item => item.Node.DocumentId == rootId)?.Node.FullPath ?? currentTree?.FullPath;
        if (string.IsNullOrWhiteSpace(rootPath) || IsReadOnlyPreviewPath(rootPath)
            || IsHistoricalDerivedPath(rootPath) || !PdmDocumentIdentityStore.IsControlledWorkspacePath(rootPath)
            || rootPath.IndexOf(Path.DirectorySeparatorChar + "View" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0) return false;
        // A part opened from an assembly retains the assembly's manifest. Resolve
        // by its exact physical path before deciding that a new workspace is needed.
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId
            && FindWorkingManifestFile(pair, rootId, rootPath) != null
            && selected.All(item => FindWorkingManifestFile(pair, item.Node.DocumentId, item.Node.FullPath) != null))
            .OrderByDescending(pair => pair.Value.RootDocumentId == rootId).FirstOrDefault();
        if (context.Value == null) return false;
        rootId = context.Value.RootDocumentId;
        rootPath = Path.Combine(context.Key, context.Value.RootRelativePath ?? string.Empty);
        var rootModel = FindLoadedDocument(rootPath);
        if (rootModel == null) return false;
        if (!EnsureEditJournal())
            throw new InvalidOperationException("当前编辑恢复记录尚未准备完成，已保留窗口，请重新登录后重试获取权限。");
        if (editJournal.ReadAll().Any(entry => entry.DiscardPending && entry.ProjectId == projectId
            && selected.Any(item => item.Node.DocumentId == entry.DocumentId && PathsEqual(item.Node.FullPath, entry.SourcePath))))
            throw new InvalidOperationException("上次放弃编辑后的本地恢复尚未完成。请先从PLM工作区打开最新版本完成恢复，再获取权限；原文件及草稿记录仍保留。");

        await apiClient.RefreshDocumentRulesAsync(lifetime.Token);
        var latest = await apiClient.CreateControlledOpenManifestAsync(rootId, null, false, true, lifetime.Token, allowMissingComponents: true,
            useLatestModelReferences: context.Value.UsesLatestModelReferences);
        var requestedIds = new HashSet<Guid>(selected.Where(item => item.Node.DocumentId.HasValue).Select(item => item.Node.DocumentId.Value));
        var relations = FilterDrawingRelations(await apiClient.GetDocumentRelationsAsync(projectId, lifetime.Token), context.Key, true, requestedIds);
        var requiredDrawings = new HashSet<Guid>(selected.Where(item => item.Node.Kind == CadDocumentKind.Drawing && item.Node.DocumentId.HasValue)
            .Select(item => item.Node.DocumentId.Value).Concat(relations.Where(relation => requestedIds.Contains(relation.ModelDocumentId)).Select(relation => relation.DrawingDocumentId)));
        await RefreshClosedConfirmedDrawingsAsync(context.Key, projectId, latest, requiredDrawings, lifetime.Token);
        var workingManifest = ReconcileRequestedWorkingArchives(context.Key, context.Value, latest, requiredDrawings);
        workingManifest = await ReconcileStoredWorkingMembershipAsync(context.Key, workingManifest, latest, requiredDrawings);
        var committedRootSource = requestedIds.Contains(rootId) ? CommittedAssemblySource(context.Key, workingManifest, latest) : null;
        if (committedRootSource != null)
            workingManifest = WithCommittedAssemblyRoot(workingManifest, latest, requiredDrawings);
        var committedRoot = committedRootSource == null ? null : latest.Files.Single(file => file.DocumentId == rootId);
        var committedHeads = await FindCompletedWorkingHeadsAsync(context.Key, workingManifest, latest, requestedIds);
        var comparisonLatest = WithWorkingFiles(latest, latest.Files.Select(file =>
            committedHeads.TryGetValue(file.DocumentId, out var head) ? head.File : file).ToArray());
        var documents = (await apiClient.GetDocumentsAsync(projectId, lifetime.Token)).ToDictionary(document => document.Id);
        var latestById = latest.Files.ToDictionary(file => file.DocumentId);
        var committedChildren = new Dictionary<Guid, ControlledOpenFileDto>();
        var loadedDocuments = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(document => new { Document = document, Path = document.GetPathName() }).ToArray();
        // A standalone part can also have assembly environment files in its
        // download manifest. Validate only the selected models and their dependencies.
        HashSet<Guid> selectedProofIds = null;
        var scopeVersions = new Dictionary<Guid, DocumentVersionDto>();
        if ((!requestedIds.Contains(rootId) || DocumentKindFromPath(rootPath) == CadDocumentKind.Part)
            && selected.All(item => item.Node.Kind is CadDocumentKind.Part or CadDocumentKind.Assembly))
            selectedProofIds = await ReadSelectedModelAcquisitionScopeAsync(requestedIds, requiredDrawings, projectId, workingManifest, latest, scopeVersions);
        var selectedModelScope = selectedProofIds != null;
        var scopedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ControlledOpenManifestDto AcquisitionScope(ControlledOpenManifestDto manifest)
        {
            var scope = WorkingAcquisitionScope(manifest, requiredDrawings);
            return selectedProofIds == null ? scope : WithWorkingFiles(scope,
                scope.Files.Where(file => selectedProofIds.Contains(file.DocumentId)).ToArray());
        }
        var localChanges = false;
        var hasUnsavedLoadedDocument = loadedDocuments.Any(item => IsPathWithinDirectory(item.Path, context.Key) && item.Document.GetSaveFlag());
        var editableAssemblyIds = new HashSet<Guid>();
        var assemblySourceHashes = new Dictionary<Guid, string>();
        var committedRootSynchronized = false;
        foreach (var file in workingManifest.Files)
        {
            if (selectedProofIds != null && !selectedProofIds.Contains(file.DocumentId)) continue;
            if (DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing && !requiredDrawings.Contains(file.DocumentId)) continue;
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, file);
            if (committedRoot != null && file.DocumentId == rootId)
            {
                VerifyCommittedAssemblySource(context.Key, projectId, committedRootSource, committedRoot);
                if (localEdits.TryGetValue(rootId, out var existingEdit) && !existingEdit.Closed)
                    throw new InvalidOperationException("总装仍有编辑恢复记录，已保留窗口和图档。请先核对该记录再获取权限。");
                assemblySourceHashes[file.DocumentId] = ComputeFileHash(path);
                continue;
            }
            if (!WorkingReuseBindingMatches(path, context.Value.ProjectId, file))
                throw new InvalidOperationException("当前工作副本的版本关联需要核对，已保留窗口和修改，未重新取回：" + file.FileName);
            var loaded = loadedDocuments.FirstOrDefault(document => PathsEqual(document.Path, path))?.Document;
            var changed = !ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, file, out var actualHash);
            if (selectedModelScope) scopedHashes[path] = actualHash;
            if (!changed && string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(path).Length != file.FileLength)
                throw new InvalidOperationException("当前工作副本的基准长度需要核对，已保留窗口和文件：" + file.FileName);
            var unsaved = loaded?.GetSaveFlag() == true;
            hasUnsavedLoadedDocument |= unsaved;
            var unsavedEdit = unsaved && !loaded.IsOpenedReadOnly();
            documents.TryGetValue(file.DocumentId, out var document);
            localEdits.TryGetValue(file.DocumentId, out var draft);
            var verifiedDraft = WorkingReuseDraftMatches(path, file, document, draft, checkoutSessionId, authenticatedUsername);
            var selectedLoadedAssembly = loaded is IAssemblyDoc
                && selected.Any(item => item.Node.DocumentId == file.DocumentId && item.Node.Kind == CadDocumentKind.Assembly);
            if (DocumentKindFromPath(file.FileName) == CadDocumentKind.Assembly
                && (!changed || verifiedDraft) && !string.IsNullOrWhiteSpace(actualHash))
                assemblySourceHashes.Add(file.DocumentId, actualHash);
            var selectedAssemblyEdit = selectedLoadedAssembly && !changed && unsaved
                && document != null && string.IsNullOrWhiteSpace(document.CheckedOutBy) && !document.CheckoutSessionId.HasValue;
            if (selectedAssemblyEdit || selectedLoadedAssembly && verifiedDraft && unsaved) editableAssemblyIds.Add(file.DocumentId);
            if (draft != null && !draft.Closed && !verifiedDraft)
                throw new InvalidOperationException("当前工作副本的编辑恢复记录与路径或版本不一致，已保留原记录和窗口，请核对后再获取权限：" + file.FileName);
            if (!requestedIds.Contains(file.DocumentId) && latestById.TryGetValue(file.DocumentId, out var stored)
                && string.Equals(file.FileName, stored.FileName, StringComparison.OrdinalIgnoreCase)
                && PathsEqual(path, ControlledReferenceMaterializer.LocalPath(context.Key, stored))
                && WorkingManifestConfigurationMatches(rootId, file, stored) && WorkingReuseCommittedMemoryMatches(path, stored))
            {
                committedChildren.Add(file.DocumentId, stored);
                LogOperation("获取总装权限复用已确认的子件存档内存状态，保留原磁盘基准：" + file.FileName);
                continue;
            }
            if (!changed && !unsavedEdit) continue;
            localChanges = true;
            if (!verifiedDraft && !selectedAssemblyEdit)
                throw new InvalidOperationException("当前工作副本存在尚未确认基准的本地修改，已保留窗口和文件。请核对后再获取权限：" + file.FileName);
        }
        bool CannotReuse(string reason)
        {
            LogOperation("Working reuse declined: " + reason);
            if (localChanges || hasUnsavedLoadedDocument || rootModel.GetSaveFlag())
                throw new InvalidOperationException("当前图档有未保存或未提交的修改，且" + reason + "。已保留当前窗口和修改，请核对后再获取权限。");
            return false;
        }
        var workingScope = AcquisitionScope(workingManifest);
        var latestScope = AcquisitionScope(latest);
        var comparisonLatestScope = AcquisitionScope(comparisonLatest);
        // This projection is only for comparing the already-committed in-session
        // content. Disk bindings and the physical working manifest stay unchanged.
        var comparisonScope = WithWorkingFiles(workingScope, workingScope.Files.Select(file =>
            committedChildren.TryGetValue(file.DocumentId, out var committed) ? committed : file).ToArray());
        if (!await WorkingReuseStoredSourceManifestsMatchAsync(comparisonScope, comparisonLatestScope))
            return CannotReuse("工作基准与服务器不一致：" + await WorkingReuseVerifiedDifferencesAsync(comparisonScope, comparisonLatestScope));
        void VerifyCommittedChildren()
        {
            foreach (var committed in committedChildren.Values)
            {
                var source = workingManifest.Files.Single(file => file.DocumentId == committed.DocumentId);
                var path = ControlledReferenceMaterializer.LocalPath(context.Key, source);
                if (!WorkingReuseBindingMatches(path, projectId, source) || !WorkingReuseCommittedMemoryMatches(path, committed))
                    throw new InvalidOperationException("已存档的子件状态在获取期间发生变化，已保留窗口、文件及现有权限，尚未解锁总装：" + committed.FileName);
            }
        }
        var referenceProofs = new Dictionary<string, WorkingAssemblyReferenceProof>(StringComparer.Ordinal);
        var archivedReferenceCache = new Dictionary<Guid, HashSet<string>>();
        var archivedOccurrences = new Dictionary<string, ArchivedMissingOccurrence[]>(StringComparer.OrdinalIgnoreCase);
        void VerifyAssemblyReferenceSource(ControlledOpenFileDto owner)
        {
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, owner);
            if (owner.DocumentId == committedRoot?.DocumentId && !committedRootSynchronized)
                VerifyCommittedAssemblySource(context.Key, projectId, committedRootSource, committedRoot);
            else if (!WorkingReuseBindingMatches(path, projectId, owner))
                throw new IOException("所属装配体的版本关联需要核对，当前窗口与修改已保留：" + owner.FileName);
            if (!assemblySourceHashes.TryGetValue(owner.DocumentId, out var hash)
                || !string.Equals(ComputeFileHash(path), hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("核对引用期间装配文件发生变化，当前窗口和修改已保留：" + owner.FileName);
        }
        void VerifyArchiveReferenceSources()
        {
            foreach (var id in referenceProofs.Values.Select(proof => proof.OwnerId).Distinct())
                VerifyAssemblyReferenceSource(workingManifest.Files.Single(file => file.DocumentId == id));
        }
        var paths = latest.Files.ToDictionary(file => file.DocumentId,
            file => ControlledReferenceMaterializer.LocalPath(context.Key, file));
        if (selected.Any(item => !item.Node.DocumentId.HasValue || !paths.TryGetValue(item.Node.DocumentId.Value, out var path)
            || !PathsEqual(path, item.Node.FullPath))) return CannotReuse("所选图档不在当前受控工作副本中");
        if (relations.Any(relation => requestedIds.Contains(relation.ModelDocumentId) && !paths.ContainsKey(relation.DrawingDocumentId)))
            return CannotReuse("配套工程图尚未在当前工作副本中准备完整");
        var rootVersion = selectedModelScope ? null
            : (await apiClient.GetVersionsAsync(rootId, lifetime.Token)).FirstOrDefault(version => version.Id == latest.RootVersionId);
        if (!selectedModelScope && !WorkingReuseSnapshotVersionMatches(latest.Files.Single(file => file.DocumentId == rootId), rootVersion))
            return CannotReuse("当前版本缺少可核对的引用快照");
        var missingComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedColdOpenEvidence = false;
        var exactSnapshots = scopeVersions.ToDictionary(pair => pair.Key, pair => pair.Value.ReferenceSnapshot);
        if (!selectedModelScope) exactSnapshots.Add(rootId, rootVersion.ReferenceSnapshot);
        var selectedModels = selected.Where(item => item.Node.Kind is CadDocumentKind.Part or CadDocumentKind.Assembly)
            .GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First())
            .Select(item => new { Item = item, Model = loadedDocuments.FirstOrDefault(loaded => PathsEqual(loaded.Path, item.Node.FullPath))?.Document }).ToArray();
        if (selectedModelScope)
            foreach (var item in selectedModels)
            {
                var file = workingManifest.Files.Single(candidate => candidate.DocumentId == item.Item.Node.DocumentId);
                var exact = scopeVersions.TryGetValue(file.DocumentId, out var verified) && verified.Id == file.VersionId ? verified
                    : (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                if (!WorkingReuseSnapshotVersionMatches(file, exact))
                    return CannotReuse("所选模型的精确版本快照无法核对：" + file.FileName);
                exactSnapshots[file.DocumentId] = exact.ReferenceSnapshot;
            }
        bool SelectedModelSourcesMatch(ISet<Guid> missing = null, Action<string> reportConflict = null,
            IDictionary<string, WorkingAssemblyReferenceScope> pendingReferences = null)
        {
            if (scopedHashes.Any(pair => string.IsNullOrWhiteSpace(pair.Value)
                || !string.Equals(ComputeFileHash(pair.Key), pair.Value, StringComparison.OrdinalIgnoreCase))) return false;
            ControlledOpenBoundary.RequireLoadedSources(application, rootPath, workingScope);
            var matched = true;
            foreach (var item in selectedModels)
            {
                var file = workingManifest.Files.Single(candidate => candidate.DocumentId == item.Item.Node.DocumentId);
                var loaded = FindLoadedDocument(item.Item.Node.FullPath);
                if (item.Model != null && !ReferenceEquals(item.Model, loaded)) return false;
                if (loaded == null) continue; // The verified source can be checked out without loading it or its parent.
                if (!WorkingReuseLoadedContextForEditMatches(loaded, context.Key, workingScope,
                        exactSnapshots[file.DocumentId], exactSnapshots, missing, editableAssemblyIds, reportConflict, missingComponents,
                        standalonePart: item.Item.Node.Kind == CadDocumentKind.Part, referenceProofs: referenceProofs,
                        pendingReferences: pendingReferences, application: application, onIndexCompleted: () => usedColdOpenEvidence = true))
                {
                    matched = false;
                    continue;
                }
                // The occurrence walk above already verifies physical sources,
                // controlled bytes, configurations and structure for this scope.
                // Re-rooting each loaded child in the open-time scanner would
                // inspect its active configuration instead of the actual instance.
                NativeDesignDependencies.Validate(loaded, ControlledOpenBoundary.Paths(context.Key, workingScope));
            }
            return matched;
        }
        async Task<string> VerifyLoadedContextAsync(Guid? unsynchronizedRootId)
        {
            while (true)
            {
                var missing = new HashSet<Guid>();
                var pendingReferences = new Dictionary<string, WorkingAssemblyReferenceScope>(StringComparer.Ordinal);
                string nativeConflict = null;
                missingComponents.Clear();
                if (selectedModelScope ? SelectedModelSourcesMatch(missing, conflict => nativeConflict = conflict, pendingReferences)
                    : WorkingReuseLoadedContextForEditMatches(rootModel, context.Key, workingManifest, rootVersion.ReferenceSnapshot, exactSnapshots, missing, editableAssemblyIds,
                        conflict => nativeConflict = conflict, missingComponents, unsynchronizedRootId, referenceProofs: referenceProofs,
                        pendingReferences: pendingReferences, application: application, onIndexCompleted: () => usedColdOpenEvidence = true)) return null;
                if (missing.Count == 0 && pendingReferences.Count == 0)
                    return "实际加载的引用或配置与当前基准不一致：" + nativeConflict;
                foreach (var scope in pendingReferences.Values)
                {
                    VerifyAssemblyReferenceSource(scope.Owner);
                    scope.ArchivedOccurrences = archivedOccurrences;
                    var proof = new WorkingAssemblyReferenceProof { OwnerId = scope.Owner.DocumentId };
                    workingManifest = await VerifyWorkingAssemblyReferencesAsync(context.Key, workingManifest, scope,
                        editableAssemblyIds, documents, latestById, proof, archivedReferenceCache);
                    referenceProofs.Add(scope.Key, proof);
                }
                VerifyArchiveReferenceSources();
                if (selectedModelScope)
                    foreach (var file in workingManifest.Files.Where(file => !latestById.ContainsKey(file.DocumentId)))
                    {
                        selectedProofIds.Add(file.DocumentId);
                        var insertedPath = ControlledReferenceMaterializer.LocalPath(context.Key, file);
                        if (!scopedHashes.ContainsKey(insertedPath)) scopedHashes[insertedPath] = ComputeFileHash(insertedPath);
                    }
                workingScope = AcquisitionScope(workingManifest);
                foreach (var id in missing)
                {
                    var file = latest.Files.Single(candidate => candidate.DocumentId == id);
                    var exact = (await apiClient.GetVersionsAsync(id, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                    if (!WorkingReuseSnapshotVersionMatches(file, exact))
                        return "引用图档的精确版本快照无法核对：" + file.FileName;
                    exactSnapshots[id] = exact.ReferenceSnapshot;
                    LogOperation("Working reuse exact snapshot file=" + file.FileName + " version=" + file.VersionId);
                }
            }
        }
        try
        {
            var conflict = await VerifyLoadedContextAsync(committedRoot?.DocumentId);
            if (conflict != null)
            {
                reportNativeConflict?.Invoke(conflict);
                return CannotReuse(conflict);
            }
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            LogDiagnostic("获取权限前核对当前工作副本", error);
            throw new InvalidOperationException("暂未完成当前装配引用核对，已保留窗口和修改，请稍后重试。");
        }

        // Only requested documents gain permissions. Existing drafts, unrelated file
        // attributes and the loaded root remain untouched; never retire this context.
        var selectedChildren = workingManifest.Files.Where(file => file.DocumentId != rootId
            && requestedIds.Contains(file.DocumentId) && !committedHeads.ContainsKey(file.DocumentId)).ToArray();
        var selectedVersions = await VerifySelectedWorkingVersionsAsync(selectedChildren);
        await VerifyCompletedWorkingHeadsAsync(context.Key, workingManifest, committedHeads);
        VerifyCommittedChildren();
        using var synchronizedRoot = committedRoot == null ? null : await PrepareCommittedRootForCheckoutAsync();
        var inserted = workingManifest.Files.Where(file => !latestById.ContainsKey(file.DocumentId)).ToArray();
        async Task<ArchiveMemoryCopy> PrepareCommittedRootForCheckoutAsync()
        {
            if (!documents.TryGetValue(rootId, out var currentRoot) || currentRoot.ProjectId != projectId
                || !string.IsNullOrWhiteSpace(currentRoot.CheckedOutBy) || currentRoot.CheckoutSessionId.HasValue)
                throw new InvalidOperationException("总装当前权限已变化，已保留窗口和图档，请刷新后核对。");
            rootModel = SynchronizeCommittedAssemblyRoot(context.Key, workingManifest, committedRootSource, committedRoot);
            committedRootSynchronized = true;
            assemblySourceHashes[rootId] = ComputeFileHash(rootPath);
            controlledOpenManifests[context.Key] = workingManifest;
            // Reload can expose a different lightweight or suppressed occurrence
            // graph. Obtain its evidence before asking the server for any lease.
            var memory = new ArchiveMemoryCopy(rootModel, rootPath) { Stamp = rootModel.GetUpdateStamp() };
            try
            {
                var conflict = await VerifyLoadedContextAsync(null);
                if (conflict != null)
                    throw new InvalidOperationException("同步后总装" + conflict + "。已保留窗口和图档，尚未获取权限。");
                if (!SynchronizedRootMatches(memory))
                    throw new InvalidOperationException("核对引用期间总装发生变化，已保留窗口和图档，尚未获取权限。");
                return memory;
            }
            catch
            {
                memory.Dispose();
                throw;
            }
        }
        bool SynchronizedRootMatches(ArchiveMemoryCopy memory) => memory.MatchesNativeState()
            && ReferenceEquals(memory.Model, FindLoadedDocument(rootPath))
            && WorkingHeadDiskMatches(context.Key, projectId, committedRoot);
        void VerifyColdOpenEvidence()
        {
            if (!usedColdOpenEvidence) return;
            var owner = FindLoadedDocument(rootPath);
            var file = workingManifest.Files.Single(entry => entry.DocumentId == rootId);
            var rows = ReferenceEquals(owner, rootModel) && owner is IAssemblyDoc assembly && !owner.GetSaveFlag()
                ? assembly.GetComponents(true) as Array : null;
            var components = rows?.OfType<IComponent2>().ToArray();
            if (components == null || components.Length != rows.Length
                || !MatchesReadOnlyAssemblyOpen(owner, rootPath, workingManifest, file,
                    owner.ConfigurationManager?.ActiveConfiguration?.Name, components))
                throw new InvalidOperationException("获取期间总装窗口、版本或直属组件已变化。请核对当前装配后重试。");
        }
        foreach (var item in selected.GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First()))
        {
            VerifyColdOpenEvidence();
            var id = item.Node.DocumentId.Value;
            documents.TryGetValue(id, out var previousDocument);
            var newCheckout = previousDocument != null && string.IsNullOrWhiteSpace(previousDocument.CheckedOutBy);
            plmAuthoritativeCheckouts.Add(id);
            DocumentDto document;
            try { document = await AcquireRecordedCheckoutAsync(id, projectId, paths[id], newCheckout); }
            finally { plmAuthoritativeCheckouts.Remove(id); }
            if (document.Id != id || document.ProjectId != projectId || document.CheckoutSessionId != checkoutSessionId
                || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务器尚未确认所选图档的编辑权限，已保留当前工作副本：" + item.Node.FileName);
            documents[id] = document;
            var matching = EnumerateCadNodes(currentTree).Where(node => node.DocumentId == id && PathsEqual(node.FullPath, paths[id]))
                .Concat(new[] { item.Node }).Distinct().ToArray();
            // Record the acquired lease against this verified path before the second
            // request. Even a later validation failure must remain closable/recoverable.
            ApplyCheckoutDocumentToMatchingInstances(matching, document);
            localEdits.TryGetValue(id, out var recordedDraft);
            var file = workingManifest.Files.Single(candidate => candidate.DocumentId == id);
            if (!WorkingReuseDraftMatches(paths[id], file, document, recordedDraft, checkoutSessionId, authenticatedUsername))
            {
                throw new InvalidOperationException("编辑恢复记录未能安全保存，已停止解锁并保留当前窗口和文件，请检查诊断日志后重试：" + item.Node.FileName);
            }
        }
        // Checkout serializes changes to each requested document. Recheck after it so
        // a version published during the first manifest request is never unlocked stale.
        var acquired = await apiClient.CreateControlledOpenManifestAsync(rootId, null, false, true, lifetime.Token, allowMissingComponents: true,
            useLatestModelReferences: context.Value.UsesLatestModelReferences);
        if (selectedModelScope)
        {
            var confirmedScope = await ReadSelectedModelAcquisitionScopeAsync(requestedIds, requiredDrawings, projectId,
                workingManifest, acquired, new Dictionary<Guid, DocumentVersionDto>());
            if (confirmedScope == null || !selectedProofIds.SetEquals(confirmedScope.Concat(inserted.Select(file => file.DocumentId))))
                throw new InvalidOperationException("获取权限期间所选模型的引用范围发生变化，已保留窗口、文件及权限记录，请核对后重试。");
        }
        if (!WorkingReuseManifestsMatch(latestScope, AcquisitionScope(acquired)))
            throw new InvalidOperationException("获取权限期间服务器版本发生变化：" + WorkingReuseManifestDifferences(latestScope, AcquisitionScope(acquired))
                + "。已保留当前窗口、文件和已获取权限，请核对后继续。");
        foreach (var added in inserted)
            if ((await apiClient.GetVersionsAsync(added.DocumentId, lifetime.Token, 1)).FirstOrDefault()?.Id != added.VersionId)
                throw new InvalidOperationException("获取权限期间新增零件的服务器版本已变化，装配和已获取的权限均保留，尚未解锁总装：" + added.FileName);
        await VerifyCompletedWorkingHeadsAsync(context.Key, workingManifest, committedHeads);
        await VerifySelectedWorkingVersionsAsync(selectedChildren, selectedVersions);
        VerifyCommittedChildren();
        VerifyArchiveReferenceSources();
        VerifyColdOpenEvidence();
        if (selectedModelScope && !SelectedModelSourcesMatch())
            throw new InvalidOperationException("获取权限期间所选模型的实际配置或引用发生变化，已保留窗口和权限记录，尚未解锁。请核对后重试。");
        if (!selectedModelScope && (referenceProofs.Count > 0 || exactSnapshots.Count > 1 || usedColdOpenEvidence)
            && !WorkingReuseLoadedContextForEditMatches(rootModel, context.Key, workingManifest, rootVersion.ReferenceSnapshot,
                exactSnapshots, null, editableAssemblyIds, missingComponents: missingComponents, referenceProofs: referenceProofs,
                application: application))
            throw new InvalidOperationException("获取权限期间装配体的实际引用发生变化，已保留窗口和权限记录，尚未解锁。");
        if (synchronizedRoot != null && !SynchronizedRootMatches(synchronizedRoot))
            throw new InvalidOperationException("总装在获取期间发生变化，已保留当前窗口、图档和权限记录，尚未解锁。");
        controlledOpenManifests[context.Key] = workingManifest;
        foreach (var item in selected.GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First()))
        {
            var id = item.Node.DocumentId.Value;
            await EnsureCheckedOutDocumentEditableAsync(paths[id], id, projectId, documents[id].CheckedOutAt);
        }
        await AcquireLinkedDrawingPermissionsAsync(projectId, selected.Select(item => item.Node.DocumentId.Value).Distinct(), context.Key, lifetime.Token);
        if (completeAcquisition) CompleteRecordedAcquisitions(requestedIds);
        ReportMissingComponentsOnOpen(missingComponents, "已获取权限", rootPath);
        LogOperation("Working reuse completed root=" + rootPath + " selected=" + selected.Select(item => item.Node.DocumentId).Distinct().Count()
            + " files=" + latest.Files.Count + " reopened=0 replaced=0");
        verifiedDocuments?.Invoke(documents);
        return true;
    }

    private async Task<HashSet<Guid>> ReadSelectedModelAcquisitionScopeAsync(ISet<Guid> selected, ISet<Guid> drawings,
        Guid projectId, ControlledOpenManifestDto working, ControlledOpenManifestDto latest,
        IDictionary<Guid, DocumentVersionDto> verifiedVersions)
    {
        var ids = new HashSet<Guid>(drawings);
        foreach (var id in selected)
        {
            var model = id == latest.RootDocumentId ? latest : await apiClient.CreateControlledOpenManifestAsync(id, null, false, true, lifetime.Token,
                allowMissingComponents: true, useLatestModelReferences: latest.UsesLatestModelReferences);
            if (model == null || model.ProjectId != projectId || model.RootDocumentId != id || model.Files == null
                || !model.Files.Any(file => file.DocumentId == id && file.IsRoot)) return null;
            ids.UnionWith(await ReadRequiredOperationFileIdsAsync(model, verifiedVersions));
        }
        // Missing/new dependencies need the existing complete reconciliation path.
        if (!ids.IsSubsetOf(new HashSet<Guid>(working.Files.Select(file => file.DocumentId)))
            || !ids.IsSubsetOf(new HashSet<Guid>(latest.Files.Select(file => file.DocumentId)))) return null;
        LogOperation("单件获取校验范围 selected=" + selected.Count + " dependencyFiles=" + ids.Count
            + " workspaceFiles=" + working.Files.Count);
        return ids;
    }

    private async Task<IReadOnlyDictionary<Guid, Guid>> VerifySelectedWorkingVersionsAsync(
        IReadOnlyList<ControlledOpenFileDto> selected, IReadOnlyDictionary<Guid, Guid> expected = null)
    {
        // The assembly manifest contains as-built reference versions. It cannot
        // establish the archive base for a child explicitly selected for editing.
        using var slots = new System.Threading.SemaphoreSlim(6);
        var versions = await Task.WhenAll(selected.Select(async file =>
        {
            await slots.WaitAsync(lifetime.Token);
            try
            {
                var latest = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token, 1)).FirstOrDefault();
                if (latest != null && (expected != null ? latest.Id == expected[file.DocumentId] : latest.Id == file.VersionId))
                    return new KeyValuePair<Guid, Guid>(file.DocumentId, latest.Id);
                if (expected == null && latest?.Status == 1 && latest.SourceVersionId == file.VersionId)
                {
                    var history = await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token);
                    if (history.FirstOrDefault()?.Id == latest.Id && PdmApiClient.ReleasedArchiveBaseMatches(
                        history.FirstOrDefault(version => version.Id == file.VersionId), latest))
                        return new KeyValuePair<Guid, Guid>(file.DocumentId, latest.Id);
                }
                var versionLabel = latest?.DrawingVersion;
                if (string.IsNullOrWhiteSpace(versionLabel)) versionLabel = "最新版";
                else versionLabel = "图纸 " + versionLabel;
                throw new InvalidOperationException(file.FileName + " 的当前工作副本基准与服务器" + versionLabel
                    + "不一致，已停止解锁并保留本地修改。请先保留修改，再关闭正在引用旧版的装配和图档，从工作区打开最新版获取权限。");
            }
            finally { slots.Release(); }
        }));
        return versions.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static bool WorkingReuseManifestsMatch(ControlledOpenManifestDto previous, ControlledOpenManifestDto current) =>
        WorkingReuseManifestsMatchCore(previous, current, WorkingReuseFileBytesMatch);

    private static bool WorkingReuseFileBytesMatch(ControlledOpenFileDto before, ControlledOpenFileDto current) =>
        before.FileLength == current.FileLength && string.Equals(before.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool WorkingConfigurationMatches(ControlledOpenFileDto before, ControlledOpenFileDto current) =>
        DocumentKindFromPath(current.FileName) == CadDocumentKind.Drawing
        || string.Equals(before.Configuration ?? string.Empty, current.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool WorkingManifestConfigurationMatches(Guid rootId, ControlledOpenFileDto before, ControlledOpenFileDto current) =>
        // A part file contains all configurations; one inventory row can describe
        // any of its occurrences. Check each actual instance against the snapshot.
        current.DocumentId != rootId && DocumentKindFromPath(current.FileName) == CadDocumentKind.Part
        || WorkingConfigurationMatches(before, current);

    private async Task<bool> WorkingReuseStoredSourceManifestsMatchAsync(ControlledOpenManifestDto previous, ControlledOpenManifestDto current)
    {
        if (WorkingReuseManifestsMatch(previous, current)) return true;
        // Check-in records the verified source bytes in this local context, while the
        // server may store a distinct upload copy of the exact same version. Never
        // apply this exception to a different identity, version, path or configuration.
        if (!WorkingReuseManifestsMatchCore(previous, current, (before, after) => true)) return false;
        var byId = previous.Files.ToDictionary(file => file.DocumentId);
        foreach (var file in current.Files.Where(file => !WorkingReuseFileBytesMatch(byId[file.DocumentId], file)))
        {
            var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
            if (!WorkingReuseStoredSourceVersionMatches(byId[file.DocumentId], file, exact)) return false;
        }
        return true;
    }

    private static bool WorkingReuseStoredSourceVersionMatches(ControlledOpenFileDto source, ControlledOpenFileDto stored, DocumentVersionDto exact) =>
        source.DocumentId == stored.DocumentId && source.VersionId == stored.VersionId && exact?.Id == stored.VersionId
        && exact.FileLength == stored.FileLength && !string.IsNullOrWhiteSpace(exact.Sha256)
        && string.Equals(exact.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(VersionSourceSha256(exact))
        && string.Equals(VersionSourceSha256(exact), source.Sha256, StringComparison.OrdinalIgnoreCase)
        && (!string.Equals(source.Sha256, stored.Sha256, StringComparison.OrdinalIgnoreCase) || source.FileLength == stored.FileLength);

    private static bool WorkingReuseSnapshotVersionMatches(ControlledOpenFileDto file, DocumentVersionDto exact) =>
        exact?.Id == file.VersionId && exact.ReferenceSnapshot?.DocumentId == file.DocumentId
        && exact.FileLength == file.FileLength && !string.IsNullOrWhiteSpace(exact.Sha256)
        && string.Equals(exact.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(exact.ReferenceSnapshot.FileName, file.FileName, StringComparison.OrdinalIgnoreCase);

    private static bool WorkingReuseManifestsMatchCore(ControlledOpenManifestDto previous, ControlledOpenManifestDto current,
        Func<ControlledOpenFileDto, ControlledOpenFileDto, bool> bytesMatch)
    {
        // A latest read-only working context can gain permissions in place. Snapshot
        // and historical paths are excluded before this manifest comparison.
        if (previous?.Files == null || current?.Files == null || !current.ForEdit
            || previous.ProjectId != current.ProjectId || previous.RootDocumentId != current.RootDocumentId
            || !WorkingReuseRootVersionMatches(previous, current) || !PathsEqual(previous.RootRelativePath, current.RootRelativePath)
            || previous.Files.Count != current.Files.Count || previous.Files.Count == 0
            || previous.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)
            || current.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)) return false;
        var byId = previous.Files.ToDictionary(file => file.DocumentId);
        return current.Files.All(file => byId.TryGetValue(file.DocumentId, out var before)
            && file.VersionId == before.VersionId && bytesMatch(before, file)
            && string.Equals(file.FileName, before.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.RelativePath ?? file.FileName, before.RelativePath ?? before.FileName, StringComparison.OrdinalIgnoreCase)
            && WorkingManifestConfigurationMatches(current.RootDocumentId, before, file));
    }

    private static bool WorkingReuseBindingMatches(string path, Guid projectId, ControlledOpenFileDto file) =>
        PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) && binding.ProjectId == projectId
        && binding.DocumentId == file.DocumentId && binding.VersionId == file.VersionId && binding.FileLength == file.FileLength
        && string.Equals(binding.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool WorkingReuseDraftMatches(string path, ControlledOpenFileDto file, DocumentDto document,
        LocalEditDraft draft, Guid sessionId, string username) => document != null && draft != null
        && document.Id == file.DocumentId && document.CheckoutSessionId == sessionId
        && string.Equals(document.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(username) && draft.DocumentId == file.DocumentId && draft.ProjectId == document.ProjectId
        && draft.SessionId == sessionId && !draft.Closed && !draft.DiscardPending && !draft.PendingClose
        && document.CheckedOutAt.HasValue && draft.CheckedOutAt == document.CheckedOutAt.Value
        && draft.BaseVersionId == file.VersionId && PathsEqual(draft.SourcePath, path)
        && PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
        && !string.IsNullOrWhiteSpace(draft.BaseSha256)
        && (string.Equals(draft.BaseSha256, binding.Sha256, StringComparison.OrdinalIgnoreCase)
            || string.Equals(draft.BaseSha256, binding.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));

    private static bool WorkingReuseLoadedContextMatches(IModelDoc2 root, string directory,
        ControlledOpenManifestDto manifest, DocumentReferenceNodeDto snapshot) =>
        WorkingReuseLoadedContextMatchesCore(root, directory, manifest, snapshot,
            new Dictionary<Guid, DocumentReferenceNodeDto> { [manifest.RootDocumentId] = snapshot }, null);

    private static bool WorkingReuseLoadedContextMatchesCore(IModelDoc2 root, string directory,
        ControlledOpenManifestDto manifest, DocumentReferenceNodeDto snapshot,
        IReadOnlyDictionary<Guid, DocumentReferenceNodeDto> exactSnapshots, ISet<Guid> missingSnapshots)
        => WorkingReuseLoadedContextForEditMatches(root, directory, manifest, snapshot, exactSnapshots, missingSnapshots, null);

    private static bool WorkingReuseLoadedContextForEditMatches(IModelDoc2 root, string directory,
        ControlledOpenManifestDto manifest, DocumentReferenceNodeDto snapshot,
        IReadOnlyDictionary<Guid, DocumentReferenceNodeDto> exactSnapshots, ISet<Guid> missingSnapshots, ISet<Guid> editableAssemblyIds,
        Action<string> reportConflict = null, ISet<string> missingComponents = null, Guid? committedAssemblyRootId = null,
        bool standalonePart = false, IReadOnlyDictionary<string, WorkingAssemblyReferenceProof> referenceProofs = null,
        IDictionary<string, WorkingAssemblyReferenceScope> pendingReferences = null, ISldWorks application = null,
        Action onIndexCompleted = null)
    {
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var snapshots = WorkingReuseSnapshotNodes(snapshot).ToLookup(node => node.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        ImportedComponentStorage importStorage = null;
        bool Reject(string reason, string path, string detail, string userDetail = null)
        {
            LogOperation("Working reuse native rejected reason=" + reason + " path=" + path + " " + detail);
            reportConflict?.Invoke(Path.GetFileName(path) + "（" + (userDetail ?? "实际来源、配置或组件结构需核对") + "）");
            return false;
        }
        bool NeedSnapshot(ControlledOpenFileDto file, string configuration)
        {
            if (missingSnapshots != null && !exactSnapshots.ContainsKey(file.DocumentId))
            {
                missingSnapshots.Add(file.DocumentId);
                LogOperation("Working reuse needs exact snapshot file=" + file.FileName + " configuration=" + configuration);
                return false;
            }
            return Reject("missing-instance-snapshot", file.FileName, "configuration=" + configuration);
        }
        bool VisitChildren(IComponent2[] components, DocumentReferenceNodeDto expected, string path,
            ControlledOpenFileDto file, IModelDoc2 loadedOwner, string configuration, int depth)
        {
            if (depth > 128) return Reject("instance-depth", path, "configuration=" + configuration);
            var nativeComponents = components;
            var embedded = components.Where(component =>
            {
                if (component.IsVirtual) return true;
                if (application == null || !NativeArchiveManifest.IsInterconnectCachePath(component.GetPathName())) return false;
                importStorage ??= new ImportedComponentStorage(application) { AllowInventory = false };
                return PathsEqual(importStorage.FindOwner(component, root), path);
            }).ToArray();
            bool IsEmbedded(IComponent2 component) => embedded.Any(member => ReferenceEquals(member, component));
            var external = components.Where(component => !IsEmbedded(component)).ToArray();
            WorkingAssemblyReferenceProof referenceProof = null;
            var missingBaseline = referenceProofs == null ? Array.Empty<string>() : ArchiveMissingReferencePolicy.ArchivedMissingPaths(
                exactSnapshots.TryGetValue(file.DocumentId, out var exactOwner) && string.Equals(configuration, exactOwner.Configuration, StringComparison.OrdinalIgnoreCase)
                    ? exactOwner.ReferenceWarnings : expected.ReferenceWarnings)
                .Where(source => IsNativeModelReference(source) && !files.ContainsKey(Path.GetFileName(source)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (referenceProofs != null && (missingBaseline.Length > 0 || external.Any(component =>
                !files.TryGetValue(Path.GetFileName(component.GetPathName()) ?? string.Empty, out var referenced)
                || !PathsEqual(component.GetPathName(), ControlledReferenceMaterializer.LocalPath(directory, referenced)))))
            {
                // The same recursive walk requests and consumes reference evidence.
                // This includes every actual nested occurrence, even when its model's
                // active configuration differs or its document is not loaded.
                var scope = WorkingReferenceScope(file, configuration, external);
                scope.HistoricalMissingPaths = missingBaseline;
                if (missingBaseline.Length > 0)
                    scope.Key += "|omitted:" + string.Concat(missingBaseline.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => path.Length + ":" + path));
                if (!referenceProofs.TryGetValue(scope.Key, out referenceProof))
                {
                    if (pendingReferences == null)
                        return Reject("unverified-reference-scope", path, "configuration=" + configuration);
                    pendingReferences[scope.Key] = scope;
                    return false;
                }
            }
            if (manifest.UsesLatestModelReferences && file.DocumentId != manifest.RootDocumentId
                && !string.Equals(expected.Revision?.Display, file.Revision, StringComparison.OrdinalIgnoreCase))
            {
                if (!exactSnapshots.TryGetValue(file.DocumentId, out var current)) return NeedSnapshot(file, configuration);
                if (!string.Equals(current.Configuration, configuration, StringComparison.OrdinalIgnoreCase))
                    return Reject("latest-snapshot-configuration", path, "configuration=" + configuration);
                expected = current;
            }
            var expectedChildren = WorkingReuseModelChildren(expected);
            if (expectedChildren.Length == 0 && components.Length > 0)
            {
                // A parent's old occurrence snapshot can be shallow. Verify the owner's
                // exact archived version first, rather than accepting a bag of known children.
                if (!exactSnapshots.TryGetValue(file.DocumentId, out var own)) return NeedSnapshot(file, configuration);
                if (!string.Equals(own.Configuration ?? string.Empty, configuration, StringComparison.OrdinalIgnoreCase)
                    && !(ReferenceEquals(own, snapshot) && string.IsNullOrWhiteSpace(own.Configuration)))
                    return Reject("exact-snapshot-configuration", path, "instance=" + configuration + " expected=" + own.Configuration);
                expectedChildren = WorkingReuseModelChildren(own);
            }
            if (missingComponents != null)
            {
                var omitted = external.Where(component =>
                {
                    var saved = component.GetPathName();
                    return !files.ContainsKey(Path.GetFileName(saved) ?? string.Empty)
                        && CanOmitUnloadedComponent(component, saved, false) && component.GetModelDoc2() == null;
                }).ToArray();
                var missingSnapshot = expectedChildren.Where(child => child.Status == (int)CadReferenceStatus.Missing
                    && IsNativeModelReference(child.FileName) && !files.ContainsKey(child.FileName)).ToArray();
                var cleanSource = (omitted.Length > 0 || missingSnapshot.Length > 0)
                    && (file.DocumentId == committedAssemblyRootId || loadedOwner?.GetSaveFlag() != true
                        && ControlledReferenceMaterializer.Matches(path, file));
                if (!cleanSource)
                    omitted = referenceProof != null
                        ? omitted.Where(component => referenceProof.RetainedMissing.Contains(component.GetPathName()) && !File.Exists(component.GetPathName())).ToArray()
                        : Array.Empty<IComponent2>();
                if (omitted.Length > 0 || cleanSource && missingSnapshot.Length > 0)
                {
                    components = components.Where(component => !omitted.Any(item => ReferenceEquals(item, component))).ToArray();
                    var omittedNames = new HashSet<string>(omitted.Select(component => Path.GetFileName(component.GetPathName())), StringComparer.OrdinalIgnoreCase);
                    expectedChildren = expectedChildren.Where(child => !missingSnapshot.Contains(child)
                        || !cleanSource && !omittedNames.Contains(child.FileName)).ToArray();
                    missingComponents.UnionWith(omitted.Select(component => Path.GetFileName(component.GetPathName())));
                    if (cleanSource) missingComponents.UnionWith(missingSnapshot.Select(child => child.FileName));
                }
            }
            var actual = components.Select(component => new CadTreeNode
            {
                FileName = Path.GetFileName(component.GetPathName()), Configuration = component.ReferencedConfiguration,
                Status = IsEmbedded(component) ? CadReferenceStatus.Virtual : CadReferenceStatus.Normal
            }).ToArray();
            if (expected.StructureIndexPending && expected.UsesNativeFileInventory && expectedChildren.Length > 0
                && actual.Length > expectedChildren.Length
                && ReferenceEquals(expected, snapshot) && loadedOwner != null && !loadedOwner.GetSaveFlag()
                && MatchesReadOnlyAssemblyOpen(loadedOwner, path, manifest, file, configuration, nativeComponents))
            {
                var completed = CompletePartialOccurrenceIndex(expected, expectedChildren, actual, files);
                if (completed.Length > expectedChildren.Length) onIndexCompleted?.Invoke();
                expectedChildren = completed;
            }
            if (!MatchWorkingReuseChildren(expectedChildren, actual, out var pending)
                && !(editableAssemblyIds?.Contains(file.DocumentId) == true && (WorkingReuseChildrenAreSubset(expectedChildren, actual)
                    || WorkingReuseChildrenAreKnownParts(expectedChildren, actual, manifest.Files))))
            {
                // An older parent index can omit suppressed occurrences that are
                // present in the unchanged subassembly's own exact version.
                if (file.DocumentId != manifest.RootDocumentId && expected.StructureIndexPending
                    && (loadedOwner?.GetSaveFlag() != true || loadedOwner.IsOpenedReadOnly())
                    && ControlledReferenceMaterializer.Matches(path, file))
                {
                    if (!exactSnapshots.TryGetValue(file.DocumentId, out var own)) return NeedSnapshot(file, configuration);
                    if (!ReferenceEquals(expected, own) && own.DocumentId == file.DocumentId
                        && string.Equals(own.Configuration, configuration, StringComparison.OrdinalIgnoreCase)
                        && WorkingReuseModelChildren(own).Length > 0)
                        return VisitChildren(nativeComponents, own, path, file, loadedOwner, configuration, depth);
                }
                var before = expectedChildren.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == (int)CadReferenceStatus.Virtual))
                    .ToDictionary(group => group.Key, group => group.Count());
                var after = actual.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual))
                    .ToDictionary(group => group.Key, group => group.Count());
                var differences = before.Keys.Union(after.Keys)
                    .Select(key => new { Key = key, Expected = before.TryGetValue(key, out var b) ? b : 0,
                        Actual = after.TryGetValue(key, out var a) ? a : 0 })
                    .Where(row => row.Expected != row.Actual).Take(5).ToArray();
                var first = differences.First();
                var nativeChild = actual.FirstOrDefault(child => WorkingReuseReferenceKey(child.FileName, child.Configuration,
                    child.Status == CadReferenceStatus.Virtual) == first.Key);
                var storedChild = expectedChildren.FirstOrDefault(child => WorkingReuseReferenceKey(child.FileName, child.Configuration,
                    child.Status == (int)CadReferenceStatus.Virtual) == first.Key);
                return Reject("direct-components", path, "configuration=" + configuration + " expectedCount=" + before.Values.Sum()
                    + " actualCount=" + actual.Length + " differences=" + string.Join(";", differences.Select(row =>
                        row.Key + " expected=" + row.Expected + " actual=" + row.Actual)),
                    "装配配置“" + configuration + "”中，组件“" + (nativeChild?.FileName ?? storedChild.FileName)
                    + "”在配置“" + (nativeChild?.Configuration ?? storedChild?.Configuration) + "”下的实例数不同，存档 "
                    + first.Expected + " 个，当前 " + first.Actual + " 个");
            }
            if (pending)
            {
                if (file.DocumentId != committedAssemblyRootId
                    && (!ControlledReferenceMaterializer.Matches(path, file) || loadedOwner?.GetSaveFlag() == true))
                    return Reject("changed-unindexed-assembly", path, "configuration=" + configuration + " actualCount=" + components.Length);
                LogOperation("Working reuse retained pending legacy structure file=" + file.FileName + " children=" + components.Length);
            }
            var complete = true;
            var remaining = expectedChildren.ToList();
            foreach (var component in components)
            {
                if (IsEmbedded(component)) continue;
                var componentPath = component.GetPathName();
                var name = Path.GetFileName(componentPath);
                var referencedConfiguration = component.ReferencedConfiguration;
                var loaded = component.GetModelDoc2() as IModelDoc2;
                if (!files.TryGetValue(name, out var childFile))
                {
                    if (pending && loaded == null) continue; // Historical unloaded omissions are not adopted.
                    return Reject("not-in-manifest", componentPath, "parent=" + path);
                }
                var expectedPath = ControlledReferenceMaterializer.LocalPath(directory, childFile);
                if (loaded != null && !PathsEqual(loaded.GetPathName(), expectedPath))
                    return Reject("loaded-path", loaded.GetPathName(), "expected=" + expectedPath + " instance=" + referencedConfiguration);
                var child = remaining.FirstOrDefault(candidate => string.Equals(candidate.FileName, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase));
                var aliases = referenceProof?.DeferredReferences;
                if (loaded == null && !PathsEqual(componentPath, expectedPath) && aliases != null && aliases.ContainsValue(childFile.DocumentId))
                {
                    if (!aliases.TryGetValue(componentPath, out var childId) || childId != childFile.DocumentId
                        || File.Exists(componentPath) || child?.DocumentId != childFile.DocumentId
                        || !UsesDeferredComponentPath((swComponentSuppressionState_e)component.GetSuppression())
                        || !WorkingReuseBindingMatches(expectedPath, manifest.ProjectId, childFile)
                        || !ControlledReferenceMaterializer.Matches(expectedPath, childFile))
                        return Reject("deferred-archived-reference", componentPath, "expected=" + expectedPath + " instance=" + referencedConfiguration);
                }
                if (child != null) remaining.Remove(child);
                else if (pending)
                {
                    child = snapshots[name].FirstOrDefault(candidate => candidate.DocumentId == childFile.DocumentId
                        && string.Equals(candidate.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase));
                    if (child == null && exactSnapshots.TryGetValue(childFile.DocumentId, out var own)
                        && string.Equals(own.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase)) child = own;
                    if (child == null) { complete = NeedSnapshot(childFile, referencedConfiguration) && complete; continue; }
                }
                else if (editableAssemblyIds?.Contains(file.DocumentId) == true && name.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(childFile.Configuration, referencedConfiguration, StringComparison.OrdinalIgnoreCase)
                        || snapshots[name].Any(item => item.DocumentId == childFile.DocumentId
                            && string.Equals(item.Configuration, referencedConfiguration, StringComparison.OrdinalIgnoreCase))))
                    child = new DocumentReferenceNodeDto { DocumentId = childFile.DocumentId, FileName = childFile.FileName,
                        Kind = (int)CadDocumentKind.Part, Configuration = referencedConfiguration, Revision = new RevisionDto { Display = childFile.Revision } };
                if (child == null || child.DocumentId.HasValue && child.DocumentId.Value != childFile.DocumentId)
                    return Reject("instance-identity", expectedPath, "configuration=" + referencedConfiguration);
                if (!name.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)) continue;
                var children = component.GetChildren() as Array;
                // Suppressed subassemblies have no loaded child scope. Their saved
                // configuration and immutable file baseline were verified above.
                if ((children == null || children.Length == 0) && component.GetSuppression() == 0) continue;
                if (children == null) return Reject("unreadable-instance-children", expectedPath, "configuration=" + referencedConfiguration);
                var childComponents = children.OfType<IComponent2>().ToArray();
                if (childComponents.Length != children.Length) return Reject("invalid-instance-children", expectedPath, "configuration=" + referencedConfiguration);
                if (!VisitChildren(childComponents, child, expectedPath, childFile, loaded, referencedConfiguration, depth + 1)) complete = false;
            }
            return complete;
        }
        var rootPath = root.GetPathName();
        if (!files.TryGetValue(Path.GetFileName(rootPath), out var rootFile)) return Reject("not-in-manifest", rootPath, "root=True");
        var expectedRootPath = ControlledReferenceMaterializer.LocalPath(directory, rootFile);
        if (!PathsEqual(rootPath, expectedRootPath)) return Reject("loaded-path", rootPath, "expected=" + expectedRootPath);
        var rootConfiguration = root.ConfigurationManager?.ActiveConfiguration?.Name;
        if (string.IsNullOrWhiteSpace(rootConfiguration)) return Reject("missing-active-configuration", rootPath, "root=True");
        // An independently opened part uses its own exact snapshot configuration;
        // the containing assembly's file inventory records an occurrence instead.
        if (!(standalonePart && root.GetType() == 1) && !string.IsNullOrWhiteSpace(rootFile.Configuration)
            && !string.Equals(rootFile.Configuration, rootConfiguration, StringComparison.OrdinalIgnoreCase))
            return Reject("root-manifest-configuration", rootPath, "actual=" + rootConfiguration + " expected=" + rootFile.Configuration);
        if (!string.IsNullOrWhiteSpace(snapshot.Configuration) && !string.Equals(snapshot.Configuration, rootConfiguration, StringComparison.OrdinalIgnoreCase))
            return Reject("snapshot-configuration", rootPath, "actual=" + rootConfiguration + " expected=" + snapshot.Configuration);
        if (!(root is IAssemblyDoc assembly)) return root.GetType() != 2 || Reject("assembly-interface", rootPath, "configuration=" + rootConfiguration);
        var values = assembly.GetComponents(true) as Array;
        if (values == null) return Reject("unreadable-direct-components", rootPath, "configuration=" + rootConfiguration);
        var rootComponents = values.OfType<IComponent2>().ToArray();
        if (rootComponents.Length != values.Length) return Reject("invalid-direct-components", rootPath, "configuration=" + rootConfiguration);
        return VisitChildren(rootComponents, snapshot, rootPath, rootFile, root, rootConfiguration, 0);
    }

    private static IEnumerable<DocumentReferenceNodeDto> WorkingReuseSnapshotNodes(DocumentReferenceNodeDto node)
    {
        if (node == null) yield break;
        yield return node;
        foreach (var child in node.Children ?? new List<DocumentReferenceNodeDto>())
            foreach (var item in WorkingReuseSnapshotNodes(child)) yield return item;
    }

    private static DocumentReferenceNodeDto[] WorkingReuseModelChildren(DocumentReferenceNodeDto expected) =>
        (expected.Children ?? new List<DocumentReferenceNodeDto>())
            .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency).ToArray();

    // A partial archive index can lack occurrences. Keep every indexed occurrence
    // and fill only the additional controlled instances observed on the verified cold open.
    private static DocumentReferenceNodeDto[] CompletePartialOccurrenceIndex(DocumentReferenceNodeDto snapshot, DocumentReferenceNodeDto[] indexed,
        IReadOnlyList<CadTreeNode> observed, IReadOnlyDictionary<string, ControlledOpenFileDto> files)
    {
        var counts = indexed.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration,
            child.Status == (int)CadReferenceStatus.Virtual)).ToDictionary(group => group.Key, group => group.Count());
        var completed = indexed.ToList();
        foreach (var instance in observed)
        {
            var key = WorkingReuseReferenceKey(instance.FileName, instance.Configuration, instance.Status == CadReferenceStatus.Virtual);
            if (counts.TryGetValue(key, out var remaining) && remaining > 0)
            {
                counts[key] = remaining - 1;
                continue;
            }
            if (instance.Status == CadReferenceStatus.Virtual || string.IsNullOrWhiteSpace(instance.Configuration)
                || !files.TryGetValue(instance.FileName, out var childFile) || childFile.DocumentId == Guid.Empty
                || childFile.VersionId == Guid.Empty || !IsNativeModelReference(childFile.FileName)
                || !snapshot.Children.Any(entry => entry.Role == (int)CadReferenceRole.ExternalFileDependency
                    && entry.DocumentId == childFile.DocumentId && entry.Kind == (int)DocumentKindFromPath(childFile.FileName)
                    && string.Equals(entry.FileName, childFile.FileName, StringComparison.OrdinalIgnoreCase))) return indexed;
            completed.Add(new DocumentReferenceNodeDto { DocumentId = childFile.DocumentId, FileName = childFile.FileName,
                Kind = (int)DocumentKindFromPath(childFile.FileName), Configuration = instance.Configuration,
                Revision = new RevisionDto { Display = childFile.Revision } });
        }
        return counts.Values.All(count => count == 0) ? completed.ToArray() : indexed;
    }

    private static bool WorkingReuseChildrenMatch(DocumentReferenceNodeDto expected, IReadOnlyList<CadTreeNode> actual, out bool pending)
        => MatchWorkingReuseChildren(WorkingReuseModelChildren(expected), actual, out pending);

    private static bool MatchWorkingReuseChildren(DocumentReferenceNodeDto[] children, IReadOnlyList<CadTreeNode> actual, out bool pending)
    {
        pending = children.Length == 0 && actual.Count > 0;
        if (pending) return true;
        var before = children.Select(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == (int)CadReferenceStatus.Virtual)).OrderBy(value => value, StringComparer.Ordinal);
        var after = actual.Select(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual)).OrderBy(value => value, StringComparer.Ordinal);
        return before.SequenceEqual(after);
    }

    private static string WorkingReuseReferenceKey(string name, string configuration, bool embedded) => (name ?? string.Empty).ToUpperInvariant()
        + "|" + (configuration ?? string.Empty).ToUpperInvariant() + "|" + embedded;

    private static bool WorkingReuseChildrenAreSubset(IEnumerable<DocumentReferenceNodeDto> expected, IEnumerable<CadTreeNode> actual)
    {
        var remaining = expected.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == (int)CadReferenceStatus.Virtual))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var child in actual)
        {
            var key = WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual);
            if (!remaining.TryGetValue(key, out var count) || count == 0) return false;
            remaining[key] = count - 1;
        }
        return true;
    }

    private ControlledOpenManifestDto ReconcileCompletedWorkingArchives(string directory, ControlledOpenManifestDto previous, ControlledOpenManifestDto latest)
    {
        if (previous.ProjectId != latest.ProjectId || previous.RootDocumentId != latest.RootDocumentId
            || previous.RootVersionId != latest.RootVersionId || !PathsEqual(previous.RootRelativePath, latest.RootRelativePath)
            || previous.Files.Any(file => !latest.Files.Any(current => current.DocumentId == file.DocumentId))
            || latest.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)) return previous;
        var files = new List<ControlledOpenFileDto>();
        var changed = false;
        foreach (var file in latest.Files)
        {
            var before = previous.Files.FirstOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            if (before != null && before.VersionId == file.VersionId
                && string.Equals(before.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(before.RelativePath ?? before.FileName, file.RelativePath ?? file.FileName, StringComparison.OrdinalIgnoreCase)
                && WorkingConfigurationMatches(before, file))
            { files.Add(before); continue; }
            var path = ControlledReferenceMaterializer.LocalPath(directory, file);
            if (!completedWorkingArchives.TryGetValue(path, out var receipt) || receipt.ProjectId != latest.ProjectId
                || receipt.DocumentId != file.DocumentId || receipt.VersionId != file.VersionId
                || !string.Equals(receipt.Username, authenticatedUsername, StringComparison.OrdinalIgnoreCase) || receipt.ServerScope != apiClient.EditJournalScope
                || !string.Equals(receipt.StoredSha256, file.Sha256, StringComparison.OrdinalIgnoreCase) || receipt.StoredLength != file.FileLength
                || before != null && (!string.Equals(before.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(before.RelativePath ?? before.FileName, file.RelativePath ?? file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !WorkingConfigurationMatches(before, file))
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) || binding.ProjectId != receipt.ProjectId
                || binding.DocumentId != receipt.DocumentId || binding.VersionId != receipt.VersionId
                || binding.FileLength != receipt.SourceLength || !string.Equals(binding.Sha256, receipt.SourceSha256, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path) || new FileInfo(path).Length != receipt.SourceLength
                || !string.Equals(ComputeFileHash(path), receipt.SourceSha256, StringComparison.OrdinalIgnoreCase)) return previous;
            files.Add(new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = file.VersionId, FileName = file.FileName,
                RelativePath = file.RelativePath, Revision = file.Revision, Configuration = file.Configuration, IsRoot = file.IsRoot,
                Sha256 = receipt.SourceSha256, FileLength = receipt.SourceLength });
            changed = true;
        }
        if (!changed) return previous;
        LogOperation("按本会话已完成存档回执补齐工作清单 files=" + files.Count + " root=" + previous.RootDocumentId);
        return new ControlledOpenManifestDto { Id = latest.Id, ProjectId = previous.ProjectId, ProjectCode = previous.ProjectCode,
            RootDocumentId = previous.RootDocumentId, RootVersionId = previous.RootVersionId, RootRevision = previous.RootRevision,
            RootRelativePath = previous.RootRelativePath, ForEdit = true, Files = files,
            ReferenceIssues = OpenReferenceFindings.Merge(latest.ReferenceIssues), Warnings = latest.Warnings,
            UsesLatestModelReferences = latest.UsesLatestModelReferences };
    }

    private static string WorkingReuseManifestDifferences(ControlledOpenManifestDto previous, ControlledOpenManifestDto latest)
    {
        var differences = latest.Files.Where(file => !previous.Files.Any(before => before.DocumentId == file.DocumentId
            && before.VersionId == file.VersionId && before.FileName == file.FileName
            && (before.RelativePath ?? before.FileName) == (file.RelativePath ?? file.FileName)
            && before.Configuration == file.Configuration && WorkingReuseFileBytesMatch(before, file)))
            .Select(file => file.FileName + (previous.Files.Any(before => before.DocumentId == file.DocumentId) ? "（版本、内容或配置需核对）" : "（新增关联文件尚未核对）"))
            .Concat(previous.Files.Where(file => !latest.Files.Any(current => current.DocumentId == file.DocumentId)).Select(file => file.FileName + "（已不在最新清单中）"));
        var files = differences.Take(6).ToArray();
        return files.Length == 0 ? Path.GetFileName(previous.RootRelativePath) + "（总装基准版本需核对）" : string.Join("、", files);
    }

    private sealed class CompletedWorkingArchive
    {
        public Guid ProjectId, DocumentId, VersionId;
        public string StoredSha256, SourceSha256, Username, ServerScope;
        public long StoredLength, SourceLength;
    }
}
