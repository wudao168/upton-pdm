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
    private static readonly Color InputBorderColor = Color.FromArgb(122, 122, 122);
    private readonly ProjectBrowserControl projectSelector = new ProjectBrowserControl(matchTaskPaneProjectLayout: true);
    private readonly TreeView tree = new TreeView();
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
        projectPanel.Controls.Add(projectSelector);
        projectPanel.Controls.Add(projectLabel);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(3)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var readOnly = CreateButton("只读打开最新");
        var edit = CreateButton("获取并编辑");
        readOnly.Click += (_, _) => Raise(ControlledOpenMode.LatestReadOnly);
        edit.Click += (_, _) => Raise(ControlledOpenMode.LatestEdit);
        actions.Controls.Add(readOnly, 0, 0);
        actions.Controls.Add(edit, 2, 0);

        tree.Dock = DockStyle.Fill;
        tree.BorderStyle = BorderStyle.FixedSingle;
        tree.HideSelection = false;
        tree.FullRowSelect = true;
        tree.BeforeExpand += (_, args) => Materialize(args.Node);
        tree.MouseDown += (_, args) =>
        {
            if (args.Button == MouseButtons.Right) tree.SelectedNode = tree.GetNodeAt(args.Location);
        };
        BuildContextMenu();
        tree.ContextMenuStrip = menu;

        empty.Dock = DockStyle.Fill;
        empty.Text = "选择项目后读取PDM受控图档";
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Color.FromArgb(111, 128, 149);

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3), BackColor = BackColor };
        treeHost.Controls.Add(tree);
        treeHost.Controls.Add(empty);

        Controls.Add(treeHost);
        Controls.Add(actions);
        Controls.Add(projectPanel);
        SetTree(null);
    }

    public event EventHandler<ControlledOpenEventArgs> OpenRequested;
    public event EventHandler<ProjectBrowseEventArgs> ProjectSelected;

    public Guid? SelectedProjectId => projectSelector.SelectedProjectId;

    public float BrowseButtonWidth
    {
        set => projectSelector.BrowseButtonWidth = value;
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
            node.Expand();
            tree.SelectedNode = node;
        }
        tree.EndUpdate();
        empty.Visible = root == null;
        tree.Visible = root != null;
    }

    private void BuildContextMenu()
    {
        menu.Items.Add("在SolidWorks中打开最新受控版（只读）", null, (_, _) => Raise(ControlledOpenMode.LatestReadOnly));
        menu.Items.Add("获取最新版本并编辑", null, (_, _) => Raise(ControlledOpenMode.LatestEdit));
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

    private static TreeNode CreateNode(CadTreeNode model)
    {
        var node = new TreeNode(string.Concat(model.DisplayName, "  ", string.IsNullOrWhiteSpace(model.Revision) ? "—" : model.Revision))
        {
            Tag = model,
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

    private sealed class LazyPlaceholder
    {
        public static readonly LazyPlaceholder Instance = new LazyPlaceholder();
        private LazyPlaceholder() { }
    }
}
