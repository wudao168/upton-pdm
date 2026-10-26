using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private CancellationTokenSource projectNavigationCancellation;
    private readonly Dictionary<Guid, NavigationVersionRequest> navigationVersions = new Dictionary<Guid, NavigationVersionRequest>();
    private readonly Dictionary<Guid, string> navigationVersionStamps = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, NavigationRelationsRequest> navigationRelations = new Dictionary<Guid, NavigationRelationsRequest>();

    private void CancelProjectNavigation()
    {
        var owner = Interlocked.Exchange(ref projectNavigationCancellation, null);
        owner?.Cancel();
        owner?.Dispose();
    }

    private async Task LoadProjectTreeAsync(Guid projectId)
    {
        CancelProjectNavigation();
        var owner = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        projectNavigationCancellation = owner;
        var token = owner.Token;
        var elapsed = Stopwatch.StartNew();
        try
        {
            taskPaneControl.SetProjectLoadingStatus(projectId, "正在读取项目图档清单…");
            var documentsTask = GetProjectDocumentsForNavigationAsync(projectId, token);
            var relationsTask = GetNavigationRelationsAsync(projectId, token);
            var treeTask = apiClient.GetReferenceTreeOrNullAsync(projectId, token);
            var documents = (await documentsTask).ToDictionary(item => item.Id);
            token.ThrowIfCancellationRequested();
            InvalidateChangedVersionHistories(documents.Values.ToArray());
            RememberNavigationVersions(documents.Values);
            // The complete document list is usable before any assembly snapshot arrives.
            taskPaneControl.SetProjectTree(projectId, CompleteProjectWorkspaceTree(projectId, null,
                Array.Empty<CadTreeNode>(), documents, Array.Empty<DocumentModelDrawingRelationDto>()));
            LogOperation("工作区清单首屏 project=" + projectId + " documents=" + documents.Count + " elapsedMs=" + elapsed.ElapsedMilliseconds);
            taskPaneControl.SetProjectLoadingStatus(projectId, "图档清单已显示，正在整理一级结构…");
            await Task.WhenAll(relationsTask, treeTask);
            token.ThrowIfCancellationRequested();
            var root = MapProjectTree(treeTask.Result, documents, true);
            ReconcileProjectDrawings(root, documents, relationsTask.Result);
            var structures = new Dictionary<Guid, CadTreeNode>();
            var loaded = new HashSet<Guid>(EnumerateCadNodes(root).Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId.Value));
            var loading = new HashSet<Guid>();
            void Display()
            {
                token.ThrowIfCancellationRequested();
                var display = CompleteProjectWorkspaceTree(projectId, root, structures.Values.ToArray(), documents, relationsTask.Result);
                var pending = new HashSet<Guid>(documents.Values.Where(document => document.ProjectId == projectId
                    && document.Kind == (int)CadDocumentKind.Assembly && document.StoredVersionCount != 0
                    && !loaded.Contains(document.Id)).Select(document => document.Id));
                taskPaneControl.SetProjectNavigation(projectId, display, async node =>
                {
                    if (!node.DocumentId.HasValue || !loading.Add(node.DocumentId.Value)) return;
                    var id = node.DocumentId.Value;
                    try
                    {
                        taskPaneControl.SetProjectLoadingStatus(projectId, "正在读取结构：" + node.DisplayName);
                        var versions = await GetNavigationLatestVersionsAsync(id, token);
                        token.ThrowIfCancellationRequested();
                        var snapshot = versions.FirstOrDefault()?.ReferenceSnapshot;
                        if (snapshot?.DocumentId == id)
                        {
                            var structure = MapProjectTree(snapshot, documents, true);
                            structures[id] = structure;
                            foreach (var child in EnumerateCadNodes(structure).Where(child => child.DocumentId.HasValue)) loaded.Add(child.DocumentId.Value);
                        }
                        loaded.Add(id);
                        Display();
                        taskPaneControl.SetProjectLoadingStatus(projectId, "结构已加载；其他装配展开时读取。");
                    }
                    finally { loading.Remove(id); }
                }, pending);
            }
            Display();
            taskPaneControl.SetProjectLoadingStatus(projectId, "清单已就绪；装配展开时读取结构。耗时 " + elapsed.Elapsed.TotalSeconds.ToString("0.0") + " 秒");
            LogOperation("工作区一级结构就绪 project=" + projectId + " elapsedMs=" + elapsed.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                taskPaneControl?.SetProjectLoadingStatus(projectId, "项目结构暂未读取完整，请重新选择项目重试。已显示的图档仍可打开。");
            LogDiagnostic("LoadProjectTreeAsync", exception);
        }
        // The token also owns subsequent, lazy structure requests.
    }

    private void RememberNavigationVersions(IEnumerable<DocumentDto> documents)
    {
        lock (metadataCacheSync)
            foreach (var document in documents)
            {
                var stamp = string.Join("|", document.ProjectId, document.Revision?.Display, document.StoredVersionCount, document.UpdatedAt?.Ticks);
                if (!navigationVersionStamps.TryGetValue(document.Id, out var previous) || stamp != previous)
                    navigationVersions.Remove(document.Id);
                navigationVersionStamps[document.Id] = stamp;
            }
    }

    private async Task<IReadOnlyList<DocumentModelDrawingRelationDto>> GetNavigationRelationsAsync(Guid projectId, CancellationToken token)
    {
        NavigationRelationsRequest request;
        lock (metadataCacheSync)
        {
            if (!navigationRelations.TryGetValue(projectId, out request) || request.Client != apiClient
                || request.Username != authenticatedUsername || request.ExpiresUtc <= DateTime.UtcNow || request.Task.IsCanceled || request.Task.IsFaulted)
            {
                request = new NavigationRelationsRequest { Client = apiClient, Username = authenticatedUsername,
                    ExpiresUtc = DateTime.UtcNow.Add(NavigationMetadataCacheDuration),
                    Task = apiClient.GetDocumentRelationsAsync(projectId, lifetime.Token) };
                navigationRelations[projectId] = request;
            }
        }
        return await AwaitNavigationRequestAsync(request.Task, token);
    }

    private async Task<IReadOnlyList<DocumentVersionDto>> GetNavigationLatestVersionsAsync(Guid documentId, CancellationToken token)
    {
        NavigationVersionRequest request;
        lock (metadataCacheSync)
        {
            if (!navigationVersions.TryGetValue(documentId, out request) || request.Client != apiClient
                || request.Username != authenticatedUsername || request.ExpiresUtc <= DateTime.UtcNow || request.Task.IsCanceled || request.Task.IsFaulted)
            {
                request = new NavigationVersionRequest { Client = apiClient, Username = authenticatedUsername,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(5),
                    Task = apiClient.GetVersionsAsync(documentId, lifetime.Token, 1) };
                navigationVersions[documentId] = request;
            }
        }
        return await AwaitNavigationRequestAsync(request.Task, token);
    }

    private async Task<IReadOnlyList<DocumentVersionDto>> GetNavigationTreeVersionsAsync(Guid documentId,
        IReadOnlyList<CadTreeNode> nodes, IReadOnlyDictionary<string, string> hashes, CancellationToken token)
    {
        lock (metadataCacheSync)
            if (documentVersionsCache.TryGetValue(documentId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Versions;
        try
        {
            var latest = await GetNavigationLatestVersionsAsync(documentId, token);
            if (latest.Count == 0 || nodes.All(node => !hashes.TryGetValue(node.FullPath ?? string.Empty, out var hash)
                || VersionMatchesLocalFile(latest[0], node.FullPath, hash))) return latest;
            // Older or changed local files still need the full history for exact matching.
            return await GetTreeVersionsAsync(documentId, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { LogDiagnostic("读取设计树最新版本", error); return null; }
    }

    private static async Task<T> AwaitNavigationRequestAsync<T>(Task<T> request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => canceled.TrySetResult(true)))
        {
            if (await Task.WhenAny(request, canceled.Task) != request) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            return await request;
        }
    }

    private async Task CompleteDrawingNavigationHistoriesAsync(IReadOnlyList<CadTreeNode> nodes,
        IDictionary<Guid, IReadOnlyList<DocumentVersionDto>> histories, CancellationToken token)
    {
        var required = nodes.Where(node => node.Kind == CadDocumentKind.Drawing && node.DocumentId.HasValue
                && histories.ContainsKey(node.DocumentId.Value))
            .SelectMany(node => histories[node.DocumentId.Value].FirstOrDefault()?.ReferenceSnapshot?.Children
                ?? new List<DocumentReferenceNodeDto>())
            .Where(reference => reference.DocumentId.HasValue && !string.IsNullOrWhiteSpace(reference.Revision?.Display)
                && histories.TryGetValue(reference.DocumentId.Value, out var versions)
                && !versions.Any(version => version.Revision?.Display == reference.Revision.Display))
            .Select(reference => reference.DocumentId.Value).Distinct().ToArray();
        for (var offset = 0; offset < required.Length; offset += 8)
        {
            var batch = required.Skip(offset).Take(8).ToArray();
            var complete = await Task.WhenAll(batch.Select(id => GetTreeVersionsAsync(id, token)));
            token.ThrowIfCancellationRequested();
            for (var index = 0; index < batch.Length; index++)
                if (complete[index] != null) histories[batch[index]] = complete[index];
        }
    }

    private sealed class NavigationVersionRequest
    {
        public PdmApiClient Client;
        public string Username;
        public DateTime ExpiresUtc;
        public Task<List<DocumentVersionDto>> Task;
    }

    private sealed class NavigationRelationsRequest
    {
        public PdmApiClient Client;
        public string Username;
        public DateTime ExpiresUtc;
        public Task<List<DocumentModelDrawingRelationDto>> Task;
    }
}
