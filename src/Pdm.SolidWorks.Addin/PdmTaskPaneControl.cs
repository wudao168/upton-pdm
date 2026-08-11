using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmTaskPaneControl : UserControl
{
    private const int ActionButtonWidth = 88;
    private static readonly Color InputBorderColor = Color.FromArgb(122, 122, 122);

    private readonly Label serviceStatus = new Label();
    private readonly ComboBox projectSelector = new ComboBox();
    private readonly TextBox searchBox = new TextBox();
    private readonly TreeView structureTree = new TreeView();
    private readonly Label selectedFile = new Label();
    private readonly Label selectedMeta = new Label();
    private readonly Button loginButton = new Button();
    private readonly Button checkoutButton = new Button();
    private readonly Button checkinButton = new Button();
    private CadTreeNode rootNode;

    public PdmTaskPaneControl()
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(250, 420);
        Font = new Font("Microsoft YaHei UI", 8.5F);
        BackColor = Color.FromArgb(244, 247, 251);

        var header = BuildHeader();
        var projectPanel = BuildProjectPanel();
        var tabs = BuildTabs();
        Controls.Add(tabs);
        Controls.Add(projectPanel);
        Controls.Add(header);
    }

    public event EventHandler LoginRequested;
    public event EventHandler RefreshRequested;
    public event EventHandler ProjectChanged;
    public event EventHandler<CadTreeNodeEventArgs> NodeSelected;
    public event EventHandler<CadTreeNodeEventArgs> OpenRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckoutRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckInRequested;

    public Guid? SelectedProjectId => projectSelector.SelectedItem is ProjectDto project ? project.Id : (Guid?)null;

    public CadTreeNode SelectedNode => structureTree.SelectedNode?.Tag as CadTreeNode;

    public void SetConnectionState(bool online, string text)
    {
        RunOnUiThread(() =>
        {
            serviceStatus.Text = online ? "●" : "○";
            serviceStatus.AccessibleDescription = text;
            serviceStatus.ForeColor = online ? Color.FromArgb(72, 210, 186) : Color.FromArgb(255, 184, 86);
        });
    }

    public void SetAuthenticatedUser(string displayName)
    {
        RunOnUiThread(() => loginButton.Text = string.IsNullOrWhiteSpace(displayName) ? "登录" : displayName);
    }

    public void SetProjects(IReadOnlyList<ProjectDto> projects)
    {
        RunOnUiThread(() =>
        {
            projectSelector.BeginUpdate();
            projectSelector.Items.Clear();
            foreach (var project in projects)
            {
                projectSelector.Items.Add(project);
            }

            if (projectSelector.Items.Count > 0)
            {
                projectSelector.SelectedIndex = 0;
            }

            projectSelector.EndUpdate();
        });
    }

    public void SetTree(CadTreeNode root)
    {
        rootNode = root;
        RunOnUiThread(() => RebuildTree(searchBox.Text));
    }

    public void ClearTree()
    {
        rootNode = null;
        RunOnUiThread(() =>
        {
            structureTree.Nodes.Clear();
            UpdateSelected(null);
        });
    }

    public void SelectByComponentName(string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            var match = FindTreeNode(structureTree.Nodes, node => node.Tag is CadTreeNode model && string.Equals(model.ComponentSelectionName, componentName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                structureTree.SelectedNode = match;
                match.EnsureVisible();
            }
        });
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Color.FromArgb(31, 49, 72), Padding = new Padding(12, 8, 9, 8) };
        var logo = new Label
        {
            Text = "P",
            ForeColor = Color.White,
            BackColor = Color.FromArgb(36, 170, 168),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14F),
            Location = new Point(10, 10),
            Size = new Size(38, 38)
        };
        var title = new Label { Text = "UPTON PDM", ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 10F), Location = new Point(58, 9), AutoSize = true };
        var subtitle = new Label { Text = "SolidWorks 插件", ForeColor = Color.FromArgb(184, 201, 220), Location = new Point(58, 32), AutoSize = true };
        serviceStatus.Text = "○";
        serviceStatus.AccessibleName = "PDM连接状态";
        serviceStatus.AccessibleDescription = "未连接";
        serviceStatus.ForeColor = Color.FromArgb(255, 184, 86);
        serviceStatus.Font = new Font("Segoe UI Symbol", 14F);
        serviceStatus.TextAlign = ContentAlignment.MiddleCenter;
        serviceStatus.Size = new Size(22, 22);
        serviceStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        serviceStatus.Location = new Point(header.ClientSize.Width - header.Padding.Right - serviceStatus.Width, 18);
        header.Controls.Add(logo);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(serviceStatus);
        return header;
    }

    private Control BuildProjectPanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.White, Padding = new Padding(12, 7, 12, 8) };
        var label = new Label { Text = "当前项目", Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(90, 107, 128) };
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var userButtonColumn = new ColumnStyle(SizeType.Absolute, ActionButtonWidth);
        actions.ColumnStyles.Add(userButtonColumn);
        actions.SizeChanged += (_, _) => MatchProjectButtonWidth(actions, userButtonColumn);
        projectSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        projectSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        projectSelector.DrawMode = DrawMode.OwnerDrawFixed;
        projectSelector.ItemHeight = 24;
        projectSelector.DrawItem += DrawProjectItem;
        projectSelector.SelectedIndexChanged += (_, _) => ProjectChanged?.Invoke(this, EventArgs.Empty);
        loginButton.Text = "登录";
        loginButton.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        loginButton.FlatStyle = FlatStyle.Flat;
        loginButton.FlatAppearance.BorderColor = InputBorderColor;
        loginButton.FlatAppearance.BorderSize = 1;
        loginButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        MatchButtonHeight(projectSelector, loginButton);
        actions.Controls.Add(projectSelector, 0, 0);
        actions.Controls.Add(loginButton, 1, 0);
        panel.Controls.Add(actions);
        panel.Controls.Add(label);
        return panel;
    }

    private Control BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 6) };
        tabs.TabPages.Add(BuildStructureTab());
        tabs.TabPages.Add(BuildCurrentFileTab());
        tabs.TabPages.Add(new TabPage("版本记录") { BackColor = Color.White });
        tabs.TabPages.Add(new TabPage("待办") { BackColor = Color.White });
        return tabs;
    }

    private TabPage BuildStructureTab()
    {
        var tab = new TabPage("结构树") { BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(8) };
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(3) };
        ConfigureStructureActionColumns(actions);
        actions.SizeChanged += (_, _) => ConfigureStructureActionColumns(actions);

        var open = StructureToolbarButton("打开");
        checkoutButton.Text = "获取权限";
        ConfigureStructureToolbarButton(checkoutButton);
        var version = StructureToolbarButton("版本");
        var refresh = StructureToolbarButton("刷新");
        open.Click += (_, _) => RaiseSelected(OpenRequested);
        checkoutButton.Click += (_, _) => RaiseSelected(CheckoutRequested);
        version.Click += (_, _) => MessageBox.Show(this, "完整版本列表将在Windows客户端打开。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(open, 0, 0);
        actions.Controls.Add(checkoutButton, 2, 0);
        actions.Controls.Add(version, 4, 0);
        actions.Controls.Add(refresh, 6, 0);

        var searchToolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        searchToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBox.AutoSize = false;
        searchBox.Dock = DockStyle.Fill;
        searchBox.Margin = new Padding(3, 4, 3, 4);
        searchBox.AccessibleName = "搜索图号、名称或文件名";
        searchBox.TextChanged += (_, _) => RebuildTree(searchBox.Text);
        searchToolbar.Controls.Add(searchBox, 0, 0);

        structureTree.Dock = DockStyle.Fill;
        structureTree.BorderStyle = BorderStyle.FixedSingle;
        structureTree.HideSelection = false;
        structureTree.FullRowSelect = true;
        structureTree.AfterSelect += (_, eventArgs) =>
        {
            var node = eventArgs.Node.Tag as CadTreeNode;
            UpdateSelected(node);
            if (node != null)
            {
                NodeSelected?.Invoke(this, new CadTreeNodeEventArgs(node));
            }
        };
        structureTree.NodeMouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Node.Tag is CadTreeNode node)
            {
                OpenRequested?.Invoke(this, new CadTreeNodeEventArgs(node));
            }
        };

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3) };
        treeHost.Controls.Add(structureTree);

        var detail = new Panel { Dock = DockStyle.Bottom, Height = 55, BackColor = Color.White, Padding = new Padding(9, 7, 9, 5) };
        selectedFile.Text = "未选择图档";
        selectedFile.Dock = DockStyle.Top;
        selectedFile.Height = 22;
        selectedFile.Font = new Font(Font, FontStyle.Bold);
        selectedMeta.Text = "配置、版本和编辑状态";
        selectedMeta.Dock = DockStyle.Top;
        selectedMeta.Height = 19;
        selectedMeta.ForeColor = Color.FromArgb(111, 128, 149);
        detail.Controls.Add(selectedMeta);
        detail.Controls.Add(selectedFile);

        tab.Controls.Add(treeHost);
        tab.Controls.Add(detail);
        tab.Controls.Add(searchToolbar);
        tab.Controls.Add(actions);
        return tab;
    }

    private static void MatchButtonHeight(Control input, Button button)
    {
        button.Height = input.Height;
        input.SizeChanged += (_, _) => button.Height = input.Height;
    }

    private void MatchProjectButtonWidth(TableLayoutPanel actions, ColumnStyle userButtonColumn)
    {
        var buttonWidth = Math.Max(60, (ClientSize.Width - 48) / 4);
        userButtonColumn.Width = buttonWidth + loginButton.Margin.Horizontal;
    }

    private void DrawProjectItem(object sender, DrawItemEventArgs eventArgs)
    {
        eventArgs.DrawBackground();
        if (eventArgs.Index >= 0)
        {
            var bounds = new Rectangle(eventArgs.Bounds.X + 3, eventArgs.Bounds.Y, Math.Max(0, eventArgs.Bounds.Width - 6), eventArgs.Bounds.Height);
            TextRenderer.DrawText(
                eventArgs.Graphics,
                projectSelector.GetItemText(projectSelector.Items[eventArgs.Index]),
                projectSelector.Font,
                bounds,
                eventArgs.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        eventArgs.DrawFocusRectangle();
    }

    private TabPage BuildCurrentFileTab()
    {
        var tab = new TabPage("当前文件") { BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(10) };
        var description = new Label
        {
            Text = "提交存档并生成不可变结构快照。存在缺失引用时不能提交存档。",
            Dock = DockStyle.Top,
            Height = 62,
            ForeColor = Color.FromArgb(88, 105, 126)
        };
        checkinButton.Text = "提交存档";
        checkinButton.Dock = DockStyle.Top;
        checkinButton.Height = 38;
        checkinButton.BackColor = Color.FromArgb(21, 126, 77);
        checkinButton.ForeColor = Color.White;
        checkinButton.FlatStyle = FlatStyle.Flat;
        checkinButton.FlatAppearance.BorderSize = 0;
        checkinButton.Click += (_, _) => RaiseSelected(CheckInRequested);
        tab.Controls.Add(checkinButton);
        tab.Controls.Add(description);
        return tab;
    }

    private void RebuildTree(string filter)
    {
        if (rootNode == null)
        {
            return;
        }

        var selectedInstancePath = SelectedNode?.InstancePath;
        structureTree.BeginUpdate();
        structureTree.Nodes.Clear();
        var root = BuildTreeNode(rootNode, filter?.Trim());
        if (root != null)
        {
            structureTree.Nodes.Add(root);
            root.Expand();
        }

        structureTree.EndUpdate();
        if (!string.IsNullOrWhiteSpace(selectedInstancePath))
        {
            var selected = FindTreeNode(
                structureTree.Nodes,
                node => node.Tag is CadTreeNode model && string.Equals(model.InstancePath, selectedInstancePath, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                structureTree.SelectedNode = selected;
            }
        }
    }

    private static TreeNode BuildTreeNode(CadTreeNode model, string filter)
    {
        var childNodes = model.Children.Select(child => BuildTreeNode(child, filter)).Where(node => node != null).ToArray();
        var selfMatches = string.IsNullOrWhiteSpace(filter)
            || model.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            || model.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        if (!selfMatches && childNodes.Length == 0)
        {
            return null;
        }

        var status = model.Status == CadReferenceStatus.Normal ? string.Empty : string.Concat(" · ", StatusText(model.Status));
        var version = string.IsNullOrWhiteSpace(model.Revision) ? string.Empty : string.Concat("  ", model.Revision);
        var text = string.Concat(Path.GetFileNameWithoutExtension(model.FileName), " · ", model.DisplayName, version, status);
        var node = new TreeNode(text) { Tag = model, ToolTipText = string.Concat(model.FileName, "\r\n配置：", model.Configuration) };
        if (model.Status == CadReferenceStatus.Missing)
        {
            node.ForeColor = Color.FromArgb(197, 74, 68);
        }
        else if (!string.IsNullOrWhiteSpace(model.CheckedOutBy))
        {
            node.ForeColor = Color.FromArgb(47, 109, 224);
        }

        node.Nodes.AddRange(childNodes);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            node.Expand();
        }

        return node;
    }

    private void UpdateSelected(CadTreeNode node)
    {
        if (node == null)
        {
            selectedFile.Text = "未选择图档";
            selectedMeta.Text = "配置、版本和编辑状态";
            return;
        }

        selectedFile.Text = node.FileName;
        selectedMeta.Text = string.Concat(
            "配置：", string.IsNullOrWhiteSpace(node.Configuration) ? "默认" : node.Configuration,
            "　版本：", string.IsNullOrWhiteSpace(node.Revision) ? "未归档" : node.Revision,
            string.IsNullOrWhiteSpace(node.CheckedOutBy) ? string.Empty : string.Concat("　编辑人员：", node.CheckedOutBy));
        checkoutButton.Text = string.IsNullOrWhiteSpace(node.CheckedOutBy) ? "获取权限" : "正在编辑";
    }

    private void RaiseSelected(EventHandler<CadTreeNodeEventArgs> handler)
    {
        var node = SelectedNode;
        if (node != null)
        {
            handler?.Invoke(this, new CadTreeNodeEventArgs(node));
        }
    }

    private static Button StructureToolbarButton(string text)
    {
        var button = new Button { Text = text };
        ConfigureStructureToolbarButton(button);
        return button;
    }

    private static void ConfigureStructureToolbarButton(Button button)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = InputBorderColor;
        button.FlatAppearance.BorderSize = 1;
    }

    private static void ConfigureStructureActionColumns(TableLayoutPanel actions)
    {
        const int standardGap = 6;
        var availableWidth = Math.Max(0, actions.ClientSize.Width - actions.Padding.Horizontal);
        var widthBeforeLastGap = Math.Max(0, availableWidth - standardGap * 3);
        var lastGap = standardGap + widthBeforeLastGap % 4;
        actions.ColumnStyles.Clear();
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, standardGap));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, standardGap));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lastGap));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static TreeNode FindTreeNode(TreeNodeCollection nodes, Func<TreeNode, bool> predicate)
    {
        foreach (TreeNode node in nodes)
        {
            if (predicate(node))
            {
                return node;
            }

            var child = FindTreeNode(node.Nodes, predicate);
            if (child != null)
            {
                return child;
            }
        }

        return null;
    }

    private static string StatusText(CadReferenceStatus status)
    {
        switch (status)
        {
            case CadReferenceStatus.Suppressed: return "抑制";
            case CadReferenceStatus.Hidden: return "隐藏";
            case CadReferenceStatus.Lightweight: return "轻量化";
            case CadReferenceStatus.Virtual: return "虚拟件";
            case CadReferenceStatus.Missing: return "缺失";
            default: return "正常";
        }
    }
}

internal sealed class CadTreeNodeEventArgs : EventArgs
{
    public CadTreeNodeEventArgs(CadTreeNode node) => Node = node;

    public CadTreeNode Node { get; }
}
