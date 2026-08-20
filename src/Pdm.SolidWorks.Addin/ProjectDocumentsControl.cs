using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal enum ControlledOpenMode
{
    LatestReadOnly,
    LatestReleased,
    LatestEdit,
    SpecificReadOnly,
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
    private readonly Button openLatest = CreateButton("打开最新");
    private readonly ColumnStyle browseButtonColumn = new ColumnStyle(SizeType.Absolute, 98);
    private readonly ColumnStyle openLatestColumn = new ColumnStyle(SizeType.Absolute, 98);
    private readonly TreeView tree = new TreeView();
    private readonly Panel treeSurface = new Panel();
    private readonly ImageList structureImages = PdmTaskPaneControl.BuildStructureImages();
    private readonly Label empty = new Label();
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private CadTreeNode root;

    public ProjectDocumentsControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(244, 247, 251);

        var projectPanel = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = BackColor, Padding = new Padding(0, 7, 0, 8) };
        var projectLabel = new Label
        {
            Text = "权限内项目",
            Dock = DockStyle.Top,
            Height = 21,
            ForeColor = Color.FromArgb(90, 107, 128),
            Padding = new Padding(3, 0, 0, 0)
        };
        projectSelector.Dock = DockStyle.Fill;
        projectSelector.Margin = Padding.Empty;
        projectSelector.SelectedProjectChanged += (_, _) =>
        {
            SetTree(null);
            if (projectSelector.SelectedProjectId.HasValue)
            {
                ProjectSelected?.Invoke(this, new ProjectBrowseEventArgs(projectSelector.SelectedProjectId.Value));
            }
        };
        openLatest.Dock = DockStyle.Fill;
        openLatest.Margin = Padding.Empty;
        openLatest.Click += (_, _) => Raise(ControlledOpenMode.LatestReadOnly);
        var projectActions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(3)
        };
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        projectActions.ColumnStyles.Add(browseButtonColumn);
        projectActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        projectActions.ColumnStyles.Add(openLatestColumn);
        projectActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        projectActions.Controls.Add(projectSelector, 0, 0);
        projectActions.SetColumnSpan(projectSelector, 3);
        projectActions.Controls.Add(openLatest, 4, 0);
        projectPanel.Controls.Add(projectActions);
        projectPanel.Controls.Add(projectLabel);

        tree.Dock = DockStyle.Fill;
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.BorderStyle = BorderStyle.None;
        tree.ImageList = structureImages;
        tree.ItemHeight = Math.Max(tree.ItemHeight, 20);
        tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        tree.DrawNode += DrawNode;
        tree.AfterSelect += (_, _) => UpdateOpenLatestState();
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
        empty.Text = "选择项目后读取PDM受控图档";
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Color.FromArgb(111, 128, 149);

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3), BackColor = BackColor };
        treeHost.Controls.Add(treeSurface);
        treeHost.Controls.Add(empty);

        Controls.Add(treeHost);
        Controls.Add(projectPanel);
        SetTree(null);
    }

    public event EventHandler<ControlledOpenEventArgs> OpenRequested;
    public event EventHandler<ProjectBrowseEventArgs> ProjectSelected;

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public float BrowseButtonWidth
    {
        set
        {
            projectSelector.BrowseButtonWidth = value;
            browseButtonColumn.Width = value;
            openLatestColumn.Width = value;
        }
    }

    public void SetProjects(IReadOnlyList<ProjectDto> projects)
    {
        projectSelector.SetProjects(projects);
        projectSelector.SelectProject(null);
        SetTree(null);
    }

    public void SetTree(CadTreeNode value)
    {
        root = value;
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
        UpdateOpenLatestState();
    }

    private void BuildContextMenu()
    {
        menu.Items.Add("在SolidWorks中打开最新受控版", null, (_, _) => Raise(ControlledOpenMode.LatestReadOnly));
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

    private void UpdateOpenLatestState()
    {
        var canOpen = tree.SelectedNode?.Tag is CadTreeNode node && node.DocumentId.HasValue;
        openLatest.Enabled = canOpen;
        openLatest.UseVisualStyleBackColor = false;
        openLatest.BackColor = canOpen ? Color.FromArgb(21, 126, 77) : Color.FromArgb(224, 228, 233);
        openLatest.ForeColor = canOpen ? Color.White : Color.FromArgb(145, 151, 159);
        openLatest.FlatAppearance.BorderColor = Color.FromArgb(31, 49, 72);
        openLatest.FlatAppearance.BorderSize = 1;
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
        GetColumns(tree.ClientSize.Width, out var nameX, out var versionX);
        using (var background = new SolidBrush(Color.FromArgb(242, 244, 247)))
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.FillRectangle(background, 0, 0, treeSurface.ClientSize.Width, 23);
            eventArgs.Graphics.DrawLine(border, 0, 22, treeSurface.ClientSize.Width, 22);
            eventArgs.Graphics.DrawLine(border, nameX, 0, nameX, treeSurface.ClientSize.Height);
            eventArgs.Graphics.DrawLine(border, versionX, 0, versionX, treeSurface.ClientSize.Height);
        }

        var textColor = Color.FromArgb(70, 82, 96);
        TextRenderer.DrawText(eventArgs.Graphics, "名称", Font, new Rectangle(5, 0, Math.Max(0, nameX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "当前版本 / 最新版本", Font, new Rectangle(nameX + 5, 0, Math.Max(0, versionX - nameX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(versionX + 5, 0, Math.Max(0, tree.ClientSize.Width - versionX - 9), 22), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawNode(object sender, DrawTreeNodeEventArgs eventArgs)
    {
        if (!(eventArgs.Node.Tag is CadTreeNode model))
        {
            return;
        }

        GetColumns(tree.ClientSize.Width, out var nameX, out var versionX);
        var selected = (eventArgs.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var background = selected ? SystemColors.Highlight : tree.BackColor;
        var foreground = selected ? SystemColors.HighlightText : tree.ForeColor;
        using (var brush = new SolidBrush(background))
        using (var border = new Pen(Color.FromArgb(228, 231, 235)))
        {
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(0, eventArgs.Bounds.Top, tree.ClientSize.Width, eventArgs.Bounds.Height));
            eventArgs.Graphics.DrawLine(border, nameX, eventArgs.Bounds.Top, nameX, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, versionX, eventArgs.Bounds.Top, versionX, eventArgs.Bounds.Bottom);
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
        var versionBounds = new Rectangle(nameX + 5, eventArgs.Bounds.Top, Math.Max(0, versionX - nameX - 9), eventArgs.Bounds.Height);
        var stateBounds = new Rectangle(versionX + 5, eventArgs.Bounds.Top, Math.Max(0, tree.ClientSize.Width - versionX - 9), eventArgs.Bounds.Height);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, eventArgs.Node.NodeFont ?? tree.Font, nameBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(eventArgs.Graphics, VersionText(model), tree.Font, versionBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(eventArgs.Graphics, StateText(model), tree.Font, stateBounds, foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

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

    private static void GetColumns(int width, out int nameX, out int versionX)
    {
        var usable = Math.Max(1, width);
        var versionWidth = Math.Max(116, Math.Min(172, usable * 31 / 100));
        var statusWidth = Math.Max(82, Math.Min(122, usable * 24 / 100));
        nameX = usable - versionWidth - statusWidth;
        versionX = usable - statusWidth;
    }

    private static string VersionText(CadTreeNode node)
    {
        var current = string.IsNullOrWhiteSpace(node.CurrentRevision) ? node.Revision : node.CurrentRevision;
        var latest = string.IsNullOrWhiteSpace(node.LatestRevision) ? node.Revision : node.LatestRevision;
        return string.Concat(string.IsNullOrWhiteSpace(current) ? "—" : current, " / ", string.IsNullOrWhiteSpace(latest) ? "—" : latest);
    }

    private static string StateText(CadTreeNode node)
    {
        if (node.Status == CadReferenceStatus.Missing) return "缺失";
        if (!node.DocumentId.HasValue) return "未入库";
        if (node.CheckoutSessionLost) return "权限失效";
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy)) return "编辑中";
        if (node.WorkState == CadWorkState.PendingCheckIn) return "待提交";
        if (node.WorkState == CadWorkState.ModifiedUnsaved) return "修改未保存";
        return "正常";
    }

    private sealed class LazyPlaceholder
    {
        public static readonly LazyPlaceholder Instance = new LazyPlaceholder();
        private LazyPlaceholder() { }
    }
}
