using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal enum BatchOperationKind
{
    AcquireLatestAndCheckout,
    CheckIn
}

internal sealed class BatchOperationItem
{
    private readonly List<CadTreeNode> ancestors = new List<CadTreeNode>();
    private readonly List<CadTreeNode> primaryAncestors = new List<CadTreeNode>();
    private bool primaryAncestorsCaptured;

    public BatchOperationItem(CadTreeNode node, int depth)
    {
        Node = node;
        Depth = depth;
    }

    public CadTreeNode Node { get; }

    public int Depth { get; }

    public IReadOnlyList<CadTreeNode> Ancestors => ancestors;

    public IReadOnlyList<CadTreeNode> PrimaryAncestors => primaryAncestors;

    public void AddAncestors(IEnumerable<CadTreeNode> values)
    {
        var candidates = (values ?? Array.Empty<CadTreeNode>()).Where(value => value != null).ToArray();
        if (!primaryAncestorsCaptured)
        {
            primaryAncestorsCaptured = true;
            primaryAncestors.AddRange(candidates);
        }
        foreach (var value in candidates)
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

internal sealed partial class BatchOperationDialog : Form
{
    private const int FileSelectionColumnWidth = 28;
    private const int FileTreeLeftPadding = 4;
    private const int FileTreeIndentWidth = 16;
    private const int FileExpanderSize = 9;
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl(requireSubmissionAccess: true) { Dock = DockStyle.Fill };
    private readonly RadioButton acquire = new RadioButton { Text = "获取权限", AutoSize = true, Checked = true };
    private readonly RadioButton checkIn = new RadioButton { Text = "提交存档", AutoSize = true };
    private readonly CheckBox standardLibrary = new CheckBox { Text = "存标准库", AutoSize = true };
    private readonly ComboBox editDisposition = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "存档后操作" };
    private readonly ComboBox handoffRecipient = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "交接给协助人" };
    private sealed class HandoffChoice
    {
        public string Username;
        public string Name;
        public override string ToString() => Name + "（" + Username + "）";
    }
    public bool KeepEditing => checkIn.Checked && !ArchiveToStandardLibrary && editDisposition.SelectedIndex == 1;
    public string HandoffTo => checkIn.Checked && !ArchiveToStandardLibrary && editDisposition.SelectedIndex == 2 ? (handoffRecipient.SelectedItem as HandoffChoice)?.Username : null;
    private readonly ComboBox drawingType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ToolTip standardLibraryTip = new ToolTip();
    private readonly bool canArchiveStandardLibrary;
    private sealed class DrawingTypeChoice
    {
        public int Value;
        public string Placeholder;
        public override string ToString() => Value == 0 ? Placeholder : Value + " · " + StandardStructureRules.TypeName(Value);
    }
    public bool ArchiveToStandardLibrary => checkIn.Checked && standardLibrary.Checked;
    public int StandardLibraryDrawingType => (drawingType.SelectedItem as DrawingTypeChoice)?.Value ?? 0;
    private readonly TreeView files = new FileSelectionTreeView();
    private readonly Panel filesHeader = new Panel();
    private readonly Panel fileDetails = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
    internal Control FileListContainer => fileDetails;
    internal Control FileListContent => files;
    private readonly ImageList selectionImages = BuildSelectionImages();
    private readonly ImageList structureImages = PdmTaskPaneControl.BuildStructureImages();
    private IReadOnlyList<BatchOperationItem> operationItems;
    private readonly IReadOnlyList<ProjectDto> projects;
    private readonly Dictionary<string, BatchOperationItem> itemsByPath;
    private readonly Dictionary<string, CadTreeNode> existingReferenceOwners;
    private readonly IReadOnlyDictionary<string, Guid> inheritedDrawingProjects;
    private readonly IReadOnlyDictionary<string, string> userDisplayNames;
    private readonly HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly CadTreeNode root;
    private readonly string username;
    private readonly TextBox changeNote = new TextBox
    {
        Multiline = false,
        ScrollBars = ScrollBars.None,
        Enabled = false,
        AutoSize = false,
        Dock = DockStyle.Fill
    };
    private readonly Button execute = new Button { Text = "确认存档", DialogResult = DialogResult.OK };
    private readonly Button recheck = new Button { Text = "重新检查", DialogResult = DialogResult.None };
    private readonly Button operationCancel = new Button { Text = "取消存档", DialogResult = DialogResult.Cancel };
    private readonly Button selectChanged = new Button { Text = "勾选变更项" };
    private readonly Button showCheckedOnly = new Button { Text = "仅显示勾选" };
    public string AdditionalDrawingFolder { get; private set; }
    private bool showCheckedOnlyActive;
    private HashSet<Guid> expandedAllFileNodes;
    private readonly Label selectionSummary = new Label
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(90, 107, 128),
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 4),
        Margin = Padding.Empty
    };
    private readonly Label selectionHint = new Label { Dock = DockStyle.Fill, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, ForeColor = Color.FromArgb(90, 107, 128) };
    public BatchOperationDialog(
        CadTreeNode root,
        IReadOnlyList<BatchOperationItem> items,
        IReadOnlyList<ProjectDto> projects,
        Guid? initialProjectId,
        string username,
        BatchOperationKind initialOperation = BatchOperationKind.AcquireLatestAndCheckout,
        IReadOnlyCollection<string> initiallySelectedPaths = null,
        IReadOnlyDictionary<string, Guid> inheritedDrawingProjects = null,
        IReadOnlyDictionary<string, string> userDisplayNames = null,
        bool canArchiveStandardLibrary = false,
        bool initialStandardLibrary = false,
        int initialDrawingType = 0)
    {
        this.root = root;
        this.username = username ?? string.Empty;
        this.canArchiveStandardLibrary = canArchiveStandardLibrary;
        operationItems = items ?? Array.Empty<BatchOperationItem>();
        this.projects = projects ?? Array.Empty<ProjectDto>();
        this.inheritedDrawingProjects = inheritedDrawingProjects
            ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        this.userDisplayNames = userDisplayNames
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        itemsByPath = operationItems
            .Where(item => !string.IsNullOrWhiteSpace(SelectionKey(item?.Node)))
            .GroupBy(item => SelectionKey(item.Node), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        existingReferenceOwners = operationItems
            .Select(item => new { Key = SelectionKey(item.Node), Owner = ExistingArchivedReferenceOwner(item) })
            .Where(item => item.Owner != null && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Owner, StringComparer.OrdinalIgnoreCase);
        var startWithCheckIn = initialOperation == BatchOperationKind.CheckIn;
        var selectedPaths = initiallySelectedPaths == null
            ? null
            : new HashSet<string>(initiallySelectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        initialArchivePaths = selectedPaths;
        acquire.Checked = !startWithCheckIn;
        checkIn.Checked = startWithCheckIn;
        changeNote.Enabled = startWithCheckIn;

        Text = startWithCheckIn ? "登记新增引用并提交最新整套存档" : "整套装配操作";
        DialogWindowSizing.CenterOnOwnerScreen(this);
        TopMost = true;
        Shown += (_, _) => { BringToFront(); Activate(); };
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(1050, 720);
        ClientSize = new Size(1230, 840);
        Font = new Font("Microsoft YaHei UI", 9F);
        LoadFileColumnWidths();

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
                && IsInFileNameColumn(eventArgs.X)
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

        projectSelector.SetProjects(projects);
        projectSelector.SelectProject(initialProjectId);
        const int projectActionWidth = 96;
        const int projectInputHeight = 28;
        projectSelector.BrowseButtonWidth = 80;
        projectSelector.BrowseButtonWidth = projectActionWidth;
        projectSelector.MinimumSize = new Size(120, projectInputHeight);
        projectSelector.MaximumSize = new Size(0, projectInputHeight);
        projectSelector.Height = projectInputHeight;
        projectSelector.Margin = Padding.Empty;

        foreach (var item in operationItems)
        {
            var path = SelectionKey(item.Node);
            var shouldCheck = selectedPaths != null
                ? selectedPaths.Contains(path)
                : startWithCheckIn ? ShouldSelectForCheckIn(item.Node, this.username) : CanAcquire(item.Node);
            if (shouldCheck && !existingReferenceOwners.ContainsKey(path)
                && (!startWithCheckIn || CanSelectNode(item.Node)))
            {
                checkedPaths.Add(path);
            }
        }
        RebuildFileTree();

        var operationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 60,
            ColumnCount = 10,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 6)
        };
        operationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        operationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 4));
        operationPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        operationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        acquire.Anchor = AnchorStyles.Left;
        acquire.Margin = Padding.Empty;
        checkIn.Anchor = AnchorStyles.Left;
        checkIn.Margin = Padding.Empty;
        changeNote.Margin = Padding.Empty;
        operationPanel.Controls.Add(new Label
        {
            Text = "归属项目",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 6, 0)
        }, 0, 0);
        operationPanel.Controls.Add(projectSelector, 1, 0);
        operationPanel.Controls.Add(acquire, 3, 0);
        operationPanel.Controls.Add(checkIn, 5, 0);
        standardLibrary.Anchor = AnchorStyles.Left;
        standardLibrary.Margin = Padding.Empty;
        drawingType.Margin = Padding.Empty;
        handoffRecipient.Margin = Padding.Empty;
        editDisposition.Margin = Padding.Empty;
        foreach (var type in new[] { 0, 1, 2, 3, 4, 9 }) drawingType.Items.Add(new DrawingTypeChoice { Value = type, Placeholder = canArchiveStandardLibrary ? "图纸类型" : "仅标准化部门可用" });
        drawingType.SelectedIndex = 0;
        standardLibrary.Enabled = canArchiveStandardLibrary && checkIn.Checked;
        drawingType.Enabled = false;
        standardLibraryTip.SetToolTip(standardLibrary, canArchiveStandardLibrary ? "存入集团共享标准库，首次自动归入未分类；升版保留原分类。" : "仅获授权的标准化部门成员可存档到标准库。");
        operationPanel.Controls.Add(standardLibrary, 7, 0);
        operationPanel.Controls.Add(drawingType, 9, 0);
        operationPanel.Controls.Add(new Label
        {
            Text = "变更说明",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 6, 0)
        }, 0, 2);
        operationPanel.Controls.Add(changeNote, 1, 2);
        editDisposition.Items.AddRange(new object[] { "存档并释放权限", "存档并继续编辑", "存档并交接给协助人" });
        editDisposition.SelectedIndex = 0;
        foreach (var user in this.userDisplayNames.Where(pair => !pair.Key.Equals(this.username, StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Value))
            handoffRecipient.Items.Add(new HandoffChoice { Username = user.Key, Name = user.Value });
        operationPanel.Controls.Add(editDisposition, 3, 2);
        operationPanel.SetColumnSpan(editDisposition, 5);
        operationPanel.Controls.Add(handoffRecipient, 9, 2);
        void UpdateDisposition()
        {
            editDisposition.Enabled = checkIn.Checked && !ArchiveToStandardLibrary;
            handoffRecipient.Enabled = editDisposition.Enabled && editDisposition.SelectedIndex == 2;
            if (checkIn.Checked && !ArchiveToStandardLibrary)
            {
                checkedPaths.RemoveWhere(path => itemsByPath.TryGetValue(path, out var item) && !CanSelectNode(item.Node));
                RefreshFileTreePresentation();
                selectionSummary.Text = SelectionCountText();
            }
        }
        editDisposition.SelectedIndexChanged += (_, _) => UpdateDisposition();
        checkIn.CheckedChanged += (_, _) => UpdateDisposition();
        standardLibrary.CheckedChanged += (_, _) => UpdateDisposition();
        UpdateDisposition();

        var standardButtonSize = new Size(80, 28);
        var selectionButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false, Margin = Padding.Empty };
        var selectAll = new Button { Text = "全选", AutoSize = false, Size = standardButtonSize };
        var clear = new Button { Text = "清除", AutoSize = false, Size = standardButtonSize };
        var supplement = new Button { Text = "补充工程图", AutoSize = false, Size = standardButtonSize };
        var pendingReferences = new Button { Text = "待补全引用", AutoSize = false, Size = standardButtonSize,
            Enabled = existingReferenceOwners.Count > 0 };
        pendingReferences.Click += (_, _) => ShowExistingReferences();
        supplement.Click += (_, _) =>
        {
            using (var picker = new FolderBrowserDialog { Description = "选择工程图目录，系统将按实际模型引用补充关联工程图。" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                AdditionalDrawingFolder = picker.SelectedPath;
                DialogResult = DialogResult.Retry;
                Close();
            }
        };
        var cancel = operationCancel;
        cancel.AutoSize = recheck.AutoSize = false;
        cancel.Size = recheck.Size = standardButtonSize;
        selectChanged.AutoSize = false;
        selectChanged.Size = standardButtonSize;
        selectChanged.Enabled = startWithCheckIn;
        execute.AutoSize = false;
        execute.Size = standardButtonSize;
        showCheckedOnly.AutoSize = false;
        showCheckedOnly.Size = standardButtonSize;
        showCheckedOnly.Anchor = AnchorStyles.Right;
        selectAll.Margin = new Padding(0, 0, 6, 0);
        clear.Margin = new Padding(0, 0, 6, 0);
        selectChanged.Margin = new Padding(0, 0, 6, 0);
        supplement.Margin = Padding.Empty;
        showCheckedOnly.Margin = Padding.Empty;
        cancel.Margin = new Padding(0, 0, 6, 0);
        execute.Margin = Padding.Empty;
        selectAll.Click += (_, _) => SetAllChecked(true);
        clear.Click += (_, _) => SetAllChecked(false);
        selectChanged.Click += async (_, _) => await SelectChangedItemsAsync(username);
        recheck.Click += async (_, _) => await AnalyzeArchiveSelectionAsync(false);
        showCheckedOnly.Click += (_, _) => ToggleShowCheckedOnly();
        foreach (var button in new[] { selectAll, clear, selectChanged, supplement, pendingReferences, showCheckedOnly, cancel, recheck, execute })
            ArchiveCommandAppearance.Apply(button, button == execute ? Color.FromArgb(21, 126, 77)
                : button == cancel ? Color.FromArgb(230, 126, 34) : (Color?)null);
        foreach (var button in new[] { selectAll, clear, selectChanged, supplement, pendingReferences, cancel, recheck }) button.Margin = new Padding(0, 0, 6, 0);
        selectionButtons.Controls.Add(selectAll);
        selectionButtons.Controls.Add(clear);
        selectionButtons.Controls.Add(selectChanged);
        selectionButtons.Controls.Add(supplement);
        selectionButtons.Controls.Add(pendingReferences);
        selectionButtons.Controls.Add(showCheckedOnly);
        standardLibrary.CheckedChanged += (_, _) =>
        {
            if (standardLibrary.Checked && (!canArchiveStandardLibrary || !checkIn.Checked)) { standardLibrary.Checked = false; return; }
            drawingType.Enabled = ArchiveToStandardLibrary;
            projectSelector.Enabled = !ArchiveToStandardLibrary;
            selectAll.Enabled = clear.Enabled = selectChanged.Enabled = !ArchiveToStandardLibrary;
            if (ArchiveToStandardLibrary)
            {
                if (drawingType.SelectedIndex == 0) drawingType.SelectedIndex = Array.IndexOf(new[] { 0, 1, 2, 3, 4, 9 },
                    StandardStructureRules.IsDrawingType(initialDrawingType) ? initialDrawingType : root.Kind == CadDocumentKind.Assembly ? 1 : root.Kind == CadDocumentKind.Part ? 2 : 9);
                checkedPaths.Clear();
                foreach (var item in operationItems) if (!string.IsNullOrWhiteSpace(item.Node.FullPath)) checkedPaths.Add(SelectionKey(item.Node));
                RebuildFileTree();
                SetSelectionHint("标准库保存完整当前结构及关联工程图；嵌入组件随父装配保存。");
            }
            else { drawingType.SelectedIndex = 0; RefreshOperationState(username); }
        };
        checkIn.CheckedChanged += (_, _) =>
        {
            standardLibrary.Enabled = canArchiveStandardLibrary && checkIn.Checked;
            if (!checkIn.Checked) standardLibrary.Checked = false;
        };

        var selectionBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 6)
        };
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 510));
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 252));
        selectionBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectionBar.Controls.Add(selectionButtons, 0, 0);

        var actionButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = Padding.Empty
        };
        actionButtons.Controls.Add(cancel);
        actionButtons.Controls.Add(recheck);
        actionButtons.Controls.Add(execute);
        selectionBar.Controls.Add(actionButtons, 2, 0);
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(selectionHint, 0, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(operationPanel, 0, 0);
        layout.Controls.Add(selectionBar, 0, 1);
        layout.Controls.Add(selectionSummary, 0, 2);
        filesHeader.Dock = DockStyle.Top;
        filesHeader.Height = 25;
        filesHeader.BackColor = Color.FromArgb(242, 244, 247);
        filesHeader.Paint += DrawFileDetailHeader;
        InitializeFileColumnResizing();
        fileDetails.Controls.Add(files);
        fileDetails.Controls.Add(filesHeader);
        layout.Controls.Add(fileDetails, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
        selectionSummary.Text = SelectionCountText();
        SetSelectionHint(string.Empty);

        acquire.CheckedChanged += (_, _) => RefreshOperationState(username);
        checkIn.CheckedChanged += (_, _) => RefreshOperationState(username);
        FormClosing += ValidateBeforeClose;
        AcceptButton = execute;
        CancelButton = cancel;
        InitializeArchiveWorkflowControls(operationPanel, selectionButtons);
        standardLibrary.Checked = initialStandardLibrary && canArchiveStandardLibrary && checkIn.Checked;
        FormClosed += (_, _) => standardLibraryTip.Dispose();
        Shown += async (_, _) => { if (checkIn.Checked && !ArchiveToStandardLibrary) await AnalyzeArchiveSelectionAsync(false); };
    }

    public BatchOperationKind Operation => checkIn.Checked
        ? BatchOperationKind.CheckIn
        : BatchOperationKind.AcquireLatestAndCheckout;

    public IReadOnlyList<BatchOperationItem> SelectedItems => operationItems
        .Where(item => !string.IsNullOrWhiteSpace(SelectionKey(item.Node)) && checkedPaths.Contains(SelectionKey(item.Node)))
        .ToArray();

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public string SelectedProjectDisplay => projectSelector.SelectedProjectDisplay;

    public string ChangeNote => changeNote.Text.Trim();

    private string SelectionCountText()
    {
        var selected = SelectedItems.Where(item => !IsEmbedded(item.Node))
            .GroupBy(item => SelectionKey(item.Node), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var permissions = checkIn.Checked && !ArchiveToStandardLibrary ? selected.Count(item => SelectionStateFor(item.Node) == ArchiveSelectionState.Unchanged) : 0;
        var drawingUpdates = checkIn.Checked ? selected.Count(item => SelectionStateFor(item.Node) == ArchiveSelectionState.DrawingUpdate) : 0;
        return string.Concat("已勾选 ", selected.Length, " 个图档",
            checkIn.Checked ? "（存档" + (selected.Length - permissions) + "个，仅处理权限" + permissions + "个"
                + (drawingUpdates == 0 ? string.Empty : "，其中工程图待更新" + drawingUpdates + "个") + "）" : string.Empty);
    }

    private void SetSelectionHint(string message)
    {
        var embedded = CountEmbedded(root);
        var imported = CountImported(root);
        selectionHint.Text = string.Concat(message,
            embedded == 0 ? string.Empty : "；另有" + embedded + "个嵌入式组件实例随父装配保存",
            imported == 0 ? string.Empty : "；" + imported + "个导入组件实例待确认保存归属", ExistingReferenceSummary()).TrimStart('；');
        standardLibraryTip.SetToolTip(selectionHint, selectionHint.Text);
    }

    private string ExistingReferenceSummary() => existingReferenceOwners.Count == 0 ? string.Empty
        : "；另有 " + existingReferenceOwners.Count + " 项历史引用，提交相关装配时自动核对补齐（不计本次修改）";

    private void ShowExistingReferences()
    {
        var details = existingReferenceOwners.Select(pair =>
            Path.GetFileName(pair.Key) + "\r\n所属存档装配：" + pair.Value.FileName + "\r\n实际引用路径：" + pair.Key);
        TopMostMessageBox.Show(this,
            "以下文件属于原存档装配的历史遗漏引用，不计入本次工程变更。提交相关装配时将自动核对并补齐实际文件，保留原装配版本。\r\n"
            + "仅复用当前项目内内容相符的版本；不会按名称绑定其他项目。原文件缺失、内容变化或身份冲突会在上传前说明原因。\r\n\r\n" + string.Join("\r\n\r\n", details),
            "历史已有引用待补全", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static int CountEmbedded(CadTreeNode node) => node == null ? 0
        : (IsEmbedded(node) && (!node.IsInterconnectImport || node.IsStoredImport) ? 1 : 0) + node.Children.Sum(CountEmbedded);

    private static int CountImported(CadTreeNode node) => node == null ? 0
        : (node.IsInterconnectImport && !node.IsStoredImport ? 1 : 0) + node.Children.Sum(CountImported);

    private void RefreshOperationState(string username)
    {
        if (ArchiveToStandardLibrary) return;
        if (!checkIn.Checked)
        {
            CancelArchiveAnalysis();
        }
        changeNote.Enabled = checkIn.Checked;
        selectChanged.Enabled = checkIn.Checked;
        SetSelectionHint(checkIn.Checked
            ? "确认未变更的图档无需提交；待核对项不会自动勾选。"
            : string.Empty);
        checkedPaths.Clear();
        archiveSelectionTouched = false;
        foreach (var item in operationItems)
        {
            if (!existingReferenceOwners.ContainsKey(SelectionKey(item.Node))
                && (checkIn.Checked ? ShouldSelectForCheckIn(item.Node, username) : CanAcquire(item.Node)))
            {
                checkedPaths.Add(SelectionKey(item.Node));
            }
        }
        RefreshFileTreePresentation();
        if (checkIn.Checked && IsHandleCreated && !archiveAnalysisInProgress)
            _ = AnalyzeArchiveSelectionAsync(false);
    }

    private void ValidateBeforeClose(object sender, FormClosingEventArgs eventArgs)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }
        if (checkIn.Checked && archiveAnalysisInProgress)
        {
            eventArgs.Cancel = true;
            SetSelectionHint("正在核对存档范围，请等待分析完成。");
            return;
        }
        if (checkIn.Checked && !ArchiveToStandardLibrary && editDisposition.SelectedIndex == 2 && string.IsNullOrWhiteSpace(HandoffTo))
        {
            TopMostMessageBox.Show(this, "请选择接手人；交接前会核对其图档和项目编辑权限。", "图档交接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            eventArgs.Cancel = true;
            return;
        }

        if (ArchiveToStandardLibrary)
        {
            if (!canArchiveStandardLibrary || !StandardStructureRules.IsDrawingType(StandardLibraryDrawingType)
                || !StandardStructureRules.MatchesKind(StandardLibraryDrawingType, root.Kind.ToString()))
            {
                TopMostMessageBox.Show(this, !canArchiveStandardLibrary ? "仅获授权的标准化部门成员可存档到标准库。"
                    : "请选择与当前图档对应的类型：组件使用装配体，零件使用零件，独立工程图选择其他图档。", "标准库存档", MessageBoxButtons.OK, MessageBoxIcon.Information);
                eventArgs.Cancel = true;
            }
            return;
        }
        if (SelectedItems.Count == 0)
        {
            TopMostMessageBox.Show(
                this,
                checkIn.Checked
                    ? "当前没有选择可提交图档。若已经修改，请先在SolidWorks中保存文件，再点击“勾选变更项”；若尚未获取编辑权限，请先选择“获取最新并获取权限”，完成编辑并保存后再提交。"
                    : "请至少选择一个图档。",
                "整套装配操作",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            eventArgs.Cancel = true;
            return;
        }

        if (!SelectedProjectId.HasValue)
        {
            TopMostMessageBox.Show(
                this,
                "请选择本次整套操作的归属项目号。插件不会为未入库图档自动选择项目。",
                "整套装配操作",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            eventArgs.Cancel = true;
            return;
        }

        var confirmationPaths = GetNewDocumentConfirmationPaths();
        if (confirmationPaths.Count > 0
            && !ConfirmSelectedProject(confirmationPaths.Count))
        {
            eventArgs.Cancel = true;
            return;
        }

    }

    private void SetAllChecked(bool value)
    {
        if (ArchiveToStandardLibrary) return;
        archiveSelectionTouched = true;
        checkedPaths.Clear();
        if (value)
        {
            foreach (var item in operationItems.Where(item => CanSelectNode(item.Node)))
            {
                checkedPaths.Add(SelectionKey(item.Node));
            }
        }
        RefreshFileTreePresentation();
        selectionSummary.Text = SelectionCountText();
    }

    private Task SelectChangedItemsAsync(string username) => AnalyzeArchiveSelectionAsync(true);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelArchiveAnalysis();
            files.DrawNode -= DrawFileDetailNode;
            filesHeader.Paint -= DrawFileDetailHeader;
            if (!files.IsDisposed)
            {
                files.ImageList = null;
                files.StateImageList = null;
            }
            selectionImages?.Dispose();
            structureImages?.Dispose();
        }

        base.Dispose(disposing);
    }

    private TreeNode CreateOccurrenceNode(CadTreeNode model) => CreateOccurrenceNode(model, false);

    private TreeNode CreateOccurrenceNode(CadTreeNode model, bool owningFileSelected)
    {
        if (model == null)
        {
            return null;
        }

        itemsByPath.TryGetValue(SelectionKey(model), out var item);
        var embedded = IsEmbedded(model);
        var ownerSelected = embedded ? owningFileSelected : item != null && checkedPaths.Contains(SelectionKey(model));
        var children = model.Children
            .Select(child => CreateOccurrenceNode(child, ownerSelected))
            .Where(child => child != null)
            .ToArray();
        var includeItem = item != null
            && (!showCheckedOnlyActive || checkedPaths.Contains(SelectionKey(item.Node)));
        if (!includeItem && children.Length == 0 && !(embedded && (!showCheckedOnlyActive || owningFileSelected)))
        {
            return null;
        }

        var node = new TreeNode
        {
            Tag = new BatchTreeOccurrence(model, includeItem ? item : null),
            ImageKey = PdmTaskPaneControl.StructureImageKey(model.Kind),
            SelectedImageKey = PdmTaskPaneControl.StructureImageKey(model.Kind),
            ToolTipText = string.Concat(
                model.FileName,
                "\r\n配置：", string.IsNullOrWhiteSpace(model.Configuration) ? "默认" : model.Configuration,
                "\r\n路径：", string.IsNullOrWhiteSpace(model.FullPath) ? "—" : model.FullPath)
        };
        if (showCheckedOnlyActive && !includeItem && !embedded) node.ToolTipText += "\r\n仅显示层级，未勾选";
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

    private void ToggleShowCheckedOnly()
    {
        if (!showCheckedOnlyActive) expandedAllFileNodes = ExpandedFileNodes();
        showCheckedOnlyActive = !showCheckedOnlyActive;
        showCheckedOnly.Text = showCheckedOnlyActive ? "显示全部" : "仅显示勾选";
        showCheckedOnly.BackColor = showCheckedOnlyActive ? Color.FromArgb(220, 245, 240) : SystemColors.Control;
        RebuildFileTree();
    }

    private void RefreshFileTreePresentation()
    {
        selectionSummary.Text = SelectionCountText();
        if (showCheckedOnlyActive)
        {
            RebuildFileTree();
            return;
        }

        UpdateAllTreeNodes();
        UpdateWorkflowButtons();
    }

    private void RebuildFileTree()
    {
        var expanded = showCheckedOnlyActive ? null : expandedAllFileNodes
            ?? (files.Nodes.Count == 0 ? null : ExpandedFileNodes());
        files.BeginUpdate();
        try
        {
            files.Nodes.Clear();
            var treeRoot = CreateOccurrenceNode(root);
            if (treeRoot != null)
            {
                files.Nodes.Add(treeRoot);
            }
            else
            {
                foreach (var item in operationItems.Where(item => !showCheckedOnlyActive || checkedPaths.Contains(SelectionKey(item.Node))))
                {
                    files.Nodes.Add(CreateFlatNode(item));
                }
            }

            var displayed = new HashSet<string>(files.Nodes.Cast<TreeNode>().SelectMany(EnumerateNodes)
                .Select(node => (node.Tag as BatchTreeOccurrence)?.Item).Where(item => item != null)
                .Select(item => SelectionKey(item.Node)), StringComparer.OrdinalIgnoreCase);
            foreach (var item in operationItems.Where(item => !displayed.Contains(SelectionKey(item.Node))
                && (!showCheckedOnlyActive || checkedPaths.Contains(SelectionKey(item.Node))))) files.Nodes.Add(CreateFlatNode(item));
            foreach (TreeNode treeNode in files.Nodes)
            {
                UpdateTreeNode(treeNode);
            }
            if (showCheckedOnlyActive)
            {
                files.ExpandAll();
                if (files.Nodes.Count == 0) files.Nodes.Add(new TreeNode("尚未勾选图档") { ForeColor = Color.FromArgb(90, 107, 128) });
            }
            else
            {
                if (expanded == null) ExpandRootOnly();
                else foreach (var node in files.Nodes.Cast<TreeNode>().SelectMany(EnumerateNodes))
                    if (node.Tag is BatchTreeOccurrence occurrence && expanded.Contains(occurrence.Model.NodeId)) node.Expand();
                expandedAllFileNodes = null;
            }
            selectionSummary.Text = SelectionCountText();
        }
        finally
        {
            files.EndUpdate();
            UpdateWorkflowButtons();
        }
    }

    private HashSet<Guid> ExpandedFileNodes() => new HashSet<Guid>(files.Nodes.Cast<TreeNode>().SelectMany(EnumerateNodes)
        .Where(node => node.IsExpanded && node.Tag is BatchTreeOccurrence)
        .Select(node => ((BatchTreeOccurrence)node.Tag).Model.NodeId));

    private void ToggleNode(TreeNode node)
    {
        if (workflowDisplayOnly)
        {
            SetSelectionHint("本次准备范围已固定；若需调整范围，请取消本次操作后重新选择。");
            return;
        }
        if (ArchiveToStandardLibrary) return;
        if (node?.Tag is BatchTreeOccurrence embedded && IsEmbedded(embedded.Model)) return;
        if (showCheckedOnlyActive && node?.Tag is BatchTreeOccurrence context && context.Item == null) return;
        var paths = checkIn.Checked
            ? node?.Tag is BatchTreeOccurrence occurrence && occurrence.Item != null && CanSelectNode(occurrence.Item.Node)
                ? new[] { SelectionKey(occurrence.Item.Node) } : Array.Empty<string>()
            : SelectablePaths(node).ToArray();
        if (paths.Length == 0)
        {
            return;
        }
        archiveSelectionTouched = true;

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

        RefreshFileTreePresentation();
        selectionSummary.Text = SelectionCountText();
    }

    private IEnumerable<string> SelectablePaths(TreeNode root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateNodes(root))
        {
            if (node.Tag is BatchTreeOccurrence occurrence
                && occurrence.Item != null
                && CanSelectNode(occurrence.Item.Node)
                && !string.IsNullOrWhiteSpace(SelectionKey(occurrence.Item.Node)))
            {
                paths.Add(SelectionKey(occurrence.Item.Node));
            }
        }
        return paths;
    }

    private bool CanSelectNode(CadTreeNode node) => workflowDisplayOnly && checkedPaths.Contains(SelectionKey(node)) || !existingReferenceOwners.ContainsKey(SelectionKey(node))
        && (checkIn.Checked ? CanSelectForCheckIn(node, username) && (ArchiveToStandardLibrary || CanSelectArchiveState(node)) : CanAcquire(node));

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
    }

    private HashSet<string> GetNewDocumentConfirmationPaths() => new HashSet<string>(
        SelectedItems
            .Where(item => RequiresNewDocumentProjectConfirmation(item, SelectedProjectId, inheritedDrawingProjects))
            .Select(item => item.Node.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)),
        StringComparer.OrdinalIgnoreCase);

    private bool ConfirmSelectedProject(int documentCount)
    {
        if (projectSelector.SelectedProject == null)
        {
            return false;
        }

        var selectedProject = projectSelector.SelectedProject;
        var parentProject = selectedProject.ParentProjectId.HasValue
            ? projects.FirstOrDefault(project => project.Id == selectedProject.ParentProjectId.Value)
            : null;
        using (var dialog = new ProjectAdmissionConfirmationDialog(selectedProject, parentProject, documentCount, userDisplayNames))
        {
            return (ArchiveSession.Current?.ShowInline(dialog) ?? dialog.ShowDialog(this)) == DialogResult.OK;
        }
    }

    internal static bool RequiresNewDocumentProjectConfirmation(
        BatchOperationItem item,
        Guid? selectedProjectId,
        IReadOnlyDictionary<string, Guid> inheritedProjects)
    {
        var node = item?.Node;
        if (node == null || node.DocumentId.HasValue || ExistingArchivedReferenceOwner(item) != null)
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

    internal static CadTreeNode ExistingArchivedReferenceOwner(BatchOperationItem item)
        => HistoricalReferenceRepair.Owner(item);

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
            node.Text = string.Concat(
                Path.GetFileNameWithoutExtension(model.FileName),
                " · ",
                model.DisplayName);
        }

        if (occurrence?.Item != null
            && CanSelectNode(occurrence.Item.Node)
            && !string.IsNullOrWhiteSpace(SelectionKey(occurrence.Item.Node)))
        {
            selectable.Add(SelectionKey(occurrence.Item.Node));
        }

        if (checkIn.Checked || showCheckedOnlyActive && occurrence?.Item == null)
        {
            selectable.Clear();
            if (occurrence?.Item != null && CanSelectNode(occurrence.Item.Node)) selectable.Add(SelectionKey(occurrence.Item.Node));
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
        var headerState = eventArgs.Graphics.Save();
        eventArgs.Graphics.TranslateTransform(FileHeaderClientOffset, 0);
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
        TextRenderer.DrawText(eventArgs.Graphics, "图档版本 / 发布版本", Font, new Rectangle(nameX + 5, 0, Math.Max(0, versionX - nameX - 9), filesHeader.Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(versionX + 5, 0, Math.Max(0, files.ClientSize.Width - versionX - 9), filesHeader.Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        eventArgs.Graphics.Restore(headerState);
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
        if (!IsEmbedded(model))
        {
            if (occurrence.Item == null || checkIn.Checked && !CanSelectNode(model))
            {
                if (Application.RenderWithVisualStyles)
                    CheckBoxRenderer.DrawCheckBox(eventArgs.Graphics, checkBoxBounds.Location, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled);
                else ControlPaint.DrawCheckBox(eventArgs.Graphics, checkBoxBounds, ButtonState.Inactive);
            }
            else eventArgs.Graphics.DrawImage(selectionImages.Images[stateImageIndex], checkBoxBounds);
        }

        var nameClip = eventArgs.Graphics.Save();
        eventArgs.Graphics.SetClip(new Rectangle(FileSelectionColumnWidth, eventArgs.Bounds.Top,
            Math.Max(0, nameX - FileSelectionColumnWidth), eventArgs.Bounds.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
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
        eventArgs.Graphics.Restore(nameClip);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            workflowVersions.TryGetValue(model.FullPath ?? string.Empty, out var workflowVersion) ? workflowVersion
                : model.IsStoredImport ? "随所属装配体" : model.IsInterconnectImport ? "随总装核对" : IsEmbedded(model) ? "随所属装配体" : model.BusinessVersionText,
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

    private void ExpandRootOnly()
    {
        foreach (TreeNode root in files.Nodes)
        {
            root.Expand();
            foreach (var node in EnumerateNodes(root).Where(node => node.Tag is BatchTreeOccurrence occurrence && IsEmbedded(occurrence.Model))) node.Expand();
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

    private static string ComputeFileHash(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
    }

    internal static string SelectionKey(CadTreeNode node) => !string.IsNullOrWhiteSpace(node?.FullPath)
        ? node.FullPath : node?.DocumentId?.ToString("D") ?? string.Empty;

    private static bool CanAcquire(CadTreeNode node) =>
        node.Status != CadReferenceStatus.Virtual
        && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
        && (node.DocumentId.HasValue || !string.IsNullOrWhiteSpace(node.FullPath))
        && (IsSupported(node.Kind) || node.IsImportedSourceFile);

    private static bool CanSelectForCheckIn(CadTreeNode node, string username) =>
        !node.IsReadOnlyPreview && !node.DrawingReviewLocked && CanAcquire(node)
        && File.Exists(node.FullPath)
        && (string.IsNullOrWhiteSpace(node.CheckedOutBy)
            || string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSelectForCheckIn(CadTreeNode node, string username) =>
        CanSelectForCheckIn(node, username)
        && (!node.DocumentId.HasValue || node.StoredVersionStateKnown && !node.HasStoredVersion);

    internal static bool IsEmbedded(CadTreeNode node) => node.Status == CadReferenceStatus.Virtual
        || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath);

    private static bool IsSupported(CadDocumentKind kind) =>
        kind == CadDocumentKind.Assembly || kind == CadDocumentKind.Part || kind == CadDocumentKind.Drawing;

    private string StateText(CadTreeNode node, string username)
    {
        if (workflowStates.TryGetValue(node.FullPath ?? string.Empty, out var workflowState)) return workflowState;
        if (existingReferenceOwners.ContainsKey(SelectionKey(node))) return "历史已有引用待补全（未纳入本次修改）";
        if (!string.IsNullOrWhiteSpace(node.DrawingUpdatePendingReason)) return checkIn.Checked && !ArchiveToStandardLibrary
            && SelectionStateFor(node) == ArchiveSelectionState.DrawingUpdate ? ArchiveSelectionText(node) : "关联工程图待更新";
        if (node.IsStoredImport) return "导入组件·随父装配保存";
        if (node.IsInterconnectImport) return "导入组件·待确认保存归属";
        if (IsEmbedded(node)) return "嵌入式·随父装配保存";
        if (node.IsLatestReadOnlyPreview) return "最新只读";
        if (node.IsHistoricalPreview) return "历史预览（只读）";
        if (checkIn.Checked && !ArchiveToStandardLibrary) return ArchiveSelectionText(node);
        if (!node.DocumentId.HasValue) return "未入库（提交时登记）";
        if (string.IsNullOrWhiteSpace(node.CheckedOutBy)) return node.IsModifiedInSolidWorks
            ? "有未保存内容（提交前核对）"
            : node.Status == CadReferenceStatus.Lightweight
                ? "轻量化·未在编辑（可勾选分析）"
                : "未在编辑（可勾选分析）";
        if (node.CheckoutSessionLost && string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
        {
            return "编辑权限失效（提交时恢复）";
        }
        return string.Equals(node.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            ? "当前用户正在编辑"
            : string.Concat(DisplayUserName(node.CheckedOutBy), "正在编辑");
    }

    private string DisplayUserName(string username)
    {
        var value = username?.Trim() ?? string.Empty;
        return userDisplayNames.TryGetValue(value, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : value;
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
