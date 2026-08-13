using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal enum BatchOperationKind
{
    AcquireLatestAndCheckout,
    CheckIn
}

internal sealed class BatchOperationItem
{
    public BatchOperationItem(CadTreeNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    public CadTreeNode Node { get; }

    public int Depth { get; }
}

internal sealed class BatchOperationDialog : Form
{
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl { Dock = DockStyle.Fill };
    private readonly RadioButton acquire = new RadioButton { Text = "获取最新并获取权限", AutoSize = true, Checked = true };
    private readonly RadioButton checkIn = new RadioButton { Text = "提交整套存档", AutoSize = true };
    private readonly ListView files = new ListView();
    private readonly TextBox changeNote = new TextBox
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Enabled = false,
        Dock = DockStyle.Fill
    };
    private readonly Button execute = new Button { Text = "开始执行", DialogResult = DialogResult.OK };

    public BatchOperationDialog(
        IReadOnlyList<BatchOperationItem> items,
        IReadOnlyList<ProjectDto> projects,
        Guid? initialProjectId,
        string username,
        BatchOperationKind initialOperation = BatchOperationKind.AcquireLatestAndCheckout)
    {
        var startWithCheckIn = initialOperation == BatchOperationKind.CheckIn;
        acquire.Checked = !startWithCheckIn;
        checkIn.Checked = startWithCheckIn;
        changeNote.Enabled = startWithCheckIn;

        Text = startWithCheckIn ? "登记新增引用并提交整套存档" : "整套装配操作";
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

        foreach (var item in items)
        {
            var node = item.Node;
            var row = new ListViewItem(string.Concat(new string('　', Math.Max(0, item.Depth)), node.DisplayName))
            {
                Tag = item,
                Checked = startWithCheckIn ? CanPrepareForCheckIn(node, username) : CanAcquire(node)
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

        var operationPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        operationPanel.Controls.Add(acquire);
        operationPanel.Controls.Add(checkIn);
        operationPanel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(99, 115, 134),
            Margin = new Padding(18, 4, 0, 0),
            Text = "提交时自动登记新增图档并准备权限；提交顺序为子件优先、总装最后"
        });

        var projectPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectPanel.Controls.Add(new Label
        {
            Text = "归属项目号（本次必选）",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 8, 0)
        }, 0, 0);
        projectPanel.Controls.Add(projectSelector, 1, 0);

        var selectionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var selectAll = new Button { Text = "全选", AutoSize = true };
        var clear = new Button { Text = "清除", AutoSize = true };
        selectAll.Click += (_, _) => SetAllChecked(true);
        clear.Click += (_, _) => SetAllChecked(false);
        selectionButtons.Controls.Add(selectAll);
        selectionButtons.Controls.Add(clear);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(execute);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, (Font.Height * 2) + 10));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(projectPanel, 0, 0);
        layout.Controls.Add(operationPanel, 0, 1);
        layout.Controls.Add(selectionButtons, 0, 2);
        layout.Controls.Add(files, 0, 3);
        layout.Controls.Add(new Label { Text = "统一变更说明（提交存档时必填）", AutoSize = true, Margin = new Padding(0, 8, 0, 4) }, 0, 4);
        layout.Controls.Add(changeNote, 0, 5);
        layout.Controls.Add(new Label
        {
            Text = "注意：勾选图档只会归入上方确认的项目；重复实例按同一个文件处理一次。",
            AutoSize = true,
            ForeColor = Color.FromArgb(99, 115, 134),
            Margin = new Padding(0, 6, 0, 0)
        }, 0, 6);
        layout.Controls.Add(buttons, 0, 7);
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

        if (Operation == BatchOperationKind.CheckIn && string.IsNullOrWhiteSpace(ChangeNote))
        {
            MessageBox.Show(this, "提交整套存档时必须填写变更说明。", "整套装配操作", MessageBoxButtons.OK, MessageBoxIcon.Information);
            eventArgs.Cancel = true;
        }
    }

    private void SetAllChecked(bool value)
    {
        foreach (ListViewItem item in files.Items) item.Checked = value;
    }

    private static bool CanAcquire(CadTreeNode node) =>
        !node.IsHistoricalPreview
        && !string.IsNullOrWhiteSpace(node.FullPath)
        && IsSupported(node.Kind);

    private static bool CanPrepareForCheckIn(CadTreeNode node, string username) =>
        CanAcquire(node)
        && (string.IsNullOrWhiteSpace(node.CheckedOutBy)
            || string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase));

    private static bool IsSupported(CadDocumentKind kind) =>
        kind == CadDocumentKind.Assembly || kind == CadDocumentKind.Part || kind == CadDocumentKind.Drawing;

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
        if (string.IsNullOrWhiteSpace(node.CheckedOutBy)) return "将获取权限后提交";
        return string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            ? "当前用户正在编辑"
            : string.Concat(node.CheckedOutBy, "正在编辑");
    }
}
