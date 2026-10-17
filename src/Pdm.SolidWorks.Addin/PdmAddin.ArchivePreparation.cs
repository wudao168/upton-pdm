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
            if (string.IsNullOrWhiteSpace(configuration) || document.GetConfigurationByName(configuration) == null)
                throw new InvalidOperationException("文件“" + node.FileName + "”缺少引用配置“" + configuration + "”，请核对装配实例和工程图视图配置后重试。");
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
        bool Clean(CadTreeNode node) => node.DocumentId.HasValue && node.StoredVersionStateKnown && node.HasStoredVersion
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
                    || !ReferenceSnapshotMatchesTree(latest.ReferenceSnapshot, node)) return null;
                snapshots[node] = latest;
            }

        var groups = nodes.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
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
                    || !ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node)) return null;
            }
        }
        if (nodes.Any(node => !Clean(node))) return null;
        LogOperation("整批图档及引用均未变化，已复核空存档计划，跳过预加载、重建和保存。");
        return plan;
    }

    private void ClosePreloadedArchiveDocuments(IEnumerable<IModelDoc2> documents)
    {
        foreach (var document in documents.Reverse())
        {
            try
            {
                var path = document.GetPathName();
                if (document.GetSaveFlag() && (!preloadedOwnedDrawings.Contains(path) || !document.IsOpenedReadOnly()))
                {
                    PluginDiagnosticLog.Record("提醒", "临时加载的图档仍有未保存内容，已保留在SolidWorks中，请打开核对并保存：" + path);
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
            foreach (var child in EnumerateCadNodes(item.Node).Skip(1).Where(node => node.Kind != CadDocumentKind.Drawing && !string.IsNullOrWhiteSpace(node.FullPath)))
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
                throw new InvalidOperationException("模型文件的保存依赖形成循环，已停止存档。请核对装配引用和配置后重试。");
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
        var initiallyDirty = new HashSet<string>(unique.Where(item => FindLoadedDocument(item.Node.FullPath)?.GetSaveFlag() == true)
            .Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        preloadedArchiveDrawings.Clear();
        preloadedOwnedDrawings.Clear();
        try
        {
            foreach (var item in unique)
            {
                token.ThrowIfCancellationRequested();
                var node = item.Node;
                report?.Invoke("存档准备：正在预加载并核对图档… " + node.FileName);
                if (node.IsImportedSourceFile) continue; // Preserve source bytes without importing/rebuilding them.
                token.ThrowIfCancellationRequested();
                var document = FindLoadedDocument(node.FullPath);
                if (document == null)
                {
                    document = node.Kind == CadDocumentKind.Drawing ? OpenDrawingForArchive(node.FullPath)
                        : OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration ?? string.Empty, true);
                    // SolidWorks may return an already open, same-named document from another
                    // directory. It is not ours to close, even when this preload fails.
                    if (!PathsEqual(document.GetPathName(), node.FullPath))
                        throw new InvalidOperationException("预加载返回了其他路径的同名图档，已停止存档：" + node.FileName);
                    opened.Add(document);
                    if (node.Kind == CadDocumentKind.Drawing) preloadedOwnedDrawings.Add(node.FullPath);
                }
                if (!PathsEqual(document.GetPathName(), node.FullPath))
                    throw new InvalidOperationException("预加载返回了其他路径的同名图档，已停止存档：" + node.FileName);
                if (node.Role == CadReferenceRole.ExternalFileDependency)
                {
                    node.Configuration = document.ConfigurationManager?.ActiveConfiguration?.Name;
                    if (string.IsNullOrWhiteSpace(node.Configuration))
                        throw new InvalidOperationException("原生依赖零件配置尚未读取，已停止存档：" + node.FileName);
                }
                if (node.Kind == CadDocumentKind.Drawing)
                {
                    document.ForceRebuild3(false);
                    scanner.UpdateDrawingReferences(document, node);
                    preloadedArchiveDrawings.Add(node.FullPath);
                }
                await Task.Yield();
            }
            var pending = unique.Where(item => FindLoadedDocument(item.Node.FullPath)?.GetSaveFlag() == true)
                .Select(item => item.Node.FileName).ToArray();
            PluginDiagnosticLog.Record("存档准备", "预加载与工程图重建完成；预加载前未保存" + initiallyDirty.Count
                + "个文件；当前待保存" + pending.Length + "个文件：" + string.Join("、", pending.Take(8))
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

    // The display tree may be partial or cached. A submission always owns a freshly verified tree.
    private async Task<IReadOnlyList<BatchOperationItem>> PrepareArchiveScopeAsync(
        IReadOnlyList<BatchOperationItem> selected, Guid projectId, CancellationToken token)
    {
        if (IsSolidWorksCommandActive())
            throw new InvalidOperationException("请先完成SolidWorks当前编辑或保存操作，再提交存档。");
        var previous = currentTree;
        Action<string> stage = text => PluginDiagnosticLog.Record("存档预检查", text + "；根图档：" + ArchiveReferencePath.Visible(previous.FullPath));
        stage("开始核对路径");
        ArchiveReferencePath.Resolve(previous.FullPath, null, "核对当前根图档路径", previous.FullPath, previous.InstancePath, previous.Configuration);
        var document = FindLoadedDocument(previous.FullPath);
        if (document == null)
            throw new InvalidOperationException("当前存档根图档已关闭或切换，请重新打开并确认存档范围。");
        var paths = new HashSet<string>(selected.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        var verifiedScanner = new SolidWorksReferenceTreeScanner(application);
        stage("重新读取完整组件结构");
        var verified = verifiedScanner.ScanDocument(document, true);
        stage("核对本地图档身份");
        RestorePersistedDocumentIdentities(verified);
        var previousNodes = EnumerateCadNodes(previous).Where(node => !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateCadNodes(verified))
            if (previousNodes.TryGetValue(node.FullPath, out var old))
            {
                CopyPdmIdentity(old, node);
                node.OpenedVersionId = old.OpenedVersionId;
                node.OpenedRevision = old.OpenedRevision;
            }
        stage("核对压缩组件及引用完整性");
        await ResolveSuppressedAssemblyReferencesAsync(verified, token);
        await ReadMissingSuppressedStructuresAsync(verified, token);
        PdmApiClient.ValidateArchiveReferenceCompleteness(verified);
        if (verified.Kind != CadDocumentKind.Drawing)
        {
            // Include both the native dependency list and explicitly selected drawing folders.
            stage("读取原生文件和工程图清单");
            var nativeFiles = verifiedScanner.AddNativeAssociatedDrawings(document, verified);
            verifiedScanner.AddRelatedDrawingPaths(verified, previousNodes.Values
                .Where(node => node.Kind == CadDocumentKind.Drawing && File.Exists(node.FullPath))
                .Select(node => node.FullPath), true);
            if (paths.Contains(verified.FullPath))
            {
                stage("比对原生文件清单与实例引用");
                var external = NativeArchiveManifest.Validate(verified, nativeFiles);
                foreach (var dependency in verified.Children.Where(child => child.Role == CadReferenceRole.ExternalFileDependency))
                    RestorePersistedDocumentIdentities(dependency);
                if (external.Count > 0)
                {
                    var examples = string.Join("\r\n", external.Take(6).Select(path =>
                        (File.Exists(path) ? "工作区外资料：" : "外部资料已缺失：") + path));
                    var choice = TopMostMessageBox.Show(taskPaneControl,
                        "SolidWorks原生清单中另有" + external.Count + "项非图档资料，本次整体存档不会保存它们：\r\n"
                        + examples + (external.Count > 6 ? "\r\n其他路径请查看诊断日志。" : string.Empty)
                        + "\r\n\r\n如需长期保留这些资料，请先作为项目附件归档并核对引用。是否只提交本次SolidWorks图档？",
                        "整体存档范围确认", System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Warning, System.Windows.Forms.MessageBoxDefaultButton.Button2);
                    if (choice != System.Windows.Forms.DialogResult.Yes)
                        throw new InvalidOperationException("已取消整体存档：外部资料尚未纳入受控范围。本次未上传文件。请先归档所需附件后重试。");
                }
            }
        }
        currentTree = verified;
        stage("补齐关联工程图并核对提交范围");
        await CompleteRelatedDrawingFilesAsync(verified, projectId, token);
        var all = BuildBatchOperationItems(verified);
        var refreshed = all.Where(item => paths.Contains(item.Node.FullPath)).ToArray();
        var absent = selected.Where(item => !all.Any(candidate => PathsEqual(candidate.Node.FullPath, item.Node.FullPath))).ToArray();
        if (absent.Length > 0)
            throw new InvalidOperationException("存档范围在检查时发生变化，以下图档未出现在完整结构中，请核对后重试："
                + string.Join("、", absent.Select(item => item.Node.FileName)));
        var complete = IncludeSelectedModelDrawings(refreshed, all);
        if (paths.Contains(verified.FullPath))
            complete = DistinctBatchItems(complete.Concat(all.Where(item => item.Node.Role == CadReferenceRole.ExternalFileDependency)));
        var drawings = complete.Where(item => item.Node.Kind == CadDocumentKind.Drawing).Select(item => item.Node.FileName).ToArray();
        PluginDiagnosticLog.Record("存档范围", "已核对提交清单：" + complete.Count + "个文件，关联工程图" + drawings.Length
            + "张；工程图清单：" + string.Join("、", drawings));
        taskPaneControl.SetTree(verified);
        return complete;
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
