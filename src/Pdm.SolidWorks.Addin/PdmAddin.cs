using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace Upton.Pdm.SolidWorks;

[ComVisible(true)]
[Guid("BCFD8A8A-472B-42E2-AC62-58BC17773650")]
[ProgId("Upton.Pdm.SolidWorks.Addin")]
public sealed class PdmAddin : ISwAddin
{
    private ISldWorks application;
    private ITaskpaneView taskPaneView;
    private PdmTaskPaneControl taskPaneControl;
    private PdmApiClient apiClient;
    private SolidWorksReferenceTreeScanner scanner;
    private CancellationTokenSource lifetime;
    private CadTreeNode currentTree;
    private Guid? currentProjectId;
    private DSldWorksEvents_Event applicationEvents;
    private DAssemblyDocEvents_Event assemblyEvents;
    private System.Windows.Forms.Timer treeRefreshTimer;

    public bool ConnectToSW(object thisSw, int cookie)
    {
        application = (ISldWorks)thisSw;
        if (!application.SetAddinCallbackInfo2(0, this, cookie))
        {
            return false;
        }

        lifetime = new CancellationTokenSource();
        apiClient = new PdmApiClient("http://127.0.0.1:5080");
        scanner = new SolidWorksReferenceTreeScanner(application);
        taskPaneControl = new PdmTaskPaneControl();
        taskPaneControl.CreateControl();
        treeRefreshTimer = new System.Windows.Forms.Timer { Interval = 200 };
        treeRefreshTimer.Tick += OnTreeRefreshTimerTick;
        WireEvents();
        WireSolidWorksEvents();

        taskPaneView = application.CreateTaskpaneView2(CreateTaskPaneIcon(), "UPTON PDM");
        taskPaneView.DisplayWindowFromHandlex64(taskPaneControl.Handle.ToInt64());
        taskPaneControl.SetConnectionState(false, "未登录");
        RefreshTree();
        _ = LoginRememberedCredentialsAsync();
        return true;
    }

    public bool DisconnectFromSW()
    {
        lifetime?.Cancel();
        UnwireSolidWorksEvents();
        UnwireEvents();
        if (treeRefreshTimer != null)
        {
            treeRefreshTimer.Stop();
            treeRefreshTimer.Tick -= OnTreeRefreshTimerTick;
            treeRefreshTimer.Dispose();
        }
        taskPaneView?.DeleteView();
        taskPaneControl?.Dispose();
        apiClient?.Dispose();
        lifetime?.Dispose();
        taskPaneView = null;
        taskPaneControl = null;
        apiClient = null;
        scanner = null;
        treeRefreshTimer = null;
        application = null;
        return true;
    }

