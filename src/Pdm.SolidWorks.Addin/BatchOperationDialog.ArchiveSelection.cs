using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class BatchOperationDialog
{
    private readonly Dictionary<string, ArchiveSelectionState> archiveSelectionStates = new Dictionary<string, ArchiveSelectionState>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource archiveAnalysisCancellation;
    private bool archiveAnalysisInProgress;
    private bool archiveSelectionTouched;
    private HashSet<string> initialArchivePaths;
    private readonly HashSet<string> archiveModelUpdatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal Func<CadTreeNode, CancellationToken, Task<IReadOnlyList<DocumentVersionDto>>> LoadArchiveVersionsAsync { get; set; }

    private void CancelArchiveAnalysis()
    {
        if (archiveAnalysisCancellation != null)
        {
            archiveAnalysisCancellation.Cancel();
            ArchiveSession.Current?.SetStage("已停止变更分析，请确认当前操作范围。");
        }
        archiveAnalysisCancellation = null;
        archiveAnalysisInProgress = false;
    }

    private ArchiveSelectionState SelectionStateFor(CadTreeNode node) => archiveSelectionStates.TryGetValue(SelectionKey(node), out var state)
        ? state : !node.DocumentId.HasValue || node.StoredVersionStateKnown && !node.HasStoredVersion
            ? ArchiveSelectionState.New : ArchiveSelectionState.Pending;

    private bool CanSelectArchiveState(CadTreeNode node)
    {
        var state = SelectionStateFor(node);
        return state == ArchiveSelectionState.New || state == ArchiveSelectionState.Changed || state == ArchiveSelectionState.DrawingUpdate || state == ArchiveSelectionState.PendingSave
            || state == ArchiveSelectionState.Unchanged && editDisposition.SelectedIndex != 1
                && !node.CheckoutSessionLost
                && string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase);
    }

    private string ArchiveSelectionText(CadTreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy) && !string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
            return DisplayUserName(node.CheckedOutBy) + "正在编辑，不能提交";
        if (!System.IO.File.Exists(node.FullPath)) return "本地文件缺失，暂不可提交";
        switch (SelectionStateFor(node))
        {
            case ArchiveSelectionState.New: return "首次存档（提交时登记）";
            case ArchiveSelectionState.Changed: return "已确认变更，可提交";
            case ArchiveSelectionState.DrawingUpdate: return "配套工程图待更新（存档时更新）";
            case ArchiveSelectionState.PendingSave: return "已获取权限，存档时保存并核对";
            case ArchiveSelectionState.Unchanged:
                if (node.DrawingReferenceIndexPending && string.IsNullOrWhiteSpace(node.CheckedOutBy)) return "未变更，历史模型引用待补全（无需提交）";
                if (node.CheckoutSessionLost) return "未变更，编辑权限已失效（无需提交）";
                if (!string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)) return "未变更，无需提交";
                return editDisposition.SelectedIndex == 1 ? "未变更，继续编辑（无需提交）"
                    : editDisposition.SelectedIndex == 2 ? "未变更，仅交接权限（不升版）" : "未变更，仅释放权限（不升版）";
            default:
                if (node.CheckoutSessionLost) return "编辑权限已失效，修改已保留待恢复";
                if (!string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
                    return node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved
                        ? "未获取权限，内存状态待核对" : "未获取权限，状态待核对";
                return node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved || node.IsRenamePendingSave
                    ? "已获取权限，保存状态待核对" : "已获取权限，变更待核对";
        }
    }

    private async Task AnalyzeArchiveSelectionAsync(bool selectChangedItems)
    {
        if (!checkIn.Checked || ArchiveToStandardLibrary || archiveAnalysisInProgress || IsDisposed) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ArchiveSession.Current?.Token ?? CancellationToken.None);
        archiveAnalysisCancellation = cancellation;
        archiveAnalysisInProgress = true;
        execute.Enabled = false;
        recheck.Enabled = false;
        selectChanged.Enabled = false;
        selectChanged.Text = "分析中...";
        ArchiveSession.Current?.ReportAnalysisProgress(0, 0, null, "正在整理本次分析的图档范围…", false);
        SetSelectionHint("正在核对文件内容和引用结构，待核对项不会自动勾选...");
        try
        {
            var token = cancellation.Token;
            foreach (var item in operationItems.Where(item => archiveModelUpdatePaths.Contains(SelectionKey(item.Node))))
                item.Node.RequiresModelUpdate = false;
            archiveModelUpdatePaths.Clear();
            foreach (var item in operationItems) ArchiveSourceIdentity.RestoreForProject(item.Node, SelectedProjectId);
            RefreshFileTreePresentation();
            var eligible = operationItems.Where(item => !existingReferenceOwners.ContainsKey(SelectionKey(item.Node)))
                .GroupBy(item => SelectionKey(item.Node), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            KeyValuePair<string, ArchiveSelectionState>[] results;
            var completed = 0;
            ArchiveSession.Current?.ReportAnalysisProgress(0, eligible.Length, null, "正在分析文件内容和引用结构…", false);
            using (var slots = new SemaphoreSlim(8))
            {
                results = await Task.WhenAll(eligible.Select(async item =>
                {
                    await slots.WaitAsync(token);
                    try
                    {
                        ArchiveSession.Current?.ReportAnalysisProgress(Volatile.Read(ref completed), eligible.Length,
                            item.Node.FileName, "正在分析文件内容和引用结构…", false);
                        IReadOnlyList<DocumentVersionDto> versions = null;
                        if (item.Node.DocumentId.HasValue && LoadArchiveVersionsAsync != null)
                            versions = await LoadArchiveVersionsAsync(item.Node, token);
                        var state = item.Node.DocumentId.HasValue && LoadArchiveVersionsAsync != null && versions == null
                            ? ArchiveSelectionState.Pending
                            : await Task.Run(() => PdmAddin.AnalyzeArchiveNode(item.Node, username, versions, ComputeFileHash(item.Node.FullPath)), token);
                        var node = item.Node;
                        if (state == ArchiveSelectionState.Pending && versions?.Count > 0 && versions[0].Id == node.OpenedVersionId
                            && (ReferenceEquals(node, root) || root.Kind == CadDocumentKind.Drawing && PdmAddin.DrawingUsesWorkingModel(root, node))
                            && (node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved)
                            && !node.CheckoutSessionLost && string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
                            && node.CheckoutSessionId.HasValue && (node.Kind != CadDocumentKind.Drawing || node.DrawingViewsRead))
                            state = ArchiveSelectionState.PendingSave;
                        return new KeyValuePair<string, ArchiveSelectionState>(SelectionKey(item.Node), state);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        PluginDiagnosticLog.Record("存档变更待核对", item.Node.FileName + "：" + error);
                        return new KeyValuePair<string, ArchiveSelectionState>(SelectionKey(item.Node), ArchiveSelectionState.Pending);
                    }
                    finally
                    {
                        slots.Release();
                        var analyzed = Interlocked.Increment(ref completed);
                        if (!token.IsCancellationRequested && ReferenceEquals(archiveAnalysisCancellation, cancellation))
                            ArchiveSession.Current?.ReportAnalysisProgress(analyzed, eligible.Length,
                                item.Node.FileName, "正在分析文件内容和引用结构…", false);
                    }
                }));
            }
            if (IsDisposed || !checkIn.Checked || ArchiveToStandardLibrary || token.IsCancellationRequested) return;
            archiveSelectionStates.Clear();
            foreach (var result in results) archiveSelectionStates[result.Key] = result.Value;
            var changedModels = eligible.Where(item => item.Node.Kind != CadDocumentKind.Drawing
                && (SelectionStateFor(item.Node) == ArchiveSelectionState.Changed || SelectionStateFor(item.Node) == ArchiveSelectionState.New || SelectionStateFor(item.Node) == ArchiveSelectionState.PendingSave))
                .Select(item => item.Node).ToArray();
            foreach (var drawing in eligible.Where(item => item.Node.Kind == CadDocumentKind.Drawing
                && (SelectionStateFor(item.Node) == ArchiveSelectionState.Pending || SelectionStateFor(item.Node) == ArchiveSelectionState.Unchanged || SelectionStateFor(item.Node) == ArchiveSelectionState.PendingSave)
                && changedModels.Any(model => PdmAddin.DrawingUsesWorkingModel(item.Node, model))))
            {
                drawing.Node.RequiresModelUpdate = true;
                archiveModelUpdatePaths.Add(SelectionKey(drawing.Node));
                archiveSelectionStates[SelectionKey(drawing.Node)] = ArchiveSelectionState.DrawingUpdate;
            }
            checkedPaths.RemoveWhere(path => itemsByPath.TryGetValue(path, out var item) && !CanSelectNode(item.Node));
            if (selectChangedItems) checkedPaths.Clear();
            if (selectChangedItems || !archiveSelectionTouched)
                foreach (var item in eligible)
                    if (CanSelectNode(item.Node) && (!selectChangedItems && initialArchivePaths != null
                        ? initialArchivePaths.Contains(SelectionKey(item.Node))
                        : SelectionStateFor(item.Node) == ArchiveSelectionState.New || SelectionStateFor(item.Node) == ArchiveSelectionState.Changed || SelectionStateFor(item.Node) == ArchiveSelectionState.DrawingUpdate || SelectionStateFor(item.Node) == ArchiveSelectionState.PendingSave))
                        checkedPaths.Add(SelectionKey(item.Node));
            if (selectChangedItems || !archiveSelectionTouched)
            {
                var models = eligible.Where(item => item.Node.Kind != CadDocumentKind.Drawing
                    && checkedPaths.Contains(SelectionKey(item.Node))
                    && (SelectionStateFor(item.Node) == ArchiveSelectionState.Changed || SelectionStateFor(item.Node) == ArchiveSelectionState.New || SelectionStateFor(item.Node) == ArchiveSelectionState.PendingSave))
                    .Select(item => item.Node).ToArray();
                foreach (var drawing in eligible.Where(item => SelectionStateFor(item.Node) == ArchiveSelectionState.DrawingUpdate
                    && CanSelectNode(item.Node) && models.Any(model => PdmAddin.DrawingUsesWorkingModel(item.Node, model))))
                    checkedPaths.Add(SelectionKey(drawing.Node));
            }
            RefreshFileTreePresentation();
            SetSelectionHint("未变更" + archiveSelectionStates.Values.Count(state => state == ArchiveSelectionState.Unchanged)
                + "个，待核对" + archiveSelectionStates.Values.Count(state => state == ArchiveSelectionState.Pending) + "个");
            ArchiveSession.Current?.ReportAnalysisProgress(eligible.Length, eligible.Length, null,
                "分析完成，请核对本次存档范围。", true);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(archiveAnalysisCancellation, cancellation))
            {
                archiveAnalysisCancellation = null;
                archiveAnalysisInProgress = false;
                if (!IsDisposed)
                {
                    selectChanged.Text = "勾选变更项";
                    selectChanged.Enabled = checkIn.Checked && !ArchiveToStandardLibrary;
                    UpdateWorkflowButtons();
                }
            }
            cancellation.Dispose();
        }
    }
}
