using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal enum BatchOperationKind
{
    AcquireLatestAndCheckout,
    CheckIn
}

internal sealed class BatchOperationItem
{
    private readonly List<CadTreeNode> ancestors = new List<CadTreeNode>();

    public BatchOperationItem(CadTreeNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    public CadTreeNode Node { get; }

    public int Depth { get; }

    public IReadOnlyList<CadTreeNode> Ancestors => ancestors;

    public void AddAncestors(IEnumerable<CadTreeNode> values)
    {
        foreach (var value in values ?? Array.Empty<CadTreeNode>())
        {
            if (value == null || ancestors.Any(existing => SameDocumentPath(existing, value)))
            {
                continue;
            }

            ancestors.Add(value);
        }
    }

    private static bool SameDocumentPath(CadTreeNode left, CadTreeNode right) =>
        !string.IsNullOrWhiteSpace(left?.FullPath)
        && !string.IsNullOrWhiteSpace(right?.FullPath)
        && string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
}

internal sealed class BatchOperationDialog : Form
{
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl { Dock = DockStyle.Fill };
    private readonly RadioButton acquire = new RadioButton { Text = "获取最新并获取权限", AutoSize = true, Checked = true };
    private readonly RadioButton checkIn = new RadioButton { Text = "提交最新整套存档", AutoSize = true };
    private readonly ListView files = new ListView();
    private readonly TextBox changeNote = new TextBox
    {
        Multiline = false,
        ScrollBars = ScrollBars.None,
        Enabled = false,
        AutoSize = false,
        Dock = DockStyle.Fill
    };
    private readonly Button execute = new Button { Text = "开始执行", DialogResult = DialogResult.OK };
    private readonly Button selectChanged = new Button { Text = "勾选变更项" };
    private readonly Label selectionSummary = new Label
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(90, 107, 128),
        Anchor = AnchorStyles.Left
    };
    private readonly TextBox projectConfirmation = new TextBox { Dock = DockStyle.Fill };
    private readonly CheckBox projectConfirmed = new CheckBox
    {
        Text = "已确认归属项目",
        AutoSize = true,
        Anchor = AnchorStyles.Left
    };

    public BatchOperationDialog(
        IReadOnlyList<BatchOperationItem> items,
        IReadOnlyList<ProjectDto> projects,
        Guid? initialProjectId,
        string username,
        BatchOperationKind initialOperation = BatchOperationKind.AcquireLatestAndCheckout,
        IReadOnlyCollection<string> initiallySelectedPaths = null)
    {
        var startWithCheckIn = initialOperation == BatchOperationKind.CheckIn;
        var selectedPaths = initiallySelectedPaths == null
            ? null
            : new HashSet<string>(initiallySelectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        acquire.Checked = !startWithCheckIn;
        checkIn.Checked = startWithCheckIn;
        changeNote.Enabled = startWithCheckIn;

        Text = startWithCheckIn ? "登记新增引用并提交最新整套存档" : "整套装配操作";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(1050, 720);
        ClientSize = new Size(1230, 840);
        Font = new Font("Microsoft YaHei UI", 9F);

        files.Dock = DockStyle.Fill;
        files.View = View.Details;
        files.CheckBoxes = true;
        files.FullRowSelect = true;
        files.HideSelection = false;
        files.Columns.Add("结构", 300);
        files.Columns.Add("类型", 70);
        files.Columns.Add("当前/最新", 100);
        files.Columns.Add("状态", 150);
        files.Columns.Add("本地文件", 170);

        projectSelector.SetProjects(projects);
        projectSelector.SelectProject(initialProjectId);
        projectSelector.BrowseButtonWidth = 98;

        files.BeginUpdate();
        try
        {
            foreach (var item in items)
            {
                var node = item.Node;
                var row = new ListViewItem(string.Concat(new string('　', Math.Max(0, item.Depth)), node.DisplayName))
                {
                    Tag = item,
                    Checked = selectedPaths != null
                        ? !string.IsNullOrWhiteSpace(node.FullPath) && selectedPaths.Contains(node.FullPath)
                        : startWithCheckIn ? CanPrepareForCheckIn(node, username) : CanAcquire(node)
                };
                row.SubItems.Add(KindText(node.Kind));
                row.SubItems.Add(string.Concat(
                    string.IsNullOrWhiteSpace(node.CurrentRevision) ? "—" : node.CurrentRevision,
                    " / ",
                    string.IsNullOrWhiteSpace(node.LatestRevision) ? "—" : node.LatestRevision));
                row.SubItems.Add(StateText(node, username));
                row.SubItems.Add(node.FullPath ?? string.Empty);
                files.Items.Add(row);
            }
        }
        finally
        {
            files.EndUpdate();
        }

        var operationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 30,
            ColumnCount = 6,
            RowCount = 2,
            Margin = Padding.Empty
        };
        operationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        acquire.Anchor = AnchorStyles.Left;
        acquire.Margin = new Padding(0, 5, 0, 0);
        checkIn.Anchor = AnchorStyles.Left;
        checkIn.Margin = new Padding(0, 5, 0, 0);
        changeNote.Margin = new Padding(8, 4, 0, 0);
        operationPanel.Controls.Add(acquire, 0, 0);
        operationPanel.Controls.Add(checkIn, 2, 0);
        operationPanel.Controls.Add(new Label
        {
            Text = "统一变更说明",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 0)
        }, 4, 0);
        operationPanel.Controls.Add(changeNote, 5, 0);

