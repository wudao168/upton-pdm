using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
using Upton.Pdm.LocalSettings;

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
    private ControlledWorkspaceManager controlledWorkspace;
    private SolidWorksOpenRequestListener controlledOpenListener;
    private SolidWorksOpenRequest pendingControlledOpenRequest;
    private SolidWorksReferenceTreeScanner scanner;
    private CancellationTokenSource lifetime;
    private CadTreeNode currentTree;
    private Guid? currentProjectId;
    private IReadOnlyList<ProjectDto> availableProjects = Array.Empty<ProjectDto>();
    private readonly Dictionary<string, ControlledOpenManifestDto> controlledOpenManifests = new Dictionary<string, ControlledOpenManifestDto>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> explicitProjectPaths = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssemblyItemRename> pendingAssemblyItemRenames = new List<AssemblyItemRename>();
    private string currentDocumentIdentity = string.Empty;
    private int projectResolutionGeneration;
    private DSldWorksEvents_Event applicationEvents;
    private DAssemblyDocEvents_Event assemblyEvents;
    private string assemblyEventDocumentPath = string.Empty;
    private DPartDocEvents_Event partEvents;
    private string partEventDocumentPath = string.Empty;
    private string authenticatedUsername = string.Empty;
    private int openOperationInProgress;
    private int checkInOperationInProgress;
    private int workspaceOperationInProgress;
    private BatchProgressDialog activeBatchProgressDialog;
    private int treeRefreshInProgress;
    private int controlledOpenInProgress;
    private int refreshSuppressionDepth;
    private int pendingTreeRefresh;
    private int checkoutHeartbeatInProgress;
    private int automaticDrawingOperationInProgress;
    private readonly Guid checkoutSessionId = Guid.NewGuid();
    private readonly string checkoutMachineName = System.Environment.MachineName;
    private readonly object checkoutDocumentSync = new object();
    private readonly HashSet<Guid> activeCheckoutDocumentIds = new HashSet<Guid>();
    private readonly Dictionary<Guid, int> checkoutReminderLevels = new Dictionary<Guid, int>();
    private DateTime nextCheckoutHeartbeatUtc = DateTime.MinValue;
    private int checkoutHeartbeatSeconds = 180;
    private int checkoutReminderHours = 4;
    private int checkoutStrongReminderHours = 8;
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
            controlledWorkspace = new ControlledWorkspaceManager(apiClient);
            controlledOpenListener = new SolidWorksOpenRequestListener();
            controlledOpenListener.Start();
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
            LogOperation("ConnectToSW success");
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
            controlledOpenListener?.Dispose();
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
            controlledWorkspace = null;
            controlledOpenListener = null;
            pendingControlledOpenRequest = null;
            scanner = null;
            lifetime = null;
            application = null;
            authenticatedUsername = string.Empty;
            availableProjects = Array.Empty<ProjectDto>();
            currentDocumentIdentity = string.Empty;
            currentProjectId = null;
            controlledOpenManifests.Clear();
            explicitProjectPaths.Clear();
            pendingAssemblyItemRenames.Clear();
            lock (checkoutDocumentSync) activeCheckoutDocumentIds.Clear();
            checkoutReminderLevels.Clear();
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
        taskPaneControl.OpenClientRequested += OnOpenClientRequested;
        taskPaneControl.RefreshRequested += OnRefreshRequested;
        taskPaneControl.NodeSelected += OnNodeSelected;
        taskPaneControl.OpenRequested += OnOpenRequested;
        taskPaneControl.OpenWorkingFileRequested += OnOpenWorkingFileRequested;
        taskPaneControl.UpdateLatestRequested += OnUpdateLatestRequested;
        taskPaneControl.CheckoutRequested += OnCheckoutRequested;
        taskPaneControl.CheckInRequested += OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested += OnDiscardCheckoutRequested;
        taskPaneControl.BatchOperationRequested += OnBatchOperationRequested;
        taskPaneControl.BatchPropertyEditRequested += OnBatchPropertyEditRequested;
        taskPaneControl.AutomaticDrawingGenerateRequested += OnAutomaticDrawingGenerateRequested;
        taskPaneControl.AutomaticDrawingOpenRequested += OnAutomaticDrawingOpenRequested;
        taskPaneControl.AutomaticDrawingImportAnnotationsRequested += OnAutomaticDrawingImportAnnotationsRequested;
        taskPaneControl.AutomaticDrawingSubmitRequested += OnAutomaticDrawingSubmitRequested;
        taskPaneControl.VersionsRequested += OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested += OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested += OnCompareVersionsRequested;
        taskPaneControl.ControlledOpenRequested += OnControlledOpenRequested;
        taskPaneControl.ProjectBrowseRequested += OnProjectBrowseRequested;
        taskPaneControl.WhereUsedRequested += OnWhereUsedRequested;
        taskPaneControl.RequestReleaseRequested += OnRequestReleaseRequested;
        taskPaneControl.WithdrawApprovalRequested += OnWithdrawApprovalRequested;
        taskPaneControl.ObsoleteRequested += OnObsoleteRequested;
        taskPaneControl.ZoomSelectionRequested += OnZoomSelectionRequested;
        taskPaneControl.IsolateRequested += OnIsolateRequested;
        taskPaneControl.ExitIsolateRequested += OnExitIsolateRequested;
        taskPaneControl.OpenContainingFolderRequested += OnOpenContainingFolderRequested;
        taskPaneControl.RenameDocumentRequested += OnRenameDocumentRequested;
        taskPaneControl.OpenReleaseCenterRequested += OnOpenReleaseCenterRequested;
    }

    private void UnwireEvents()
    {
        if (taskPaneControl == null)
        {
            return;
        }

        taskPaneControl.LoginRequested -= OnLoginRequested;
        taskPaneControl.OpenClientRequested -= OnOpenClientRequested;
        taskPaneControl.RefreshRequested -= OnRefreshRequested;
        taskPaneControl.NodeSelected -= OnNodeSelected;
        taskPaneControl.OpenRequested -= OnOpenRequested;
        taskPaneControl.OpenWorkingFileRequested -= OnOpenWorkingFileRequested;
        taskPaneControl.UpdateLatestRequested -= OnUpdateLatestRequested;
        taskPaneControl.CheckoutRequested -= OnCheckoutRequested;
        taskPaneControl.CheckInRequested -= OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested -= OnDiscardCheckoutRequested;
        taskPaneControl.BatchOperationRequested -= OnBatchOperationRequested;
        taskPaneControl.BatchPropertyEditRequested -= OnBatchPropertyEditRequested;
        taskPaneControl.AutomaticDrawingGenerateRequested -= OnAutomaticDrawingGenerateRequested;
        taskPaneControl.AutomaticDrawingOpenRequested -= OnAutomaticDrawingOpenRequested;
        taskPaneControl.AutomaticDrawingImportAnnotationsRequested -= OnAutomaticDrawingImportAnnotationsRequested;
        taskPaneControl.AutomaticDrawingSubmitRequested -= OnAutomaticDrawingSubmitRequested;
        taskPaneControl.VersionsRequested -= OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested -= OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested -= OnCompareVersionsRequested;
        taskPaneControl.ControlledOpenRequested -= OnControlledOpenRequested;
        taskPaneControl.ProjectBrowseRequested -= OnProjectBrowseRequested;
        taskPaneControl.WhereUsedRequested -= OnWhereUsedRequested;
        taskPaneControl.RequestReleaseRequested -= OnRequestReleaseRequested;
        taskPaneControl.WithdrawApprovalRequested -= OnWithdrawApprovalRequested;
        taskPaneControl.ObsoleteRequested -= OnObsoleteRequested;
        taskPaneControl.ZoomSelectionRequested -= OnZoomSelectionRequested;
        taskPaneControl.IsolateRequested -= OnIsolateRequested;
        taskPaneControl.ExitIsolateRequested -= OnExitIsolateRequested;
        taskPaneControl.OpenContainingFolderRequested -= OnOpenContainingFolderRequested;
        taskPaneControl.RenameDocumentRequested -= OnRenameDocumentRequested;
        taskPaneControl.OpenReleaseCenterRequested -= OnOpenReleaseCenterRequested;
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
                    partEvents.UserSelectionPostNotify += OnSolidWorksSelectionChanged;
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

            assemblyEvents.UserSelectionPostNotify += OnSolidWorksSelectionChanged;
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
            assemblyEvents.UserSelectionPostNotify -= OnSolidWorksSelectionChanged;
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
            partEvents.UserSelectionPostNotify -= OnSolidWorksSelectionChanged;
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
        if (IsCurrentDocument(fileName))
        {
            ClearActiveDocumentContext();
        }

        ScheduleTreeRefresh();
        RequestImmediateTreeRefresh();
        return 0;
    }

    private bool IsCurrentDocument(string fileName)
    {
        if (currentTree == null || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (PathsEqual(fileName, currentTree.FullPath)
            || string.Equals(fileName, currentDocumentIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var closedName = Path.GetFileName(fileName);
        var currentName = string.IsNullOrWhiteSpace(currentTree.FileName)
            ? Path.GetFileName(currentTree.FullPath)
            : currentTree.FileName;
        return string.Equals(closedName, currentName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.GetFileNameWithoutExtension(closedName),
                Path.GetFileNameWithoutExtension(currentName),
                StringComparison.OrdinalIgnoreCase);
    }

    private int OnAssemblyTreeChanged()
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnSolidWorksSelectionChanged()
    {
        try
        {
            var model = application?.ActiveDoc as IModelDoc2;
            var selectionManager = model?.SelectionManager as ISelectionMgr;
            if (selectionManager == null || selectionManager.GetSelectedObjectCount2(-1) == 0)
            {
                return 0;
            }

            var selectedCount = selectionManager.GetSelectedObjectCount2(-1);
            for (var index = selectedCount; index >= 1; index--)
            {
                var component = selectionManager.GetSelectedObjectsComponent4(index, -1) as IComponent2
                    ?? selectionManager.GetSelectedObject6(index, -1) as IComponent2;
                var componentName = component?.Name2;
                if (!string.IsNullOrWhiteSpace(componentName))
                {
                    taskPaneControl?.SelectByComponentName(componentName);
                    return 0;
                }
            }

            taskPaneControl?.SelectRootNode();
        }
        catch (Exception exception)
        {
            // Selection synchronization must never interrupt SolidWorks interaction.
            LogDiagnostic("OnSolidWorksSelectionChanged", exception);
        }

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
        if (!string.IsNullOrWhiteSpace(oldName) && !string.IsNullOrWhiteSpace(newName))
        {
            pendingAssemblyItemRenames.Add(new AssemblyItemRename(oldName, newName));
            LogOperation(string.Concat("Assembly item renamed old=", oldName, " new=", newName));
        }
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

    private void RequestImmediateTreeRefresh()
    {
        var control = taskPaneControl;
        if (disconnecting || control == null || control.IsDisposed || !control.IsHandleCreated)
        {
            return;
        }

        try
        {
            control.BeginInvoke((Action)(() => TryRefreshPendingTree("FileCloseRefresh")));
        }
        catch (ObjectDisposedException)
        {
            // SolidWorks is closing the task pane together with the document.
        }
        catch (InvalidOperationException)
        {
            // The task pane handle was destroyed before the queued refresh ran.
        }
    }

    private int OnSolidWorksIdle()
    {
        TryStartControlledOpenRequest();
        TryStartCheckoutHeartbeat();
        TryRefreshPendingTree("IdleRefresh");
        return 0;
    }

    private void TryRefreshPendingTree(string source)
    {
        if (disconnecting
            || Volatile.Read(ref refreshSuppressionDepth) > 0
            || Volatile.Read(ref openOperationInProgress) > 0
            || Volatile.Read(ref pendingTreeRefresh) == 0
            || taskPaneControl == null
            || taskPaneControl.IsDisposed
            || !taskPaneControl.IsHandleCreated
            || Interlocked.Exchange(ref treeRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref pendingTreeRefresh, 0);
            LogOperation(string.Concat(source, " start"));
            BindAssemblyEvents();
            RefreshTree(false);
            LogOperation(string.Concat(source, " end"));
        }
        catch (Exception exception)
        {
            LogDiagnostic(source, exception);
        }
        finally
        {
            Interlocked.Exchange(ref treeRefreshInProgress, 0);
        }
    }

    private void TryStartCheckoutHeartbeat()
    {
        if (disconnecting || apiClient == null || !apiClient.IsAuthenticated || lifetime == null
            || DateTime.UtcNow < nextCheckoutHeartbeatUtc || Interlocked.Exchange(ref checkoutHeartbeatInProgress, 1) != 0) return;
        Guid[] documentIds;
        lock (checkoutDocumentSync) documentIds = activeCheckoutDocumentIds.ToArray();
        if (documentIds.Length == 0)
        {
            Interlocked.Exchange(ref checkoutHeartbeatInProgress, 0);
            nextCheckoutHeartbeatUtc = DateTime.UtcNow.AddSeconds(checkoutHeartbeatSeconds);
            return;
        }
        nextCheckoutHeartbeatUtc = DateTime.UtcNow.AddSeconds(checkoutHeartbeatSeconds);
        _ = HeartbeatCheckoutSessionAsync(documentIds);
    }

    private async Task HeartbeatCheckoutSessionAsync(IReadOnlyList<Guid> documentIds)
    {
        try
        {
            var response = await apiClient.HeartbeatEditSessionAsync(checkoutSessionId, checkoutMachineName, documentIds, lifetime.Token);
            if (response.Settings != null)
            {
                checkoutHeartbeatSeconds = Math.Max(30, response.Settings.CheckoutHeartbeatSeconds);
                checkoutReminderHours = Math.Max(1, response.Settings.CheckoutReminderHours);
                checkoutStrongReminderHours = Math.Max(checkoutReminderHours + 1, response.Settings.CheckoutStrongReminderHours);
            }
            var lost = new HashSet<Guid>(response.LostDocumentIds ?? new List<Guid>());
            if (lost.Count > 0)
            {
                lock (checkoutDocumentSync)
                    foreach (var documentId in lost) activeCheckoutDocumentIds.Remove(documentId);
            }
            foreach (var node in EnumerateTree(currentTree).Where(node => node.DocumentId.HasValue && documentIds.Contains(node.DocumentId.Value)))
            {
                if (lost.Contains(node.DocumentId.Value))
                {
                    node.CheckoutSessionLost = true;
                    node.CheckoutSessionId = null;
                    node.WorkState = CadWorkState.EditingByOther;
                    SetFileReadOnly(node.FullPath, true);
                    continue;
                }
                node.CheckoutLastHeartbeatAt = response.ServerTime;
                UpdateCheckoutReminder(node, response.ServerTime);
            }
            if (lost.Count > 0)
                taskPaneControl.SetCheckoutReminder("编辑权限已被释放或转移。本地修改不能直接提交，请另存文件或重新获取权限。", true);
            taskPaneControl.SetTree(currentTree);
        }
        catch (Exception exception)
        {
            LogDiagnostic("Checkout heartbeat", exception);
        }
        finally
        {
            Interlocked.Exchange(ref checkoutHeartbeatInProgress, 0);
        }
    }

    private void UpdateCheckoutReminder(CadTreeNode node, DateTime serverTime)
    {
        if (!node.CheckedOutAt.HasValue || !node.DocumentId.HasValue) return;
        var hours = (serverTime.ToUniversalTime() - node.CheckedOutAt.Value.ToUniversalTime()).TotalHours;
        var level = hours >= checkoutStrongReminderHours ? 2 : hours >= checkoutReminderHours ? 1 : 0;
        checkoutReminderLevels.TryGetValue(node.DocumentId.Value, out var previousLevel);
        if (level == 0 || previousLevel >= level) return;
        checkoutReminderLevels[node.DocumentId.Value] = level;
        taskPaneControl.SetCheckoutReminder(
            level == 2
                ? string.Concat(node.FileName, "已连续编辑", Math.Floor(hours), "小时，请尽快提交存档或结束编辑。")
                : string.Concat(node.FileName, "已编辑", Math.Floor(hours), "小时，请及时提交存档。"),
            level == 2);
    }

    private static IEnumerable<CadTreeNode> EnumerateTree(CadTreeNode root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in EnumerateTree(child)) yield return descendant;
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

    private void OnOpenClientRequested(object sender, EventArgs eventArgs)
    {
        try
        {
            StartDesktopClient(string.Empty);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
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
        availableProjects = projects;
        taskPaneControl.SetProjects(projects);
        if (currentTree != null && application?.ActiveDoc != null)
        {
            await ResolveProjectForCurrentDocumentAsync(currentTree, DocumentIdentity(currentTree));
        }
        TryStartControlledOpenRequest();
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

    private async void OnProjectBrowseRequested(object sender, ProjectBrowseEventArgs eventArgs)
    {
        if (!apiClient.IsAuthenticated)
        {
            return;
        }

        await LoadProjectTreeAsync(eventArgs.ProjectId);
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
            if (model != null)
            {
                TrySelectComponentForNode(model, eventArgs.Node, out _);
            }
        }
        catch
        {
            // Selection synchronization must not break the task pane.
        }
    }

    private bool TrySelectComponentForNode(IModelDoc2 assemblyModel, CadTreeNode node, out IComponent2 selectedComponent)
    {
        selectedComponent = null;
        if (assemblyModel == null || node == null || assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            return false;
        }

        var assembly = assemblyModel as IAssemblyDoc;
        var components = (assembly?.GetComponents(false) as object[] ?? Array.Empty<object>())
            .OfType<IComponent2>()
            .ToArray();
        selectedComponent = components.FirstOrDefault(component =>
            ComponentNameMatches(component.Name2, node.ComponentSelectionName));
        if (selectedComponent == null && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            selectedComponent = components.FirstOrDefault(component =>
            {
                try
                {
                    return PathsEqual(component.GetPathName(), node.FullPath);
                }
                catch
                {
                    return false;
                }
            });
        }
        if (selectedComponent == null)
        {
            LogOperation(string.Concat(
                "Component select lookup failed name=", node.ComponentSelectionName,
                " path=", node.FullPath,
                " candidates=", components.Length));
            return false;
        }

        assemblyModel.ClearSelection2(true);
        var selectionManager = assemblyModel.SelectionManager as ISelectionMgr;
        var selectData = selectionManager?.CreateSelectData();
        var selected = selectedComponent.Select4(false, selectData, false);
        if (!selected)
        {
            LogOperation(string.Concat(
                "Component Select4 failed name=", selectedComponent.Name2,
                " path=", selectedComponent.GetPathName()));
        }
        return selected;
    }

    private async void OnWhereUsedRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!eventArgs.Node.DocumentId.HasValue) return;
        try
        {
            var usages = await apiClient.GetWhereUsedAsync(eventArgs.Node.DocumentId.Value, lifetime.Token);
            using (var dialog = new WhereUsedDialog(eventArgs.Node.FileName, usages)) dialog.ShowDialog(taskPaneControl);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void OnRequestReleaseRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!eventArgs.Node.DocumentId.HasValue) return;
        using (var dialog = new LifecycleActionDialog("申请释放编辑权限", string.Concat("当前编辑人员：", eventArgs.Node.CheckedOutBy, "。\r\n请填写申请原因，对方可在客户端待办中处理。"), "发送申请"))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK) return;
            try
            {
                await apiClient.RequestCheckoutReleaseAsync(eventArgs.Node.DocumentId.Value, dialog.Comment, lifetime.Token);
                MessageBox.Show(taskPaneControl, "释放申请已发送。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
    }

    private async void OnWithdrawApprovalRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!currentProjectId.HasValue) return;
        using (var dialog = new LifecycleActionDialog("撤回当前审批", "撤回后发布包回到草稿，相关图档恢复为工作中。请填写撤回原因。", "确认撤回"))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK) return;
            try
            {
                var packages = await apiClient.GetReleasePackagesAsync(currentProjectId.Value, lifetime.Token);
                var active = packages.FirstOrDefault(package => package.State == 1 || package.State == 2)
                    ?? throw new InvalidOperationException("当前项目没有审批中的发布包。");
                await apiClient.WithdrawReleasePackageAsync(active.Id, dialog.Comment, lifetime.Token);
                RefreshTree(true);
                MessageBox.Show(taskPaneControl, "审批已撤回，图档已恢复为工作中。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
    }

    private async void OnObsoleteRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (!eventArgs.Node.DocumentId.HasValue) return;
        using (var dialog = new LifecycleActionDialog("作废图档", "作废后图档不能再获取编辑权限。请填写可追溯的作废原因。", "确认作废"))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK) return;
            try
            {
                var document = await apiClient.ObsoleteAsync(eventArgs.Node.DocumentId.Value, dialog.Comment, lifetime.Token);
                ApplyCheckoutDocument(eventArgs.Node, document);
                taskPaneControl.SetTree(currentTree);
                MessageBox.Show(taskPaneControl, "图档已受控作废。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }
    }

    private void OnZoomSelectionRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        try
        {
            OnNodeSelected(sender, eventArgs);
            (application.ActiveDoc as IModelDoc2)?.ViewZoomToSelection();
        }
        catch (Exception exception) { ShowError(string.Concat("放大所选范围失败：", exception.Message)); }
    }

    private void OnIsolateRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        try
        {
            OnNodeSelected(sender, eventArgs);
            var assembly = application.ActiveDoc as IAssemblyDoc ?? throw new InvalidOperationException("只有装配体可以隔离零部件。");
            assembly.Isolate();
        }
        catch (Exception exception) { ShowError(string.Concat("隔离显示失败：", exception.Message)); }
    }

    private void OnExitIsolateRequested(object sender, EventArgs eventArgs)
    {
        try
        {
            var assembly = application.ActiveDoc as IAssemblyDoc ?? throw new InvalidOperationException("当前文档不是装配体。");
            assembly.ExitIsolate();
        }
        catch (Exception exception) { ShowError(string.Concat("退出隔离失败：", exception.Message)); }
    }

    private void OnOpenContainingFolderRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Node.FullPath) || !File.Exists(eventArgs.Node.FullPath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = string.Concat("/select,\"", eventArgs.Node.FullPath, "\""),
            UseShellExecute = true
        });
    }

    private void OnRenameDocumentRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        try
        {
            var node = eventArgs?.Node ?? throw new InvalidOperationException("请先选择需要重命名的图档。");
            if (node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly)
            {
                throw new InvalidOperationException("只能重命名零件或装配体。");
            }
            if (!node.DocumentId.HasValue)
            {
                throw new InvalidOperationException("该图档尚未入库，不能执行受控重命名。");
            }
            if (node.IsHistoricalPreview)
            {
                throw new InvalidOperationException("历史版本不能重命名。");
            }
            if (!IsCheckedOutByCurrentUser(node))
            {
                throw new InvalidOperationException("请先获取该图档的编辑权限。");
            }
            if (currentTree == null || !IsCheckedOutByCurrentUser(currentTree))
            {
                throw new InvalidOperationException("重命名会修改装配引用，请先获取当前装配体的编辑权限。");
            }
            var renamingRoot = ReferenceEquals(node, currentTree) || PathsEqual(node.FullPath, currentTree.FullPath);
            if (!renamingRoot && string.IsNullOrWhiteSpace(node.ComponentSelectionName))
            {
                throw new InvalidOperationException("SolidWorks未识别到该零部件实例，请刷新结构树后重试。");
            }

            var assemblyModel = application?.ActiveDoc as IModelDoc2
                ?? throw new InvalidOperationException("请先打开包含该图档的装配体。");
            if (!PathsEqual(assemblyModel.GetPathName(), currentTree.FullPath)
                || !renamingRoot && assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("请先激活当前结构树对应的装配体。");
            }

            var extension = Path.GetExtension(node.FileName);
            var currentBaseName = Path.GetFileNameWithoutExtension(node.FileName);
            using (var dialog = new RenameDocumentDialog(currentBaseName, extension))
            {
                if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
                {
                    return;
                }

                var newBaseName = NormalizeRenamedDocumentName(dialog.NewName, extension);
                if (string.Equals(currentBaseName, newBaseName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var oldPath = Path.GetFullPath(node.FullPath);
                var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? string.Empty, string.Concat(newBaseName, extension));
                if (File.Exists(newPath) && !PathsEqual(oldPath, newPath))
                {
                    throw new IOException(string.Concat("目标文件已存在：", Path.GetFileName(newPath)));
                }

                if (renamingRoot)
                {
                    assemblyModel.ClearSelection2(true);
                    var saveErrors = 0;
                    var saveWarnings = 0;
                    var saved = assemblyModel.Extension.SaveAs3(
                        newPath,
                        (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        null,
                        null,
                        ref saveErrors,
                        ref saveWarnings);
                    if (!saved || saveErrors != 0 || !File.Exists(newPath) || !PathsEqual(assemblyModel.GetPathName(), newPath))
                    {
                        throw new IOException(string.Concat(
                            "SolidWorks保存主装配体新名称失败，错误码：",
                            saveErrors,
                            "，警告码：",
                            saveWarnings,
                            "。"));
                    }

                    node.FullPath = newPath;
                    node.FileName = Path.GetFileName(newPath);
                    node.DrawingNumber = newBaseName;
                    node.IsModifiedInSolidWorks = false;
                    node.IsRenamePendingSave = false;
                    node.Status = CadReferenceStatus.Normal;
                    node.CurrentRevision = "本地修改";
                    node.WorkState = CadWorkState.PendingCheckIn;
                    PdmDocumentIdentityStore.TryWrite(newPath, node.DocumentId.Value);
                    if (currentProjectId.HasValue)
                    {
                        RememberExplicitProjectPath(newPath, currentProjectId.Value);
                    }
                    currentDocumentIdentity = newPath;
                    taskPaneControl.SetTree(currentTree);
                    ScheduleTreeRefresh();
                    LogOperation(string.Concat("Root document rename saved old=", oldPath, " new=", newPath, " document=", node.DocumentId.Value));
                    return;
                }

                if (!TrySelectComponentForNode(assemblyModel, node, out _))
                {
                    throw new InvalidOperationException("SolidWorks无法选中该零部件，请刷新结构树后重试。");
                }

                var renameStatus = (swRenameDocumentError_e)assemblyModel.Extension.RenameDocument(newBaseName);
                assemblyModel.ClearSelection2(true);
                if (renameStatus != swRenameDocumentError_e.swRenameDocumentError_None)
                {
                    throw new InvalidOperationException(RenameDocumentErrorText(renameStatus));
                }

                foreach (var matchingNode in EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, oldPath)))
                {
                    matchingNode.FullPath = newPath;
                    matchingNode.FileName = Path.GetFileName(newPath);
                    matchingNode.DrawingNumber = newBaseName;
                    matchingNode.IsModifiedInSolidWorks = true;
                    matchingNode.IsRenamePendingSave = true;
                    matchingNode.Status = CadReferenceStatus.Normal;
                    matchingNode.WorkState = CadWorkState.ModifiedUnsaved;
                }
                currentTree.IsModifiedInSolidWorks = true;
                currentTree.WorkState = CadWorkState.ModifiedUnsaved;
                if (currentProjectId.HasValue)
                {
                    RememberExplicitProjectPath(newPath, currentProjectId.Value);
                }

                taskPaneControl.SetTree(currentTree);
                ScheduleTreeRefresh();
                LogOperation(string.Concat("Document rename staged old=", oldPath, " new=", newPath, " document=", node.DocumentId.Value));
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("Rename document", exception);
            ShowError(string.Concat("重命名失败：", exception.Message));
        }
    }

    private static string NormalizeRenamedDocumentName(string requestedName, string extension)
    {
        var name = (requestedName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(extension) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - extension.Length).Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("新文件名不能为空。");
        }
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("新文件名包含Windows不允许的字符，或以句点、空格结尾。");
        }
        if (name.Length + (extension?.Length ?? 0) > 240)
        {
            throw new InvalidOperationException("新文件名过长。");
        }

        var deviceName = name.Split('.')[0].ToUpperInvariant();
        var reserved = deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX" || deviceName == "NUL"
            || (deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.Ordinal) || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                && deviceName[3] >= '1' && deviceName[3] <= '9');
        if (reserved)
        {
            throw new InvalidOperationException("该名称是Windows保留名称，请使用其他名称。");
        }
        return name;
    }

    private static string RenameDocumentErrorText(swRenameDocumentError_e error)
    {
        switch (error)
        {
            case swRenameDocumentError_e.swRenameDocumentError_InvalidSelection:
                return "SolidWorks未识别到可重命名的零部件。";
            case swRenameDocumentError_e.swRenameDocumentError_ComponentNotResolved:
            case swRenameDocumentError_e.swRenameDocumentError_LightWeightComponent:
                return "零部件尚未完全解析，请先将其设为还原状态后重试。";
            case swRenameDocumentError_e.swRenameDocumentError_FileAlreadyExists:
            case swRenameDocumentError_e.swRenameDocumentError_DocumentNameInUse:
            case swRenameDocumentError_e.swRenameDocumentError_PendingNameAlreadyInUse:
                return "目标名称已被现有文件或已打开图档占用。";
            case swRenameDocumentError_e.swRenameDocumentError_InvalidCharactersInName:
                return "新文件名包含SolidWorks不允许的字符。";
            case swRenameDocumentError_e.swRenameDocumentError_NameTooLong:
                return "新文件名过长。";
            case swRenameDocumentError_e.swRenameDocumentError_ReadOnlyDocument:
                return "图档仍为只读状态，请重新获取编辑权限后重试。";
            case swRenameDocumentError_e.swRenameDocumentError_DocumentNotSaved:
                return "该图档尚未保存，不能重命名。";
            case swRenameDocumentError_e.swRenameDocumentError_RoutingComponent:
            case swRenameDocumentError_e.swRenameDocumentError_ToolboxComponent:
            case swRenameDocumentError_e.swRenameDocumentError_PatternedComponent:
            case swRenameDocumentError_e.swRenameDocumentError_VirtualComponent:
            case swRenameDocumentError_e.swRenameDocumentError_InvalidVirtualComponent:
                return "该零部件类型不支持直接重命名。";
            case swRenameDocumentError_e.swRenameDocumentError_NotAllowedWithPDM:
                return "SolidWorks拒绝了本次PDM环境下的重命名操作。";
            default:
                return string.Concat("SolidWorks重命名失败，错误代码：", (int)error, "。");
        }
    }

    private void OnOpenReleaseCenterRequested(object sender, EventArgs eventArgs)
    {
        try { StartDesktopClient(string.Empty); }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void OnAutomaticDrawingGenerateRequested(object sender, AutomaticDrawingRequestEventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref automaticDrawingOperationInProgress, 1) != 0)
        {
            ShowError("已有自动出图操作正在进行，请稍候再试。");
            return;
        }

        try
        {
            var source = eventArgs.Source;
            ValidateAutomaticDrawingSource(source);
            var drawingPath = AutomaticDrawingControl.GetDrawingPath(source);
            var existed = File.Exists(drawingPath);
            var projectId = currentProjectId ?? GetExplicitProjectId(source.FullPath);
            if (existed)
            {
                UpdateAutomaticDrawing(source, drawingPath, eventArgs.Options);
            }
            else
            {
                CreateAutomaticDrawing(source, drawingPath, eventArgs.Options);
            }

            if (projectId.HasValue)
            {
                RememberExplicitProjectPath(drawingPath, projectId.Value);
            }

            RefreshTree(true);
            if (projectId.HasValue)
            {
                currentProjectId = projectId;
                taskPaneControl.SelectProject(projectId);
            }
            taskPaneControl.SetGeneratedDrawing(source, drawingPath);
            taskPaneControl.SetAutomaticDrawingOperationResult(
                existed
                    ? "工程图已更新，可继续手工编辑或重新整理自动标注。"
                    : "工程图已生成，可继续手工编辑并执行自动标注。");
            LogOperation(string.Concat(existed ? "Automatic drawing updated path=" : "Automatic drawing created path=", drawingPath));
        }
        catch (Exception exception)
        {
            LogDiagnostic("Automatic drawing generation", exception);
            ShowError(exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref automaticDrawingOperationInProgress, 0);
        }
    }

    private void OnAutomaticDrawingOpenRequested(object sender, AutomaticDrawingRequestEventArgs eventArgs)
    {
        try
        {
            var drawingPath = AutomaticDrawingControl.GetDrawingPath(eventArgs.Source);
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
            {
                throw new FileNotFoundException("尚未生成关联工程图。");
            }

            var drawingNode = FindCadNodeByPath(currentTree, drawingPath);
            if (drawingNode?.DocumentId.HasValue == true && !IsCheckedOutByCurrentUser(drawingNode))
            {
                BeginControlledOpen(drawingNode, ControlledOpenMode.LatestReadOnly);
                return;
            }

            var document = OpenOrActivateDocumentOnSolidWorksThread(
                drawingPath,
                (int)swDocumentTypes_e.swDocDRAWING,
                string.Empty);
            if (document != null)
            {
                taskPaneControl.SetGeneratedDrawing(eventArgs.Source, drawingPath);
                ScheduleTreeRefresh();
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("Open automatic drawing", exception);
            ShowError(exception.Message);
        }
    }

    private void OnAutomaticDrawingImportAnnotationsRequested(object sender, AutomaticDrawingRequestEventArgs eventArgs)
    {
        if (Interlocked.Exchange(ref automaticDrawingOperationInProgress, 1) != 0)
        {
            ShowError("已有自动出图操作正在进行，请稍候再试。");
            return;
        }

        try
        {
            var drawingPath = AutomaticDrawingControl.GetDrawingPath(eventArgs.Source);
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
            {
                throw new FileNotFoundException("请先生成工程图草稿。");
            }

            EnsureDrawingCanBeChanged(drawingPath);
            var drawingModel = OpenOrActivateDocumentOnSolidWorksThread(
                drawingPath,
                (int)swDocumentTypes_e.swDocDRAWING,
                string.Empty);
            var drawing = drawingModel as IDrawingDoc;
            if (drawing == null)
            {
                throw new InvalidOperationException("当前文件不是SolidWorks工程图。");
            }

            ApplyGbDrawingStandard(drawingModel);
            var rules = AutomaticDrawingRuleStore.Load();
            var ruleDecision = ResolveAutomaticDrawingRuleDecision(eventArgs.Source, drawing, rules);

            var annotationTypes = 0;
            if (eventArgs.Options.ImportMarkedDimensions)
            {
                annotationTypes |= (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing;
                annotationTypes |= (int)swInsertAnnotation_e.swInsertTolerancedDims;
                if (rules.ImportUnmarkedModelDimensions)
                {
                    annotationTypes |= (int)swInsertAnnotation_e.swInsertDimensionsNotMarkedForDrawing;
                }
            }
            if (eventArgs.Options.ImportHoleDimensions)
            {
                annotationTypes |= (int)swInsertAnnotation_e.swInsertHoleWizardProfileDimensions;
                annotationTypes |= (int)swInsertAnnotation_e.swInsertHoleWizardLocationDimensions;
                annotationTypes |= (int)swInsertAnnotation_e.swInsertholeCallout;
            }
            if (annotationTypes == 0)
            {
                throw new InvalidOperationException("请至少选择一种需要导入的模型标注。");
            }

            var dimensionsBeforeImport = CountDrawingDimensions(drawing);
            var annotations = drawing.InsertModelAnnotations3(
                (int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel,
                annotationTypes,
                true,
                true,
                false,
                false);
            var count = annotations is Array annotationArray ? annotationArray.Length : annotations == null ? 0 : 1;
            var dimensionsAfterImport = CountDrawingDimensions(drawing);
            var fallbackDimensions = 0;
            if (dimensionsBeforeImport == 0
                && dimensionsAfterImport == 0
                && eventArgs.Source.Kind == CadDocumentKind.Part
                && rules.EnableFallbackAutoDimension)
            {
                fallbackDimensions = AutoDimensionPrimaryView(drawingModel, drawing, ruleDecision);
                count += fallbackDimensions;
            }

            var centerMarksAdded = rules.GenerateCenterMarks
                ? InsertAutomaticDrawingCenterMarks(drawingModel, drawing, rules)
                : 0;
            var centerLinesAdded = rules.GenerateSymmetryCenterlines
                ? InsertAutomaticDrawingCenterlines(drawingModel, drawing, ruleDecision)
                : 0;
            var standardizedChamfers = rules.Standardize45DegreeChamfers
                ? Standardize45DegreeChamferDimensions(drawing)
                : 0;
            var hiddenDuplicates = HideDuplicateDrawingDimensions(drawingModel, drawing);
            var arrangedViews = ArrangeDrawingDimensions(drawingModel, drawing, rules.DimensionSpacingMeters);
            drawingModel.ForceRebuild3(false);
            SaveSolidWorksDocument(drawingModel);
            var visibleDimensions = CountVisibleDrawingDimensions(drawing);
            AutomaticDrawingLearningStore.Record(
                new AutomaticDrawingLearningRecord
                {
                    RuleVersion = rules.RuleVersion,
                    SourceFileName = Path.GetFileName(eventArgs.Source.FullPath),
                    SourceKind = eventArgs.Source.Kind.ToString(),
                    PartFamily = ruleDecision.PartFamily.ToString(),
                    DatumStrategy = ruleDecision.DatumStrategy.ToString(),
                    DimensionsBefore = dimensionsBeforeImport,
                    ImportedAnnotations = count,
                    VisibleDimensionsAfter = visibleDimensions,
                    StandardizedChamfers = standardizedChamfers,
                    HiddenDuplicates = hiddenDuplicates,
                    ArrangedViews = arrangedViews,
                    UsedFallbackAutoDimension = fallbackDimensions > 0,
                    CenterMarksAdded = centerMarksAdded,
                    CenterLinesAdded = centerLinesAdded,
                    IncludedIsometricView = EnumerateDrawingModelViews(drawing).Any(IsIsometricDrawingView)
                },
                rules.MaximumLearningRecords);
            var centerAnnotationSummary = centerMarksAdded > 0 || centerLinesAdded > 0
                ? string.Concat("；新增中心标记", centerMarksAdded, "、中心线", centerLinesAdded)
                : string.Empty;
            taskPaneControl.SetGeneratedDrawing(eventArgs.Source, drawingPath);
            taskPaneControl.SetAutomaticDrawingOperationResult(
                count > 0
                    ? string.Concat(
                        "规则 ", rules.RuleVersion, " · ", ruleDecision.PartFamilyText,
                        "（", ruleDecision.DatumStrategyText, "）：已导入或补充", count,
                        "项标注，并重新整理现有尺寸", centerAnnotationSummary, "。")
                    : arrangedViews > 0
                        ? string.Concat(
                            "规则 ", rules.RuleVersion, " · ", ruleDecision.PartFamilyText,
                            "（", ruleDecision.DatumStrategyText, "）：未新增重复尺寸，已重新整理",
                            arrangedViews, "个视图的现有标注", centerAnnotationSummary, "。")
                        : string.Concat(
                            "规则 ", rules.RuleVersion, " · ", ruleDecision.PartFamilyText,
                            "（", ruleDecision.DatumStrategyText,
                            "）：现有标注无需新增，工程图已重新检查并保存",
                            centerAnnotationSummary,
                            "。"));
            LogOperation(string.Concat(
                "Automatic drawing annotations ruleVersion=",
                rules.RuleVersion,
                " partFamily=",
                ruleDecision.PartFamily,
                " datumStrategy=",
                ruleDecision.DatumStrategy,
                " importedCount=",
                count,
                " standardizedChamfers=",
                standardizedChamfers,
                " hiddenDuplicates=",
                hiddenDuplicates,
                " centerMarksAdded=",
                centerMarksAdded,
                " centerLinesAdded=",
                centerLinesAdded,
                " arrangedViews=",
                arrangedViews,
                " path=",
                drawingPath));
        }
        catch (Exception exception)
        {
            LogDiagnostic("Import automatic drawing annotations", exception);
            ShowError(exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref automaticDrawingOperationInProgress, 0);
        }
    }

    private void OnAutomaticDrawingSubmitRequested(object sender, AutomaticDrawingRequestEventArgs eventArgs)
    {
        try
        {
            var source = eventArgs.Source;
            if (source?.DocumentId.HasValue != true)
            {
                throw new InvalidOperationException("源三维模型尚未入库。请先提交三维模型，再提交关联工程图，以保证版本关联正确。");
            }

            var drawingPath = AutomaticDrawingControl.GetDrawingPath(source);
            if (string.IsNullOrWhiteSpace(drawingPath) || !File.Exists(drawingPath))
            {
                throw new FileNotFoundException("请先生成工程图草稿。");
            }

            var projectId = currentProjectId ?? GetExplicitProjectId(drawingPath) ?? GetExplicitProjectId(source.FullPath);
            if (!projectId.HasValue)
            {
                throw new InvalidOperationException("未识别工程图所属项目，请先打开源三维模型并确认当前项目。");
            }

            RememberExplicitProjectPath(drawingPath, projectId.Value);
            var drawingNode = FindCadNodeByPath(currentTree, drawingPath);
            if (drawingNode == null)
            {
                var drawingModel = OpenOrActivateDocumentOnSolidWorksThread(
                    drawingPath,
                    (int)swDocumentTypes_e.swDocDRAWING,
                    string.Empty);
                if (drawingModel == null)
                {
                    return;
                }
                RefreshTree(true);
                currentProjectId = projectId;
                taskPaneControl.SelectProject(projectId);
                drawingNode = FindCadNodeByPath(currentTree, drawingPath);
            }

            if (drawingNode == null)
            {
                drawingNode = new CadTreeNode
                {
                    FileName = Path.GetFileName(drawingPath),
                    FullPath = drawingPath,
                    InstancePath = drawingPath,
                    DisplayName = Path.GetFileNameWithoutExtension(drawingPath),
                    Kind = CadDocumentKind.Drawing,
                    Status = CadReferenceStatus.Normal
                };
            }
            drawingNode.RelatedModelDocumentId = source.DocumentId;

            var sourceInDrawingTree = FindCadNodeByPath(currentTree, source.FullPath);
            if (sourceInDrawingTree != null && !sourceInDrawingTree.DocumentId.HasValue)
            {
                CopyPdmIdentity(source, sourceInDrawingTree);
            }

            if (drawingNode.DocumentId.HasValue && !IsCheckedOutByCurrentUser(drawingNode))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(drawingNode.CheckedOutBy)
                    ? "请先在结构树中为工程图获取编辑权限，再提交存档。"
                    : string.Concat("该工程图正在由", drawingNode.CheckedOutBy, "编辑。"));
            }

            OnCheckInRequested(
                this,
                new CadTreeNodeEventArgs(drawingNode, new[] { drawingNode }, true));
        }
        catch (Exception exception)
        {
            LogDiagnostic("Submit automatic drawing", exception);
            ShowError(exception.Message);
        }
    }

    private void ValidateAutomaticDrawingSource(CadTreeNode source)
    {
        if (source == null || (source.Kind != CadDocumentKind.Part && source.Kind != CadDocumentKind.Assembly))
        {
            throw new InvalidOperationException("请选择零件或装配体后再生成工程图。");
        }
        if (source.IsHistoricalPreview || IsHistoricalPreviewPath(source.FullPath))
        {
            throw new InvalidOperationException("历史版本只能预览，不能生成或更新当前工程图。");
        }
        if (string.IsNullOrWhiteSpace(source.FullPath) || !File.Exists(source.FullPath))
        {
            throw new FileNotFoundException("本地三维模型不存在，不能生成工程图。");
        }
    }

    private void CreateAutomaticDrawing(CadTreeNode source, string drawingPath, AutomaticDrawingOptions options)
    {
        var template = ResolveDrawingTemplate(options?.TemplatePath);
        IModelDoc2 drawingModel = null;
        var saved = false;
        try
        {
            drawingModel = application.NewDocument(template, 0, 0, 0) as IModelDoc2;
            var drawing = drawingModel as IDrawingDoc;
            if (drawing == null)
            {
                throw new InvalidOperationException("SolidWorks未能创建工程图，请检查工程图模板。");
            }

            ApplyGbDrawingStandard(drawingModel);
            CreateAutomaticDrawingViews(drawingModel, drawing, source.FullPath, options ?? new AutomaticDrawingOptions());
            drawingModel.ForceRebuild3(false);
            var errors = 0;
            var warnings = 0;
            saved = drawingModel.Extension.SaveAs(
                drawingPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new IOException(string.Concat("SolidWorks保存工程图失败，错误码：", errors, "，警告码：", warnings));
            }
        }
        finally
        {
            if (!saved && drawingModel != null)
            {
                application.CloseDoc(drawingModel.GetTitle());
            }
        }
    }

    private void UpdateAutomaticDrawing(CadTreeNode source, string drawingPath, AutomaticDrawingOptions options)
    {
        EnsureDrawingCanBeChanged(drawingPath);
        var drawingModel = OpenOrActivateDocumentOnSolidWorksThread(
            drawingPath,
            (int)swDocumentTypes_e.swDocDRAWING,
            string.Empty);
        var drawing = drawingModel as IDrawingDoc;
        if (drawing == null)
        {
            throw new InvalidOperationException("关联文件不是SolidWorks工程图。");
        }

        ApplyGbDrawingStandard(drawingModel);
        var firstModelView = (drawing.GetFirstView() as IView)?.GetNextView() as IView;
        if (firstModelView == null)
        {
            CreateAutomaticDrawingViews(drawingModel, drawing, source.FullPath, options ?? new AutomaticDrawingOptions());
        }
        else if (!ReferencesSource(firstModelView.GetReferencedModelName(), source.FullPath))
        {
            throw new InvalidOperationException("现有工程图引用了其他三维模型，不能自动覆盖。请核对同名文件。");
        }
        else
        {
            ArrangeExistingAutomaticDrawingViews(
                drawingModel,
                drawing,
                source.FullPath,
                options ?? new AutomaticDrawingOptions());
        }

        drawingModel.ForceRebuild3(false);
        SaveSolidWorksDocument(drawingModel);
    }

    private static void ApplyGbDrawingStandard(IModelDoc2 drawingModel)
    {
        if (drawingModel?.Extension == null)
        {
            return;
        }

        drawingModel.Extension.SetUserPreferenceInteger(
            (int)swUserPreferenceIntegerValue_e.swDetailingDimensionStandard,
            (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified,
            (int)swDetailingStandard_e.swDetailingStandardGB);
    }

    private static int AutoDimensionPrimaryView(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        AutomaticDrawingRuleDecision ruleDecision)
    {
        var views = EnumerateDrawingModelViews(drawing).ToList();
        IView frontView;
        IView topView;
        IView sideView;
        IView isometricView;
        ResolveAutomaticDrawingViews(
            views,
            out frontView,
            out topView,
            out sideView,
            out isometricView);
        if (frontView == null)
        {
            return 0;
        }

        drawingModel.ClearSelection2(true);
        var viewName = frontView.GetName2();
        drawing.ActivateView(viewName);
        if (!drawingModel.Extension.SelectByID2(viewName, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0))
        {
            return 0;
        }

        var before = CountDrawingDimensions(drawing);
        var useOrdinate = ruleDecision?.UseOrdinateFallback == true;
        var horizontalScheme = useOrdinate
            ? (int)swAutodimScheme_e.swAutodimSchemeOrdinate
            : (int)swAutodimScheme_e.swAutodimSchemeBaseline;
        var verticalScheme = horizontalScheme;
        var horizontalPlacement = useOrdinate
            ? (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementBelow
            : (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementAbove;
        var verticalPlacement = useOrdinate
            ? (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementLeft
            : (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementRight;
        var status = drawing.AutoDimension(
            (int)swAutodimEntities_e.swAutodimEntitiesAll,
            horizontalScheme,
            horizontalPlacement,
            verticalScheme,
            verticalPlacement);
        drawingModel.ClearSelection2(true);
        if (status != (int)swAutodimStatus_e.swAutodimStatusSuccess)
        {
            return 0;
        }

        return Math.Max(0, CountDrawingDimensions(drawing) - before);
    }

    private static int CountDrawingDimensions(IDrawingDoc drawing)
    {
        return EnumerateDrawingModelViews(drawing).Sum(view => Math.Max(0, view.GetDisplayDimensionCount()));
    }

    private static int CountVisibleDrawingDimensions(IDrawingDoc drawing)
    {
        var count = 0;
        foreach (var view in EnumerateDrawingModelViews(drawing))
        {
            var dimension = view.GetFirstDisplayDimension5() as IDisplayDimension;
            while (dimension != null)
            {
                var annotation = dimension.GetAnnotation() as IAnnotation;
                if (annotation != null
                    && annotation.Visible != (int)swAnnotationVisibilityState_e.swAnnotationHidden)
                {
                    count++;
                }
                dimension = dimension.GetNext5() as IDisplayDimension;
            }
        }
        return count;
    }

    private static int InsertAutomaticDrawingCenterMarks(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        AutomaticDrawingRuleProfile rules)
    {
        const int allSupportedCircularFeatures =
            (int)swAutoInsertCenterMarkTypes_e.swAutoInsertCenterMarkType_Hole
            | (int)swAutoInsertCenterMarkTypes_e.swAutoInsertCenterMarkType_Fillets
            | (int)swAutoInsertCenterMarkTypes_e.swAutoInsertCenterMarkType_Slots;
        const int connectionLines =
            (int)swCenterMarkConnectionLine_e.swCenterMark_ShowLinearConnectLines
            | (int)swCenterMarkConnectionLine_e.swCenterMark_ShowCircularConnectLines
            | (int)swCenterMarkConnectionLine_e.swCenterMark_ShowBaseCenterMarkLines;
        var inserted = 0;
        foreach (var view in EnumerateDrawingModelViews(drawing).Where(view => !IsIsometricDrawingView(view)))
        {
            var before = Math.Max(0, view.GetCenterMarkCount());
            var name = view.GetName2();
            if (string.IsNullOrWhiteSpace(name) || !drawing.ActivateView(name))
            {
                continue;
            }
            var activeView = drawing.ActiveDrawingView as IView ?? view;
            activeView.AutoInsertCenterMarks2(
                allSupportedCircularFeatures,
                connectionLines,
                true,
                true,
                false,
                rules.CenterMarkSizeMeters,
                rules.CenterMarkGapMeters,
                true,
                true,
                0d);
            inserted += Math.Max(0, activeView.GetCenterMarkCount() - before);
            inserted += InsertMissingFullCircleCenterMarks(drawingModel, drawing, activeView);
        }
        drawingModel.ClearSelection2(true);
        return inserted;
    }

    private static int InsertMissingFullCircleCenterMarks(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        IView view)
    {
        var markedEntities = GetCenterMarkedEntityKeys(drawingModel, view);
        var components = (view.GetVisibleComponents() as object[])?
            .OfType<Component2>()
            .ToList()
            ?? new List<Component2>();
        var rootComponent = view.RootDrawingComponent?.Component as Component2;
        if (rootComponent != null && !components.Contains(rootComponent))
        {
            components.Add(rootComponent);
        }
        if (components.Count == 0)
        {
            components.Add(null);
        }

        var inserted = 0;
        foreach (var component in components)
        {
            Array visibleEdges;
            try
            {
                visibleEdges = view.GetVisibleEntities2(
                    component,
                    (int)swViewEntityType_e.swViewEntityType_Edge) as Array;
            }
            catch (COMException)
            {
                continue;
            }
            if (visibleEdges == null)
            {
                continue;
            }
            foreach (var item in visibleEdges)
            {
                var edge = item as IEdge;
                var curve = edge?.GetCurve() as ICurve;
                if (curve == null || !curve.IsCircle())
                {
                    continue;
                }
                var start = 0d;
                var end = 0d;
                var closed = false;
                var periodic = false;
                if (!curve.GetEndParams(out start, out end, out closed, out periodic) || !closed)
                {
                    continue;
                }

                var entityKey = GetDrawingEntityPersistenceKey(drawingModel, edge);
                if (!string.IsNullOrWhiteSpace(entityKey) && markedEntities.Contains(entityKey))
                {
                    continue;
                }
                drawingModel.ClearSelection2(true);
                if (!view.SelectEntity(edge, false))
                {
                    continue;
                }
                var centerMark = drawing.InsertCenterMark3(
                    (int)swCenterMarkStyle_e.swCenterMark_Single,
                    true,
                    false) as ICenterMark;
                if (centerMark != null)
                {
                    inserted++;
                    if (!string.IsNullOrWhiteSpace(entityKey))
                    {
                        markedEntities.Add(entityKey);
                    }
                }
            }
        }
        return inserted;
    }

    private static HashSet<string> GetCenterMarkedEntityKeys(IModelDoc2 drawingModel, IView view)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var centerMark = view?.GetFirstCenterMark() as ICenterMark;
        while (centerMark != null)
        {
            var annotation = centerMark.GetAnnotation() as IAnnotation;
            var attached = annotation?.GetAttachedEntities3() as Array;
            if (attached != null)
            {
                foreach (var entity in attached)
                {
                    var key = GetDrawingEntityPersistenceKey(drawingModel, entity);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        keys.Add(key);
                    }
                }
            }
            centerMark = centerMark.GetNext() as ICenterMark;
        }
        return keys;
    }

    private static string GetDrawingEntityPersistenceKey(IModelDoc2 drawingModel, object entity)
    {
        try
        {
            var reference = drawingModel?.Extension?.GetPersistReference3(entity) as byte[];
            return reference == null || reference.Length == 0
                ? string.Empty
                : Convert.ToBase64String(reference);
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static int InsertAutomaticDrawingCenterlines(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        AutomaticDrawingRuleDecision ruleDecision)
    {
        const string layerName = "UPTON_AUTO_CENTERLINE";
        var layerManager = drawingModel.GetLayerManager() as ILayerMgr;
        var previousLayer = layerManager?.GetCurrentLayer() ?? string.Empty;
        if (layerManager?.GetLayer(layerName) == null)
        {
            drawing.CreateLayer2(
                layerName,
                "PDM自动中心线",
                0,
                (int)swLineStyles_e.swLineCENTER,
                (int)swLineWeights_e.swLW_THIN,
                true,
                true);
        }

        var inserted = 0;
        try
        {
            foreach (var view in EnumerateDrawingModelViews(drawing).Where(view => !IsIsometricDrawingView(view)))
            {
                if (view.GetCenterLineCount() > 0 || HasAutomaticCenterline(view, layerName))
                {
                    continue;
                }

                var outline = GetDrawingViewOutline(view);
                if (outline.Width <= 0 || outline.Height <= 0)
                {
                    continue;
                }

                var centerX = (outline.Left + outline.Right) / 2d;
                var centerY = (outline.Top + outline.Bottom) / 2d;
                var tolerance = Math.Max(0.0005d, Math.Min(outline.Width, outline.Height) * 0.025d);
                var centerMarks = GetCenterMarkPositions(view);
                var addVertical = IsCenterMarkPatternSymmetric(centerMarks, centerX, true, tolerance);
                var addHorizontal = IsCenterMarkPatternSymmetric(centerMarks, centerY, false, tolerance);

                if (ruleDecision?.PartFamily == AutomaticDrawingPartFamily.Axisymmetric)
                {
                    if (outline.Width >= outline.Height * 1.15d)
                    {
                        addHorizontal = true;
                    }
                    else if (outline.Height >= outline.Width * 1.15d)
                    {
                        addVertical = true;
                    }
                }
                if (!addVertical && !addHorizontal)
                {
                    continue;
                }

                var name = view.GetName2();
                if (string.IsNullOrWhiteSpace(name) || !drawing.ActivateView(name))
                {
                    continue;
                }
                layerManager?.SetCurrentLayer(layerName);
                var extension = Math.Max(0.003d, Math.Max(outline.Width, outline.Height) * 0.04d);
                if (addHorizontal)
                {
                    inserted += CreateAutomaticCenterlineSegment(
                        drawingModel,
                        layerName,
                        outline.Left - extension,
                        centerY,
                        outline.Right + extension,
                        centerY);
                }
                if (addVertical)
                {
                    inserted += CreateAutomaticCenterlineSegment(
                        drawingModel,
                        layerName,
                        centerX,
                        outline.Top - extension,
                        centerX,
                        outline.Bottom + extension);
                }
            }
        }
        finally
        {
            layerManager?.SetCurrentLayer(previousLayer);
            drawingModel.ClearSelection2(true);
        }
        return inserted;
    }

    private static int CreateAutomaticCenterlineSegment(
        IModelDoc2 drawingModel,
        string layerName,
        double x1,
        double y1,
        double x2,
        double y2)
    {
        var segment = drawingModel?.SketchManager?.CreateCenterLine(x1, y1, 0d, x2, y2, 0d) as ISketchSegment;
        if (segment == null)
        {
            return 0;
        }
        segment.ConstructionGeometry = true;
        segment.Style = (int)swLineStyles_e.swLineCENTER;
        segment.Layer = layerName;
        return 1;
    }

    private static bool HasAutomaticCenterline(IView view, string layerName)
    {
        var sketch = view?.GetSketch() as ISketch;
        var segments = sketch?.GetSketchSegments() as object[];
        return segments != null && segments
            .OfType<ISketchSegment>()
            .Any(segment => string.Equals(segment.Layer, layerName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<PointF> GetCenterMarkPositions(IView view)
    {
        var positions = new List<PointF>();
        var centerMark = view?.GetFirstCenterMark() as ICenterMark;
        while (centerMark != null)
        {
            var annotation = centerMark.GetAnnotation() as IAnnotation;
            var position = annotation?.GetPosition() as double[];
            if (position != null && position.Length >= 2)
            {
                positions.Add(new PointF((float)position[0], (float)position[1]));
            }
            centerMark = centerMark.GetNext() as ICenterMark;
        }
        return positions;
    }

    private static bool IsCenterMarkPatternSymmetric(
        IReadOnlyList<PointF> positions,
        double axis,
        bool verticalAxis,
        double tolerance)
    {
        if (positions == null || positions.Count < 2)
        {
            return false;
        }

        var hasOffAxisPair = false;
        foreach (var point in positions)
        {
            var coordinate = verticalAxis ? point.X : point.Y;
            if (Math.Abs(coordinate - axis) > tolerance)
            {
                hasOffAxisPair = true;
            }
            var reflectedX = verticalAxis ? 2d * axis - point.X : point.X;
            var reflectedY = verticalAxis ? point.Y : 2d * axis - point.Y;
            if (!positions.Any(candidate =>
                Math.Abs(candidate.X - reflectedX) <= tolerance
                && Math.Abs(candidate.Y - reflectedY) <= tolerance))
            {
                return false;
            }
        }
        return hasOffAxisPair;
    }

    private AutomaticDrawingRuleDecision ResolveAutomaticDrawingRuleDecision(
        CadTreeNode source,
        IDrawingDoc drawing,
        AutomaticDrawingRuleProfile rules)
    {
        var hasSheetMetalFeature = false;
        var hasRevolvedFeature = false;
        try
        {
            var sourceModel = FindLoadedDocument(source?.FullPath);
            var feature = sourceModel?.FirstFeature() as IFeature;
            var inspected = 0;
            while (feature != null && inspected++ < 10000)
            {
                var typeName = feature.GetTypeName2() ?? feature.GetTypeName() ?? string.Empty;
                hasSheetMetalFeature |= typeName.IndexOf("SheetMetal", StringComparison.OrdinalIgnoreCase) >= 0
                    || typeName.IndexOf("FlatPattern", StringComparison.OrdinalIgnoreCase) >= 0
                    || typeName.IndexOf("SMBaseFlange", StringComparison.OrdinalIgnoreCase) >= 0;
                hasRevolvedFeature |= typeName.IndexOf("Revol", StringComparison.OrdinalIgnoreCase) >= 0
                    || (feature.Name ?? string.Empty).IndexOf("旋转", StringComparison.OrdinalIgnoreCase) >= 0;
                feature = feature.GetNextFeature() as IFeature;
            }
        }
        catch (COMException)
        {
            // Feature inspection is advisory; view geometry still provides a safe classification fallback.
        }

        var extents = new List<double>();
        var views = EnumerateDrawingModelViews(drawing).ToList();
        IView frontView;
        IView topView;
        IView sideView;
        IView isometricView;
        ResolveAutomaticDrawingViews(
            views,
            out frontView,
            out topView,
            out sideView,
            out isometricView);
        AddDrawingViewExtents(frontView, extents, true);
        if (topView != null)
        {
            AddDrawingViewExtents(topView, extents, false);
        }
        else if (sideView != null)
        {
            AddDrawingViewExtents(sideView, extents, false);
        }

        return AutomaticDrawingRuleEngine.Decide(
            source?.Kind ?? CadDocumentKind.Other,
            extents,
            hasSheetMetalFeature,
            hasRevolvedFeature,
            rules);
    }

    private static void AddDrawingViewExtents(IView view, ICollection<double> extents, bool includeBothAxes)
    {
        if (view == null || extents == null || view.ScaleDecimal <= 0)
        {
            return;
        }
        var outline = GetDrawingViewOutline(view);
        if (includeBothAxes)
        {
            if (outline.Width > 0)
            {
                extents.Add(outline.Width / view.ScaleDecimal);
            }
            if (outline.Height > 0)
            {
                extents.Add(outline.Height / view.ScaleDecimal);
            }
            return;
        }

        var secondaryExtent = outline.Width > 0 && outline.Height > 0
            ? Math.Min(outline.Width, outline.Height)
            : Math.Max(outline.Width, outline.Height);
        if (secondaryExtent > 0)
        {
            extents.Add(secondaryExtent / view.ScaleDecimal);
        }
    }

    private static int Standardize45DegreeChamferDimensions(IDrawingDoc drawing)
    {
        var candidatesByFeature = new Dictionary<string, List<ChamferDimensionCandidate>>(StringComparer.OrdinalIgnoreCase);
        var standardized = 0;

        foreach (var view in EnumerateDrawingModelViews(drawing))
        {
            var viewName = view.GetName2() ?? string.Empty;
            var displayDimension = view.GetFirstDisplayDimension5() as IDisplayDimension;
            while (displayDimension != null)
            {
                var modelDimension = displayDimension.GetDimension2(0) as IDimension;
                var feature = modelDimension?.GetFeatureOwner();
                if (modelDimension != null)
                {
                    var dimensionType = displayDimension.Type2;
                    if (dimensionType == (int)swDimensionType_e.swDimensionTypeUnknown)
                    {
                        dimensionType = modelDimension.GetType();
                    }
                    if (dimensionType == (int)swDimensionType_e.swChamferDimension)
                    {
                        var length = 0d;
                        var angle = 0d;
                        if (modelDimension.GetSystemChamferValues(ref length, ref angle)
                            && Is45DegreeAngle(angle))
                        {
                            displayDimension.ChamferTextStyle =
                                (int)swDetailingChamferDimLeaderTextStyle_e.swDetailChamferDimCDist;
                            standardized++;
                        }
                    }
                    else
                    {
                        var key = GetDimensionFeatureKey(modelDimension);
                        List<ChamferDimensionCandidate> candidates = null;
                        if ((dimensionType == (int)swDimensionType_e.swAngularDimension
                                || IsLinearDimension(dimensionType))
                            && !string.IsNullOrWhiteSpace(key)
                            && !candidatesByFeature.TryGetValue(key, out candidates))
                        {
                            candidates = new List<ChamferDimensionCandidate>();
                            candidatesByFeature[key] = candidates;
                        }
                        if (candidates != null)
                        {
                            candidates.Add(new ChamferDimensionCandidate(
                                displayDimension,
                                modelDimension,
                                dimensionType,
                                viewName,
                                GetAnnotationPosition(displayDimension),
                                IsChamferFeature(feature)));
                        }
                    }
                }

                displayDimension = displayDimension.GetNext5() as IDisplayDimension;
            }
        }

        foreach (var candidates in candidatesByFeature.Values)
        {
            var usedLengths = new HashSet<ChamferDimensionCandidate>();
            foreach (var angleDimension in candidates.Where(candidate =>
                candidate.DimensionType == (int)swDimensionType_e.swAngularDimension
                && Is45DegreeAngle(candidate.ModelDimension.SystemValue)))
            {
                var lengthDimension = candidates
                    .Where(candidate =>
                        IsLinearDimension(candidate.DimensionType)
                        && candidate.ModelDimension.SystemValue > 0
                        && string.Equals(candidate.ViewName, angleDimension.ViewName, StringComparison.OrdinalIgnoreCase)
                        && !usedLengths.Contains(candidate))
                    .OrderBy(candidate => GetDistance(candidate.AnnotationPosition, angleDimension.AnnotationPosition))
                    .FirstOrDefault();
                if (lengthDimension == null)
                {
                    continue;
                }

                var annotationDistance = GetDistance(
                    lengthDimension.AnnotationPosition,
                    angleDimension.AnnotationPosition);
                if (!angleDimension.IsChamferFeature
                    && !lengthDimension.IsChamferFeature
                    && (angleDimension.AnnotationPosition.IsEmpty
                        || lengthDimension.AnnotationPosition.IsEmpty
                        || annotationDistance > 0.035d))
                {
                    continue;
                }

                lengthDimension.DisplayDimension.SetText(
                    (int)swDimensionTextParts_e.swDimensionTextPrefix,
                    "C");
                lengthDimension.DisplayDimension.ShowDimensionValue = true;

                var angleAnnotation = angleDimension.DisplayDimension.GetAnnotation() as IAnnotation;
                if (angleAnnotation != null)
                {
                    angleAnnotation.Visible = (int)swAnnotationVisibilityState_e.swAnnotationHidden;
                }
                usedLengths.Add(lengthDimension);
                standardized++;
            }
        }

        return standardized;
    }

    private static bool IsChamferFeature(IFeature feature)
    {
        if (feature == null)
        {
            return false;
        }

        var typeName = feature.GetTypeName2() ?? feature.GetTypeName() ?? string.Empty;
        return typeName.IndexOf("Chamfer", StringComparison.OrdinalIgnoreCase) >= 0
            || (feature.Name ?? string.Empty).IndexOf("倒角", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetDimensionFeatureKey(IDimension dimension)
    {
        var fullName = dimension?.FullName ?? string.Empty;
        var separator = fullName.IndexOf('@');
        return separator >= 0 && separator < fullName.Length - 1
            ? fullName.Substring(separator + 1)
            : fullName;
    }

    private static bool IsLinearDimension(int dimensionType)
    {
        return dimensionType == (int)swDimensionType_e.swLinearDimension
            || dimensionType == (int)swDimensionType_e.swHorLinearDimension
            || dimensionType == (int)swDimensionType_e.swVertLinearDimension;
    }

    private static bool Is45DegreeAngle(double systemValue)
    {
        var degrees = Math.Abs(systemValue) * 180d / Math.PI % 180d;
        if (degrees > 90d)
        {
            degrees = 180d - degrees;
        }
        return Math.Abs(degrees - 45d) <= 0.1d;
    }

    private static PointF GetAnnotationPosition(IDisplayDimension dimension)
    {
        var annotation = dimension?.GetAnnotation() as IAnnotation;
        var position = annotation?.GetPosition() as double[];
        return position != null && position.Length >= 2
            ? new PointF((float)position[0], (float)position[1])
            : PointF.Empty;
    }

    private static double GetDistance(PointF left, PointF right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static int HideDuplicateDrawingDimensions(IModelDoc2 drawingModel, IDrawingDoc drawing)
    {
        // GB/T 4458.4：同一尺寸一般只标注一次，并放在表达最清晰的视图中。
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hidden = 0;
        foreach (var view in EnumerateDrawingModelViews(drawing))
        {
            var dimension = view.GetFirstDisplayDimension5() as IDisplayDimension;
            while (dimension != null)
            {
                var next = dimension.GetNext5() as IDisplayDimension;
                var annotation = dimension.GetAnnotation() as IAnnotation;
                if (annotation != null
                    && annotation.Visible != (int)swAnnotationVisibilityState_e.swAnnotationHidden)
                {
                    var identity = GetDrawingDimensionIdentity(drawingModel, view, dimension, annotation);
                    if (!string.IsNullOrWhiteSpace(identity) && !identities.Add(identity))
                    {
                        annotation.Visible = (int)swAnnotationVisibilityState_e.swAnnotationHidden;
                        hidden++;
                    }
                }

                dimension = next;
            }
        }

        return hidden;
    }

    private static string GetDrawingDimensionIdentity(
        IModelDoc2 drawingModel,
        IView view,
        IDisplayDimension displayDimension,
        IAnnotation annotation)
    {
        try
        {
            var modelDimension = displayDimension.GetDimension2(0) as IDimension;
            var fullName = modelDimension?.FullName?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fullName) && !displayDimension.IsReferenceDim())
            {
                return string.Concat("MODEL|", fullName);
            }

            var attached = annotation.GetAttachedEntities3() as object[];
            if (attached == null || attached.Length == 0 || drawingModel?.Extension == null)
            {
                return string.Empty;
            }

            var references = attached
                .Select(entity => drawingModel.Extension.GetPersistReference3(entity) as byte[])
                .Where(reference => reference != null && reference.Length > 0)
                .Select(Convert.ToBase64String)
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
            if (references.Length == 0)
            {
                return string.Empty;
            }

            return string.Concat(
                "REFERENCE|",
                view?.GetName2() ?? string.Empty,
                "|",
                displayDimension.Type2.ToString(CultureInfo.InvariantCulture),
                "|",
                string.Join(";", references));
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static int ArrangeDrawingDimensions(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        double minimumSpacing)
    {
        var selectionManager = drawingModel.SelectionManager as ISelectionMgr;
        if (selectionManager == null)
        {
            return 0;
        }

        var arrangedViews = 0;
        foreach (var view in EnumerateDrawingModelViews(drawing))
        {
            drawingModel.ClearSelection2(true);
            var selectData = selectionManager.CreateSelectData();
            var selected = 0;
            var dimension = view.GetFirstDisplayDimension5() as IDisplayDimension;
            while (dimension != null)
            {
                dimension.CenterText = true;
                dimension.DimensionToInside = false;
                var annotation = dimension.GetAnnotation() as IAnnotation;
                if (annotation != null
                    && annotation.Visible != (int)swAnnotationVisibilityState_e.swAnnotationHidden
                    && annotation.Select3(true, selectData))
                {
                    selected++;
                }
                dimension = dimension.GetNext5() as IDisplayDimension;
            }

            if (selected > 0)
            {
                if (drawingModel.Extension.AlignDimensions(
                    (int)swAlignDimensionType_e.swAlignDimensionType_AutoArrange,
                    minimumSpacing))
                {
                    arrangedViews++;
                }
            }
        }
        drawingModel.ClearSelection2(true);
        return arrangedViews;
    }

    private sealed class ChamferDimensionCandidate
    {
        public ChamferDimensionCandidate(
            IDisplayDimension displayDimension,
            IDimension modelDimension,
            int dimensionType,
            string viewName,
            PointF annotationPosition,
            bool isChamferFeature)
        {
            DisplayDimension = displayDimension;
            ModelDimension = modelDimension;
            DimensionType = dimensionType;
            ViewName = viewName;
            AnnotationPosition = annotationPosition;
            IsChamferFeature = isChamferFeature;
        }

        public IDisplayDimension DisplayDimension { get; }

        public IDimension ModelDimension { get; }

        public int DimensionType { get; }

        public string ViewName { get; }

        public PointF AnnotationPosition { get; }

        public bool IsChamferFeature { get; }
    }

    private string ResolveDrawingTemplate(string requestedTemplate)
    {
        if (!string.IsNullOrWhiteSpace(requestedTemplate))
        {
            if (!File.Exists(requestedTemplate))
            {
                throw new FileNotFoundException("所选工程图模板不存在，请重新选择模板。", requestedTemplate);
            }
            return requestedTemplate;
        }

        var template = application.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
        {
            template = application.GetDocumentTemplate(
                (int)swDocumentTypes_e.swDocDRAWING,
                string.Empty,
                (int)swDwgPaperSizes_e.swDwgPaperA3size,
                0,
                0);
        }
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
        {
            throw new FileNotFoundException("未找到SolidWorks默认工程图模板。请在“自动出图”页签选择.drwDot模板。 ");
        }
        return template;
    }

    private void CreateAutomaticDrawingViews(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        string sourcePath,
        AutomaticDrawingOptions options)
    {
        var sheet = drawing.GetCurrentSheet() as ISheet;
        var width = 0.42;
        var height = 0.297;
        sheet?.GetSize(ref width, ref height);
        if (width <= 0 || height <= 0)
        {
            width = 0.42;
            height = 0.297;
        }

        var sheetProperties = sheet?.GetProperties2() as double[];
        if (sheetProperties == null || sheetProperties.Length < 8)
        {
            throw new InvalidOperationException("SolidWorks未能读取工程图图纸属性，不能设置第一角法投影。");
        }
        sheet.SetProperties2(
            (int)sheetProperties[0],
            (int)sheetProperties[1],
            sheetProperties[2],
            sheetProperties[3],
            true,
            sheetProperties[5],
            sheetProperties[6],
            sheetProperties[7] != 0);

        if (!drawing.Create1stAngleViews2(sourcePath))
        {
            throw new InvalidOperationException("SolidWorks未能创建标准三视图。请确认模型可正常打开，并检查工程图模板。");
        }

        var createdViews = EnumerateDrawingModelViews(drawing).ToList();
        IView primaryView;
        IView topView;
        IView sideView;
        IView isometricView;
        ResolveAutomaticDrawingViews(
            createdViews,
            out primaryView,
            out topView,
            out sideView,
            out isometricView);
        if (primaryView == null)
        {
            throw new InvalidOperationException("SolidWorks已执行三视图生成，但未返回可用的工程图视图。");
        }

        if (options.GenerateIsometric && isometricView == null)
        {
            isometricView = CreateAutomaticDrawingIsometricView(
                drawing,
                sourcePath,
                width,
                height);
            createdViews.Add(isometricView);
        }

        ArrangeAutomaticDrawingViews(
            drawingModel,
            sheet,
            width,
            height,
            primaryView,
            topView,
            sideView,
            isometricView);

        foreach (var view in createdViews)
        {
            var position = GetDrawingViewPosition(view);
            var outline = GetDrawingViewOutline(view);
            LogOperation(string.Concat(
                "Automatic drawing view name=", view.GetName2(),
                " orientation=", GetDrawingViewOrientation(view),
                " scale=", view.ScaleDecimal.ToString("G17", CultureInfo.InvariantCulture),
                " x=", position.X.ToString("G17", CultureInfo.InvariantCulture),
                " y=", position.Y.ToString("G17", CultureInfo.InvariantCulture),
                " left=", outline.Left.ToString("G17", CultureInfo.InvariantCulture),
                " bottom=", outline.Top.ToString("G17", CultureInfo.InvariantCulture),
                " right=", outline.Right.ToString("G17", CultureInfo.InvariantCulture),
                " top=", outline.Bottom.ToString("G17", CultureInfo.InvariantCulture)));
        }

        if (options.IncludeAssemblyBom
            && string.Equals(Path.GetExtension(sourcePath), ".SLDASM", StringComparison.OrdinalIgnoreCase))
        {
            InsertAssemblyBom(primaryView, width, height);
        }
    }

    private static void ArrangeExistingAutomaticDrawingViews(
        IModelDoc2 drawingModel,
        IDrawingDoc drawing,
        string sourcePath,
        AutomaticDrawingOptions options)
    {
        var sheet = drawing.GetCurrentSheet() as ISheet;
        var width = 0.42;
        var height = 0.297;
        sheet?.GetSize(ref width, ref height);
        var createdViews = EnumerateDrawingModelViews(drawing).ToList();
        IView frontView;
        IView topView;
        IView sideView;
        IView isometricView;
        ResolveAutomaticDrawingViews(
            createdViews,
            out frontView,
            out topView,
            out sideView,
            out isometricView);
        if (frontView == null)
        {
            throw new InvalidOperationException("现有工程图中未找到可重新布局的主视图。");
        }


        if (options?.GenerateIsometric == true && isometricView == null)
        {
            isometricView = CreateAutomaticDrawingIsometricView(
                drawing,
                sourcePath,
                width,
                height);
        }

        ArrangeAutomaticDrawingViews(
            drawingModel,
            sheet,
            width,
            height,
            frontView,
            topView,
            sideView,
            isometricView);
    }

    private static IView CreateAutomaticDrawingIsometricView(
        IDrawingDoc drawing,
        string sourcePath,
        double sheetWidth,
        double sheetHeight)
    {
        var view = drawing.CreateDrawViewFromModelView3(
            sourcePath,
            "*Isometric",
            sheetWidth * 0.68d,
            sheetHeight * 0.30d,
            0d) as IView;
        if (view == null)
        {
            throw new InvalidOperationException("SolidWorks未能创建轴测图，请确认模型包含标准轴测视图。");
        }
        return view;
    }

    private static void ResolveAutomaticDrawingViews(
        IReadOnlyList<IView> createdViews,
        out IView frontView,
        out IView topView,
        out IView sideView,
        out IView isometricView)
    {
        var resolvedIsometricView = createdViews.FirstOrDefault(IsIsometricDrawingView);
        var resolvedFrontView = createdViews.FirstOrDefault(view =>
            string.Equals(GetDrawingViewOrientation(view), "*Front", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetDrawingViewOrientation(view), "*前视", StringComparison.OrdinalIgnoreCase))
            ?? createdViews.FirstOrDefault(view => !ReferenceEquals(view, resolvedIsometricView));
        var projectedViews = createdViews.Where(view =>
            !ReferenceEquals(view, resolvedFrontView)
            && !ReferenceEquals(view, resolvedIsometricView)).ToList();
        var frontPosition = GetDrawingViewPosition(resolvedFrontView);
        var resolvedTopView = projectedViews.FirstOrDefault(view =>
            string.Equals(GetDrawingViewOrientation(view), "*Top", StringComparison.OrdinalIgnoreCase))
            ?? projectedViews.OrderBy(view => Math.Abs(GetDrawingViewPosition(view).X - frontPosition.X)).FirstOrDefault();
        var resolvedSideView = projectedViews.FirstOrDefault(view =>
            !ReferenceEquals(view, resolvedTopView)
            && string.Equals(GetDrawingViewOrientation(view), "*Left", StringComparison.OrdinalIgnoreCase))
            ?? projectedViews.FirstOrDefault(view =>
                !ReferenceEquals(view, resolvedTopView)
                && string.Equals(GetDrawingViewOrientation(view), "*Right", StringComparison.OrdinalIgnoreCase))
            ?? projectedViews.FirstOrDefault(view => !ReferenceEquals(view, resolvedTopView));
        frontView = resolvedFrontView;
        topView = resolvedTopView;
        sideView = resolvedSideView;
        isometricView = resolvedIsometricView;
    }

    private static bool IsIsometricDrawingView(IView view)
    {
        var orientation = GetDrawingViewOrientation(view);
        var name = view?.GetName2() ?? string.Empty;
        return orientation.IndexOf("Isometric", StringComparison.OrdinalIgnoreCase) >= 0
            || orientation.IndexOf("等轴测", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Isometric", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("等轴测", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ArrangeAutomaticDrawingViews(
        IModelDoc2 drawingModel,
        ISheet sheet,
        double sheetWidth,
        double sheetHeight,
        IView frontView,
        IView topView,
        IView sideView,
        IView isometricView)
    {
        var rules = AutomaticDrawingRuleStore.Load();
        var frameLeft = sheetWidth * 0.05;
        var frameRight = sheetWidth * 0.95;
        var frameBottom = sheetHeight * 0.18;
        var frameTop = sheetHeight * 0.94;
        var maximumScale = double.MaxValue;
        maximumScale = Math.Min(maximumScale, GetMaximumDrawingViewScale(
            frontView,
            sheetWidth * 0.34,
            sheetHeight * 0.36));
        maximumScale = Math.Min(maximumScale, GetMaximumDrawingViewScale(
            topView,
            sheetWidth * 0.34,
            sheetHeight * 0.36));
        maximumScale = Math.Min(maximumScale, GetMaximumDrawingViewScale(
            sideView,
            sheetWidth * 0.30,
            sheetHeight * 0.36));
        if (isometricView != null)
        {
            maximumScale = Math.Min(
                maximumScale,
                GetMaximumDrawingViewScale(
                    isometricView,
                    sheetWidth * 0.30,
                    sheetHeight * 0.30) / rules.IsometricScaleRatio);
        }

        if (double.IsInfinity(maximumScale) || maximumScale == double.MaxValue)
        {
            maximumScale = frontView.ScaleDecimal;
        }
        var targetScale = SelectStandardDrawingScale(maximumScale * 0.98, rules);
        ApplyDrawingSheetScale(sheet, targetScale);
        ApplyProjectedDrawingViewScale(frontView, topView, sideView, isometricView, targetScale, rules);
        drawingModel.ForceRebuild3(false);

        var frontX = sheetWidth * 0.30;
        var sideX = sheetWidth * 0.67;
        var upperY = sheetHeight * 0.66;
        frontView.Position = new double[] { frontX, upperY };
        if (topView != null)
        {
            topView.Position = new double[] { frontX, sheetHeight * 0.30 };
        }
        if (sideView != null)
        {
            sideView.Position = new double[] { sideX, upperY };
        }
        if (isometricView != null)
        {
            isometricView.Position = new double[] { sideX, sheetHeight * 0.30d };
        }
        drawingModel.ForceRebuild3(false);

        var annotationFrameReserve = rules.AnnotationFrameReserveMeters;
        var annotationViewGap = rules.ViewAnnotationGapMeters;
        var boundaryFactor = 1.0;
        foreach (var view in new[] { frontView, topView, sideView, isometricView }.Where(view => view != null))
        {
            boundaryFactor = Math.Min(
                boundaryFactor,
                GetDrawingViewBoundaryScaleFactor(
                    view,
                    frameLeft + annotationFrameReserve,
                    frameRight - annotationFrameReserve,
                    frameBottom + annotationFrameReserve,
                    frameTop - annotationFrameReserve));
        }
        boundaryFactor = Math.Min(
            boundaryFactor,
            GetVerticalDrawingViewGapScaleFactor(frontView, topView, annotationViewGap));
        boundaryFactor = Math.Min(
            boundaryFactor,
            GetHorizontalDrawingViewGapScaleFactor(frontView, sideView, annotationViewGap));
        boundaryFactor = Math.Min(
            boundaryFactor,
            GetVerticalDrawingViewGapScaleFactor(sideView, isometricView, annotationViewGap));
        boundaryFactor = Math.Min(
            boundaryFactor,
            GetHorizontalDrawingViewGapScaleFactor(topView, isometricView, annotationViewGap));
        if (boundaryFactor < 0.999)
        {
            var reducedScale = SelectStandardDrawingScale(targetScale * boundaryFactor * 0.98, rules);
            if (reducedScale < targetScale)
            {
                ApplyDrawingSheetScale(sheet, reducedScale);
                ApplyProjectedDrawingViewScale(
                    frontView,
                    topView,
                    sideView,
                    isometricView,
                    reducedScale,
                    rules);
                drawingModel.ForceRebuild3(false);
            }
        }
    }

    private static double GetVerticalDrawingViewGapScaleFactor(
        IView upperView,
        IView lowerView,
        double minimumGap)
    {
        if (upperView == null || lowerView == null)
        {
            return 1.0;
        }

        var upperOutline = GetDrawingViewOutline(upperView);
        var lowerOutline = GetDrawingViewOutline(lowerView);
        var upperPosition = GetDrawingViewPosition(upperView);
        var lowerPosition = GetDrawingViewPosition(lowerView);
        var currentExtents = upperPosition.Y - upperOutline.Top
            + lowerOutline.Bottom - lowerPosition.Y;
        var availableExtents = upperPosition.Y - lowerPosition.Y - minimumGap;
        return currentExtents > 0
            ? Math.Max(0.05, availableExtents / currentExtents)
            : 1.0;
    }

    private static double GetHorizontalDrawingViewGapScaleFactor(
        IView leftView,
        IView rightView,
        double minimumGap)
    {
        if (leftView == null || rightView == null)
        {
            return 1.0;
        }

        var leftOutline = GetDrawingViewOutline(leftView);
        var rightOutline = GetDrawingViewOutline(rightView);
        var leftPosition = GetDrawingViewPosition(leftView);
        var rightPosition = GetDrawingViewPosition(rightView);
        var currentExtents = leftOutline.Right - leftPosition.X
            + rightPosition.X - rightOutline.Left;
        var availableExtents = rightPosition.X - leftPosition.X - minimumGap;
        return currentExtents > 0
            ? Math.Max(0.05, availableExtents / currentExtents)
            : 1.0;
    }

    private static double GetMaximumDrawingViewScale(IView view, double maximumWidth, double maximumHeight)
    {
        if (view == null || view.ScaleDecimal <= 0)
        {
            return double.MaxValue;
        }
        var outline = GetDrawingViewOutline(view);
        if (outline.Width <= 0 || outline.Height <= 0)
        {
            return double.MaxValue;
        }
        return Math.Min(
            maximumWidth / (outline.Width / view.ScaleDecimal),
            maximumHeight / (outline.Height / view.ScaleDecimal));
    }

    private static double SelectStandardDrawingScale(
        double maximumScale,
        AutomaticDrawingRuleProfile rules)
    {
        var standardScales = rules?.StandardScales;
        if (standardScales == null || standardScales.Length == 0)
        {
            standardScales = new[] { 10.0, 5.0, 2.0, 1.0, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };
        }
        foreach (var scale in standardScales)
        {
            if (scale <= maximumScale + 0.0000001)
            {
                return scale;
            }
        }
        return standardScales[standardScales.Length - 1];
    }

    private static void ApplyDrawingSheetScale(ISheet sheet, double scale)
    {
        var properties = sheet?.GetProperties2() as double[];
        if (properties == null || properties.Length < 8)
        {
            return;
        }
        var numerator = scale >= 1 ? scale : 1;
        var denominator = scale >= 1 ? 1 : 1 / scale;
        sheet.SetProperties2(
            (int)properties[0],
            (int)properties[1],
            numerator,
            denominator,
            true,
            properties[5],
            properties[6],
            properties[7] != 0);
    }

    private static void ApplyProjectedDrawingViewScale(
        IView frontView,
        IView topView,
        IView sideView,
        IView isometricView,
        double scale,
        AutomaticDrawingRuleProfile rules)
    {
        frontView.UseParentScale = false;
        frontView.ScaleDecimal = scale;
        if (topView != null)
        {
            topView.UseParentScale = true;
        }
        if (sideView != null)
        {
            sideView.UseParentScale = true;
        }
        if (isometricView != null)
        {
            isometricView.UseParentScale = false;
            isometricView.ScaleDecimal = scale * (rules?.IsometricScaleRatio ?? 0.5d);
        }
    }

    private static double GetDrawingViewBoundaryScaleFactor(
        IView view,
        double frameLeft,
        double frameRight,
        double frameBottom,
        double frameTop)
    {
        var outline = GetDrawingViewOutline(view);
        var position = GetDrawingViewPosition(view);
        var factor = 1.0;
        var leftExtent = position.X - outline.Left;
        var rightExtent = outline.Right - position.X;
        var bottomExtent = position.Y - outline.Top;
        var topExtent = outline.Bottom - position.Y;
        if (leftExtent > 0)
        {
            factor = Math.Min(factor, (position.X - frameLeft) / leftExtent);
        }
        if (rightExtent > 0)
        {
            factor = Math.Min(factor, (frameRight - position.X) / rightExtent);
        }
        if (bottomExtent > 0)
        {
            factor = Math.Min(factor, (position.Y - frameBottom) / bottomExtent);
        }
        if (topExtent > 0)
        {
            factor = Math.Min(factor, (frameTop - position.Y) / topExtent);
        }
        return Math.Max(0.05, factor);
    }

    private static PointF GetDrawingViewPosition(IView view)
    {
        var position = view?.Position as double[];
        return position != null && position.Length >= 2
            ? new PointF((float)position[0], (float)position[1])
            : PointF.Empty;
    }

    private static RectangleF GetDrawingViewOutline(IView view)
    {
        var outline = view?.GetOutline() as double[];
        return outline != null && outline.Length >= 4
            ? RectangleF.FromLTRB((float)outline[0], (float)outline[1], (float)outline[2], (float)outline[3])
            : RectangleF.Empty;
    }

    private static IEnumerable<IView> EnumerateDrawingModelViews(IDrawingDoc drawing)
    {
        var view = (drawing.GetFirstView() as IView)?.GetNextView() as IView;
        while (view != null)
        {
            yield return view;
            view = view.GetNextView() as IView;
        }
    }

    private static string GetDrawingViewOrientation(IView view)
    {
        try
        {
            return view?.GetOrientationName() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void InsertAssemblyBom(IView view, double sheetWidth, double sheetHeight)
    {
        if (view == null)
        {
            throw new InvalidOperationException("未找到可用于生成BOM的装配体视图。");
        }

        var template = ResolveBomTemplate();
        var bom = view.InsertBomTable4(
            true,
            Math.Max(0.01, sheetWidth - 0.01),
            Math.Max(0.01, sheetHeight - 0.01),
            (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
            (int)swBomType_e.swBomType_Indented,
            string.Empty,
            template,
            false,
            (int)swNumberingType_e.swNumberingType_Detailed,
            true);
        if (bom == null)
        {
            throw new InvalidOperationException("SolidWorks未能生成装配体BOM，请检查BOM模板和装配体配置。");
        }
    }

    private string ResolveBomTemplate()
    {
        var candidates = new List<string>();
        var configuredFolders = application.GetUserPreferenceStringListValue((int)swUserPreferenceStringValue_e.swFileLocationsBOMTemplates);
        foreach (var folder in (configuredFolders ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(folder.Trim(), "bom-standard.sldbomtbt"));
        }

        var executableDirectory = Path.GetDirectoryName(application.GetExecutablePath()) ?? string.Empty;
        var language = application.GetCurrentLanguage();
        if (!string.IsNullOrWhiteSpace(language))
        {
            candidates.Add(Path.Combine(executableDirectory, "lang", language, "bom-standard.sldbomtbt"));
        }
        candidates.Add(Path.Combine(executableDirectory, "lang", "chinese-simplified", "bom-standard.sldbomtbt"));
        candidates.Add(Path.Combine(executableDirectory, "lang", "english", "bom-standard.sldbomtbt"));

        var template = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new FileNotFoundException("未找到SolidWorks标准BOM模板。请在SolidWorks文件位置中配置BOM模板。 ");
        }
        return template;
    }

    private void EnsureDrawingCanBeChanged(string drawingPath)
    {
        if (IsHistoricalPreviewPath(drawingPath))
        {
            throw new InvalidOperationException("历史工程图只能预览，不能更新。");
        }

        var drawingNode = FindCadNodeByPath(currentTree, drawingPath);
        if (drawingNode?.DocumentId.HasValue == true && !IsCheckedOutByCurrentUser(drawingNode))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(drawingNode.CheckedOutBy)
                ? "该工程图已入库。请先获取工程图的编辑权限，再进行更新。"
                : string.Concat("该工程图正在由", drawingNode.CheckedOutBy, "编辑。"));
        }

        if ((File.GetAttributes(drawingPath) & FileAttributes.ReadOnly) != 0)
        {
            throw new InvalidOperationException("关联工程图为只读文件。请先获取工程图编辑权限。");
        }

        var loaded = FindLoadedDocument(drawingPath);
        if (loaded?.IsOpenedReadOnly() == true)
        {
            throw new InvalidOperationException("关联工程图当前以只读方式打开。请关闭后获取编辑权限。");
        }
    }

    private static void SaveSolidWorksDocument(IModelDoc2 document)
    {
        var errors = 0;
        var warnings = 0;
        if (!document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
        {
            throw new IOException(string.Concat("SolidWorks保存工程图失败，错误码：", errors, "，警告码：", warnings));
        }
    }

    private static bool ReferencesSource(string referencedPath, string sourcePath) =>
        PathsEqual(referencedPath, sourcePath)
        || string.Equals(Path.GetFileName(referencedPath), Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase);

    private CadTreeNode ResolveActiveDrawingSource(CadTreeNode drawingRoot, CadTreeNode previousTree)
    {
        if (drawingRoot?.Kind != CadDocumentKind.Drawing)
        {
            return null;
        }

        var drawing = application?.ActiveDoc as IDrawingDoc;
        var referencedPath = string.Empty;
        try
        {
            var view = (drawing?.GetFirstView() as IView)?.GetNextView() as IView;
            while (view != null)
            {
                var candidate = view.GetReferencedModelName() ?? string.Empty;
                var candidateKind = DocumentKindFromPath(candidate);
                if (candidateKind == CadDocumentKind.Part || candidateKind == CadDocumentKind.Assembly)
                {
                    referencedPath = candidate;
                    break;
                }

                view = view.GetNextView() as IView;
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("Resolve active drawing source", exception);
        }

        if (!string.IsNullOrWhiteSpace(referencedPath) && !Path.IsPathRooted(referencedPath))
        {
            referencedPath = Path.Combine(Path.GetDirectoryName(drawingRoot.FullPath) ?? string.Empty, referencedPath);
        }

        if (string.IsNullOrWhiteSpace(referencedPath) || !File.Exists(referencedPath))
        {
            var basePath = Path.Combine(
                Path.GetDirectoryName(drawingRoot.FullPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(drawingRoot.FullPath));
            referencedPath = new[] { string.Concat(basePath, ".SLDPRT"), string.Concat(basePath, ".SLDASM") }
                .FirstOrDefault(File.Exists) ?? string.Empty;
        }

        var kind = DocumentKindFromPath(referencedPath);
        if (string.IsNullOrWhiteSpace(referencedPath)
            || (kind != CadDocumentKind.Part && kind != CadDocumentKind.Assembly))
        {
            return null;
        }

        var source = new CadTreeNode
        {
            InstancePath = referencedPath,
            FileName = Path.GetFileName(referencedPath),
            FullPath = referencedPath,
            DisplayName = Path.GetFileNameWithoutExtension(referencedPath),
            DrawingNumber = Path.GetFileNameWithoutExtension(referencedPath),
            Kind = kind,
            Configuration = "默认",
            Status = CadReferenceStatus.Normal
        };
        var previousSource = FindCadNodeByPath(previousTree, referencedPath);
        if (previousSource != null)
        {
            CopyPdmIdentity(previousSource, source);
        }

        RestorePersistedDocumentIdentities(source);
        ApplyControlledOpenMetadata(source);
        if (currentProjectId.HasValue)
        {
            RememberExplicitProjectPath(referencedPath, currentProjectId.Value);
        }

        return source;
    }

    private static CadTreeNode FindCadNodeByPath(CadTreeNode root, string fullPath) =>
        EnumerateCadNodes(root).FirstOrDefault(node => PathsEqual(node.FullPath, fullPath));

    private static void CopyPdmIdentity(CadTreeNode source, CadTreeNode target)
    {
        target.DocumentId = source.DocumentId;
        target.DrawingNumber = source.DrawingNumber;
        target.Description = source.Description;
        target.Material = source.Material;
        target.LifecycleState = source.LifecycleState;
        target.UpdatedAt = source.UpdatedAt;
        target.Revision = source.Revision;
        target.CurrentRevision = source.CurrentRevision;
        target.LatestRevision = source.LatestRevision;
        target.LatestVersionSha256 = source.LatestVersionSha256;
        target.LatestStoredSha256 = source.LatestStoredSha256;
        target.CheckedOutBy = source.CheckedOutBy;
        target.CheckedOutAt = source.CheckedOutAt;
        target.CheckoutSessionId = source.CheckoutSessionId;
        target.CheckoutMachine = source.CheckoutMachine;
        target.CheckoutLastHeartbeatAt = source.CheckoutLastHeartbeatAt;
        target.CheckoutSessionLost = source.CheckoutSessionLost;
        target.WorkState = source.WorkState;
        target.IsRenamePendingSave = source.IsRenamePendingSave
            && !string.IsNullOrWhiteSpace(target.FullPath)
            && !File.Exists(target.FullPath);
        if (target.IsRenamePendingSave)
        {
            target.Status = CadReferenceStatus.Normal;
        }
    }

    private void OnOpenRequested(object sender, CadTreeNodeEventArgs eventArgs) =>
        BeginControlledOpen(eventArgs.Node, ControlledOpenMode.LatestReadOnly);

    private void OnOpenWorkingFileRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            ShowError("当前结构引用的本地图档不存在，不能打开。");
            return;
        }

        QueueOpenDocument(node.FullPath, node.Kind, node.Configuration);
    }

    private async void OnUpdateLatestRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。");
            return;
        }
        if (node == null || !node.DocumentId.HasValue)
        {
            ShowError("该图档尚未入库，不能获取最新版本。");
            return;
        }
        if (node.IsHistoricalPreview || IsHistoricalPreviewContext(node))
        {
            ShowError("历史版本为只读预览，不能更新工作区。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }
        if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
        {
            ShowError("已有获取文件或存档任务正在进行，请稍候再试。");
            return;
        }

        var stagedPath = string.Empty;
        var reopenPath = string.Empty;
        var reopenKind = CadDocumentKind.Other;
        var reopenConfiguration = string.Empty;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            RefreshLoadedDocumentModificationFlags(currentTree);
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var serverDocument = projectDocuments.FirstOrDefault(document => document.Id == node.DocumentId.Value)
                ?? throw new InvalidOperationException("当前项目中未找到该图档，请刷新结构树后重试。");
            ApplyCheckoutDocument(node, serverDocument);
            ValidateUpdateLatestNode(node);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该图档尚无可获取的PDM版本。");
            ApplyLatestVersion(node, latest);

            if (File.Exists(node.FullPath))
            {
                var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), lifetime.Token);
                if (VersionMatchesLocalFile(latest, node.FullPath, localSha256))
                {
                    ApplyUpdatedWorkingVersion(node, latest);
                    taskPaneControl.SetTree(currentTree);
                    MessageBox.Show(taskPaneControl, "当前工作文件已是最新版本。", "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!versions.Any(version => VersionMatchesLocalFile(version, node.FullPath, localSha256)))
                {
                    throw new InvalidOperationException(string.Concat(
                        node.FileName,
                        "的本地内容无法对应任何PDM历史版本，可能存在待提交修改。为避免覆盖，已停止更新。"));
                }
            }

            stagedPath = await apiClient.DownloadVersionToWorkspaceStageAsync(
                node.DocumentId.Value,
                latest.Id,
                node.FileName,
                latest.Sha256,
                lifetime.Token);

            var rootPath = currentTree?.FullPath ?? string.Empty;
            var rootLoaded = !string.IsNullOrWhiteSpace(rootPath) && FindLoadedDocument(rootPath) != null;
            var selectedLoaded = FindLoadedDocument(node.FullPath) != null;
            if (rootLoaded)
            {
                reopenPath = rootPath;
                reopenKind = currentTree.Kind;
                reopenConfiguration = currentTree.Configuration;
            }
            else if (selectedLoaded)
            {
                reopenPath = node.FullPath;
                reopenKind = node.Kind;
                reopenConfiguration = node.Configuration;
            }

            EnsureWorkspaceDocumentsAreSaved();
            CloseDocumentsForWorkspaceUpdate(new[] { node.FullPath }, rootPath);
            ApplyWorkspaceUpdates(new[] { new WorkspaceUpdatePlan(node, latest, stagedPath) });
            ApplyUpdatedWorkingVersion(node, latest);
            taskPaneControl.SetTree(currentTree);
            MessageBox.Show(
                taskPaneControl,
                string.Concat("已更新到最新版本：", latest.Revision?.Display ?? "-", "。\r\n原工作文件已保留在本地工作区备份目录。"),
                "UPTON PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnUpdateLatestRequested", exception);
            ShowError(exception.Message);
        }
        finally
        {
            DeleteWorkspaceStage(stagedPath);
            if (!string.IsNullOrWhiteSpace(reopenPath) && File.Exists(reopenPath))
            {
                OpenOrActivateDocumentOnSolidWorksThread(reopenPath, ToSolidWorksDocumentType(reopenKind), reopenConfiguration);
            }
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            ScheduleTreeRefresh();
        }
    }

    private void ValidateUpdateLatestNode(CadTreeNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy))
        {
            throw new InvalidOperationException(string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                ? "当前用户正在编辑该图档，请先提交存档或放弃编辑。"
                : string.Concat("该图档正在由", node.CheckedOutBy, "编辑，不能更新工作区。"));
        }
        if (node.IsModifiedInSolidWorks
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn)
        {
            throw new InvalidOperationException("该图档存在未保存修改或待提交内容，不能用PDM版本覆盖。");
        }
        if (string.IsNullOrWhiteSpace(node.FullPath)
            || ToSolidWorksDocumentType(node.Kind) == (int)swDocumentTypes_e.swDocNONE)
        {
            throw new InvalidOperationException("该文件类型或工作路径不支持更新。");
        }
        var loaded = FindLoadedDocument(node.FullPath);
        if (loaded?.GetSaveFlag() == true)
        {
            throw new InvalidOperationException("该图档存在未保存修改，请先保存或放弃修改。");
        }
    }

    private void ApplyUpdatedWorkingVersion(CadTreeNode source, DocumentVersionDto latest)
    {
        var revision = latest?.Revision?.Display ?? string.Empty;
        foreach (var node in EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, source.FullPath)))
        {
            ApplyLatestVersion(node, latest);
            node.Revision = revision;
            node.CurrentRevision = revision;
            node.WorkState = CadWorkState.None;
            node.IsModifiedInSolidWorks = false;
            if (node.Status == CadReferenceStatus.Missing && File.Exists(node.FullPath))
            {
                node.Status = CadReferenceStatus.Normal;
            }
        }
    }

    private async void OnControlledOpenRequested(object sender, ControlledOpenEventArgs eventArgs)
    {
        if (eventArgs.Mode == ControlledOpenMode.Versions)
        {
            try
            {
                var versions = await apiClient.GetVersionsAsync(eventArgs.Node.DocumentId.Value, lifetime.Token);
                taskPaneControl.ShowVersions(eventArgs.Node.DocumentId.Value, eventArgs.Node.FileName, versions);
            }
            catch (Exception exception) { ShowError(exception.Message); }
            return;
        }

        if (!eventArgs.ProjectId.HasValue)
        {
            ShowError("未识别图档所属项目，请重新选择权限内项目。");
            return;
        }

        BeginControlledOpen(eventArgs.ProjectId.Value, eventArgs.Node.DocumentId.Value, eventArgs.Mode, eventArgs.VersionId);
    }

    private void BeginControlledOpen(CadTreeNode node, ControlledOpenMode mode, Guid? versionId = null)
    {
        if (!EnsureServerDocument(node)) return;
        BeginControlledOpen(currentProjectId ?? Guid.Empty, node.DocumentId.Value, mode, versionId);
    }

    private void BeginControlledOpen(Guid projectId, Guid documentId, ControlledOpenMode mode, Guid? versionId = null)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM，再打开受控图档。");
            return;
        }
        if (Interlocked.Exchange(ref controlledOpenInProgress, 1) != 0)
        {
            ShowError("已有受控图档正在准备，请稍候。");
            return;
        }

        ExecuteControlledOpenAsync(projectId, documentId, mode, versionId);
    }

    private async void ExecuteControlledOpenAsync(Guid requestedProjectId, Guid documentId, ControlledOpenMode mode, Guid? versionId)
    {
        string rootPath = null;
        Guid? missingReferenceRecoveryProjectId = null;
        try
        {
            Interlocked.Exchange(ref workspaceOperationInProgress, 1);
            var forEdit = mode == ControlledOpenMode.LatestEdit;
            var releasedOnly = mode == ControlledOpenMode.LatestReleased;
            var specificVersionId = mode == ControlledOpenMode.SpecificReadOnly ? versionId : null;
            var manifest = await apiClient.CreateControlledOpenManifestAsync(
                documentId,
                specificVersionId,
                releasedOnly,
                forEdit,
                lifetime.Token);
            if (requestedProjectId != Guid.Empty && manifest.ProjectId != requestedProjectId)
            {
                throw new InvalidOperationException("客户端所选项目与图档所属项目不一致，已停止打开。请刷新项目图档后重试。");
            }
            var readOnlyRootPath = await controlledWorkspace.PrepareReadOnlyAsync(manifest, lifetime.Token);
            rootPath = readOnlyRootPath;
            if (forEdit)
            {
                var documents = await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
                var rootDocument = documents.FirstOrDefault(item => item.Id == manifest.RootDocumentId)
                    ?? throw new InvalidOperationException("打开清单的根图档不存在。");
                var alreadyCheckedOut = string.Equals(rootDocument.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                    && rootDocument.CheckoutSessionId == checkoutSessionId;
                TrackCheckoutDocument(rootDocument);
                var oldWorkingRoot = controlledWorkspace.GetWorkingRootPath(manifest);
                var oldWorkingOpen = File.Exists(oldWorkingRoot) && FindLoadedDocument(oldWorkingRoot) != null;
                if (oldWorkingOpen)
                {
                    EnsureWorkspaceDocumentsAreSaved();
                    CloseDocumentsForWorkspaceUpdate(controlledWorkspace.GetWorkingFilePaths(manifest), oldWorkingRoot);
                }

                if (!alreadyCheckedOut)
                {
                    var checkedOutDocument = await apiClient.CheckoutAsync(manifest.RootDocumentId, checkoutSessionId, checkoutMachineName, lifetime.Token);
                    TrackCheckoutDocument(checkedOutDocument);
                }
                try
                {
                    rootPath = controlledWorkspace.PromoteToEditable(manifest, readOnlyRootPath);
                }
                catch
                {
                    if (!alreadyCheckedOut)
                    {
                        try
                        {
                            var discarded = await apiClient.DiscardCheckoutAsync(manifest.RootDocumentId, checkoutSessionId, lifetime.Token);
                            TrackCheckoutDocument(discarded);
                        }
                        catch (Exception rollbackException) { LogDiagnostic("ControlledOpen checkout rollback", rollbackException); }
                    }
                    throw;
                }
            }

            RememberControlledOpenManifest(manifest, Path.GetDirectoryName(rootPath));

            RememberExplicitProjectPath(rootPath, manifest.ProjectId);
            LogOperation(string.Concat("ControlledOpen prepared project=", manifest.ProjectCode, " document=", documentId, " revision=", manifest.RootRevision, " files=", manifest.Files.Count, " edit=", forEdit));
        }
        catch (Exception exception)
        {
            LogDiagnostic("ExecuteControlledOpenAsync", exception);
            var recoveryProjectId = requestedProjectId != Guid.Empty
                ? requestedProjectId
                : currentProjectId ?? Guid.Empty;
            if (IsUnregisteredReferenceOpenFailure(exception)
                && recoveryProjectId != Guid.Empty
                && CanRecoverUnregisteredReferences(documentId))
            {
                missingReferenceRecoveryProjectId = recoveryProjectId;
                LogOperation(string.Concat(
                    "ControlledOpen redirected to batch check-in project=",
                    recoveryProjectId,
                    " document=",
                    documentId));
            }
            else
            {
                ShowError(exception.Message);
            }
            rootPath = null;
        }
        finally
        {
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            Interlocked.Exchange(ref controlledOpenInProgress, 0);
        }

        if (missingReferenceRecoveryProjectId.HasValue)
        {
            currentProjectId = missingReferenceRecoveryProjectId.Value;
            RememberExplicitProjectPath(currentTree.FullPath, missingReferenceRecoveryProjectId.Value);
            taskPaneControl.SelectProject(missingReferenceRecoveryProjectId.Value);
            taskPaneControl.ShowStructureTab();
            OpenBatchOperationDialog(missingReferenceRecoveryProjectId, BatchOperationKind.CheckIn);
            return;
        }

        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            QueueOpenDocument(rootPath, DocumentKindFromPath(rootPath), string.Empty);
        }
    }

    private void TryStartControlledOpenRequest()
    {
        if (disconnecting || Volatile.Read(ref controlledOpenInProgress) > 0 || controlledOpenListener == null)
        {
            return;
        }
        if (pendingControlledOpenRequest == null && !controlledOpenListener.TryDequeue(out pendingControlledOpenRequest))
        {
            return;
        }
        if (!apiClient.IsAuthenticated)
        {
            taskPaneControl?.SetConnectionState(false, "登录后打开客户端所选图档");
            return;
        }

        var request = pendingControlledOpenRequest;
        pendingControlledOpenRequest = null;
        if (!Enum.TryParse(request.Mode, false, out ControlledOpenMode mode))
        {
            ShowError("客户端发送了不支持的SolidWorks打开模式。");
            return;
        }
        if (taskPaneControl != null && !taskPaneControl.IsDisposed && taskPaneControl.IsHandleCreated)
        {
            taskPaneControl.BeginInvoke(new Action(() => BeginControlledOpen(request.ProjectId, request.DocumentId, mode, request.VersionId)));
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

        if (Volatile.Read(ref checkInOperationInProgress) > 0
            || Volatile.Read(ref workspaceOperationInProgress) > 0)
        {
            ShowError("正在处理PDM工作文件，请稍候再打开其他图档。");
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

        if (IsHistoricalPreviewContext(node))
        {
            ShowError("历史版本为只读预览，不能获取编辑权限。请关闭历史版本并打开当前工作文件。 ");
            return;
        }

        if (!currentProjectId.HasValue)
        {
            ShowError("请先选择当前项目。");
            return;
        }

        if (!node.DocumentId.HasValue)
        {
            OpenBatchOperationDialog(
                currentProjectId,
                BatchOperationKind.AcquireLatestAndCheckout,
                string.IsNullOrWhiteSpace(node.FullPath) ? null : new[] { node.FullPath });
            return;
        }

        if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
        {
            ShowError("已有获取文件或整套装配任务正在进行，请稍候再试。");
            return;
        }

        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            var result = await AcquireLatestAndCheckoutAsync(
                new[] { new BatchOperationItem(node, 0) },
                currentProjectId.Value);
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.RefreshAutomaticDrawingState();
            if (node.Kind == CadDocumentKind.Drawing)
            {
                taskPaneControl.SetAutomaticDrawingOperationResult("已获取工程图权限，可更新、手工编辑或重新执行自动标注。");
            }
            LogOperation(string.Concat(
                "Checkout completed files=", result.CheckedOutFiles,
                " updated=", result.UpdatedFiles,
                " path=", node.FullPath));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            ScheduleTreeRefresh();
        }
    }

    private void OnBatchOperationRequested(object sender, EventArgs eventArgs)
    {
        OpenBatchOperationDialog(null, BatchOperationKind.AcquireLatestAndCheckout);
    }

    private async void OnBatchPropertyEditRequested(object sender, EventArgs eventArgs)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。");
            return;
        }
        if (currentTree == null || currentTree.IsHistoricalPreview)
        {
            ShowError("请先打开当前工作装配，历史版本不能批量填写属性。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("请先选择当前项目。");
            return;
        }

        RefreshLoadedDocumentModificationFlags(currentTree);
        var operationItems = BuildBatchOperationItems(currentTree)
            .Where(item => !string.IsNullOrWhiteSpace(item.Node.FullPath) && File.Exists(item.Node.FullPath))
            .ToArray();
        if (operationItems.Length == 0)
        {
            ShowError("当前结构中没有可编辑的SolidWorks图档。");
            return;
        }

        try
        {
            await ValidateBatchProjectAsync(operationItems, projectId.Value);
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var editItems = BuildBatchPropertyEditItems(operationItems, projectDocuments);
            using (var dialog = new BatchPropertyEditDialog(editItems, projectDocuments))
            {
                if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
                {
                    return;
                }

                var changedItems = dialog.ChangedItems;
                var selectedOperationItems = changedItems.Select(item => item.OperationItem).ToArray();
                IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions =
                    new Dictionary<string, RegistrationDecision>(StringComparer.OrdinalIgnoreCase);
                var newDocumentCount = selectedOperationItems.Count(item => !item.Node.DocumentId.HasValue);
                if (newDocumentCount > 0)
                {
                    if (!ConfirmNewDocumentProject(projectId.Value, newDocumentCount)) return;
                    registrationDecisions = await ReviewRegistrationDuplicatesAsync(selectedOperationItems, projectId.Value);
                    if (registrationDecisions == null) return;
                    await ValidateBatchProjectAsync(selectedOperationItems, projectId.Value);
                }
                if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
                {
                    ShowError("已有获取文件、存档或批量属性任务正在进行，请稍候再试。");
                    return;
                }
                if (Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
                {
                    Interlocked.Exchange(ref workspaceOperationInProgress, 0);
                    ShowError("已有提交存档任务正在进行，请稍候再试。");
                    return;
                }

                Interlocked.Increment(ref refreshSuppressionDepth);
                var previousContext = SynchronizationContext.Current;
                var contextInstalled = false;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new TaskPaneSynchronizationContext(taskPaneControl));
                    contextInstalled = true;
                    taskPaneControl.UseWaitCursor = true;
                    var acquire = await AcquireLatestAndCheckoutAsync(selectedOperationItems, projectId.Value, registrationDecisions);
                    var identities = new Dictionary<string, BatchDocumentIdentity>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in changedItems)
                    {
                        ApplyBatchPropertyEdit(item);
                        identities[item.OperationItem.Node.FullPath] = new BatchDocumentIdentity(item.DrawingNumber, item.Name);
                    }

                    var result = await CheckInBatchAsync(
                        selectedOperationItems,
                        projectId.Value,
                        dialog.ChangeNote,
                        null,
                        lifetime.Token,
                        identities);
                    var message = string.Concat(
                        "批量属性处理完成。",
                        "\r\n获取或确认编辑权限：", acquire.CheckedOutFiles, "个",
                        "\r\n生成新版本：", result.CreatedVersions, "个",
                        "\r\n无变更：", result.UnchangedFiles, "个",
                        "\r\n失败：", result.Failures.Count, "个");
                    if (result.Failures.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.Failures.Take(8)));
                    }
                    MessageBox.Show(taskPaneControl, message, "UPTON PDM", MessageBoxButtons.OK,
                        result.Failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    RememberExplicitProjectPaths(selectedOperationItems, projectId.Value);
                    currentProjectId = projectId.Value;
                    taskPaneControl.SelectProject(projectId.Value);
                }
                finally
                {
                    taskPaneControl.UseWaitCursor = false;
                    if (contextInstalled)
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }
                    Interlocked.Decrement(ref refreshSuppressionDepth);
                    Interlocked.Exchange(ref checkInOperationInProgress, 0);
                    Interlocked.Exchange(ref workspaceOperationInProgress, 0);
                    ScheduleTreeRefresh();
                }
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnBatchPropertyEditRequested", exception);
            ShowError(exception.Message);
        }
    }

    private IReadOnlyList<BatchPropertyEditItem> BuildBatchPropertyEditItems(
        IReadOnlyList<BatchOperationItem> operationItems,
        IReadOnlyList<DocumentDto> projectDocuments)
    {
        var documentsById = (projectDocuments ?? Array.Empty<DocumentDto>()).ToDictionary(document => document.Id);
        var result = new List<BatchPropertyEditItem>();
        foreach (var operationItem in operationItems)
        {
            var node = operationItem.Node;
            documentsById.TryGetValue(node.DocumentId ?? Guid.Empty, out var pdmDocument);
            var properties = ReadBatchPropertyValues(node);
            result.Add(new BatchPropertyEditItem(
                operationItem,
                IdentityPropertyValue(properties, "图号", pdmDocument?.DrawingNumber, Path.GetFileNameWithoutExtension(node.FileName)),
                IdentityPropertyValue(properties, "名称", pdmDocument?.Name, node.DisplayName ?? Path.GetFileNameWithoutExtension(node.FileName)),
                PropertyValue(properties, "材料", string.Empty),
                PropertyValue(properties, "规格", string.Empty),
                PropertyValue(properties, "备注", string.Empty)));
        }
        return result;
    }

    private IReadOnlyDictionary<string, string> ReadBatchPropertyValues(CadTreeNode node)
    {
        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            document = FindLoadedDocument(node.FullPath);
            if (document == null)
            {
                document = OpenDocumentSilentlyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration ?? string.Empty);
                openedForBatch = true;
            }
            return ReadModelProperties(document, node.Configuration);
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
        }
    }

    private static string PropertyValue(IReadOnlyDictionary<string, string> properties, string name, string fallback)
    {
        return properties != null && properties.TryGetValue(string.Concat("全局/", name), out var value)
            ? value ?? string.Empty
            : fallback ?? string.Empty;
    }

    private static string IdentityPropertyValue(
        IReadOnlyDictionary<string, string> properties,
        string name,
        string pdmValue,
        string fallback)
    {
        return !string.IsNullOrWhiteSpace(pdmValue)
            ? pdmValue
            : PropertyValue(properties, name, fallback);
    }

    private void ApplyBatchPropertyEdit(BatchPropertyEditItem item)
    {
        var node = item.OperationItem.Node;
        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            document = FindLoadedDocument(node.FullPath);
            if (document == null)
            {
                document = OpenDocumentSilentlyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration ?? string.Empty);
                openedForBatch = true;
            }
            if (document == null || !PathsEqual(document.GetPathName(), node.FullPath))
            {
                throw new IOException(string.Concat(node.FileName, "未能安全加载，属性未写入。"));
            }
            EnsureDocumentEditable(document, node.FullPath);
            var manager = document.Extension.CustomPropertyManager[string.Empty];
            SetGlobalCustomProperty(manager, "图号", item.DrawingNumber, item.OriginalDrawingNumber);
            SetGlobalCustomProperty(manager, "名称", item.Name, item.OriginalName);
            SetGlobalCustomProperty(manager, "材料", item.Material, item.OriginalMaterial);
            SetGlobalCustomProperty(manager, "规格", item.Specification, item.OriginalSpecification);
            SetGlobalCustomProperty(manager, "备注", item.Remark, item.OriginalRemark);

            var saveErrors = 0;
            var saveWarnings = 0;
            var saved = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
            if (!saved || saveErrors != 0)
            {
                throw new IOException(string.Concat(node.FileName, "保存属性失败，错误码：", saveErrors, "，警告码：", saveWarnings));
            }

            if (!string.Equals(item.Name?.Trim(), item.OriginalName?.Trim(), StringComparison.Ordinal))
            {
                foreach (var matching in EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, node.FullPath)))
                {
                    matching.DisplayName = item.Name.Trim();
                }
            }
            node.WorkState = CadWorkState.PendingCheckIn;
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
        }
    }

    private static void SetGlobalCustomProperty(CustomPropertyManager manager, string name, string value, string originalValue)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.Equals(normalized, originalValue?.Trim() ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            manager.Set2(name, normalized);
        }
        else
        {
            manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, normalized, (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
        }

        var raw = string.Empty;
        var resolved = string.Empty;
        var wasResolved = false;
        var linked = false;
        manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
        if (!string.Equals(raw ?? string.Empty, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Concat("SolidWorks属性“", name, "”写入失败。"));
        }
    }

    private async void OpenBatchOperationDialog(
        Guid? preferredProjectId,
        BatchOperationKind initialOperation,
        IReadOnlyCollection<string> initiallySelectedPaths = null)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。");
            return;
        }

        if (availableProjects.Count == 0)
        {
            ShowError("当前账号没有可用项目，无法执行整套操作。");
            return;
        }

        if (currentTree == null || currentTree.IsHistoricalPreview)
        {
            ShowError("请先打开当前工作装配，历史版本不能执行整套操作。");
            return;
        }

        RefreshLoadedDocumentModificationFlags(currentTree);
        var items = BuildBatchOperationItems(currentTree);
        if (items.Count == 0)
        {
            ShowError("当前结构中没有可操作的SolidWorks图档。");
            return;
        }

        var initialProjectId = preferredProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        using (var dialog = new BatchOperationDialog(
            items,
            availableProjects,
            initialProjectId,
            authenticatedUsername,
            initialOperation,
            initiallySelectedPaths))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
            {
                return;
            }

            var selectedProjectId = dialog.SelectedProjectId.Value;
            var selectedProjectDisplay = dialog.SelectedProjectDisplay;
            IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions;
            try
            {
                await ValidateBatchProjectAsync(dialog.SelectedItems, selectedProjectId);
                registrationDecisions = await ReviewRegistrationDuplicatesAsync(dialog.SelectedItems, selectedProjectId);
                if (registrationDecisions == null) return;
                await ValidateBatchProjectAsync(dialog.SelectedItems, selectedProjectId);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
                return;
            }

            if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
            {
                ShowError("已有获取文件或整套装配任务正在进行，请稍候再试。");
                return;
            }

            var ownsCheckInOperation = false;
            if (dialog.Operation == BatchOperationKind.CheckIn)
            {
                if (Volatile.Read(ref openOperationInProgress) > 0
                    || Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
                {
                    Interlocked.Exchange(ref workspaceOperationInProgress, 0);
                    ShowError("正在打开图档或已有提交存档任务，请稍候再试。");
                    return;
                }

                ownsCheckInOperation = true;
            }

            Interlocked.Increment(ref refreshSuppressionDepth);
            BatchProgressDialog progressDialog = null;
            CancellationTokenSource batchCancellation = null;
            CancellationTokenSource batchPreflightTimeout = null;
            SynchronizationContext previousBatchSynchronizationContext = null;
            var batchSynchronizationContextInstalled = false;
            try
            {
                if (dialog.Operation == BatchOperationKind.AcquireLatestAndCheckout)
                {
                    var result = await AcquireLatestAndCheckoutAsync(dialog.SelectedItems, selectedProjectId, registrationDecisions);
                    MessageBox.Show(
                        taskPaneControl,
                        string.Concat(
                            "整套获取完成。\r\n归属项目：", selectedProjectDisplay,
                            "\r\n已获取权限：", result.CheckedOutFiles, "个",
                            "\r\n已更新本地文件：", result.UpdatedFiles, "个",
                            "\r\n本地已是最新或首次登记：", result.CheckedOutFiles - result.UpdatedFiles, "个"),
                        "UPTON PDM",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    batchPreflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(batchCancellation.Token);
                    batchPreflightTimeout.CancelAfter(TimeSpan.FromMinutes(15));
                    progressDialog = new BatchProgressDialog(dialog.SelectedItems.Count);
                    progressDialog.CancelRequested += (_, __) => batchCancellation.Cancel();
                    progressDialog.Show(taskPaneControl);
                    progressDialog.BringToFront();
                    progressDialog.Activate();
                    activeBatchProgressDialog = progressDialog;
                    previousBatchSynchronizationContext = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(new TaskPaneSynchronizationContext(taskPaneControl));
                    batchSynchronizationContextInstalled = true;
                    progressDialog.SetStage("正在检查文件并准备编辑权限…");

                    PreparePendingRenamesForCheckIn(dialog.SelectedItems.Select(item => item.Node));

                    LogOperation(string.Concat("Batch check-in plan start selected=", dialog.SelectedItems.Count));
                    var checkInPlan = await BuildBatchCheckInPlanAsync(
                        dialog.SelectedItems,
                        progressDialog.SetStage,
                        batchPreflightTimeout.Token);
                    LogOperation(string.Concat(
                        "Batch check-in plan end included=", checkInPlan.Items.Count,
                        " skipped=", checkInPlan.SkippedFiles));
                    var preparedPermissions = await PrepareBatchCheckInPermissionsAsync(
                        checkInPlan.Items,
                        selectedProjectId,
                        progressDialog.SetStage,
                        batchPreflightTimeout.Token,
                        registrationDecisions);
                    batchPreflightTimeout.Dispose();
                    batchPreflightTimeout = null;

                    progressDialog.SetStage("正在提交整套文件…");
                    LogOperation(string.Concat("Batch check-in execution start items=", checkInPlan.Items.Count));
                    var result = await CheckInBatchAsync(
                        checkInPlan.Items,
                        selectedProjectId,
                        dialog.ChangeNote,
                        progressDialog.ReportFile,
                        batchCancellation.Token);
                    LogOperation(string.Concat(
                        "Batch check-in execution end created=", result.CreatedVersions,
                        " unchanged=", result.UnchangedFiles,
                        " failures=", result.Failures.Count));
                    var message = string.Concat(
                        "归属项目：", selectedProjectDisplay,
                        "\r\n自动登记/准备权限：", preparedPermissions, "个",
                        "\r\n生成新版本：", result.CreatedVersions, "个",
                        "\r\n无变更并结束编辑：", result.UnchangedFiles, "个",
                        "\r\n未变更且未获取权限：", checkInPlan.SkippedFiles, "个",
                        "\r\n失败：", result.Failures.Count, "个");
                    if (result.Failures.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.Failures.Take(8)));
                    }

                    progressDialog.Complete(message, result.Failures.Count > 0);
                }

                RememberExplicitProjectPaths(dialog.SelectedItems, selectedProjectId);
                currentProjectId = selectedProjectId;
                taskPaneControl.SelectProject(selectedProjectId);
            }
            catch (OperationCanceledException) when (
                batchCancellation != null && batchCancellation.IsCancellationRequested
                || batchPreflightTimeout != null && batchPreflightTimeout.IsCancellationRequested)
            {
                var preflightTimedOut = batchPreflightTimeout != null
                    && batchPreflightTimeout.IsCancellationRequested
                    && (batchCancellation == null || !batchCancellation.IsCancellationRequested);
                LogOperation(preflightTimedOut ? "Batch check-in preflight timed out." : "Batch check-in cancelled.");
                if (progressDialog != null && !progressDialog.IsDisposed)
                {
                    progressDialog.Fail(preflightTimedOut
                        ? "检查文件和准备编辑权限超过15分钟，整套提交已停止；本次新获取的权限已回滚。"
                        : "整套提交已取消。已成功提交的文件不会回退；已获取权限但尚未提交的文件仍保持可编辑，可稍后重试或放弃编辑。 ");
                }
            }
            catch (Exception exception)
            {
                LogDiagnostic("OnBatchOperationRequested", exception);
                if (progressDialog != null && !progressDialog.IsDisposed)
                {
                    progressDialog.Fail(exception.Message);
                }
                else
                {
                    ShowError(exception.Message);
                }
            }
            finally
            {
                if (batchSynchronizationContextInstalled)
                {
                    SynchronizationContext.SetSynchronizationContext(previousBatchSynchronizationContext);
                }

                if (ReferenceEquals(activeBatchProgressDialog, progressDialog))
                {
                    activeBatchProgressDialog = null;
                }

                batchPreflightTimeout?.Dispose();
                batchCancellation?.Dispose();
                Interlocked.Decrement(ref refreshSuppressionDepth);
                if (ownsCheckInOperation)
                {
                    Interlocked.Exchange(ref checkInOperationInProgress, 0);
                }

                Interlocked.Exchange(ref workspaceOperationInProgress, 0);
                ScheduleTreeRefresh();
            }
        }
    }

    private async Task<WorkspaceAcquireResult> AcquireLatestAndCheckoutAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null)
    {
        var items = DistinctBatchItems(selectedItems);
        ValidateBatchFileNames(items);
        var updatePlans = new List<WorkspaceUpdatePlan>();
        var stagedPaths = new List<string>();
        var rootPath = currentTree?.FullPath ?? string.Empty;
        var rootConfiguration = currentTree?.Configuration ?? string.Empty;
        var rootKind = currentTree?.Kind ?? CadDocumentKind.Other;
        var closedRoot = false;

        try
        {
            foreach (var item in items)
            {
                var node = item.Node;
                ValidateAcquireNode(node);
                if (!node.DocumentId.HasValue)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(node.CheckedOutBy)
                    && !string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Concat(node.FileName, "正在由", node.CheckedOutBy, "编辑，整套获取已停止。"));
                }

                var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
                var latest = versions.FirstOrDefault();
                ApplyLatestVersion(node, latest);
                if (latest == null)
                {
                    if (!File.Exists(node.FullPath))
                    {
                        throw new FileNotFoundException(string.Concat(node.FileName, "尚无PDM存档版本，本地文件也不存在。"));
                    }

                    continue;
                }

                var needsDownload = !File.Exists(node.FullPath);
                if (!needsDownload)
                {
                    if (node.IsModifiedInSolidWorks)
                    {
                        throw new InvalidOperationException(string.Concat(node.FileName, "存在未保存修改，不能用PDM版本更新。请先保存或放弃修改。"));
                    }

                    var localSha256 = ComputeFileHash(node.FullPath);
                    if (VersionMatchesLocalFile(latest, node.FullPath, localSha256))
                    {
                        continue;
                    }

                    if (!versions.Any(version => VersionMatchesLocalFile(version, node.FullPath, localSha256)))
                    {
                        throw new InvalidOperationException(string.Concat(
                            node.FileName,
                            "的本地内容无法对应任何PDM历史版本，可能包含未存档修改。为避免覆盖，整套获取已停止。"));
                    }

                    needsDownload = true;
                }

                if (needsDownload)
                {
                    var stagedPath = await apiClient.DownloadVersionToWorkspaceStageAsync(
                        node.DocumentId.Value,
                        latest.Id,
                        node.FileName,
                        latest.Sha256,
                        lifetime.Token);
                    stagedPaths.Add(stagedPath);
                    updatePlans.Add(new WorkspaceUpdatePlan(node, latest, stagedPath));
                }
            }

            if (updatePlans.Count > 0)
            {
                EnsureWorkspaceDocumentsAreSaved();
                closedRoot = FindLoadedDocument(rootPath) != null;
                CloseDocumentsForWorkspaceUpdate(updatePlans.Select(plan => plan.Node.FullPath), rootPath);
                ApplyWorkspaceUpdates(updatePlans);
            }

            foreach (var item in items.Where(item => !item.Node.DocumentId.HasValue))
            {
                var node = item.Node;
                ApplyDrawingModelRelation(node);
                var decision = RegistrationDecisionFor(node, registrationDecisions);
                var registered = await apiClient.RegisterDocumentAsync(
                    projectId,
                    node,
                    decision.SourceSha256,
                    decision.AllowDuplicateContent,
                    decision.DuplicateReason,
                    lifetime.Token);
                ApplyRegisteredDocumentToMatchingInstances(node, registered);
            }

            var checkedOut = 0;
            var newlyCheckedOut = new List<CadTreeNode>();
            try
            {
                foreach (var item in items)
                {
                    var node = item.Node;
                    var wasAlreadyCheckedOut = IsCheckedOutByCurrentUser(node);
                    var document = await apiClient.CheckoutAsync(node.DocumentId.Value, checkoutSessionId, checkoutMachineName, lifetime.Token);
                    ApplyCheckoutDocument(node, document);
                    node.WorkState = CadWorkState.Editable;
                    if (!wasAlreadyCheckedOut)
                    {
                        newlyCheckedOut.Add(node);
                    }
                    SetFileReadOnly(node.FullPath, false);
                    EnsureLoadedDocumentEditable(node.FullPath);

                    checkedOut++;
                }
            }
            catch
            {
                foreach (var node in newlyCheckedOut.AsEnumerable().Reverse())
                {
                    try
                    {
                        var discarded = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
                        ApplyCheckoutDocument(node, discarded);
                        node.WorkState = CadWorkState.None;
                        ProtectLoadedDocument(node.FullPath);
                    }
                    catch (Exception rollbackException)
                    {
                        LogDiagnostic(string.Concat("Acquire rollback.", node.FileName), rollbackException);
                    }
                }

                throw;
            }

            return new WorkspaceAcquireResult(checkedOut, updatePlans.Count);
        }
        finally
        {
            foreach (var stagedPath in stagedPaths)
            {
                DeleteWorkspaceStage(stagedPath);
            }

            if (closedRoot && File.Exists(rootPath))
            {
                OpenOrActivateDocumentOnSolidWorksThread(rootPath, ToSolidWorksDocumentType(rootKind), rootConfiguration);
            }
        }
    }

    private static IReadOnlyList<BatchOperationItem> DistinctBatchItems(IEnumerable<BatchOperationItem> items)
    {
        return items
            .Where(item => item?.Node != null)
            .GroupBy(item => item.Node.FullPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Depth).First())
            .ToArray();
    }

    private async Task ValidateBatchProjectAsync(IReadOnlyList<BatchOperationItem> selectedItems, Guid projectId)
    {
        var registeredIds = new HashSet<Guid>(selectedItems
            .Where(item => item?.Node?.DocumentId != null)
            .Select(item => item.Node.DocumentId.Value));
        if (registeredIds.Count == 0)
        {
            return;
        }

        var projectDocuments = await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
        var projectDocumentIds = new HashSet<Guid>(projectDocuments.Select(document => document.Id));
        var mismatch = selectedItems.FirstOrDefault(item =>
            item?.Node?.DocumentId != null && !projectDocumentIds.Contains(item.Node.DocumentId.Value));
        if (mismatch != null)
        {
            throw new InvalidOperationException(string.Concat(
                mismatch.Node.FileName,
                "已登记在其他项目，不能归入当前所选项目。请核对项目号后重试。"));
        }
    }

    private bool ConfirmNewDocumentProject(Guid projectId, int documentCount)
    {
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (project == null) throw new InvalidOperationException("所选项目已不可用，请刷新后重试。");
        var confirmationCode = project.ParentProjectId.HasValue ? project.Code : string.Concat(project.Code, "-0");
        var display = project.ParentProjectId.HasValue
            ? project.ToString()
            : string.Concat(confirmationCode, " · 主项目图档");
        using (var dialog = new ProjectAdmissionConfirmationDialog(confirmationCode, display, documentCount))
        {
            return dialog.ShowDialog(taskPaneControl) == DialogResult.OK;
        }
    }

    private async Task<IReadOnlyDictionary<string, RegistrationDecision>> ReviewRegistrationDuplicatesAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId)
    {
        var nodes = DistinctBatchItems(selectedItems)
            .Select(item => item.Node)
            .Where(node => !node.DocumentId.HasValue)
            .ToArray();
        if (nodes.Length == 0) return new Dictionary<string, RegistrationDecision>(StringComparer.OrdinalIgnoreCase);

        var candidates = await Task.Run(() => nodes.Select(node => new DocumentRegistrationCandidateDto
        {
            CandidateKey = node.NodeId.ToString("N"),
            FileName = node.FileName,
            Kind = (int)node.Kind,
            SourceSha256 = ComputeFileHash(node.FullPath)
        }).ToArray(), lifetime.Token);
        var matches = await apiClient.PreflightDocumentRegistrationAsync(projectId, candidates, lifetime.Token);
        var nodesByKey = nodes.ToDictionary(node => node.NodeId.ToString("N"), StringComparer.OrdinalIgnoreCase);
        var candidatesByKey = candidates.ToDictionary(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase);
        var blockers = matches
            .Where(match => (RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.SameNameDifferentContent)
            .Select(match => string.Concat(
                nodesByKey.TryGetValue(match.CandidateKey, out var node) ? node.FileName : match.CandidateKey,
                " 与库中同名图档内容不同"))
            .ToArray();
        if (blockers.Length > 0)
        {
            throw new InvalidOperationException(string.Concat(
                "发现同名但内容不同的图档，已阻止入库：\r\n",
                string.Join("\r\n", blockers.Take(12)),
                "\r\n请引用库中现有图档、修改本地图号/文件名，或对已有图档获取权限后按新版本提交。"));
        }

        List<DocumentDto> projectDocuments = null;
        var decisions = new Dictionary<string, RegistrationDecision>(StringComparer.OrdinalIgnoreCase);
        var reused = 0;
        var independent = 0;
        foreach (var match in matches)
        {
            if (!nodesByKey.TryGetValue(match.CandidateKey, out var node)
                || !candidatesByKey.TryGetValue(match.CandidateKey, out var candidate))
                continue;

            var matchKind = (RegistrationMatchKind)match.MatchKind;
            if (matchKind == RegistrationMatchKind.New)
            {
                decisions[node.FullPath] = new RegistrationDecision(candidate.SourceSha256, false, null);
                continue;
            }

            if (matchKind == RegistrationMatchKind.SameNameSameContent)
            {
                projectDocuments ??= await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
                var existing = projectDocuments.FirstOrDefault(document => document.Id == match.ExistingDocumentId)
                    ?? throw new InvalidOperationException(string.Concat("重复预检命中的图档已发生变化：", match.ExistingFileName));
                ApplyRegisteredDocumentToMatchingInstances(node, existing);
                reused++;
                continue;
            }

            if (matchKind == RegistrationMatchKind.SameContentDifferentName)
            {
                var choice = MessageBox.Show(
                    taskPaneControl,
                    string.Concat(
                        "本地图档：", node.FileName,
                        "\r\n库中图档：", match.ExistingFileName, "（", match.ExistingDrawingNumber, "，", match.ExistingRevision, "）",
                        "\r\n两者内容完全相同。\r\n\r\n是：引用库中已有图档（推荐）\r\n否：作为独立图档登记并填写原因\r\n取消：停止本次操作"),
                    "发现内容完全相同的图档",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);
                if (choice == DialogResult.Cancel) return null;
                if (choice == DialogResult.Yes)
                {
                    projectDocuments ??= await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
                    var existing = projectDocuments.FirstOrDefault(document => document.Id == match.ExistingDocumentId)
                        ?? throw new InvalidOperationException(string.Concat("重复预检命中的图档已发生变化：", match.ExistingFileName));
                    ApplyRegisteredDocumentToMatchingInstances(node, existing);
                    reused++;
                    continue;
                }

                using (var reasonDialog = new DuplicateRegistrationReasonDialog(node.FileName, match.ExistingFileName))
                {
                    if (reasonDialog.ShowDialog(taskPaneControl) != DialogResult.OK) return null;
                    decisions[node.FullPath] = new RegistrationDecision(candidate.SourceSha256, true, reasonDialog.Reason);
                    independent++;
                }
                continue;
            }

            if (matchKind == RegistrationMatchKind.SameContentOtherProject)
            {
                var proceed = MessageBox.Show(
                    taskPaneControl,
                    string.Concat(
                        "本地图档“", node.FileName, "”与其他项目中的图档内容完全相同。",
                        "\r\n已有图档：", match.ExistingProjectCode, " · ", match.ExistingProjectName, " / ", match.ExistingFileName,
                        "\r\n当前版本不支持把项目专用图档自动跨项目复用。是否仍在已确认的目标项目中独立登记？"),
                    "发现跨项目重复内容",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);
                if (proceed != DialogResult.OK) return null;
                using (var reasonDialog = new DuplicateRegistrationReasonDialog(node.FileName, match.ExistingFileName))
                {
                    if (reasonDialog.ShowDialog(taskPaneControl) != DialogResult.OK) return null;
                    decisions[node.FullPath] = new RegistrationDecision(candidate.SourceSha256, true, reasonDialog.Reason);
                    independent++;
                }
            }
        }

        if (reused > 0 || independent > 0)
        {
            MessageBox.Show(
                taskPaneControl,
                string.Concat("重复图档预检完成。\r\n引用已有图档：", reused, "个\r\n确认独立登记：", independent, "个"),
                "重复图档预检",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        return decisions;
    }

    private static RegistrationDecision RegistrationDecisionFor(
        CadTreeNode node,
        IReadOnlyDictionary<string, RegistrationDecision> decisions)
    {
        if (decisions != null
            && !string.IsNullOrWhiteSpace(node?.FullPath)
            && decisions.TryGetValue(node.FullPath, out var decision))
            return decision;
        return new RegistrationDecision(ComputeFileHash(node.FullPath), false, null);
    }

    private void RememberExplicitProjectPaths(IEnumerable<BatchOperationItem> items, Guid projectId)
    {
        foreach (var item in items ?? Array.Empty<BatchOperationItem>())
        {
            RememberExplicitProjectPath(item?.Node?.FullPath, projectId);
        }
    }

    private void RememberExplicitProjectPath(string fullPath, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        explicitProjectPaths[Path.GetFullPath(fullPath)] = projectId;
    }

    private Guid? GetExplicitProjectId(string fullPath)
    {
        Guid projectId;
        return !string.IsNullOrWhiteSpace(fullPath)
            && explicitProjectPaths.TryGetValue(Path.GetFullPath(fullPath), out projectId)
            && availableProjects.Any(project => project.Id == projectId)
                ? projectId
                : (Guid?)null;
    }

    private static void ValidateBatchFileNames(IReadOnlyList<BatchOperationItem> items)
    {
        var conflict = items
            .GroupBy(item => item.Node.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group
                .Select(item => item.Node.FullPath ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
        if (conflict != null)
        {
            throw new InvalidOperationException(string.Concat(
                "检测到不同路径的同名文件：", conflict.Key,
                "。当前PDM以项目内文件名识别图档，请先处理同名冲突。"));
        }
    }

    private static void ValidateAcquireNode(CadTreeNode node)
    {
        if (node.IsHistoricalPreview)
        {
            throw new InvalidOperationException("历史版本不能获取编辑权限。");
        }

        if (ToSolidWorksDocumentType(node.Kind) == (int)swDocumentTypes_e.swDocNONE)
        {
            throw new InvalidOperationException(string.Concat(node.FileName, "不是受支持的SolidWorks图档。"));
        }

        if (string.IsNullOrWhiteSpace(node.FullPath))
        {
            throw new InvalidOperationException(string.Concat(node.FileName, "没有可用的本地工作路径。"));
        }

        if (!node.DocumentId.HasValue && !File.Exists(node.FullPath))
        {
            throw new FileNotFoundException(string.Concat(node.FileName, "尚未入库且本地文件不存在。"));
        }
    }

    private void EnsureWorkspaceDocumentsAreSaved()
    {
        var treePaths = new HashSet<string>(
            EnumerateCadNodes(currentTree)
                .Select(node => node.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var documents = application.GetDocuments() as Array;
        if (documents == null)
        {
            return;
        }

        foreach (var item in documents)
        {
            if (item is IModelDoc2 document
                && treePaths.Contains(document.GetPathName() ?? string.Empty)
                && document.GetSaveFlag())
            {
                throw new InvalidOperationException(string.Concat(
                    Path.GetFileName(document.GetPathName()),
                    "存在未保存修改。请先保存或放弃修改，再获取PDM最新版本。"));
            }
        }
    }

    private void CloseDocumentsForWorkspaceUpdate(IEnumerable<string> updatePaths, string rootPath)
    {
        var targets = new HashSet<string>(updatePaths, StringComparer.OrdinalIgnoreCase);
        var rootDocument = FindLoadedDocument(rootPath);
        if (rootDocument != null)
        {
            application.CloseDoc(rootDocument.GetTitle());
        }

        foreach (var path in targets)
        {
            var loaded = FindLoadedDocument(path);
            if (loaded != null)
            {
                application.CloseDoc(loaded.GetTitle());
            }
        }

        var stillLoaded = targets.FirstOrDefault(path => FindLoadedDocument(path) != null);
        if (stillLoaded != null)
        {
            throw new IOException(string.Concat(Path.GetFileName(stillLoaded), "仍被SolidWorks占用，未更新本地工作文件。"));
        }
    }

    private static void ApplyWorkspaceUpdates(IReadOnlyList<WorkspaceUpdatePlan> plans)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(
            WorkspaceSettingsStore.GetWorkspaceRoot(),
            "_backup",
            timestamp);
        Directory.CreateDirectory(backupRoot);
        var completed = new List<WorkspaceUpdatePlan>();
        try
        {
            foreach (var plan in plans)
            {
                var targetDirectory = Path.GetDirectoryName(plan.Node.FullPath);
                if (string.IsNullOrWhiteSpace(targetDirectory))
                {
                    throw new IOException(string.Concat(plan.Node.FileName, "的工作路径无效。"));
                }

                Directory.CreateDirectory(targetDirectory);
                if (File.Exists(plan.Node.FullPath))
                {
                    plan.OriginalAttributes = File.GetAttributes(plan.Node.FullPath);
                    plan.BackupPath = Path.Combine(
                        backupRoot,
                        string.Concat(plan.Node.DocumentId?.ToString("N") ?? Guid.NewGuid().ToString("N"), "_", plan.Node.FileName));
                    File.Copy(plan.Node.FullPath, plan.BackupPath, true);
                    SetFileReadOnly(plan.Node.FullPath, false);
                }

                completed.Add(plan);
                File.Copy(plan.StagedPath, plan.Node.FullPath, true);
                SetFileReadOnly(plan.Node.FullPath, true);
                var actualSha256 = ComputeFileHash(plan.Node.FullPath);
                if (!string.Equals(actualSha256, plan.Version.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(string.Concat(plan.Node.FileName, "更新后的文件校验失败。"));
                }
            }
        }
        catch
        {
            foreach (var plan in completed.AsEnumerable().Reverse())
            {
                SetFileReadOnly(plan.Node.FullPath, false);
                if (!string.IsNullOrWhiteSpace(plan.BackupPath) && File.Exists(plan.BackupPath))
                {
                    File.Copy(plan.BackupPath, plan.Node.FullPath, true);
                    if (plan.OriginalAttributes.HasValue)
                    {
                        File.SetAttributes(plan.Node.FullPath, plan.OriginalAttributes.Value);
                    }
                }
                else if (File.Exists(plan.Node.FullPath))
                {
                    File.Delete(plan.Node.FullPath);
                }
            }

            throw;
        }
    }

    private static void DeleteWorkspaceStage(string stagedPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(stagedPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // Temporary workspace staging is best-effort cleanup.
        }
    }

    private static void SetFileReadOnly(string path, bool readOnly)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
    }

    private void EnsureLoadedDocumentEditable(string path)
    {
        var document = FindLoadedDocument(path);
        if (document != null)
        {
            EnsureDocumentEditable(document, path);
        }
    }

    private static void EnsureDocumentEditable(IModelDoc2 document, string path)
    {
        SetFileReadOnly(path, false);
        if (!document.IsOpenedReadOnly())
        {
            return;
        }

        if (!document.SetReadOnlyState(false) || document.IsOpenedReadOnly())
        {
            throw new IOException(string.Concat(
                Path.GetFileName(path),
                "已获取PDM编辑权限，但SolidWorks仍以只读方式打开。请关闭该图档后重新获取权限。"));
        }
    }

    private void ProtectLoadedDocument(string path)
    {
        SetFileReadOnly(path, true);
        var document = FindLoadedDocument(path);
        if (document == null || document.IsOpenedReadOnly())
        {
            return;
        }

        try
        {
            if (!document.SetReadOnlyState(true) || !document.IsOpenedReadOnly())
            {
                LogDiagnostic(
                    string.Concat("ProtectLoadedDocument.", Path.GetFileName(path)),
                    new IOException("存档已完成，但SolidWorks未能把当前文档切换为只读状态。"));
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic(string.Concat("ProtectLoadedDocument.", Path.GetFileName(path)), exception);
        }
    }

    private static IReadOnlyList<BatchOperationItem> BuildBatchOperationItems(CadTreeNode root)
    {
        var result = new List<BatchOperationItem>();
        var itemsByPath = new Dictionary<string, BatchOperationItem>(StringComparer.OrdinalIgnoreCase);
        CollectBatchOperationItems(root, 0, result, itemsByPath, Array.Empty<CadTreeNode>());
        return result;
    }

    private void RefreshLoadedDocumentModificationFlags(CadTreeNode root)
    {
        var documents = application?.GetDocuments() as Array;
        if (root == null || documents == null)
        {
            return;
        }

        var modifiedByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in documents)
        {
            try
            {
                if (item is IModelDoc2 document)
                {
                    var path = document.GetPathName() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        modifiedByPath[path] = document.GetSaveFlag();
                    }
                }
            }
            catch (Exception exception)
            {
                LogDiagnostic("Read loaded document modification state", exception);
            }
        }

        foreach (var node in EnumerateCadNodes(root))
        {
            if (string.IsNullOrWhiteSpace(node.FullPath)
                || !modifiedByPath.TryGetValue(node.FullPath, out var modified))
            {
                continue;
            }

            node.IsModifiedInSolidWorks = modified;
            if (modified)
            {
                node.WorkState = CadWorkState.ModifiedUnsaved;
            }
            else if (node.WorkState == CadWorkState.ModifiedUnsaved)
            {
                node.WorkState = string.IsNullOrWhiteSpace(node.CheckedOutBy)
                    ? CadWorkState.None
                    : CadWorkState.Editable;
            }
        }
    }

    private void PreparePendingRenamesForCheckIn(IEnumerable<CadTreeNode> selectedNodes)
    {
        var selected = (selectedNodes ?? Array.Empty<CadTreeNode>())
            .Where(node => node != null)
            .GroupBy(node => node.FullPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var pending = selected.Where(node => node.IsRenamePendingSave).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        if (currentTree == null
            || !selected.Any(node => PathsEqual(node.FullPath, currentTree.FullPath)))
        {
            throw new InvalidOperationException("重命名会修改总装引用，请同时勾选主装配体后再提交存档。");
        }

        var pendingDocuments = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in pending)
        {
            if (!IsCheckedOutByCurrentUser(node))
            {
                throw new InvalidOperationException(string.Concat(node.FileName, "尚未获取当前用户的编辑权限，不能保存重命名。"));
            }

            var document = FindLoadedDocument(node.FullPath)
                ?? throw new InvalidOperationException(string.Concat(node.FileName, "未在SolidWorks中加载，不能完成重命名保存。"));
            EnsureDocumentEditable(document, node.FullPath);
            pendingDocuments[node.FullPath] = document;
        }

        var rootDocument = FindLoadedDocument(currentTree.FullPath)
            ?? throw new InvalidOperationException("主装配体未在SolidWorks中加载，不能保存重命名引用。");
        if (!IsCheckedOutByCurrentUser(currentTree))
        {
            throw new InvalidOperationException("主装配体尚未获取当前用户的编辑权限，不能保存重命名引用。");
        }

        EnsureDocumentEditable(rootDocument, currentTree.FullPath);
        rootDocument.Extension.Rebuild((int)swRebuildOptions_e.swRebuildAll);
        var rootErrors = 0;
        var rootWarnings = 0;
        var saveOptions = (int)swSaveAsOptions_e.swSaveAsOptions_Silent
            | (int)swSaveAsOptions_e.swSaveAsOptions_SaveReferenced;
        var rootSaved = rootDocument.Save3(saveOptions, ref rootErrors, ref rootWarnings);
        var unsavedRenames = pending
            .Where(node => !File.Exists(node.FullPath)
                || !pendingDocuments.TryGetValue(node.FullPath, out var document)
                || document.GetSaveFlag())
            .Select(node => node.FileName)
            .ToArray();
        if (!rootSaved || rootDocument.GetSaveFlag() || unsavedRenames.Length > 0)
        {
            throw new IOException(string.Concat(
                "主装配体保存重命名引用失败，错误码：",
                rootErrors,
                "，警告码：",
                rootWarnings,
                unsavedRenames.Length > 0
                    ? string.Concat("，未完成文件：", string.Join("、", unsavedRenames))
                    : string.Empty,
                "。"));
        }

        if (rootErrors != 0)
        {
            LogOperation(string.Concat(
                "Pending rename SaveReferenced completed with nonfatal flags errors=",
                rootErrors,
                " warnings=",
                rootWarnings));
        }

        foreach (var node in pending)
        {
            node.IsRenamePendingSave = false;
            node.IsModifiedInSolidWorks = false;
            node.Status = CadReferenceStatus.Normal;
            node.CurrentRevision = "本地修改";
            node.WorkState = CadWorkState.PendingCheckIn;
            if (node.DocumentId.HasValue)
            {
                PdmDocumentIdentityStore.TryWrite(node.FullPath, node.DocumentId.Value);
            }
            LogOperation(string.Concat("Pending rename saved path=", node.FullPath, " document=", node.DocumentId));
        }

        currentTree.IsModifiedInSolidWorks = false;
        currentTree.CurrentRevision = "本地修改";
        currentTree.WorkState = CadWorkState.PendingCheckIn;
        taskPaneControl.SetTree(currentTree);
        LogOperation(string.Concat("Pending rename root references saved path=", currentTree.FullPath));
    }

    private static void CollectBatchOperationItems(
        CadTreeNode node,
        int depth,
        ICollection<BatchOperationItem> target,
        IDictionary<string, BatchOperationItem> itemsByPath,
        IReadOnlyList<CadTreeNode> ancestors)
    {
        if (node == null)
        {
            return;
        }

        var childAncestors = ancestors;
        if (ToSolidWorksDocumentType(node.Kind) != (int)swDocumentTypes_e.swDocNONE
            && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            if (!itemsByPath.TryGetValue(node.FullPath, out var item))
            {
                item = new BatchOperationItem(node, depth);
                itemsByPath.Add(node.FullPath, item);
                target.Add(item);
            }

            item.AddAncestors(ancestors);
            childAncestors = ancestors.Concat(new[] { node }).ToArray();
        }

        foreach (var child in node.Children)
        {
            CollectBatchOperationItems(child, depth + 1, target, itemsByPath, childAncestors);
        }
    }

    private async Task<BatchCheckInPlan> BuildBatchCheckInPlanAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Action<string> reportStage,
        CancellationToken cancellationToken)
    {
        var items = DistinctBatchItems(selectedItems);
        ValidateBatchFileNames(items);
        var checkInItems = new List<BatchOperationItem>();
        var skippedFiles = 0;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var node = item.Node;
            reportStage?.Invoke(string.Concat("正在分析变更 ", index + 1, " / ", items.Count, "：", node.FileName));
            LogOperation(string.Concat("Batch preflight start path=", node.FullPath));
            ValidateAcquireNode(node);

            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                throw new FileNotFoundException(string.Concat(node.FileName, "的本地工作文件不存在，不能提交存档。"));
            }

            if (!string.IsNullOrWhiteSpace(node.CheckedOutBy) && !IsCheckedOutByCurrentUser(node))
            {
                throw new InvalidOperationException(string.Concat(node.FileName, "正在由", node.CheckedOutBy, "编辑，整套提交已停止。"));
            }

            if (!node.DocumentId.HasValue
                || IsCheckedOutByCurrentUser(node)
                || node.IsModifiedInSolidWorks
                || node.WorkState == CadWorkState.ModifiedUnsaved
                || node.WorkState == CadWorkState.PendingCheckIn)
            {
                checkInItems.Add(item);
                LogOperation(string.Concat("Batch preflight include direct path=", node.FullPath, " state=", node.WorkState));
                continue;
            }

            var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = NodeMatchesLatestFile(node, localSha256);
            DocumentVersionDto latest = null;
            if (node.Kind == CadDocumentKind.Assembly || !fileMatchesLatest)
            {
                LogOperation(string.Concat("Batch preflight versions start path=", node.FullPath));
                var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
                latest = versions.FirstOrDefault();
                ApplyLatestVersion(node, latest);
                fileMatchesLatest = latest != null && VersionMatchesLocalFile(latest, node.FullPath, localSha256);
                LogOperation(string.Concat("Batch preflight versions end path=", node.FullPath, " count=", versions.Count));
            }

            var referenceMatchesLatest = node.Kind != CadDocumentKind.Assembly
                || latest != null && ReferenceSnapshotMatchesTree(latest.ReferenceSnapshot, node);
            if (!fileMatchesLatest || !referenceMatchesLatest)
            {
                checkInItems.Add(item);
                LogOperation(string.Concat(
                    "Batch preflight include changed path=", node.FullPath,
                    " fileMatches=", fileMatchesLatest,
                    " referenceMatches=", referenceMatchesLatest));
            }
            else
            {
                skippedFiles++;
                LogOperation(string.Concat("Batch preflight skip unchanged path=", node.FullPath));
            }
        }

        return new BatchCheckInPlan(checkInItems, skippedFiles);
    }

    private async Task<int> PrepareBatchCheckInPermissionsAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        Action<string> reportStage,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null)
    {
        var items = DistinctBatchItems(selectedItems);
        var newlyCheckedOut = new List<CadTreeNode>();
        var preparedPermissions = 0;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = items[index].Node;
                if (IsCheckedOutByCurrentUser(node))
                {
                    continue;
                }

                reportStage?.Invoke(string.Concat("正在准备权限 ", index + 1, " / ", items.Count, "：", node.FileName));
                ValidateAcquireNode(node);
                if (!node.DocumentId.HasValue)
                {
                    LogOperation(string.Concat("Batch check-in register start path=", node.FullPath));
                    ApplyDrawingModelRelation(node);
                    var decision = RegistrationDecisionFor(node, registrationDecisions);
                    var registered = await apiClient.RegisterDocumentAsync(
                        projectId,
                        node,
                        decision.SourceSha256,
                        decision.AllowDuplicateContent,
                        decision.DuplicateReason,
                        cancellationToken);
                    ApplyRegisteredDocumentToMatchingInstances(node, registered);
                    LogOperation(string.Concat("Batch check-in register end path=", node.FullPath, " document=", node.DocumentId));
                }

                LogOperation(string.Concat("Batch check-in checkout start path=", node.FullPath));
                var document = await apiClient.CheckoutAsync(node.DocumentId.Value, checkoutSessionId, checkoutMachineName, cancellationToken);
                ApplyCheckoutDocument(node, document);
                node.WorkState = CadWorkState.Editable;
                newlyCheckedOut.Add(node);
                preparedPermissions++;
                LogOperation(string.Concat("Batch check-in checkout end path=", node.FullPath));

                SetFileReadOnly(node.FullPath, false);
                LogOperation(string.Concat("Batch check-in editable-state start path=", node.FullPath));
                EnsureLoadedDocumentEditable(node.FullPath);
                LogOperation(string.Concat("Batch check-in editable-state end path=", node.FullPath));
            }

            return preparedPermissions;
        }
        catch
        {
            foreach (var node in newlyCheckedOut.AsEnumerable().Reverse())
            {
                try
                {
                    var discarded = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
                    ApplyCheckoutDocument(node, discarded);
                    node.WorkState = CadWorkState.None;
                    ProtectLoadedDocument(node.FullPath);
                }
                catch (Exception rollbackException)
                {
                    LogDiagnostic(string.Concat("Batch check-in preparation rollback.", node.FileName), rollbackException);
                }
            }

            throw;
        }
    }

    private async Task<BatchCheckInResult> CheckInBatchAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        string changeNote,
        Action<int, int, string, string> reportProgress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, BatchDocumentIdentity> identities = null)
    {
        var items = DistinctBatchItems(selectedItems);
        ValidateBatchFileNames(items);
        var result = new BatchCheckInResult();
        var originalDocumentPath = (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty;
        var orderedItems = items
            .OrderByDescending(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.Node.Kind == CadDocumentKind.Assembly ? 1 : 0)
            .ThenBy(candidate => candidate.Node.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            for (var index = 0; index < orderedItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = orderedItems[index];
                var node = item.Node;
                reportProgress?.Invoke(index, orderedItems.Length, node?.FileName, "正在检查版本和本地变更…");
                LogOperation(string.Concat("Batch check-in file start path=", node?.FullPath));
                try
                {
                    ValidateBatchCheckInNode(node);
                    BatchDocumentIdentity identity = null;
                    identities?.TryGetValue(node.FullPath, out identity);
                    var fileResult = await CheckInBatchNodeAsync(
                        node,
                        projectId,
                        changeNote.Trim(),
                        item.Depth == 0 && node.Kind == CadDocumentKind.Assembly,
                        cancellationToken,
                        identity);
                    if (fileResult.VersionCreated)
                    {
                        result.CreatedVersions++;
                        reportProgress?.Invoke(index + 1, orderedItems.Length, node.FileName, "已生成新版本。");
                    }
                    else
                    {
                        result.UnchangedFiles++;
                        reportProgress?.Invoke(index + 1, orderedItems.Length, node.FileName, "无变更，已结束编辑且版本号不变。");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("CheckInBatch.", node?.FileName), exception);
                    result.Failures.Add(string.Concat(node?.FileName ?? "未知图档", "：", exception.Message));
                    reportProgress?.Invoke(index + 1, orderedItems.Length, node?.FileName, string.Concat("处理失败：", exception.Message));
                }
            }
        }
        finally
        {
            RestoreOriginalDocumentAfterCheckIn(originalDocumentPath, (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty);
            taskPaneControl.SetTree(currentTree);
        }

        return result;
    }

    private void ValidateBatchCheckInNode(CadTreeNode node)
    {
        if (node == null || !node.DocumentId.HasValue)
        {
            throw new InvalidOperationException("图档尚未登记到PDM。");
        }

        if (node.IsHistoricalPreview || IsHistoricalPreviewPath(node.FullPath))
        {
            throw new InvalidOperationException("历史版本不能提交存档。");
        }

        if (node.HasBlockingIssue)
        {
            throw new InvalidOperationException("存在缺失引用，不能提交存档。");
        }

        if (!IsCheckedOutByCurrentUser(node))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(node.CheckedOutBy)
                ? "尚未获取编辑权限。"
                : string.Concat("正在由", node.CheckedOutBy, "编辑。"));
        }

        if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            throw new FileNotFoundException("本地图档不存在。");
        }

        if (ToSolidWorksDocumentType(node.Kind) == (int)swDocumentTypes_e.swDocNONE)
        {
            throw new InvalidOperationException("该文件类型不能提交存档。");
        }
    }

    private async Task<BatchNodeCheckInResult> CheckInBatchNodeAsync(
        CadTreeNode node,
        Guid projectId,
        string changeNote,
        bool isProjectRoot,
        CancellationToken cancellationToken,
        BatchDocumentIdentity identity = null)
    {
        var uploadCopyPath = string.Empty;
        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogOperation(string.Concat("Batch check-in versions start path=", node.FullPath));
            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
            LogOperation(string.Concat("Batch check-in versions end path=", node.FullPath, " count=", versions.Count));
            var latest = versions.FirstOrDefault();
            ApplyLatestVersion(node, latest);
            var referenceMatchesLatest = ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node);
            var referenceChanged = !referenceMatchesLatest;
            document = FindLoadedDocument(node.FullPath);
            var hasUnsavedChanges = document?.GetSaveFlag() == true;
            var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latest, node.FullPath, localSha256);
            LogOperation(string.Concat(
                "Batch check-in change detection path=", node.FullPath,
                " fileMatches=", fileMatchesLatest,
                " referenceMatches=", referenceMatchesLatest,
                " unsaved=", hasUnsavedChanges));
            if (identity == null && !referenceChanged && fileMatchesLatest && !hasUnsavedChanges)
            {
                LogOperation(string.Concat("Batch check-in skipped unchanged path=", node.FullPath));
                await CompleteUnchangedEditAsync(node, node.FullPath, cancellationToken);
                return new BatchNodeCheckInResult(false);
            }

            if (document == null)
            {
                document = OpenDocumentSilentlyForBatch(
                    node.FullPath,
                    ToSolidWorksDocumentType(node.Kind),
                    node.Configuration ?? string.Empty);
                openedForBatch = true;
            }

            var documentPath = document?.GetPathName() ?? string.Empty;
            if (document == null || !PathsEqual(documentPath, node.FullPath))
            {
                throw new IOException("未能安全加载待提交图档。");
            }
            EnsureDocumentEditable(document, documentPath);

            if (document.GetSaveFlag())
            {
                throw new InvalidOperationException(string.Concat(
                    node.FileName,
                    "在SolidWorks中处于未保存状态。为避免未变更图档误升版，请先手动保存确认内容，再提交存档；如无需保留修改，请使用“放弃编辑”。"));
            }

            localSha256 = await Task.Run(() => ComputeFileHash(documentPath), cancellationToken);
            if (identity == null
                && !referenceChanged
                && !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latest, documentPath, localSha256))
            {
                await CompleteUnchangedEditAsync(node, documentPath, cancellationToken);
                return new BatchNodeCheckInResult(false);
            }

            var modelProperties = ReadModelProperties(document, node.Configuration);
            uploadCopyPath = await Task.Run(
                () => CreateCheckInUploadCopy(documentPath, node.DocumentId.Value),
                cancellationToken);
            var storedFile = await apiClient.UploadVersionFileAsync(
                projectId,
                uploadCopyPath,
                node.DocumentId.Value,
                documentPath,
                cancellationToken);
            var checkIn = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                changeNote,
                storedFile,
                modelProperties,
                checkoutSessionId,
                isProjectRoot,
                referenceChanged || identity != null,
                identity?.DrawingNumber,
                identity?.Name,
                cancellationToken);
            ApplyCheckoutDocument(node, checkIn.Document);
            node.Revision = checkIn.Version?.Revision?.Display ?? checkIn.Document.Revision?.Display ?? node.Revision;
            node.CurrentRevision = node.Revision;
            node.WorkState = CadWorkState.None;
            ApplyLatestVersion(node, checkIn.Version);
            if (!openedForBatch)
            {
                ProtectLoadedDocument(documentPath);
            }
            return new BatchNodeCheckInResult(checkIn.VersionCreated);
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
            DeleteCheckInUploadCopy(uploadCopyPath);
        }
    }

    private async Task CompleteUnchangedEditAsync(CadTreeNode node, string activePath, CancellationToken cancellationToken)
    {
        var unchanged = await apiClient.CompleteEditWithoutChangesAsync(
            node.DocumentId.Value,
            checkoutSessionId,
            node.LatestStoredSha256,
            cancellationToken);
        ApplyCheckoutDocument(node, unchanged);
        node.Revision = unchanged.Revision?.Display ?? node.Revision;
        node.WorkState = CadWorkState.None;
        ProtectLoadedDocument(activePath);
    }

    private void OnCheckInRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var requestedNodes = eventArgs.Nodes
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.FullPath ?? candidate.InstancePath ?? candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var node = requestedNodes.FirstOrDefault() ?? eventArgs.Node;
        if (node == null)
        {
            return;
        }


        if (IsHistoricalPreviewContext(node))
        {
            ShowError("历史版本为只读预览，不能提交存档。请关闭历史版本并打开当前工作文件。 ");
            return;
        }

        var pendingRenames = currentTree == null
            ? Array.Empty<CadTreeNode>()
            : EnumerateCadNodes(currentTree)
                .Where(candidate => candidate.IsRenamePendingSave)
                .GroupBy(candidate => candidate.FullPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        if (pendingRenames.Length > 0)
        {
            var paths = pendingRenames
                .Select(candidate => candidate.FullPath)
                .Concat(new[] { currentTree.FullPath })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OpenBatchOperationDialog(
                currentProjectId ?? GetExplicitProjectId(currentTree.FullPath),
                BatchOperationKind.CheckIn,
                paths);
            return;
        }

        if (eventArgs.UsesCheckedSelection && requestedNodes.Length > 1)
        {
            OpenBatchOperationDialog(
                currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath),
                BatchOperationKind.CheckIn,
                requestedNodes.Select(candidate => candidate.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
            return;
        }

        var unregisteredDescendant = node.Kind == CadDocumentKind.Assembly
            ? EnumerateCadNodes(node)
                .Skip(1)
                .FirstOrDefault(descendant =>
                    !descendant.DocumentId.HasValue
                    && ToSolidWorksDocumentType(descendant.Kind) != (int)swDocumentTypes_e.swDocNONE)
            : null;
        if (!currentProjectId.HasValue || !node.DocumentId.HasValue || unregisteredDescendant != null)
        {
            OpenBatchOperationDialog(
                currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath),
                BatchOperationKind.CheckIn,
                eventArgs.UsesCheckedSelection
                    ? requestedNodes.Select(candidate => candidate.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
                    : null);
            return;
        }

        if (!EnsureServerDocument(node))
        {
            return;
        }

        if (!EnsureCurrentCheckoutSession(node, "提交存档"))
        {
            return;
        }

        if (node.HasBlockingIssue)
        {
            ShowError("结构树存在缺失引用，不能提交存档。 ");
            return;
        }

        if (Volatile.Read(ref openOperationInProgress) > 0)
        {
            ShowError("正在打开图档或已有提交存档任务，请稍候再试。");
            return;
        }

        if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
        {
            if (!BringActiveBatchProgressToFront())
            {
                ShowError("上一项PDM工作文件操作仍在完成（可能正在恢复原图档），请等待状态刷新后再试。");
            }
            return;
        }

        if (Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
        {
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            ShowError("已有提交存档任务正在进行，请稍候再试。");
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
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            ShowError(exception.Message);
        }
    }

    private bool BringActiveBatchProgressToFront()
    {
        var dialog = activeBatchProgressDialog;
        if (dialog == null || dialog.IsDisposed)
        {
            return false;
        }

        if (dialog.WindowState == FormWindowState.Minimized)
        {
            dialog.WindowState = FormWindowState.Normal;
        }

        dialog.Show();
        dialog.BringToFront();
        dialog.Activate();
        return true;
    }

    private async void OnDiscardCheckoutRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null)
        {
            return;
        }


        if (IsHistoricalPreviewContext(node))
        {
            ShowError("历史版本为只读预览，不能更改当前工作文件的编辑状态。请切换到当前工作文件。 ");
            return;
        }

        if (!EnsureServerDocument(node))
        {
            return;
        }

        if (!EnsureCurrentCheckoutSession(node, "放弃编辑"))
        {
            return;
        }

        try
        {
            var document = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
            ApplyCheckoutDocument(node, document);
            node.WorkState = CadWorkState.None;
            taskPaneControl.SetTree(currentTree);
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

            if (node.IsHistoricalPreview || IsHistoricalPreviewPath(node.FullPath))
            {
                ShowError("历史版本为只读预览，不能提交存档。请关闭历史版本并打开当前工作文件。 ");
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

            if (IsHistoricalPreviewPath(activePath))
            {
                ShowError("历史版本为只读预览，不能提交存档。请关闭历史版本并打开当前工作文件。 ");
                return;
            }
            EnsureDocumentEditable(document, activePath);

            var currentVersions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latestVersion = currentVersions.FirstOrDefault();
            ApplyLatestVersion(node, latestVersion);
            var isProjectRoot = node.Kind == CadDocumentKind.Assembly
                && currentTree != null
                && PathsEqual(node.FullPath, currentTree.FullPath);
            var referenceMatchesLatest = ReferenceSnapshotMatchesTree(latestVersion?.ReferenceSnapshot, node);
            var referenceChanged = !referenceMatchesLatest;
            var hasUnsavedChanges = document.GetSaveFlag();
            var currentSha256 = ComputeFileHash(activePath);
            var fileMatchesLatest = !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latestVersion, activePath, currentSha256);
            LogOperation(string.Concat(
                "CheckIn change detection path=", activePath,
                " fileMatches=", fileMatchesLatest,
                " referenceMatches=", referenceMatchesLatest,
                " unsaved=", hasUnsavedChanges));

            if (hasUnsavedChanges)
            {
                ShowError(string.Concat(
                    node.FileName,
                    "在SolidWorks中处于未保存状态。为避免未变更图档误升版，请先手动保存确认内容，再提交存档；如无需保留修改，请使用“放弃编辑”。"));
                return;
            }

            if (!referenceChanged && fileMatchesLatest)
            {
                var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, checkoutSessionId, node.LatestStoredSha256, lifetime.Token);
                ApplyCheckoutDocument(node, unchanged);
                node.Revision = unchanged.Revision?.Display ?? node.Revision;
                node.WorkState = CadWorkState.None;
                ProtectLoadedDocument(activePath);
                taskPaneControl.SetTree(currentTree);
                MessageBox.Show(taskPaneControl, string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"), "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var changeNote = string.Empty;
            if (currentVersions.Count > 0)
            {
                using (var dialog = new ChangeNoteDialog(node.FileName))
                {
                    if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
                    {
                        return;
                    }

                    changeNote = dialog.ChangeNote;
                }
            }

            var modelProperties = ReadModelProperties(document);
            uploadCopyPath = CreateCheckInUploadCopy(document, activePath, node.DocumentId.Value);
            var storedFile = await apiClient.UploadVersionFileAsync(projectId, uploadCopyPath, node.DocumentId.Value, activePath, lifetime.Token);
            var result = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                changeNote,
                storedFile,
                modelProperties,
                checkoutSessionId,
                isProjectRoot,
                referenceChanged,
                null,
                null,
                lifetime.Token);
            ApplyCheckoutDocument(node, result.Document);
            node.Revision = result.Version?.Revision?.Display ?? result.Document.Revision?.Display ?? node.Revision;
            node.CurrentRevision = node.Revision;
            node.WorkState = CadWorkState.None;
            ApplyLatestVersion(node, result.Version);
            ProtectLoadedDocument(activePath);
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
                Interlocked.Exchange(ref workspaceOperationInProgress, 0);
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

    private static string CreateCheckInUploadCopy(string sourcePath, Guid documentId)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "checkin", documentId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copyPath = Path.Combine(directory, Path.GetFileName(sourcePath));
        LogOperation(string.Concat("Batch check-in file copy start source=", sourcePath, " target=", copyPath));
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
        }
        LogOperation(string.Concat("Batch check-in file copy end target=", copyPath));
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
                if ((openErrors & (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen) != 0)
                {
                    ShowError("SolidWorks中已打开同名图档，无法同时打开该历史版本。请关闭同名历史窗口后重新获取。");
                }
                else
                {
                    ShowError(string.Concat("SolidWorks打开文件失败，错误码：", openErrors));
                }
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

    private IModelDoc2 OpenDocumentSilentlyForBatch(string fullPath, int documentType, string configuration)
    {
        var openErrors = 0;
        var openWarnings = 0;
        LogOperation(string.Concat("Batch fallback OpenDoc6 start path=", fullPath));
        var document = application.OpenDoc6(
            fullPath,
            documentType,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            configuration,
            ref openErrors,
            ref openWarnings);
        LogOperation(string.Concat("Batch fallback OpenDoc6 end path=", fullPath, " errors=", openErrors, " warnings=", openWarnings, " null=", document == null));
        if (document == null)
        {
            throw new IOException(string.Concat(
                Path.GetFileName(fullPath),
                "未在当前装配中加载，SolidWorks静默读取失败。错误码：",
                openErrors));
        }

        return document;
    }

    private void CloseBatchOpenedDocument(IModelDoc2 document)
    {
        try
        {
            var title = document.GetTitle();
            LogOperation(string.Concat("Batch fallback CloseDoc start title=", title));
            application.CloseDoc(title);
            LogOperation(string.Concat("Batch fallback CloseDoc end title=", title));
        }
        catch (Exception exception)
        {
            LogDiagnostic("CloseBatchOpenedDocument", exception);
        }
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

    private bool IsHistoricalPreviewContext(CadTreeNode node)
    {
        if (node?.IsHistoricalPreview == true || IsHistoricalPreviewPath(node?.FullPath))
        {
            return true;
        }

        var activePath = (application?.ActiveDoc as IModelDoc2)?.GetPathName();
        return IsHistoricalPreviewPath(activePath);
    }

    private static bool IsHistoricalPreviewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var historyRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UPTON-PDM", "history"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(historyRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var workspaceRoot = Path.GetFullPath(Path.Combine(
                    WorkspaceSettingsStore.GetWorkspaceRoot()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && fullPath.IndexOf(string.Concat(Path.DirectorySeparatorChar, "ReadOnly", Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkHistoricalPreview(CadTreeNode node, bool inheritedPreview)
    {
        if (node == null)
        {
            return;
        }

        node.IsHistoricalPreview = inheritedPreview || IsHistoricalPreviewPath(node.FullPath);
        foreach (var child in node.Children)
        {
            MarkHistoricalPreview(child, node.IsHistoricalPreview);
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
            if (taskPaneControl.SelectedNode?.DocumentId == documentId || eventArgs.Node.Kind == CadDocumentKind.Drawing)
                taskPaneControl.ShowVersions(documentId.Value, eventArgs.Node.FileName, versions);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static IReadOnlyDictionary<string, string> ReadModelProperties(IModelDoc2 document, string configurationName = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document == null) return result;
        ReadPropertyManager(document.Extension.CustomPropertyManager[string.Empty], "全局", result);
        if (string.IsNullOrWhiteSpace(configurationName))
            configurationName = document.ConfigurationManager?.ActiveConfiguration?.Name;
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
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PDM。");
            return;
        }

        var node = taskPaneControl.SelectedNode;
        if (node?.DocumentId != eventArgs.DocumentId)
        {
            node = EnumerateCadNodes(currentTree).FirstOrDefault(candidate => candidate.DocumentId == eventArgs.DocumentId);
        }
        if (node == null || !node.DocumentId.HasValue)
        {
            ShowError("当前结构树中未找到该图档，请刷新后重试。");
            return;
        }
        if (node.IsHistoricalPreview || IsHistoricalPreviewContext(node))
        {
            ShowError("历史预览目录不能切换本地工作版本。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }
        if (Interlocked.Exchange(ref workspaceOperationInProgress, 1) != 0)
        {
            ShowError("已有获取文件或存档任务正在进行，请稍候再试。");
            return;
        }

        var stagedPath = string.Empty;
        var reopenPath = string.Empty;
        var reopenKind = CadDocumentKind.Other;
        var reopenConfiguration = string.Empty;
        string initialLocalSha256 = null;
        var localFileExisted = false;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            RefreshLoadedDocumentModificationFlags(currentTree);
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var serverDocument = projectDocuments.FirstOrDefault(document => document.Id == node.DocumentId.Value)
                ?? throw new InvalidOperationException("当前项目中未找到该图档，请刷新结构树后重试。");
            ApplyCheckoutDocument(node, serverDocument);
            ValidateUpdateLatestNode(node);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该图档尚无可选择的PDM版本。");
            var selected = versions.FirstOrDefault(version => version.Id == eventArgs.Version.Id)
                ?? throw new InvalidOperationException("所选版本已不存在，请刷新版本列表后重试。");
            ApplyLatestVersion(node, latest);

            DocumentVersionDto current = null;
            localFileExisted = File.Exists(node.FullPath);
            if (localFileExisted)
            {
                initialLocalSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), lifetime.Token);
                current = versions.FirstOrDefault(version => VersionMatchesLocalFile(version, node.FullPath, initialLocalSha256));
                if (current == null)
                {
                    throw new InvalidOperationException(string.Concat(
                        node.FileName,
                        "的本地内容无法对应任何PDM历史版本，可能存在待提交修改。为避免覆盖，已停止切换。"));
                }

                if (current.Id == selected.Id)
                {
                    ProtectLoadedDocument(node.FullPath);
                    ApplySelectedWorkingVersion(node, selected, latest);
                    taskPaneControl.SetTree(currentTree);
                    MessageBox.Show(
                        taskPaneControl,
                        string.Concat("当前工作文件已经是", selected.Revision?.Display ?? "-", "，结构树已更新版本状态。"),
                        "UPTON PDM",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            var selectedRevision = selected.Revision?.Display ?? "-";
            var latestRevision = latest.Revision?.Display ?? "-";
            var currentRevision = current?.Revision?.Display ?? "文件缺失";
            var confirmation = string.Concat(
                "将", node.FileName, "的本地工作版本从", currentRevision, "切换到", selectedRevision, "。\r\n",
                "切换后结构树显示：", selectedRevision, " / ", latestRevision, "。\r\n",
                "原工作文件将自动备份，所选版本保持只读。是否继续？");
            if (MessageBox.Show(taskPaneControl, confirmation, "选择工作版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            stagedPath = await apiClient.DownloadVersionToWorkspaceStageAsync(
                node.DocumentId.Value,
                selected.Id,
                node.FileName,
                selected.Sha256,
                lifetime.Token);

            var rootPath = currentTree?.FullPath ?? string.Empty;
            var rootLoaded = !string.IsNullOrWhiteSpace(rootPath) && FindLoadedDocument(rootPath) != null;
            var selectedLoaded = FindLoadedDocument(node.FullPath) != null;
            if (rootLoaded)
            {
                reopenPath = rootPath;
                reopenKind = currentTree.Kind;
                reopenConfiguration = currentTree.Configuration;
            }
            else if (selectedLoaded)
            {
                reopenPath = node.FullPath;
                reopenKind = node.Kind;
                reopenConfiguration = node.Configuration;
            }

            EnsureWorkspaceDocumentsAreSaved();
            if (localFileExisted != File.Exists(node.FullPath)
                || localFileExisted && !string.Equals(initialLocalSha256, ComputeFileHash(node.FullPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("本地工作文件在版本准备期间发生变化，已停止切换。");
            }

            CloseDocumentsForWorkspaceUpdate(new[] { node.FullPath }, rootPath);
            ApplyWorkspaceUpdates(new[] { new WorkspaceUpdatePlan(node, selected, stagedPath) });
            ApplySelectedWorkingVersion(node, selected, latest);
            taskPaneControl.SetTree(currentTree);
            LogOperation(string.Concat(
                "Workspace version switched document=", node.DocumentId.Value,
                " current=", selectedRevision,
                " latest=", latestRevision,
                " path=", node.FullPath));
            MessageBox.Show(
                taskPaneControl,
                string.Concat("工作版本已切换为", selectedRevision, "。\r\n结构树版本：", selectedRevision, " / ", latestRevision, "。"),
                "UPTON PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnOpenHistoryRequested", exception);
            ShowError(exception.Message);
        }
        finally
        {
            DeleteWorkspaceStage(stagedPath);
            if (!string.IsNullOrWhiteSpace(reopenPath) && File.Exists(reopenPath))
            {
                OpenOrActivateDocumentOnSolidWorksThread(reopenPath, ToSolidWorksDocumentType(reopenKind), reopenConfiguration);
            }
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            ScheduleTreeRefresh();
        }
    }

    private void ApplySelectedWorkingVersion(CadTreeNode source, DocumentVersionDto selected, DocumentVersionDto latest)
    {
        var revision = selected?.Revision?.Display ?? string.Empty;
        foreach (var node in EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, source.FullPath)))
        {
            ApplyLatestVersion(node, latest);
            node.Revision = revision;
            node.CurrentRevision = revision;
            node.OpenedVersionId = null;
            node.OpenedRevision = string.Empty;
            node.IsHistoricalPreview = false;
            node.WorkState = CadWorkState.None;
            node.IsModifiedInSolidWorks = false;
            if (node.Status == CadReferenceStatus.Missing && File.Exists(node.FullPath))
            {
                node.Status = CadReferenceStatus.Normal;
            }
        }
    }

    private void OnCompareVersionsRequested(object sender, VersionComparisonEventArgs eventArgs)
    {
        try
        {
            StartDesktopClient(string.Concat("--compare ", eventArgs.DocumentId, " ", eventArgs.LeftVersionId, " ", eventArgs.RightVersionId));
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private static void StartDesktopClient(string arguments)
    {
        var executable = FindDesktopClientExecutable();

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = true
        });
    }

    private static string FindDesktopClientExecutable()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location) ?? string.Empty;
        var applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(assemblyDirectory, "Upton.Pdm.Desktop.exe"),
            Path.Combine(assemblyDirectory, "..", "client", "Upton.Pdm.Desktop.exe"),
            Path.Combine(applicationDirectory, "Upton.Pdm.Desktop.exe"),
            Path.Combine(applicationDirectory, "..", "client", "Upton.Pdm.Desktop.exe")
        };
        var executable = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new FileNotFoundException("未找到Windows客户端。请确认客户端已完成本地部署。");
        }
        return executable;
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

    private void ApplyDrawingModelRelation(CadTreeNode drawing)
    {
        if (drawing?.Kind != CadDocumentKind.Drawing || drawing.RelatedModelDocumentId.HasValue) return;
        drawing.RelatedModelDocumentId = FindParentModelDocumentId(currentTree, drawing);
    }

    private static Guid? FindParentModelDocumentId(CadTreeNode current, CadTreeNode target)
    {
        if (current == null || target == null) return null;
        foreach (var child in current.Children)
        {
            if (ReferenceEquals(child, target)
                && current.Kind is CadDocumentKind.Assembly or CadDocumentKind.Part)
                return current.DocumentId;
            var match = FindParentModelDocumentId(child, target);
            if (match.HasValue) return match;
        }
        return null;
    }

    private void RefreshTree(bool showErrors)
    {
        if (application?.ActiveDoc == null)
        {
            ClearActiveDocumentContext();
            return;
        }

        try
        {
            taskPaneControl.SetProjectContextAvailable(true);
            var previousTree = currentTree;
            var scannedTree = scanner.ScanActiveDocument();
            RestorePersistedDocumentIdentities(scannedTree);
            CarryForwardDocumentIdentities(previousTree, scannedTree);
            var documentIdentity = DocumentIdentity(scannedTree);
            var documentChanged = !string.Equals(currentDocumentIdentity, documentIdentity, StringComparison.OrdinalIgnoreCase);
            if (documentChanged)
            {
                Interlocked.Increment(ref projectResolutionGeneration);
                currentProjectId = null;
                taskPaneControl.SelectProject(null);
                currentDocumentIdentity = documentIdentity;
            }

            currentTree = scannedTree;
            MarkHistoricalPreview(currentTree, false);
            var controlledManifest = ApplyControlledOpenMetadata(currentTree);
            var activeDrawingSource = ResolveActiveDrawingSource(currentTree, previousTree);
            if (controlledManifest != null)
            {
                currentProjectId = controlledManifest.ProjectId;
                taskPaneControl.SelectProject(controlledManifest.ProjectId);
            }
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.SetActiveDrawingSource(activeDrawingSource);
            LogOperation(string.Concat("RefreshTree success nodes=", CountTreeNodes(currentTree), " path=", currentTree?.FullPath ?? string.Empty));
            if (currentProjectId.HasValue && apiClient.IsAuthenticated)
            {
                _ = RefreshMetadataAsync(currentProjectId.Value);
            }
            else if (apiClient.IsAuthenticated)
            {
                _ = ResolveProjectForCurrentDocumentAsync(currentTree, documentIdentity);
            }
        }
        catch (Exception exception)
        {
            if (application.ActiveDoc == null)
            {
                ClearActiveDocumentContext();
                return;
            }

            LogDiagnostic("RefreshTree", exception);
            if (showErrors)
            {
                ShowError(exception.Message);
            }
        }
    }

    private void ClearActiveDocumentContext()
    {
        Interlocked.Increment(ref projectResolutionGeneration);
        currentTree = null;
        currentDocumentIdentity = string.Empty;
        currentProjectId = null;
        taskPaneControl.SetProjectContextAvailable(false);
        taskPaneControl.SelectProject(null);
        taskPaneControl.ClearTree();
        taskPaneControl.SetConnectionState(false, "未打开图档");
    }

    private async Task ResolveProjectForCurrentDocumentAsync(CadTreeNode tree, string documentIdentity)
    {
        if (tree == null || availableProjects.Count == 0 || !apiClient.IsAuthenticated)
        {
            return;
        }

        var generation = Interlocked.Increment(ref projectResolutionGeneration);
        try
        {
            var explicitProjectId = Guid.Empty;
            var hasExplicitProject = !string.IsNullOrWhiteSpace(tree.FullPath)
                && explicitProjectPaths.TryGetValue(Path.GetFullPath(tree.FullPath), out explicitProjectId)
                && availableProjects.Any(project => project.Id == explicitProjectId);

            Guid? resolvedProjectId = hasExplicitProject ? explicitProjectId : (Guid?)null;

            if (generation == Volatile.Read(ref projectResolutionGeneration)
                && string.Equals(currentDocumentIdentity, documentIdentity, StringComparison.OrdinalIgnoreCase)
                && application?.ActiveDoc != null)
            {
                currentProjectId = resolvedProjectId;
                taskPaneControl.SelectProject(currentProjectId);
                if (currentProjectId.HasValue)
                {
                    await RefreshMetadataAsync(currentProjectId.Value);
                }
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("ResolveProjectForCurrentDocumentAsync", exception);
        }
    }

    private static string DocumentIdentity(CadTreeNode tree) =>
        string.IsNullOrWhiteSpace(tree?.FullPath) ? tree?.FileName ?? string.Empty : tree.FullPath;

    private static int CountTreeNodes(CadTreeNode node)
    {
        return node == null ? 0 : 1 + node.Children.Sum(CountTreeNodes);
    }

    private async Task RefreshMetadataAsync(Guid projectId)
    {
        var targetTree = currentTree;
        try
        {
            var documents = await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
            if (disconnecting
                || taskPaneControl == null
                || taskPaneControl.IsDisposed
                || !ReferenceEquals(currentTree, targetTree)
                || currentProjectId != projectId)
            {
                return;
            }

            var byId = documents.ToDictionary(document => document.Id);
            var byFileName = documents
                .GroupBy(document => document.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ApplyMetadata(targetTree, byId, byFileName);
            await ApplyWorkingStatesAsync(targetTree);
            if (!ReferenceEquals(currentTree, targetTree) || currentProjectId != projectId)
            {
                return;
            }
            taskPaneControl.SetTree(targetTree);
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

    private async Task LoadProjectTreeAsync(Guid projectId)
    {
        try
        {
            var documentsTask = apiClient.GetDocumentsAsync(projectId, lifetime.Token);
            var relationsTask = apiClient.GetDocumentRelationsAsync(projectId, lifetime.Token);
            var treeTask = apiClient.GetReferenceTreeAsync(projectId, lifetime.Token);
            await Task.WhenAll(documentsTask, relationsTask, treeTask);
            var documents = documentsTask.Result.ToDictionary(item => item.Id);
            var root = MapProjectTree(treeTask.Result, documents, true);
            ReconcileProjectDrawings(root, documents, relationsTask.Result);
            taskPaneControl.SetProjectTree(projectId, root);
        }
        catch (Exception exception)
        {
            taskPaneControl?.SetProjectTree(projectId, null);
            LogDiagnostic("LoadProjectTreeAsync", exception);
        }
    }

    private static CadTreeNode MapProjectTree(
        DocumentReferenceNodeDto source,
        IReadOnlyDictionary<Guid, DocumentDto> documents,
        bool isRoot)
    {
        if (source == null) return null;
        documents.TryGetValue(source.DocumentId ?? Guid.Empty, out var document);
        var snapshotRevision = source.Revision?.Display ?? document?.Revision?.Display ?? string.Empty;
        var displayedRevision = isRoot
            ? document?.Revision?.Display ?? snapshotRevision
            : snapshotRevision;
        var node = new CadTreeNode
        {
            DocumentId = source.DocumentId,
            InstancePath = source.InstancePath ?? string.Empty,
            ComponentSelectionName = string.Empty,
            FileName = source.FileName ?? document?.FileName ?? string.Empty,
            FullPath = string.Empty,
            DisplayName = source.DisplayName ?? document?.Name ?? source.FileName ?? string.Empty,
            DrawingNumber = document?.DrawingNumber ?? Path.GetFileNameWithoutExtension(source.FileName ?? string.Empty),
            Kind = (CadDocumentKind)source.Kind,
            Configuration = source.Configuration ?? string.Empty,
            Quantity = Math.Max(1, source.Quantity),
            Status = (CadReferenceStatus)source.Status,
            Revision = displayedRevision,
            CurrentRevision = displayedRevision,
            LatestRevision = document?.Revision?.Display ?? source.Revision?.Display ?? string.Empty,
            CheckedOutBy = source.CheckedOutBy ?? document?.CheckedOutBy,
            CheckedOutAt = document?.CheckedOutAt,
            CheckoutSessionId = document?.CheckoutSessionId,
            CheckoutMachine = document?.CheckoutMachine ?? string.Empty,
            CheckoutLastHeartbeatAt = document?.CheckoutLastHeartbeatAt,
            LifecycleState = LifecycleStateName(document?.State ?? 0),
            UpdatedAt = document?.UpdatedAt
        };
        var seenInstancePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in source.Children ?? new List<DocumentReferenceNodeDto>())
        {
            if (!string.IsNullOrWhiteSpace(child.InstancePath) && !seenInstancePaths.Add(child.InstancePath))
            {
                continue;
            }
            node.Children.Add(MapProjectTree(child, documents, false));
        }
        return node;
    }

    private static void ReconcileProjectDrawings(
        CadTreeNode node,
        IReadOnlyDictionary<Guid, DocumentDto> documents,
        IReadOnlyList<DocumentModelDrawingRelationDto> relations)
    {
        if (node == null) return;

        foreach (var child in node.Children.ToArray())
        {
            ReconcileProjectDrawings(child, documents, relations);
        }

        if (!node.DocumentId.HasValue || node.Kind == CadDocumentKind.Drawing) return;

        var existingDrawingIds = new HashSet<Guid>(node.Children
            .Where(child => child.Kind == CadDocumentKind.Drawing && child.DocumentId.HasValue)
            .Select(child => child.DocumentId.Value));
        foreach (var relation in relations.Where(item => item.ModelDocumentId == node.DocumentId.Value))
        {
            if (existingDrawingIds.Contains(relation.DrawingDocumentId)
                || !documents.TryGetValue(relation.DrawingDocumentId, out var drawing)
                || (CadDocumentKind)drawing.Kind != CadDocumentKind.Drawing)
            {
                continue;
            }

            var revision = drawing.Revision?.Display ?? string.Empty;
            node.Children.Add(new CadTreeNode
            {
                DocumentId = drawing.Id,
                RelatedModelDocumentId = node.DocumentId,
                InstancePath = string.Concat(node.InstancePath, "/drawing:", drawing.Id.ToString("N")),
                FileName = drawing.FileName ?? string.Empty,
                DisplayName = drawing.Name ?? drawing.DrawingNumber ?? drawing.FileName ?? string.Empty,
                DrawingNumber = drawing.DrawingNumber ?? Path.GetFileNameWithoutExtension(drawing.FileName ?? string.Empty),
                Kind = CadDocumentKind.Drawing,
                Configuration = "工程图",
                Quantity = 1,
                Status = CadReferenceStatus.Normal,
                Revision = revision,
                CurrentRevision = revision,
                LatestRevision = revision,
                CheckedOutBy = drawing.CheckedOutBy,
                CheckedOutAt = drawing.CheckedOutAt,
                CheckoutSessionId = drawing.CheckoutSessionId,
                CheckoutMachine = drawing.CheckoutMachine ?? string.Empty,
                CheckoutLastHeartbeatAt = drawing.CheckoutLastHeartbeatAt,
                LifecycleState = LifecycleStateName(drawing.State),
                UpdatedAt = drawing.UpdatedAt
            });
            existingDrawingIds.Add(drawing.Id);
        }
    }

    private bool CanRecoverUnregisteredReferences(Guid rootDocumentId)
    {
        return currentTree != null
            && !currentTree.IsHistoricalPreview
            && currentTree.DocumentId == rootDocumentId
            && EnumerateCadNodes(currentTree)
                .Skip(1)
                .Any(node =>
                    !node.DocumentId.HasValue
                    && !string.IsNullOrWhiteSpace(node.FullPath)
                    && File.Exists(node.FullPath)
                    && ToSolidWorksDocumentType(node.Kind) != (int)swDocumentTypes_e.swDocNONE);
    }

    private static bool IsUnregisteredReferenceOpenFailure(Exception exception)
    {
        var message = exception?.Message ?? string.Empty;
        return message.IndexOf("尚未登记", StringComparison.Ordinal) >= 0
            && message.IndexOf("打开清单", StringComparison.Ordinal) >= 0;
    }

    private void ApplyCheckoutDocument(CadTreeNode node, DocumentDto document)
    {
        if (node == null || document == null) return;
        TrackCheckoutDocument(document);
        node.CheckedOutBy = document.CheckedOutBy;
        node.CheckedOutAt = document.CheckedOutAt;
        node.CheckoutSessionId = document.CheckoutSessionId;
        node.CheckoutMachine = document.CheckoutMachine ?? string.Empty;
        node.CheckoutLastHeartbeatAt = document.CheckoutLastHeartbeatAt;
        node.DrawingNumber = document.DrawingNumber ?? node.DrawingNumber;
        node.DisplayName = document.Name ?? node.DisplayName;
        node.LifecycleState = LifecycleStateName(document.State);
        node.UpdatedAt = document.UpdatedAt;
        var checkedOutByCurrentUser = !string.IsNullOrWhiteSpace(authenticatedUsername)
            && !string.IsNullOrWhiteSpace(document.CheckedOutBy)
            && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        node.CheckoutSessionLost = checkedOutByCurrentUser && document.CheckoutSessionId != checkoutSessionId;
        node.Revision = document.Revision?.Display ?? node.Revision;
        if (string.IsNullOrWhiteSpace(document.CheckedOutBy) && node.DocumentId.HasValue)
        {
            checkoutReminderLevels.Remove(node.DocumentId.Value);
            RefreshCheckoutReminder();
        }
    }

    private void ApplyRegisteredDocumentToMatchingInstances(CadTreeNode source, DocumentDto document)
    {
        if (source == null || document == null) return;
        var matchingNodes = currentTree == null
            ? new[] { source }
            : EnumerateCadNodes(currentTree)
                .Where(candidate => PathsEqual(candidate.FullPath, source.FullPath))
                .ToArray();
        if (matchingNodes.Length == 0) matchingNodes = new[] { source };

        foreach (var node in matchingNodes)
        {
            node.DocumentId = document.Id;
            PdmDocumentIdentityStore.TryWrite(node.FullPath, document.Id);
            node.Revision = document.Revision?.Display ?? node.Revision;
            node.CurrentRevision = node.Revision;
            node.LatestRevision = document.Revision?.Display ?? node.LatestRevision;
            ApplyCheckoutDocument(node, document);
        }
    }

    private void RefreshCheckoutReminder()
    {
        var reminderNode = EnumerateTree(currentTree)
            .Where(candidate => candidate.DocumentId.HasValue
                && IsCheckedOutByCurrentUser(candidate)
                && checkoutReminderLevels.ContainsKey(candidate.DocumentId.Value))
            .OrderByDescending(candidate => checkoutReminderLevels[candidate.DocumentId.Value])
            .ThenBy(candidate => candidate.CheckedOutAt ?? DateTime.MaxValue)
            .FirstOrDefault();
        if (reminderNode == null)
        {
            taskPaneControl.SetCheckoutReminder(string.Empty, false);
            return;
        }

        var level = checkoutReminderLevels[reminderNode.DocumentId.Value];
        var hours = reminderNode.CheckedOutAt.HasValue
            ? Math.Max(0, Math.Floor((DateTime.UtcNow - reminderNode.CheckedOutAt.Value.ToUniversalTime()).TotalHours))
            : 0;
        taskPaneControl.SetCheckoutReminder(
            level >= 2
                ? string.Concat(reminderNode.FileName, "已连续编辑", hours, "小时，请尽快提交存档或结束编辑。")
                : string.Concat(reminderNode.FileName, "已编辑", hours, "小时，请及时提交存档。"),
            level >= 2);
    }

    private void TrackCheckoutDocument(DocumentDto document)
    {
        if (document == null || document.Id == Guid.Empty) return;
        var ownedByCurrentSession = document.CheckoutSessionId == checkoutSessionId
            && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        lock (checkoutDocumentSync)
        {
            if (ownedByCurrentSession) activeCheckoutDocumentIds.Add(document.Id);
            else activeCheckoutDocumentIds.Remove(document.Id);
        }
    }

    private void ApplyMetadata(
        CadTreeNode node,
        IReadOnlyDictionary<Guid, DocumentDto> documentsById,
        IReadOnlyDictionary<string, DocumentDto> legacyDocumentsByFileName)
    {
        if (node == null)
        {
            return;
        }

        DocumentDto document = null;
        if (!node.IsHistoricalPreview && node.DocumentId.HasValue)
        {
            documentsById.TryGetValue(node.DocumentId.Value, out document);
        }
        else if (!node.IsHistoricalPreview && !string.IsNullOrWhiteSpace(node.FileName))
        {
            legacyDocumentsByFileName.TryGetValue(node.FileName, out document);
        }

        if (document != null)
        {
            node.DocumentId = document.Id;
            PdmDocumentIdentityStore.TryWrite(node.FullPath, document.Id);
            ApplyCheckoutDocument(node, document);
        }

        foreach (var child in node.Children)
        {
            ApplyMetadata(child, documentsById, legacyDocumentsByFileName);
        }
    }

    private ControlledOpenManifestDto ApplyControlledOpenMetadata(CadTreeNode root)
    {
        if (root == null || string.IsNullOrWhiteSpace(root.FullPath))
        {
            return null;
        }

        var rootPath = Path.GetFullPath(root.FullPath);
        var context = controlledOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(rootPath, pair.Key));
        if (context.Value == null)
        {
            return null;
        }

        var filesByPath = new Dictionary<string, ControlledOpenFileDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in context.Value.Files ?? new List<ControlledOpenFileDto>())
        {
            var relativePath = file.RelativePath ?? file.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(context.Key, relativePath));
            if (IsPathWithinDirectory(fullPath, context.Key))
            {
                filesByPath[fullPath] = file;
            }
        }

        ApplyControlledOpenMetadata(root, filesByPath);
        return context.Value;
    }

    private void RememberControlledOpenManifest(ControlledOpenManifestDto manifest, string directory)
    {
        if (manifest == null || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var normalizedDirectory = NormalizeDirectory(directory);
        controlledOpenManifests[normalizedDirectory] = manifest;
        foreach (var file in manifest.Files ?? new List<ControlledOpenFileDto>())
        {
            var relativePath = file.RelativePath ?? file.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(normalizedDirectory, relativePath));
            if (IsPathWithinDirectory(fullPath, normalizedDirectory))
            {
                PdmDocumentIdentityStore.TryWrite(fullPath, file.DocumentId);
            }
        }
    }

    private static void RestorePersistedDocumentIdentities(CadTreeNode node)
    {
        if (node == null)
        {
            return;
        }

        if (PdmDocumentIdentityStore.TryRead(node.FullPath, out var documentId))
        {
            node.DocumentId = documentId;
        }

        foreach (var child in node.Children)
        {
            RestorePersistedDocumentIdentities(child);
        }
    }

    private void CarryForwardDocumentIdentities(CadTreeNode previousRoot, CadTreeNode currentRoot)
    {
        if (previousRoot == null || currentRoot == null)
        {
            pendingAssemblyItemRenames.Clear();
            return;
        }

        var previousByPath = EnumerateCadNodes(previousRoot)
            .Where(node => node.DocumentId.HasValue && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => Path.GetFullPath(node.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in EnumerateCadNodes(currentRoot).Where(node => !node.DocumentId.HasValue && !string.IsNullOrWhiteSpace(node.FullPath)))
        {
            if (previousByPath.TryGetValue(Path.GetFullPath(node.FullPath), out var previous))
            {
                CopyPdmIdentity(previous, node);
            }
        }

        foreach (var rename in pendingAssemblyItemRenames.ToArray())
        {
            var sources = EnumerateCadNodes(previousRoot)
                .Where(node => node.DocumentId.HasValue && ComponentNameMatches(node.ComponentSelectionName, rename.OldName))
                .ToArray();
            var targets = EnumerateCadNodes(currentRoot)
                .Where(node => !node.DocumentId.HasValue && ComponentNameMatches(node.ComponentSelectionName, rename.NewName))
                .ToArray();
            if (sources.Length == 1 && targets.Length == 1)
            {
                CopyPdmIdentity(sources[0], targets[0]);
                PdmDocumentIdentityStore.TryWrite(targets[0].FullPath, sources[0].DocumentId.Value);
            }
        }

        pendingAssemblyItemRenames.Clear();
    }

    private static bool ComponentNameMatches(string componentName, string eventName)
    {
        if (string.Equals(componentName, eventName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(componentName ?? string.Empty),
            Path.GetFileNameWithoutExtension(eventName ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyControlledOpenMetadata(CadTreeNode node, IReadOnlyDictionary<string, ControlledOpenFileDto> filesByPath)
    {
        if (node == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.FullPath)
            && filesByPath.TryGetValue(Path.GetFullPath(node.FullPath), out var file))
        {
            node.DocumentId = file.DocumentId;
            PdmDocumentIdentityStore.TryWrite(node.FullPath, file.DocumentId);
            node.OpenedVersionId = file.VersionId;
            node.OpenedRevision = file.Revision ?? string.Empty;
            node.Revision = node.OpenedRevision;
            node.CurrentRevision = node.OpenedRevision;
        }

        foreach (var child in node.Children)
        {
            ApplyControlledOpenMetadata(child, filesByPath);
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsPathWithinDirectory(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(NormalizeDirectory(directory), StringComparison.OrdinalIgnoreCase);

    private async Task ApplyWorkingStatesAsync(CadTreeNode root)
    {
        var nodes = EnumerateCadNodes(root).ToArray();
        var latestHashes = new Dictionary<Guid, string>();
        var latestStoredHashes = new Dictionary<Guid, string>();
        var versionsByDocument = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        var documentIds = nodes
            .Select(node => node.DocumentId)
            .Where(id => id.HasValue)
            .Select(id => id.Value)
            .Distinct()
            .ToArray();
        var versionTasks = documentIds.ToDictionary(documentId => documentId, GetTreeVersionsAsync);
        await Task.WhenAll(versionTasks.Values);
        foreach (var pair in versionTasks)
        {
            var versions = pair.Value.Result;
            if (versions != null)
            {
                versionsByDocument[pair.Key] = versions;
                var latest = versions.FirstOrDefault();
                latestHashes[pair.Key] = VersionSourceSha256(latest) ?? string.Empty;
                latestStoredHashes[pair.Key] = latest?.Sha256 ?? string.Empty;
            }
        }

        var pathsToHash = nodes
            .Where(node => node.DocumentId.HasValue
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
            node.LatestStoredSha256 = node.DocumentId.HasValue && latestStoredHashes.TryGetValue(node.DocumentId.Value, out var latestStoredSha256)
                ? latestStoredSha256
                : string.Empty;
            node.LatestRevision = node.DocumentId.HasValue
                && versionsByDocument.TryGetValue(node.DocumentId.Value, out var versions)
                ? versions.FirstOrDefault()?.Revision?.Display ?? string.Empty
                : string.Empty;
            if (node.DocumentId.HasValue && versionsByDocument.TryGetValue(node.DocumentId.Value, out var propertyVersions))
            {
                var properties = propertyVersions.FirstOrDefault()?.PropertySnapshot;
                node.Description = FindSnapshotProperty(properties, "描述", "Description");
                node.Material = FindSnapshotProperty(properties, "材料", "Material");
            }
            node.CurrentRevision = DetermineCurrentRevision(node, versionsByDocument, localHashes);
            node.WorkState = DetermineWorkState(node, localHashes, versionsByDocument);
        }
    }

    private async Task<IReadOnlyList<DocumentVersionDto>> GetTreeVersionsAsync(Guid documentId)
    {
        try
        {
            return await apiClient.GetVersionsAsync(documentId, lifetime.Token);
        }
        catch (Exception exception)
        {
            LogDiagnostic("ApplyWorkingStates.GetVersions", exception);
            return null;
        }
    }

    private static string DetermineCurrentRevision(
        CadTreeNode node,
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>> versionsByDocument,
        IReadOnlyDictionary<string, string> localHashes)
    {
        if (node.IsHistoricalPreview && !string.IsNullOrWhiteSpace(node.OpenedRevision))
        {
            return node.OpenedRevision;
        }

        if (!node.DocumentId.HasValue)
        {
            return node.IsHistoricalPreview ? "只读预览" : "未入库";
        }

        if (node.IsRenamePendingSave)
        {
            var revision = (node.CurrentRevision ?? string.Empty).TrimEnd('*');
            if (string.IsNullOrWhiteSpace(revision)
                || string.Equals(revision, "文件缺失", StringComparison.Ordinal)
                || string.Equals(revision, "待识别", StringComparison.Ordinal)
                || string.Equals(revision, "本地修改", StringComparison.Ordinal)
                || string.Equals(revision, "重命名待保存", StringComparison.Ordinal))
            {
                revision = versionsByDocument.TryGetValue(node.DocumentId.Value, out var renameVersions)
                    ? renameVersions.FirstOrDefault()?.Revision?.Display ?? string.Empty
                    : string.Empty;
            }

            return string.IsNullOrWhiteSpace(revision) ? "重命名待保存" : string.Concat(revision, "*");
        }

        if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            return "文件缺失";
        }

        if (!versionsByDocument.TryGetValue(node.DocumentId.Value, out var versions))
        {
            return "待识别";
        }

        if (versions.Count == 0)
        {
            return "未存档";
        }

        if (localHashes.TryGetValue(node.FullPath, out var localSha256))
        {
            var matching = versions.FirstOrDefault(version =>
                VersionMatchesLocalFile(version, node.FullPath, localSha256));
            if (matching != null)
            {
                if (!VersionFileNameMatches(matching, node.FullPath))
                {
                    return "本地修改";
                }

                var revision = matching.Revision?.Display ?? "未存档";
                return node.IsModifiedInSolidWorks ? string.Concat(revision, "*") : revision;
            }

            return node.IsModifiedInSolidWorks || versions.Any(version => !string.IsNullOrWhiteSpace(VersionSourceSha256(version)))
                ? "本地修改"
                : "待识别";
        }

        return "待识别";
    }

    private static bool VersionMatchesFile(DocumentVersionDto version, string sha256)
    {
        var sourceSha256 = VersionSourceSha256(version);
        return (!string.IsNullOrWhiteSpace(sourceSha256)
                && string.Equals(sourceSha256, sha256, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(version?.Sha256)
                && string.Equals(version.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool VersionMatchesLocalFile(DocumentVersionDto version, string fullPath, string sha256)
    {
        if (VersionMatchesFile(version, sha256))
        {
            return true;
        }

        if (version?.PropertySnapshot == null
            || !string.IsNullOrWhiteSpace(VersionSourceSha256(version))
            || string.IsNullOrWhiteSpace(fullPath)
            || !File.Exists(fullPath)
            || !version.PropertySnapshot.TryGetValue("FileName", out var recordedFileName)
            || !string.Equals(recordedFileName, Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase)
            || !version.PropertySnapshot.TryGetValue("LastWriteTimeUtc", out var recordedWriteTime)
            || !DateTimeOffset.TryParse(
                recordedWriteTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var versionWriteTime))
        {
            return false;
        }

        var difference = File.GetLastWriteTimeUtc(fullPath) - versionWriteTime.UtcDateTime;
        return Math.Abs(difference.TotalSeconds) <= 2;
    }

    private static bool VersionFileNameMatches(DocumentVersionDto version, string fullPath)
    {
        if (version?.PropertySnapshot == null
            || string.IsNullOrWhiteSpace(fullPath)
            || !version.PropertySnapshot.TryGetValue("FileName", out var recordedFileName)
            || string.IsNullOrWhiteSpace(recordedFileName))
        {
            return true;
        }

        return string.Equals(recordedFileName, Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current) =>
        ReferenceSnapshotMatchesTree(archived, current, true);

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current, bool isRoot)
    {
        if (archived == null || current == null
            || archived.DocumentId != current.DocumentId
            || !string.Equals(archived.FileName ?? string.Empty, current.FileName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || archived.Kind != (int)current.Kind)
        {
            return false;
        }

        if (current.Kind == CadDocumentKind.Drawing)
        {
            return true;
        }

        if (!string.Equals(archived.Configuration ?? string.Empty, current.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || archived.Quantity != current.Quantity
            || NormalizeReferenceStatus(archived.Status) != NormalizeReferenceStatus((int)current.Status))
        {
            return false;
        }

        if (!isRoot
            && !string.Equals(
                archived.Revision?.Display ?? string.Empty,
                current.CurrentRevision ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var archivedChildren = (archived.Children ?? new List<DocumentReferenceNodeDto>())
            .Where(child => child.Kind != (int)CadDocumentKind.Drawing)
            .ToArray();
        var currentChildren = current.Children
            .Where(child => child.Kind != CadDocumentKind.Drawing)
            .ToArray();
        if (archivedChildren.Length != currentChildren.Length)
        {
            return false;
        }

        var unmatchedCurrentChildren = new List<CadTreeNode>(currentChildren);
        foreach (var archivedChild in archivedChildren)
        {
            var matchingIndex = unmatchedCurrentChildren.FindIndex(candidate =>
                ReferenceSnapshotMatchesTree(archivedChild, candidate, false));
            if (matchingIndex < 0)
            {
                return false;
            }

            unmatchedCurrentChildren.RemoveAt(matchingIndex);
        }

        return true;
    }

    private static int NormalizeReferenceStatus(int status)
    {
        return status == (int)CadReferenceStatus.Hidden || status == (int)CadReferenceStatus.Lightweight
            ? (int)CadReferenceStatus.Normal
            : status;
    }

    private static bool NodeMatchesLatestFile(CadTreeNode node, string sha256)
    {
        return node != null
            && !string.IsNullOrWhiteSpace(sha256)
            && ((!string.IsNullOrWhiteSpace(node.LatestVersionSha256)
                    && string.Equals(node.LatestVersionSha256, sha256, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                    && string.Equals(node.LatestStoredSha256, sha256, StringComparison.OrdinalIgnoreCase)));
    }

    private static void ApplyLatestVersion(CadTreeNode node, DocumentVersionDto latest)
    {
        node.LatestVersionSha256 = VersionSourceSha256(latest) ?? string.Empty;
        node.LatestStoredSha256 = latest?.Sha256 ?? string.Empty;
        node.LatestRevision = latest?.Revision?.Display ?? string.Empty;
        node.Description = FindSnapshotProperty(latest?.PropertySnapshot, "描述", "Description");
        node.Material = FindSnapshotProperty(latest?.PropertySnapshot, "材料", "Material");
    }

    private static string FindSnapshotProperty(IReadOnlyDictionary<string, string> properties, params string[] names)
    {
        if (properties == null) return string.Empty;
        foreach (var name in names)
        {
            var match = properties.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
                || pair.Key.EndsWith(string.Concat("/", name), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return string.Empty;
    }

    private static string LifecycleStateName(int state) => state == 1 ? "InReview" : state == 2 ? "Released" : state == 3 ? "Obsolete" : "Work";

    private static string VersionSourceSha256(DocumentVersionDto version)
    {
        if (version?.PropertySnapshot == null)
        {
            return null;
        }

        var property = version.PropertySnapshot.FirstOrDefault(pair => string.Equals(pair.Key, "SourceFileSha256", StringComparison.OrdinalIgnoreCase));
        return property.Value;
    }

    private bool IsCheckedOutByCurrentUser(CadTreeNode node) =>
        !string.IsNullOrWhiteSpace(authenticatedUsername)
        && !string.IsNullOrWhiteSpace(node?.CheckedOutBy)
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
        && node.CheckoutSessionId == checkoutSessionId
        && !node.CheckoutSessionLost;

    private bool EnsureCurrentCheckoutSession(CadTreeNode node, string operation)
    {
        if (IsCheckedOutByCurrentUser(node))
        {
            return true;
        }

        var checkedOutByCurrentUser = !string.IsNullOrWhiteSpace(authenticatedUsername)
            && !string.IsNullOrWhiteSpace(node?.CheckedOutBy)
            && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        if (checkedOutByCurrentUser)
        {
            node.CheckoutSessionLost = true;
            node.WorkState = CadWorkState.EditingByOther;
            if (node.DocumentId.HasValue)
            {
                lock (checkoutDocumentSync)
                {
                    activeCheckoutDocumentIds.Remove(node.DocumentId.Value);
                }
            }
            taskPaneControl.SetTree(currentTree);
            ShowError(string.Concat(
                "该图档的编辑权限属于旧插件会话，当前会话不能",
                operation,
                "。请点击“获取权限”恢复本机已过期的编辑会话；若本地有修改，请先另存文件。"));
            return false;
        }

        ShowError(string.IsNullOrWhiteSpace(node?.CheckedOutBy)
            ? string.Concat("当前图档尚未获取编辑权限，不能", operation, "。")
            : string.Concat("当前图档正在由", node.CheckedOutBy, "编辑，不能", operation, "。"));
        return false;
    }

    private CadWorkState DetermineWorkState(
        CadTreeNode node,
        IReadOnlyDictionary<string, string> localHashes,
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>> versionsByDocument)
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

        if (node.DocumentId.HasValue
            && versionsByDocument.TryGetValue(node.DocumentId.Value, out var documentVersions)
            && !VersionFileNameMatches(documentVersions.FirstOrDefault(), node.FullPath))
        {
            return CadWorkState.PendingCheckIn;
        }

        if ((!string.IsNullOrWhiteSpace(node.LatestVersionSha256) || !string.IsNullOrWhiteSpace(node.LatestStoredSha256))
            && !string.IsNullOrWhiteSpace(node.FullPath)
            && localHashes.TryGetValue(node.FullPath, out var localSha256))
        {
            var matchesLatest = NodeMatchesLatestFile(node, localSha256);
            if (!matchesLatest
                && node.DocumentId.HasValue
                && versionsByDocument.TryGetValue(node.DocumentId.Value, out var versions))
            {
                matchesLatest = VersionMatchesLocalFile(versions.FirstOrDefault(), node.FullPath, localSha256);
            }

            if (!matchesLatest)
            {
                return CadWorkState.PendingCheckIn;
            }
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

    private sealed class WorkspaceUpdatePlan
    {
        public WorkspaceUpdatePlan(CadTreeNode node, DocumentVersionDto version, string stagedPath)
        {
            Node = node;
            Version = version;
            StagedPath = stagedPath;
        }

        public CadTreeNode Node { get; }
        public DocumentVersionDto Version { get; }
        public string StagedPath { get; }
        public string BackupPath { get; set; }
        public FileAttributes? OriginalAttributes { get; set; }
    }

    private enum RegistrationMatchKind
    {
        New,
        SameNameSameContent,
        SameNameDifferentContent,
        SameContentDifferentName,
        SameContentOtherProject
    }

    private sealed class RegistrationDecision
    {
        public RegistrationDecision(string sourceSha256, bool allowDuplicateContent, string duplicateReason)
        {
            SourceSha256 = sourceSha256;
            AllowDuplicateContent = allowDuplicateContent;
            DuplicateReason = duplicateReason;
        }

        public string SourceSha256 { get; }
        public bool AllowDuplicateContent { get; }
        public string DuplicateReason { get; }
    }

    private sealed class WorkspaceAcquireResult
    {
        public WorkspaceAcquireResult(int checkedOutFiles, int updatedFiles)
        {
            CheckedOutFiles = checkedOutFiles;
            UpdatedFiles = updatedFiles;
        }

        public int CheckedOutFiles { get; }
        public int UpdatedFiles { get; }
    }

    private sealed class BatchNodeCheckInResult
    {
        public BatchNodeCheckInResult(bool versionCreated)
        {
            VersionCreated = versionCreated;
        }

        public bool VersionCreated { get; }
    }

    private sealed class BatchCheckInPlan
    {
        public BatchCheckInPlan(IReadOnlyList<BatchOperationItem> items, int skippedFiles)
        {
            Items = items;
            SkippedFiles = skippedFiles;
        }

        public IReadOnlyList<BatchOperationItem> Items { get; }
        public int SkippedFiles { get; }
    }

    private sealed class BatchCheckInResult
    {
        public int CreatedVersions { get; set; }
        public int UnchangedFiles { get; set; }
        public List<string> Failures { get; } = new List<string>();
    }

    private sealed class AssemblyItemRename
    {
        public AssemblyItemRename(string oldName, string newName)
        {
            OldName = oldName;
            NewName = newName;
        }

        public string OldName { get; }
        public string NewName { get; }
    }
}
