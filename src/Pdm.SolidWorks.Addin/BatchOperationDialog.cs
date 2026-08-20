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
    private const int FileSelectionColumnWidth = 28;
    private const int FileTreeLeftPadding = 4;
    private const int FileTreeIndentWidth = 16;
    private const int FileExpanderSize = 9;
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl { Dock = DockStyle.Fill };
    private readonly RadioButton acquire = new RadioButton { Text = "获取最新并获取权限", AutoSize = true, Checked = true };
    private readonly RadioButton checkIn = new RadioButton { Text = "提交最新整套存档", AutoSize = true };
    private readonly TreeView files = new TreeView();
    private readonly Panel filesHeader = new Panel();
    private readonly ImageList selectionImages = BuildSelectionImages();
    private readonly ImageList structureImages = PdmTaskPaneControl.BuildStructureImages();
    private readonly IReadOnlyList<BatchOperationItem> operationItems;
    private readonly Dictionary<string, BatchOperationItem> itemsByPath;
    private readonly IReadOnlyDictionary<string, Guid> inheritedDrawingProjects;
    private readonly HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly string username;
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
        CadTreeNode root,
        IReadOnlyList<BatchOperationItem> items,
        IReadOnlyList<ProjectDto> projects,
        Guid? initialProjectId,
        string username,
        BatchOperationKind initialOperation = BatchOperationKind.AcquireLatestAndCheckout,
        IReadOnlyCollection<string> initiallySelectedPaths = null,
        IReadOnlyDictionary<string, Guid> inheritedDrawingProjects = null)
    {
        this.username = username ?? string.Empty;
        operationItems = items ?? Array.Empty<BatchOperationItem>();
        this.inheritedDrawingProjects = inheritedDrawingProjects
            ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        itemsByPath = operationItems
            .Where(item => !string.IsNullOrWhiteSpace(item?.Node?.FullPath))
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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
        files.BorderStyle = BorderStyle.FixedSingle;
        files.HideSelection = false;
        files.FullRowSelect = true;
        files.ShowNodeToolTips = true;
        files.ImageList = structureImages;
        files.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        files.StateImageList = selectionImages;
        files.ItemHeight = Math.Max(files.ItemHeight, 20);
        files.DrawNode += DrawFileDetailNode;
        files.Resize += (_, _) => filesHeader.Invalidate();
        files.MouseDown += (_, eventArgs) =>
        {
            var rowNode = GetFileNodeAtRow(eventArgs.Y);
            if (eventArgs.Button == MouseButtons.Left && eventArgs.X < FileSelectionColumnWidth)
            {
                if (rowNode != null)
                {
                    files.SelectedNode = rowNode;
                    ToggleNode(rowNode);
                }
                return;
            }

            if (eventArgs.Button == MouseButtons.Left
                && rowNode != null
                && rowNode.Nodes.Count > 0
                && GetFileExpanderBounds(rowNode).Contains(eventArgs.Location))
            {
                if (rowNode.IsExpanded)
                {
                    rowNode.Collapse();
                }
                else
                {
                    rowNode.Expand();
                }
                files.Invalidate();
                return;
            }

            if ((eventArgs.Button == MouseButtons.Left || eventArgs.Button == MouseButtons.Right) && rowNode != null)
            {
                files.SelectedNode = rowNode;
            }
        };
        files.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Space && files.SelectedNode != null)
            {
                ToggleNode(files.SelectedNode);
                eventArgs.Handled = true;
            }
        };

        projectSelector.SelectedProjectChanged += (_, _) =>
        {
            projectConfirmation.Clear();
            projectConfirmed.Checked = false;
            RefreshProjectConfirmationState();
        };
        projectSelector.SetProjects(projects);
        projectSelector.SelectProject(initialProjectId);
        const int projectActionWidth = 130;
        const int projectInputHeight = 30;
        projectSelector.BrowseButtonWidth = projectActionWidth;
        projectSelector.MinimumSize = new Size(120, projectInputHeight);
        projectSelector.MaximumSize = new Size(0, projectInputHeight);
        projectSelector.Height = projectInputHeight;
        projectSelector.Margin = Padding.Empty;

        files.BeginUpdate();
        try
        {
            var treeRoot = CreateOccurrenceNode(root);
            if (treeRoot != null)
            {
                files.Nodes.Add(treeRoot);
            }
            else
            {
                foreach (var item in operationItems)
                {
                    files.Nodes.Add(CreateFlatNode(item));
                }
            }

            foreach (var item in operationItems)
            {
                var path = item.Node.FullPath;
                var shouldCheck = selectedPaths != null
                    ? selectedPaths.Contains(path)
                    : startWithCheckIn ? CanPrepareForCheckIn(item.Node, this.username) : CanAcquire(item.Node);
                if (shouldCheck)
                {
                    checkedPaths.Add(path);
                }
            }

            UpdateAllTreeNodes();
            ExpandRootOnly();
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
            AutoSize = false,
            Height = projectInputHeight,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        projectPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, projectInputHeight));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var projectSelectionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = projectInputHeight,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        projectSelectionPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, projectInputHeight));
        projectSelectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        projectSelectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectSelectionPanel.Controls.Add(new Label
        {
            Text = "选择归属项目",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0)
        }, 0, 0);
        projectSelectionPanel.Controls.Add(projectSelector, 1, 0);

        var projectConfirmationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = projectInputHeight,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        projectConfirmationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, projectInputHeight));
        projectConfirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        projectConfirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectConfirmationPanel.Controls.Add(new Label
        {
            Text = "新增存档确认",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0)
        }, 0, 0);
        var confirmationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = projectInputHeight,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        confirmationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, projectInputHeight));
        confirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        confirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        confirmationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, projectActionWidth));
        projectConfirmation.AutoSize = false;
        projectConfirmation.MinimumSize = new Size(0, projectInputHeight);
        projectConfirmation.MaximumSize = new Size(0, projectInputHeight);
        projectConfirmation.Height = projectInputHeight;
        projectConfirmation.Margin = Padding.Empty;
        confirmationPanel.Controls.Add(projectConfirmation, 0, 0);
        projectConfirmed.Margin = Padding.Empty;
        confirmationPanel.Controls.Add(projectConfirmed, 2, 0);
        projectConfirmationPanel.Controls.Add(confirmationPanel, 1, 0);

        projectPanel.Controls.Add(projectSelectionPanel, 0, 0);
        projectPanel.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(205, 211, 219),
            Margin = new Padding(11, 2, 11, 2)
        }, 1, 0);
        projectPanel.Controls.Add(projectConfirmationPanel, 2, 0);

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
        filesHeader.Dock = DockStyle.Top;
        filesHeader.Height = 25;
        filesHeader.BackColor = Color.FromArgb(242, 244, 247);
        filesHeader.Paint += DrawFileDetailHeader;
        var fileDetails = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        fileDetails.Controls.Add(files);
        fileDetails.Controls.Add(filesHeader);
        layout.Controls.Add(fileDetails, 0, 3);
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

    public IReadOnlyList<BatchOperationItem> SelectedItems => operationItems
        .Where(item => !string.IsNullOrWhiteSpace(item.Node.FullPath) && checkedPaths.Contains(item.Node.FullPath))
        .ToArray();

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public string SelectedProjectDisplay => projectSelector.SelectedProjectDisplay;

    public string ChangeNote => changeNote.Text.Trim();

    private void RefreshOperationState(string username)
    {
        changeNote.Enabled = checkIn.Checked;
        selectChanged.Enabled = checkIn.Checked;
        selectionSummary.Text = string.Empty;
        checkedPaths.Clear();
        foreach (var item in operationItems)
        {
            if (checkIn.Checked ? CanPrepareForCheckIn(item.Node, username) : CanAcquire(item.Node))
            {
                checkedPaths.Add(item.Node.FullPath);
            }
        }
        UpdateAllTreeNodes();
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

        if (SelectedItems.Any(item => RequiresNewDocumentProjectConfirmation(
                item,
                SelectedProjectId,
                inheritedDrawingProjects)))
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
        checkedPaths.Clear();
        if (value)
        {
            foreach (var item in operationItems.Where(item => CanSelectNode(item.Node)))
            {
                checkedPaths.Add(item.Node.FullPath);
            }
        }
        UpdateAllTreeNodes();
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
            var directItems = new HashSet<BatchOperationItem>();
            foreach (var item in operationItems)
            {
                if (CanSelectForChangedSubmission(item.Node, username)
                    && await Task.Run(() => HasDirectChange(item.Node, username)))
                {
                    directItems.Add(item);
                }
            }

            var relatedItems = new HashSet<BatchOperationItem>();
            foreach (var directItem in directItems)
            {
                foreach (var ancestor in directItem.Ancestors.Where(candidate => candidate.Kind == CadDocumentKind.Assembly))
                {
                    if (!string.IsNullOrWhiteSpace(ancestor.FullPath)
                        && itemsByPath.TryGetValue(ancestor.FullPath, out var ancestorItem)
                        && !directItems.Contains(ancestorItem)
                        && CanSelectForChangedSubmission(ancestor, username))
                    {
                        relatedItems.Add(ancestorItem);
                    }
                }
            }

            checkedPaths.Clear();
            foreach (var item in directItems.Concat(relatedItems))
            {
                checkedPaths.Add(item.Node.FullPath);
            }
            UpdateAllTreeNodes();

            selectionSummary.Text = directItems.Count == 0
                ? "未检测到需要提交的变更"
                : string.Concat(
                    "已勾选", directItems.Count + relatedItems.Count,
                    "项（直接变更", directItems.Count,
                    "，关联装配体", relatedItems.Count, "）");
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            selectionImages?.Dispose();
            structureImages?.Dispose();
        }

        base.Dispose(disposing);
    }

    private TreeNode CreateOccurrenceNode(CadTreeNode model)
    {
        if (model == null)
        {
            return null;
        }

        itemsByPath.TryGetValue(model.FullPath ?? string.Empty, out var item);
        var children = model.Children
            .Select(CreateOccurrenceNode)
            .Where(child => child != null)
            .ToArray();
        if (item == null && children.Length == 0)
        {
            return null;
        }

        var node = new TreeNode
        {
            Tag = new BatchTreeOccurrence(model, item),
            ImageKey = PdmTaskPaneControl.StructureImageKey(model.Kind),
            SelectedImageKey = PdmTaskPaneControl.StructureImageKey(model.Kind),
            ToolTipText = string.Concat(
                model.FileName,
                "\r\n配置：", string.IsNullOrWhiteSpace(model.Configuration) ? "默认" : model.Configuration,
                "\r\n路径：", string.IsNullOrWhiteSpace(model.FullPath) ? "—" : model.FullPath)
        };
        node.Nodes.AddRange(children);
        return node;
    }

    private TreeNode CreateFlatNode(BatchOperationItem item)
    {
        var node = new TreeNode
        {
            Tag = new BatchTreeOccurrence(item.Node, item),
            ImageKey = PdmTaskPaneControl.StructureImageKey(item.Node.Kind),
            SelectedImageKey = PdmTaskPaneControl.StructureImageKey(item.Node.Kind),
            ToolTipText = item.Node.FullPath ?? string.Empty
        };
        return node;
    }

    private void ToggleNode(TreeNode node)
    {
        var paths = SelectablePaths(node).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var shouldCheck = paths.Any(path => !checkedPaths.Contains(path));
        foreach (var path in paths)
        {
            if (shouldCheck)
            {
                checkedPaths.Add(path);
            }
            else
            {
                checkedPaths.Remove(path);
            }
        }

        UpdateAllTreeNodes();
        selectionSummary.Text = string.Concat("已选择 ", SelectedItems.Count, " 个图档");
    }

    private IEnumerable<string> SelectablePaths(TreeNode root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateNodes(root))
        {
            if (node.Tag is BatchTreeOccurrence occurrence
                && occurrence.Item != null
                && CanSelectNode(occurrence.Item.Node)
                && !string.IsNullOrWhiteSpace(occurrence.Item.Node.FullPath))
            {
                paths.Add(occurrence.Item.Node.FullPath);
            }
        }
        return paths;
    }

    private bool CanSelectNode(CadTreeNode node) => checkIn.Checked
        ? CanPrepareForCheckIn(node, username)
        : CanAcquire(node);

    private void UpdateAllTreeNodes()
    {
        files.BeginUpdate();
        try
        {
            foreach (TreeNode root in files.Nodes)
            {
                UpdateTreeNode(root);
            }
        }
        finally
        {
            files.EndUpdate();
        }
        RefreshProjectConfirmationState();
    }

    private void RefreshProjectConfirmationState()
    {
        var required = SelectedItems.Any(item => RequiresNewDocumentProjectConfirmation(
            item,
            SelectedProjectId,
            inheritedDrawingProjects));
        projectConfirmation.Enabled = required;
        projectConfirmed.Enabled = required;
        if (!required)
        {
            projectConfirmation.Clear();
            projectConfirmed.Checked = false;
        }
    }

    internal static bool RequiresNewDocumentProjectConfirmation(
        BatchOperationItem item,
        Guid? selectedProjectId,
        IReadOnlyDictionary<string, Guid> inheritedProjects)
    {
        var node = item?.Node;
        if (node == null || node.DocumentId.HasValue)
        {
            return false;
        }

        if (!selectedProjectId.HasValue
            || string.IsNullOrWhiteSpace(node.FullPath)
            || inheritedProjects == null
            || !inheritedProjects.TryGetValue(node.FullPath, out var inheritedProjectId))
        {
            return true;
        }

        return inheritedProjectId != selectedProjectId.Value;
    }

    private HashSet<string> UpdateTreeNode(TreeNode node)
    {
        var selectable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode child in node.Nodes)
        {
            selectable.UnionWith(UpdateTreeNode(child));
        }

        var occurrence = node.Tag as BatchTreeOccurrence;
        var model = occurrence?.Model;
        if (model != null)
        {
            node.Text = string.IsNullOrWhiteSpace(model.DisplayName) ? model.FileName : model.DisplayName;
        }

        if (occurrence?.Item != null
            && CanSelectNode(occurrence.Item.Node)
            && !string.IsNullOrWhiteSpace(occurrence.Item.Node.FullPath))
        {
            selectable.Add(occurrence.Item.Node.FullPath);
        }

        var checkedCount = selectable.Count(path => checkedPaths.Contains(path));
        var state = selectable.Count == 0 || checkedCount == 0
            ? SelectionState.Unchecked
            : checkedCount == selectable.Count ? SelectionState.Checked : SelectionState.Mixed;
        node.StateImageIndex = (int)state;
        node.ForeColor = selectable.Count == 0 ? Color.FromArgb(145, 155, 168) : Color.FromArgb(31, 49, 72);
        return selectable;
    }

    private void DrawFileDetailHeader(object sender, PaintEventArgs eventArgs)
    {
        GetFileDetailColumns(files.ClientSize.Width, out var nameX, out var versionX);
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.DrawLine(border, 0, filesHeader.Height - 1, filesHeader.Width, filesHeader.Height - 1);
            eventArgs.Graphics.DrawLine(border, FileSelectionColumnWidth, 0, FileSelectionColumnWidth, filesHeader.Height);
            eventArgs.Graphics.DrawLine(border, nameX, 0, nameX, filesHeader.Height);
            eventArgs.Graphics.DrawLine(border, versionX, 0, versionX, filesHeader.Height);
        }

        var textColor = Color.FromArgb(70, 82, 96);
        TextRenderer.DrawText(eventArgs.Graphics, "选", Font, new Rectangle(0, 0, FileSelectionColumnWidth, filesHeader.Height), textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "名称", Font, new Rectangle(FileSelectionColumnWidth + 5, 0, Math.Max(0, nameX - FileSelectionColumnWidth - 9), filesHeader.Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "当前版本 / 最新版本", Font, new Rectangle(nameX + 5, 0, Math.Max(0, versionX - nameX - 9), filesHeader.Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(versionX + 5, 0, Math.Max(0, files.ClientSize.Width - versionX - 9), filesHeader.Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawFileDetailNode(object sender, DrawTreeNodeEventArgs eventArgs)
    {
        var occurrence = eventArgs.Node.Tag as BatchTreeOccurrence;
        var model = occurrence?.Model;
        if (model == null)
        {
            eventArgs.DrawDefault = true;
            return;
        }

        GetFileDetailColumns(files.ClientSize.Width, out var nameX, out var versionX);
        var selected = (eventArgs.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var background = selected ? SystemColors.Highlight : files.BackColor;
        var foreground = selected ? SystemColors.HighlightText : eventArgs.Node.ForeColor;
        using (var brush = new SolidBrush(background))
        using (var border = new Pen(Color.FromArgb(228, 231, 235)))
        {
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(0, eventArgs.Bounds.Top, files.ClientSize.Width, eventArgs.Bounds.Height));
            eventArgs.Graphics.DrawLine(border, FileSelectionColumnWidth, eventArgs.Bounds.Top, FileSelectionColumnWidth, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, nameX, eventArgs.Bounds.Top, nameX, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, versionX, eventArgs.Bounds.Top, versionX, eventArgs.Bounds.Bottom);
        }

        var checkBoxBounds = new Rectangle(
            Math.Max(2, (FileSelectionColumnWidth - 16) / 2),
            eventArgs.Bounds.Top + Math.Max(0, (eventArgs.Bounds.Height - 16) / 2),
            16,
            16);
        var stateImageIndex = Math.Max(0, Math.Min(selectionImages.Images.Count - 1, eventArgs.Node.StateImageIndex));
        eventArgs.Graphics.DrawImage(selectionImages.Images[stateImageIndex], checkBoxBounds);

        var expanderBounds = GetFileExpanderBounds(eventArgs.Node);
        if (eventArgs.Node.Nodes.Count > 0)
        {
            using (var expanderPen = new Pen(selected ? SystemColors.HighlightText : Color.FromArgb(112, 126, 143)))
            {
                eventArgs.Graphics.DrawRectangle(expanderPen, expanderBounds);
                var centerX = expanderBounds.Left + expanderBounds.Width / 2;
                var centerY = expanderBounds.Top + expanderBounds.Height / 2;
                eventArgs.Graphics.DrawLine(expanderPen, expanderBounds.Left + 2, centerY, expanderBounds.Right - 2, centerY);
                if (!eventArgs.Node.IsExpanded)
                {
                    eventArgs.Graphics.DrawLine(expanderPen, centerX, expanderBounds.Top + 2, centerX, expanderBounds.Bottom - 2);
                }
            }
        }

        var nameLeft = expanderBounds.Right + 4;
        if (!string.IsNullOrWhiteSpace(eventArgs.Node.ImageKey) && structureImages.Images.ContainsKey(eventArgs.Node.ImageKey))
        {
            eventArgs.Graphics.DrawImage(structureImages.Images[eventArgs.Node.ImageKey], nameLeft, eventArgs.Bounds.Top + Math.Max(0, (eventArgs.Bounds.Height - 16) / 2), 16, 16);
        }
        nameLeft += 20;
        var nameBounds = new Rectangle(nameLeft, eventArgs.Bounds.Top, Math.Max(0, nameX - nameLeft - 4), eventArgs.Bounds.Height);
        var versionBounds = new Rectangle(nameX + 5, eventArgs.Bounds.Top, Math.Max(0, versionX - nameX - 9), eventArgs.Bounds.Height);
        var stateBounds = new Rectangle(versionX + 5, eventArgs.Bounds.Top, Math.Max(0, files.ClientSize.Width - versionX - 9), eventArgs.Bounds.Height);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, eventArgs.Node.NodeFont ?? files.Font, nameBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            string.Concat(string.IsNullOrWhiteSpace(model.CurrentRevision) ? "—" : model.CurrentRevision, " / ", string.IsNullOrWhiteSpace(model.LatestRevision) ? "—" : model.LatestRevision),
            files.Font,
            versionBounds,
            foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, StateText(model, username), files.Font, stateBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if ((eventArgs.State & TreeNodeStates.Focused) == TreeNodeStates.Focused)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, new Rectangle(eventArgs.Bounds.Left, eventArgs.Bounds.Top, Math.Max(0, files.ClientSize.Width - eventArgs.Bounds.Left), eventArgs.Bounds.Height), foreground, background);
        }
    }

    private TreeNode GetFileNodeAtRow(int y)
    {
        for (var node = files.TopNode; node != null; node = node.NextVisibleNode)
        {
            if (y >= node.Bounds.Top && y < node.Bounds.Bottom)
            {
                return node;
            }
        }
        return null;
    }

    private Rectangle GetFileExpanderBounds(TreeNode node)
    {
        var left = FileSelectionColumnWidth + FileTreeLeftPadding + Math.Max(0, node?.Level ?? 0) * FileTreeIndentWidth;
        var top = (node?.Bounds.Top ?? 0) + Math.Max(0, ((node?.Bounds.Height ?? files.ItemHeight) - FileExpanderSize) / 2);
        return new Rectangle(left, top, FileExpanderSize, FileExpanderSize);
    }

    private static void GetFileDetailColumns(int width, out int nameX, out int versionX)
    {
        var usable = Math.Max(1, width);
        var versionWidth = Math.Max(116, Math.Min(172, usable * 31 / 100));
        var statusWidth = Math.Max(210, Math.Min(260, usable * 21 / 100));
        nameX = usable - versionWidth - statusWidth;
        versionX = usable - statusWidth;
    }

    private void ExpandRootOnly()
    {
        foreach (TreeNode root in files.Nodes)
        {
            root.Expand();
        }
    }

    private static IEnumerable<TreeNode> EnumerateNodes(TreeNode root)
    {
        if (root == null)
        {
            yield break;
        }

        yield return root;
        foreach (TreeNode child in root.Nodes)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static ImageList BuildSelectionImages()
    {
        var images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(16, 16),
            TransparentColor = Color.Transparent
        };
        images.Images.Add(DrawSelectionImage(System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal));
        images.Images.Add(DrawSelectionImage(System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal));
        images.Images.Add(DrawSelectionImage(System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal));
        return images;
    }

    private static Bitmap DrawSelectionImage(System.Windows.Forms.VisualStyles.CheckBoxState state)
    {
        var image = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            if (Application.RenderWithVisualStyles)
            {
                CheckBoxRenderer.DrawCheckBox(graphics, new Point(1, 1), state);
            }
            else
            {
                var buttonState = state == System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
                    ? ButtonState.Checked
                    : ButtonState.Normal;
                ControlPaint.DrawCheckBox(graphics, new Rectangle(1, 1, 13, 13), buttonState);
                if (state == System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(90, 107, 128)))
                    {
                        graphics.FillRectangle(brush, new Rectangle(4, 7, 7, 2));
                    }
                }
            }
        }
        return image;
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
        !node.IsReadOnlyPreview
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

    private static string StateText(CadTreeNode node, string username)
    {
        if (node.IsLatestReadOnlyPreview) return "最新只读";
        if (node.IsHistoricalPreview) return "历史预览（只读）";
        if (!node.DocumentId.HasValue) return "未入库（提交时登记）";
        if (string.IsNullOrWhiteSpace(node.CheckedOutBy)) return node.IsModifiedInSolidWorks
            ? "将获取权限后提交"
            : "未在编辑（提交时分析）";
        if (node.CheckoutSessionLost && string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
        {
            return "编辑权限失效（提交时恢复）";
        }
        return string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            ? "当前用户正在编辑"
            : string.Concat(node.CheckedOutBy, "正在编辑");
    }

    private enum SelectionState
    {
        Unchecked = 0,
        Checked = 1,
        Mixed = 2
    }

    private sealed class BatchTreeOccurrence
    {
        public BatchTreeOccurrence(CadTreeNode model, BatchOperationItem item)
        {
            Model = model;
            Item = item;
        }

        public CadTreeNode Model { get; }

        public BatchOperationItem Item { get; }
    }
}
