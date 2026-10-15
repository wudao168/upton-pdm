using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // The display tree may be partial or cached. A submission always owns a freshly verified tree.
    private async Task<IReadOnlyList<BatchOperationItem>> PrepareArchiveScopeAsync(
        IReadOnlyList<BatchOperationItem> selected, Guid projectId, CancellationToken token)
    {
        if (IsSolidWorksCommandActive())
            throw new InvalidOperationException("请先完成SolidWorks当前编辑或保存操作，再提交存档。");
        var previous = currentTree;
        var document = FindLoadedDocument(previous.FullPath);
        if (document == null)
            throw new InvalidOperationException("当前存档根图档已关闭或切换，请重新打开并确认存档范围。");
        var paths = new HashSet<string>(selected.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        var verifiedScanner = new SolidWorksReferenceTreeScanner(application);
        var verified = verifiedScanner.ScanDocument(document, true);
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
        await ResolveSuppressedAssemblyReferencesAsync(verified, token);
        await ReadMissingSuppressedStructuresAsync(verified, token);
        PdmApiClient.ValidateArchiveReferenceCompleteness(verified);
        if (verified.Kind != CadDocumentKind.Drawing)
        {
            // Include both the native dependency list and explicitly selected drawing folders.
            verifiedScanner.AddNativeAssociatedDrawings(document, verified);
            verifiedScanner.AddRelatedDrawingPaths(verified, previousNodes.Values
                .Where(node => node.Kind == CadDocumentKind.Drawing && File.Exists(node.FullPath))
                .Select(node => node.FullPath), true);
        }
        currentTree = verified;
        await CompleteRelatedDrawingFilesAsync(verified, projectId, token);
        var all = BuildBatchOperationItems(verified);
        var refreshed = all.Where(item => paths.Contains(item.Node.FullPath)).ToArray();
        var absent = selected.Where(item => !all.Any(candidate => PathsEqual(candidate.Node.FullPath, item.Node.FullPath))).ToArray();
        if (absent.Length > 0)
            throw new InvalidOperationException("存档范围在检查时发生变化，以下图档未出现在完整结构中，请核对后重试："
                + string.Join("、", absent.Select(item => item.Node.FileName)));
        var complete = IncludeSelectedModelDrawings(refreshed, all);
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
