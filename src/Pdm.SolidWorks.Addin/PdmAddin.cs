using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private string assemblyEventDocumentPath = string.Empty;
    private DPartDocEvents_Event partEvents;
    private string partEventDocumentPath = string.Empty;
    private string authenticatedUsername = string.Empty;
    private int openOperationInProgress;
    private int checkInOperationInProgress;
    private int treeRefreshInProgress;
    private int refreshSuppressionDepth;
    private int pendingTreeRefresh;
    private bool disconnecting;

    public bool ConnectToSW(object thisSw, int cookie)
    {
        try
        {
            disconnecting = false;
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
            WireEvents();
            WireSolidWorksEvents();

            taskPaneView = application.CreateTaskpaneView2(CreateTaskPaneIcon(), "UPTON PDM");
            taskPaneView.DisplayWindowFromHandlex64(taskPaneControl.Handle.ToInt64());
            taskPaneControl.SetConnectionState(false, "未登录");
            RefreshTree(false);
            _ = LoginRememberedCredentialsAsync();
            return true;
        }
        catch (Exception exception)
        {
            LogDiagnostic("ConnectToSW", exception);
            DisconnectFromSW();
            return false;
        }
    }

    public bool DisconnectFromSW()
    {
        disconnecting = true;
        try
        {
            lifetime?.Cancel();
            UnwireSolidWorksEvents();
            UnwireEvents();

            taskPaneView?.DeleteView();
            taskPaneControl?.Dispose();
            apiClient?.Dispose();
            lifetime?.Dispose();
        }
        catch (Exception exception)
        {
            LogDiagnostic("DisconnectFromSW", exception);
        }
        finally
        {
            taskPaneView = null;
            taskPaneControl = null;
            apiClient = null;
            scanner = null;
            lifetime = null;
            application = null;
            authenticatedUsername = string.Empty;
        }

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
        taskPaneControl.GetLatestVersionRequested += OnGetLatestVersionRequested;
        taskPaneControl.CheckoutRequested += OnCheckoutRequested;
        taskPaneControl.CheckInRequested += OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested += OnDiscardCheckoutRequested;
        taskPaneControl.VersionsRequested += OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested += OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested += OnCompareVersionsRequested;
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
        taskPaneControl.GetLatestVersionRequested -= OnGetLatestVersionRequested;
        taskPaneControl.CheckoutRequested -= OnCheckoutRequested;
        taskPaneControl.CheckInRequested -= OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested -= OnDiscardCheckoutRequested;
        taskPaneControl.VersionsRequested -= OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested -= OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested -= OnCompareVersionsRequested;
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
            applicationEvents.OnIdleNotify += OnSolidWorksIdle;
        }

        BindAssemblyEvents();
    }

    private void UnwireSolidWorksEvents()
    {
        UnwireAssemblyEvents();
        UnwirePartEvents();
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
            applicationEvents.OnIdleNotify -= OnSolidWorksIdle;
        }
        catch (Exception exception)
        {
            // SolidWorks can release its event source before disconnecting the add-in.
            LogDiagnostic("UnwireSolidWorksEvents", exception);
        }

        applicationEvents = null;
    }

    private void BindAssemblyEvents()
    {
        try
        {
            var activeDocument = application?.ActiveDoc as IModelDoc2;
            var activePath = activeDocument?.GetPathName() ?? string.Empty;
            var isAssembly = activeDocument != null && activeDocument.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            var isPart = activeDocument != null && activeDocument.GetType() == (int)swDocumentTypes_e.swDocPART;
            if (isAssembly && assemblyEvents != null && string.Equals(activePath, assemblyEventDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (isPart && partEvents != null && string.Equals(activePath, partEventDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnwireAssemblyEvents();
            UnwirePartEvents();
            if (isPart)
            {
                partEvents = activeDocument as DPartDocEvents_Event;
                if (partEvents != null)
                {
                    partEventDocumentPath = activePath;
                    partEvents.ModifyNotify += OnAssemblyTreeChanged;
                    partEvents.FileSavePostNotify += OnAssemblyFileSaved;
                    partEvents.RegenPostNotify += OnAssemblyTreeChanged;
                    partEvents.UndoPostNotify += OnAssemblyTreeChanged;
                    partEvents.RedoPostNotify += OnAssemblyTreeChanged;
                }
                return;
            }

            if (!isAssembly)
            {
                return;
            }

            assemblyEvents = activeDocument as DAssemblyDocEvents_Event;
            if (assemblyEvents == null)
            {
                return;
            }

            assemblyEventDocumentPath = activePath;

            assemblyEvents.RegenPostNotify += OnAssemblyTreeChanged;
            assemblyEvents.ModifyNotify += OnAssemblyTreeChanged;
            assemblyEvents.FileSavePostNotify += OnAssemblyFileSaved;
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
        catch (Exception exception)
        {
            LogDiagnostic("BindAssemblyEvents", exception);
            UnwireAssemblyEvents();
        }
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
            assemblyEvents.ModifyNotify -= OnAssemblyTreeChanged;
            assemblyEvents.FileSavePostNotify -= OnAssemblyFileSaved;
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
        catch (Exception exception)
        {
            // The document may already be closing.
            LogDiagnostic("UnwireAssemblyEvents", exception);
        }

        assemblyEvents = null;
        assemblyEventDocumentPath = string.Empty;
    }

    private void UnwirePartEvents()
    {
        if (partEvents == null)
        {
            return;
        }

        try
        {
            partEvents.ModifyNotify -= OnAssemblyTreeChanged;
            partEvents.FileSavePostNotify -= OnAssemblyFileSaved;
            partEvents.RegenPostNotify -= OnAssemblyTreeChanged;
            partEvents.UndoPostNotify -= OnAssemblyTreeChanged;
            partEvents.RedoPostNotify -= OnAssemblyTreeChanged;
        }
        catch (Exception exception)
        {
            LogDiagnostic("UnwirePartEvents", exception);
        }

        partEvents = null;
        partEventDocumentPath = string.Empty;
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

    private int OnAssemblyFileSaved(int saveType, string fileName)
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
        if (disconnecting)
        {
            return;
        }

        Interlocked.Exchange(ref pendingTreeRefresh, 1);
    }

    private int OnSolidWorksIdle()
    {
        if (disconnecting
            || Volatile.Read(ref refreshSuppressionDepth) > 0
            || Volatile.Read(ref openOperationInProgress) > 0
            || Volatile.Read(ref pendingTreeRefresh) == 0
            || taskPaneControl == null
            || taskPaneControl.IsDisposed
            || !taskPaneControl.IsHandleCreated)
        {
            return 0;
        }

        if (disconnecting || Interlocked.Exchange(ref treeRefreshInProgress, 1) != 0)
        {
            return 0;
        }

        try
        {
            Interlocked.Exchange(ref pendingTreeRefresh, 0);
            LogOperation("IdleRefresh start");
            BindAssemblyEvents();
            RefreshTree(false);
            LogOperation("IdleRefresh end");
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnSolidWorksIdle", exception);
        }
        finally
        {
            Interlocked.Exchange(ref treeRefreshInProgress, 0);
        }

        return 0;
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
        authenticatedUsername = response.Username ?? string.Empty;
        taskPaneControl.SetAuthenticatedUser(response.DisplayName, response.Username);
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

    private void OnRefreshRequested(object sender, EventArgs eventArgs) => RefreshTree(true);

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
        var node = eventArgs.Node;
        if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            ShowError("文件不存在或尚未保存。 ");
            return;
        }

        QueueOpenDocument(node.FullPath, node.Kind, node.Configuration);
    }

    private async void OnGetLatestVersionRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!EnsureServerDocument(eventArgs.Node))
        {
            return;
        }

        try
        {
            var versions = await apiClient.GetVersionsAsync(eventArgs.Node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault();
            if (latest == null)
            {
                ShowError("该图档尚无可获取的存档版本。");
                return;
            }

            var path = await apiClient.DownloadVersionToTempAsync(
                eventArgs.Node.DocumentId.Value,
                latest.Id,
                eventArgs.Node.FileName,
                lifetime.Token);
            QueueOpenDocument(path, DocumentKindFromPath(path), string.Empty);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void QueueOpenDocument(string fullPath, CadDocumentKind kind, string configuration)
    {
        var documentType = ToSolidWorksDocumentType(kind);
        if (documentType == (int)swDocumentTypes_e.swDocNONE)
        {
            ShowError("该文件类型不能在SolidWorks中直接打开。 ");
            return;
        }

        if (Volatile.Read(ref checkInOperationInProgress) > 0)
        {
            ShowError("提交存档正在进行，请稍候再打开其他图档。");
            return;
        }

        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed || Interlocked.Exchange(ref openOperationInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            LogOperation(string.Concat("Open queued path=", fullPath));
            taskPaneControl.BeginInvoke((Action)(() => OpenDocumentOnSolidWorksThread(fullPath, documentType, configuration ?? string.Empty)));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref openOperationInProgress, 0);
            LogDiagnostic("QueueOpenDocument", exception);
            ShowError(string.Concat("打开图档失败：", exception.Message));
        }
    }

    private void OpenDocumentOnSolidWorksThread(string fullPath, int documentType, string configuration)
    {
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            if (disconnecting || application == null)
            {
                return;
            }

            OpenOrActivateDocumentOnSolidWorksThread(fullPath, documentType, configuration);
        }
        catch (Exception exception)
        {
            LogDiagnostic("OpenDocumentOnSolidWorksThread", exception);
            ShowError(string.Concat("打开图档失败：", exception.Message));
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref openOperationInProgress, 0);
            ScheduleTreeRefresh();
        }
    }

    private IModelDoc2 FindLoadedDocument(string fullPath)
    {
        var documents = application.GetDocuments() as Array;
        if (documents == null)
        {
            return null;
        }

        foreach (var item in documents)
        {
            if (item is IModelDoc2 document && PathsEqual(document.GetPathName(), fullPath))
            {
                return document;
            }
        }

        return null;
    }

    private async void OnCheckoutRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null || !apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。");
            return;
        }

        try
        {
            if (!node.DocumentId.HasValue)
            {
                if (!currentProjectId.HasValue)
                {
                    ShowError("请先选择当前项目。");
                    return;
                }

                if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
                {
                    ShowError("本地文件不存在，不能登记并获取权限。");
                    return;
                }

                if (node.Kind != CadDocumentKind.Assembly && node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Drawing)
                {
                    ShowError("只有SolidWorks装配体、零件和工程图可以登记并获取权限。");
                    return;
                }

                var registered = await apiClient.RegisterDocumentAsync(currentProjectId.Value, node, lifetime.Token);
                node.DocumentId = registered.Id;
                node.Revision = registered.Revision?.Display ?? node.Revision;
                node.CheckedOutBy = registered.CheckedOutBy;
            }

            var document = await apiClient.CheckoutAsync(node.DocumentId.Value, lifetime.Token);
            node.CheckedOutBy = document.CheckedOutBy;
            node.Revision = document.Revision?.Display ?? node.Revision;
            node.WorkState = CadWorkState.Editable;
            taskPaneControl.SetTree(currentTree);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OnCheckInRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null || !currentProjectId.HasValue || !EnsureServerDocument(node))
        {
            return;
        }

        if (node.HasBlockingIssue)
        {
            ShowError("结构树存在缺失引用，不能提交存档。 ");
            return;
        }

        if (Volatile.Read(ref openOperationInProgress) > 0 || Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
        {
            ShowError("正在打开图档或已有提交存档任务，请稍候再试。");
            return;
        }

        try
        {
            var projectId = currentProjectId.Value;
            taskPaneControl.BeginInvoke((Action)(() => PrepareAndCheckInOnSolidWorksThread(node, projectId)));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref checkInOperationInProgress, 0);
            ShowError(exception.Message);
        }
    }

    private async void OnDiscardCheckoutRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null || !EnsureServerDocument(node))
        {
            return;
        }

        var warning = node.WorkState == CadWorkState.ModifiedUnsaved || node.WorkState == CadWorkState.PendingCheckIn
            ? "检测到未提交的修改。放弃编辑后，这些修改不会存档。是否继续？"
            : "放弃编辑后将释放编辑权限，当前版本不会变化。是否继续？";
        if (MessageBox.Show(taskPaneControl, warning, "放弃编辑", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var document = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, lifetime.Token);
            node.CheckedOutBy = document.CheckedOutBy;
            node.Revision = document.Revision?.Display ?? node.Revision;
            node.WorkState = CadWorkState.None;
            taskPaneControl.SetTree(currentTree);
            MessageBox.Show(taskPaneControl, string.Concat("已放弃编辑，版本仍为", node.Revision, "。本地文件未被覆盖。"), "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void PrepareAndCheckInOnSolidWorksThread(CadTreeNode node, Guid projectId)
    {
        var originalDocumentPath = string.Empty;
        var uploadCopyPath = string.Empty;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            if (disconnecting || application == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                ShowError("本地图档不存在，不能提交存档。");
                return;
            }

            originalDocumentPath = (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty;
            var documentType = ToSolidWorksDocumentType(node.Kind);
            if (documentType == (int)swDocumentTypes_e.swDocNONE)
            {
                ShowError("该文件类型不能提交存档。");
                return;
            }

            var document = OpenOrActivateDocumentOnSolidWorksThread(node.FullPath, documentType, node.Configuration ?? string.Empty);
            var activePath = document?.GetPathName() ?? string.Empty;
            if (document == null || !PathsEqual(activePath, node.FullPath))
            {
                ShowError("未能安全激活所选图档，未执行提交存档。");
                return;
            }

            if (!document.GetSaveFlag()
                && !string.IsNullOrWhiteSpace(node.LatestVersionSha256))
            {
                var unchangedSha256 = ComputeFileHash(activePath);
                if (string.Equals(unchangedSha256, node.LatestVersionSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, unchangedSha256, lifetime.Token);
                    node.CheckedOutBy = unchanged.CheckedOutBy;
                    node.Revision = unchanged.Revision?.Display ?? node.Revision;
                    node.WorkState = CadWorkState.None;
                    taskPaneControl.SetTree(currentTree);
                    MessageBox.Show(taskPaneControl, string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"), "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var saveErrors = 0;
            var saveWarnings = 0;
            LogOperation(string.Concat("CheckIn Save3 start path=", activePath));
            var saved = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
            LogOperation(string.Concat("CheckIn Save3 end path=", activePath, " saved=", saved, " errors=", saveErrors, " warnings=", saveWarnings));
            if (!saved || saveErrors != 0)
            {
                ShowError(string.Concat("SolidWorks保存图档失败，未提交存档。错误码：", saveErrors, "，警告码：", saveWarnings));
                return;
            }

            var currentSha256 = ComputeFileHash(activePath);
            if (!string.IsNullOrWhiteSpace(node.LatestVersionSha256)
                && string.Equals(currentSha256, node.LatestVersionSha256, StringComparison.OrdinalIgnoreCase))
            {
                var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, currentSha256, lifetime.Token);
                node.CheckedOutBy = unchanged.CheckedOutBy;
                node.Revision = unchanged.Revision?.Display ?? node.Revision;
                node.WorkState = CadWorkState.None;
                taskPaneControl.SetTree(currentTree);
                MessageBox.Show(taskPaneControl, string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"), "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string changeNote;
            using (var dialog = new ChangeNoteDialog(node.FileName))
            {
                if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
                {
                    return;
                }

                changeNote = dialog.ChangeNote;
            }

            var modelProperties = ReadModelProperties(document);
            uploadCopyPath = CreateCheckInUploadCopy(document, activePath, node.DocumentId.Value);
            var storedFile = await apiClient.UploadVersionFileAsync(projectId, uploadCopyPath, node.DocumentId.Value, activePath, lifetime.Token);
            var result = await apiClient.CheckInAsync(node.DocumentId.Value, projectId, node, changeNote, storedFile, modelProperties, lifetime.Token);
            node.CheckedOutBy = result.Document.CheckedOutBy;
            node.Revision = result.Version?.Revision?.Display ?? result.Document.Revision?.Display ?? node.Revision;
            node.WorkState = CadWorkState.None;
            node.LatestVersionSha256 = result.Version?.Sha256 ?? node.LatestVersionSha256;
            taskPaneControl.SetTree(currentTree);
            MessageBox.Show(
                taskPaneControl,
                result.VersionCreated
                    ? string.Concat("提交存档成功，工作版本：", node.Revision)
                    : string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"),
                "UPTON PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            LogDiagnostic("PrepareAndCheckInOnSolidWorksThread", exception);
            ShowError(exception.Message);
        }
        finally
        {
            try
            {
                RestoreOriginalDocumentAfterCheckIn(originalDocumentPath, node?.FullPath ?? string.Empty);
            }
            catch (Exception exception)
            {
                LogDiagnostic("RestoreOriginalDocumentAfterCheckIn", exception);
            }
            finally
            {
                DeleteCheckInUploadCopy(uploadCopyPath);
                Interlocked.Decrement(ref refreshSuppressionDepth);
                Interlocked.Exchange(ref checkInOperationInProgress, 0);
                ScheduleTreeRefresh();
            }
        }
    }

    private static string CreateCheckInUploadCopy(IModelDoc2 document, string sourcePath, Guid documentId)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "checkin", documentId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copyPath = Path.Combine(directory, Path.GetFileName(sourcePath));
        var saveErrors = 0;
        var saveWarnings = 0;
        LogOperation(string.Concat("CheckIn SaveAs copy start source=", sourcePath, " target=", copyPath));
        var copied = document.Extension.SaveAs(
            copyPath,
            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
            null,
            ref saveErrors,
            ref saveWarnings);
        LogOperation(string.Concat("CheckIn SaveAs copy end target=", copyPath, " copied=", copied, " errors=", saveErrors, " warnings=", saveWarnings));
        if (!copied || saveErrors != 0 || !File.Exists(copyPath))
        {
            DeleteCheckInUploadCopy(copyPath);
            throw new IOException(string.Concat("SolidWorks创建提交副本失败。错误码：", saveErrors, "，警告码：", saveWarnings));
        }

        return copyPath;
    }

    private static void DeleteCheckInUploadCopy(string copyPath)
    {
        if (string.IsNullOrWhiteSpace(copyPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(copyPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("DeleteCheckInUploadCopy", exception);
        }
    }

    private IModelDoc2 OpenOrActivateDocumentOnSolidWorksThread(string fullPath, int documentType, string configuration)
    {
        var activeDocument = application.ActiveDoc as IModelDoc2;
        if (activeDocument != null && PathsEqual(activeDocument.GetPathName(), fullPath))
        {
            LogOperation(string.Concat("Open skipped already-active path=", fullPath));
            return activeDocument;
        }

        var document = FindLoadedDocument(fullPath);
        if (document == null)
        {
            var openErrors = 0;
            var openWarnings = 0;
            LogOperation(string.Concat("OpenDoc6 start path=", fullPath));
            document = application.OpenDoc6(
                fullPath,
                documentType,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                configuration,
                ref openErrors,
                ref openWarnings);
            LogOperation(string.Concat("OpenDoc6 end path=", fullPath, " errors=", openErrors, " warnings=", openWarnings, " null=", document == null));
            if (document == null)
            {
                ShowError(string.Concat("SolidWorks打开文件失败，错误码：", openErrors));
                return null;
            }
        }
        else
        {
            LogOperation(string.Concat("Open reused loaded document path=", fullPath));
        }

        var activationErrors = 0;
        var documentName = Path.GetFileName(fullPath);
        LogOperation(string.Concat("ActivateDoc3 start name=", documentName));
        var activated = application.ActivateDoc3(
            documentName,
            false,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
            ref activationErrors) as IModelDoc2;
        LogOperation(string.Concat("ActivateDoc3 end name=", documentName, " errors=", activationErrors, " null=", activated == null));
        if (activated == null || activationErrors == (int)swActivateDocError_e.swGenericActivateError || !PathsEqual(activated.GetPathName(), fullPath))
        {
            ShowError(string.Concat("文件已加载，但无法安全切换到该文档，错误码：", activationErrors));
            return null;
        }

        return activated;
    }

    private void RestoreOriginalDocumentAfterCheckIn(string originalDocumentPath, string submittedDocumentPath)
    {
        if (disconnecting
            || application == null
            || string.IsNullOrWhiteSpace(originalDocumentPath)
            || PathsEqual(originalDocumentPath, submittedDocumentPath)
            || !(application.ActiveDoc is IModelDoc2 activeDocument)
            || !PathsEqual(activeDocument.GetPathName(), submittedDocumentPath)
            || FindLoadedDocument(originalDocumentPath) == null)
        {
            return;
        }

        var activationErrors = 0;
        LogOperation(string.Concat("CheckIn restore start path=", originalDocumentPath));
        var restored = application.ActivateDoc3(
            Path.GetFileName(originalDocumentPath),
            false,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
            ref activationErrors) as IModelDoc2;
        LogOperation(string.Concat("CheckIn restore end path=", originalDocumentPath, " errors=", activationErrors, " null=", restored == null));
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

    private async void OnVersionsRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var documentId = eventArgs.Node.DocumentId;
        if (!documentId.HasValue)
        {
            ShowError("该图档尚未入库，暂无版本记录");
            return;
        }
        try
        {
            var versions = await apiClient.GetVersionsAsync(documentId.Value, lifetime.Token);
            if (taskPaneControl.SelectedNode?.DocumentId == documentId) taskPaneControl.ShowVersions(versions);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static IReadOnlyDictionary<string, string> ReadModelProperties(IModelDoc2 document)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document == null) return result;
        ReadPropertyManager(document.Extension.CustomPropertyManager[string.Empty], "全局", result);
        var configurationName = document.ConfigurationManager?.ActiveConfiguration?.Name;
        if (!string.IsNullOrWhiteSpace(configurationName))
            ReadPropertyManager(document.Extension.CustomPropertyManager[configurationName], string.Concat("配置:", configurationName), result);
        return result;
    }

    private static void ReadPropertyManager(CustomPropertyManager manager, string scope, IDictionary<string, string> target)
    {
        if (manager == null || !(manager.GetNames() is string[] names)) return;
        foreach (var name in names)
        {
            var raw = string.Empty;
            var resolved = string.Empty;
            var wasResolved = false;
            var linked = false;
            manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
            target[string.Concat(scope, "/", name)] = wasResolved ? resolved : raw;
        }
    }

    private async void OnOpenHistoryRequested(object sender, DocumentVersionEventArgs eventArgs)
    {
        try
        {
            var path = await apiClient.DownloadVersionToTempAsync(eventArgs.DocumentId, eventArgs.Version.Id, eventArgs.FileName, lifetime.Token);
            QueueOpenDocument(path, DocumentKindFromPath(path), string.Empty);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void OnCompareVersionsRequested(object sender, VersionComparisonEventArgs eventArgs)
    {
        try
        {
            var executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Upton.Pdm.Desktop.exe");
            if (!File.Exists(executable)) executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "client", "Upton.Pdm.Desktop.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("未找到Windows客户端。");
            System.Diagnostics.Process.Start(executable, string.Concat("--compare ", eventArgs.DocumentId, " ", eventArgs.LeftVersionId, " ", eventArgs.RightVersionId));
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static CadDocumentKind DocumentKindFromPath(string path)
    {
        switch (Path.GetExtension(path).ToUpperInvariant())
        {
            case ".SLDASM": return CadDocumentKind.Assembly;
            case ".SLDPRT": return CadDocumentKind.Part;
            case ".SLDDRW": return CadDocumentKind.Drawing;
            default: return CadDocumentKind.Other;
        }
    }

    private void RefreshTree(bool showErrors)
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

            LogDiagnostic("RefreshTree", exception);
            if (showErrors)
            {
                ShowError(exception.Message);
            }
        }
    }

    private async Task RefreshMetadataAsync(Guid projectId)
    {
        try
        {
            var documents = await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
            if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
            {
                return;
            }

            var byFileName = documents
                .GroupBy(document => document.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ApplyMetadata(currentTree, byFileName);
            await ApplyWorkingStatesAsync(currentTree);
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.SetConnectionState(true, "服务正常");
        }
        catch (Exception exception)
        {
            if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
            {
                return;
            }

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

    private async Task ApplyWorkingStatesAsync(CadTreeNode root)
    {
        var nodes = EnumerateCadNodes(root).ToArray();
        var latestHashes = new Dictionary<Guid, string>();
        foreach (var documentId in nodes
            .Where(IsCheckedOutByCurrentUser)
            .Select(node => node.DocumentId)
            .Where(id => id.HasValue)
            .Select(id => id.Value)
            .Distinct())
        {
            try
            {
                var latest = (await apiClient.GetVersionsAsync(documentId, lifetime.Token)).FirstOrDefault();
                latestHashes[documentId] = latest?.Sha256 ?? string.Empty;
            }
            catch (Exception exception)
            {
                LogDiagnostic("ApplyWorkingStates.GetVersions", exception);
                latestHashes[documentId] = string.Empty;
            }
        }

        var pathsToHash = nodes
            .Where(IsCheckedOutByCurrentUser)
            .Where(node => !node.IsModifiedInSolidWorks
                && node.DocumentId.HasValue
                && latestHashes.TryGetValue(node.DocumentId.Value, out var sha256)
                && !string.IsNullOrWhiteSpace(sha256)
                && !string.IsNullOrWhiteSpace(node.FullPath)
                && File.Exists(node.FullPath))
            .Select(node => node.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var localHashes = await Task.Run(() => ComputeFileHashes(pathsToHash));

        foreach (var node in nodes)
        {
            node.LatestVersionSha256 = node.DocumentId.HasValue && latestHashes.TryGetValue(node.DocumentId.Value, out var latestSha256)
                ? latestSha256
                : string.Empty;
            node.WorkState = DetermineWorkState(node, localHashes);
        }
    }

    private bool IsCheckedOutByCurrentUser(CadTreeNode node) =>
        !string.IsNullOrWhiteSpace(authenticatedUsername)
        && !string.IsNullOrWhiteSpace(node?.CheckedOutBy)
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);

    private CadWorkState DetermineWorkState(CadTreeNode node, IReadOnlyDictionary<string, string> localHashes)
    {
        if (string.IsNullOrWhiteSpace(node.CheckedOutBy))
        {
            return CadWorkState.None;
        }

        if (!IsCheckedOutByCurrentUser(node))
        {
            return CadWorkState.EditingByOther;
        }

        if (node.IsModifiedInSolidWorks)
        {
            return CadWorkState.ModifiedUnsaved;
        }

        if (!string.IsNullOrWhiteSpace(node.LatestVersionSha256)
            && !string.IsNullOrWhiteSpace(node.FullPath)
            && localHashes.TryGetValue(node.FullPath, out var localSha256)
            && !string.Equals(localSha256, node.LatestVersionSha256, StringComparison.OrdinalIgnoreCase))
        {
            return CadWorkState.PendingCheckIn;
        }

        return CadWorkState.Editable;
    }

    private static IReadOnlyDictionary<string, string> ComputeFileHashes(IEnumerable<string> paths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                hashes[path] = ComputeFileHash(path);
            }
            catch
            {
                // A file being saved is skipped and will be re-evaluated on the next refresh.
            }
        }

        return hashes;
    }

    private static string ComputeFileHash(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
    }

    private static IEnumerable<CadTreeNode> EnumerateCadNodes(CadTreeNode node)
    {
        if (node == null)
        {
            yield break;
        }

        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateCadNodes(child))
            {
                yield return descendant;
            }
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

    private void ShowError(string message)
    {
        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
        {
            return;
        }

        try
        {
            MessageBox.Show(taskPaneControl, message, "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            LogDiagnostic("ShowError", exception);
        }
    }

    private static void LogDiagnostic(string operation, Exception exception)
    {
        try
        {
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            var line = string.Concat(DateTime.Now.ToString("O"), " ", operation, " ", exception, System.Environment.NewLine);
            File.AppendAllText(Path.Combine(directory, "addin-errors.log"), line);
        }
        catch
        {
            // Diagnostics must never escape into SolidWorks.
        }
    }

    private static void LogOperation(string message)
    {
        try
        {
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "addin-operations.log"),
                string.Concat(DateTime.Now.ToString("O"), " ", message, System.Environment.NewLine));
        }
        catch
        {
            // Operation markers must never escape into SolidWorks.
        }
    }
}
