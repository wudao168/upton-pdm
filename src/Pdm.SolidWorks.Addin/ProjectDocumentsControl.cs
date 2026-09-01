using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal enum ControlledOpenMode
{
    LatestReadOnly,
    LatestReleased,
    LatestEdit,
    SpecificReadOnly,
    PropertyWriteback,
    Versions
}

internal sealed class ControlledOpenEventArgs : EventArgs
{
    public ControlledOpenEventArgs(CadTreeNode node, ControlledOpenMode mode, Guid? versionId = null, Guid? projectId = null)
    {
        Node = node;
        Mode = mode;
        VersionId = versionId;
        ProjectId = projectId;
    }

    public CadTreeNode Node { get; }
    public ControlledOpenMode Mode { get; }
    public Guid? VersionId { get; }
    public Guid? ProjectId { get; }
}

internal sealed class ProjectBrowseEventArgs : EventArgs
{
    public ProjectBrowseEventArgs(Guid projectId) => ProjectId = projectId;

    public Guid ProjectId { get; }
}

internal sealed class ProjectDocumentsControl : UserControl
{
    private const int TreeLeftPadding = 4;
    private const int TreeIndentWidth = 16;
    private const int ExpanderSize = 9;
    private static readonly Color InputBorderColor = Color.FromArgb(122, 122, 122);
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl();
    private readonly Button openLatest = CreateButton("查看最新版（只读）");
    private readonly Button openEdit = CreateButton("检出并编辑");
    private readonly TreeView tree = new TreeView();
    private readonly Panel treeSurface = new Panel();
    private readonly ImageList structureImages = PdmTaskPaneControl.BuildStructureImages();
    private readonly Label empty = new Label();
    private readonly Label selectedFile = new Label();
    private readonly Label selectedState = new Label();
    private readonly Label workspaceLocation = new Label();
    private readonly Label selectedHint = new Label();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly ToolTip toolTip = new ToolTip();
    private Func<string> workspaceRootResolver = Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot;
    private CadTreeNode root;
    private string authenticatedUsername = string.Empty;

    public ProjectDocumentsControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(244, 247, 251);

