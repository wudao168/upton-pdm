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
    private readonly Panel structureTreeSurface = new Panel();
    private readonly Label selectedFile = new Label();
    private readonly Label selectedMeta = new Label();
    private readonly Button loginButton = new Button();
    private readonly Button checkoutButton = new Button();
    private readonly Button checkinButton = new Button();
    private readonly Button versionButton = new Button();
    private readonly TabControl tabs = new TabControl();
    private readonly ListView versionList = new ListView();
    private readonly Button openCurrentButton = new Button();
    private readonly Button openHistoryButton = new Button();
    private readonly Button compareVersionsButton = new Button();
    private readonly ToolTip actionToolTip = new ToolTip();
    private readonly ContextMenuStrip structureMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem contextOpen = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextGetLatest = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCheckout = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCheckIn = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextDiscardCheckout = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextVersions = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCompare = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextRefresh = new ToolStripMenuItem();
    private readonly Label contextHint = new Label();
    private readonly Font emphasizedNodeFont;
    private CadTreeNode rootNode;
    private string authenticatedUsername = string.Empty;

    public PdmTaskPaneControl()
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(250, 420);
        Font = new Font("Microsoft YaHei UI", 8.5F);
        emphasizedNodeFont = new Font(Font, FontStyle.Bold);
        BackColor = Color.FromArgb(244, 247, 251);

        var header = BuildHeader();
        var projectPanel = BuildProjectPanel();
        BuildTabs();
        Controls.Add(tabs);
        Controls.Add(projectPanel);
        Controls.Add(header);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            emphasizedNodeFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    public event EventHandler LoginRequested;
    public event EventHandler RefreshRequested;
    public event EventHandler ProjectChanged;
    public event EventHandler<CadTreeNodeEventArgs> NodeSelected;
    public event EventHandler<CadTreeNodeEventArgs> OpenRequested;
    public event EventHandler<CadTreeNodeEventArgs> GetLatestVersionRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckoutRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckInRequested;
    public event EventHandler<CadTreeNodeEventArgs> DiscardCheckoutRequested;
    public event EventHandler<CadTreeNodeEventArgs> VersionsRequested;
    public event EventHandler<DocumentVersionEventArgs> OpenHistoryRequested;
    public event EventHandler<VersionComparisonEventArgs> CompareVersionsRequested;

    public Guid? SelectedProjectId => projectSelector.SelectedItem is ProjectDto project ? project.Id : (Guid?)null;

    public CadTreeNode SelectedNode => structureTree.SelectedNode?.Tag as CadTreeNode;

    public void ShowVersions(IReadOnlyList<DocumentVersionDto> versions)
    {
        RunOnUiThread(() =>
        {
            versionList.BeginUpdate();
            versionList.Items.Clear();
            foreach (var version in versions)
            {
                var item = new ListViewItem(version.Revision?.Display ?? "-") { Tag = version };
                item.SubItems.Add(version.Status == 1 ? "正式" : "工作");
                item.SubItems.Add(version.CreatedBy ?? string.Empty);
                item.SubItems.Add(version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(version.ChangeNote ?? string.Empty);
                versionList.Items.Add(item);
            }
            versionList.EndUpdate();
            tabs.SelectedIndex = 1;
            UpdateVersionActions();
        });
    }

    public void SetConnectionState(bool online, string text)
    {
        RunOnUiThread(() =>
        {
            serviceStatus.Text = online ? "●" : "○";
            serviceStatus.AccessibleDescription = text;
            serviceStatus.ForeColor = online ? Color.FromArgb(72, 210, 186) : Color.FromArgb(255, 184, 86);
        });
    }

    public void SetAuthenticatedUser(string displayName, string username)
    {
        authenticatedUsername = username ?? string.Empty;
        RunOnUiThread(() =>
        {
            loginButton.Text = string.IsNullOrWhiteSpace(displayName) ? "登录" : displayName;
            if (rootNode == null)
            {
                UpdateSelected(SelectedNode);
            }
            else
            {
                RebuildTree(searchBox.Text);
            }
        });
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

    private void BuildTabs()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(10, 6);
        tabs.TabPages.Add(BuildStructureTab());
        tabs.TabPages.Add(BuildVersionsTab());
        tabs.TabPages.Add(new TabPage("待办") { BackColor = Color.White });
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
        versionButton.Text = "版本";
        ConfigureStructureToolbarButton(versionButton);
        checkinButton.Text = "提交存档";
        ConfigureStructureToolbarButton(checkinButton);
        checkinButton.BackColor = Color.FromArgb(21, 126, 77);
        checkinButton.ForeColor = Color.White;
        checkinButton.FlatAppearance.BorderColor = checkinButton.BackColor;
        checkinButton.FlatAppearance.BorderSize = 0;
        checkinButton.UseVisualStyleBackColor = false;
        open.Click += (_, _) => RaiseSelected(OpenRequested);
        checkoutButton.Click += (_, _) => RaiseSelected(CheckoutRequested);
        versionButton.Click += (_, _) => RaiseVersionsRequested();
        checkinButton.Click += (_, _) => RaiseSelected(CheckInRequested);
        actions.Controls.Add(open, 0, 0);
        actions.Controls.Add(checkoutButton, 2, 0);
        actions.Controls.Add(versionButton, 4, 0);
        actions.Controls.Add(checkinButton, 6, 0);

        var searchToolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        searchToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBox.AutoSize = false;
        searchBox.Dock = DockStyle.Fill;
        searchBox.Margin = new Padding(3, 4, 3, 4);
        searchBox.AccessibleName = "搜索图号、名称或文件名";
        searchBox.TextChanged += (_, _) => RebuildTree(searchBox.Text);
        searchToolbar.Controls.Add(searchBox, 0, 0);

        structureTree.Dock = DockStyle.Fill;
        structureTree.BorderStyle = BorderStyle.None;
        structureTree.HideSelection = false;
        structureTree.FullRowSelect = true;
        structureTree.ShowNodeToolTips = true;
        structureTree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        structureTree.DrawNode += DrawStructureNode;
        structureTreeSurface.Dock = DockStyle.Fill;
        structureTreeSurface.BorderStyle = BorderStyle.FixedSingle;
        structureTreeSurface.BackColor = Color.White;
        structureTreeSurface.Padding = new Padding(0, 23, 0, 0);
        structureTreeSurface.Paint += DrawStructureHeader;
        structureTreeSurface.Resize += (_, _) =>
        {
            structureTreeSurface.Invalidate();
            structureTree.Invalidate();
        };
        structureTreeSurface.Controls.Add(structureTree);
        BuildStructureContextMenu();
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
        structureTree.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right)
            {
                structureTree.SelectedNode = structureTree.GetNodeAt(eventArgs.Location);
            }
        };

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3) };
        treeHost.Controls.Add(structureTreeSurface);

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

    private void DrawStructureHeader(object sender, PaintEventArgs eventArgs)
    {
        var statusWidth = GetStatusColumnWidth();
        var dividerX = Math.Max(80, structureTreeSurface.ClientSize.Width - statusWidth);
        using (var background = new SolidBrush(Color.FromArgb(242, 244, 247)))
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.FillRectangle(background, 0, 0, structureTreeSurface.ClientSize.Width, 23);
            eventArgs.Graphics.DrawLine(border, 0, 22, structureTreeSurface.ClientSize.Width, 22);
            eventArgs.Graphics.DrawLine(border, dividerX, 0, dividerX, structureTreeSurface.ClientSize.Height);
        }

        TextRenderer.DrawText(eventArgs.Graphics, "结构", Font, new Rectangle(6, 0, Math.Max(0, dividerX - 10), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(dividerX + 5, 0, Math.Max(0, statusWidth - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawStructureNode(object sender, DrawTreeNodeEventArgs eventArgs)
    {
        var model = eventArgs.Node.Tag as CadTreeNode;
        var statusWidth = GetStatusColumnWidth();
        var dividerX = Math.Max(80, structureTree.ClientSize.Width - statusWidth);
        var selected = (eventArgs.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var background = selected ? SystemColors.Highlight : eventArgs.Node.BackColor.IsEmpty ? structureTree.BackColor : eventArgs.Node.BackColor;
        var structureColor = selected ? SystemColors.HighlightText : eventArgs.Node.ForeColor.IsEmpty ? structureTree.ForeColor : eventArgs.Node.ForeColor;
        var font = eventArgs.Node.NodeFont ?? structureTree.Font;
        using (var brush = new SolidBrush(background))
        using (var border = new Pen(Color.FromArgb(225, 228, 233)))
        {
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(0, eventArgs.Bounds.Top, structureTree.ClientSize.Width, eventArgs.Bounds.Height));
            eventArgs.Graphics.DrawLine(border, dividerX, eventArgs.Bounds.Top, dividerX, eventArgs.Bounds.Bottom);
        }

        var structureBounds = new Rectangle(eventArgs.Bounds.Left, eventArgs.Bounds.Top, Math.Max(0, dividerX - eventArgs.Bounds.Left - 4), eventArgs.Bounds.Height);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, font, structureBounds, structureColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (model != null)
        {
            var status = WorkStateText(model);
            var statusColor = selected ? SystemColors.HighlightText : WorkStateColor(model);
            var statusBounds = new Rectangle(dividerX + 5, eventArgs.Bounds.Top, Math.Max(0, statusWidth - 9), eventArgs.Bounds.Height);
            TextRenderer.DrawText(eventArgs.Graphics, status, model.WorkState == CadWorkState.None ? structureTree.Font : emphasizedNodeFont, statusBounds, statusColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if ((eventArgs.State & TreeNodeStates.Focused) == TreeNodeStates.Focused)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, new Rectangle(0, eventArgs.Bounds.Top, structureTree.ClientSize.Width, eventArgs.Bounds.Height), structureColor, background);
        }
    }

    private int GetStatusColumnWidth() => Math.Min(132, Math.Max(88, structureTreeSurface.ClientSize.Width / 3));

    private void BuildStructureContextMenu()
    {
        contextOpen.Text = "打开";
        contextGetLatest.Text = "获取最新版本";
        contextCheckout.Text = "获取权限";
        contextCheckIn.Text = "提交存档";
        contextDiscardCheckout.Text = "放弃编辑";
        contextVersions.Text = "版本记录";
        contextCompare.Text = "版本对比";
        contextRefresh.Text = "刷新结构树";
        contextHint.AutoSize = false;
        contextHint.Size = new Size(110, 58);
        contextHint.MinimumSize = contextHint.Size;
        contextHint.MaximumSize = contextHint.Size;
        contextHint.Padding = new Padding(5, 2, 5, 2);
        contextHint.TextAlign = ContentAlignment.MiddleLeft;
        contextHint.ForeColor = Color.FromArgb(111, 128, 149);
        contextHint.BackColor = SystemColors.Menu;

        contextOpen.Click += (_, _) => RaiseSelected(OpenRequested);
        contextGetLatest.Click += (_, _) => RaiseSelected(GetLatestVersionRequested);
        contextCheckout.Click += (_, _) => RaiseSelected(CheckoutRequested);
        contextCheckIn.Click += (_, _) => RaiseSelected(CheckInRequested);
        contextDiscardCheckout.Click += (_, _) => RaiseSelected(DiscardCheckoutRequested);
        contextVersions.Click += (_, _) => RaiseSelected(VersionsRequested);
        contextCompare.Click += (_, _) => RaiseSelected(VersionsRequested);
        contextRefresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        structureMenu.ShowItemToolTips = true;
        structureMenu.ShowImageMargin = false;
        structureMenu.ShowCheckMargin = false;
        var contextHintHost = new ToolStripControlHost(contextHint)
        {
            AutoSize = false,
            Size = contextHint.Size,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        structureMenu.Items.AddRange(new ToolStripItem[]
        {
            contextOpen,
            contextGetLatest,
            new ToolStripSeparator(),
            contextCheckout,
            contextCheckIn,
            contextDiscardCheckout,
            new ToolStripSeparator(),
            contextVersions,
            contextCompare,
            new ToolStripSeparator(),
            contextRefresh,
            new ToolStripSeparator(),
            contextHintHost
        });
        structureMenu.Opening += (_, eventArgs) =>
        {
            var node = SelectedNode;
            if (node == null)
            {
                eventArgs.Cancel = true;
                return;
            }

            UpdateStructureContextMenu(node);
        };
        structureTree.ContextMenuStrip = structureMenu;
    }

    private void UpdateStructureContextMenu(CadTreeNode node)
    {
        var localFileExists = !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath);
        var canOpenInSolidWorks = node.Kind == CadDocumentKind.Assembly
            || node.Kind == CadDocumentKind.Part
            || node.Kind == CadDocumentKind.Drawing;
        var registered = node.DocumentId.HasValue;
        var authenticated = !string.IsNullOrWhiteSpace(authenticatedUsername);
        var editing = !string.IsNullOrWhiteSpace(node.CheckedOutBy);
        var editingByCurrentUser = editing
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        var canRegister = !registered && localFileExists && canOpenInSolidWorks;

        SetContextState(
            contextOpen,
            localFileExists && canOpenInSolidWorks,
            !localFileExists ? "本地文件不存在或尚未保存" : "该文件类型不能在SolidWorks中直接打开");
        SetContextState(contextGetLatest, registered && authenticated, PdmActionReason(registered, authenticated));

        contextCheckout.Text = editing
            ? string.Concat("正在编辑（", node.CheckedOutBy, "）")
            : "获取权限";
        var checkoutReason = PdmActionReason(registered, authenticated);
        if (registered && authenticated && editing)
        {
            checkoutReason = editingByCurrentUser ? "您已获取该图档的编辑权限" : string.Concat("当前编辑人员：", node.CheckedOutBy);
        }
        if (canRegister && authenticated)
        {
            checkoutReason = "首次获取权限时将自动登记该图档";
        }
        SetContextState(contextCheckout, authenticated && !editing && (registered || canRegister), checkoutReason);

        var checkInReason = PdmActionReason(registered, authenticated);
        if (registered && authenticated && !editing)
        {
            checkInReason = "尚未获取该图档的编辑权限";
        }
        else if (registered && authenticated && editing && !editingByCurrentUser)
        {
            checkInReason = string.Concat("只有当前编辑人员", node.CheckedOutBy, "可以提交存档");
        }
        else if (registered && authenticated && editingByCurrentUser && !localFileExists)
        {
            checkInReason = "本地文件不存在，不能提交存档";
        }
        SetContextState(contextCheckIn, registered && authenticated && editingByCurrentUser && localFileExists, checkInReason);
        SetContextState(
            contextDiscardCheckout,
            registered && authenticated && editingByCurrentUser,
            registered && authenticated && editing && !editingByCurrentUser
                ? string.Concat("只有当前编辑人员", node.CheckedOutBy, "可以放弃编辑")
                : registered && authenticated ? "尚未获取该图档的编辑权限" : PdmActionReason(registered, authenticated));

        SetContextState(contextVersions, registered && authenticated, PdmActionReason(registered, authenticated));
        SetContextState(contextCompare, registered && authenticated, PdmActionReason(registered, authenticated));
        contextCompare.ToolTipText = contextCompare.Enabled
            ? "打开版本记录后选择两个版本进行对比"
            : contextCompare.ToolTipText;

        if (!registered)
        {
            contextHint.Text = "提示：该图档尚未入库，仅支持本地打开";
        }
        else if (!authenticated)
        {
            contextHint.Text = "提示：登录后可使用PDM操作";
        }
        else if (!localFileExists)
        {
            contextHint.Text = "提示：本地文件不存在，可获取最新版本";
        }
        else if (editing)
        {
            contextHint.Text = string.Concat("提示：", WorkStateText(node), "；编辑人员：", node.CheckedOutBy);
        }
        else
        {
            contextHint.Text = "提示：请选择需要执行的操作";
        }
    }

    private static string PdmActionReason(bool registered, bool authenticated)
    {
        if (!registered)
        {
            return "该图档尚未入库";
        }

        return authenticated ? string.Empty : "请先登录PDM";
    }

    private static void SetContextState(ToolStripMenuItem item, bool enabled, string disabledReason)
    {
        item.Enabled = enabled;
        item.ToolTipText = enabled ? string.Empty : disabledReason;
        item.AccessibleDescription = enabled ? item.Text : disabledReason;
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

    private TabPage BuildVersionsTab()
    {
        var tab = new TabPage("版本记录") { BackColor = Color.White, Padding = new Padding(8) };
        versionList.Dock = DockStyle.Fill;
        versionList.View = View.Details;
        versionList.FullRowSelect = true;
        versionList.MultiSelect = true;
        versionList.HideSelection = false;
        versionList.Columns.Add("版本", 62);
        versionList.Columns.Add("状态", 62);
        versionList.Columns.Add("创建人", 76);
        versionList.Columns.Add("时间", 118);
        versionList.Columns.Add("变更说明", 180);
        versionList.SelectedIndexChanged += (_, _) => UpdateVersionActions();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
        openCurrentButton.Text = "打开当前版本";
        openCurrentButton.AutoSize = true;
        openCurrentButton.Click += (_, _) => RaiseSelected(OpenRequested);
        openHistoryButton.Text = "只读打开历史版本";
        openHistoryButton.AutoSize = true;
        openHistoryButton.Click += (_, _) => RaiseOpenHistory();
        compareVersionsButton.Text = "发起版本对比";
        compareVersionsButton.AutoSize = true;
        compareVersionsButton.Click += (_, _) => RaiseVersionComparison();
        actions.Controls.Add(openCurrentButton);
        actions.Controls.Add(openHistoryButton);
        actions.Controls.Add(compareVersionsButton);
        tab.Controls.Add(versionList);
        tab.Controls.Add(actions);
        return tab;
    }

    private void UpdateVersionActions()
    {
        openCurrentButton.Enabled = SelectedNode != null;
        openHistoryButton.Enabled = versionList.SelectedItems.Count == 1;
        compareVersionsButton.Enabled = versionList.SelectedItems.Count == 2;
    }

    private void RaiseOpenHistory()
    {
        if (SelectedNode?.DocumentId is Guid documentId && versionList.SelectedItems.Count == 1 && versionList.SelectedItems[0].Tag is DocumentVersionDto version)
            OpenHistoryRequested?.Invoke(this, new DocumentVersionEventArgs(documentId, SelectedNode.FileName, version));
    }

    private void RaiseVersionComparison()
    {
        if (SelectedNode?.DocumentId is Guid documentId && versionList.SelectedItems.Count == 2 && versionList.SelectedItems[0].Tag is DocumentVersionDto left && versionList.SelectedItems[1].Tag is DocumentVersionDto right)
            CompareVersionsRequested?.Invoke(this, new VersionComparisonEventArgs(documentId, left.Id, right.Id));
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
            var relatedDrawings = BuildRelatedDrawingsGroup(filter?.Trim());
            if (relatedDrawings != null)
            {
                root.Nodes.Add(relatedDrawings);
            }

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

    private TreeNode BuildTreeNode(CadTreeNode model, string filter)
    {
        var childNodes = model.Children
            .Where(child => child.Kind != CadDocumentKind.Drawing)
            .Select(child => BuildTreeNode(child, filter))
            .Where(node => node != null)
            .ToArray();
        var selfMatches = MatchesFilter(model, filter)
            || ReferenceEquals(model, rootNode) && HasMatchingRelatedDrawing(filter);
        if (!selfMatches && childNodes.Length == 0)
        {
            return null;
        }

        var isMissing = model.Status == CadReferenceStatus.Missing;
        var status = model.Status == CadReferenceStatus.Normal || isMissing ? string.Empty : string.Concat(" · ", StatusText(model.Status));
        var version = string.IsNullOrWhiteSpace(model.Revision) ? string.Empty : string.Concat("  ", model.Revision);
        var editTip = isMissing
            ? "\r\n状态：文件缺失"
            : string.Concat("\r\n状态：", WorkStateText(model), string.IsNullOrWhiteSpace(model.CheckedOutBy) ? string.Empty : string.Concat("\r\n编辑人员：", model.CheckedOutBy));
        var text = string.Concat(Path.GetFileNameWithoutExtension(model.FileName), " · ", model.DisplayName, version, status);
        var node = new TreeNode(text)
        {
            Tag = model,
            ToolTipText = string.Concat(model.FileName, "\r\n配置：", model.Configuration, editTip)
        };
        if (isMissing)
        {
            node.ForeColor = Color.FromArgb(197, 74, 68);
            node.BackColor = Color.FromArgb(255, 235, 235);
            node.NodeFont = emphasizedNodeFont;
        }
        else if (model.WorkState == CadWorkState.ModifiedUnsaved)
        {
            node.ForeColor = Color.FromArgb(188, 68, 35);
            node.BackColor = Color.FromArgb(255, 235, 226);
            node.NodeFont = emphasizedNodeFont;
        }
        else if (model.WorkState == CadWorkState.PendingCheckIn)
        {
            node.ForeColor = Color.FromArgb(112, 65, 160);
            node.BackColor = Color.FromArgb(242, 233, 250);
            node.NodeFont = emphasizedNodeFont;
        }
        else if (model.WorkState == CadWorkState.Editable)
        {
            node.ForeColor = Color.FromArgb(21, 126, 77);
            node.BackColor = Color.FromArgb(228, 246, 235);
            node.NodeFont = emphasizedNodeFont;
        }
        else if (model.WorkState == CadWorkState.EditingByOther)
        {
            node.ForeColor = Color.FromArgb(174, 94, 0);
            node.BackColor = Color.FromArgb(255, 244, 214);
        }

        node.Nodes.AddRange(childNodes);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            node.Expand();
        }

        return node;
    }

    private static string WorkStateText(CadTreeNode node)
    {
        if (node.Status == CadReferenceStatus.Missing)
        {
            return "文件缺失";
        }

        switch (node.WorkState)
        {
            case CadWorkState.ModifiedUnsaved: return "修改未保存";
            case CadWorkState.PendingCheckIn: return "待提交";
            case CadWorkState.Editable: return "可编辑";
            case CadWorkState.EditingByOther: return string.IsNullOrWhiteSpace(node.CheckedOutBy) ? "他人编辑中" : string.Concat(node.CheckedOutBy, "编辑中");
            default: return node.Status == CadReferenceStatus.Normal ? string.Empty : StatusText(node.Status);
        }
    }

    private static Color WorkStateColor(CadTreeNode node)
    {
        if (node.Status == CadReferenceStatus.Missing)
        {
            return Color.FromArgb(197, 74, 68);
        }

        switch (node.WorkState)
        {
            case CadWorkState.ModifiedUnsaved: return Color.FromArgb(188, 68, 35);
            case CadWorkState.PendingCheckIn: return Color.FromArgb(112, 65, 160);
            case CadWorkState.Editable: return Color.FromArgb(21, 126, 77);
            case CadWorkState.EditingByOther: return Color.FromArgb(174, 94, 0);
            default: return Color.FromArgb(90, 107, 128);
        }
    }

    private TreeNode BuildRelatedDrawingsGroup(string filter)
    {
        var drawings = new List<CadTreeNode>();
        CollectRelatedDrawings(rootNode, drawings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var drawingNodes = drawings
            .Where(drawing => MatchesFilter(drawing, filter))
            .Select(drawing => BuildTreeNode(drawing, filter))
            .Where(node => node != null)
            .ToArray();
        if (drawingNodes.Length == 0)
        {
            return null;
        }

        var group = new TreeNode(string.Concat("关联图纸（", drawingNodes.Length, "）"))
        {
            ForeColor = Color.FromArgb(90, 107, 128),
            ToolTipText = "同名工程图集中显示，不参与SolidWorks组件顺序。"
        };
        group.Nodes.AddRange(drawingNodes);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            group.Expand();
        }

        return group;
    }

    private static void CollectRelatedDrawings(CadTreeNode node, ICollection<CadTreeNode> drawings, ISet<string> paths)
    {
        if (node == null)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            if (child.Kind == CadDocumentKind.Drawing)
            {
                var key = string.IsNullOrWhiteSpace(child.FullPath) ? child.InstancePath : child.FullPath;
                if (paths.Add(key))
                {
                    drawings.Add(child);
                }

                continue;
            }

            CollectRelatedDrawings(child, drawings, paths);
        }
    }

    private bool HasMatchingRelatedDrawing(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var drawings = new List<CadTreeNode>();
        CollectRelatedDrawings(rootNode, drawings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return drawings.Any(drawing => MatchesFilter(drawing, filter));
    }

    private static bool MatchesFilter(CadTreeNode model, string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || model.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
        || model.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private void UpdateSelected(CadTreeNode node)
    {
        if (node == null)
        {
            selectedFile.Text = "未选择图档";
            selectedMeta.Text = "配置、版本和编辑状态";
            checkoutButton.Enabled = false;
            checkinButton.Enabled = false;
            versionButton.Enabled = false;
            actionToolTip.SetToolTip(versionButton, "请先选择图档");
            return;
        }

        selectedFile.Text = node.FileName;
        var workStateText = WorkStateText(node);
        var editState = string.IsNullOrWhiteSpace(workStateText) ? string.Empty : string.Concat("　状态：", workStateText);
        selectedMeta.Text = string.Concat(
            "配置：", string.IsNullOrWhiteSpace(node.Configuration) ? "默认" : node.Configuration,
            "　版本：", string.IsNullOrWhiteSpace(node.Revision) ? "未归档" : node.Revision,
            editState);
        checkoutButton.Text = string.IsNullOrWhiteSpace(node.CheckedOutBy) ? "获取权限" : "正在编辑";
        var localFileExists = !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath);
        var canRegister = !node.DocumentId.HasValue
            && localFileExists
            && (node.Kind == CadDocumentKind.Assembly || node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Drawing);
        var authenticated = !string.IsNullOrWhiteSpace(authenticatedUsername);
        var editingByCurrentUser = authenticated
            && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        checkoutButton.Enabled = authenticated
            && string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && (node.DocumentId.HasValue || canRegister);
        checkinButton.Enabled = node.DocumentId.HasValue && editingByCurrentUser && localFileExists;
        actionToolTip.SetToolTip(
            checkoutButton,
            canRegister ? "首次获取权限时将自动登记该图档" : node.DocumentId.HasValue ? "获取该图档的独占编辑权限" : "本地文件不存在或文件类型不支持登记");
        actionToolTip.SetToolTip(
            checkinButton,
            !node.DocumentId.HasValue ? "该图档尚未登记，请先获取权限" : !editingByCurrentUser ? "只有当前编辑人员可以提交存档" : !localFileExists ? "本地文件不存在，不能提交存档" : "提交当前文件并生成新工作版本");
        var canReadVersions = node.DocumentId.HasValue && !string.IsNullOrWhiteSpace(authenticatedUsername);
        var versionReason = !node.DocumentId.HasValue ? "该图档尚未入库，暂无版本记录" : string.IsNullOrWhiteSpace(authenticatedUsername) ? "请先登录PDM" : "打开版本记录";
        versionButton.Enabled = canReadVersions;
        versionButton.AccessibleDescription = versionReason;
        actionToolTip.SetToolTip(versionButton, versionReason);
    }

    private void RaiseSelected(EventHandler<CadTreeNodeEventArgs> handler)
    {
        var node = SelectedNode;
        if (node != null)
        {
            handler?.Invoke(this, new CadTreeNodeEventArgs(node));
        }
    }

    private void RaiseVersionsRequested()
    {
        var node = SelectedNode;
        if (node == null) return;
        if (!node.DocumentId.HasValue)
        {
            MessageBox.Show(this, "该图档尚未入库，暂无版本记录", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        VersionsRequested?.Invoke(this, new CadTreeNodeEventArgs(node));
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

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // SolidWorks is closing and has already destroyed the task pane handle.
        }
        catch (InvalidOperationException)
        {
            // SolidWorks is closing and has already destroyed the task pane handle.
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

internal sealed class DocumentVersionEventArgs : EventArgs
{
    public DocumentVersionEventArgs(Guid documentId, string fileName, DocumentVersionDto version)
    {
        DocumentId = documentId;
        FileName = fileName;
        Version = version;
    }

    public Guid DocumentId { get; }
    public string FileName { get; }
    public DocumentVersionDto Version { get; }
}

internal sealed class VersionComparisonEventArgs : EventArgs
{
    public VersionComparisonEventArgs(Guid documentId, Guid leftVersionId, Guid rightVersionId)
    {
        DocumentId = documentId;
        LeftVersionId = leftVersionId;
        RightVersionId = rightVersionId;
    }

    public Guid DocumentId { get; }
    public Guid LeftVersionId { get; }
    public Guid RightVersionId { get; }
}
