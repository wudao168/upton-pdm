using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Dictionary<string, CompletedWorkingArchive> completedWorkingArchives = new Dictionary<string, CompletedWorkingArchive>(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> TryAcquireReusableWorkingCopyAsync(IReadOnlyList<BatchOperationItem> selected,
        Guid projectId, Guid rootId)
    {
        var rootPath = currentTree?.FullPath;
        if (string.IsNullOrWhiteSpace(rootPath) || IsReadOnlyPreviewPath(rootPath)
            || IsHistoricalDerivedPath(rootPath) || !PdmDocumentIdentityStore.IsControlledWorkspacePath(rootPath)
            || rootPath.IndexOf(Path.DirectorySeparatorChar + "View" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0) return false;
        // A part opened from an assembly retains the assembly's manifest. Resolve
        // by its exact physical path before deciding that a new workspace is needed.
        var context = controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId
            && pair.Value.Files.Any(file => file.DocumentId == rootId
                && PathsEqual(rootPath, Path.Combine(pair.Key, file.RelativePath ?? file.FileName)))
            && selected.All(item => pair.Value.Files.Any(file => file.DocumentId == item.Node.DocumentId
                && PathsEqual(item.Node.FullPath, Path.Combine(pair.Key, file.RelativePath ?? file.FileName)))))
            .OrderByDescending(pair => pair.Value.RootDocumentId == rootId).FirstOrDefault();
        if (context.Value == null) return false;
        rootId = context.Value.RootDocumentId;
        rootPath = Path.Combine(context.Key, context.Value.RootRelativePath ?? string.Empty);
        var rootModel = FindLoadedDocument(rootPath);
        if (rootModel == null) return false;
        if (!EnsureEditJournal())
            throw new InvalidOperationException("当前编辑恢复记录尚未准备完成，已保留窗口，请重新登录后重试获取权限。");

        await apiClient.RefreshDocumentRulesAsync(lifetime.Token);
        var latest = await apiClient.CreateControlledOpenManifestAsync(rootId, null, false, true, lifetime.Token);
        var workingManifest = ReconcileCompletedWorkingArchives(context.Key, context.Value, latest);
        var documents = (await apiClient.GetDocumentsAsync(projectId, lifetime.Token)).ToDictionary(document => document.Id);
        var loadedDocuments = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(document => new { Document = document, Path = document.GetPathName() }).ToArray();
        var localChanges = false;
        var hasUnsavedLoadedDocument = false;
        var editableAssemblyIds = new HashSet<Guid>();
        foreach (var file in workingManifest.Files)
        {
            var path = ControlledReferenceMaterializer.LocalPath(context.Key, file);
            if (!WorkingReuseBindingMatches(path, context.Value.ProjectId, file))
                throw new InvalidOperationException("当前工作副本的版本关联需要核对，已保留窗口和修改，未重新取回：" + file.FileName);
            var loaded = loadedDocuments.FirstOrDefault(document => PathsEqual(document.Path, path))?.Document;
            var changed = !ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, file, out var actualHash);
            if (!changed && string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(path).Length != file.FileLength)
                throw new InvalidOperationException("当前工作副本的基准长度需要核对，已保留窗口和文件：" + file.FileName);
            var unsaved = loaded?.GetSaveFlag() == true;
            hasUnsavedLoadedDocument |= unsaved;
            var unsavedEdit = unsaved && !loaded.IsOpenedReadOnly();
            documents.TryGetValue(file.DocumentId, out var document);
            localEdits.TryGetValue(file.DocumentId, out var draft);
            var verifiedDraft = WorkingReuseDraftMatches(path, file, document, draft, checkoutSessionId, authenticatedUsername);
            var selectedAssemblyEdit = !changed && unsaved && loaded is IAssemblyDoc
                && selected.Any(item => item.Node.DocumentId == file.DocumentId && item.Node.Kind == CadDocumentKind.Assembly)
                && document != null && string.IsNullOrWhiteSpace(document.CheckedOutBy) && !document.CheckoutSessionId.HasValue;
            if (selectedAssemblyEdit) editableAssemblyIds.Add(file.DocumentId);
            if (draft != null && !draft.Closed && !verifiedDraft)
                throw new InvalidOperationException("当前工作副本的编辑恢复记录与路径或版本不一致，已保留原记录和窗口，请核对后再获取权限：" + file.FileName);
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
        if (!await WorkingReuseStoredSourceManifestsMatchAsync(workingManifest, latest))
            return CannotReuse("工作基准与服务器不一致：" + WorkingReuseManifestDifferences(workingManifest, latest));
        var paths = latest.Files.ToDictionary(file => file.DocumentId,
            file => ControlledReferenceMaterializer.LocalPath(context.Key, file));
        if (selected.Any(item => !item.Node.DocumentId.HasValue || !paths.TryGetValue(item.Node.DocumentId.Value, out var path)
            || !PathsEqual(path, item.Node.FullPath))) return CannotReuse("所选图档不在当前受控工作副本中");
        var requestedIds = new HashSet<Guid>(selected.Select(item => item.Node.DocumentId.Value));
        var relations = await apiClient.GetDocumentRelationsAsync(projectId, lifetime.Token);
        if (relations.Any(relation => requestedIds.Contains(relation.ModelDocumentId) && !paths.ContainsKey(relation.DrawingDocumentId)))
            return CannotReuse("配套工程图尚未在当前工作副本中准备完整");
        var rootVersion = (await apiClient.GetVersionsAsync(rootId, lifetime.Token)).FirstOrDefault(version => version.Id == latest.RootVersionId);
        if (!WorkingReuseSnapshotVersionMatches(latest.Files.Single(file => file.DocumentId == rootId), rootVersion))
            return CannotReuse("当前版本缺少可核对的引用快照");
        try
        {
            var exactSnapshots = new Dictionary<Guid, DocumentReferenceNodeDto> { [rootId] = rootVersion.ReferenceSnapshot };
            while (true)
            {
                var missing = new HashSet<Guid>();
                string nativeConflict = null;
                if (WorkingReuseLoadedContextForEditMatches(rootModel, context.Key, workingManifest, rootVersion.ReferenceSnapshot, exactSnapshots, missing, editableAssemblyIds,
                    conflict => nativeConflict = conflict)) break;
                if (missing.Count == 0) return CannotReuse("实际加载的引用或配置与当前基准不一致：" + nativeConflict);
                foreach (var id in missing)
                {
                    var file = latest.Files.Single(candidate => candidate.DocumentId == id);
                    var exact = (await apiClient.GetVersionsAsync(id, lifetime.Token)).FirstOrDefault(version => version.Id == file.VersionId);
                    if (!WorkingReuseSnapshotVersionMatches(file, exact))
                        return CannotReuse("引用图档的精确版本快照无法核对：" + file.FileName);
                    exactSnapshots[id] = exact.ReferenceSnapshot;
                    LogOperation("Working reuse exact snapshot file=" + file.FileName + " version=" + file.VersionId);
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            LogDiagnostic("获取权限前核对当前工作副本", error);
            throw new InvalidOperationException("暂未完成当前装配引用核对，已保留窗口和修改，请稍后重试。");
        }

        // Only requested documents gain permissions. Existing drafts, unrelated file
        // attributes and the loaded root remain untouched; never retire this context.
        foreach (var item in selected.GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First()))
        {
            var id = item.Node.DocumentId.Value;
            documents.TryGetValue(id, out var previousDocument);
            var newCheckout = previousDocument != null && string.IsNullOrWhiteSpace(previousDocument.CheckedOutBy);
            plmAuthoritativeCheckouts.Add(id);
            DocumentDto document;
            try { document = await apiClient.CheckoutAsync(id, checkoutSessionId, checkoutMachineName, lifetime.Token); }
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
                if (newCheckout && FindLoadedDocument(paths[id])?.GetSaveFlag() != true && ControlledReferenceMaterializer.Matches(paths[id], file))
                    try
                    {
                        var released = await apiClient.DiscardCheckoutAsync(id, checkoutSessionId, lifetime.Token);
                        ApplyCheckoutDocumentToMatchingInstances(matching, released);
                    }
                    catch (Exception error) { LogDiagnostic("获取权限后恢复记录未完成，新增权限尚未释放", error); }
                throw new InvalidOperationException("编辑恢复记录未能安全保存，已停止解锁并保留当前窗口和文件，请检查诊断日志后重试：" + item.Node.FileName);
            }
        }
        // Checkout serializes changes to each requested document. Recheck after it so
        // a version published during the first manifest request is never unlocked stale.
        var acquired = await apiClient.CreateControlledOpenManifestAsync(rootId, null, false, true, lifetime.Token);
        if (!WorkingReuseManifestsMatch(latest, acquired))
            throw new InvalidOperationException("获取权限期间服务器版本发生变化：" + WorkingReuseManifestDifferences(latest, acquired)
                + "。已保留当前窗口、文件和已获取权限，请核对后继续。");
        controlledOpenManifests[context.Key] = workingManifest;
        foreach (var item in selected.GroupBy(item => item.Node.DocumentId.Value).Select(group => group.First()))
        {
            var id = item.Node.DocumentId.Value;
            SetFileReadOnly(paths[id], false);
            EnsureLoadedDocumentEditable(paths[id]);
        }
        await AcquireLinkedDrawingPermissionsAsync(projectId, selected.Select(item => item.Node.DocumentId.Value).Distinct(), context.Key, lifetime.Token);
        LogOperation("Working reuse completed root=" + rootPath + " selected=" + selected.Select(item => item.Node.DocumentId).Distinct().Count()
            + " files=" + latest.Files.Count + " reopened=0 replaced=0");
        return true;
    }

    private static bool WorkingReuseManifestsMatch(ControlledOpenManifestDto previous, ControlledOpenManifestDto current) =>
        WorkingReuseManifestsMatchCore(previous, current, WorkingReuseFileBytesMatch);

    private static bool WorkingReuseFileBytesMatch(ControlledOpenFileDto before, ControlledOpenFileDto current) =>
        before.FileLength == current.FileLength && string.Equals(before.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase);

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
            || previous.RootVersionId != current.RootVersionId || !PathsEqual(previous.RootRelativePath, current.RootRelativePath)
            || previous.Files.Count != current.Files.Count || previous.Files.Count == 0
            || previous.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)
            || current.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)) return false;
        var byId = previous.Files.ToDictionary(file => file.DocumentId);
        return current.Files.All(file => byId.TryGetValue(file.DocumentId, out var before)
            && file.VersionId == before.VersionId && bytesMatch(before, file)
            && string.Equals(file.FileName, before.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.RelativePath ?? file.FileName, before.RelativePath ?? before.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.Configuration ?? string.Empty, before.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase));
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
        IReadOnlyDictionary<Guid, DocumentReferenceNodeDto> exactSnapshots, ISet<Guid> missingSnapshots, ISet<Guid> editableAssemblyIds, Action<string> reportConflict = null)
    {
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var snapshots = WorkingReuseSnapshotNodes(snapshot).ToLookup(node => node.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        bool Reject(string reason, string path, string detail)
        {
            LogOperation("Working reuse native rejected reason=" + reason + " path=" + path + " " + detail);
            reportConflict?.Invoke(Path.GetFileName(path) + "（实际来源、配置或组件结构需核对）");
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
            var expectedChildren = (expected.Children ?? new List<DocumentReferenceNodeDto>())
                .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency).ToArray();
            if (expectedChildren.Length == 0 && components.Length > 0)
            {
                // A parent's old occurrence snapshot can be shallow. Verify the owner's
                // exact archived version first, rather than accepting a bag of known children.
                if (!exactSnapshots.TryGetValue(file.DocumentId, out var own)) return NeedSnapshot(file, configuration);
                if (!string.Equals(own.Configuration ?? string.Empty, configuration, StringComparison.OrdinalIgnoreCase)
                    && !(ReferenceEquals(own, snapshot) && string.IsNullOrWhiteSpace(own.Configuration)))
                    return Reject("exact-snapshot-configuration", path, "instance=" + configuration + " expected=" + own.Configuration);
                expected = own;
                expectedChildren = (own.Children ?? new List<DocumentReferenceNodeDto>())
                    .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency).ToArray();
            }
            var actual = components.Select(component => new CadTreeNode
            {
                FileName = Path.GetFileName(component.GetPathName()), Configuration = component.ReferencedConfiguration,
                Status = component.IsVirtual ? CadReferenceStatus.Virtual : CadReferenceStatus.Normal
            }).ToArray();
            if (!WorkingReuseChildrenMatch(expected, actual, out var pending)
                && !(editableAssemblyIds?.Contains(file.DocumentId) == true && WorkingReuseChildrenAreSubset(expectedChildren, actual)))
            {
                var before = expectedChildren.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == (int)CadReferenceStatus.Virtual))
                    .ToDictionary(group => group.Key, group => group.Count());
                var after = actual.GroupBy(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual))
                    .ToDictionary(group => group.Key, group => group.Count());
                var differences = before.Keys.Union(after.Keys).Where(key => (before.TryGetValue(key, out var b) ? b : 0)
                    != (after.TryGetValue(key, out var a) ? a : 0)).Take(5)
                    .Select(key => key + " expected=" + (before.TryGetValue(key, out var b) ? b : 0) + " actual=" + (after.TryGetValue(key, out var a) ? a : 0));
                return Reject("direct-components", path, "configuration=" + configuration + " expectedCount=" + before.Values.Sum()
                    + " actualCount=" + actual.Length + " differences=" + string.Join(";", differences));
            }
            if (pending)
            {
                if (!ControlledReferenceMaterializer.Matches(path, file) || loadedOwner?.GetSaveFlag() == true)
                    return Reject("changed-unindexed-assembly", path, "configuration=" + configuration + " actualCount=" + components.Length);
                LogOperation("Working reuse retained pending legacy structure file=" + file.FileName + " children=" + components.Length);
            }
            var complete = true;
            var remaining = expectedChildren.ToList();
            foreach (var component in components)
            {
                if (component.IsVirtual) continue;
                var componentPath = component.GetPathName();
                var name = Path.GetFileName(componentPath);
                var referencedConfiguration = component.ReferencedConfiguration;
                var loaded = component.GetModelDoc2() as IModelDoc2;
                if (!files.TryGetValue(name, out var childFile))
                {
                    if (pending) continue; // Historical external omissions are not adopted or given permissions.
                    return Reject("not-in-manifest", componentPath, "parent=" + path);
                }
                var expectedPath = ControlledReferenceMaterializer.LocalPath(directory, childFile);
                if (loaded != null && !PathsEqual(loaded.GetPathName(), expectedPath))
                    return Reject("loaded-path", loaded.GetPathName(), "expected=" + expectedPath + " instance=" + referencedConfiguration);
                var child = remaining.FirstOrDefault(candidate => string.Equals(candidate.FileName, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase));
                if (child != null) remaining.Remove(child);
                else if (pending)
                {
                    child = snapshots[name].FirstOrDefault(candidate => candidate.DocumentId == childFile.DocumentId
                        && string.Equals(candidate.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase));
                    if (child == null && exactSnapshots.TryGetValue(childFile.DocumentId, out var own)
                        && string.Equals(own.Configuration ?? string.Empty, referencedConfiguration, StringComparison.OrdinalIgnoreCase)) child = own;
                    if (child == null) { complete = NeedSnapshot(childFile, referencedConfiguration) && complete; continue; }
                }
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
        if (!string.IsNullOrWhiteSpace(rootFile.Configuration) && !string.Equals(rootFile.Configuration, rootConfiguration, StringComparison.OrdinalIgnoreCase))
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

    private static bool WorkingReuseChildrenMatch(DocumentReferenceNodeDto expected, IReadOnlyList<CadTreeNode> actual, out bool pending)
    {
        var children = (expected.Children ?? new List<DocumentReferenceNodeDto>())
            .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency).ToArray();
        pending = children.Length == 0 && actual.Count > 0;
        if (pending) return true;
        var before = children.Select(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == (int)CadReferenceStatus.Virtual)).OrderBy(value => value);
        var after = actual.Select(child => WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual)).OrderBy(value => value);
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
                && string.Equals(before.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            { files.Add(before); continue; }
            var path = ControlledReferenceMaterializer.LocalPath(directory, file);
            if (!completedWorkingArchives.TryGetValue(path, out var receipt) || receipt.ProjectId != latest.ProjectId
                || receipt.DocumentId != file.DocumentId || receipt.VersionId != file.VersionId
                || !string.Equals(receipt.Username, authenticatedUsername, StringComparison.OrdinalIgnoreCase) || receipt.ServerScope != apiClient.EditJournalScope
                || !string.Equals(receipt.StoredSha256, file.Sha256, StringComparison.OrdinalIgnoreCase) || receipt.StoredLength != file.FileLength
                || before != null && (!string.Equals(before.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(before.RelativePath ?? before.FileName, file.RelativePath ?? file.FileName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(before.Configuration ?? string.Empty, file.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
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
            RootRelativePath = previous.RootRelativePath, ForEdit = true, Files = files };
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
