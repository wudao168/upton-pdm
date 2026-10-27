using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class BatchOperationDialog
{
    private Control workflowHeader;
    private Control workflowSelectors;
    private bool workflowDisplayOnly;
    private bool workflowFinished;
    private readonly Dictionary<string, string> workflowStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> workflowVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal Button CancelOperationButton => operationCancel;

    private void InitializeArchiveWorkflowControls(Control header, Control selectors)
    {
        workflowHeader = header; workflowSelectors = selectors;
        foreach (var button in ChildControls(projectSelector).OfType<Button>()) ArchiveCommandAppearance.Apply(button);
        acquire.CheckedChanged += (_, _) => UpdateWorkflowButtons();
        checkIn.CheckedChanged += (_, _) => UpdateWorkflowButtons();
        standardLibrary.CheckedChanged += (_, _) => UpdateWorkflowButtons();
        UpdateWorkflowButtons();
    }

    private static IEnumerable<Control> ChildControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in ChildControls(child)) yield return nested;
        }
    }

    private void UpdateWorkflowButtons()
    {
        if (workflowHeader == null || IsDisposed) return;
        var busy = workflowDisplayOnly;
        execute.Text = acquire.Checked ? "获取权限" : "确认存档";
        execute.Enabled = !workflowFinished && !archiveAnalysisInProgress && !busy
            && SelectedItems.Any(item => !IsEmbedded(item.Node) && CanSelectNode(item.Node));
        recheck.Enabled = !workflowFinished && !busy && !archiveAnalysisInProgress && checkIn.Checked && !ArchiveToStandardLibrary;
    }

    internal void SetWorkflowBusy()
    {
        CancelArchiveAnalysis();
        workflowDisplayOnly = true;
        workflowHeader.Enabled = false;
        foreach (var button in workflowSelectors.Controls.OfType<Button>()) button.Enabled = false;
        operationCancel.DialogResult = recheck.DialogResult = execute.DialogResult = DialogResult.None;
        operationCancel.Show(); recheck.Show(); execute.Show();
        UpdateWorkflowButtons();
    }

    internal void SetPreparedWorkflow(IReadOnlyList<BatchOperationItem> prepared)
    {
        SetWorkflowBusy();
        var merged = operationItems.ToDictionary(item => SelectionKey(item.Node), StringComparer.OrdinalIgnoreCase);
        foreach (var item in prepared)
        {
            var path = SelectionKey(item.Node);
            merged[path] = item; itemsByPath[path] = item;
            workflowStates[path] = item.Node.Kind == CadDocumentKind.Drawing && item.Node.RequiresModelUpdate ? "已更新并核对，正在自动存档" : "准备完成，正在自动存档";
            workflowVersions[path] = item.Node.BusinessVersionText;
        }
        operationItems = merged.Values.ToArray();
        var preparedPaths = new HashSet<string>(prepared.Select(item => SelectionKey(item.Node)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in checkedPaths.Where(path => !preparedPaths.Contains(path))) workflowStates[path] = "未纳入本批（未提交）";
        checkedPaths.Clear();
        foreach (var item in prepared) checkedPaths.Add(SelectionKey(item.Node));
        showCheckedOnly.Enabled = true;
        foreach (var button in workflowSelectors.Controls.OfType<Button>().Where(button => button.Text == "待补全引用")) button.Enabled = existingReferenceOwners.Count > 0;
        RebuildFileTree();
        foreach (var treeNode in files.Nodes.Cast<TreeNode>().SelectMany(EnumerateNodes)
            .Where(node => node.Tag is BatchTreeOccurrence occurrence && checkedPaths.Contains(SelectionKey(occurrence.Model))).ToArray())
            for (var parent = treeNode.Parent; parent != null; parent = parent.Parent) parent.Expand();
        SetSelectionHint("准备完成；自动继续存档，无需再次确认。");
        UpdateWorkflowButtons();
    }

    internal void SetWorkflowFile(CadTreeNode node, string message, string versionText = null)
    {
        if (string.IsNullOrWhiteSpace(node?.FullPath)) return;
        workflowStates[node.FullPath] = message;
        workflowVersions[node.FullPath] = versionText ?? node.BusinessVersionText;
        RefreshFileTreePresentation(); files.Invalidate();
    }

    internal void SetWorkflowFile(string fileName, string message)
    {
        var matches = operationItems.Where(item => checkedPaths.Contains(SelectionKey(item.Node))
            && string.Equals(item.Node.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) SetWorkflowFile(matches[0].Node, message);
    }

    internal void SetWorkflowFiles(IEnumerable<ArchiveSession.ProgressUpdate> updates)
    {
        var byName = operationItems.Where(item => checkedPaths.Contains(SelectionKey(item.Node)))
            .GroupBy(item => item.Node.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Node, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var update in updates)
        {
            var node = update.Node;
            if (node == null && (string.IsNullOrWhiteSpace(update.FileName) || !byName.TryGetValue(update.FileName, out node))) continue;
            if (string.IsNullOrWhiteSpace(node.FullPath)) continue;
            workflowStates[node.FullPath] = update.Message;
            workflowVersions[node.FullPath] = update.VersionText ?? node.BusinessVersionText;
            changed = true;
        }
        // Progress changes only the owner-drawn status/version columns. Selection and
        // hierarchy stay fixed, so do not recalculate checkboxes or rebuild a filtered tree.
        if (changed) files.Invalidate();
    }

    internal void FinishWorkflow()
    {
        workflowFinished = true;
        foreach (var item in operationItems.Where(item => checkedPaths.Contains(SelectionKey(item.Node))))
            if (!workflowStates.TryGetValue(item.Node.FullPath, out var state)
                || !(state.StartsWith("已存档") || state.StartsWith("已获取权限") || state.StartsWith("未变更") || state.StartsWith("结果待核实") || state.StartsWith("未提交")))
                workflowStates[item.Node.FullPath] = "未确认存档结果，请查看过程记录";
        operationCancel.Text = "关闭"; operationCancel.Enabled = true;
        showCheckedOnly.Enabled = true;
        foreach (var button in workflowSelectors.Controls.OfType<Button>().Where(button => button.Text == "待补全引用")) button.Enabled = existingReferenceOwners.Count > 0;
        UpdateWorkflowButtons(); RefreshFileTreePresentation(); files.Invalidate();
    }
}
