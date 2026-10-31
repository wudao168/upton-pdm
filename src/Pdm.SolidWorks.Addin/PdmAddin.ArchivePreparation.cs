using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly HashSet<string> preloadedArchiveDrawings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> preloadedOwnedDrawings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> archiveWarnings = new HashSet<string>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, string> ReadArchiveProperties(IModelDoc2 document, CadTreeNode node,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> cards,
        IReadOnlyDictionary<string, IReadOnlyList<string>> configurations = null)
    {
        // A confirmed missing native configuration blocks; a temporarily unreadable
        // configuration index remains pending without discarding the native file.
        if (node.Kind != CadDocumentKind.Drawing && configurations != null
            && configurations.TryGetValue(node.FullPath, out var referenced))
            foreach (var configuration in referenced.Where(value => !string.Equals(value, node.Configuration, StringComparison.OrdinalIgnoreCase)))
                if (string.IsNullOrWhiteSpace(configuration) || document.GetConfigurationByName(configuration) == null)
                {
                    string[] available = null;
                    try { available = (document.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray(); }
                    catch (Exception error) { LogDiagnostic("配置目录暂未读取：" + node.FileName, error); }
                    if (!string.IsNullOrWhiteSpace(configuration) && available?.Length > 0
                        && !available.Contains(configuration, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("文件“" + node.FileName + "”确实不存在引用配置“" + configuration + "”，请补齐配置或修正引用后再存档。本地文件和修改均已保留。");
                    node.StructureIndexPending = true;
                    archiveWarnings.Add("文件“" + node.FileName + "”的引用配置“" + configuration + "”待核对；原生文件继续存档，未自动替换配置，补全前不能正式发布。");
                }
        try
        {
            return MergeReferencedConfigurationProperties(document, node,
                ReadCheckInProperties(document, node, useCached: true, nativePropertyCards: cards, requireComplete: false), configurations);
        }
        catch (Exception error)
        {
            LogDiagnostic("存档属性索引暂未完整读取，原生文件仍完整保存：" + node.FullPath, error);
            archiveWarnings.Add(node.FileName + "：图档内容已保存，部分属性索引待补齐，可稍后刷新或维护BOM，无需重复存档。");
            var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (metadataCacheSync)
                if (documentVersionsCache.TryGetValue(node.DocumentId ?? Guid.Empty, out var cached)
                    && cached.Versions.FirstOrDefault()?.PropertySnapshot != null)
                    foreach (var pair in cached.Versions.First().PropertySnapshot) fallback[pair.Key] = pair.Value;
            try
            {
                foreach (var pair in ReadCheckInProperties(document, node, useCached: true, nativePropertyCards: cards))
                    fallback[pair.Key] = pair.Value;
            }
            catch (Exception partialError) { LogDiagnostic("存档属性索引保留已有值：" + node.FullPath, partialError); }
            return fallback;
        }
    }

    private Task PrepareArchiveSnapshotsAsync(IEnumerable<PreparedArchiveFile> files,
        ICollection<string> plannedPaths, CancellationToken token)
        => PrepareArchiveSnapshotsCoreAsync(files, plannedPaths, token, null);

    private async Task PrepareArchiveSnapshotsCoreAsync(IEnumerable<PreparedArchiveFile> files,
        ICollection<string> plannedPaths, CancellationToken token, Dictionary<Guid, IReadOnlyList<DocumentVersionDto>> histories)
    {
        var batch = files.ToArray();
        ArchiveSession.Current?.SetStep(7, "正在固定本次引用快照与版本基准…", batch.Length);
        var failures = new List<string>();
        // Freeze the whole confirmed batch without yielding. Freezing one file and then
        // awaiting its base version lets a metadata refresh invalidate the next file.
        foreach (var file in batch)
        {
            token.ThrowIfCancellationRequested();
            ArchiveSession.Current?.ReportFile(0, 0, file.Node.FileName, "正在固定本次存档的引用和文件校验记录…");
            try
            {
                file.ReferenceSnapshot = PdmApiClient.CaptureArchiveSnapshot(file.Node, plannedPaths);
                if (file.NativeIsolationVerified) file.ReferenceSnapshot = file.ReferenceSnapshot.WithNativeIsolationVerified();
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                LogDiagnostic("Archive snapshot capture. file=" + file.Node.FileName, exception);
                failures.Add(file.Node.FileName + "：" + ArchivePreflightError(exception));
            }
        }
        ThrowArchivePreflightFailures(failures);
        ArchiveSession.Current?.ReportFile(0, 0, "—", "正在按服务器规则核对最终整批引用关系…");
        var frozenBatch = batch.ToDictionary(file => file.Node.DocumentId.Value, file => file.ReferenceSnapshot);
        await Task.Run(() => FrozenArchiveSubmissionGraph.Validate(frozenBatch), token);
        if (histories != null) await apiClient.RefreshArchiveHistoriesAsync(histories, token);
        var fixedSnapshots = 0;
        foreach (var file in batch)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                file.ReferenceSnapshot = histories == null
                    ? await apiClient.PrepareArchiveSnapshotAsync(file.Node, plannedPaths, HistoricalArchiveBase(file.Node), token, file.ReferenceSnapshot)
                    : await apiClient.PrepareArchiveSnapshotWithHistoryAsync(file.Node, HistoricalArchiveBase(file.Node),
                        file.ReferenceSnapshot, histories[file.Node.DocumentId.Value], token);
                ArchiveSession.Current?.ReportFile(0, batch.Length, file.Node.FileName, "已固定引用快照 " + (++fixedSnapshots) + "/" + batch.Length + "，准备最终校验");
                LogOperation("Archive frozen reference snapshot prepared file=" + file.Node.FileName
                    + " base=" + file.ReferenceSnapshot.ExpectedVersionId
                    + " planned=" + file.ReferenceSnapshot.PlannedDocumentIds.Count);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                LogDiagnostic("Archive snapshot base. file=" + file.Node.FileName, exception);
                ThrowOnArchiveConnectionFailure(exception);
                failures.Add(file.Node.FileName + "：" + ArchivePreflightError(exception));
            }
        }
        ThrowArchivePreflightFailures(failures);
    }

    private static IReadOnlyDictionary<string, string> MergeReferencedConfigurationProperties(
        IModelDoc2 document, CadTreeNode node, IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, IReadOnlyList<string>> referencedConfigurations)
    {
        if (node.Kind == CadDocumentKind.Drawing || referencedConfigurations == null
            || !referencedConfigurations.TryGetValue(node.FullPath, out var configurations)) return primary;
        var merged = primary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var configuration in configurations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(configuration => !string.Equals(configuration, node.Configuration, StringComparison.OrdinalIgnoreCase)))
        {
            var prefix = "配置:" + configuration + "/";
            foreach (var entry in ReadModelProperties(document, configuration, useCached: true, requireComplete: true)
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                merged[entry.Key] = entry.Value;
        }
        return merged;
    }

    private static IReadOnlyDictionary<string, string> PreserveInspectedConfigurationProperties(
        CadTreeNode node, IReadOnlyDictionary<string, string> prepared, IReadOnlyDictionary<string, string> inspected)
    {
        if (node.Kind == CadDocumentKind.Drawing || inspected == null) return prepared;
        var primary = "配置:" + node.Configuration + "/";
        var merged = prepared.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inspected.Where(entry => entry.Key.StartsWith("配置:", StringComparison.OrdinalIgnoreCase)
            && !entry.Key.StartsWith(primary, StringComparison.OrdinalIgnoreCase))) merged[entry.Key] = entry.Value;
        return merged;
    }

    private async Task<BatchCheckInPlan> TryBuildUnchangedArchivePlanAsync(
        IReadOnlyList<BatchOperationItem> selected, Action<string> report, CancellationToken token)
    {
        var items = DistinctBatchItems(selected);
        var nodes = items.SelectMany(item => EnumerateArchiveNodes(item.Node)).Distinct().ToArray();
        bool Clean(CadTreeNode node) => !node.LocalStructureDeferred && node.DocumentId.HasValue && node.StoredVersionStateKnown && node.HasStoredVersion
            && string.IsNullOrWhiteSpace(node.CheckedOutBy) && !node.CheckoutSessionId.HasValue && !node.CheckoutSessionLost
            && !node.IsRenamePendingSave && !node.IsModifiedInSolidWorks && node.WorkState == CadWorkState.None
            && !node.DrawingReviewLocked && !node.IsReadOnlyPreview && !node.HasBlockingIssue
            && !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath)
            && FindLoadedDocument(node.FullPath)?.GetSaveFlag() != true;
        token.ThrowIfCancellationRequested();
        if (items.Count == 0 || nodes.Any(node => !Clean(node))) return null;

        // Existing metadata is only a cheap eligibility check. Unknown/changed files use
        // the normal preload/save path without issuing an extra round of HTTP requests.
        var snapshots = new Dictionary<CadTreeNode, DocumentVersionDto>();
        lock (metadataCacheSync)
            foreach (var node in nodes)
            {
                if (!documentVersionsCache.TryGetValue(node.DocumentId.Value, out var cached)
                    || cached.ExpiresUtc <= DateTime.UtcNow) return null;
                var latest = cached.Versions.FirstOrDefault();
                if (latest?.Revision == null || !string.Equals(latest.Revision.Display, node.LatestRevision, StringComparison.OrdinalIgnoreCase)
                    || ArchiveIndexWasCompleted(latest.ReferenceSnapshot, node)
                    || !ReferenceSnapshotMatchesTree(latest.ReferenceSnapshot, node)) return null;
                snapshots[node] = latest;
            }

        var groups = nodes.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var analyzed = 0;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            ArchiveSession.Current?.ReportAnalysisProgress(analyzed++, groups.Length, group.First().FileName,
                "正在分析本地图档与缓存版本…", false);
            var hash = await Task.Run(() => ArchiveFileFingerprint.Read(group.Key, token), token);
            if (group.Any(node => !Clean(node) || !NodeMatchesLatestFile(node, hash) || !VersionMatchesFile(snapshots[node], hash))) return null;
            hashes[group.Key] = hash;
        }

        var plan = await BuildBatchCheckInPlanAsync(items, report, token);
        if (plan.Items.Count != 0 || plan.SkippedFiles != items.Count) return null;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            if (group.Any(node => !Clean(node))) return null;
            var hash = await Task.Run(() => ArchiveFileFingerprint.Read(group.Key, token), token);
            if (!string.Equals(hash, hashes[group.Key], StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var node in group)
            {
                // BuildPlan refreshes assembly/drawing histories. Compare that fresh
                // snapshot as well; in particular drawing view/configuration changes
                // must not be hidden by the plan's legacy file-only drawing check.
                var latest = plan.Preflights.TryGetValue(node.FullPath, out var preflight)
                    ? preflight.Versions.FirstOrDefault() : snapshots[node];
                if (!Clean(node) || !NodeMatchesLatestFile(node, hash) || !VersionMatchesFile(latest, hash)
                    || ArchiveIndexWasCompleted(latest?.ReferenceSnapshot, node)
                    || !ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node)) return null;
            }
        }
        if (nodes.Any(node => !Clean(node))) return null;
        LogOperation("整批图档及引用均未变化，已复核空存档计划，跳过预加载、重建和保存。");
        return plan;
    }

    private void ClosePreloadedArchiveDocuments(IEnumerable<IModelDoc2> documents)
    {
        ArchiveSession.Current?.SetStage("正在核对并结束临时图档读取…");
        foreach (var document in documents.Reverse())
        {
            try
            {
                var path = document.GetPathName();
                ArchiveSession.Current?.ReportFile(0, 0, Path.GetFileName(path), "正在核对并关闭临时读取的图档…");
                if (document.GetSaveFlag() && (!preloadedOwnedDrawings.Contains(path) || !document.IsOpenedReadOnly()))
                {
                    PluginDiagnosticLog.Record("提醒", "临时加载的图档仍有未保存内容，已保留在SolidWorks中，请打开核对并保存：" + path);
                    ArchiveSession.Current?.ReportFile(0, 0, Path.GetFileName(path), "该图档仍有未保存内容，已保留在SolidWorks中，请核对并保存。");
                    continue;
                }
                CloseBatchOpenedDocument(document);
            }
            catch (Exception exception) { LogDiagnostic("保留无法确认保存状态的临时图档", exception); }
        }
    }

    private static IReadOnlyList<BatchOperationItem> OrderBatchItemsForSave(IReadOnlyList<BatchOperationItem> items)
    {
        var byPath = items.ToDictionary(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase);
        var dependencies = items.ToDictionary(item => item.Node.FullPath,
            item => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => item.Node.Kind != CadDocumentKind.Drawing))
        {
            // A file can occur directly below the root and deep inside another assembly.
            // The first occurrence's display depth does not describe every parent dependency.
            foreach (var ancestor in item.Ancestors.Where(node => node.Kind != CadDocumentKind.Drawing))
                if (!PathsEqual(ancestor.FullPath, item.Node.FullPath) && dependencies.TryGetValue(ancestor.FullPath, out var children))
                    children.Add(item.Node.FullPath);
            foreach (var child in EnumerateCadNodes(item.Node).Skip(1).Where(node => node.Kind != CadDocumentKind.Drawing
                && node.Role != CadReferenceRole.ExternalFileDependency && !string.IsNullOrWhiteSpace(node.FullPath)))
                if (!PathsEqual(child.FullPath, item.Node.FullPath) && byPath.ContainsKey(child.FullPath))
                    dependencies[item.Node.FullPath].Add(child.FullPath);
        }
        var ordered = new List<BatchOperationItem>();
        var remaining = new HashSet<string>(items.Where(item => item.Node.Kind != CadDocumentKind.Drawing)
            .Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var ready = items.Where(item => remaining.Contains(item.Node.FullPath)
                && !dependencies[item.Node.FullPath].Any(remaining.Contains)).ToArray();
            if (ready.Length == 0)
            {
                if (DocumentRuleGate.ShouldBlock("B15", "模型文件的保存依赖形成循环，已停止存档。请核对装配引用和配置后重试。"))
                    throw new InvalidOperationException("模型文件的保存依赖形成循环，已停止存档。请核对装配引用和配置后重试。");
                ready = items.Where(item => remaining.Contains(item.Node.FullPath)).OrderByDescending(item => item.Depth).ToArray();
            }
            foreach (var item in ready) { ordered.Add(item); remaining.Remove(item.Node.FullPath); }
        }
        ordered.AddRange(items.Where(item => item.Node.Kind == CadDocumentKind.Drawing));
        return ordered;
    }

    private async Task<List<IModelDoc2>> PreloadBatchCheckInDocumentsAsync(
        IReadOnlyList<BatchOperationItem> items, Action<string> report, CancellationToken token)
    {
        var opened = new List<IModelDoc2>();
        var originalPath = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        var unique = DistinctBatchItems(items).OrderBy(item => item.Node.Kind == CadDocumentKind.Drawing ? 1 : 0).ToArray();
        var loaded = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Where(document => !string.IsNullOrWhiteSpace(document.GetPathName()))
            .GroupBy(document => document.GetPathName(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        IModelDoc2 Existing(string path) => !string.IsNullOrWhiteSpace(path) && loaded.TryGetValue(path, out var document) ? document : null;
        var initiallyDirty = new HashSet<string>(unique.Where(item => Existing(item.Node.FullPath)?.GetSaveFlag() == true)
            .Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        preloadedArchiveDrawings.Clear();
        preloadedOwnedDrawings.Clear();
        try
        {
            foreach (var item in unique)
            {
                token.ThrowIfCancellationRequested();
                var node = item.Node;
                report?.Invoke("存档准备：正在核对已打开图档… " + node.FileName);
                if (node.Role == CadReferenceRole.ExternalFileDependency || node.HistoricalRepair != null) continue; // Preserve companions and historical bytes without opening/rebuilding them.
                token.ThrowIfCancellationRequested();
                var document = Existing(node.FullPath);
                if (document == null)
                {
                    // Closed files already have a saved disk state. Read them one at a time
                    // during preparation and close each owned document immediately afterwards.
                    PluginDiagnosticLog.Record("存档准备", "按文件顺序读取，不整批打开驻留：" + node.FileName);
                    continue;
                }
                if (!PathsEqual(document.GetPathName(), node.FullPath))
                    throw new InvalidOperationException("预加载返回了其他路径的同名图档，已停止存档：" + node.FileName);
                if (node.Kind == CadDocumentKind.Drawing)
                {
                    // Archive the confirmed drawing; updating/rebuilding is a separate design action.
                    scanner.UpdateDrawingReferences(document, node);
                    preloadedArchiveDrawings.Add(node.FullPath);
                }
                await Task.Yield();
            }
            var pending = unique.Where(item => Existing(item.Node.FullPath)?.GetSaveFlag() == true)
                .Select(item => item.Node.FileName).ToArray();
            var referenceOnly = unique.Where(item => item.Node.Kind == CadDocumentKind.Drawing)
                .SelectMany(item => item.Node.ReferencedModels)
                .Where(model => !unique.Any(item => PathsEqual(item.Node.FullPath, model.FullPath)))
                .Where(model => Existing(model.FullPath)?.GetSaveFlag() == true)
                .Select(model => model.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            PluginDiagnosticLog.Record("存档准备", "预加载与引用核对完成；本次提交文件预加载前未保存" + initiallyDirty.Count
                + "个；当前待保存" + pending.Length + "个：" + string.Join("、", pending.Take(8))
                + "；仅作参考的模型有未保存标记" + referenceOnly.Length + "个：" + string.Join("、", referenceOnly.Take(8))
                + "。参考模型保留内存状态，不自动获取权限或升版"
                + "。尚未上传，确认保存后才开始存档校验。");
            return opened;
        }
        catch
        {
            ClosePreloadedArchiveDocuments(opened);
            preloadedArchiveDrawings.Clear();
            preloadedOwnedDrawings.Clear();
            throw;
        }
        finally
        {
            RestoreOriginalDocumentAfterCheckIn(originalPath, (application.ActiveDoc as IModelDoc2)?.GetPathName());
        }
    }

    private static void ReuseVerifiedArchiveStructures(CadTreeNode previous, CadTreeNode current)
    {
        var sources = EnumerateCadNodes(previous).Where(node => node.Kind == CadDocumentKind.Assembly && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateCadNodes(current).Where(SuppressedAssemblyArchive.IsUnexpanded).ToArray())
        {
            if (!sources.TryGetValue(node.FullPath, out var candidates)) continue;
            var source = candidates.FirstOrDefault(old => old.Configuration == node.Configuration && old.InstancePath == node.InstancePath
                && old.DocumentId == node.DocumentId && !old.LocalStructureDeferred);
            if (source == null || source.IsModifiedInSolidWorks || source.IsRenamePendingSave || node.IsModifiedInSolidWorks || node.IsRenamePendingSave) continue;
            // Reuse the native structure only after TryReuse's fresh content hash.
            SuppressedAssemblyLocalStructure.TryReuse(source, node);
            if (string.IsNullOrWhiteSpace(node.CurrentRevision) || node.CurrentRevision == source.CurrentRevision)
            {
                node.CurrentRevision = source.CurrentRevision;
                node.SuppressedArchiveVersion = source.SuppressedArchiveVersion;
                node.SuppressedArchiveSha256 = source.SuppressedArchiveSha256;
            }
        }
    }

    // B02-B08: files come from the native inventory; structure is a separate index.
    private async Task<IReadOnlyList<BatchOperationItem>> PrepareArchiveScopeAsync(
        IReadOnlyList<BatchOperationItem> selected, Guid projectId, CancellationToken token)
    {
        ArchiveSession.Current?.SetStep(3, "正在核对本次选择的引用与配置…", selected.Count);
        await apiClient.RefreshArchiveWarningPolicyAsync(token);
        ArchiveMissingReferencePolicy.Prepare(currentTree, selected.Select(item => item.Node).ToArray());
        await apiClient.RefreshDocumentRulesAsync(token);
        // Catch known historical ownership/content conflicts before the expensive native scan.
        await IncludeHistoricalReferenceRepairsAsync(currentTree, selected, projectId, token, validateOnly: true);
        if (IsSolidWorksCommandActive())
        {
            if (DocumentRuleGate.ShouldBlock("A10", "请先完成SolidWorks当前编辑或保存操作，再提交存档。"))
                throw new InvalidOperationException("请先完成SolidWorks当前编辑或保存操作，再提交存档。");
            while (IsSolidWorksCommandActive())
            {
                taskPaneControl?.SetWorkspaceOperationProgress("等待SolidWorks当前命令结束后继续存档…", 0, 0);
                await Task.Delay(150, token);
            }
        }
        CancelMetadataRefresh();
        var previous = currentTree;
        Action<string> stage = text =>
        {
            PluginDiagnosticLog.Record("存档预检查", text + "；根图档：" + ArchiveReferencePath.Visible(previous.FullPath));
            ArchiveSession.Current?.ReportFile(0, 0, previous.FileName, "存档前结构检查：" + text);
        };
        stage("开始核对路径");
        ArchiveReferencePath.Resolve(previous.FullPath, null, "核对当前根图档路径", previous.FullPath, previous.InstancePath, previous.Configuration);
        var document = FindLoadedDocument(previous.FullPath);
        if (document == null)
            throw new InvalidOperationException("当前存档根图档已关闭或切换，请重新打开并确认存档范围。");
        var paths = new HashSet<string>(selected.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        var wholeRoot = paths.Contains(previous.FullPath);
        var verifiedScanner = new SolidWorksReferenceTreeScanner(application) { DiscoverRelatedDrawings = wholeRoot };
        stage("重新读取组件结构及原生文件范围");
        var verified = verifiedScanner.ScanDocument(document, false);
        stage("定位旧目录中的实际引用文件");
        var locationAliases = ResolveArchiveFileLocations(verified);
        stage("核对本地图档身份");
        RestorePersistedDocumentIdentities(verified);
        var previousNodes = EnumerateCadNodes(previous).Where(node => !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateCadNodes(verified))
        {
            previousNodes.TryGetValue(node.FullPath, out var old);
            if (old == null && !string.IsNullOrWhiteSpace(node.SourceReferencePath))
                old = previousNodes.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourceReferencePath, node.SourceReferencePath, StringComparison.OrdinalIgnoreCase)
                    && candidate.InstancePath == node.InstancePath && candidate.Configuration == node.Configuration
                    && candidate.Kind == node.Kind && candidate.FileName == node.FileName);
            if (old != null)
            {
                CopyPdmIdentity(old, node);
                node.OpenedVersionId = old.OpenedVersionId;
                node.OpenedRevision = old.OpenedRevision;
                if (PathsEqual(old.FullPath, node.FullPath) && old.DocumentId == node.DocumentId
                    && old.Configuration == node.Configuration && old.CurrentRevision == node.CurrentRevision
                    && !old.IsModifiedInSolidWorks && !old.IsRenamePendingSave)
                {
                    node.SuppressedArchiveVersion = old.SuppressedArchiveVersion;
                    node.SuppressedArchiveSha256 = old.SuppressedArchiveSha256;
                }
                if (!wholeRoot)
                    foreach (var drawing in old.Children.Where(child => child.Kind == CadDocumentKind.Drawing))
                        if (!node.Children.Any(child => PathsEqual(child.FullPath, drawing.FullPath))) node.Children.Add(drawing);
                if (SuppressedAssemblyLocalStructure.TryReuse(old, node))
                    LogOperation("存档结构复用，未重复打开压缩子装配体：" + node.FullPath);
            }
        }
        var inspectionItems = BuildBatchOperationItems(verified);
        var selectedForInspection = inspectionItems.Where(item => selected.Any(original =>
            PathsEqual(item.Node.FullPath, original.Node.FullPath)
            || locationAliases.TryGetValue(original.Node.FullPath, out var actual) && PathsEqual(item.Node.FullPath, actual)
            || original.Node.DocumentId.HasValue && item.Node.DocumentId == original.Node.DocumentId)).ToArray();
        var inspectedOwners = selectedForInspection;
        stage("轻量核对本次选取图档的引用配置");
        var selectedModelPaths = new HashSet<string>(selectedForInspection.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        ValidateSavedModelConfigurations(EnumerateCadNodes(verified).Where(node => selectedModelPaths.Contains(node.FullPath)), token);
        ArchiveMissingReferencePolicy.Prepare(verified, selectedForInspection.Select(item => item.Node).ToArray());
        var inspectionRoots = ArchiveInspectionRoots(verified, inspectedOwners);
        stage("核对压缩组件及引用完整性");
        foreach (var scope in inspectionRoots)
        {
            await RestoreMissingControlledArchiveFilesAsync(scope, projectId, token);
            await ResolveSuppressedAssemblyReferencesAsync(scope, token);
            // File check-in does not require loading a suppressed assembly merely
            // to build its occurrence index. Keep any unknown index explicitly pending.
            if (EnumerateCadNodes(scope).Any(node => !node.IsOutsideNativeFileScope
                && SuppressedAssemblyArchive.IsUnexpanded(node) && node.SuppressedArchiveVersion == null))
                scope.StructureIndexPending = true;
            {
                // Include both the native dependency list and explicitly selected drawing folders.
                stage("读取原生文件和工程图清单");
                var nativeFiles = ReadSelectedNativeInventory(verifiedScanner, scope);
                ArchiveMissingReferencePolicy.Prepare(verified, selectedForInspection.Select(item => item.Node).ToArray());
                NativeArchiveManifest.ConfirmFileScope(scope, nativeFiles);
                foreach (var dependency in EnumerateArchiveNodes(scope).Where(node => node.Role == CadReferenceRole.ExternalFileDependency))
                {
                    RestorePersistedDocumentIdentities(dependency);
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(dependency.FullPath, out var metadata)
                        && metadata.DocumentId == dependency.DocumentId && metadata.ProjectId == projectId)
                    {
                        dependency.OpenedVersionId = metadata.VersionId;
                        dependency.OpenedRevision = dependency.CurrentRevision = dependency.Revision = metadata.Revision;
                    }
                }
                await RestoreMissingControlledArchiveFilesAsync(scope, projectId, token);
                verifiedScanner.AddRelatedDrawingPaths(scope, previousNodes.Values
                    .Where(node => node.Kind == CadDocumentKind.Drawing && File.Exists(node.FullPath))
                    .Where(node => paths.Contains(node.FullPath))
                    .Select(node => node.FullPath), true);
                ArchiveMissingReferencePolicy.Prepare(verified, selectedForInspection.Select(item => item.Node).ToArray());
                if (scope.Kind == CadDocumentKind.Assembly)
                {
                    stage("比对原生文件清单与实例引用");
                    scope.UnmanagedReferences.AddRange(NativeArchiveManifest.Validate(scope, nativeFiles));
                }
            }
        }
        currentTree = verified;
        stage("补齐关联工程图并核对提交范围");
        foreach (var scope in inspectionRoots) await CompleteRelatedDrawingFilesAsync(scope, projectId, token);
        ArchiveMissingReferencePolicy.Prepare(verified, selectedForInspection.Select(item => item.Node).ToArray());
        foreach (var drawing in inspectionRoots.SelectMany(EnumerateCadNodes).Where(node => node.Kind == CadDocumentKind.Drawing).Distinct().ToArray())
            for (var index = 0; index < drawing.ReferencedModels.Count; index++)
            {
                var model = drawing.ReferencedModels[index];
                if (!model.IsOutsideNativeFileScope && !model.DocumentId.HasValue && model.Kind == CadDocumentKind.Assembly && model.Children.Count == 0)
                {
                    model.StructureIndexPending = drawing.StructureIndexPending = true;
                    PluginDiagnosticLog.Record("结构待补全", model.FileName + "：保留已保存的模型和工程图，不为补索引额外打开装配体。");
                }
            }
        // Every actual drawing view remains a dependency, including models outside
        // the selected assembly. Otherwise the drawing cannot reopen independently.
        selected = RestoreRecoveredArchiveReferences(verified, selected);
        var aliases = ArchiveFileAliases.Normalize(verified);
        var all = BuildBatchOperationItems(verified);
        bool MatchesSelection(BatchOperationItem candidate, BatchOperationItem original) =>
            PathsEqual(candidate.Node.FullPath, original.Node.FullPath)
            || locationAliases.TryGetValue(original.Node.FullPath, out var relocated) && PathsEqual(candidate.Node.FullPath, relocated)
            || aliases.TryGetValue(original.Node.FullPath, out var canonical) && PathsEqual(candidate.Node.FullPath, canonical)
            || original.Node.DocumentId.HasValue && candidate.Node.DocumentId == original.Node.DocumentId;
        var refreshed = all.Where(item => selected.Any(original => MatchesSelection(item, original))).ToArray();
        var absent = selected.Where(item => !all.Any(candidate => MatchesSelection(candidate, item))
            && !EnumerateCadNodes(verified).Any(node => node.IsOutsideNativeFileScope && PathsEqual(node.FullPath, item.Node.FullPath))).ToArray();
        if (absent.Length > 0)
            throw new InvalidOperationException("存档范围在检查时发生变化，以下图档未出现在完整结构中，请核对后重试："
                + string.Join("、", absent.Select(item => item.Node.FileName)));
        var complete = (IReadOnlyList<BatchOperationItem>)refreshed;
        complete = await IncludeHistoricalReferenceRepairsAsync(verified, complete, projectId, token);
        complete = complete.Where(item => selected.Any(original => MatchesSelection(item, original)))
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        stage("提前核对原生引用的实际位置");
        complete = await RecoverPdmReferencesAsync(complete, projectId, token);
        ValidatePdmNativeDependencyPaths(complete, token);
        await CompleteCycleEvidenceAsync(complete, token);
        ArchiveSession.Current?.ReportFile(0, 0, "—", "引用读取已完成，正在核对提交依赖和文件完整性…");
        ArchiveReadContext.Read<object>(null, "核对提交依赖和文件完整性", () =>
        {
            ArchiveReferenceCycles.Validate(complete);
            foreach (var item in complete) PdmApiClient.ValidateArchiveReferenceCompleteness(item.Node);
            return null;
        });

        var drawings = complete.Where(item => item.Node.Kind == CadDocumentKind.Drawing).Select(item => item.Node.FileName).ToArray();
        PluginDiagnosticLog.Record("存档范围", "已核对提交清单：" + complete.Count + "个文件，关联工程图" + drawings.Length
            + "张；工程图清单：" + string.Join("、", drawings));
        ArchiveSession.Current?.ReportFile(0, 0, "—", "引用核对已完成，正在显示存档选择范围…");
        ArchiveReadContext.Read<object>(null, "请求显示存档选择范围", () => { taskPaneControl.SetTree(verified); return null; });
        return complete;
    }

    internal static IReadOnlyList<CadTreeNode> ArchiveInspectionRoots(CadTreeNode root, IReadOnlyList<BatchOperationItem> selected)
    {
        var items = DistinctBatchItems(selected);
        if (items.Any(item => PathsEqual(item.Node.FullPath, root.FullPath))) return new[] { root };
        return items.Where(item => !item.Ancestors.Any(ancestor => items.Any(parent =>
                parent.Node.Kind == CadDocumentKind.Assembly && PathsEqual(parent.Node.FullPath, ancestor.FullPath))))
            .Select(item => item.Node).ToArray();
    }

    private IReadOnlyList<string> ReadSelectedNativeInventory(SolidWorksReferenceTreeScanner reader, CadTreeNode node)
    {
        if (node.ConfirmedArchivePaths != null) return ConfirmedArchiveInventory.Read(node);
        var loaded = FindLoadedDocument(node.FullPath);
        var opened = loaded == null;
        try
        {
            if (opened) loaded = OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind),
                node.Kind == CadDocumentKind.Drawing ? string.Empty : node.Configuration, true);
            if (loaded == null || !PathsEqual(loaded.GetPathName(), node.FullPath))
            { opened = false; throw new IOException("待提交图档未能从确认路径读取，原窗口已保留：" + node.FileName); }
            return reader.AddNativeAssociatedDrawings(loaded, node);
        }
        finally
        {
            if (opened && loaded != null && !CloseArchiveReadDocument(loaded))
                throw new IOException("本次临时读取窗口未能关闭，原文件及用户窗口已保留：" + node.FileName);
        }
    }

    private void InvalidateChangedVersionHistories(IReadOnlyList<DocumentDto> documents)
    {
        lock (metadataCacheSync)
            foreach (var document in documents)
            {
                if (!documentVersionsCache.TryGetValue(document.Id, out var cached)) continue;
                if (cached.Versions.Count != document.StoredVersionCount
                    || cached.Versions.FirstOrDefault()?.Revision?.Display != (document.StoredVersionCount > 0 ? document.Revision?.Display : null))
                    documentVersionsCache.Remove(document.Id);
            }
    }
}