        var projectPanel = new Panel { Dock = DockStyle.Top, Height = 126, BackColor = BackColor, Padding = new Padding(3, 5, 3, 5) };
        var projectLabel = new Label
        {
            Text = "项目工作区",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(31, 49, 72),
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(1, 0, 0, 0)
        };
        var workflow = new Label
        {
            Text = "选择项目 → 选择图档 → 查看或检出编辑 → 保存后到“设计树”签入",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(90, 107, 128),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(1, 0, 0, 0)
        };
        toolTip.SetToolTip(workflow, workflow.Text);
        projectSelector.BrowseButtonText = "选择项目";
        projectSelector.Dock = DockStyle.Fill;
        projectSelector.Margin = Padding.Empty;
        projectSelector.SelectedProjectChanged += (_, _) =>
        {
            SetTree(null);
            UpdateWorkspaceLocation();
            if (projectSelector.SelectedProjectId.HasValue)
            {
                ProjectSelected?.Invoke(this, new ProjectBrowseEventArgs(projectSelector.SelectedProjectId.Value));
            }
        };
        openLatest.Dock = DockStyle.Fill;
        openLatest.Margin = Padding.Empty;
        openLatest.Click += (_, _) => Raise(ControlledOpenMode.LatestReadOnly);
        openEdit.Dock = DockStyle.Fill;
        openEdit.Margin = Padding.Empty;
        openEdit.Click += (_, _) => Raise(ControlledOpenMode.LatestEdit);
        var projectActions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 3, 0, 0),
            Padding = Padding.Empty
        };
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        projectActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        projectActions.Controls.Add(openLatest, 0, 0);
        projectActions.Controls.Add(openEdit, 2, 0);

        var projectLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        projectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        projectLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        projectLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        projectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        projectLayout.Controls.Add(projectLabel, 0, 0);
        projectLayout.Controls.Add(workflow, 0, 1);
        projectLayout.Controls.Add(projectSelector, 0, 2);
        projectLayout.Controls.Add(projectActions, 0, 3);
        projectPanel.Controls.Add(projectLayout);

        tree.Dock = DockStyle.Fill;
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.BorderStyle = BorderStyle.None;
        tree.ImageList = structureImages;
        tree.ItemHeight = Math.Max(tree.ItemHeight, 20);
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.DrawNode += DrawNode;
        tree.AfterSelect += (_, _) => UpdateSelectionState();
        tree.BeforeExpand += (_, args) => Materialize(args.Node);
        tree.MouseDown += (_, args) =>
        {
            var rowNode = GetNodeAtRow(args.Y);
            if (args.Button == MouseButtons.Left
                && rowNode != null
                && HasExpandableChildren(rowNode)
                && GetExpanderBounds(rowNode).Contains(args.Location))
            {
                if (rowNode.IsExpanded)
                {
                    rowNode.Collapse();
                }
                else
                {
                    rowNode.Expand();
                }
                tree.Invalidate();
                return;
            }

            if ((args.Button == MouseButtons.Left || args.Button == MouseButtons.Right) && rowNode != null)
            {
                tree.SelectedNode = rowNode;
            }
        };
        BuildContextMenu();
        tree.ContextMenuStrip = menu;
        VisibleChanged += (_, _) =>
        {
            if (Visible) RefreshLocalWorkspacePaths();
        };

        treeSurface.Dock = DockStyle.Fill;
        treeSurface.BorderStyle = BorderStyle.FixedSingle;
        treeSurface.BackColor = Color.White;
        treeSurface.Padding = new Padding(0, 23, 0, 0);
        treeSurface.Paint += DrawHeader;
        treeSurface.Resize += (_, _) =>
        {
            treeSurface.Invalidate();
            tree.Invalidate();
        };
        treeSurface.Controls.Add(tree);

        empty.Dock = DockStyle.Fill;
        empty.Text = "选择项目后读取PLM受控图档";
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Color.FromArgb(111, 128, 149);

        var selectionPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 87,
            BackColor = Color.White,
            Padding = new Padding(7, 3, 7, 3)
        };
        selectedFile.Dock = DockStyle.Top;
        selectedFile.Height = 21;
        selectedFile.Font = new Font(Font, FontStyle.Bold);
        selectedFile.ForeColor = Color.FromArgb(31, 49, 72);
        selectedFile.AutoEllipsis = true;
        selectedState.Dock = DockStyle.Top;
        selectedState.Height = 20;
        selectedState.AutoEllipsis = true;
        workspaceLocation.Dock = DockStyle.Top;
        workspaceLocation.Height = 19;
        workspaceLocation.ForeColor = Color.FromArgb(90, 107, 128);
        workspaceLocation.AutoEllipsis = true;
        selectedHint.Dock = DockStyle.Fill;
        selectedHint.ForeColor = Color.FromArgb(90, 107, 128);
        selectedHint.AutoEllipsis = true;
        selectedHint.TextAlign = ContentAlignment.MiddleLeft;
        selectionPanel.Controls.Add(selectedHint);
        selectionPanel.Controls.Add(workspaceLocation);
        selectionPanel.Controls.Add(selectedState);
        selectionPanel.Controls.Add(selectedFile);

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3), BackColor = BackColor };
        treeHost.Controls.Add(treeSurface);
        treeHost.Controls.Add(empty);

        Controls.Add(treeHost);
        Controls.Add(selectionPanel);
        Controls.Add(projectPanel);
        SetTree(null);
    }

    public event EventHandler<ControlledOpenEventArgs> OpenRequested;
    public event EventHandler<ProjectBrowseEventArgs> ProjectSelected;

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public void SetAuthenticatedUser(string username)
    {
        authenticatedUsername = username ?? string.Empty;
        UpdateSelectionState();
    }

    public float BrowseButtonWidth
    {
        set
        {
            projectSelector.BrowseButtonWidth = value;
        }
    }

    public void SetProjects(IReadOnlyList<ProjectDto> projects)
    {
        projectSelector.SetProjects(projects);
        projectSelector.SelectProject(null);
        UpdateWorkspaceLocation();
        SetTree(null);
    }

    public void SetTree(CadTreeNode value)
    {
        root = value;
        RefreshLocalWorkspacePaths(false);
        tree.BeginUpdate();
        tree.Nodes.Clear();
        if (root != null)
        {
            var node = CreateNode(root);
            tree.Nodes.Add(node);
            Materialize(node);
            node.Expand();
            tree.SelectedNode = node;
        }
        tree.EndUpdate();
        empty.Visible = root == null;
        treeSurface.Visible = root != null;
        UpdateSelectionState();
    }

    private void BuildContextMenu()
    {
        menu.Items.Add("在SolidWorks中打开最新受控版", null, (_, _) => Raise(ControlledOpenMode.LatestReadOnly));
        menu.Items.Add("获取编辑权限并在SolidWorks中打开", null, (_, _) => Raise(ControlledOpenMode.LatestEdit));
        menu.Items.Add("打开最新正式发布版（只读）", null, (_, _) => Raise(ControlledOpenMode.LatestReleased));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开指定历史版本...", null, (_, _) => Raise(ControlledOpenMode.Versions));
        menu.Opening += (_, args) =>
        {
            if (!(tree.SelectedNode?.Tag is CadTreeNode selected) || !selected.DocumentId.HasValue)
            {
                args.Cancel = true;
            }
        };
    }

    private void Raise(ControlledOpenMode mode)
    {
        if (tree.SelectedNode?.Tag is CadTreeNode node && node.DocumentId.HasValue)
        {
            OpenRequested?.Invoke(this, new ControlledOpenEventArgs(node, mode, projectId: SelectedProjectId));
        }
    }

    private void UpdateSelectionState()
    {
        var node = tree.SelectedNode?.Tag as CadTreeNode;
        var canOpen = node != null && node.DocumentId.HasValue;
        var checkedOutByMe = canOpen
            && !string.IsNullOrWhiteSpace(authenticatedUsername)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        var checkedOutByOther = canOpen
            && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && !checkedOutByMe;
        var canEdit = canOpen
            && !string.IsNullOrWhiteSpace(authenticatedUsername)
            && !checkedOutByOther
            && !node.DrawingReviewLocked;

        openLatest.Enabled = canOpen;
        openLatest.Text = "查看最新版（只读）";
        openLatest.UseVisualStyleBackColor = false;
        openLatest.BackColor = canOpen ? Color.FromArgb(21, 126, 77) : Color.FromArgb(224, 228, 233);
        openLatest.ForeColor = canOpen ? Color.White : Color.FromArgb(145, 151, 159);
        openLatest.FlatAppearance.BorderColor = Color.FromArgb(31, 49, 72);
        openLatest.FlatAppearance.BorderSize = 1;
        openEdit.Text = checkedOutByMe ? "继续编辑" : node?.CheckoutSessionLost == true ? "重新获取编辑权限" : "检出并编辑";
        openEdit.Enabled = canEdit;
        openEdit.UseVisualStyleBackColor = false;
        openEdit.BackColor = canEdit ? Color.FromArgb(31, 49, 72) : Color.FromArgb(224, 228, 233);
        openEdit.ForeColor = canEdit ? Color.White : Color.FromArgb(145, 151, 159);
        openEdit.FlatAppearance.BorderColor = Color.FromArgb(31, 49, 72);
        openEdit.FlatAppearance.BorderSize = 1;

        UpdateSelectionSummary(node, checkedOutByMe, checkedOutByOther);
        tree.Invalidate();
    }

    private void UpdateSelectionSummary(CadTreeNode node, bool checkedOutByMe, bool checkedOutByOther)
    {
        if (node == null)
        {
            selectedFile.Text = "当前选择：尚未选择图档";
            selectedState.Text = root == null ? "请先选择项目，系统将读取PLM图档" : "请在下方列表选择装配、零件或工程图";
            selectedState.ForeColor = Color.FromArgb(90, 107, 128);
            selectedHint.Text = "查看不占用编辑权限；修改时再检出并编辑";
            toolTip.SetToolTip(selectedHint, selectedHint.Text);
            return;
        }

        selectedFile.Text = string.Concat("当前选择：", string.IsNullOrWhiteSpace(node.DisplayName) ? node.FileName : node.DisplayName);
        var current = Revision(node.CurrentRevision, node.Revision);
        var latest = Revision(node.LatestRevision, node.Revision);
        if (node.IsExternalProvenance)
        {
            SetSelectionMessage("外部本地副本，不作为PLM工作文件", "请从项目工作区重新查看或检出后编辑", Color.FromArgb(174, 94, 0));
        }
        else if (node.Status == CadReferenceStatus.Missing)
        {
            SetSelectionMessage("本地引用缺失，不能直接打开", "请重新获取PLM最新版并检查装配引用", Color.FromArgb(188, 68, 35));
        }
        else if (!node.DocumentId.HasValue)
        {
            SetSelectionMessage("尚未纳入PLM", "该文件不能从项目工作区打开", Color.FromArgb(174, 94, 0));
        }
        else if (node.CheckoutSessionLost)
        {
            SetSelectionMessage("编辑权限已失效", "请先另存本地修改，再重新获取编辑权限", Color.FromArgb(188, 68, 35));
        }
        else if (checkedOutByOther)
        {
            SetSelectionMessage(string.Concat("当前由 ", node.CheckedOutBy, " 编辑"), "你可以查看最新版，但不能取得编辑权限", Color.FromArgb(174, 94, 0));
        }
        else if (checkedOutByMe)
        {
            SetSelectionMessage("已由你检出，可以继续编辑", "编辑完成后保存，并到“设计树”提交存档", Color.FromArgb(21, 126, 77));
        }
        else if (!string.Equals(current, latest, StringComparison.OrdinalIgnoreCase))
        {
            SetSelectionMessage(string.Concat("本地 ", current, "，PLM最新 ", latest), "打开时将获取最新版；要修改请选择“检出并编辑”", Color.FromArgb(174, 94, 0));
        }
        else if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            SetSelectionMessage(string.Concat("PLM最新版本 ", latest, "，本地尚未下载"), "点击打开后自动下载到项目工作区", Color.FromArgb(59, 104, 153));
        }
        else
        {
            SetSelectionMessage(string.Concat("本地 ", current, " = PLM最新 ", latest), "可以安全查看；修改时请选择“检出并编辑”", Color.FromArgb(21, 126, 77));
        }
    }

    private void UpdateWorkspaceLocation()
    {
        var project = projectSelector.SelectedProject;
        if (project == null || string.IsNullOrWhiteSpace(project.Code))
        {
            workspaceLocation.Text = "本地位置：选择项目后自动创建项目工作区";
            toolTip.SetToolTip(workspaceLocation, workspaceLocation.Text);
            return;
        }

        var path = ControlledWorkspaceManager.ProjectViewDirectory(
            workspaceRootResolver(),
            project.Code);
        workspaceLocation.Text = string.Concat("本地位置：", path);
        toolTip.SetToolTip(workspaceLocation, path);
    }

    private void RefreshLocalWorkspacePaths(bool updateUi = true)
    {
        var project = projectSelector.SelectedProject;
        if (root == null || project == null || string.IsNullOrWhiteSpace(project.Code))
        {
            return;
        }

        try
        {
            var directory = ControlledWorkspaceManager.ProjectViewDirectory(
                workspaceRootResolver(),
                project.Code);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var pathsByDocument = new Dictionary<Guid, string>();
            var ambiguousDocuments = new HashSet<Guid>();
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                Guid documentId;
                Guid? boundProjectId;
                if (PdmDocumentIdentityStore.TryRead(path, out documentId))
                {
                    boundProjectId = PdmDocumentIdentityStore.ReadProjectId(path);
                }
                else if (!PdmDocumentIdentityStore.TryReadProvenance(path, out documentId, out boundProjectId))
                {
                    continue;
                }

                if (documentId == Guid.Empty || (boundProjectId.HasValue && boundProjectId.Value != project.Id))
                {
                    continue;
                }
                if (pathsByDocument.TryGetValue(documentId, out var existing)
                    && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguousDocuments.Add(documentId);
                    continue;
                }
                pathsByDocument[documentId] = path;
            }
            foreach (var documentId in ambiguousDocuments) pathsByDocument.Remove(documentId);
            ApplyLocalWorkspacePaths(root, directory, pathsByDocument);
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException)
        {
            // Local status is advisory. A transient directory read must not block PLM actions.
        }

        if (updateUi)
        {
            UpdateSelectionState();
        }
    }

    private static void ApplyLocalWorkspacePaths(
        CadTreeNode node,
        string projectDirectory,
        IReadOnlyDictionary<Guid, string> pathsByDocument)
    {
        if (node == null) return;
        if (node.DocumentId.HasValue && pathsByDocument.TryGetValue(node.DocumentId.Value, out var path))
        {
            node.FullPath = path;
        }
        else if (IsPathWithinDirectory(node.FullPath, projectDirectory))
        {
            node.FullPath = string.Empty;
        }

        foreach (var child in node.Children)
        {
            ApplyLocalWorkspacePaths(child, projectDirectory, pathsByDocument);
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
            || exception is UnauthorizedAccessException
            || exception is ArgumentException
            || exception is NotSupportedException)
        {
            return false;
        }
    }

    private void SetSelectionMessage(string state, string hint, Color color)
    {
        selectedState.Text = state;
        selectedState.ForeColor = color;
        selectedHint.Text = hint;
        toolTip.SetToolTip(selectedState, state);
        toolTip.SetToolTip(selectedHint, hint);
    }

    private static Button CreateButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(31, 49, 72)
        };
        button.FlatAppearance.BorderColor = InputBorderColor;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            structureImages?.Dispose();
            toolTip?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static TreeNode CreateNode(CadTreeNode model)
    {
        var imageKey = PdmTaskPaneControl.StructureImageKey(model.Kind);
        var node = new TreeNode(string.IsNullOrWhiteSpace(model.DisplayName) ? model.FileName : model.DisplayName)
        {
            Tag = model,
            ImageKey = imageKey,
            SelectedImageKey = imageKey,
            ToolTipText = string.Concat(model.FileName, " · ", model.Configuration)
        };
        if (model.Children.Count > 0) node.Nodes.Add(new TreeNode { Tag = LazyPlaceholder.Instance });
        return node;
    }

    private static void Materialize(TreeNode parent)
    {
        if (!(parent.Tag is CadTreeNode model)
            || parent.Nodes.Count != 1
            || !ReferenceEquals(parent.Nodes[0].Tag, LazyPlaceholder.Instance))
        {
            return;
        }

        parent.Nodes.Clear();
        foreach (var child in model.Children) parent.Nodes.Add(CreateNode(child));
    }

    private void DrawHeader(object sender, PaintEventArgs eventArgs)
    {
        GetColumns(tree.ClientSize.Width, out var nameX, out var localX);
        using (var background = new SolidBrush(Color.FromArgb(242, 244, 247)))
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.FillRectangle(background, 0, 0, treeSurface.ClientSize.Width, 23);
            eventArgs.Graphics.DrawLine(border, 0, 22, treeSurface.ClientSize.Width, 22);
            eventArgs.Graphics.DrawLine(border, nameX, 0, nameX, treeSurface.ClientSize.Height);
            eventArgs.Graphics.DrawLine(border, localX, 0, localX, treeSurface.ClientSize.Height);
        }

        var textColor = Color.FromArgb(70, 82, 96);
        TextRenderer.DrawText(eventArgs.Graphics, "名称", Font, new Rectangle(5, 0, Math.Max(0, nameX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "本地状态", Font, new Rectangle(nameX + 5, 0, Math.Max(0, localX - nameX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, "PLM状态", Font, new Rectangle(localX + 5, 0, Math.Max(0, tree.ClientSize.Width - localX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawNode(object sender, DrawTreeNodeEventArgs eventArgs)
    {
        if (!(eventArgs.Node.Tag is CadTreeNode model))
        {
            return;
        }

        GetColumns(tree.ClientSize.Width, out var nameX, out var localX);
        var selected = (eventArgs.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var background = selected ? SystemColors.Highlight : tree.BackColor;
        var foreground = selected ? SystemColors.HighlightText : tree.ForeColor;
        using (var brush = new SolidBrush(background))
        using (var border = new Pen(Color.FromArgb(228, 231, 235)))
        {
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(0, eventArgs.Bounds.Top, tree.ClientSize.Width, eventArgs.Bounds.Height));
            eventArgs.Graphics.DrawLine(border, nameX, eventArgs.Bounds.Top, nameX, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, localX, eventArgs.Bounds.Top, localX, eventArgs.Bounds.Bottom);
        }

        var expanderBounds = GetExpanderBounds(eventArgs.Node);
        if (HasExpandableChildren(eventArgs.Node))
        {
            using (var pen = new Pen(selected ? SystemColors.HighlightText : Color.FromArgb(112, 126, 143)))
            {
                eventArgs.Graphics.DrawRectangle(pen, expanderBounds);
                var centerX = expanderBounds.Left + expanderBounds.Width / 2;
                var centerY = expanderBounds.Top + expanderBounds.Height / 2;
                eventArgs.Graphics.DrawLine(pen, expanderBounds.Left + 2, centerY, expanderBounds.Right - 2, centerY);
                if (!eventArgs.Node.IsExpanded)
                {
                    eventArgs.Graphics.DrawLine(pen, centerX, expanderBounds.Top + 2, centerX, expanderBounds.Bottom - 2);
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
        var localBounds = new Rectangle(nameX + 5, eventArgs.Bounds.Top, Math.Max(0, localX - nameX - 9), eventArgs.Bounds.Height);
        var plmBounds = new Rectangle(localX + 5, eventArgs.Bounds.Top, Math.Max(0, tree.ClientSize.Width - localX - 9), eventArgs.Bounds.Height);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, eventArgs.Node.NodeFont ?? tree.Font, nameBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(eventArgs.Graphics, LocalStateText(model), tree.Font, localBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(eventArgs.Graphics, PlmStateText(model), tree.Font, plmBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if ((eventArgs.State & TreeNodeStates.Focused) == TreeNodeStates.Focused)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, new Rectangle(0, eventArgs.Bounds.Top, tree.ClientSize.Width, eventArgs.Bounds.Height), foreground, background);
        }
    }

    private TreeNode GetNodeAtRow(int y)
    {
        for (var node = tree.TopNode; node != null; node = node.NextVisibleNode)
        {
            if (y >= node.Bounds.Top && y < node.Bounds.Bottom)
            {
                return node;
            }
        }
        return null;
    }

    private Rectangle GetExpanderBounds(TreeNode node)
    {
        var left = TreeLeftPadding + Math.Max(0, node?.Level ?? 0) * TreeIndentWidth;
        var top = (node?.Bounds.Top ?? 0) + Math.Max(0, ((node?.Bounds.Height ?? tree.ItemHeight) - ExpanderSize) / 2);
        return new Rectangle(left, top, ExpanderSize, ExpanderSize);
    }

    private static bool HasExpandableChildren(TreeNode node) => node != null && node.Nodes.Count > 0;

    private static void GetColumns(int width, out int nameX, out int localX)
    {
        var usable = Math.Max(1, width);
        var plmWidth = Math.Max(106, Math.Min(150, usable * 29 / 100));
        var localWidth = Math.Max(88, Math.Min(116, usable * 23 / 100));
        nameX = usable - localWidth - plmWidth;
        localX = usable - plmWidth;
    }

    private string LocalStateText(CadTreeNode node)
    {
        if (node.IsExternalProvenance) return "外部副本";
        if (node.Status == CadReferenceStatus.Missing) return "文件缺失";
        if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath)) return "未下载";
        if (node.WorkState == CadWorkState.ModifiedUnsaved) return "修改未保存";
        if (node.WorkState == CadWorkState.PendingCheckIn) return "待签入";
        if (node.WorkState == CadWorkState.Editable
            || (!string.IsNullOrWhiteSpace(authenticatedUsername)
                && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))) return "可编辑";
        return "只读缓存";
    }

    private string PlmStateText(CadTreeNode node)
    {
        if (node.Status == CadReferenceStatus.Missing) return "缺失";
        if (!node.DocumentId.HasValue) return "未入库";
        if (node.CheckoutSessionLost) return "权限失效";
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy))
        {
            return string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                ? "我正在编辑"
                : string.Concat(node.CheckedOutBy, "编辑");
        }
        var current = Revision(node.CurrentRevision, node.Revision);
        var latest = Revision(node.LatestRevision, node.Revision);
        return string.Equals(current, latest, StringComparison.OrdinalIgnoreCase)
            ? string.Concat("最新 ", latest)
            : string.Concat("需更新 ", current, "→", latest);
    }

    private static string Revision(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred)
            ? string.IsNullOrWhiteSpace(fallback) ? "—" : fallback
            : preferred;

    private sealed class LazyPlaceholder
    {
        public static readonly LazyPlaceholder Instance = new LazyPlaceholder();
        private LazyPlaceholder() { }
    }
}
