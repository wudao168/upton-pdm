using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmTaskPaneControl : UserControl
{
    private static readonly Color InputBorderColor = Color.FromArgb(122, 122, 122);
    private static readonly Color SubmitAvailableColor = Color.FromArgb(47, 109, 224);
    private static readonly Color SubmitUnavailableColor = Color.FromArgb(217, 221, 227);
    private static readonly Color CheckoutAvailableColor = Color.FromArgb(230, 126, 34);
    private static readonly Color EditingColor = Color.FromArgb(21, 126, 77);

    private readonly Label serviceStatus = new Label();
    private readonly TextBox currentProject = new TextBox();
    private readonly TextBox searchBox = new TextBox();
    private readonly TreeView structureTree = new TreeView();
    private readonly Panel structureTreeSurface = new Panel();
    private readonly Label selectedFile = new Label();
    private readonly Label selectedMeta = new Label();
    private readonly Button loginButton = new Button();
    private readonly Button checkoutButton = new Button();
    private readonly Button checkinButton = new Button();
    private readonly Button batchOperationButton = new Button();
    private readonly TabControl tabs = new TabControl();
    private readonly ProjectDocumentsControl projectDocuments = new ProjectDocumentsControl();
    private TabPage structureTab;
    private TabPage versionsTab;
    private bool suppressVersionTabRequest;
    private readonly ListView versionList = new ListView();
    private readonly Button openCurrentButton = new Button();
    private readonly Button openHistoryButton = new Button();
    private readonly Button compareVersionsButton = new Button();
    private readonly ContextMenuStrip versionMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem versionContextGetSelected = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextGetLatest = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextOpenCurrent = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextCompare = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextRefresh = new ToolStripMenuItem();
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
    private readonly ImageList structureImages;
    private CadTreeNode rootNode;
    private string authenticatedUsername = string.Empty;
    private Guid? displayedVersionDocumentId;
    private string displayedVersionFileName = string.Empty;
    private int treeBuildGeneration;
    private TreeBuildPlan activeTreeBuildPlan;
    private string pendingComponentSelectionName = string.Empty;
    private bool suppressNodeSelectedNotification;
    private IReadOnlyList<ProjectDto> availableProjects = Array.Empty<ProjectDto>();
    private Guid? selectedProjectId;
    private bool projectContextAvailable;

    public PdmTaskPaneControl()
    {
        Dock = DockStyle.Fill;
        MinimumSize = new Size(250, 420);
        Font = new Font("Microsoft YaHei UI", 8.5F);
        emphasizedNodeFont = new Font(Font, FontStyle.Bold);
        structureImages = BuildStructureImages();
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
            Interlocked.Increment(ref treeBuildGeneration);
            CancelActiveTreeBuild();
            emphasizedNodeFont?.Dispose();
            structureImages?.Dispose();
        }

        base.Dispose(disposing);
    }

    public event EventHandler LoginRequested;
    public event EventHandler RefreshRequested;
    public event EventHandler<CadTreeNodeEventArgs> NodeSelected;
    public event EventHandler<CadTreeNodeEventArgs> OpenRequested;
    public event EventHandler<CadTreeNodeEventArgs> GetLatestVersionRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckoutRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckInRequested;
    public event EventHandler<CadTreeNodeEventArgs> DiscardCheckoutRequested;
    public event EventHandler BatchOperationRequested;
    public event EventHandler<CadTreeNodeEventArgs> VersionsRequested;
    public event EventHandler<DocumentVersionEventArgs> OpenHistoryRequested;
    public event EventHandler<VersionComparisonEventArgs> CompareVersionsRequested;
    public event EventHandler<ControlledOpenEventArgs> ControlledOpenRequested;
    public event EventHandler<ProjectBrowseEventArgs> ProjectBrowseRequested;

    public CadTreeNode SelectedNode => structureTree.SelectedNode?.Tag as CadTreeNode;

    public void SetProjectTree(Guid projectId, CadTreeNode root) => RunOnUiThread(() =>
    {
        if (projectDocuments.SelectedProjectId == projectId) projectDocuments.SetTree(root);
    });

    public void ShowStructureTab() => RunOnUiThread(() =>
    {
        if (structureTab != null) tabs.SelectedTab = structureTab;
    });

    public void ShowVersions(Guid documentId, string fileName, IReadOnlyList<DocumentVersionDto> versions)
    {
        RunOnUiThread(() =>
        {
            displayedVersionDocumentId = documentId;
            displayedVersionFileName = fileName ?? string.Empty;
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
            suppressVersionTabRequest = true;
            try
            {
                tabs.SelectedTab = versionsTab;
            }
            finally
            {
                suppressVersionTabRequest = false;
            }
            UpdateVersionActions();
        });
    }

    public void SetConnectionState(bool online, string text)
    {
        RunOnUiThread(() =>
        {
            serviceStatus.Text = online ? "● ● ●" : "○ ○ ○";
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
            loginButton.AccessibleDescription = string.IsNullOrWhiteSpace(username)
                ? loginButton.Text
                : string.Concat(loginButton.Text, "（", username, "）");
            actionToolTip.SetToolTip(loginButton, loginButton.AccessibleDescription);
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
            availableProjects = projects ?? Array.Empty<ProjectDto>();
            if (selectedProjectId.HasValue && availableProjects.All(project => project.Id != selectedProjectId.Value))
            {
                selectedProjectId = null;
            }
            projectDocuments.SetProjects(availableProjects);
            UpdateCurrentProjectDisplay();
        });
    }

    public void SelectProject(Guid? projectId)
    {
        RunOnUiThread(() =>
        {
            selectedProjectId = projectId;
            UpdateCurrentProjectDisplay();
        });
    }

    public void SetProjectContextAvailable(bool available)
    {
        RunOnUiThread(() =>
        {
            projectContextAvailable = available;
            if (!available)
            {
                selectedProjectId = null;
            }
            UpdateCurrentProjectDisplay();
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
            Interlocked.Increment(ref treeBuildGeneration);
            CancelActiveTreeBuild();
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
            if (TrySelectComponentNode(componentName))
            {
                pendingComponentSelectionName = string.Empty;
                return;
            }

            pendingComponentSelectionName = componentName;
            if (!string.IsNullOrWhiteSpace(searchBox.Text))
            {
                searchBox.Clear();
            }
        });
    }

    public void SelectRootNode()
    {
        RunOnUiThread(() =>
        {
            if (structureTree.Nodes.Count > 0)
            {
                SelectTreeNodeWithoutNotification(structureTree.Nodes[0]);
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
        serviceStatus.Text = "○ ○ ○";
        serviceStatus.AccessibleName = "PDM连接状态";
        serviceStatus.AccessibleDescription = "未连接";
        serviceStatus.ForeColor = Color.FromArgb(255, 184, 86);
        serviceStatus.Font = new Font("Segoe UI Symbol", 11.2F);
        serviceStatus.TextAlign = ContentAlignment.MiddleCenter;
        serviceStatus.Size = new Size(48, 22);
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
        var panel = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Color.White, Padding = new Padding(12, 7, 12, 8) };
        var label = new Label { Text = "当前项目", Dock = DockStyle.Top, Height = 21, ForeColor = Color.FromArgb(90, 107, 128) };
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        currentProject.ReadOnly = true;
        currentProject.TabStop = false;
        currentProject.ShortcutsEnabled = false;
        currentProject.Dock = DockStyle.Fill;
        currentProject.Margin = new Padding(0, 1, 0, 3);
        currentProject.BackColor = Color.White;
        currentProject.AccessibleName = "当前项目号（只读）";
        loginButton.Text = "登录";
        loginButton.Dock = DockStyle.Fill;
        loginButton.Margin = new Padding(0, 3, 0, 0);
        loginButton.AutoSize = false;
        loginButton.AutoEllipsis = false;
        loginButton.TextAlign = ContentAlignment.MiddleCenter;
        loginButton.UseCompatibleTextRendering = false;
        loginButton.FlatStyle = FlatStyle.Flat;
        loginButton.FlatAppearance.BorderColor = InputBorderColor;
        loginButton.FlatAppearance.BorderSize = 1;
        loginButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(currentProject, 0, 0);
        actions.Controls.Add(loginButton, 0, 1);
        panel.Controls.Add(actions);
        panel.Controls.Add(label);
        UpdateCurrentProjectDisplay();
        return panel;
    }

    private void UpdateCurrentProjectDisplay()
    {
        var project = selectedProjectId.HasValue
            ? availableProjects.FirstOrDefault(item => item.Id == selectedProjectId.Value)
            : null;
        currentProject.Text = project?.Code ?? (projectContextAvailable ? "未关联" : "未打开图档");
    }

    private void BuildTabs()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(10, 6);
        structureTab = BuildStructureTab();
        tabs.TabPages.Add(structureTab);
        var projectTab = new TabPage("项目图档") { BackColor = Color.White, Padding = new Padding(4) };
        projectDocuments.OpenRequested += (_, args) => ControlledOpenRequested?.Invoke(this, args);
        projectDocuments.ProjectSelected += (_, args) => ProjectBrowseRequested?.Invoke(this, args);
        projectTab.Controls.Add(projectDocuments);
        tabs.TabPages.Add(projectTab);
        versionsTab = BuildVersionsTab();
        tabs.TabPages.Add(versionsTab);
        tabs.TabPages.Add(new TabPage("待办") { BackColor = Color.White });
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (!suppressVersionTabRequest && tabs.SelectedTab == versionsTab)
            {
                RaiseVersionsRequested();
            }
        };
    }

    private TabPage BuildStructureTab()
    {
        var tab = new TabPage("结构树") { BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(8) };
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(3) };
        ConfigureStructureActionColumns(actions);
        actions.SizeChanged += (_, _) => ConfigureStructureActionColumns(actions);

        var open = StructureToolbarButton("受控打开");
        checkoutButton.Text = "获取权限";
        ConfigureStructureToolbarButton(checkoutButton);
        checkinButton.Text = "提交存档";
        ConfigureStructureToolbarButton(checkinButton);
        ApplyCheckinButtonAppearance(false);
        open.Click += (_, _) => RaiseSelected(OpenRequested);
        checkoutButton.Click += (_, _) => RaiseSelected(CheckoutRequested);
        checkinButton.Click += (_, _) => RaiseSelected(CheckInRequested);
        actions.Controls.Add(open, 0, 0);
        actions.Controls.Add(checkoutButton, 2, 0);
        actions.Controls.Add(checkinButton, 4, 0);

        var batchActions = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(3, 2, 3, 2) };
        batchOperationButton.Text = "整套装配操作...";
        ConfigureStructureToolbarButton(batchOperationButton);
        batchOperationButton.Click += (_, _) => BatchOperationRequested?.Invoke(this, EventArgs.Empty);
        actionToolTip.SetToolTip(batchOperationButton, "整套获取最新文件及权限，或按子件优先顺序提交存档");
        batchActions.Controls.Add(batchOperationButton);

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
        structureTree.ImageList = structureImages;
        structureTree.ItemHeight = Math.Max(structureTree.ItemHeight, 20);
        structureTree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        structureTree.DrawNode += DrawStructureNode;
        structureTree.BeforeExpand += (_, eventArgs) => MaterializeChildren(eventArgs.Node, activeTreeBuildPlan);
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
            if (node != null && !suppressNodeSelectedNotification)
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
        tab.Controls.Add(batchActions);
        tab.Controls.Add(actions);
        return tab;
    }

    private void DrawStructureHeader(object sender, PaintEventArgs eventArgs)
    {
        GetStructureColumns(structureTreeSurface.ClientSize.Width, out var nameDividerX, out var versionDividerX);
        using (var background = new SolidBrush(Color.FromArgb(242, 244, 247)))
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.FillRectangle(background, 0, 0, structureTreeSurface.ClientSize.Width, 23);
            eventArgs.Graphics.DrawLine(border, 0, 22, structureTreeSurface.ClientSize.Width, 22);
            eventArgs.Graphics.DrawLine(border, nameDividerX, 0, nameDividerX, structureTreeSurface.ClientSize.Height);
            eventArgs.Graphics.DrawLine(border, versionDividerX, 0, versionDividerX, structureTreeSurface.ClientSize.Height);
        }

        TextRenderer.DrawText(eventArgs.Graphics, "名称", Font, new Rectangle(6, 0, Math.Max(0, nameDividerX - 10), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(eventArgs.Graphics, "当前版本 / 最新版本", Font, new Rectangle(nameDividerX + 5, 0, Math.Max(0, versionDividerX - nameDividerX - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(versionDividerX + 5, 0, Math.Max(0, structureTreeSurface.ClientSize.Width - versionDividerX - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void DrawStructureNode(object sender, DrawTreeNodeEventArgs eventArgs)
    {
        var model = eventArgs.Node.Tag as CadTreeNode;
        GetStructureColumns(structureTree.ClientSize.Width, out var nameDividerX, out var versionDividerX);
        var selected = (eventArgs.State & TreeNodeStates.Selected) == TreeNodeStates.Selected;
        var background = selected ? SystemColors.Highlight : eventArgs.Node.BackColor.IsEmpty ? structureTree.BackColor : eventArgs.Node.BackColor;
        var structureColor = selected ? SystemColors.HighlightText : eventArgs.Node.ForeColor.IsEmpty ? structureTree.ForeColor : eventArgs.Node.ForeColor;
        var font = eventArgs.Node.NodeFont ?? structureTree.Font;
        using (var brush = new SolidBrush(background))
        using (var border = new Pen(Color.FromArgb(225, 228, 233)))
        {
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(0, eventArgs.Bounds.Top, structureTree.ClientSize.Width, eventArgs.Bounds.Height));
            eventArgs.Graphics.DrawLine(border, nameDividerX, eventArgs.Bounds.Top, nameDividerX, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, versionDividerX, eventArgs.Bounds.Top, versionDividerX, eventArgs.Bounds.Bottom);
        }

        var structureBounds = new Rectangle(eventArgs.Bounds.Left, eventArgs.Bounds.Top, Math.Max(0, nameDividerX - eventArgs.Bounds.Left - 4), eventArgs.Bounds.Height);
        DrawNodeImage(eventArgs.Graphics, eventArgs.Node, structureBounds, selected);
        structureBounds.X += 20;
        structureBounds.Width = Math.Max(0, structureBounds.Width - 20);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, font, structureBounds, structureColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (model != null)
        {
            var versions = VersionText(model);
            var status = WorkStateText(model);
            var statusColor = selected ? SystemColors.HighlightText : WorkStateColor(model);
            var versionBounds = new Rectangle(nameDividerX + 5, eventArgs.Bounds.Top, Math.Max(0, versionDividerX - nameDividerX - 9), eventArgs.Bounds.Height);
            TextRenderer.DrawText(eventArgs.Graphics, versions, structureTree.Font, versionBounds, selected ? SystemColors.HighlightText : Color.FromArgb(64, 76, 89), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var statusBounds = new Rectangle(versionDividerX + 5, eventArgs.Bounds.Top, Math.Max(0, structureTree.ClientSize.Width - versionDividerX - 9), eventArgs.Bounds.Height);
            TextRenderer.DrawText(eventArgs.Graphics, status, model.WorkState == CadWorkState.None ? structureTree.Font : emphasizedNodeFont, statusBounds, statusColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if ((eventArgs.State & TreeNodeStates.Focused) == TreeNodeStates.Focused)
        {
            ControlPaint.DrawFocusRectangle(eventArgs.Graphics, new Rectangle(0, eventArgs.Bounds.Top, structureTree.ClientSize.Width, eventArgs.Bounds.Height), structureColor, background);
        }
    }

    private static void GetStructureColumns(int width, out int nameDividerX, out int versionDividerX)
    {
        var usable = Math.Max(1, width);
        if (usable < 320)
        {
            nameDividerX = Math.Max(74, usable * 40 / 100);
            versionDividerX = Math.Max(nameDividerX + 72, usable * 74 / 100);
            versionDividerX = Math.Min(Math.Max(nameDividerX + 1, usable - 58), versionDividerX);
            return;
        }

        var versionWidth = Math.Max(116, Math.Min(172, usable * 31 / 100));
        var statusWidth = Math.Max(82, Math.Min(122, usable * 24 / 100));
        nameDividerX = usable - versionWidth - statusWidth;
        versionDividerX = usable - statusWidth;
    }

    private static string VersionText(CadTreeNode node)
    {
        var current = string.IsNullOrWhiteSpace(node.CurrentRevision) ? "—" : node.CurrentRevision;
        var latest = string.IsNullOrWhiteSpace(node.LatestRevision) ? "—" : node.LatestRevision;
        return string.Concat(current, " / ", latest);
    }

    private void DrawNodeImage(Graphics graphics, TreeNode node, Rectangle bounds, bool selected)
    {
        var key = selected ? node.SelectedImageKey : node.ImageKey;
        if (!string.IsNullOrWhiteSpace(key) && structureImages.Images.ContainsKey(key))
        {
            graphics.DrawImage(structureImages.Images[key], bounds.Left, bounds.Top + Math.Max(0, (bounds.Height - 16) / 2), 16, 16);
        }
    }

    private void BuildStructureContextMenu()
    {
        contextOpen.Text = "在SolidWorks中打开最新受控版（只读）";
        contextGetLatest.Text = "重新获取最新受控版";
        contextCheckout.Text = "获取权限";
        contextCheckIn.Text = "提交存档";
        contextDiscardCheckout.Text = "放弃编辑";
        contextVersions.Text = "获取指定版本...";
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
        var historicalPreview = node.IsHistoricalPreview;

        SetContextState(
            contextOpen,
            registered && authenticated && canOpenInSolidWorks,
            PdmActionReason(registered, authenticated));
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
        if (historicalPreview)
        {
            checkoutReason = "历史版本为只读预览，不能获取编辑权限";
        }
        SetContextState(contextCheckout, !historicalPreview && authenticated && !editing && (registered || canRegister), checkoutReason);

        var canFirstCheckIn = !historicalPreview && authenticated && canRegister;
        var checkInReason = PdmActionReason(registered, authenticated);
        if (historicalPreview)
        {
            checkInReason = "历史版本为只读预览，不能提交存档；请打开当前工作文件";
        }
        else if (canFirstCheckIn)
        {
            checkInReason = "首次提交存档时选择归属项目，系统将自动登记并准备权限";
        }
        else if (registered && authenticated && !editing)
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
        SetContextState(
            contextCheckIn,
            canFirstCheckIn || (!historicalPreview && registered && authenticated && editingByCurrentUser && localFileExists),
            checkInReason);
        SetContextState(
            contextDiscardCheckout,
            !historicalPreview && registered && authenticated && editingByCurrentUser,
            historicalPreview
                ? "历史版本为只读预览，不能更改编辑状态"
                : registered && authenticated && editing && !editingByCurrentUser
                ? string.Concat("只有当前编辑人员", node.CheckedOutBy, "可以放弃编辑")
                : registered && authenticated ? "尚未获取该图档的编辑权限" : PdmActionReason(registered, authenticated));

        SetContextState(contextVersions, registered && authenticated, PdmActionReason(registered, authenticated));
        SetContextState(contextCompare, registered && authenticated, PdmActionReason(registered, authenticated));
        contextCompare.ToolTipText = contextCompare.Enabled
            ? "打开版本记录后选择两个版本进行对比"
            : contextCompare.ToolTipText;

        if (historicalPreview)
        {
            contextHint.Text = "提示：历史版本仅供只读预览，不能获取权限或提交存档";
        }
        else if (canFirstCheckIn)
        {
            contextHint.Text = "提示：首次提交存档时请选择归属项目号";
        }
        else if (!registered)
        {
            contextHint.Text = "提示：该图档尚未入库，仅支持本地打开";
        }
        else if (!authenticated)
        {
            contextHint.Text = "提示：登录后可使用PDM操作";
        }
        else if (!localFileExists)
        {
            contextHint.Text = "提示：本地文件不存在，可获取最新或指定版本";
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

    private TabPage BuildVersionsTab()
    {
        var tab = new TabPage("版本记录") { BackColor = Color.White, Padding = new Padding(8) };
        versionList.Dock = DockStyle.Fill;
        versionList.View = View.Details;
        versionList.FullRowSelect = true;
        versionList.MultiSelect = true;
        versionList.HideSelection = false;
        versionList.AccessibleName = "图档历史版本";
        versionList.Columns.Add("版本", 62);
        versionList.Columns.Add("状态", 62);
        versionList.Columns.Add("创建人", 76);
        versionList.Columns.Add("时间", 118);
        versionList.Columns.Add("变更说明", 180);
        versionList.SelectedIndexChanged += (_, _) => UpdateVersionActions();
        versionList.DoubleClick += (_, _) => RaiseOpenHistory();
        BuildVersionContextMenu();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
        openCurrentButton.Text = "只读打开最新受控版";
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
        openCurrentButton.Enabled = IsDisplayedDocumentSelected();
        openHistoryButton.Enabled = displayedVersionDocumentId.HasValue && versionList.SelectedItems.Count == 1;
        compareVersionsButton.Enabled = displayedVersionDocumentId.HasValue && versionList.SelectedItems.Count == 2;
    }

    private void BuildVersionContextMenu()
    {
        versionContextGetSelected.Text = "只读打开历史版本";
        versionContextGetLatest.Text = "只读打开最新受控版";
        versionContextOpenCurrent.Text = "只读打开当前受控版";
        versionContextCompare.Text = "版本对比";
        versionContextRefresh.Text = "刷新版本列表";
        versionContextGetSelected.Click += (_, _) => RaiseOpenHistory();
        versionContextGetLatest.Click += (_, _) => RaiseOpenLatestVersion();
        versionContextOpenCurrent.Click += (_, _) => RaiseDisplayedNode(OpenRequested);
        versionContextCompare.Click += (_, _) => RaiseVersionComparison();
        versionContextRefresh.Click += (_, _) => RaiseDisplayedNode(VersionsRequested);
        versionMenu.Items.AddRange(new ToolStripItem[]
        {
            versionContextGetSelected,
            versionContextGetLatest,
            new ToolStripSeparator(),
            versionContextOpenCurrent,
            versionContextCompare,
            new ToolStripSeparator(),
            versionContextRefresh
        });
        versionMenu.Opening += (_, _) => UpdateVersionContextMenu();
        versionList.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right)
            {
                return;
            }

            var item = versionList.GetItemAt(eventArgs.X, eventArgs.Y);
            if (item != null && !item.Selected)
            {
                versionList.SelectedItems.Clear();
                item.Selected = true;
            }
        };
        versionList.ContextMenuStrip = versionMenu;
    }

    private void UpdateVersionContextMenu()
    {
        var selectionCount = versionList.SelectedItems.Count;
        versionContextGetSelected.Enabled = displayedVersionDocumentId.HasValue && selectionCount == 1;
        versionContextGetLatest.Enabled = displayedVersionDocumentId.HasValue && versionList.Items.Count > 0;
        versionContextOpenCurrent.Enabled = IsDisplayedDocumentSelected();
        versionContextCompare.Enabled = displayedVersionDocumentId.HasValue && selectionCount == 2;
        versionContextRefresh.Enabled = IsDisplayedDocumentSelected();
    }

    private void RaiseOpenLatestVersion()
    {
        if (displayedVersionDocumentId is Guid documentId && versionList.Items.Count > 0 && versionList.Items[0].Tag is DocumentVersionDto version)
        {
            OpenHistoryRequested?.Invoke(this, new DocumentVersionEventArgs(documentId, displayedVersionFileName, version));
        }
    }

    private void RaiseDisplayedNode(EventHandler<CadTreeNodeEventArgs> handler)
    {
        if (IsDisplayedDocumentSelected())
        {
            handler?.Invoke(this, new CadTreeNodeEventArgs(SelectedNode));
        }
    }

    private bool IsDisplayedDocumentSelected() =>
        displayedVersionDocumentId.HasValue && SelectedNode?.DocumentId == displayedVersionDocumentId;

    private void RaiseOpenHistory()
    {
        if (displayedVersionDocumentId is Guid documentId && versionList.SelectedItems.Count == 1 && versionList.SelectedItems[0].Tag is DocumentVersionDto version)
            OpenHistoryRequested?.Invoke(this, new DocumentVersionEventArgs(documentId, displayedVersionFileName, version));
    }

    private void RaiseVersionComparison()
    {
        if (displayedVersionDocumentId is Guid documentId && versionList.SelectedItems.Count == 2 && versionList.SelectedItems[0].Tag is DocumentVersionDto left && versionList.SelectedItems[1].Tag is DocumentVersionDto right)
            CompareVersionsRequested?.Invoke(this, new VersionComparisonEventArgs(documentId, left.Id, right.Id));
    }

    private void RebuildTree(string filter)
    {
        var modelRoot = rootNode;
        if (modelRoot == null)
        {
            return;
        }

        var selectedInstancePath = SelectedNode?.InstancePath;
        var normalizedFilter = filter?.Trim();
        var generation = Interlocked.Increment(ref treeBuildGeneration);
        CancelActiveTreeBuild();
        Task.Run(() =>
        {
            var root = BuildTreeNode(modelRoot, normalizedFilter, modelRoot);
            var relatedDrawings = BuildRelatedDrawingsGroup(modelRoot, normalizedFilter);
            if (relatedDrawings != null)
            {
                root?.Nodes.Add(relatedDrawings);
            }

            return PrepareTreeBuildPlan(root);
        }).ContinueWith(task => RunOnUiThread(() =>
        {
            if (task.IsFaulted || task.IsCanceled || generation != treeBuildGeneration || !ReferenceEquals(modelRoot, rootNode))
            {
                return;
            }

            structureTree.BeginUpdate();
            var plan = task.Result;
            plan.UpdateOpen = true;
            activeTreeBuildPlan = plan;
            structureTree.Nodes.Clear();
            if (plan.Root != null)
            {
                structureTree.Nodes.Add(plan.Root);
            }

            AppendTreeBatch(plan, generation, selectedInstancePath);
        }), TaskScheduler.Default);
    }

    private void CancelActiveTreeBuild()
    {
        if (activeTreeBuildPlan?.UpdateOpen == true)
        {
            activeTreeBuildPlan.UpdateOpen = false;
            structureTree.EndUpdate();
        }

        activeTreeBuildPlan = null;
    }

    private static TreeBuildPlan PrepareTreeBuildPlan(TreeNode root)
    {
        var plan = new TreeBuildPlan(root);
        if (root != null)
        {
            CaptureTreeChildren(root, plan, true);
            if (plan.ChildrenByParent.TryGetValue(root, out var children))
            {
                foreach (var child in children)
                {
                    plan.PendingChildren.Enqueue(new TreeAppendOperation(root, child));
                }
            }
        }

        return plan;
    }

    private static void CaptureTreeChildren(TreeNode parent, TreeBuildPlan plan, bool isRoot)
    {
        var children = parent.Nodes.Cast<TreeNode>().ToArray();
        parent.Nodes.Clear();
        plan.IndexNode(parent);
        if (children.Length == 0)
        {
            return;
        }

        plan.ChildrenByParent[parent] = children;
        foreach (var child in children)
        {
            plan.ParentByChild[child] = parent;
            CaptureTreeChildren(child, plan, false);
        }

        if (!isRoot)
        {
            parent.Nodes.Add(new TreeNode { Tag = LazyTreePlaceholder.Instance });
        }
    }

    private void MaterializeChildren(TreeNode parent, TreeBuildPlan plan)
    {
        if (parent == null || plan == null || plan.MaterializedParents.Contains(parent)
            || !plan.ChildrenByParent.TryGetValue(parent, out var children))
        {
            return;
        }

        structureTree.BeginUpdate();
        try
        {
            parent.Nodes.Clear();
            parent.Nodes.AddRange(children);
            plan.MaterializedParents.Add(parent);
        }
        finally
        {
            structureTree.EndUpdate();
        }
    }

    private void EnsureTreeNodeAttached(TreeNode node, TreeBuildPlan plan)
    {
        if (node == null || plan == null || node.TreeView == structureTree)
        {
            return;
        }

        if (!plan.ParentByChild.TryGetValue(node, out var parent))
        {
            return;
        }

        EnsureTreeNodeAttached(parent, plan);
        MaterializeChildren(parent, plan);
        parent.Expand();
    }

    private void AppendTreeBatch(TreeBuildPlan plan, int generation, string selectedInstancePath)
    {
        if (generation != treeBuildGeneration || IsDisposed || !ReferenceEquals(plan.Root?.Tag, rootNode))
        {
            if (plan.UpdateOpen)
            {
                plan.UpdateOpen = false;
                structureTree.EndUpdate();
            }
            return;
        }

        TreeNode batchParent = null;
        var batchChildren = new List<TreeNode>();
        for (var index = 0; index < 250 && plan.PendingChildren.Count > 0; index++)
        {
            var operation = plan.PendingChildren.Dequeue();
            if (batchParent != null && !ReferenceEquals(batchParent, operation.Parent))
            {
                batchParent.Nodes.AddRange(batchChildren.ToArray());
                batchChildren.Clear();
            }

            batchParent = operation.Parent;
            batchChildren.Add(operation.Child);
        }

        if (batchParent != null && batchChildren.Count > 0)
        {
            batchParent.Nodes.AddRange(batchChildren.ToArray());
        }

        if (plan.PendingChildren.Count > 0)
        {
            Task.Delay(1).ContinueWith(_ => RunOnUiThread(() => AppendTreeBatch(plan, generation, selectedInstancePath)), TaskScheduler.Default);
            return;
        }

        if (plan.UpdateOpen)
        {
            plan.UpdateOpen = false;
            if (plan.Root != null)
            {
                plan.MaterializedParents.Add(plan.Root);
            }
            structureTree.EndUpdate();
        }
        if (plan.Root != null)
        {
            plan.Root.Expand();
        }

        var pendingSelectionApplied = TrySelectComponentNode(pendingComponentSelectionName);
        if (pendingSelectionApplied)
        {
            pendingComponentSelectionName = string.Empty;
        }

        if (!pendingSelectionApplied && !string.IsNullOrWhiteSpace(selectedInstancePath))
        {
            var selected = FindTreeNode(
                structureTree.Nodes,
                node => node.Tag is CadTreeNode model && string.Equals(model.InstancePath, selectedInstancePath, StringComparison.OrdinalIgnoreCase));
            if (selected != null)
            {
                SelectTreeNodeWithoutNotification(selected);
            }
        }
    }

    private bool TrySelectComponentNode(string componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
        {
            return false;
        }

        var match = FindTreeNode(
            structureTree.Nodes,
            node => node.Tag is CadTreeNode model
                && string.Equals(model.ComponentSelectionName, componentName, StringComparison.OrdinalIgnoreCase));
        if (match == null && activeTreeBuildPlan != null)
        {
            match = activeTreeBuildPlan.FindByComponentName(componentName);
            EnsureTreeNodeAttached(match, activeTreeBuildPlan);
        }
        if (match == null)
        {
            return false;
        }

        SelectTreeNodeWithoutNotification(match);
        return true;
    }

    private void SelectTreeNodeWithoutNotification(TreeNode node)
    {
        suppressNodeSelectedNotification = true;
        try
        {
            structureTree.SelectedNode = node;
            node.EnsureVisible();
        }
        finally
        {
            suppressNodeSelectedNotification = false;
        }
    }

    private sealed class TreeBuildPlan
    {
        public TreeBuildPlan(TreeNode root) => Root = root;

        public TreeNode Root { get; }

        public Queue<TreeAppendOperation> PendingChildren { get; } = new Queue<TreeAppendOperation>();

        public Dictionary<TreeNode, TreeNode[]> ChildrenByParent { get; } = new Dictionary<TreeNode, TreeNode[]>();

        public Dictionary<TreeNode, TreeNode> ParentByChild { get; } = new Dictionary<TreeNode, TreeNode>();

        public HashSet<TreeNode> MaterializedParents { get; } = new HashSet<TreeNode>();

        private Dictionary<string, TreeNode> NodesByComponentName { get; } = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        public bool UpdateOpen { get; set; }

        public void IndexNode(TreeNode node)
        {
            if (node?.Tag is CadTreeNode model && !string.IsNullOrWhiteSpace(model.ComponentSelectionName)
                && !NodesByComponentName.ContainsKey(model.ComponentSelectionName))
            {
                NodesByComponentName.Add(model.ComponentSelectionName, node);
            }
        }

        public TreeNode FindByComponentName(string componentName) =>
            !string.IsNullOrWhiteSpace(componentName) && NodesByComponentName.TryGetValue(componentName, out var node) ? node : null;
    }

    private sealed class LazyTreePlaceholder
    {
        public static readonly LazyTreePlaceholder Instance = new LazyTreePlaceholder();

        private LazyTreePlaceholder()
        {
        }
    }

    private sealed class TreeAppendOperation
    {
        public TreeAppendOperation(TreeNode parent, TreeNode child)
        {
            Parent = parent;
            Child = child;
        }

        public TreeNode Parent { get; }

        public TreeNode Child { get; }
    }

    private TreeNode BuildTreeNode(CadTreeNode model, string filter, CadTreeNode modelRoot)
    {
        var childNodes = model.Children
            .Where(child => child.Kind != CadDocumentKind.Drawing)
            .Select(child => BuildTreeNode(child, filter, modelRoot))
            .Where(node => node != null)
            .ToArray();
        var selfMatches = MatchesFilter(model, filter)
            || ReferenceEquals(model, modelRoot) && HasMatchingRelatedDrawing(modelRoot, filter);
        if (!selfMatches && childNodes.Length == 0)
        {
            return null;
        }

        var isMissing = model.Status == CadReferenceStatus.Missing;
        var status = model.Status == CadReferenceStatus.Normal || isMissing ? string.Empty : string.Concat(" · ", StatusText(model.Status));
        var editTip = isMissing
            ? "\r\n状态：文件缺失"
            : string.Concat("\r\n状态：", WorkStateText(model), string.IsNullOrWhiteSpace(model.CheckedOutBy) ? string.Empty : string.Concat("\r\n编辑人员：", model.CheckedOutBy));
        var text = string.Concat(Path.GetFileNameWithoutExtension(model.FileName), " · ", model.DisplayName, status);
        var node = new TreeNode(text)
        {
            Tag = model,
            ImageKey = StructureImageKey(model.Kind),
            SelectedImageKey = StructureImageKey(model.Kind),
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

    private static string StructureImageKey(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "assembly";
            case CadDocumentKind.Part: return "part";
            case CadDocumentKind.Drawing: return "drawing";
            default: return "other";
        }
    }

    private static ImageList BuildStructureImages()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16), TransparentColor = Color.Transparent };
        images.Images.Add("assembly", DrawDocumentIcon(Color.FromArgb(233, 174, 45), true));
        images.Images.Add("part", DrawDocumentIcon(Color.FromArgb(94, 164, 91), false));
        images.Images.Add("drawing", DrawDrawingIcon());
        images.Images.Add("other", DrawDocumentIcon(Color.FromArgb(139, 151, 164), false));
        images.Images.Add("group", DrawGroupIcon());
        return images;
    }

    private static Bitmap DrawDocumentIcon(Color fill, bool assembly)
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(fill))
        using (var light = new SolidBrush(ControlPaint.Light(fill)))
        using (var pen = new Pen(ControlPaint.Dark(fill), 1))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillPolygon(brush, new[] { new Point(3, 6), new Point(8, 3), new Point(13, 6), new Point(8, 9) });
            graphics.FillPolygon(light, new[] { new Point(3, 6), new Point(8, 9), new Point(8, 14), new Point(3, 11) });
            graphics.FillPolygon(brush, new[] { new Point(8, 9), new Point(13, 6), new Point(13, 11), new Point(8, 14) });
            graphics.DrawPolygon(pen, new[] { new Point(3, 6), new Point(8, 3), new Point(13, 6), new Point(13, 11), new Point(8, 14), new Point(3, 11) });
            if (assembly)
            {
                graphics.FillEllipse(Brushes.SteelBlue, 1, 1, 5, 5);
                graphics.DrawEllipse(Pens.White, 2, 2, 3, 3);
            }
        }
        return bitmap;
    }

    private static Bitmap DrawDrawingIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var paper = new SolidBrush(Color.White))
        using (var pen = new Pen(Color.FromArgb(72, 126, 181), 1))
        {
            graphics.FillRectangle(paper, 3, 2, 10, 12);
            graphics.DrawRectangle(pen, 3, 2, 10, 12);
            graphics.DrawLine(pen, 5, 6, 11, 6);
            graphics.DrawLine(pen, 5, 9, 11, 9);
        }
        return bitmap;
    }

    private static Bitmap DrawGroupIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(Color.FromArgb(222, 183, 76)))
        using (var pen = new Pen(Color.FromArgb(150, 113, 31), 1))
        {
            graphics.FillRectangle(fill, 2, 5, 12, 8);
            graphics.FillRectangle(fill, 3, 3, 5, 3);
            graphics.DrawRectangle(pen, 2, 5, 12, 8);
        }
        return bitmap;
    }

    private static string WorkStateText(CadTreeNode node)
    {
        if (node.IsHistoricalPreview)
        {
            return "历史预览（只读）";
        }

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
            default: return !node.DocumentId.HasValue ? "未入库" : node.Status == CadReferenceStatus.Normal ? "正常" : StatusText(node.Status);
        }
    }

    private static Color WorkStateColor(CadTreeNode node)
    {
        if (node.IsHistoricalPreview)
        {
            return Color.FromArgb(59, 104, 153);
        }

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

    private TreeNode BuildRelatedDrawingsGroup(CadTreeNode modelRoot, string filter)
    {
        var drawings = new List<CadTreeNode>();
        CollectRelatedDrawings(modelRoot, drawings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var drawingNodes = drawings
            .Where(drawing => MatchesFilter(drawing, filter))
            .Select(drawing => BuildTreeNode(drawing, filter, modelRoot))
            .Where(node => node != null)
            .ToArray();
        if (drawingNodes.Length == 0)
        {
            return null;
        }

        var group = new TreeNode(string.Concat("关联图纸（", drawingNodes.Length, "）"))
        {
            ForeColor = Color.FromArgb(90, 107, 128),
            ImageKey = "group",
            SelectedImageKey = "group",
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

    private static bool HasMatchingRelatedDrawing(CadTreeNode modelRoot, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var drawings = new List<CadTreeNode>();
        CollectRelatedDrawings(modelRoot, drawings, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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
            batchOperationButton.Enabled = false;
            ApplyCheckoutButtonAppearance(false, false);
            ApplyCheckinButtonAppearance(false);
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
        var historicalPreview = node.IsHistoricalPreview;
        var canCheckout = !historicalPreview
            && authenticated
            && string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && (node.DocumentId.HasValue || canRegister);
        var canFirstCheckIn = !historicalPreview && authenticated && canRegister;
        var canCheckIn = canFirstCheckIn || (!historicalPreview && node.DocumentId.HasValue && editingByCurrentUser && localFileExists);
        checkoutButton.Enabled = canCheckout;
        checkinButton.Enabled = canCheckIn;
        ApplyCheckoutButtonAppearance(canCheckout, editingByCurrentUser);
        ApplyCheckinButtonAppearance(canCheckIn);
        batchOperationButton.Enabled = authenticated && rootNode != null && !rootNode.IsHistoricalPreview;
        actionToolTip.SetToolTip(
            checkoutButton,
            historicalPreview ? "历史版本为只读预览，不能获取编辑权限" : canRegister ? "首次获取权限时将自动登记该图档" : node.DocumentId.HasValue ? "获取该图档的独占编辑权限" : "本地文件不存在或文件类型不支持登记");
        actionToolTip.SetToolTip(
            checkinButton,
            historicalPreview ? "历史版本为只读预览，不能提交存档；请打开当前工作文件" : canFirstCheckIn ? "首次提交存档时选择归属项目，系统将自动登记并准备权限" : !node.DocumentId.HasValue ? "本地文件不存在或文件类型不支持登记" : !editingByCurrentUser ? "只有当前编辑人员可以提交存档" : !localFileExists ? "本地文件不存在，不能提交存档" : "提交当前文件并生成新工作版本");
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
        if (node == null)
        {
            ClearDisplayedVersions();
            return;
        }
        if (!node.DocumentId.HasValue)
        {
            ClearDisplayedVersions();
            MessageBox.Show(this, "该图档尚未入库，暂无版本记录", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(authenticatedUsername))
        {
            ClearDisplayedVersions();
            MessageBox.Show(this, "请先登录PDM。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        VersionsRequested?.Invoke(this, new CadTreeNodeEventArgs(node));
    }

    private void ClearDisplayedVersions()
    {
        displayedVersionDocumentId = null;
        displayedVersionFileName = string.Empty;
        versionList.Items.Clear();
        UpdateVersionActions();
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
        button.AutoSize = false;
        button.AutoEllipsis = false;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = InputBorderColor;
        button.FlatAppearance.BorderSize = 1;
    }

    private void ApplyCheckoutButtonAppearance(bool canCheckout, bool editingByCurrentUser)
    {
        if (editingByCurrentUser)
        {
            ApplyFilledButtonAppearance(checkoutButton, EditingColor);
        }
        else if (canCheckout)
        {
            ApplyFilledButtonAppearance(checkoutButton, CheckoutAvailableColor);
        }
        else
        {
            ApplyDefaultButtonAppearance(checkoutButton);
        }
    }

    private void ApplyCheckinButtonAppearance(bool canCheckIn)
    {
        ApplyFilledButtonAppearance(checkinButton, canCheckIn ? SubmitAvailableColor : SubmitUnavailableColor, canCheckIn ? Color.White : SystemColors.GrayText);
    }

    private static void ApplyFilledButtonAppearance(Button button, Color background, Color foreground)
    {
        button.UseVisualStyleBackColor = false;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatAppearance.BorderColor = background;
        button.FlatAppearance.BorderSize = 0;
    }

    private static void ApplyFilledButtonAppearance(Button button, Color background) =>
        ApplyFilledButtonAppearance(button, background, Color.White);

    private static void ApplyDefaultButtonAppearance(Button button)
    {
        button.UseVisualStyleBackColor = true;
        button.BackColor = SystemColors.Control;
        button.ForeColor = SystemColors.ControlText;
        button.FlatAppearance.BorderColor = InputBorderColor;
        button.FlatAppearance.BorderSize = 1;
    }

    private static void ConfigureStructureActionColumns(TableLayoutPanel actions)
    {
        const int standardGap = 6;
        var availableWidth = Math.Max(0, actions.ClientSize.Width - actions.Padding.Horizontal);
        var widthBeforeLastGap = Math.Max(0, availableWidth - standardGap * 2);
        var lastGap = standardGap + widthBeforeLastGap % 3;
        actions.ColumnStyles.Clear();
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33334F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, standardGap));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33333F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, lastGap));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33333F));
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