        var projectPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        projectPanel.Controls.Add(new Label
        {
            Text = "归属项目号",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 8, 0)
        }, 0, 0);
        projectPanel.Controls.Add(projectSelector, 1, 0);
        projectPanel.Controls.Add(new Label
        {
            Text = "新增图档确认",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 9, 8, 0)
        }, 0, 1);
        var confirmationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 5, 0, 0)
        };
        confirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        confirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        projectConfirmation.Margin = new Padding(0, 0, 8, 0);
        confirmationPanel.Controls.Add(projectConfirmation, 0, 0);
        confirmationPanel.Controls.Add(projectConfirmed, 1, 0);
        projectPanel.Controls.Add(confirmationPanel, 1, 1);
        projectPanel.SetColumnSpan(confirmationPanel, 2);

        var standardButtonSize = new Size(75, 30);
        var selectionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var selectAll = new Button { Text = "全选", AutoSize = false, Size = standardButtonSize };
        var clear = new Button { Text = "清除", AutoSize = false, Size = standardButtonSize };
        selectChanged.AutoSize = false;
        selectChanged.Size = standardButtonSize;
        selectChanged.Enabled = startWithCheckIn;
        selectAll.Click += (_, _) => SetAllChecked(true);
        clear.Click += (_, _) => SetAllChecked(false);
        selectChanged.Click += async (_, _) => await SelectChangedItemsAsync(username);
        selectionButtons.Controls.Add(selectAll);
        selectionButtons.Controls.Add(clear);
        selectionButtons.Controls.Add(selectChanged);

        var commandButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty
        };
        execute.AutoSize = false;
        execute.Size = standardButtonSize;
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = false, Size = standardButtonSize };
        ApplyCommandButtonAppearance(execute, Color.FromArgb(21, 126, 77));
        ApplyCommandButtonAppearance(cancel, Color.FromArgb(230, 126, 34));
        commandButtons.Controls.Add(cancel);
        commandButtons.Controls.Add(execute);

        var selectionBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selectionSummary.Dock = DockStyle.Fill;
        selectionSummary.TextAlign = ContentAlignment.MiddleLeft;
        selectionSummary.Margin = new Padding(8, 0, 8, 0);
        selectionBar.Controls.Add(selectionButtons, 0, 0);
        selectionBar.Controls.Add(selectionSummary, 1, 0);
        selectionBar.Controls.Add(commandButtons, 2, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(projectPanel, 0, 0);
        layout.Controls.Add(operationPanel, 0, 1);
        layout.Controls.Add(selectionBar, 0, 2);
        layout.Controls.Add(files, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = "注意：勾选图档只会归入上方确认的项目；重复实例按同一个文件处理一次。提交时自动登记新增图档并准备权限，顺序为子件优先、总装最后。",
            AutoSize = true,
            ForeColor = Color.FromArgb(99, 115, 134),
            Margin = new Padding(0, 6, 0, 0)
        }, 0, 4);
        Controls.Add(layout);

        acquire.CheckedChanged += (_, _) => RefreshOperationState(username);
        checkIn.CheckedChanged += (_, _) => RefreshOperationState(username);
        FormClosing += ValidateBeforeClose;
        AcceptButton = execute;
        CancelButton = cancel;
    }

    public BatchOperationKind Operation => checkIn.Checked
        ? BatchOperationKind.CheckIn
        : BatchOperationKind.AcquireLatestAndCheckout;

    public IReadOnlyList<BatchOperationItem> SelectedItems => files.CheckedItems
        .Cast<ListViewItem>()
        .Select(item => (BatchOperationItem)item.Tag)
        .ToArray();

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public string SelectedProjectDisplay => projectSelector.SelectedProjectDisplay;

    public string ChangeNote => changeNote.Text.Trim();

    private void RefreshOperationState(string username)
    {
        changeNote.Enabled = checkIn.Checked;
        selectChanged.Enabled = checkIn.Checked;
        selectionSummary.Text = string.Empty;
        foreach (ListViewItem row in files.Items)
        {
            var item = (BatchOperationItem)row.Tag;
            row.Checked = checkIn.Checked ? CanPrepareForCheckIn(item.Node, username) : CanAcquire(item.Node);
        }
    }

    private void ValidateBeforeClose(object sender, FormClosingEventArgs eventArgs)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        if (SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个图档。", "整套装配操作", MessageBoxButtons.OK, MessageBoxIcon.Information);
            eventArgs.Cancel = true;
            return;
        }

        if (!SelectedProjectId.HasValue)
        {
            MessageBox.Show(
                this,
                "请选择本次整套操作的归属项目号。插件不会为未入库图档自动选择项目。",
                "整套装配操作",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            eventArgs.Cancel = true;
            return;
        }

        if (SelectedItems.Any(item => !item.Node.DocumentId.HasValue))
        {
            var expectedCode = projectSelector.SelectedProjectConfirmationCode;
            if (!string.Equals(projectConfirmation.Text.Trim(), expectedCode, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    string.Concat("本次包含新增图档。请再次输入项目号“", expectedCode, "”确认归属。"),
                    "新增图档归属确认",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                eventArgs.Cancel = true;
                projectConfirmation.Focus();
                return;
            }
            if (!projectConfirmed.Checked)
            {
                MessageBox.Show(this, "请勾选“已确认归属项目”后再执行。", "新增图档归属确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                eventArgs.Cancel = true;
                projectConfirmed.Focus();
                return;
            }
        }

    }

    private void SetAllChecked(bool value)
    {
        files.BeginUpdate();
        try
        {
            foreach (ListViewItem item in files.Items) item.Checked = value;
        }
        finally
        {
            files.EndUpdate();
        }
        selectionSummary.Text = string.Empty;
    }

    private async Task SelectChangedItemsAsync(string username)
    {
        if (!checkIn.Checked)
        {
            return;
        }

        selectChanged.Enabled = false;
        selectChanged.Text = "分析中...";
        selectionSummary.Text = "正在比较本地文件与最新存档...";
        try
        {
            var rows = files.Items.Cast<ListViewItem>().ToArray();
            var directRows = new HashSet<ListViewItem>();
            foreach (var row in rows)
            {
                var item = (BatchOperationItem)row.Tag;
                if (CanSelectForChangedSubmission(item.Node, username)
                    && await Task.Run(() => HasDirectChange(item.Node, username)))
                {
                    directRows.Add(row);
                }
            }

            var rowsByPath = rows
                .Select(row => new { Row = row, Item = (BatchOperationItem)row.Tag })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Item.Node.FullPath))
                .GroupBy(entry => entry.Item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.OrdinalIgnoreCase);
            var relatedRows = new HashSet<ListViewItem>();
            foreach (var directRow in directRows)
            {
                var item = (BatchOperationItem)directRow.Tag;
                foreach (var ancestor in item.Ancestors.Where(candidate => candidate.Kind == CadDocumentKind.Assembly))
                {
                    if (!string.IsNullOrWhiteSpace(ancestor.FullPath)
                        && rowsByPath.TryGetValue(ancestor.FullPath, out var ancestorRow)
                        && !directRows.Contains(ancestorRow)
                        && CanSelectForChangedSubmission(ancestor, username))
                    {
                        relatedRows.Add(ancestorRow);
                    }
                }
            }

            files.BeginUpdate();
            try
            {
                foreach (var row in rows)
                {
                    row.Checked = directRows.Contains(row) || relatedRows.Contains(row);
                }
            }
            finally
            {
                files.EndUpdate();
            }

            selectionSummary.Text = directRows.Count == 0
                ? "未检测到需要提交的变更"
                : string.Concat(
                    "已勾选", directRows.Count + relatedRows.Count,
                    "项（直接变更", directRows.Count,
                    "，关联装配体", relatedRows.Count, "）");
        }
        catch (Exception exception)
        {
            selectionSummary.Text = "变更分析失败，请保存图档后重试";
            MessageBox.Show(this, exception.Message, "勾选变更项", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            selectChanged.Text = "勾选变更项";
            selectChanged.Enabled = checkIn.Checked;
        }
    }

    private static bool CanSelectForChangedSubmission(CadTreeNode node, string username) =>
        CanAcquire(node)
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && File.Exists(node.FullPath)
        && (string.IsNullOrWhiteSpace(node.CheckedOutBy)
            || string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase));

    private static bool HasDirectChange(CadTreeNode node, string username)
    {
        if (!node.DocumentId.HasValue
            || node.IsModifiedInSolidWorks
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(node.LatestVersionSha256)
            && string.IsNullOrWhiteSpace(node.LatestStoredSha256))
        {
            return string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase);
        }

        var localSha256 = ComputeFileHash(node.FullPath);
        return !string.Equals(localSha256, node.LatestVersionSha256, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(localSha256, node.LatestStoredSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeFileHash(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
    }

    private static bool CanAcquire(CadTreeNode node) =>
        !node.IsHistoricalPreview
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && IsSupported(node.Kind);

    private static bool CanPrepareForCheckIn(CadTreeNode node, string username) =>
        CanAcquire(node)
        && (string.IsNullOrWhiteSpace(node.CheckedOutBy)
            || string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
        && (!node.DocumentId.HasValue
            || string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            || node.IsModifiedInSolidWorks
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn);

    private static bool IsSupported(CadDocumentKind kind) =>
        kind == CadDocumentKind.Assembly || kind == CadDocumentKind.Part || kind == CadDocumentKind.Drawing;

    private static void ApplyCommandButtonAppearance(Button button, Color background)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = background;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = background;
        button.FlatAppearance.BorderSize = 0;
    }

    private static string KindText(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "装配体";
            case CadDocumentKind.Part: return "零件";
            case CadDocumentKind.Drawing: return "工程图";
            default: return "其他";
        }
    }

    private static string StateText(CadTreeNode node, string username)
    {
        if (node.IsHistoricalPreview) return "历史预览（只读）";
        if (!node.DocumentId.HasValue) return "未入库（提交时登记）";
        if (string.IsNullOrWhiteSpace(node.CheckedOutBy)) return node.IsModifiedInSolidWorks
            ? "将获取权限后提交"
            : "未在编辑（提交时分析）";
        return string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            ? "当前用户正在编辑"
            : string.Concat(node.CheckedOutBy, "正在编辑");
    }
}
