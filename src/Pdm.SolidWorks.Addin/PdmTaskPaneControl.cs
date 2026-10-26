using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmTaskPaneControl : UserControl
{
    private readonly TaskPaneSynchronizationContext uiContext;
    private const int StructureSelectionColumnWidth = 28;
    private const int StructureVersionColumnWidth = 105;
    private const int StructureTreeLeftPadding = 4;
    private const int StructureTreeIndentWidth = 16;
    private const int StructureExpanderSize = 9;
    private static readonly string StructureColumnSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "solidworks-design-tree-columns.json");
    private int structureNameWidth;
    private int structureVersionWidth;
    private int draggingStructureDivider;
    private static readonly Color InputBorderColor = Color.FromArgb(122, 122, 122);
    private static readonly Color CheckoutAvailableColor = Color.FromArgb(36, 170, 168);
    private static readonly Color DiscardCheckoutAvailableColor = Color.FromArgb(230, 126, 34);
    private static readonly Color SubmitAvailableColor = Color.FromArgb(21, 126, 77);
    private static readonly Color SecondaryActionAvailableColor = Color.FromArgb(72, 84, 99);
    private static readonly Color DisabledActionBackgroundColor = Color.FromArgb(224, 228, 233);
    private static readonly Color DisabledActionTextColor = Color.FromArgb(145, 151, 159);

    private readonly PdmRippleStatusIndicator serviceStatus = new PdmRippleStatusIndicator();
    private readonly Image headerLogoImage;
    private readonly TextBox currentProject = new TextBox();
    private readonly Panel plannedTaskBar = new Panel();
    private Label plannedTaskLabel = new Label();
    private Label nextPlannedTaskLabel = new Label();
    private readonly System.Windows.Forms.Timer plannedTaskTimer = new System.Windows.Forms.Timer { Interval = 30000 };
    private readonly System.Windows.Forms.Timer plannedTaskRefreshTimer = new System.Windows.Forms.Timer { Interval = 300000 };
    private readonly System.Windows.Forms.Timer plannedTaskAnimation = new System.Windows.Forms.Timer { Interval = 20 };
    private string[] plannedTasks = Array.Empty<string>();
    private int plannedTaskIndex;
    private readonly TextBox searchBox = new TextBox();
    private readonly LinkLabel treeHealth = new LinkLabel();
    private readonly TreeView structureTree = new StructureTreeView();
    private readonly Panel structureTreeSurface = new Panel();
    private readonly Label selectedFile = new Label();
    private readonly Label selectedMeta = new Label();
    private readonly Label checkoutReminder = new Label();
    private readonly Button loginButton = new Button();
    private readonly Button openClientButton = new Button();
    private readonly Button workHoursButton = new Button();
    private readonly Button checkoutButton = new Button();
    private readonly Button checkinButton = new Button();
    private readonly Button propertyEditButton = new Button();
    private readonly Button settingsButton = new Button();
    private readonly Panel workspaceOperationPanel = new Panel();
    private readonly Label workspaceOperationStatus = new Label();
    private readonly PdmQuantityProgressBar workspaceOperationProgress = new PdmQuantityProgressBar { Visible = false };
    private readonly System.Windows.Forms.Timer workspaceElapsedTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    private DateTime? workspaceElapsedStartedUtc;
    private string workspaceStageMessage;
    private readonly PdmSegmentedTabControl tabs = new PdmSegmentedTabControl();
    private readonly Panel tabsHost = new Panel();
    private readonly Control projectPanel;
    private readonly Label pluginVersion = new Label();
    private readonly string installedPluginVersion;
    private readonly ProjectDocumentsControl projectDocuments = new ProjectDocumentsControl();
    private readonly AutomaticDrawingControl automaticDrawing = new AutomaticDrawingControl();
    private CadTreeNode activeDrawingSource;
    private TabPage structureTab;
    private TabPage automaticDrawingTab;
    private TabPage versionsTab;
    private TabPage diagnosticsTab;
    private bool suppressVersionTabRequest;
    private readonly ListView versionList = new ListView();
    private readonly Button openCurrentButton = new Button();
    private readonly Button openHistoryButton = new Button();
    private readonly Button editHistoryButton = new Button();
    private readonly Button compareVersionsButton = new Button();
    private readonly ContextMenuStrip versionMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem versionContextGetSelected = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextEditSelected = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextGetLatest = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextOpenCurrent = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextCompare = new ToolStripMenuItem();
    private readonly ToolStripMenuItem versionContextRefresh = new ToolStripMenuItem();
    private readonly ToolTip actionToolTip = new ToolTip();
    private readonly ContextMenuStrip structureMenu = new ContextMenuStrip();
    private readonly ToolStripMenuItem contextOpenWorkingFile = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextUpdateLatest = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextVersionInfo = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCheckout = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCheckIn = new ToolStripMenuItem();

    private readonly ToolStripMenuItem contextDiscardCheckout = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextGenerateDrawing = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextOpenDrawing = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextDrawingVersions = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextVersions = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextRefresh = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextWhereUsed = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextRequestRelease = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextOpenReleaseCenter = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextWithdrawApproval = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextObsolete = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextZoomSelection = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextIsolate = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextExitIsolate = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextOpenFolder = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextRenameDocument = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextSelectEligible = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextSelectChanged = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextSelectMine = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextClearSelection = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextExpandAll = new ToolStripMenuItem();
    private readonly ToolStripMenuItem contextCollapseAll = new ToolStripMenuItem();
    private readonly Label contextHint = new Label();
    private readonly Font emphasizedNodeFont;
    private readonly ImageList structureImages;
    private CadTreeNode rootNode;
    private string authenticatedUsername = string.Empty;
    private IReadOnlyDictionary<string, string> userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private Guid? displayedVersionDocumentId;
    private string displayedVersionFileName = string.Empty;
    private int treeBuildGeneration;
    private TreeBuildPlan activeTreeBuildPlan;
    private readonly Dictionary<string, TreeViewState> treeViewStates = new Dictionary<string, TreeViewState>(StringComparer.OrdinalIgnoreCase);
    private string displayedTreeViewKey = string.Empty;
    private string renderedTreeStamp;
    private bool treeBuildPending;
    private bool restoringTreeViewState;
    private bool changedTreeView;
    private TreeViewState pendingTreeViewState;
    private string pendingComponentSelectionName = string.Empty;
    private bool suppressNodeSelectedNotification;
    private bool suppressTreeCheckEvents;
    private bool allowStructureCollapse;
    private StructureHealthFilter activeHealthFilter;
    private bool selectFirstHealthMatchAfterBuild;
    private readonly HashSet<string> checkedCheckInPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ProjectDto> availableProjects = Array.Empty<ProjectDto>();
    private Guid? selectedProjectId;
    private bool projectContextAvailable;
    private bool workspaceOperationActive;
    private bool refreshingWorkspaceOperationUi;

    public PdmTaskPaneControl(string installedVersion = "")
    {
        workspaceElapsedTimer.Tick += (_, _) => UpdateWorkspaceElapsed();
        uiContext = new TaskPaneSynchronizationContext(this);
        installedPluginVersion = installedVersion?.Trim() ?? string.Empty;
        Dock = DockStyle.Fill;
        MinimumSize = new Size(250, 420);
        Font = new Font("Microsoft YaHei UI", 8.5F);
        LoadStructureColumnWidths();
        emphasizedNodeFont = new Font(Font, FontStyle.Bold);
        structureImages = BuildStructureImages();
        headerLogoImage = LoadHeaderLogoImage();
        BackColor = Color.FromArgb(244, 247, 251);

        var header = BuildHeader();
        projectPanel = BuildProjectPanel();
        ConfigureCheckoutReminder();
        BuildTabs();
        ConfigurePluginVersion();
        tabsHost.Dock = DockStyle.Fill;
        tabsHost.Padding = new Padding(0, 5, 0, 0);
        tabsHost.BackColor = Color.FromArgb(244, 247, 251);
        tabsHost.Controls.Add(tabs);
        tabsHost.Layout += (_, _) => ResizeTabHeaders();
        Controls.Add(tabsHost);
        Controls.Add(workspaceOperationPanel);
        Controls.Add(checkoutReminder);
        Controls.Add(projectPanel);
        Controls.Add(header);
        ApplyContentTypography(projectPanel);
        ApplyContentTypography(checkoutReminder);
        ApplyContentTypography(tabs);
        pluginVersion.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref treeBuildGeneration);
            CancelActiveTreeBuild();
            plannedTaskTimer.Dispose();
            plannedTaskRefreshTimer.Dispose();
            plannedTaskAnimation.Dispose();
            workspaceElapsedTimer.Dispose();
            emphasizedNodeFont?.Dispose();
            structureImages?.Dispose();
            headerLogoImage?.Dispose();
        }

        base.Dispose(disposing);
    }

    public event EventHandler LoginRequested;
    public event EventHandler OpenClientRequested;
    public event EventHandler WorkHoursRequested;
    public event EventHandler PlannedTasksRefreshRequested;

    public void SetPlannedTaskScrollInterval(int seconds)
    {
        RunOnUiThread(() => plannedTaskTimer.Interval = (seconds == 15 || seconds == 60 ? seconds : 30) * 1000);
    }

    public void SetPlannedTasks(IReadOnlyList<string> tasks)
    {
        RunOnUiThread(() =>
        {
            var updated = tasks?.ToArray() ?? Array.Empty<string>();
            if (plannedTasks.SequenceEqual(updated)) return;
            plannedTaskAnimation.Stop();
            plannedTasks = updated;
            plannedTaskIndex = 0;
            plannedTaskLabel.Top = 0;
            nextPlannedTaskLabel.Top = plannedTaskBar.ClientSize.Height;
            plannedTaskLabel.Text = plannedTasks.Length == 0
                ? (string.IsNullOrWhiteSpace(authenticatedUsername) ? "登录后显示本人负责的计划任务" : "暂无本人负责的计划任务")
                : plannedTasks[0];
            actionToolTip.SetToolTip(plannedTaskBar, string.Join("\r\n", plannedTasks));
            plannedTaskTimer.Stop();
            if (!string.IsNullOrWhiteSpace(authenticatedUsername)) plannedTaskTimer.Start();
        });
    }
    public event EventHandler RefreshRequested;
    public event EventHandler<CadTreeNodeEventArgs> NodeSelected;
    public event EventHandler<CadTreeNodeEventArgs> OpenRequested;

    public event EventHandler<CadTreeNodeEventArgs> OpenWorkingFileRequested;
    public event EventHandler<CadTreeNodeEventArgs> UpdateLatestRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckoutRequested;
    public event EventHandler<CadTreeNodeEventArgs> CheckInRequested;



    public event EventHandler<CadTreeNodeEventArgs> DiscardCheckoutRequested;
    public event EventHandler BatchOperationRequested;
    public event EventHandler<CadTreeNodeEventArgs> BatchPropertyEditRequested;
    public event EventHandler<CadTreeNodeEventArgs> BatchPropertyCardRequested;
    public event EventHandler SettingsRequested;
    public event EventHandler<AutomaticDrawingRequestEventArgs> AutomaticDrawingGenerateRequested;
    public event EventHandler<AutomaticDrawingRequestEventArgs> AutomaticDrawingOpenRequested;
    public event EventHandler<AutomaticDrawingRequestEventArgs> AutomaticDrawingImportAnnotationsRequested;
    public event EventHandler<AutomaticDrawingRequestEventArgs> AutomaticDrawingSubmitRequested;
    public event EventHandler<CadTreeNodeEventArgs> VersionsRequested;
    public event EventHandler<DocumentVersionEventArgs> OpenHistoryRequested;
    public event EventHandler<DocumentVersionEventArgs> EditHistoricalVersionRequested;
    public event EventHandler<VersionComparisonEventArgs> CompareVersionsRequested;
    public event EventHandler<ControlledOpenEventArgs> ControlledOpenRequested;
    public event EventHandler<ProjectBrowseEventArgs> ProjectBrowseRequested;
    public event EventHandler<CadTreeNodeEventArgs> WhereUsedRequested;
    public event EventHandler<CadTreeNodeEventArgs> RequestReleaseRequested;
    public event EventHandler<CadTreeNodeEventArgs> WithdrawApprovalRequested;
    public event EventHandler<CadTreeNodeEventArgs> ObsoleteRequested;
    public event EventHandler<CadTreeNodeEventArgs> ZoomSelectionRequested;
    public event EventHandler<CadTreeNodeEventArgs> IsolateRequested;
    public event EventHandler ExitIsolateRequested;
    public event EventHandler<CadTreeNodeEventArgs> OpenContainingFolderRequested;
    public event EventHandler<CadTreeNodeEventArgs> RenameDocumentRequested;
    public event EventHandler OpenReleaseCenterRequested;

    public CadTreeNode SelectedNode => structureTree.SelectedNode?.Tag as CadTreeNode;

    public void SetProjectTree(Guid projectId, CadTreeNode root) => RunOnUiThread(() =>
    {
        if (projectDocuments.SelectedProjectId != projectId) return;
        projectDocuments.ConfigureStructureLoading(null, null);
        projectDocuments.SetTree(root);
    });

    public void SetProjectNavigation(Guid projectId, CadTreeNode root, Func<CadTreeNode, Task> loader, ISet<Guid> pending) => RunOnUiThread(() =>
    {
        if (projectDocuments.SelectedProjectId != projectId) return;
        projectDocuments.ConfigureStructureLoading(loader, pending);
        projectDocuments.SetTree(root);
    });

    public void SetProjectLoadingStatus(Guid projectId, string message) => RunOnUiThread(() =>
    {
        if (projectDocuments.SelectedProjectId == projectId) projectDocuments.SetLoadingStatus(message);
    });

    public void SetTreeLoadingStatus(string message) => RunOnUiThread(() =>
    {
        if (workspaceOperationActive) return;
        SetWorkspaceStage(message);
        workspaceOperationProgress.SetProgress(0, 0);
        workspaceOperationProgress.Visible = !string.IsNullOrWhiteSpace(message);
        actionToolTip.SetToolTip(workspaceOperationStatus, workspaceOperationStatus.Text);
        workspaceOperationPanel.Update();
        workspaceOperationProgress.Update();
    });

    public void ShowStructureTab() => RunOnUiThread(() =>
    {
        if (structureTab != null) tabs.SelectedTab = structureTab;
    });

    public void ShowAutomaticDrawing(CadTreeNode node) => RunOnUiThread(() =>
    {
        automaticDrawing.SetSource(node, FindRelatedDrawing(node));
        if (automaticDrawingTab != null)
        {
            tabs.SelectedTab = automaticDrawingTab;
        }
    });

    public void SetGeneratedDrawing(CadTreeNode source, string drawingPath) =>
        RunOnUiThread(() => automaticDrawing.SetGeneratedDrawing(source, drawingPath));

    public void SetActiveDrawingSource(CadTreeNode source) => RunOnUiThread(() =>
    {
        activeDrawingSource = source;
        if (rootNode?.Kind == CadDocumentKind.Drawing)
        {
            automaticDrawing.SetSource(source, rootNode);
        }
    });

    public void RefreshAutomaticDrawingState() => RunOnUiThread(automaticDrawing.RefreshState);

    public void SetAutomaticDrawingOperationResult(string message) =>
        RunOnUiThread(() => automaticDrawing.SetOperationResult(message));

    public void SetWorkspaceOperationState(bool active, string message)
    {
        if (active && !workspaceOperationActive) Upton.Pdm.ClientShared.ClientDiagnostics.BeginOperation();
        RunOnUiThread(() =>
        {
            if (active || workspaceOperationActive)
                PluginDiagnosticLog.Record(active ? "开始" : "结束", active
                    ? message + "\r\n项目：" + currentProject.Text + "\r\n图档：" + rootNode?.FullPath
                    : "操作已结束，请结合结果记录确认成功或失败。");
            workspaceOperationActive = active;
            SetWorkspaceStage(active ? (string.IsNullOrWhiteSpace(message) ? "PLM工作文件操作" : message.Trim()) : null);
            actionToolTip.SetToolTip(workspaceOperationStatus, workspaceOperationStatus.Text);
            workspaceOperationProgress.SetProgress(0, 0);
            workspaceOperationProgress.Visible = active;
            serviceStatus.Active = active;
            actionToolTip.SetToolTip(serviceStatus, active ? "PLM正在处理工作文件" : serviceStatus.AccessibleDescription);
            structureMenu.Enabled = !active;
            versionMenu.Enabled = !active;
            foreach (TabPage tab in tabs.TabPages) tab.Enabled = !active || tab == diagnosticsTab;
            projectPanel.Enabled = !active;
            UseWaitCursor = active;
            UpdateSelected(SelectedNode);
            if (active)
            {
                RefreshWorkspaceOperationUi();
            }
        });
    }

    public void SetWorkspaceOperationProgress(string message, int completed, int total)
    {
        PluginDiagnosticLog.Record("进度", message);
        RunOnUiThread(() =>
        {
            if (!workspaceOperationActive)
            {
                return;
            }

            SetWorkspaceStage(string.IsNullOrWhiteSpace(message) ? "PLM工作文件操作" : message.Trim());
            actionToolTip.SetToolTip(workspaceOperationStatus, workspaceOperationStatus.Text);
            workspaceOperationProgress.SetProgress(completed, total);
            RefreshWorkspaceOperationUi();
        });
    }

    private void SetWorkspaceStage(string message)
    {
        workspaceStageMessage = message;
        if (string.IsNullOrWhiteSpace(message))
        {
            workspaceElapsedStartedUtc = null;
            workspaceElapsedTimer.Stop();
            workspaceOperationStatus.Text = "运行状态：空闲\r\n当前无进行中的操作";
            return;
        }
        if (!workspaceElapsedStartedUtc.HasValue) workspaceElapsedStartedUtc = DateTime.UtcNow;
        workspaceElapsedTimer.Start();
        UpdateWorkspaceElapsed();
    }

    private void UpdateWorkspaceElapsed()
    {
        if (!workspaceElapsedStartedUtc.HasValue || IsDisposed) return;
        workspaceOperationStatus.Text = "运行状态：" + (workspaceOperationActive ? "处理中" : "正在加载设计树")
            + "  用时 " + (DateTime.UtcNow - workspaceElapsedStartedUtc.Value).ToString(@"hh\:mm\:ss")
            + Environment.NewLine + workspaceStageMessage;
        actionToolTip.SetToolTip(workspaceOperationStatus, workspaceOperationStatus.Text);
    }

    public void ShowVersions(Guid documentId, string fileName, IReadOnlyList<DocumentVersionDto> versions)
    {
        RunOnUiThread(() =>
        {
            displayedVersionDocumentId = documentId;
            displayedVersionFileName = fileName ?? string.Empty;
            versionList.BeginUpdate();
            versionList.ShowItemToolTips = true;
            versionList.Items.Clear();
            foreach (var version in versions)
            {
                var item = new ListViewItem(version.Revision?.Display ?? "-") { Tag = version };
                item.SubItems.Add(version.Status == 1 ? "正式" : "工作");
                item.SubItems.Add(DisplayUserName(version.CreatedBy));
                item.SubItems.Add(version.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                var referenceWarnings = version.ReferenceSnapshot?.ReferenceWarnings ?? new List<string>();
                item.SubItems.Add((version.ChangeNote ?? string.Empty) + (referenceWarnings.Count == 0 ? string.Empty
                    : "　引用提示 " + referenceWarnings.Count + " 项"));
                item.ToolTipText = string.Join(Environment.NewLine, referenceWarnings);
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
            serviceStatus.Online = online;
            serviceStatus.AccessibleDescription = string.IsNullOrWhiteSpace(text) ? (online ? "PLM已连接" : "PLM未连接") : text;
            if (!workspaceOperationActive) actionToolTip.SetToolTip(serviceStatus, serviceStatus.AccessibleDescription);
        });
    }

    public void SetCheckoutReminder(string text, bool critical)
    {
        RunOnUiThread(() =>
        {
            checkoutReminder.Text = text ?? string.Empty;
            checkoutReminder.BackColor = critical ? Color.FromArgb(255, 236, 232) : Color.FromArgb(255, 247, 225);
            checkoutReminder.ForeColor = critical ? Color.FromArgb(176, 45, 36) : Color.FromArgb(155, 91, 15);
            checkoutReminder.Visible = !string.IsNullOrWhiteSpace(text);
        });
    }

    private void ConfigureCheckoutReminder()
    {
        checkoutReminder.Dock = DockStyle.Top;
        checkoutReminder.Height = 38;
        checkoutReminder.Padding = new Padding(8, 5, 8, 5);
        checkoutReminder.AutoEllipsis = true;
        checkoutReminder.TextAlign = ContentAlignment.MiddleLeft;
        checkoutReminder.Visible = false;
    }

    public void SetAuthenticatedUser(string displayName, string username)
    {
        authenticatedUsername = username ?? string.Empty;

        RunOnUiThread(() =>
        {
            loginButton.Text = string.IsNullOrWhiteSpace(displayName) ? "登录" : displayName;
            workHoursButton.Enabled = !string.IsNullOrWhiteSpace(authenticatedUsername);
            plannedTaskTimer.Stop();
            plannedTaskRefreshTimer.Stop();
            SetPlannedTasks(Array.Empty<string>());
            plannedTaskLabel.Text = string.IsNullOrWhiteSpace(authenticatedUsername)
                ? "登录后显示本人负责的计划任务" : "暂无本人负责的计划任务";
            if (!string.IsNullOrWhiteSpace(authenticatedUsername)) plannedTaskTimer.Start();
            if (!string.IsNullOrWhiteSpace(authenticatedUsername)) plannedTaskRefreshTimer.Start();
            loginButton.AccessibleDescription = string.IsNullOrWhiteSpace(username)
                ? loginButton.Text
                : string.Concat(loginButton.Text, "（", username, "）");
            actionToolTip.SetToolTip(loginButton, loginButton.AccessibleDescription);
            projectDocuments.SetAuthenticatedUser(authenticatedUsername);
            RefreshSelectionIndependentStructureActions();
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

    public void SetUserDisplayNames(IReadOnlyDictionary<string, string> displayNames)
    {
        userDisplayNames = displayNames ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        projectDocuments.SetUserDisplayNames(userDisplayNames);
        RunOnUiThread(() =>
        {
            if (rootNode != null)
            {
                RebuildTree(searchBox.Text);
            }
            else
            {
                UpdateSelected(SelectedNode);
            }
        });
    }

    private string DisplayUserName(string username)
    {
        var value = username?.Trim() ?? string.Empty;
        return userDisplayNames.TryGetValue(value, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : value;
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
        RunOnUiThread(() =>
        {
            if (root == null) { ClearTree(); return; }
            var previousRoot = rootNode;
            var key = TreeViewKey(root);
            var changedDocument = !string.Equals(displayedTreeViewKey, key, StringComparison.OrdinalIgnoreCase);
            if (changedDocument)
            {
                RememberTreeViewState();
                treeViewStates.TryGetValue(key, out pendingTreeViewState);
                changedTreeView = true;
                rootNode = root;
                displayedTreeViewKey = key;
                suppressNodeSelectedNotification = true;
                try { structureTree.SelectedNode = null; }
                finally { suppressNodeSelectedNotification = false; }
                UpdateSelected(null);
                restoringTreeViewState = true;
                try { searchBox.Text = pendingTreeViewState?.Filter ?? string.Empty; }
                finally { restoringTreeViewState = false; }
                activeHealthFilter = pendingTreeViewState?.HealthFilter ?? StructureHealthFilter.None;
                selectFirstHealthMatchAfterBuild = false;
                checkedCheckInPaths.Clear();
                if (pendingTreeViewState != null)
                {
                    var available = EnumerateCadTree(root).ToLookup(node => node.FullPath ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                    checkedCheckInPaths.UnionWith(pendingTreeViewState.CheckedPaths.Where(path => available.Contains(path)
                        && (!pendingTreeViewState.CheckedDocumentIds.TryGetValue(path, out var previousId) || !previousId.HasValue
                            || available[path].Any(node => node.DocumentId == previousId))));
                }
            }
            else rootNode = root;
            RefreshSelectionIndependentStructureActions();
            if (ReferenceEquals(previousRoot, root) && !treeBuildPending && activeTreeBuildPlan?.UpdateOpen == false
                && activeHealthFilter != StructureHealthFilter.Missing
                && string.Equals(renderedTreeStamp, TreePresentationStamp(root, searchBox.Text, activeHealthFilter), StringComparison.Ordinal))
            {
                UpdateTreeHealth();
                UpdateSelected(SelectedNode);
                structureTree.Invalidate();
                return;
            }
            RebuildTree(searchBox.Text);
        });
    }

    public void ClearTree()
    {
        RunOnUiThread(() =>
        {
            RememberTreeViewState();
            rootNode = null;
            displayedTreeViewKey = string.Empty;
            pendingTreeViewState = null;
            changedTreeView = false;
            renderedTreeStamp = null;
            treeBuildPending = false;
            activeHealthFilter = StructureHealthFilter.None;
            selectFirstHealthMatchAfterBuild = false;
            Interlocked.Increment(ref treeBuildGeneration);
            CancelActiveTreeBuild();
            structureTree.Nodes.Clear();
            UpdateTreeHealth();
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
        var logo = new PictureBox
        {
            Image = headerLogoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Location = new Point(10, 10),
            Size = new Size(38, 38)
        };
        var title = new Label { Text = "UPLM", ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 10F), Location = new Point(58, 9), AutoSize = true };
        var subtitle = new Label { Text = "阿普顿产品管理系统", ForeColor = Color.FromArgb(184, 201, 220), Location = new Point(58, 32), AutoSize = true };
        serviceStatus.AccessibleName = "PLM连接状态";
        serviceStatus.AccessibleDescription = "未连接";
        serviceStatus.Online = false;
        serviceStatus.Active = false;
        serviceStatus.Cursor = Cursors.Help;
        actionToolTip.SetToolTip(serviceStatus, "PLM未连接");
        serviceStatus.Size = new Size(48, 40);
        serviceStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        serviceStatus.Location = new Point(header.ClientSize.Width - header.Padding.Right - serviceStatus.Width, 11);
        header.Controls.Add(logo);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(serviceStatus);
        header.Controls.Add(pluginVersion);
        header.Layout += (_, _) => PositionPluginVersion();
        return header;
    }

    private static Image LoadHeaderLogoImage()
    {
        const string resourceName = "Upton.Pdm.SolidWorks.Assets.PdmClient.png";
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
        {
            if (stream == null)
            {
                return null;
            }

            using (var source = Image.FromStream(stream))
            {
                var bitmap = new Bitmap(38, 38, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                }

                return bitmap;
            }
        }
    }

    private Control BuildProjectPanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.White, Padding = new Padding(12, 6, 12, 6) };
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 36,
            ColumnCount = 5,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(3)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        var clientButtonColumn = new ColumnStyle(SizeType.Absolute, 60);
        actions.ColumnStyles.Add(clientButtonColumn);
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
        var userButtonColumn = new ColumnStyle(SizeType.Absolute, 60);
        actions.ColumnStyles.Add(userButtonColumn);
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        actions.SizeChanged += (_, _) =>
        {
            MatchProjectButtonWidths(actions, clientButtonColumn, userButtonColumn);
            projectDocuments.BrowseButtonWidth = userButtonColumn.Width;
        };
        currentProject.ReadOnly = true;
        currentProject.TabStop = false;
        currentProject.ShortcutsEnabled = false;
        currentProject.AutoSize = false;
        currentProject.Dock = DockStyle.Fill;
        currentProject.Margin = Padding.Empty;
        currentProject.BackColor = Color.White;
        currentProject.AccessibleName = "当前项目号（只读）";
        openClientButton.Text = "客户端";
        ConfigureStructureToolbarButton(openClientButton);
        actionToolTip.SetToolTip(openClientButton, "打开PLM客户端");
        openClientButton.Click += (_, _) => OpenClientRequested?.Invoke(this, EventArgs.Empty);
        loginButton.Text = "登录";
        ConfigureStructureToolbarButton(loginButton);
        loginButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        workHoursButton.Text = "工时";
        workHoursButton.AccessibleName = "填写工时";
        ConfigureStructureToolbarButton(workHoursButton);
        workHoursButton.Enabled = false;
        actionToolTip.SetToolTip(workHoursButton, "打开工时管理，填写本人工时");
        workHoursButton.Click += (_, _) => WorkHoursRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(currentProject, 0, 0);
        actions.Controls.Add(openClientButton, 2, 0);
        actions.Controls.Add(loginButton, 4, 0);
        plannedTaskBar.Dock = DockStyle.Fill;
        plannedTaskBar.Margin = Padding.Empty;
        plannedTaskBar.BorderStyle = BorderStyle.FixedSingle;
        plannedTaskBar.AccessibleName = "本人负责子任务计划完成日期";
        foreach (var label in new[] { plannedTaskLabel, nextPlannedTaskLabel })
        {
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            plannedTaskBar.Controls.Add(label);
        }
        plannedTaskLabel.Text = "登录后显示本人负责的计划任务";
        plannedTaskBar.SizeChanged += (_, _) =>
        {
            plannedTaskLabel.Size = nextPlannedTaskLabel.Size = plannedTaskBar.ClientSize;
            plannedTaskLabel.Top = 0;
            nextPlannedTaskLabel.Top = plannedTaskBar.ClientSize.Height;
        };
        plannedTaskRefreshTimer.Tick += (_, _) => PlannedTasksRefreshRequested?.Invoke(this, EventArgs.Empty);
        plannedTaskTimer.Tick += (_, _) =>
        {
            if (plannedTasks.Length < 2) return;
            plannedTaskIndex = (plannedTaskIndex + 1) % plannedTasks.Length;
            nextPlannedTaskLabel.Text = plannedTasks[plannedTaskIndex];
            nextPlannedTaskLabel.Top = plannedTaskBar.ClientSize.Height;
            plannedTaskAnimation.Start();
        };
        plannedTaskAnimation.Tick += (_, _) =>
        {
            var offset = Math.Min(2, nextPlannedTaskLabel.Top);
            plannedTaskLabel.Top -= offset;
            nextPlannedTaskLabel.Top -= offset;
            if (nextPlannedTaskLabel.Top > 0) return;
            plannedTaskAnimation.Stop();
            var previous = plannedTaskLabel;
            plannedTaskLabel = nextPlannedTaskLabel;
            nextPlannedTaskLabel = previous;
            nextPlannedTaskLabel.Top = plannedTaskBar.ClientSize.Height;
        };
        actions.Controls.Add(plannedTaskBar, 0, 2);
        actions.SetColumnSpan(plannedTaskBar, 3);
        actions.Controls.Add(workHoursButton, 4, 2);
        panel.Controls.Add(actions);
        UpdateCurrentProjectDisplay();
        return panel;
    }

    private void UpdateCurrentProjectDisplay()
    {
        var project = selectedProjectId.HasValue
            ? availableProjects.FirstOrDefault(item => item.Id == selectedProjectId.Value)
            : null;
        currentProject.Text = project == null
            ? (projectContextAvailable ? "未关联" : "未打开图档")
            : string.IsNullOrWhiteSpace(project.Name)
                ? project.Code
                : string.Concat(project.Code, " - ", project.Name.Trim());
        actionToolTip.SetToolTip(currentProject, currentProject.Text);
    }

    private void BuildTabs()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.Padding = new Point(0, 6);
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ShowToolTips = true;
        tabs.FontChanged += (_, _) => ResizeTabHeaders();
        structureTab = BuildStructureTab();
        tabs.TabPages.Add(structureTab);
        var projectTab = new TabPage("工作区") { BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(8) };
        projectDocuments.OpenRequested += (_, args) =>
        {
            ControlledOpenRequested?.Invoke(this, args);
            tabs.SelectedTab = structureTab;
        };
        projectDocuments.ProjectSelected += (_, args) => ProjectBrowseRequested?.Invoke(this, args);
        projectTab.Controls.Add(projectDocuments);
        tabs.TabPages.Add(projectTab);
        automaticDrawingTab = new TabPage("自动出图") { BackColor = Color.FromArgb(244, 247, 251), Padding = Padding.Empty };
        automaticDrawing.GenerateRequested += (_, args) => AutomaticDrawingGenerateRequested?.Invoke(this, args);
        automaticDrawing.AcquireEditRequested += (_, args) => CheckoutRequested?.Invoke(this, args);
        automaticDrawing.OpenRequested += (_, args) => AutomaticDrawingOpenRequested?.Invoke(this, args);
        automaticDrawing.ImportAnnotationsRequested += (_, args) => AutomaticDrawingImportAnnotationsRequested?.Invoke(this, args);
        automaticDrawing.SubmitRequested += (_, args) => AutomaticDrawingSubmitRequested?.Invoke(this, args);
        automaticDrawingTab.Controls.Add(automaticDrawing);
        tabs.TabPages.Add(automaticDrawingTab);
        versionsTab = BuildVersionsTab();
        tabs.TabPages.Add(versionsTab);
        diagnosticsTab = new TabPage("日志") { BackColor = Color.White, Padding = new Padding(8) };
        diagnosticsTab.Controls.Add(new DiagnosticLogControl(installedPluginVersion));
        tabs.TabPages.Add(diagnosticsTab);
        tabs.TabPages.Add(new TabPage { AccessibleName = "预留页签", ToolTipText = "预留页签", BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(8) });
        foreach (TabPage tab in tabs.TabPages)
            if (!string.IsNullOrWhiteSpace(tab.Text)) tab.ToolTipText = tab.Text;
        PluginDiagnosticLog.Record("启动", "插件已加载，版本：" + installedPluginVersion);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (!suppressVersionTabRequest && tabs.SelectedTab == versionsTab)
            {
                RaiseVersionsRequested();
            }
        };
    }

    private void ConfigurePluginVersion()
    {
        pluginVersion.AutoSize = true;
        pluginVersion.BackColor = Color.FromArgb(31, 49, 72);
        pluginVersion.ForeColor = Color.FromArgb(184, 201, 220);
        pluginVersion.Font = new Font("Microsoft YaHei UI", 8F);
        pluginVersion.Text = FormatDisplayVersion(installedPluginVersion);
        pluginVersion.AccessibleName = string.IsNullOrWhiteSpace(installedPluginVersion)
            ? "插件版本未知"
            : string.Concat("插件版本 ", installedPluginVersion);
        actionToolTip.SetToolTip(pluginVersion, pluginVersion.AccessibleName);
    }

    private void ResizeTabHeaders()
    {
        if (tabs.TabCount == 0 || tabsHost.ClientSize.Width <= 4) return;
        var itemSize = new Size(Math.Max(1, (tabsHost.ClientSize.Width - 4) / tabs.TabCount), Math.Max(27, tabs.Font.Height + 12));
        if (tabs.ItemSize != itemSize) tabs.ItemSize = itemSize;
    }

    public void ApplyTheme(string theme)
    {
        RunOnUiThread(() => tabs.ApplyTheme(theme));
    }

    private void PositionPluginVersion()
    {
        var header = pluginVersion.Parent;
        if (header == null || header.ClientSize.Width <= 0) return;
        var title = header.Controls.OfType<Label>().FirstOrDefault(label => label.Text == "UPLM");
        var left = (title?.Right ?? 96) + 8;
        var right = header.ClientSize.Width - header.Padding.Right - serviceStatus.Width - 8;
        var fullText = FormatDisplayVersion(installedPluginVersion);
        var fullWidth = TextRenderer.MeasureText(fullText, pluginVersion.Font).Width;
        var text = right - left >= fullWidth ? fullText : "V";
        if (pluginVersion.Text != text) pluginVersion.Text = text;
        pluginVersion.Location = new Point(Math.Max(left, right - pluginVersion.PreferredWidth), 12);
        pluginVersion.BringToFront();
    }

    private static string FormatDisplayVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "版本未知";
        var separator = version.IndexOf('-');
        return string.Concat("V", separator > 0 ? version.Substring(0, separator) : version);
    }

    private TabPage BuildStructureTab()
    {
        var tab = new TabPage("设计树") { BackColor = Color.FromArgb(244, 247, 251), Padding = new Padding(8) };
        var actions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(3) };
        ConfigureStructureActionColumns(actions);
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        ConfigureCompactActionButton(checkoutButton, "获取", CheckoutAvailableColor);
        ConfigureCompactActionButton(checkinButton, "存档", SubmitAvailableColor);
        ConfigureCompactActionButton(propertyEditButton, "属性", SecondaryActionAvailableColor);
        ConfigureCompactActionButton(settingsButton, "设置", SecondaryActionAvailableColor);
        actionToolTip.SetToolTip(checkoutButton, "获取编辑权限");
        actionToolTip.SetToolTip(checkinButton, "选择存档范围，统一检查后整批提交；未勾选时自动选择整套变更项");
        actionToolTip.SetToolTip(propertyEditButton, "批量编辑图档属性，或在属性窗口内设置SolidWorks原生属性卡");
        actionToolTip.SetToolTip(settingsButton, "设置服务器连接，查看插件版本与更新状态");
        checkoutButton.Click += (_, _) => RaiseCheckoutToggle();
        checkinButton.Click += (_, _) => RaiseCheckInRequested();
        propertyEditButton.Click += (_, _) => RaiseBatchPropertyRequested();
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        checkoutButton.Enabled = false;
        checkinButton.Enabled = false;
        propertyEditButton.Enabled = false;
        settingsButton.Enabled = true;
        ApplyStructureActionButtonAppearances();
        actions.Controls.Add(checkoutButton, 0, 0);
        actions.Controls.Add(checkinButton, 2, 0);
        actions.Controls.Add(propertyEditButton, 4, 0);
        actions.Controls.Add(settingsButton, 6, 0);

        workspaceOperationPanel.Dock = DockStyle.Bottom;
        workspaceOperationPanel.Height = 72;
        workspaceOperationPanel.Padding = new Padding(3, 3, 3, 3);
        workspaceOperationPanel.BackColor = Color.FromArgb(232, 243, 255);
        workspaceOperationPanel.Visible = true;
        var workspaceOperationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        workspaceOperationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspaceOperationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        workspaceOperationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspaceOperationStatus.Text = string.Concat("运行状态：空闲", Environment.NewLine, "当前无进行中的操作");
        workspaceOperationStatus.Dock = DockStyle.Fill;
        workspaceOperationStatus.Margin = Padding.Empty;
        workspaceOperationStatus.AutoEllipsis = false;
        workspaceOperationStatus.UseCompatibleTextRendering = true;
        workspaceOperationStatus.ForeColor = Color.FromArgb(31, 89, 147);
        workspaceOperationStatus.TextAlign = ContentAlignment.MiddleLeft;
        workspaceOperationProgress.Dock = DockStyle.Fill;
        workspaceOperationProgress.Margin = new Padding(0, 3, 0, 0);
        workspaceOperationLayout.Controls.Add(workspaceOperationStatus, 0, 0);
        workspaceOperationLayout.Controls.Add(workspaceOperationProgress, 0, 1);
        workspaceOperationPanel.Controls.Add(workspaceOperationLayout);

        var searchToolbar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 1, RowCount = 1, Margin = Padding.Empty };
        searchToolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchBox.AutoSize = false;
        searchBox.Dock = DockStyle.Fill;
        searchBox.Margin = new Padding(3);
        searchBox.AccessibleName = "搜索图号、名称或文件名";
        searchBox.TextChanged += (_, _) => { if (!restoringTreeViewState) RebuildTree(searchBox.Text); };
        searchToolbar.Controls.Add(searchBox, 0, 0);

        treeHealth.Dock = DockStyle.Top;
        treeHealth.Height = 29;
        treeHealth.Padding = new Padding(6, 5, 6, 3);
        treeHealth.ForeColor = Color.FromArgb(90, 107, 128);
        treeHealth.BackColor = Color.FromArgb(247, 249, 252);
        treeHealth.AutoEllipsis = true;
        treeHealth.Text = "结构健康：等待读取";
        treeHealth.LinkBehavior = LinkBehavior.HoverUnderline;
        treeHealth.LinkColor = Color.FromArgb(174, 94, 0);
        treeHealth.ActiveLinkColor = Color.FromArgb(112, 65, 160);
        treeHealth.LinkClicked += (_, eventArgs) =>
        {
            if (eventArgs.Link.LinkData is StructureHealthFilter filter)
            {
                ToggleHealthFilter(filter);
            }
        };

        structureTree.Dock = DockStyle.Fill;
        structureTree.BorderStyle = BorderStyle.None;
        structureTree.HideSelection = false;
        structureTree.FullRowSelect = true;
        structureTree.CheckBoxes = false;
        structureTree.ShowNodeToolTips = true;
        structureTree.ImageList = structureImages;
        structureTree.ItemHeight = Math.Max(structureTree.ItemHeight, 20);
        structureTree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        structureTree.DrawNode += DrawStructureNode;
        structureTree.ClientSizeChanged += (_, _) => structureTreeSurface.Invalidate();
        structureTree.BeforeExpand += (_, eventArgs) => MaterializeChildren(eventArgs.Node, activeTreeBuildPlan);
        structureTree.BeforeCollapse += (_, eventArgs) => eventArgs.Cancel = !allowStructureCollapse;
        structureTree.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Left && structureTree.SelectedNode?.IsExpanded == true)
            {
                CollapseStructureNode(structureTree.SelectedNode);
                eventArgs.SuppressKeyPress = true;
            }
        };
        structureTree.BeforeCheck += (_, eventArgs) =>
        {
            if (!suppressTreeCheckEvents
                && eventArgs.Action != TreeViewAction.Unknown
                && (!(eventArgs.Node.Tag is CadTreeNode node) || !CanSelectForBatchAction(node)))
            {
                eventArgs.Cancel = true;
            }
        };
        structureTree.AfterCheck += (_, eventArgs) =>
        {
            if (suppressTreeCheckEvents || !(eventArgs.Node.Tag is CadTreeNode node))
            {
                return;
            }

            SynchronizeCheckedDocument(node, eventArgs.Node.Checked);
            UpdateSelected(SelectedNode);
        };
        structureTreeSurface.Dock = DockStyle.Fill;
        structureTreeSurface.BorderStyle = BorderStyle.FixedSingle;
        structureTreeSurface.BackColor = Color.White;
        structureTreeSurface.Padding = new Padding(0, 23, 0, 0);
        structureTreeSurface.Paint += DrawStructureHeader;
        structureTreeSurface.MouseDown += OnStructureHeaderMouseDown;
        structureTreeSurface.MouseMove += OnStructureHeaderMouseMove;
        structureTreeSurface.MouseUp += (_, args) =>
        {
            if (args.Button != MouseButtons.Left || draggingStructureDivider == 0) return;
            UpdateStructureColumnWidths(args.X);
            draggingStructureDivider = 0;
            structureTreeSurface.Capture = false;
            structureTreeSurface.Cursor = StructureHeaderDividerAt(args.Location) != 0 ? Cursors.VSplit : Cursors.Default;
            SaveStructureColumnWidths();
        };
        structureTreeSurface.MouseCaptureChanged += (_, _) =>
        {
            if (structureTreeSurface.Capture || draggingStructureDivider == 0) return;
            draggingStructureDivider = 0;
            structureTreeSurface.Cursor = Cursors.Default;
            SaveStructureColumnWidths();
        };
        structureTreeSurface.MouseLeave += (_, _) =>
        {
            if (draggingStructureDivider == 0) structureTreeSurface.Cursor = Cursors.Default;
        };
        actionToolTip.SetToolTip(structureTreeSurface, "拖动表头分隔线调整列宽，自动记住上次设置");
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
        structureTree.MouseDown += (_, eventArgs) =>
        {
            var rowNode = GetStructureNodeAtRow(eventArgs.Y);
            GetStructureColumns(structureTree.ClientSize.Width, out var nameDividerX, out var versionDividerX);
            if (eventArgs.Button == MouseButtons.Left && eventArgs.X < StructureSelectionColumnWidth)
            {
                if (rowNode?.Tag is CadTreeNode model && CanSelectForBatchAction(model))
                {
                    structureTree.SelectedNode = rowNode;
                    rowNode.Checked = !rowNode.Checked;
                }
                return;
            }

            if (eventArgs.Button == MouseButtons.Left
                && eventArgs.X < nameDividerX
                && rowNode != null
                && HasExpandableChildren(rowNode)
                && GetStructureExpanderBounds(rowNode).Contains(eventArgs.Location))
            {
                if (rowNode.IsExpanded)
                {
                    CollapseStructureNode(rowNode);
                }
                else
                {
                    rowNode.Expand();
                }
                structureTree.Invalidate();
                return;
            }

            if ((eventArgs.Button == MouseButtons.Left || eventArgs.Button == MouseButtons.Right) && rowNode != null)
            {
                structureTree.SelectedNode = rowNode;
            }
        };

        var treeHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 0, 3, 3) };
        treeHost.Controls.Add(structureTreeSurface);

        var detail = new Panel { Dock = DockStyle.Bottom, Height = 118, BackColor = Color.White, Padding = new Padding(9, 7, 9, 5) };
        selectedFile.Text = "未选择图档";
        selectedFile.Dock = DockStyle.Top;
        selectedFile.Height = 22;
        selectedMeta.Text = "配置、版本和编辑状态";
        selectedMeta.Dock = DockStyle.Top;
        selectedMeta.Height = 80;
        selectedMeta.ForeColor = Color.FromArgb(111, 128, 149);
        selectedMeta.AutoEllipsis = true;
        detail.Controls.Add(selectedMeta);
        detail.Controls.Add(selectedFile);

        tab.Controls.Add(treeHost);
        tab.Controls.Add(detail);
        tab.Controls.Add(treeHealth);
        tab.Controls.Add(searchToolbar);
        tab.Controls.Add(actions);
        return tab;
    }

    private TreeNode GetStructureNodeAtRow(int y)
    {
        for (var node = structureTree.TopNode; node != null; node = node.NextVisibleNode)
        {
            if (y >= node.Bounds.Top && y < node.Bounds.Bottom)
            {
                return node;
            }
        }

        return null;
    }

    private void ApplyContentTypography(Control control)
    {
        control.Font = Font;
        if (control is Label label)
        {
            label.UseCompatibleTextRendering = false;
        }
        else if (control is ButtonBase button)
        {
            button.UseCompatibleTextRendering = false;
        }

        foreach (Control child in control.Controls)
        {
            ApplyContentTypography(child);
        }
    }

    private void DrawStructureHeader(object sender, PaintEventArgs eventArgs)
    {
        var graphicsState = eventArgs.Graphics.Save();
        eventArgs.Graphics.TranslateTransform(structureTree.Left, 0);
        var contentWidth = structureTree.ClientSize.Width > 0
            ? structureTree.ClientSize.Width
            : structureTreeSurface.ClientSize.Width;
        GetStructureColumns(contentWidth, out var nameDividerX, out var versionDividerX);
        using (var background = new SolidBrush(Color.FromArgb(242, 244, 247)))
        using (var border = new Pen(Color.FromArgb(205, 210, 217)))
        {
            eventArgs.Graphics.FillRectangle(background, 0, 0, structureTreeSurface.ClientSize.Width, 23);
            eventArgs.Graphics.DrawLine(border, 0, 22, structureTreeSurface.ClientSize.Width, 22);
            eventArgs.Graphics.DrawLine(border, StructureSelectionColumnWidth, 0, StructureSelectionColumnWidth, structureTreeSurface.ClientSize.Height);
            eventArgs.Graphics.DrawLine(border, nameDividerX, 0, nameDividerX, structureTreeSurface.ClientSize.Height);
            eventArgs.Graphics.DrawLine(border, versionDividerX, 0, versionDividerX, structureTreeSurface.ClientSize.Height);
        }

        TextRenderer.DrawText(eventArgs.Graphics, "选", Font, new Rectangle(0, 0, StructureSelectionColumnWidth, 22), Color.FromArgb(70, 82, 96), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsTranslateTransform);
        TextRenderer.DrawText(eventArgs.Graphics, "名称", Font, new Rectangle(StructureSelectionColumnWidth + 5, 0, Math.Max(0, nameDividerX - StructureSelectionColumnWidth - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsTranslateTransform);
        TextRenderer.DrawText(eventArgs.Graphics, "图纸 / 发布", Font, new Rectangle(nameDividerX + 5, 0, Math.Max(0, versionDividerX - nameDividerX - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsTranslateTransform);
        TextRenderer.DrawText(eventArgs.Graphics, "状态", Font, new Rectangle(versionDividerX + 5, 0, Math.Max(0, contentWidth - versionDividerX - 9), 22), Color.FromArgb(70, 82, 96), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsTranslateTransform);
        eventArgs.Graphics.Restore(graphicsState);
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
            eventArgs.Graphics.DrawLine(border, StructureSelectionColumnWidth, eventArgs.Bounds.Top, StructureSelectionColumnWidth, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, nameDividerX, eventArgs.Bounds.Top, nameDividerX, eventArgs.Bounds.Bottom);
            eventArgs.Graphics.DrawLine(border, versionDividerX, eventArgs.Bounds.Top, versionDividerX, eventArgs.Bounds.Bottom);
        }

        var checkBoxSize = Math.Min(14, Math.Max(10, eventArgs.Bounds.Height - 4));
        var checkBoxBounds = new Rectangle(
            Math.Max(2, (StructureSelectionColumnWidth - checkBoxSize) / 2),
            eventArgs.Bounds.Top + Math.Max(0, (eventArgs.Bounds.Height - checkBoxSize) / 2),
            checkBoxSize,
            checkBoxSize);
        var checkState = eventArgs.Node.Checked ? ButtonState.Checked : ButtonState.Normal;
        if (model == null || !CanSelectForBatchAction(model))
        {
            checkState |= ButtonState.Inactive;
        }
        ControlPaint.DrawCheckBox(eventArgs.Graphics, checkBoxBounds, checkState);

        var nameGraphicsState = eventArgs.Graphics.Save();
        eventArgs.Graphics.SetClip(new Rectangle(StructureSelectionColumnWidth, eventArgs.Bounds.Top,
            Math.Max(0, nameDividerX - StructureSelectionColumnWidth), eventArgs.Bounds.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
        var expanderBounds = GetStructureExpanderBounds(eventArgs.Node);
        if (HasExpandableChildren(eventArgs.Node))
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

        var structureLeft = expanderBounds.Right + 4;
        var structureBounds = new Rectangle(structureLeft, eventArgs.Bounds.Top, Math.Max(0, nameDividerX - structureLeft - 4), eventArgs.Bounds.Height);
        DrawNodeImage(eventArgs.Graphics, eventArgs.Node, structureBounds, selected);
        structureBounds.X += 20;
        structureBounds.Width = Math.Max(0, structureBounds.Width - 20);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Node.Text, font, structureBounds, structureColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        eventArgs.Graphics.Restore(nameGraphicsState);
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

    private Rectangle GetStructureExpanderBounds(TreeNode node)
    {
        var left = StructureSelectionColumnWidth
            + StructureTreeLeftPadding
            + Math.Max(0, node?.Level ?? 0) * StructureTreeIndentWidth;
        var top = (node?.Bounds.Top ?? 0) + Math.Max(0, ((node?.Bounds.Height ?? structureTree.ItemHeight) - StructureExpanderSize) / 2);
        return new Rectangle(left, top, StructureExpanderSize, StructureExpanderSize);
    }

    private bool HasExpandableChildren(TreeNode node) => node != null
        && (node.Nodes.Cast<TreeNode>().Any(child => !(child.Tag is LazyTreePlaceholder))
            || activeTreeBuildPlan?.ChildrenByParent.ContainsKey(node) == true);

    private void GetStructureColumns(int width, out int nameDividerX, out int versionDividerX)
    {
        var usable = Math.Max(1, width);
        if (structureNameWidth > 0 && structureVersionWidth > 0)
        {
            var minimumName = StructureSelectionColumnWidth + ScaleStructureWidth(64);
            var minimumVersion = ScaleStructureWidth(60);
            var minimumStatus = ScaleStructureWidth(48);
            nameDividerX = Math.Max(minimumName, Math.Min(usable - minimumVersion - minimumStatus,
                StructureSelectionColumnWidth + ScaleStructureWidth(structureNameWidth)));
            versionDividerX = Math.Max(nameDividerX + minimumVersion, Math.Min(usable - minimumStatus,
                nameDividerX + ScaleStructureWidth(structureVersionWidth)));
            return;
        }
        if (usable < 320)
        {
            nameDividerX = Math.Max(74, usable * 40 / 100);
            versionDividerX = Math.Max(nameDividerX + 72, usable * 74 / 100);
            versionDividerX = Math.Min(Math.Max(nameDividerX + 1, usable - 58), versionDividerX);
            return;
        }

        var versionWidth = StructureVersionColumnWidth;
        var statusWidth = Math.Max(82, Math.Min(122, usable * 24 / 100));
        nameDividerX = usable - versionWidth - statusWidth;
        versionDividerX = usable - statusWidth;
    }

    private int ScaleStructureWidth(int width) => (int)Math.Round(width * DeviceDpi / 96d);

    private int StructureHeaderDividerAt(Point location)
    {
        if (location.Y < 0 || location.Y >= structureTree.Top) return 0;
        GetStructureColumns(structureTree.ClientSize.Width, out var nameX, out var versionX);
        var x = location.X - structureTree.Left;
        if (Math.Abs(x - nameX) <= 4) return 1;
        return Math.Abs(x - versionX) <= 4 ? 2 : 0;
    }

    private void OnStructureHeaderMouseDown(object sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left) return;
        draggingStructureDivider = StructureHeaderDividerAt(args.Location);
        if (draggingStructureDivider == 0) return;
        structureTreeSurface.Cursor = Cursors.VSplit;
        structureTreeSurface.Capture = true;
    }

    private void OnStructureHeaderMouseMove(object sender, MouseEventArgs args)
    {
        if (draggingStructureDivider != 0) UpdateStructureColumnWidths(args.X);
        structureTreeSurface.Cursor = draggingStructureDivider != 0 || StructureHeaderDividerAt(args.Location) != 0
            ? Cursors.VSplit : Cursors.Default;
    }

    private void UpdateStructureColumnWidths(int mouseX)
    {
        GetStructureColumns(structureTree.ClientSize.Width, out var nameX, out var versionX);
        var x = mouseX - structureTree.Left;
        if (draggingStructureDivider == 1)
            nameX = Math.Max(StructureSelectionColumnWidth + ScaleStructureWidth(64), Math.Min(versionX - ScaleStructureWidth(60), x));
        else
            versionX = Math.Max(nameX + ScaleStructureWidth(60), Math.Min(structureTree.ClientSize.Width - ScaleStructureWidth(48), x));
        structureNameWidth = (int)Math.Round((nameX - StructureSelectionColumnWidth) * 96d / DeviceDpi);
        structureVersionWidth = (int)Math.Round((versionX - nameX) * 96d / DeviceDpi);
        structureTreeSurface.Invalidate();
        structureTree.Invalidate();
    }

    private void LoadStructureColumnWidths()
    {
        try
        {
            if (!File.Exists(StructureColumnSettingsPath)) return;
            var widths = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, int>>(File.ReadAllText(StructureColumnSettingsPath, Encoding.UTF8));
            if (widths != null && widths.TryGetValue("name", out var name) && widths.TryGetValue("version", out var version)
                && name >= 64 && name <= 4000 && version >= 60 && version <= 4000)
            {
                structureNameWidth = name;
                structureVersionWidth = version;
            }
        }
        catch { /* Invalid or unavailable preferences use the default column widths. */ }
    }

    private void SaveStructureColumnWidths()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StructureColumnSettingsPath));
            File.WriteAllText(StructureColumnSettingsPath, new System.Web.Script.Serialization.JavaScriptSerializer()
                .Serialize(new { name = structureNameWidth, version = structureVersionWidth }), new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Upton.Pdm.ClientShared.ClientDiagnostics.Record("error", "保存设计树列宽", exception.ToString());
            contextHint.Text = "列宽已调整，但未能保存；请检查本机设置目录的写入权限和磁盘空间。";
        }
    }

    private sealed class StructureTreeView : TreeView
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= 0x8000; // TVS_NOHSCROLL: custom columns stay fixed while the tree scrolls vertically.
                return parameters;
            }
        }
    }

    private static string VersionText(CadTreeNode node)
    {
        if (node.IsStoredImport) return "随父装配";
        if (node.IsInterconnectImport) return "随总装核对";
        if (node.Status == CadReferenceStatus.Virtual || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)) return "随父装配";
        if (node.StoredVersionStateKnown && !node.HasStoredVersion)
        {
            return "— / —";
        }

        return node.BusinessVersionText;
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
        contextOpenWorkingFile.Text = "打开此图档";
        contextUpdateLatest.Text = "更新到最新版本";
        contextVersionInfo.Enabled = false;
        contextCheckout.Text = "获取编辑权限";
        contextCheckIn.Text = "提交存档";
        contextDiscardCheckout.Text = "放弃编辑";
        contextGenerateDrawing.Text = "生成工程图...";
        contextOpenDrawing.Text = "打开关联工程图";
        contextDrawingVersions.Text = "关联工程图版本...";
        contextVersions.Text = "查看/对比历史版本...";
        contextRefresh.Text = "刷新设计树";
        contextWhereUsed.Text = "使用位置...";
        contextRequestRelease.Text = "我需要编辑";
        contextOpenReleaseCenter.Text = "进入审批与发布...";
        contextWithdrawApproval.Text = "撤回当前审批...";
        contextObsolete.Text = "作废图档...";
        contextZoomSelection.Text = "放大所选范围";
        contextIsolate.Text = "隔离显示";
        contextExitIsolate.Text = "退出隔离";
        contextOpenFolder.Text = "在资源管理器中定位";
        contextRenameDocument.Text = "重命名图档...";
        contextSelectEligible.Text = "全选可提交项";
        contextSelectChanged.Text = "选择已修改/待提交";
        contextSelectMine.Text = "选择我检出的图档";
        contextClearSelection.Text = "清除选择";
        contextExpandAll.Text = "全部展开";
        contextCollapseAll.Text = "全部折叠";
        contextHint.AutoSize = false;
        contextHint.Size = new Size(110, 58);
        contextHint.MinimumSize = contextHint.Size;
        contextHint.MaximumSize = contextHint.Size;
        contextHint.Padding = new Padding(5, 2, 5, 2);
        contextHint.TextAlign = ContentAlignment.MiddleLeft;
        contextHint.ForeColor = Color.FromArgb(111, 128, 149);
        contextHint.BackColor = SystemColors.Menu;

        contextOpenWorkingFile.Click += (_, _) => RaiseSelected(OpenWorkingFileRequested);
        contextUpdateLatest.Click += (_, _) => RaiseSelected(UpdateLatestRequested);
        contextCheckout.Click += (_, _) => RaiseSelected(CheckoutRequested);
        contextCheckIn.Click += (_, _) => RaiseSelected(CheckInRequested);

        contextDiscardCheckout.Click += (_, _) => RaiseSelected(DiscardCheckoutRequested);
        contextGenerateDrawing.Click += (_, _) => ShowAutomaticDrawing(SelectedNode);
        contextOpenDrawing.Click += (_, _) =>
        {
            automaticDrawing.SetSource(SelectedNode);
            automaticDrawing.RequestOpen();
        };
        contextDrawingVersions.Click += (_, _) =>
        {
            var drawing = FindRelatedDrawing(SelectedNode);
            if (drawing != null)
            {
                VersionsRequested?.Invoke(this, new CadTreeNodeEventArgs(drawing));
            }
        };
        contextVersions.Click += (_, _) => RaiseSelected(VersionsRequested);
        contextRefresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        contextWhereUsed.Click += (_, _) => RaiseSelected(WhereUsedRequested);
        contextRequestRelease.Click += (_, _) => RaiseSelected(RequestReleaseRequested);
        contextOpenReleaseCenter.Click += (_, _) => OpenReleaseCenterRequested?.Invoke(this, EventArgs.Empty);
        contextWithdrawApproval.Click += (_, _) => RaiseSelected(WithdrawApprovalRequested);
        contextObsolete.Click += (_, _) => RaiseSelected(ObsoleteRequested);
        contextZoomSelection.Click += (_, _) => RaiseSelected(ZoomSelectionRequested);
        contextIsolate.Click += (_, _) => RaiseSelected(IsolateRequested);
        contextExitIsolate.Click += (_, _) => ExitIsolateRequested?.Invoke(this, EventArgs.Empty);
        contextOpenFolder.Click += (_, _) => RaiseSelected(OpenContainingFolderRequested);
        contextRenameDocument.Click += (_, _) => RaiseSelected(RenameDocumentRequested);
        contextSelectEligible.Click += (_, _) => SelectTreeDocuments(CanCheckInNode);
        contextSelectChanged.Click += (_, _) => SelectTreeDocuments(node => CanCheckInNode(node)
            && (node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved || node.WorkState == CadWorkState.PendingCheckIn));
        contextSelectMine.Click += (_, _) => SelectTreeDocuments(node => CanCheckInNode(node)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase));
        contextClearSelection.Click += (_, _) => SelectTreeDocuments(_ => false);
        contextExpandAll.Click += (_, _) => ExpandAllMaterialized();
        contextCollapseAll.Click += (_, _) => CollapseAllStructureNodes();

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
        var modelMenu = new ToolStripMenuItem("模型显示");
        modelMenu.DropDownItems.AddRange(new ToolStripItem[] { contextZoomSelection, contextIsolate, contextExitIsolate });
        var selectionMenu = new ToolStripMenuItem("批量选择与树");
        selectionMenu.DropDownItems.AddRange(new ToolStripItem[] { contextSelectEligible, contextSelectChanged, contextSelectMine, contextClearSelection, new ToolStripSeparator(), contextExpandAll, contextCollapseAll });
        var lifecycleMenu = new ToolStripMenuItem("业务状态");
        lifecycleMenu.DropDownItems.AddRange(new ToolStripItem[] { contextOpenReleaseCenter, contextWithdrawApproval, contextObsolete });
        structureMenu.Items.AddRange(new ToolStripItem[]
        {
            contextOpenWorkingFile,
            contextUpdateLatest,
            contextVersions,
            contextVersionInfo,
            new ToolStripSeparator(),
            contextWhereUsed,
            contextOpenFolder,
            contextRenameDocument,
            modelMenu,
            selectionMenu,
            lifecycleMenu,
            new ToolStripSeparator(),
            contextCheckout,
            contextRequestRelease,
            contextDiscardCheckout,
            contextCheckIn,

            new ToolStripSeparator(),
            contextGenerateDrawing,
            contextOpenDrawing,
            contextDrawingVersions,
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
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            && !node.CheckoutSessionLost;
        var canRecoverCheckout = node.CheckoutSessionLost
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        var canRegister = !registered && localFileExists && canOpenInSolidWorks;
        var historicalPreview = node.IsHistoricalPreview;
        var latestReadOnlyPreview = node.IsLatestReadOnlyPreview;
        var readOnlyPreview = node.IsReadOnlyPreview;
        var lifecycleInReview = string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase);
        var lifecycleObsolete = string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase);
        var drawingReviewLocked = node.DrawingReviewLocked;
        var drawingSource = node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly;
        var drawingPath = AutomaticDrawingControl.GetDrawingPath(node);
        var drawingExists = drawingSource && !string.IsNullOrWhiteSpace(drawingPath) && File.Exists(drawingPath);
        var relatedDrawing = drawingExists ? FindRelatedDrawing(node) : null;

        SetContextState(
            contextOpenWorkingFile,
            canOpenInSolidWorks && (registered ? authenticated : localFileExists && node.Status != CadReferenceStatus.Missing),
            !canOpenInSolidWorks
                ? "该文件类型不能在SolidWorks中打开"
                : registered ? "从PLM重新打开图档，本地文件不作为打开前提"
                : !localFileExists || node.Status == CadReferenceStatus.Missing
                    ? "当前结构引用的本地文件不存在"
                    : "打开或激活当前结构实际引用的工作图档");
        var currentRevision = string.IsNullOrWhiteSpace(node.CurrentRevision)
            ? string.IsNullOrWhiteSpace(node.Revision) ? "待识别" : node.Revision
            : node.CurrentRevision;
        var latestRevision = string.IsNullOrWhiteSpace(node.LatestRevision) ? "待识别" : node.LatestRevision;
        SetContextState(contextUpdateLatest, registered && authenticated && canOpenInSolidWorks,
            registered ? "从PLM重新取回当前版本，本地修改、缺失或只读状态不拦截获取" : "该图档尚未入库");
        contextVersionInfo.Text = registered
            ? string.Concat("当前 ", currentRevision, " / 最新 ", latestRevision)
            : "当前 未入库 / 最新 -";

        contextGenerateDrawing.Text = drawingExists ? "更新工程图..." : "生成工程图...";
        SetContextState(
            contextGenerateDrawing,
            drawingSource && localFileExists && !historicalPreview,
            historicalPreview
                ? "请打开当前模型版本后生成工程图"
                : drawingSource ? "本地模型不存在，不能生成工程图" : "请选择零件或装配体");
        SetContextState(
            contextOpenDrawing,
            drawingExists,
            drawingSource ? "尚未生成关联工程图" : "请选择零件或装配体");
        SetContextState(
            contextDrawingVersions,
            relatedDrawing?.DocumentId.HasValue == true && authenticated,
            relatedDrawing?.DocumentId.HasValue == true ? "请先登录PLM" : "关联工程图尚未入库");

        var checkoutReason = PdmActionReason(registered, authenticated);
        if (registered && authenticated && editing)
        {
            checkoutReason = editingByCurrentUser ? "您已获取该图档的编辑权限" : string.Concat("当前编辑人员：", DisplayUserName(node.CheckedOutBy));
        }
        if (canRegister && authenticated)
        {
            checkoutReason = "首次获取权限时将自动登记该图档";
        }
        if (drawingReviewLocked)
        {
            checkoutReason = "图纸审核中，只允许只读打开，不能获取编辑权限";
        }
        else if (latestReadOnlyPreview)
        {
            checkoutReason = "切换到Working工作区并获取编辑权限";
        }
        else if (node.CheckoutSessionLost)
        {
            checkoutReason = "编辑权限已失效；请另存本地修改或重新获取权限";
        }
        SetContextState(
            contextCheckout,
            !drawingReviewLocked
                && authenticated
                && (!editing || editingByCurrentUser || canRecoverCheckout) && (registered || canRegister),
            checkoutReason);

        var canFirstCheckIn = !readOnlyPreview && !drawingReviewLocked && authenticated && canRegister;
        var checkInReason = PdmActionReason(registered, authenticated);
        if (node.CheckoutSessionLost)
        {
            checkInReason = "编辑权限已失效；请另存本地修改或重新获取权限";
        }
        else if (readOnlyPreview)
        {
            checkInReason = "只读预览不能提交存档；请先切换到编辑工作区";
        }
        else if (drawingReviewLocked)
        {
            checkInReason = "图纸审核中，不能提交存档";
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
            checkInReason = string.Concat("只有当前编辑人员", DisplayUserName(node.CheckedOutBy), "可以提交存档");
        }
        else if (registered && authenticated && editingByCurrentUser && !localFileExists)
        {
            checkInReason = "本地文件不存在，不能提交存档";
        }
        SetContextState(
            contextCheckIn,
            canFirstCheckIn || (!readOnlyPreview && !drawingReviewLocked && registered && authenticated && editingByCurrentUser && localFileExists),
            checkInReason);
        SetContextState(
            contextDiscardCheckout,
            !readOnlyPreview && registered && authenticated && editingByCurrentUser,
            readOnlyPreview
                ? "只读预览不能更改编辑状态"
                : registered && authenticated && editing && !editingByCurrentUser
                ? string.Concat("只有当前编辑人员", DisplayUserName(node.CheckedOutBy), "可以放弃编辑")
                : registered && authenticated ? "尚未获取该图档的编辑权限" : PdmActionReason(registered, authenticated));

        SetContextState(contextWhereUsed, registered && authenticated, registered ? "请先登录PLM" : "该图档尚未入库");
        SetContextState(contextRequestRelease, registered && authenticated && !readOnlyPreview && editing && !editingByCurrentUser,
            editingByCurrentUser ? "当前编辑权限属于您" : editing ? string.Concat("向", DisplayUserName(node.CheckedOutBy), "申请释放编辑权限") : "该图档当前未被检出");
        SetContextState(contextOpenFolder, localFileExists, "本地文件不存在");
        var selectedRoot = rootNode != null
            && (ReferenceEquals(node, rootNode)
                || !string.IsNullOrWhiteSpace(node.FullPath) && string.Equals(node.FullPath, rootNode.FullPath, StringComparison.OrdinalIgnoreCase));
        var renameSupported = (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
            && (selectedRoot || !string.IsNullOrWhiteSpace(node.ComponentSelectionName));
        var rootEditingByCurrentUser = rootNode != null
            && string.Equals(rootNode.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            && !rootNode.CheckoutSessionLost;
        var renameReason = selectedRoot
            ? "以新名称保存当前主图档；提交存档后更新PLM当前名称"
            : "在SolidWorks中重命名所选图档；保存并提交存档后更新PLM当前名称";
        if (!renameSupported)
        {
            renameReason = node.Kind == CadDocumentKind.Drawing
                ? "工程图暂不支持从设计树重命名"
                : "SolidWorks未识别到该零部件实例";
        }
        else if (!registered)
        {
            renameReason = "该图档尚未入库";
        }
        else if (!authenticated)
        {
            renameReason = "请先登录PLM";
        }
        else if (readOnlyPreview)
        {
            renameReason = "只读预览不能重命名";
        }
        else if (lifecycleInReview || lifecycleObsolete)
        {
            renameReason = lifecycleInReview ? "图档正在审批，不能重命名" : "图档已作废，不能重命名";
        }
        else if (!localFileExists)
        {
            renameReason = "本地文件不存在";
        }
        else if (!editingByCurrentUser)
        {
            renameReason = editing ? string.Concat("当前编辑人员：", DisplayUserName(node.CheckedOutBy)) : "请先获取该图档的编辑权限";
        }
        else if (!rootEditingByCurrentUser)
        {
            renameReason = "重命名会修改装配引用，请先获取当前装配体的编辑权限";
        }
        SetContextState(
            contextRenameDocument,
            renameSupported && registered && authenticated && !readOnlyPreview && !lifecycleInReview && !lifecycleObsolete
                && localFileExists && editingByCurrentUser && rootEditingByCurrentUser,
            renameReason);
        SetContextState(contextZoomSelection, !string.IsNullOrWhiteSpace(node.ComponentSelectionName), "请选择装配体中的零部件实例");
        SetContextState(contextIsolate, !string.IsNullOrWhiteSpace(node.ComponentSelectionName), "请选择装配体中的零部件实例");
        contextExitIsolate.Enabled = canOpenInSolidWorks;
        SetContextState(contextOpenReleaseCenter, authenticated && selectedProjectId.HasValue, authenticated ? "请先选择当前项目" : "请先登录PLM");
        SetContextState(contextWithdrawApproval, registered && authenticated && !readOnlyPreview && selectedProjectId.HasValue && lifecycleInReview,
            lifecycleInReview ? "撤回当前项目审批并恢复工作状态" : "该图档当前不在审批中");
        SetContextState(contextObsolete, registered && authenticated && !editing && !readOnlyPreview && !lifecycleInReview && !lifecycleObsolete,
            lifecycleObsolete ? "图档已作废" : lifecycleInReview ? "请先撤回审批" : editing ? "请先提交或放弃编辑" : "填写原因后受控作废图档");

        SetContextState(contextVersions, registered && authenticated, registered ? "请先登录PLM" : "该图档尚未入库");

        if (historicalPreview)
        {
            contextHint.Text = "提示：历史版本仅供只读预览，不能获取权限或提交存档";
        }
        else if (latestReadOnlyPreview)
        {
            contextHint.Text = "提示：当前为最新只读预览，获取权限后会切换到Working工作区";
        }
        else if (canFirstCheckIn)
        {
            contextHint.Text = "提示：首次提交存档时请选择归属项目号";
        }
        else if (!registered)
        {
            contextHint.Text = node.IsExternalProvenance
                ? "提示：当前是工作区外的PLM来源副本，不会自动关联项目；纳入或恢复后请从PLM打开受控工作区副本"
                : "提示：可通过获取权限或提交存档选择归属项目";
        }
        else if (!authenticated)
        {
            contextHint.Text = "提示：登录后可使用PLM操作";
        }
        else if (!localFileExists)
        {
            contextHint.Text = "提示：本地文件不存在，请从项目图档获取受控版本";
        }
        else if (editing)
        {
            contextHint.Text = string.Concat("提示：", WorkStateText(node), "；编辑人员：", DisplayUserName(node.CheckedOutBy));
        }
        else if (node.Status == CadReferenceStatus.Lightweight)
        {
            contextHint.Text = "提示：轻量化不阻止存档；请先获取编辑权限，完成修改并保存后提交";
        }
        else
        {
            contextHint.Text = "提示：请先获取编辑权限，完成修改并保存后再提交存档";
        }
        if (node.IsExternalProvenance && registered)
        {
            contextHint.Text = string.Concat(contextHint.Text, "；当前仍是工作区外文件，完成本次操作后请从PLM打开工作区副本");
        }
    }

    private static string PdmActionReason(bool registered, bool authenticated)
    {
        if (!registered)
        {
            return "该图档尚未入库";
        }

        return authenticated ? string.Empty : "请先登录PLM";
    }

    private CadTreeNode FindRelatedDrawing(CadTreeNode source)
    {
        var drawingPath = AutomaticDrawingControl.GetDrawingPath(source);
        if (string.IsNullOrWhiteSpace(drawingPath) || rootNode == null)
        {
            return null;
        }

        return EnumerateCadTree(rootNode).FirstOrDefault(candidate =>
            candidate.Kind == CadDocumentKind.Drawing
            && PathsEqual(candidate.FullPath, drawingPath));
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
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
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            ColumnCount = 7,
            RowCount = 1,
            Padding = new Padding(3)
        };
        ConfigureStructureActionColumns(actions);
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        foreach (var button in new[] { openCurrentButton, openHistoryButton, editHistoryButton, compareVersionsButton })
            ConfigureStructureToolbarButton(button);
        openCurrentButton.Text = "打开最新";
        openCurrentButton.Click += (_, _) => RaiseSelected(OpenRequested);
        openHistoryButton.Text = "切换版本";
        openHistoryButton.Click += (_, _) => RaiseOpenHistory();
        editHistoryButton.Text = "获取权限";
        editHistoryButton.Click += (_, _) => RaiseEditHistoricalVersion();
        compareVersionsButton.Text = "版本对比";
        compareVersionsButton.Click += (_, _) => RaiseVersionComparison();
        actions.Controls.Add(openCurrentButton, 0, 0);
        actions.Controls.Add(openHistoryButton, 2, 0);
        actions.Controls.Add(editHistoryButton, 4, 0);
        actions.Controls.Add(compareVersionsButton, 6, 0);
        var arrangedColumns = 0;
        Action arrangeButtons = () =>
        {
            var buttons = new[] { openCurrentButton, openHistoryButton, editHistoryButton, compareVersionsButton };
            var gap = (int)Math.Round(actions.ColumnStyles[1].Width);
            var buttonHeight = (int)Math.Round(actions.RowStyles[0].SizeType == SizeType.Absolute ? actions.RowStyles[0].Height : openCurrentButton.Height);
            var minimumWidth = buttons.Max(button => TextRenderer.MeasureText(button.Text, button.Font).Width) + 8;
            var columns = actions.ClientSize.Width - actions.Padding.Horizontal >= minimumWidth * 4 + gap * 3 ? 4 : 2;
            if (columns == arrangedColumns) return;
            arrangedColumns = columns;
            actions.SuspendLayout();
            actions.ColumnCount = columns * 2 - 1;
            ConfigureStructureActionColumns(actions, columns, gap);
            actions.RowCount = columns == 4 ? 1 : 3;
            actions.RowStyles.Clear();
            for (var row = 0; row < actions.RowCount; row++) actions.RowStyles.Add(new RowStyle(SizeType.Absolute, row % 2 == 0 ? buttonHeight : gap));
            actions.Height = actions.Padding.Vertical + (columns == 4 ? buttonHeight : buttonHeight * 2 + gap);
            for (var index = 0; index < buttons.Length; index++)
                actions.SetCellPosition(buttons[index], new TableLayoutPanelCellPosition(index % columns * 2, index / columns * 2));
            actions.ResumeLayout();
        };
        actions.Layout += (_, _) => arrangeButtons();
        arrangeButtons();
        tab.Controls.Add(versionList);
        tab.Controls.Add(actions);
        return tab;
    }

    private void UpdateVersionActions()
    {
        openCurrentButton.Enabled = IsDisplayedDocumentSelected();
        actionToolTip.SetToolTip(openCurrentButton, "只读打开最新受控版，不获取编辑权限");
        var canSwitchVersion = CanSwitchDisplayedVersion(out var switchReason);
        openHistoryButton.Enabled = canSwitchVersion && versionList.SelectedItems.Count == 1;
        actionToolTip.SetToolTip(openHistoryButton, canSwitchVersion ? "将本地工作文件切换为所选版本，并在设计树显示当前/最新版本" : switchReason);
        var canEditHistoricalVersion = CanEditSelectedHistoricalVersion(out var editReason);
        editHistoryButton.Enabled = canEditHistoricalVersion;
        actionToolTip.SetToolTip(editHistoryButton, canEditHistoricalVersion ? "将零件切换为所选历史版本并获取独占编辑权限；提交时从服务器最新版本继续升版" : editReason);
        compareVersionsButton.Enabled = displayedVersionDocumentId.HasValue && versionList.SelectedItems.Count == 2;
    }

    private void BuildVersionContextMenu()
    {
        versionContextGetSelected.Text = "切换到所选版本";
        versionContextEditSelected.Text = "基于所选版本获取权限";
        versionContextGetLatest.Text = "切换到最新版本";
        versionContextOpenCurrent.Text = "只读打开最新受控版";
        versionContextCompare.Text = "版本对比";
        versionContextRefresh.Text = "刷新版本列表";
        versionContextGetSelected.Click += (_, _) => RaiseOpenHistory();
        versionContextEditSelected.Click += (_, _) => RaiseEditHistoricalVersion();
        versionContextGetLatest.Click += (_, _) => RaiseOpenLatestVersion();
        versionContextOpenCurrent.Click += (_, _) => RaiseDisplayedNode(OpenRequested);
        versionContextCompare.Click += (_, _) => RaiseVersionComparison();
        versionContextRefresh.Click += (_, _) => RaiseDisplayedNode(VersionsRequested);
        versionMenu.Items.AddRange(new ToolStripItem[]
        {
            versionContextGetSelected,
            versionContextEditSelected,
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
        var canSwitchVersion = CanSwitchDisplayedVersion(out var switchReason);
        versionContextGetSelected.Enabled = canSwitchVersion && selectionCount == 1;
        versionContextGetSelected.ToolTipText = canSwitchVersion ? string.Empty : switchReason;
        var canEditHistoricalVersion = CanEditSelectedHistoricalVersion(out var editReason);
        versionContextEditSelected.Enabled = canEditHistoricalVersion;
        versionContextEditSelected.ToolTipText = canEditHistoricalVersion ? string.Empty : editReason;
        versionContextGetLatest.Enabled = canSwitchVersion && versionList.Items.Count > 0;
        versionContextGetLatest.ToolTipText = canSwitchVersion ? string.Empty : switchReason;
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

    private bool CanSwitchDisplayedVersion(out string reason)
    {
        var node = SelectedNode;
        if (!IsDisplayedDocumentSelected() || node == null)
        {
            reason = "请先在设计树中选择对应图档";
            return false;
        }
        if (string.IsNullOrWhiteSpace(authenticatedUsername))
        {
            reason = "请先登录PLM";
            return false;
        }
        if (node.IsReadOnlyPreview)
        {
            reason = "只读预览目录不能切换工作版本";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy))
        {
            reason = string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                ? "请先提交存档或放弃编辑"
                : string.Concat("该图档正在由", DisplayUserName(node.CheckedOutBy), "编辑");
            return false;
        }
        if (node.IsModifiedInSolidWorks
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn)
        {
            reason = "存在未保存修改或待提交内容，不能切换版本";
            return false;
        }
        if (string.IsNullOrWhiteSpace(node.FullPath)
            || node.Kind != CadDocumentKind.Assembly
                && node.Kind != CadDocumentKind.Part
                && node.Kind != CadDocumentKind.Drawing)
        {
            reason = "该图档没有可切换的本地工作路径";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanEditSelectedHistoricalVersion(out string reason)
    {
        var node = SelectedNode;
        if (node == null || !node.DocumentId.HasValue || displayedVersionDocumentId != node.DocumentId)
        {
            reason = "请先选择已入库图档";
            return false;
        }
        if (string.IsNullOrWhiteSpace(authenticatedUsername))
        {
            reason = "请先登录PLM";
            return false;
        }
        if (versionList.SelectedItems.Count != 1 || !(versionList.SelectedItems[0].Tag is DocumentVersionDto selected))
        {
            reason = "请选择一个历史版本";
            return false;
        }
        if (node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly)
        {
            reason = "仅零件和装配体支持基于历史版本获取编辑";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy))
        {
            reason = string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                ? "请先提交存档或放弃当前编辑"
                : string.Concat("该图档正在由", DisplayUserName(node.CheckedOutBy), "编辑");
            return false;
        }
        if (node.IsModifiedInSolidWorks
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn)
        {
            reason = "存在未保存修改或待提交内容";
            return false;
        }
        if (node.DrawingReviewLocked
            || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase))
        {
            reason = node.DrawingReviewLocked ? "图纸审核中的图档不能获取编辑权限" : "审批中或已作废的图档不能获取编辑权限";
            return false;
        }
        if (versionList.Items.Count == 0
            || !(versionList.Items[0].Tag is DocumentVersionDto latest)
            || selected.Id == latest.Id)
        {
            reason = "最新版本请直接使用“获取权限”";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void RaiseOpenHistory()
    {
        if (displayedVersionDocumentId is Guid documentId && versionList.SelectedItems.Count == 1 && versionList.SelectedItems[0].Tag is DocumentVersionDto version)
            OpenHistoryRequested?.Invoke(this, new DocumentVersionEventArgs(documentId, displayedVersionFileName, version));
    }

    private void RaiseEditHistoricalVersion()
    {
        if (CanEditSelectedHistoricalVersion(out _)
            && displayedVersionDocumentId is Guid documentId
            && versionList.SelectedItems[0].Tag is DocumentVersionDto version)
        {
            EditHistoricalVersionRequested?.Invoke(this, new DocumentVersionEventArgs(documentId, displayedVersionFileName, version));
        }
    }

    private void RaiseVersionComparison()
    {
        if (displayedVersionDocumentId is Guid documentId && versionList.SelectedItems.Count == 2 && versionList.SelectedItems[0].Tag is DocumentVersionDto left && versionList.SelectedItems[1].Tag is DocumentVersionDto right)
            CompareVersionsRequested?.Invoke(this, new VersionComparisonEventArgs(documentId, left.Id, right.Id));
    }

    private static string TreeViewKey(CadTreeNode root) => root == null ? string.Empty
        : (string.IsNullOrWhiteSpace(root.FullPath) ? root.FileName : root.FullPath) + "\n" + root.Configuration;

    private static string TreeViewIdentity(CadTreeNode node) => node == null ? string.Empty
        : string.IsNullOrWhiteSpace(node.InstancePath) ? node.FullPath : node.InstancePath;

    private void RememberTreeViewState()
    {
        if (rootNode == null || string.IsNullOrWhiteSpace(displayedTreeViewKey) || treeBuildPending || activeTreeBuildPlan == null
            || activeTreeBuildPlan.Root != null && !ReferenceEquals(activeTreeBuildPlan.Root.Tag, rootNode)) return;
        treeViewStates[displayedTreeViewKey] = new TreeViewState
        {
            Filter = searchBox.Text, HealthFilter = activeHealthFilter,
            SelectedIdentity = TreeViewIdentity(SelectedNode), TopIdentity = TreeViewIdentity(structureTree.TopNode?.Tag as CadTreeNode),
            CheckedPaths = new HashSet<string>(checkedCheckInPaths, StringComparer.OrdinalIgnoreCase),
            CheckedDocumentIds = EnumerateCadTree(rootNode).Where(node => !string.IsNullOrWhiteSpace(node.FullPath) && checkedCheckInPaths.Contains(node.FullPath))
                .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().DocumentId, StringComparer.OrdinalIgnoreCase),
            ExpandedPaths = CaptureExpandedInstancePaths(structureTree.Nodes), LastUsedUtc = DateTime.UtcNow
        };
        while (treeViewStates.Count > 16) treeViewStates.Remove(treeViewStates.OrderBy(pair => pair.Value.LastUsedUtc).First().Key);
    }

    private static string TreePresentationStamp(CadTreeNode root, string filter, StructureHealthFilter healthFilter)
    {
        var stamp = new StringBuilder();
        stamp.Append(filter?.Trim()).Append('|').Append((int)healthFilter);
        foreach (var node in EnumerateCadTree(root))
            stamp.Append('\n').Append(string.Join("\u001f", new object[]
            {
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node), node.Children.Count, node.InstancePath,
                node.FullPath, node.FileName, node.DisplayName, node.Configuration, node.ComponentSelectionName,
                node.Kind, node.Status, node.WorkState, node.CurrentRevision, node.LatestRevision, node.OpenedRevision,
                node.CheckedOutBy, node.CheckoutSessionId, node.CheckoutSessionLost, node.IsRenamePendingSave,
                node.IsInterconnectImport, node.IsHistoricalPreview, node.IsLatestReadOnlyPreview, node.LatestStructureIndexPending,
                node.DrawingReviewLocked, node.DrawingUpdatePendingReason, node.DocumentId, node.Revision
            }));
        return stamp.ToString();
    }

    private void RestoreTreeTopNode(TreeBuildPlan plan)
    {
        var top = plan.FindByInstancePath(plan.RestoredView?.TopIdentity);
        if (top == null) return;
        EnsureTreeNodeAttached(top, plan);
        structureTree.TopNode = top;
    }

    private sealed class TreeViewState
    {
        public string Filter, SelectedIdentity, TopIdentity;
        public StructureHealthFilter HealthFilter;
        public HashSet<string> CheckedPaths, ExpandedPaths;
        public Dictionary<string, Guid?> CheckedDocumentIds;
        public DateTime LastUsedUtc;
    }

    private void RebuildTree(string filter)
    {
        var modelRoot = rootNode;
        if (modelRoot == null)
        {
            UpdateTreeHealth();
            return;
        }

        UpdateTreeHealth();

        var restoredView = changedTreeView ? pendingTreeViewState : null;
        var selectedInstancePath = changedTreeView ? restoredView?.SelectedIdentity : TreeViewIdentity(SelectedNode);
        var expandedInstancePaths = changedTreeView ? restoredView?.ExpandedPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : CaptureExpandedInstancePaths(structureTree.Nodes);
        changedTreeView = false;
        pendingTreeViewState = null;
        var checkedPaths = new HashSet<string>(checkedCheckInPaths, StringComparer.OrdinalIgnoreCase);
        var normalizedFilter = filter?.Trim();
        var healthFilter = activeHealthFilter;
        var presentationStamp = TreePresentationStamp(modelRoot, normalizedFilter, healthFilter);
        treeBuildPending = true;
        var generation = Interlocked.Increment(ref treeBuildGeneration);
        CancelActiveTreeBuild();
        Task.Run(() =>
        {
            var root = BuildTreeNode(modelRoot, normalizedFilter, checkedPaths, healthFilter);
            return PrepareTreeBuildPlan(root, !string.IsNullOrWhiteSpace(normalizedFilter) || healthFilter != StructureHealthFilter.None);
        }).ContinueWith(task => RunOnUiThread(() =>
        {
            if (task.IsFaulted || task.IsCanceled || generation != treeBuildGeneration || !ReferenceEquals(modelRoot, rootNode))
            {
                if (generation == treeBuildGeneration) treeBuildPending = false;
                return;
            }

            structureTree.BeginUpdate();
            var plan = task.Result;
            plan.PresentationStamp = presentationStamp;
            plan.RestoredView = restoredView;
            plan.UpdateOpen = true;
            activeTreeBuildPlan = plan;
            structureTree.Nodes.Clear();
            if (plan.Root != null)
            {
                structureTree.Nodes.Add(plan.Root);
            }

            AppendTreeBatch(plan, generation, selectedInstancePath, expandedInstancePaths);
        }), TaskScheduler.Default);
    }

    private static HashSet<string> CaptureExpandedInstancePaths(TreeNodeCollection nodes)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode node in nodes)
        {
            CaptureExpandedInstancePaths(node, expanded);
        }
        return expanded;
    }

    private static void CaptureExpandedInstancePaths(TreeNode node, ISet<string> expanded)
    {
        if (node == null)
        {
            return;
        }

        if (node.IsExpanded
            && node.Tag is CadTreeNode model
            && !string.IsNullOrWhiteSpace(TreeViewIdentity(model)))
        {
            expanded.Add(TreeViewIdentity(model));
        }

        foreach (TreeNode child in node.Nodes)
        {
            CaptureExpandedInstancePaths(child, expanded);
        }
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

    private static TreeBuildPlan PrepareTreeBuildPlan(TreeNode root, bool expandAll)
    {
        var plan = new TreeBuildPlan(root, expandAll);
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

    private void AppendTreeBatch(
        TreeBuildPlan plan,
        int generation,
        string selectedInstancePath,
        IReadOnlyCollection<string> expandedInstancePaths)
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
            Task.Delay(1).ContinueWith(
                _ => RunOnUiThread(() => AppendTreeBatch(plan, generation, selectedInstancePath, expandedInstancePaths)),
                TaskScheduler.Default);
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
            if (plan.ExpandAll)
            {
                MaterializeAndExpand(plan.Root, plan);
            }
            else
            {
                RestoreExpandedNodes(plan, expandedInstancePaths);
            }
        }

        var pendingSelectionApplied = TrySelectComponentNode(pendingComponentSelectionName);
        if (pendingSelectionApplied)
        {
            pendingComponentSelectionName = string.Empty;
        }

        var quickHealthSelectionApplied = false;
        if (!pendingSelectionApplied && activeHealthFilter != StructureHealthFilter.None && selectFirstHealthMatchAfterBuild)
        {
            var healthNode = FindTreeNode(
                structureTree.Nodes,
                node => node.Tag is CadTreeNode model && MatchesHealthFilter(model, activeHealthFilter));
            if (healthNode != null)
            {
                EnsureTreeNodeAttached(healthNode, plan);
                SelectTreeNodeWithoutNotification(healthNode);
                healthNode.EnsureVisible();
                quickHealthSelectionApplied = true;
            }
            selectFirstHealthMatchAfterBuild = false;
        }

        if (!pendingSelectionApplied && !quickHealthSelectionApplied && !string.IsNullOrWhiteSpace(selectedInstancePath))
        {
            var selected = plan.FindByInstancePath(selectedInstancePath);
            if (selected != null)
            {
                EnsureTreeNodeAttached(selected, plan);
                SelectTreeNodeWithoutNotification(selected);
            }
        }

        if (!plan.ExpandAll && plan.Root != null)
        {
            Task.Delay(1).ContinueWith(_ => RunOnUiThread(() =>
            {
                if (generation == treeBuildGeneration && ReferenceEquals(plan.Root?.Tag, rootNode))
                {
                    ExpandRootOnly(plan.Root, plan);
                    RestoreTreeTopNode(plan);
                }
            }), TaskScheduler.Default);
        }
        else RestoreTreeTopNode(plan);
        renderedTreeStamp = plan.PresentationStamp;
        treeBuildPending = false;
    }

    private void RestoreExpandedNodes(TreeBuildPlan plan, IReadOnlyCollection<string> expandedInstancePaths)
    {
        ExpandRootOnly(plan.Root, plan);
        foreach (var instancePath in expandedInstancePaths ?? Array.Empty<string>())
        {
            var node = plan.FindByInstancePath(instancePath);
            if (node == null)
            {
                continue;
            }

            EnsureTreeNodeAttached(node, plan);
            MaterializeChildren(node, plan);
            node.Expand();
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
        public TreeBuildPlan(TreeNode root, bool expandAll)
        {
            Root = root;
            ExpandAll = expandAll;
        }

        public TreeNode Root { get; }

        public bool ExpandAll { get; }

        public Queue<TreeAppendOperation> PendingChildren { get; } = new Queue<TreeAppendOperation>();

        public Dictionary<TreeNode, TreeNode[]> ChildrenByParent { get; } = new Dictionary<TreeNode, TreeNode[]>();

        public Dictionary<TreeNode, TreeNode> ParentByChild { get; } = new Dictionary<TreeNode, TreeNode>();

        public HashSet<TreeNode> MaterializedParents { get; } = new HashSet<TreeNode>();

        private Dictionary<string, TreeNode> NodesByComponentName { get; } = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, TreeNode> NodesByInstancePath { get; } = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        public bool UpdateOpen { get; set; }
        public string PresentationStamp { get; set; }
        public TreeViewState RestoredView { get; set; }

        public void IndexNode(TreeNode node)
        {
            if (node?.Tag is CadTreeNode model && !string.IsNullOrWhiteSpace(model.ComponentSelectionName)
                && !NodesByComponentName.ContainsKey(model.ComponentSelectionName))
            {
                NodesByComponentName.Add(model.ComponentSelectionName, node);
            }
            if (node?.Tag is CadTreeNode instanceModel && !string.IsNullOrWhiteSpace(TreeViewIdentity(instanceModel))
                && !NodesByInstancePath.ContainsKey(TreeViewIdentity(instanceModel)))
            {
                NodesByInstancePath.Add(TreeViewIdentity(instanceModel), node);
            }
        }

        public TreeNode FindByComponentName(string componentName) =>
            !string.IsNullOrWhiteSpace(componentName) && NodesByComponentName.TryGetValue(componentName, out var node) ? node : null;

        public TreeNode FindByInstancePath(string instancePath) =>
            !string.IsNullOrWhiteSpace(instancePath) && NodesByInstancePath.TryGetValue(instancePath, out var node) ? node : null;
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

    private TreeNode BuildTreeNode(CadTreeNode model, string filter, ISet<string> checkedPaths, StructureHealthFilter healthFilter)
    {
        var childNodes = model.Children
            .Select(child => BuildTreeNode(child, filter, checkedPaths, healthFilter))
            .Where(node => node != null)
            .ToArray();
        var selfMatches = MatchesFilter(model, filter) && MatchesHealthFilter(model, healthFilter);
        if (!selfMatches && childNodes.Length == 0)
        {
            return null;
        }

        var isMissing = model.Status == CadReferenceStatus.Missing && !model.IsRenamePendingSave;
        var status = model.IsInterconnectImport ? " · 导入组件"
            : model.Status == CadReferenceStatus.Normal || isMissing ? string.Empty : string.Concat(" · ", StatusText(model.Status));
        var editTip = isMissing
            ? "\r\n状态：文件缺失"
            : string.Concat("\r\n状态：", WorkStateText(model), string.IsNullOrWhiteSpace(model.CheckedOutBy) ? string.Empty : string.Concat("\r\n编辑人员：", DisplayUserName(model.CheckedOutBy)));
        var text = string.Concat(Path.GetFileNameWithoutExtension(model.FileName), " · ", model.DisplayName, status);
        var node = new TreeNode(text)
        {
            Tag = model,
            ImageKey = StructureImageKey(model.Kind),
            SelectedImageKey = StructureImageKey(model.Kind),
            ToolTipText = string.Concat(model.FileName, "\r\n配置：", model.Configuration, editTip),
            Checked = !string.IsNullOrWhiteSpace(model.FullPath) && checkedPaths.Contains(model.FullPath)
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

    internal static string StructureImageKey(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "assembly";
            case CadDocumentKind.Part: return "part";
            case CadDocumentKind.Drawing: return "drawing";
            default: return "other";
        }
    }

    internal static ImageList BuildStructureImages()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16), TransparentColor = Color.Transparent };
        images.Images.Add("assembly", DrawAssemblyIcon());
        images.Images.Add("part", DrawPartIcon());
        images.Images.Add("drawing", DrawDrawingIcon());
        images.Images.Add("other", DrawOtherDocumentIcon());
        images.Images.Add("group", DrawGroupIcon());
        return images;
    }

    private static Bitmap DrawPartIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var top = new SolidBrush(Color.FromArgb(250, 215, 91)))
        using (var left = new SolidBrush(Color.FromArgb(226, 160, 26)))
        using (var right = new SolidBrush(Color.FromArgb(191, 118, 5)))
        using (var outline = new Pen(Color.FromArgb(91, 67, 16), 0.8F))
        using (var highlight = new Pen(Color.FromArgb(255, 244, 191), 0.45F))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            var topFace = new[] { new PointF(8F, 1.65F), new PointF(13.5F, 4.4F), new PointF(8F, 7.15F), new PointF(2.5F, 4.4F) };
            var leftFace = new[] { new PointF(2.5F, 4.4F), new PointF(8F, 7.15F), new PointF(8F, 13.6F), new PointF(2.5F, 10.8F) };
            var rightFace = new[] { new PointF(8F, 7.15F), new PointF(13.5F, 4.4F), new PointF(13.5F, 10.8F), new PointF(8F, 13.6F) };
            graphics.FillPolygon(top, topFace);
            graphics.FillPolygon(left, leftFace);
            graphics.FillPolygon(right, rightFace);
            graphics.DrawPolygon(outline, topFace);
            graphics.DrawPolygon(outline, leftFace);
            graphics.DrawPolygon(outline, rightFace);
            graphics.DrawLines(highlight, new[] { new PointF(4F, 4.55F), new PointF(8F, 6.55F), new PointF(12F, 4.55F) });
        }
        return bitmap;
    }

    private static Bitmap DrawAssemblyIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var blueTop = new SolidBrush(Color.FromArgb(132, 181, 215)))
        using (var blueLeft = new SolidBrush(Color.FromArgb(84, 143, 186)))
        using (var blueRight = new SolidBrush(Color.FromArgb(50, 109, 155)))
        using (var blueOutline = new Pen(Color.FromArgb(40, 79, 114), 0.75F))
        using (var frontTopBrush = new SolidBrush(Color.FromArgb(115, 169, 208)))
        using (var frontLeftBrush = new SolidBrush(Color.FromArgb(63, 127, 174)))
        using (var frontRightBrush = new SolidBrush(Color.FromArgb(39, 94, 138)))
        using (var frontOutline = new Pen(Color.FromArgb(35, 79, 114), 0.8F))
        using (var link = new Pen(Color.FromArgb(36, 106, 158), 0.8F))
        using (var nodeFill = new SolidBrush(Color.FromArgb(237, 247, 255)))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var rearTop = new[] { new PointF(5.2F, 1.7F), new PointF(9.7F, 3.95F), new PointF(5.2F, 6.2F), new PointF(0.75F, 3.95F) };
            var rearLeft = new[] { new PointF(0.75F, 3.95F), new PointF(5.2F, 6.2F), new PointF(5.2F, 11.05F), new PointF(0.75F, 8.75F) };
            var rearRight = new[] { new PointF(5.2F, 6.2F), new PointF(9.7F, 3.95F), new PointF(9.7F, 8.75F), new PointF(5.2F, 11.05F) };
            graphics.FillPolygon(blueTop, rearTop);
            graphics.FillPolygon(blueLeft, rearLeft);
            graphics.FillPolygon(blueRight, rearRight);
            graphics.DrawPolygon(blueOutline, rearTop);
            graphics.DrawPolygon(blueOutline, rearLeft);
            graphics.DrawPolygon(blueOutline, rearRight);

            var frontTop = new[] { new PointF(10.1F, 4.9F), new PointF(15.25F, 7.45F), new PointF(10.1F, 10.05F), new PointF(4.95F, 7.45F) };
            var frontLeft = new[] { new PointF(4.95F, 7.45F), new PointF(10.1F, 10.05F), new PointF(10.1F, 14.3F), new PointF(4.95F, 11.7F) };
            var frontRight = new[] { new PointF(10.1F, 10.05F), new PointF(15.25F, 7.45F), new PointF(15.25F, 11.7F), new PointF(10.1F, 14.3F) };
            graphics.FillPolygon(frontTopBrush, frontTop);
            graphics.FillPolygon(frontLeftBrush, frontLeft);
            graphics.FillPolygon(frontRightBrush, frontRight);
            graphics.DrawPolygon(frontOutline, frontTop);
            graphics.DrawPolygon(frontOutline, frontLeft);
            graphics.DrawPolygon(frontOutline, frontRight);

            graphics.DrawLine(link, 5.6F, 7.8F, 9.35F, 9.65F);
            graphics.FillEllipse(nodeFill, 4.1F, 6.65F, 1.6F, 1.6F);
            graphics.DrawEllipse(link, 4.1F, 6.65F, 1.6F, 1.6F);
            graphics.FillEllipse(nodeFill, 9.25F, 9.25F, 1.6F, 1.6F);
            graphics.DrawEllipse(link, 9.25F, 9.25F, 1.6F, 1.6F);
        }
        return bitmap;
    }

    private static Bitmap DrawDrawingIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var paper = new SolidBrush(Color.FromArgb(252, 253, 255)))
        using (var fold = new SolidBrush(Color.FromArgb(220, 226, 232)))
        using (var viewFill = new SolidBrush(Color.FromArgb(229, 238, 247)))
        using (var titleFill = new SolidBrush(Color.FromArgb(243, 245, 247)))
        using (var pen = new Pen(Color.FromArgb(77, 86, 98), 0.85F))
        using (var viewPen = new Pen(Color.FromArgb(71, 118, 160), 0.65F))
        using (var detail = new Pen(Color.FromArgb(124, 135, 146), 0.55F))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            var page = new[] { new PointF(2.6F, 1.3F), new PointF(10.4F, 1.3F), new PointF(13.4F, 4.3F), new PointF(13.4F, 14.7F), new PointF(2.6F, 14.7F) };
            var pageFold = new[] { new PointF(10.4F, 1.3F), new PointF(10.4F, 4.3F), new PointF(13.4F, 4.3F) };
            graphics.FillPolygon(paper, page);
            graphics.FillPolygon(fold, pageFold);
            graphics.DrawPolygon(pen, page);
            graphics.DrawPolygon(pen, pageFold);

            graphics.FillRectangle(viewFill, 4.15F, 5.5F, 3.9F, 3.1F);
            graphics.DrawRectangle(viewPen, 4.15F, 5.5F, 3.9F, 3.1F);
            graphics.DrawEllipse(pen, 9.1F, 5.55F, 3.1F, 3.1F);
            graphics.DrawLine(detail, 9.1F, 7.1F, 12.2F, 7.1F);
            graphics.DrawLine(detail, 10.65F, 5.55F, 10.65F, 8.65F);

            graphics.FillRectangle(titleFill, 4.15F, 10F, 7.75F, 3.05F);
            graphics.DrawRectangle(detail, 4.15F, 10F, 7.75F, 3.05F);
            graphics.DrawLine(detail, 4.15F, 11.4F, 11.9F, 11.4F);
            graphics.DrawLine(detail, 8.9F, 10F, 8.9F, 13.05F);
            graphics.DrawLine(detail, 10.4F, 10F, 10.4F, 13.05F);
        }
        return bitmap;
    }

    private static Bitmap DrawOtherDocumentIcon()
    {
        var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(Color.FromArgb(196, 205, 214)))
        using (var pen = new Pen(Color.FromArgb(99, 112, 126), 1))
        {
            graphics.FillRectangle(fill, 3, 2, 10, 12);
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

    private string WorkStateText(CadTreeNode node)
        => WorkStateTextCore(node) + (node.StructureIndexPending ? " · 结构待补全" : string.Empty);

    private string WorkStateTextCore(CadTreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.DrawingUpdatePendingReason)) return "关联模型已变化 · 工程图待更新";
        if (node.IsStoredImport) return "导入组件·随父装配保存";
        if (node.IsInterconnectImport) return "导入组件·待确认保存归属";
        if (node.Status == CadReferenceStatus.Virtual || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath))
            return "嵌入式·随父装配保存";
        if (node.IsLatestReadOnlyPreview)
        {
            return "最新只读";
        }

        if (node.IsHistoricalPreview)
        {
            return "历史预览（只读）";
        }

        if (node.IsRenamePendingSave)
        {
            return "重命名待保存";
        }

        if (node.Status == CadReferenceStatus.Missing)
        {
            return "文件缺失";
        }

        if (node.IsExternalProvenance && !node.DocumentId.HasValue)
        {
            return "外部副本（未受控）";
        }

        if (node.StoredVersionStateKnown && !node.HasStoredVersion)
        {
            return "未存档";
        }

        var historicalSourceRevision = IsVersionOutdated(node)
            ? (node.CurrentRevision ?? string.Empty).TrimEnd('*')
            : string.Empty;
        string editState;
        switch (node.WorkState)
        {
            case CadWorkState.ModifiedUnsaved: editState = "修改未保存"; break;
            case CadWorkState.PendingCheckIn: editState = string.IsNullOrWhiteSpace(historicalSourceRevision) ? "待提交" : string.Concat("基于", historicalSourceRevision, "待提交"); break;
            case CadWorkState.Editable: editState = string.IsNullOrWhiteSpace(historicalSourceRevision) ? "可编辑" : string.Concat("基于", historicalSourceRevision, "编辑"); break;
            case CadWorkState.EditingByOther: editState = node.CheckoutSessionLost ? "编辑权限已失效" : string.IsNullOrWhiteSpace(node.CheckedOutBy) ? "他人编辑中" : string.Concat(DisplayUserName(node.CheckedOutBy), "编辑中"); break;
            default: editState = !node.DocumentId.HasValue
                ? "未入库"
                : IsVersionOutdated(node) ? "版本落后" : "未获取权限";
                break;
        }
        var lifecycle = LifecycleText(node.LifecycleState);
        var result = string.Equals(lifecycle, "工作中", StringComparison.Ordinal) || !node.DocumentId.HasValue
            ? editState
            : string.Concat(lifecycle, " · ", editState);
        return node.IsExternalProvenance ? string.Concat("外部路径 · ", result) : result;
    }

    private static string LifecycleText(string state)
    {
        if (string.Equals(state, "InReview", StringComparison.OrdinalIgnoreCase)) return "审批中";
        if (string.Equals(state, "Released", StringComparison.OrdinalIgnoreCase)) return "已发布";
        if (string.Equals(state, "Obsolete", StringComparison.OrdinalIgnoreCase)) return "已作废";
        return "工作中";
    }

    private static Color WorkStateColor(CadTreeNode node)
    {
        if (node.IsReadOnlyPreview)
        {
            return Color.FromArgb(59, 104, 153);
        }

        if (node.IsRenamePendingSave)
        {
            return Color.FromArgb(188, 68, 35);
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

    private static bool MatchesFilter(CadTreeNode model, string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || model.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
        || model.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private void UpdateSelected(CadTreeNode node)
    {
        var automaticDrawingSource = node?.Kind == CadDocumentKind.Drawing ? activeDrawingSource : node;
        var relatedDrawing = node?.Kind == CadDocumentKind.Drawing ? node : FindRelatedDrawing(automaticDrawingSource);
        automaticDrawing.SetSource(automaticDrawingSource, relatedDrawing);
        if (node == null)
        {
            selectedFile.Text = "未选择图档";
            selectedMeta.Text = "配置、版本和编辑状态";
            checkoutButton.Text = "获取";
            checkoutButton.Enabled = false;
            checkinButton.Enabled = CanOpenArchiveDialog;
            propertyEditButton.Enabled = rootNode is object
                && rootNode.IsReadOnlyPreview == false
                && workspaceOperationActive == false;
            settingsButton.Enabled = true;
            ApplyStructureActionButtonAppearances();
            UpdateTreeHealth();
            return;
        }

        selectedFile.Text = string.Concat(
            string.IsNullOrWhiteSpace(node.DrawingNumber) ? Path.GetFileNameWithoutExtension(node.FileName) : node.DrawingNumber,
            " · ",
            string.IsNullOrWhiteSpace(node.DisplayName) ? node.FileName : node.DisplayName);
        var workStateText = WorkStateText(node);
        var embedded = node.Status == CadReferenceStatus.Virtual || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath);
        var editState = string.IsNullOrWhiteSpace(workStateText) ? string.Empty : string.Concat("　状态：", workStateText);
        if (node.StoredReferenceWarnings.Count > 0) editState += "　最新存档有引用提示 " + node.StoredReferenceWarnings.Count + " 项（版本记录中查看）";
        var selectedVersionText = node.IsInterconnectImport ? "随总装核对" : embedded ? "随父装配" : string.IsNullOrWhiteSpace(node.CurrentRevision)
            ? string.IsNullOrWhiteSpace(node.Revision) ? "未归档" : node.BusinessVersionText
            : VersionText(node);
        var editor = node.IsInterconnectImport ? "随总装处理" : embedded ? "随父装配编辑" : string.IsNullOrWhiteSpace(node.CheckedOutBy)
            ? "未检出"
            : string.Concat(DisplayUserName(node.CheckedOutBy),
                string.IsNullOrWhiteSpace(node.CheckoutMachine) ? string.Empty : string.Concat(" @ ", node.CheckoutMachine),
                node.CheckedOutAt.HasValue ? string.Concat(" · ", node.CheckedOutAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) : string.Empty);
        var provenance = node.IsInterconnectImport ? "\r\n来源：3D Interconnect导入组件\r\n来源路径记录（存档时核对有效链接）：" + node.ImportedSourcePath
            : embedded ? "\r\n来源：嵌入式组件（保存在所属装配体内）" : node.IsExternalProvenance
            ? string.Concat("\r\n来源：工作区外的PLM来源副本", ProvenanceProjectText(node))
            : PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
                ? string.Concat("\r\n来源：工作区", node.DocumentId.HasValue ? "受控文件" : "本地文件（未入库）")
                : "\r\n来源：普通本地文件（未受控）";
        selectedMeta.Text = string.Concat(
            "文件：", node.FileName,
            "\r\n业务状态：", LifecycleText(node.LifecycleState), editState, "　编辑：", editor,
            "\r\n配置：", string.IsNullOrWhiteSpace(node.Configuration) ? "默认" : node.Configuration,
            "　版本：", selectedVersionText,
            embedded || node.IsInterconnectImport ? string.Empty : string.Concat("\r\n存档版本：", node.CurrentRevision ?? node.Revision, " / ", node.LatestRevision),
            "\r\n描述：", string.IsNullOrWhiteSpace(node.Description) ? "-" : node.Description,
            "　材料：", string.IsNullOrWhiteSpace(node.Material) ? "-" : node.Material,
            provenance,
            node.IsStoredImport ? "\r\n存储：" + node.ImportedStorageOwnerPath + "（内部组件随此图档存档，不单独登记或升版）"
                : node.IsInterconnectImport ? "\r\n存档：尚未确认原生保存归属，请保存所属装配后重试。"
                : embedded ? "\r\n存储：所属父装配体内" : "\r\n路径：" + (string.IsNullOrWhiteSpace(node.FullPath) ? "-" : node.FullPath));
        var localFileExists = !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath);
        actionToolTip.SetToolTip(selectedMeta, selectedMeta.Text + (node.StoredReferenceWarnings.Count == 0 ? string.Empty
            : "\r\n最新版本引用提示：\r\n" + string.Join("\r\n", node.StoredReferenceWarnings)));
        var canRegister = !embedded && !node.DocumentId.HasValue
            && localFileExists
            && (node.Kind == CadDocumentKind.Assembly || node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Drawing);
        var authenticated = !string.IsNullOrWhiteSpace(authenticatedUsername);
        var editingByCurrentUser = authenticated
            && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            && !node.CheckoutSessionLost;
        var canRecoverCheckout = authenticated
            && node.CheckoutSessionLost
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        var historicalPreview = node.IsHistoricalPreview;
        var latestReadOnlyPreview = node.IsLatestReadOnlyPreview;
        var readOnlyPreview = node.IsReadOnlyPreview;
        var lifecycleLocked = node.DrawingReviewLocked
            || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase);
        var canCheckout = !embedded
            && !lifecycleLocked
            && authenticated
            && (string.IsNullOrWhiteSpace(node.CheckedOutBy) || editingByCurrentUser || canRecoverCheckout)
            && (node.DocumentId.HasValue || canRegister);
        var checkedActionNodes = GetCheckedActionNodes();
        var discardCheckedNodes = checkedActionNodes.Count > 0 && checkedActionNodes.All(IsEditingByCurrentUser);
        checkoutButton.Text = checkedActionNodes.Count > 0
            ? discardCheckedNodes ? "放弃" : "获取"
            : editingByCurrentUser ? "放弃" : "获取";
        checkoutButton.AccessibleName = checkoutButton.Text;
        checkoutButton.Enabled = checkedActionNodes.Count > 0
            || canCheckout
            || (!embedded && !readOnlyPreview && node.DocumentId.HasValue && editingByCurrentUser);
        checkinButton.Enabled = CanOpenArchiveDialog;
        propertyEditButton.Enabled = rootNode != null && !rootNode.IsReadOnlyPreview;
        settingsButton.Enabled = true;
        if (workspaceOperationActive)
        {
            checkoutButton.Enabled = false;
            checkinButton.Enabled = false;
            propertyEditButton.Enabled = false;
        }
        ApplyStructureActionButtonAppearances();
        actionToolTip.SetToolTip(
            checkoutButton,
            checkedActionNodes.Count > 0
                ? string.Concat(checkoutButton.Text, "已勾选的", checkedActionNodes.Count, "个图档")
                : node.IsInterconnectImport ? "导入组件需保存为稳定原生内容，有效设计依赖随存档保存"
                : embedded ? "嵌入式组件随所属装配处理"
                : node.DrawingReviewLocked
                ? "图纸审核中，只允许只读打开，不能获取编辑权限"
                : editingByCurrentUser
                ? "释放当前图档的编辑权限，不生成新版本"
                : node.DocumentId.HasValue ? "按PLM版本准备新的工作副本并获取编辑权限" : canRegister ? "首次获取权限时将自动登记该图档" : "本地文件不存在或文件类型不支持登记");
        actionToolTip.SetToolTip(
            checkinButton,
            checkedActionNodes.Count > 0
                ? string.Concat("检查并整批提交已勾选的", checkedActionNodes.Count, "个图档，自动纳入必要的关联文件")
                : rootNode?.IsReadOnlyPreview == true ? "只读预览不能提交存档；请先获取编辑权限"
                    : "选择存档范围，统一检查后整批提交；未勾选时自动选择整套变更项");
        if (workspaceOperationActive)
        {
            var operationText = string.Concat(workspaceOperationStatus.Text, "，完成后按钮会自动恢复");
            actionToolTip.SetToolTip(checkoutButton, operationText);
            actionToolTip.SetToolTip(checkinButton, operationText);
            actionToolTip.SetToolTip(propertyEditButton, operationText);
        }
        UpdateTreeHealth();
    }

    private bool CanOpenArchiveDialog => !string.IsNullOrWhiteSpace(authenticatedUsername)
        && rootNode != null && (!rootNode.IsReadOnlyPreview || SelectedNode?.Kind == CadDocumentKind.Drawing
            && !SelectedNode.IsReadOnlyPreview) && !workspaceOperationActive;

    private void RefreshSelectionIndependentStructureActions()
    {
        checkinButton.Enabled = CanOpenArchiveDialog;
        propertyEditButton.Enabled = rootNode != null
            && !rootNode.IsReadOnlyPreview
            && !workspaceOperationActive;
        ApplyActionButtonAppearance(checkinButton, SubmitAvailableColor);
        ApplyActionButtonAppearance(propertyEditButton, SecondaryActionAvailableColor);
    }

    private string ProvenanceProjectText(CadTreeNode node)
    {
        if (!node.ProvenanceProjectId.HasValue) return string.Empty;
        var project = availableProjects.FirstOrDefault(item => item.Id == node.ProvenanceProjectId.Value);
        return project == null ? "" : string.Concat("（", project.Code, " · ", project.Name, "）");
    }

    private bool CanSelectForBatchAction(CadTreeNode node)
    {
        if (node == null || node.IsReadOnlyPreview || node.DrawingReviewLocked || string.IsNullOrWhiteSpace(authenticatedUsername)
            || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            return false;
        }

        return node.Kind == CadDocumentKind.Assembly
            || node.Kind == CadDocumentKind.Part
            || node.Kind == CadDocumentKind.Drawing;
    }

    private bool IsEditingByCurrentUser(CadTreeNode node) => node != null
        && !string.IsNullOrWhiteSpace(authenticatedUsername)
        && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
        && !node.CheckoutSessionLost;

    private bool CanCheckInNode(CadTreeNode node)
    {
        if (node == null || node.IsReadOnlyPreview || node.DrawingReviewLocked || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(authenticatedUsername)
            || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            return false;
        }

        var supported = node.Kind == CadDocumentKind.Assembly
            || node.Kind == CadDocumentKind.Part
            || node.Kind == CadDocumentKind.Drawing;
        if (!supported)
        {
            return false;
        }

        if (!node.DocumentId.HasValue)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            && !node.CheckoutSessionLost;
    }

    private void SynchronizeCheckedDocument(CadTreeNode node, bool isChecked)
    {
        if (string.IsNullOrWhiteSpace(node?.FullPath))
        {
            return;
        }

        if (isChecked)
        {
            checkedCheckInPaths.Add(node.FullPath);
        }
        else
        {
            checkedCheckInPaths.Remove(node.FullPath);
        }

        suppressTreeCheckEvents = true;
        try
        {
            SynchronizeVisibleChecks(structureTree.Nodes, node.FullPath, isChecked);
        }
        finally
        {
            suppressTreeCheckEvents = false;
        }
    }

    private static void SynchronizeVisibleChecks(TreeNodeCollection nodes, string fullPath, bool isChecked)
    {
        foreach (TreeNode treeNode in nodes)
        {
            if (treeNode.Tag is CadTreeNode model
                && string.Equals(model.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                treeNode.Checked = isChecked;
            }

            SynchronizeVisibleChecks(treeNode.Nodes, fullPath, isChecked);
        }
    }

    private IReadOnlyList<CadTreeNode> GetCheckedActionNodes()
    {
        if (rootNode == null || checkedCheckInPaths.Count == 0)
        {
            return Array.Empty<CadTreeNode>();
        }

        var paths = new HashSet<string>(checkedCheckInPaths, StringComparer.OrdinalIgnoreCase);
        return EnumerateCadTree(rootNode)
            .Where(node => !string.IsNullOrWhiteSpace(node.FullPath) && paths.Contains(node.FullPath) && CanSelectForBatchAction(node))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void SelectTreeDocuments(Func<CadTreeNode, bool> predicate)
    {
        checkedCheckInPaths.Clear();
        if (rootNode != null)
        {
            foreach (var node in EnumerateCadTree(rootNode).Where(predicate))
            {
                if (!string.IsNullOrWhiteSpace(node.FullPath)) checkedCheckInPaths.Add(node.FullPath);
            }
        }
        RebuildTree(searchBox.Text);
        UpdateSelected(SelectedNode);
    }

    private void ExpandAllMaterialized()
    {
        structureTree.BeginUpdate();
        try
        {
            foreach (TreeNode node in structureTree.Nodes) MaterializeAndExpand(node);
        }
        finally
        {
            structureTree.EndUpdate();
        }
    }

    private void CollapseStructureNode(TreeNode node)
    {
        allowStructureCollapse = true;
        try
        {
            node?.Collapse();
        }
        finally
        {
            allowStructureCollapse = false;
        }
    }

    private void CollapseAllStructureNodes()
    {
        allowStructureCollapse = true;
        try
        {
            structureTree.CollapseAll();
        }
        finally
        {
            allowStructureCollapse = false;
        }
    }

    private void MaterializeAndExpand(TreeNode node)
    {
        MaterializeAndExpand(node, activeTreeBuildPlan);
    }

    private void ExpandRootOnly(TreeNode root, TreeBuildPlan plan)
    {
        MaterializeChildren(root, plan);
        root.Expand();
        foreach (TreeNode child in root.Nodes)
            if (child.Tag is CadTreeNode model && model.Status == CadReferenceStatus.Virtual) MaterializeAndExpand(child, plan);
    }

    private void MaterializeAndExpand(TreeNode node, TreeBuildPlan plan)
    {
        MaterializeChildren(node, plan);
        foreach (TreeNode child in node.Nodes.Cast<TreeNode>().Where(child => !(child.Tag is LazyTreePlaceholder)))
            MaterializeAndExpand(child, plan);
        node.Expand();
    }

    private static IEnumerable<CadTreeNode> EnumerateCadTree(CadTreeNode root)
    {
        if (root == null)
        {
            yield break;
        }

        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var descendant in EnumerateCadTree(child))
            {
                yield return descendant;
            }
        }
    }

    private void UpdateTreeHealth()
    {
        if (rootNode == null)
        {
            treeHealth.Links.Clear();
            treeHealth.Cursor = Cursors.Default;
            treeHealth.Text = "结构健康：暂无结构";
            return;
        }
        var nodes = EnumerateCadTree(rootNode)
            .GroupBy(node => string.IsNullOrWhiteSpace(node.FullPath) ? node.InstancePath : node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var missing = nodes.Count(IsMissingReference);
        var outdated = nodes.Count(IsVersionOutdated);
        var otherEditing = nodes.Count(IsEditingByOther);
        var pending = nodes.Count(IsPendingSubmission);
        var activeCount = HealthFilterCount(activeHealthFilter, outdated, missing, otherEditing, pending);
        if (activeHealthFilter != StructureHealthFilter.None && activeCount == 0)
        {
            activeHealthFilter = StructureHealthFilter.None;
            selectFirstHealthMatchAfterBuild = false;
        }

        var items = new[]
        {
            new { Filter = StructureHealthFilter.Outdated, Text = string.Concat("版本落后 ", outdated), Count = outdated },
            new { Filter = StructureHealthFilter.Missing, Text = string.Concat("缺失 ", missing), Count = missing },
            new { Filter = StructureHealthFilter.OtherEditing, Text = string.Concat("他人编辑 ", otherEditing), Count = otherEditing },
            new { Filter = StructureHealthFilter.Pending, Text = string.Concat("待提交 ", pending), Count = pending }
        };
        treeHealth.Text = string.Concat(
            "结构健康：",
            string.Join("　", items.Select(item => item.Text)),
            activeHealthFilter == StructureHealthFilter.None ? string.Empty : string.Concat("（仅显示", HealthFilterName(activeHealthFilter), "）"));
        treeHealth.Links.Clear();
        foreach (var item in items.Where(item => item.Count > 0))
        {
            var start = treeHealth.Text.IndexOf(item.Text, StringComparison.Ordinal);
            treeHealth.Links.Add(start, item.Text.Length, item.Filter);
        }
        var hasHealthIssue = outdated > 0 || missing > 0 || otherEditing > 0 || pending > 0;
        treeHealth.Cursor = hasHealthIssue ? Cursors.Hand : Cursors.Default;
        treeHealth.AccessibleDescription = hasHealthIssue
            ? activeHealthFilter == StructureHealthFilter.None
                ? "点击异常类别，仅显示对应项目并定位第一项"
                : "点击当前类别恢复全部，或点击其他类别切换筛选"
            : "当前没有结构异常";
        actionToolTip.SetToolTip(treeHealth, treeHealth.AccessibleDescription);
        treeHealth.ForeColor = hasHealthIssue
            ? Color.FromArgb(174, 94, 0)
            : Color.FromArgb(21, 126, 77);
    }

    private void ToggleHealthFilter(StructureHealthFilter filter)
    {
        if (workspaceOperationActive || rootNode == null || filter == StructureHealthFilter.None
            || !EnumerateCadTree(rootNode).Any(node => MatchesHealthFilter(node, filter)))
        {
            return;
        }

        activeHealthFilter = activeHealthFilter == filter ? StructureHealthFilter.None : filter;
        selectFirstHealthMatchAfterBuild = activeHealthFilter != StructureHealthFilter.None;
        RebuildTree(searchBox.Text);
    }

    private static bool MatchesHealthFilter(CadTreeNode node, StructureHealthFilter filter)
    {
        switch (filter)
        {
            case StructureHealthFilter.Outdated: return IsVersionOutdated(node);
            case StructureHealthFilter.Missing: return IsMissingReference(node);
            case StructureHealthFilter.OtherEditing: return IsEditingByOther(node);
            case StructureHealthFilter.Pending: return IsPendingSubmission(node);
            default: return true;
        }
    }

    private static int HealthFilterCount(StructureHealthFilter filter, int outdated, int missing, int otherEditing, int pending)
    {
        switch (filter)
        {
            case StructureHealthFilter.Outdated: return outdated;
            case StructureHealthFilter.Missing: return missing;
            case StructureHealthFilter.OtherEditing: return otherEditing;
            case StructureHealthFilter.Pending: return pending;
            default: return 0;
        }
    }

    private static string HealthFilterName(StructureHealthFilter filter)
    {
        switch (filter)
        {
            case StructureHealthFilter.Outdated: return "版本落后";
            case StructureHealthFilter.Missing: return "缺失";
            case StructureHealthFilter.OtherEditing: return "他人编辑";
            case StructureHealthFilter.Pending: return "待提交";
            default: return string.Empty;
        }
    }

    private static bool IsMissingReference(CadTreeNode node) =>
        node != null
        && !node.IsRenamePendingSave
        && (node.Status == CadReferenceStatus.Missing
            || (!string.IsNullOrWhiteSpace(node.FullPath) && !File.Exists(node.FullPath)));

    private static bool IsEditingByOther(CadTreeNode node) =>
        node != null && node.WorkState == CadWorkState.EditingByOther && !node.CheckoutSessionLost;

    private static bool IsPendingSubmission(CadTreeNode node) =>
        node != null && (node.WorkState == CadWorkState.ModifiedUnsaved || node.WorkState == CadWorkState.PendingCheckIn
            || !string.IsNullOrWhiteSpace(node.DrawingUpdatePendingReason));

    private static bool IsVersionOutdated(CadTreeNode node) =>
        node != null
        && !string.IsNullOrWhiteSpace(node.LatestRevision)
        && !string.IsNullOrWhiteSpace(node.CurrentRevision)
        && !string.Equals(node.CurrentRevision.TrimEnd('*'), node.LatestRevision, StringComparison.OrdinalIgnoreCase);

    private enum StructureHealthFilter
    {
        None,
        Outdated,
        Missing,
        OtherEditing,
        Pending
    }

    private void RaiseCheckoutToggle()
    {
        var checkedNodes = GetCheckedActionNodes();
        if (checkedNodes.Count > 0)
        {
            var discard = checkedNodes.All(IsEditingByCurrentUser);
            var targets = (discard
                ? checkedNodes
                : checkedNodes.Where(node => !IsEditingByCurrentUser(node))).ToArray();
            if (targets.Length > 0)
            {
                var handler = discard ? DiscardCheckoutRequested : CheckoutRequested;
                PluginDiagnosticLog.Record("权限按钮", (discard ? "放弃编辑" : "获取权限") + "；勾选图档=" + targets.Length);
                handler?.Invoke(this, new CadTreeNodeEventArgs(targets[0], targets, true));
            }
            return;
        }

        var node = SelectedNode;
        if (node == null)
        {
            return;
        }

        var editingByCurrentUser = !string.IsNullOrWhiteSpace(authenticatedUsername)
            && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            && !node.CheckoutSessionLost;
        PluginDiagnosticLog.Record("权限按钮", (editingByCurrentUser ? "放弃编辑" : "获取权限") + "；图档=" + node.FullPath);
        RaiseSelected(editingByCurrentUser ? DiscardCheckoutRequested : CheckoutRequested);
    }

    private void RaiseSelected(EventHandler<CadTreeNodeEventArgs> handler)
    {
        var node = SelectedNode;
        if (node != null)
        {
            handler?.Invoke(this, new CadTreeNodeEventArgs(node));
        }
    }

    private void RaiseCheckInRequested()
    {
        if (!CanOpenArchiveDialog) return;
        var checkedNodes = GetCheckedActionNodes();
        if (checkedNodes.Count > 0)
        {
            CheckInRequested?.Invoke(this, new CadTreeNodeEventArgs(checkedNodes[0], checkedNodes, true));
            return;
        }

        if (SelectedNode?.Kind == CadDocumentKind.Drawing)
        {
            CheckInRequested?.Invoke(this, new CadTreeNodeEventArgs(SelectedNode, new[] { SelectedNode }, true));
            return;
        }

        BatchOperationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseBatchPropertyRequested()
    {
        var checkedNodes = GetCheckedActionNodes();
        if (checkedNodes.Count > 0)
        {
            BatchPropertyEditRequested?.Invoke(this, new CadTreeNodeEventArgs(checkedNodes[0], checkedNodes, true));
            return;
        }

        if (rootNode != null)
        {
            var allNodes = EnumerateCadTree(rootNode).ToArray();
            BatchPropertyEditRequested?.Invoke(this, new CadTreeNodeEventArgs(rootNode, allNodes, true));
        }
    }

    private void RaiseBatchPropertyCardRequested()
    {
        var checkedNodes = GetCheckedActionNodes();
        if (checkedNodes.Count > 0)
        {
            BatchPropertyCardRequested?.Invoke(this, new CadTreeNodeEventArgs(checkedNodes[0], checkedNodes, true));
            return;
        }

        var node = SelectedNode;
        if (node != null)
        {
            BatchPropertyCardRequested?.Invoke(this, new CadTreeNodeEventArgs(node));
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
            MessageBox.Show(this, "该图档尚未入库，暂无版本记录", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(authenticatedUsername))
        {
            ClearDisplayedVersions();
            MessageBox.Show(this, "请先登录PLM。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        button.AutoEllipsis = true;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = InputBorderColor;
        button.FlatAppearance.BorderSize = 1;
    }

    private static void ConfigureCompactActionButton(Button button, string text, Color availableColor)
    {
        button.Text = text;
        button.AccessibleName = text;
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.AutoSize = false;
        button.AutoEllipsis = true;
        button.TabStop = true;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = false;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(availableColor, 0.12F);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(availableColor, 0.10F);
        button.Cursor = Cursors.Hand;
    }

    private void ApplyStructureActionButtonAppearances()
    {
        ApplyActionButtonAppearance(
            checkoutButton,
            string.Equals(checkoutButton.Text, "放弃", StringComparison.Ordinal) ? DiscardCheckoutAvailableColor : CheckoutAvailableColor);
        ApplyActionButtonAppearance(checkinButton, SubmitAvailableColor);
        ApplyActionButtonAppearance(propertyEditButton, SecondaryActionAvailableColor);
        ApplyActionButtonAppearance(settingsButton, SecondaryActionAvailableColor);
    }

    private static void ApplyActionButtonAppearance(Button button, Color availableColor)
    {
        button.UseVisualStyleBackColor = false;
        button.BackColor = button.Enabled ? availableColor : DisabledActionBackgroundColor;
        button.ForeColor = button.Enabled ? Color.White : DisabledActionTextColor;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = button.Enabled ? ControlPaint.Light(availableColor, 0.12F) : DisabledActionBackgroundColor;
        button.FlatAppearance.MouseDownBackColor = button.Enabled ? ControlPaint.Dark(availableColor, 0.10F) : DisabledActionBackgroundColor;
        button.Invalidate();
    }

    private static void ConfigureStructureActionColumns(TableLayoutPanel actions, int buttons = 4, float gap = 6)
    {
        actions.ColumnStyles.Clear();
        for (var column = 0; column < buttons * 2 - 1; column++)
        {
            actions.ColumnStyles.Add(column % 2 == 0
                ? new ColumnStyle(SizeType.Percent, 100F / buttons)
                : new ColumnStyle(SizeType.Absolute, gap));
        }
    }

    private static void MatchProjectButtonWidths(TableLayoutPanel actions, ColumnStyle clientButtonColumn, ColumnStyle userButtonColumn)
    {
        const int structureToolbarGaps = 18;
        var availableWidth = Math.Max(0, actions.ClientSize.Width - actions.Padding.Horizontal);
        var buttonWidth = Math.Max(48, (availableWidth - structureToolbarGaps) / 4);
        clientButtonColumn.Width = buttonWidth;
        userButtonColumn.Width = buttonWidth;
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            if (!uiContext.IsCurrentThread)
            {
                uiContext.Post(_ => { if (!IsDisposed) action(); }, null);
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

    private void RefreshWorkspaceOperationUi()
    {
        if (refreshingWorkspaceOperationUi || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            refreshingWorkspaceOperationUi = true;
            workspaceOperationPanel.PerformLayout();
            workspaceOperationPanel.Invalidate(true);
            workspaceOperationProgress.Invalidate();
            serviceStatus.Invalidate();
            System.Windows.Forms.Application.DoEvents();
            workspaceOperationPanel.Update();
            workspaceOperationProgress.Update();
            serviceStatus.Update();
        }
        finally
        {
            refreshingWorkspaceOperationUi = false;
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
    public CadTreeNodeEventArgs(CadTreeNode node)
        : this(node, node == null ? Array.Empty<CadTreeNode>() : new[] { node }, false)
    {
    }

    public CadTreeNodeEventArgs(CadTreeNode node, IReadOnlyList<CadTreeNode> nodes, bool usesCheckedSelection)
    {
        Node = node;
        Nodes = nodes ?? Array.Empty<CadTreeNode>();
        UsesCheckedSelection = usesCheckedSelection;
    }

    public CadTreeNode Node { get; }

    public IReadOnlyList<CadTreeNode> Nodes { get; }

    public bool UsesCheckedSelection { get; }
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
