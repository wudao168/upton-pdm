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
    private int nativeTreeForOperation;
    private readonly HashSet<CadTreeNode> passiveIndexLoads = new HashSet<CadTreeNode>();

    private void OnStructureViewRequested(object sender, EventArgs args)
    {
        if (currentTree != null && currentProjectId.HasValue && apiClient?.IsAuthenticated == true && !DeferBackgroundTreeRefresh())
            _ = RefreshPassiveMetadataAsync(currentTree, currentProjectId.Value, BeginMetadataRefresh());
    }

    private async void OnStructureNodeRequested(object sender, CadTreeNodeEventArgs args)
    {
        if (currentTree == null || !currentProjectId.HasValue || apiClient?.IsAuthenticated != true || DeferBackgroundTreeRefresh()) return;
        var token = BeginMetadataRefresh();
        try
        {
            var projectId = currentProjectId.Value;
            var documents = await GetProjectDocumentsForNavigationAsync(projectId, token);
            await LoadPassiveStructureAsync(args.Node, projectId, documents.ToDictionary(item => item.Id), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogDiagnostic("展开PLM结构索引", error);
            taskPaneControl.SetTreeLoadingStatus("该结构暂未读取完整，请重新展开或刷新；本地修改已保留。");
        }
        finally { ReleasePassiveMetadataOwner(token); }
    }

    // Activation/save notifications must not resolve components or search drawings.
    // Cached structure is presentation data until an explicit PLM operation verifies it.
    private void RefreshPassiveTree(IModelDoc2 document, bool structureChanged)
    {
        var path = document.GetPathName() ?? string.Empty;
        var identity = string.IsNullOrWhiteSpace(path) ? document.GetTitle() ?? string.Empty : path;
        var sameDocument = currentTree != null && string.Equals(currentDocumentIdentity, identity, StringComparison.OrdinalIgnoreCase);
        if (!sameDocument)
        {
            CancelMetadataRefresh();
            Interlocked.Increment(ref projectResolutionGeneration);
            lock (activeTreeCacheSync)
                currentTree = activeTreeCache.Values.Select(entry => entry.Tree)
                    .FirstOrDefault(tree => string.Equals(DocumentIdentity(tree), identity, StringComparison.OrdinalIgnoreCase));
            if (currentTree == null)
            {
                var name = string.IsNullOrWhiteSpace(path) ? identity : Path.GetFileName(path);
                currentTree = new CadTreeNode { FullPath = path, FileName = name, DisplayName = Path.GetFileNameWithoutExtension(name),
                    InstancePath = name, Kind = document.GetType() == 2 ? CadDocumentKind.Assembly
                        : document.GetType() == 3 ? CadDocumentKind.Drawing : document.GetType() == 1 ? CadDocumentKind.Part : CadDocumentKind.Other,
                    LocalStructureDeferred = true };
                RestorePersistedDocumentIdentities(currentTree);
            }
            currentDocumentIdentity = identity;
            currentTreeCacheKey = identity + "\n";
            currentProjectId = GetExplicitProjectId(path);
        }
        if (structureChanged) currentTree.LocalStructureDeferred = true;
        currentTree.IsModifiedInSolidWorks = document.GetSaveFlag();
        if (currentTree.IsModifiedInSolidWorks) currentTree.WorkState = CadWorkState.ModifiedUnsaved;
        else if (currentTree.WorkState == CadWorkState.ModifiedUnsaved)
            currentTree.WorkState = string.IsNullOrWhiteSpace(currentTree.CheckedOutBy) ? CadWorkState.None : CadWorkState.Editable;
        taskPaneControl.SetProjectContextAvailable(true);
        taskPaneControl.SelectProject(currentProjectId);
        taskPaneControl.SetTree(currentTree);
        taskPaneControl.SetActiveDrawingSource(currentTree.Kind == CadDocumentKind.Drawing
            ? currentTree.ReferencedModels.Concat(currentTree.Children).FirstOrDefault(node => node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly) : null);
        CacheActiveTree(currentTreeCacheKey, currentTree, currentProjectId);
        taskPaneControl.SetTreeLoadingStatus(currentTree.LocalStructureDeferred
            ? "结构按需读取；执行PLM操作时核对实际图档。" : string.Empty);
        if (currentProjectId.HasValue && apiClient?.IsAuthenticated == true)
            _ = RefreshPassiveMetadataAsync(currentTree, currentProjectId.Value, BeginMetadataRefresh());
        LogOperation("设计树轻量刷新，未读取原生组件结构：" + path);
    }

    private async Task RefreshPassiveMetadataAsync(CadTreeNode tree, Guid projectId, CancellationToken token)
    {
        // The normal editing path uses database metadata, never per-file hashes/history
        // or ApplyWorkingStatesAsync (which can read every loaded CAD document).
        try
        {
            var documents = await GetProjectDocumentsForNavigationAsync(projectId, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(tree, currentTree) || DeferBackgroundTreeRefresh()) return;
            var byId = documents.ToDictionary(item => item.Id);
            foreach (var node in EnumerateCadNodes(tree))
                if (node.DocumentId.HasValue && byId.TryGetValue(node.DocumentId.Value, out var document))
                    ApplyCheckoutDocument(node, document);
            taskPaneControl.SetTree(tree);
            if (tree.LocalStructureDeferred && tree.Children.Count == 0 && taskPaneControl.IsStructureTabVisible)
                await LoadPassiveStructureAsync(tree, projectId, byId, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogDiagnostic("读取设计树轻量状态", error);
            if (ReferenceEquals(tree, currentTree)) taskPaneControl.SetTreeLoadingStatus("图档状态暂未更新，请刷新重试；本地修改已保留。");
        }
        finally { ReleasePassiveMetadataOwner(token); }
    }

    private void ReleasePassiveMetadataOwner(CancellationToken token)
    {
        CancellationTokenSource owner;
        lock (metadataRefreshSync)
        {
            if (!metadataRefreshOwners.TryGetValue(token, out owner)) return;
            metadataRefreshOwners.Remove(token);
            if (ReferenceEquals(metadataRefreshCancellation, owner)) metadataRefreshCancellation = null;
        }
        _ = Task.Run(() => { lock (metadataRefreshSync) owner.Dispose(); });
    }

    private async Task LoadPassiveStructureAsync(CadTreeNode tree, Guid projectId,
        IReadOnlyDictionary<Guid, DocumentDto> documents, CancellationToken token)
    {
        if (!tree.DocumentId.HasValue || !passiveIndexLoads.Add(tree)) return;
        try
        {
            var versions = await GetNavigationLatestVersionsAsync(tree.DocumentId.Value, token);
            var version = versions.FirstOrDefault();
            // A displayed old working base must never silently acquire a latest structure.
            var boundVersion = tree.OpenedVersionId;
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(tree.FullPath, out var binding) && binding.VersionId.HasValue)
                boundVersion = binding.VersionId;
            if (boundVersion.HasValue && boundVersion != version?.Id)
                version = (await apiClient.GetVersionsAsync(tree.DocumentId.Value, token)).FirstOrDefault(item => item.Id == boundVersion);
            token.ThrowIfCancellationRequested();
            if (!EnumerateCadNodes(currentTree).Contains(tree) || currentProjectId != projectId || DeferBackgroundTreeRefresh()
                || version?.ReferenceSnapshot?.DocumentId != tree.DocumentId) return;
            var indexed = MapProjectTree(version.ReferenceSnapshot, documents, true);
            if (!ReferenceEquals(tree, currentTree) && !string.IsNullOrWhiteSpace(tree.Configuration)
                && !string.Equals(tree.Configuration, indexed.Configuration, StringComparison.OrdinalIgnoreCase)) return;
            var context = controlledOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(currentTree.FullPath, pair.Key));
            var files = context.Value?.Files ?? new List<ControlledOpenFileDto>();
            foreach (var node in EnumerateCadNodes(indexed))
            {
                var matches = files.Where(file => file.DocumentId == node.DocumentId).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    var relative = matches[0].RelativePath ?? matches[0].FileName;
                    var candidate = Path.GetFullPath(Path.Combine(context.Key, relative));
                    if (IsPathWithinDirectory(candidate, context.Key)) node.FullPath = candidate;
                    node.OpenedVersionId = matches[0].VersionId;
                }
                node.LocalStructureDeferred = true;
                node.DisplayStructurePending = node.Kind == CadDocumentKind.Assembly && node.DocumentId.HasValue && node.Children.Count == 0;
                if (!ReferenceEquals(tree, currentTree) && !string.IsNullOrWhiteSpace(indexed.InstancePath)
                    && node.InstancePath.StartsWith(indexed.InstancePath + "/", StringComparison.OrdinalIgnoreCase))
                    node.InstancePath = tree.InstancePath + node.InstancePath.Substring(indexed.InstancePath.Length);
            }
            tree.Children.Clear();
            tree.Children.AddRange(indexed.Children);
            tree.Configuration = indexed.Configuration;
            tree.OpenedVersionId = version.Id;
            tree.CurrentRevision = version.Revision?.Display ?? tree.CurrentRevision;
            tree.DisplayStructurePending = false;
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.SetTreeLoadingStatus("已显示PLM结构索引；存档时核对本地实际结构。");
        }
        finally { passiveIndexLoads.Remove(tree); }
    }

    private CadTreeNodeEventArgs EnsureNativeTreeForOperation(CadTreeNodeEventArgs selection)
    {
        if (currentTree != null && !currentTree.LocalStructureDeferred) return selection;
        Interlocked.Increment(ref nativeTreeForOperation);
        try { RefreshTree(true); }
        finally { Interlocked.Decrement(ref nativeTreeForOperation); }
        if (currentTree == null || currentTree.LocalStructureDeferred)
            throw new InvalidOperationException("当前图档实际结构尚未读取完整，请等待SolidWorks就绪后重试；本地修改已保留。");
        if (selection == null) return null;
        CadTreeNode Resolve(CadTreeNode selected)
        {
            if (selected == null) return null;
            var matches = EnumerateCadNodes(currentTree).Where(node => !string.IsNullOrWhiteSpace(selected.FullPath)
                ? PathsEqual(node.FullPath, selected.FullPath) : selected.DocumentId.HasValue && node.DocumentId == selected.DocumentId).ToArray();
            var match = matches.FirstOrDefault(node => node.InstancePath == selected.InstancePath) ?? matches.FirstOrDefault();
            if (match == null) throw new InvalidOperationException("所选图档已不在当前实际结构中，请重新选择：" + selected.FileName);
            return match;
        }
        return new CadTreeNodeEventArgs(Resolve(selection.Node), selection.Nodes.Select(Resolve).ToArray(), selection.UsesCheckedSelection);
    }
}