    [ComRegisterFunction]
    public static void Register(Type type)
    {
        var guid = type.GUID.ToString("B");
        using (var addinKey = Registry.LocalMachine.CreateSubKey(string.Concat(@"SOFTWARE\SOLIDWORKS\Addins\", guid)))
        {
            addinKey?.SetValue(null, 0, RegistryValueKind.DWord);
            addinKey?.SetValue("Title", "UPTON PDM");
            addinKey?.SetValue("Description", "UPTON PDM SolidWorks图档、版本与结构树插件");
        }

        using (var startupKey = Registry.CurrentUser.CreateSubKey(string.Concat(@"SOFTWARE\SOLIDWORKS\AddInsStartup\", guid)))
        {
            startupKey?.SetValue(null, 1, RegistryValueKind.DWord);
        }
    }

    [ComUnregisterFunction]
    public static void Unregister(Type type)
    {
        var guid = type.GUID.ToString("B");
        Registry.LocalMachine.DeleteSubKeyTree(string.Concat(@"SOFTWARE\SOLIDWORKS\Addins\", guid), false);
        Registry.CurrentUser.DeleteSubKeyTree(string.Concat(@"SOFTWARE\SOLIDWORKS\AddInsStartup\", guid), false);
    }

    private void WireEvents()
    {
        taskPaneControl.LoginRequested += OnLoginRequested;
        taskPaneControl.RefreshRequested += OnRefreshRequested;
        taskPaneControl.ProjectChanged += OnProjectChanged;
        taskPaneControl.NodeSelected += OnNodeSelected;
        taskPaneControl.OpenRequested += OnOpenRequested;
        taskPaneControl.CheckoutRequested += OnCheckoutRequested;
        taskPaneControl.CheckInRequested += OnCheckInRequested;
    }

    private void UnwireEvents()
    {
        if (taskPaneControl == null)
        {
            return;
        }

        taskPaneControl.LoginRequested -= OnLoginRequested;
        taskPaneControl.RefreshRequested -= OnRefreshRequested;
        taskPaneControl.ProjectChanged -= OnProjectChanged;
        taskPaneControl.NodeSelected -= OnNodeSelected;
        taskPaneControl.OpenRequested -= OnOpenRequested;
        taskPaneControl.CheckoutRequested -= OnCheckoutRequested;
        taskPaneControl.CheckInRequested -= OnCheckInRequested;
    }

    private void WireSolidWorksEvents()
    {
        applicationEvents = application as DSldWorksEvents_Event;
        if (applicationEvents != null)
        {
            applicationEvents.ActiveDocChangeNotify += OnActiveDocumentChanged;
            applicationEvents.ActiveModelDocChangeNotify += OnActiveDocumentChanged;
            applicationEvents.FileOpenPostNotify += OnFileOpened;
            applicationEvents.FileNewNotify2 += OnFileCreated;
            applicationEvents.FileCloseNotify += OnFileClosed;
        }

        BindAssemblyEvents();
    }

    private void UnwireSolidWorksEvents()
    {
        UnwireAssemblyEvents();
        if (applicationEvents == null)
        {
            return;
        }

        try
        {
            applicationEvents.ActiveDocChangeNotify -= OnActiveDocumentChanged;
            applicationEvents.ActiveModelDocChangeNotify -= OnActiveDocumentChanged;
            applicationEvents.FileOpenPostNotify -= OnFileOpened;
            applicationEvents.FileNewNotify2 -= OnFileCreated;
            applicationEvents.FileCloseNotify -= OnFileClosed;
        }
        catch (COMException)
        {
            // SolidWorks can release its event source before disconnecting the add-in.
        }

        applicationEvents = null;
    }

    private void BindAssemblyEvents()
    {
        UnwireAssemblyEvents();
        assemblyEvents = application?.ActiveDoc as DAssemblyDocEvents_Event;
        if (assemblyEvents == null)
        {
            return;
        }

        assemblyEvents.RegenPostNotify += OnAssemblyTreeChanged;
        assemblyEvents.ActiveConfigChangePostNotify += OnAssemblyTreeChanged;
        assemblyEvents.UndoPostNotify += OnAssemblyTreeChanged;
        assemblyEvents.RedoPostNotify += OnAssemblyTreeChanged;
        assemblyEvents.AddItemNotify += OnAssemblyItemChanged;
        assemblyEvents.DeleteItemNotify += OnAssemblyItemChanged;
        assemblyEvents.RenameItemNotify += OnAssemblyItemRenamed;
        assemblyEvents.ComponentStateChangeNotify3 += OnAssemblyComponentStateChanged;
        assemblyEvents.ComponentReorganizeNotify += OnAssemblyComponentReorganized;
        assemblyEvents.ComponentConfigurationChangeNotify += OnAssemblyComponentConfigurationChanged;
    }

    private void UnwireAssemblyEvents()
    {
        if (assemblyEvents == null)
        {
            return;
        }

        try
        {
            assemblyEvents.RegenPostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.ActiveConfigChangePostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.UndoPostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.RedoPostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.AddItemNotify -= OnAssemblyItemChanged;
            assemblyEvents.DeleteItemNotify -= OnAssemblyItemChanged;
            assemblyEvents.RenameItemNotify -= OnAssemblyItemRenamed;
            assemblyEvents.ComponentStateChangeNotify3 -= OnAssemblyComponentStateChanged;
            assemblyEvents.ComponentReorganizeNotify -= OnAssemblyComponentReorganized;
            assemblyEvents.ComponentConfigurationChangeNotify -= OnAssemblyComponentConfigurationChanged;
        }
        catch (COMException)
        {
            // The document may already be closing.
        }

        assemblyEvents = null;
    }

    private int OnActiveDocumentChanged()
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnFileOpened(string fileName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnFileCreated(object newDocument, int documentType, string templateName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnFileClosed(string fileName, int reason)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyTreeChanged()
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyItemChanged(int entityType, string itemName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyItemRenamed(int entityType, string oldName, string newName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentStateChanged(object component, string componentName, short oldState, short newState)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentReorganized(string sourceName, string targetName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentConfigurationChanged(string componentName, string oldConfigurationName, string newConfigurationName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private void ScheduleTreeRefresh()
    {
        if (taskPaneControl == null || taskPaneControl.IsDisposed || !taskPaneControl.IsHandleCreated || treeRefreshTimer == null)
        {
            return;
        }

        if (taskPaneControl.InvokeRequired)
        {
            taskPaneControl.BeginInvoke((Action)ScheduleTreeRefresh);
            return;
        }

        treeRefreshTimer.Stop();
        treeRefreshTimer.Start();
    }

    private void OnTreeRefreshTimerTick(object sender, EventArgs eventArgs)
    {
        treeRefreshTimer.Stop();
        BindAssemblyEvents();
        RefreshTree();
    }

    private async void OnLoginRequested(object sender, EventArgs eventArgs)
    {
        using (var dialog = new LoginDialog())
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
            {
                return;
            }

            try
            {
                await AuthenticateAsync(dialog.Username, dialog.Password);
                PersistRememberedCredentials(dialog);
            }
            catch (Exception exception)
            {
                taskPaneControl.SetConnectionState(false, "登录失败");
                ShowError(exception.Message);
            }
        }
    }

    private async Task AuthenticateAsync(string username, string password)
    {
        taskPaneControl.SetConnectionState(false, "正在登录");
        var response = await apiClient.LoginAsync(username, password, lifetime.Token);
        taskPaneControl.SetAuthenticatedUser(response.DisplayName);
        taskPaneControl.SetConnectionState(true, "服务正常");
        var projects = await apiClient.GetProjectsAsync(lifetime.Token);
        taskPaneControl.SetProjects(projects);
    }

    private async Task LoginRememberedCredentialsAsync()
    {
        if (!RememberedCredentialsStore.TryLoad(out var username, out var password))
        {
            return;
        }

        try
        {
            await AuthenticateAsync(username, password);
        }
        catch (OperationCanceledException) when (lifetime == null || lifetime.IsCancellationRequested)
        {
            // SolidWorks is closing while the automatic login is still running.
        }
        catch
        {
            taskPaneControl?.SetConnectionState(false, "自动登录失败");
        }
    }

    private void PersistRememberedCredentials(LoginDialog dialog)
    {
        try
        {
            if (dialog.RememberCredentials)
            {
                RememberedCredentialsStore.Save(dialog.Username, dialog.Password);
            }
            else
            {
                RememberedCredentialsStore.Clear();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                taskPaneControl,
                string.Concat("登录成功，但登录信息未能保存：", exception.Message),
                "UPTON PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnRefreshRequested(object sender, EventArgs eventArgs) => RefreshTree();

    private async void OnProjectChanged(object sender, EventArgs eventArgs)
    {
        currentProjectId = taskPaneControl.SelectedProjectId;
        if (currentProjectId.HasValue && apiClient.IsAuthenticated)
        {
            await RefreshMetadataAsync(currentProjectId.Value);
        }
    }

    private void OnNodeSelected(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Node.ComponentSelectionName))
        {
            return;
        }

        try
        {
            var model = application.ActiveDoc as IModelDoc2;
            model?.Extension.SelectByID2(eventArgs.Node.ComponentSelectionName, "COMPONENT", 0, 0, 0, false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
        }
        catch
        {
            // Selection synchronization must not break the task pane.
        }
    }

    private void OnOpenRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Node.FullPath) || !File.Exists(eventArgs.Node.FullPath))
        {
            ShowError("文件不存在或尚未保存。 ");
            return;
        }

        var document = application.GetOpenDocumentByName(eventArgs.Node.FullPath) as IModelDoc2;
        var openErrors = 0;
        var openWarnings = 0;
        if (document == null)
        {
            document = application.OpenDoc6(
                eventArgs.Node.FullPath,
                ToSolidWorksDocumentType(eventArgs.Node.Kind),
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                eventArgs.Node.Configuration ?? string.Empty,
                ref openErrors,
                ref openWarnings);
        }

        if (document == null)
        {
            ShowError(string.Concat("SolidWorks打开文件失败，错误码：", openErrors));
            return;
        }

        var activationErrors = 0;
        var activated = application.ActivateDoc3(
            document.GetTitle(),
            false,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
            ref activationErrors) as IModelDoc2;
        if (activated == null || activationErrors == (int)swActivateDocError_e.swGenericActivateError)
        {
            ShowError(string.Concat("文件已加载，但无法切换到该文档，错误码：", activationErrors));
        }
    }

    private async void OnCheckoutRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!EnsureServerDocument(eventArgs.Node))
        {
            return;
        }

        try
        {
            var document = await apiClient.CheckoutAsync(eventArgs.Node.DocumentId.Value, lifetime.Token);
            eventArgs.Node.CheckedOutBy = document.CheckedOutBy;
            eventArgs.Node.Revision = document.Revision?.Display ?? eventArgs.Node.Revision;
            taskPaneControl.SetTree(currentTree);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnCheckInRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var root = currentTree;
        if (root == null || !currentProjectId.HasValue || !EnsureServerDocument(root))
        {
            return;
        }

        if (root.HasBlockingIssue)
        {
            ShowError("结构树存在缺失引用，不能提交存档。 ");
            return;
        }

        try
        {
            var document = await apiClient.CheckInAsync(root.DocumentId.Value, currentProjectId.Value, root, "SolidWorks插件提交存档", lifetime.Token);
            root.CheckedOutBy = document.CheckedOutBy;
            root.Revision = document.Revision?.Display ?? root.Revision;
            taskPaneControl.SetTree(root);
            MessageBox.Show(taskPaneControl, string.Concat("提交存档成功，工作版本：", root.Revision), "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void RefreshTree()
    {
        try
        {
            currentTree = scanner.ScanActiveDocument();
            taskPaneControl.SetTree(currentTree);
            if (currentProjectId.HasValue && apiClient.IsAuthenticated)
            {
                _ = RefreshMetadataAsync(currentProjectId.Value);
            }
        }
        catch (Exception exception)
        {
            if (application.ActiveDoc == null)
            {
                currentTree = null;
                taskPaneControl.ClearTree();
                taskPaneControl.SetConnectionState(false, "未打开图档");
                return;
            }

            ShowError(exception.Message);
        }
    }

    private async Task RefreshMetadataAsync(Guid projectId)
    {
        try
        {
            var documents = await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
            var byFileName = documents
                .GroupBy(document => document.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ApplyMetadata(currentTree, byFileName);
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.SetConnectionState(true, "服务正常");
        }
        catch (Exception exception)
        {
            taskPaneControl.SetConnectionState(false, "服务不可用");
            ShowError(exception.Message);
        }
    }

    private static void ApplyMetadata(CadTreeNode node, IReadOnlyDictionary<string, DocumentDto> documents)
    {
        if (node == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.FileName) && documents.TryGetValue(node.FileName, out var document))
        {
            node.DocumentId = document.Id;
            node.CheckedOutBy = document.CheckedOutBy;
            node.Revision = document.Revision?.Display ?? string.Empty;
        }

        foreach (var child in node.Children)
        {
            ApplyMetadata(child, documents);
        }
    }

    private bool EnsureServerDocument(CadTreeNode node)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。 ");
            return false;
        }

        if (!node.DocumentId.HasValue)
        {
            ShowError("该文件尚未在当前项目图档库登记。 ");
            return false;
        }

        return true;
    }

    private static int ToSolidWorksDocumentType(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return (int)swDocumentTypes_e.swDocASSEMBLY;
            case CadDocumentKind.Part: return (int)swDocumentTypes_e.swDocPART;
            case CadDocumentKind.Drawing: return (int)swDocumentTypes_e.swDocDRAWING;
            default: return (int)swDocumentTypes_e.swDocNONE;
        }
    }

    private static string CreateTaskPaneIcon()
    {
        var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
        Directory.CreateDirectory(directory);
        var iconPath = Path.Combine(directory, "pdm-taskpane.bmp");
        if (File.Exists(iconPath))
        {
            return iconPath;
        }

        using (var bitmap = new Bitmap(20, 20))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(36, 170, 168)))
        using (var textBrush = new SolidBrush(Color.White))
        using (var font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.FillRectangle(background, 0, 0, 20, 20);
            graphics.DrawString("P", font, textBrush, new PointF(5, 3));
            bitmap.Save(iconPath);
        }

        return iconPath;
    }

    private void ShowError(string message) =>
        MessageBox.Show(taskPaneControl, message, "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
