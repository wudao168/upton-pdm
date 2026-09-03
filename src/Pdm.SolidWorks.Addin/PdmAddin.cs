using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Upton.Pdm.ClientShared;
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
    private IReadOnlyDictionary<string, string> userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    private string workspaceOperationDescription = string.Empty;
    private BatchProgressDialog activeBatchProgressDialog;
    private int treeRefreshInProgress;
    private int controlledOpenInProgress;
    private int refreshSuppressionDepth;
    private int pendingTreeRefresh;
    private int pendingPropertyWritebackDialog;
    private int openPropertyWritebackTab;
    private int openPropertyCardTab;
    private Guid? pendingPropertyWritebackProjectId;
    private int checkoutHeartbeatInProgress;
    private int staleCheckoutResolutionInProgress;
    private int automaticDrawingOperationInProgress;
    private readonly Guid checkoutSessionId = Guid.NewGuid();
    private readonly string checkoutMachineName = System.Environment.MachineName;
    private readonly object checkoutDocumentSync = new object();
    private readonly HashSet<Guid> activeCheckoutDocumentIds = new HashSet<Guid>();
    private readonly Dictionary<Guid, int> checkoutReminderLevels = new Dictionary<Guid, int>();
    private readonly Dictionary<Guid, HistoricalPartEditContext> historicalPartEditContexts = new Dictionary<Guid, HistoricalPartEditContext>();
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
            var bootstrap = ClientBootstrapLoader.LoadAsync(lifetime.Token).GetAwaiter().GetResult();
            apiClient = new PdmApiClient(bootstrap.ApiBaseUrl);
            apiClient.AuthenticationExpired += OnAuthenticationExpired;
            controlledWorkspace = new ControlledWorkspaceManager(apiClient);
            controlledOpenListener = new SolidWorksOpenRequestListener();
            controlledOpenListener.Start();
            scanner = new SolidWorksReferenceTreeScanner(application);
            taskPaneControl = new PdmTaskPaneControl();
            taskPaneControl.CreateControl();
            WireEvents();
            WireSolidWorksEvents();

            taskPaneView = application.CreateTaskpaneView2(CreateTaskPaneIcon(), "UPLM");
            taskPaneView.DisplayWindowFromHandlex64(taskPaneControl.Handle.ToInt64());
            taskPaneControl.SetConnectionState(false, "未登录");
            RefreshTree(false);
            _ = LoginRememberedCredentialsAsync();
            _ = MonitorClientUpdatesAsync(bootstrap);
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

    private async Task MonitorClientUpdatesAsync(ClientBootstrapConfiguration bootstrap)
    {
        while (!disconnecting && lifetime != null && !lifetime.IsCancellationRequested)
        {
            try
            {
                await ClientPackageUpdater.StageAsync(
                    "solidworks-addin",
                    bootstrap.SolidWorksAddin,
                    Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location),
                    lifetime.Token).ConfigureAwait(false);
                ClientPackageUpdater.TryLaunchPendingUpdate(
                    "solidworks-addin",
                    Process.GetCurrentProcess().Id,
                    string.Empty);
                await Task.Delay(TimeSpan.FromSeconds(bootstrap.PollSeconds), lifetime.Token).ConfigureAwait(false);
                bootstrap = await ClientBootstrapLoader.LoadAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDiagnostic("MonitorClientUpdates", exception);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, bootstrap.PollSeconds)), lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
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
            if (apiClient != null) apiClient.AuthenticationExpired -= OnAuthenticationExpired;

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
            pendingPropertyWritebackProjectId = null;
            Interlocked.Exchange(ref pendingPropertyWritebackDialog, 0);
            Interlocked.Exchange(ref openPropertyWritebackTab, 0);
            Interlocked.Exchange(ref openPropertyCardTab, 0);
            Interlocked.Exchange(ref openOperationInProgress, 0);
            Interlocked.Exchange(ref checkInOperationInProgress, 0);
            Interlocked.Exchange(ref workspaceOperationInProgress, 0);
            Interlocked.Exchange(ref controlledOpenInProgress, 0);
            Interlocked.Exchange(ref workspaceOperationDescription, string.Empty);
            scanner = null;
            lifetime = null;
            application = null;
            authenticatedUsername = string.Empty;
            availableProjects = Array.Empty<ProjectDto>();
            userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            currentDocumentIdentity = string.Empty;
            currentProjectId = null;
            controlledOpenManifests.Clear();
            explicitProjectPaths.Clear();
            pendingAssemblyItemRenames.Clear();
            lock (checkoutDocumentSync) activeCheckoutDocumentIds.Clear();
            checkoutReminderLevels.Clear();
            historicalPartEditContexts.Clear();
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
            addinKey?.SetValue("Title", "UPLM");
            addinKey?.SetValue("Description", "UPLM SolidWorks图档、版本与设计树插件");
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
        taskPaneControl.BatchPropertyCardRequested += OnBatchPropertyCardRequested;
        taskPaneControl.UpdateAllLatestRequested += OnUpdateAllLatestRequested;
        taskPaneControl.AutomaticDrawingGenerateRequested += OnAutomaticDrawingGenerateRequested;
        taskPaneControl.AutomaticDrawingOpenRequested += OnAutomaticDrawingOpenRequested;
        taskPaneControl.AutomaticDrawingImportAnnotationsRequested += OnAutomaticDrawingImportAnnotationsRequested;
        taskPaneControl.AutomaticDrawingSubmitRequested += OnAutomaticDrawingSubmitRequested;
        taskPaneControl.VersionsRequested += OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested += OnOpenHistoryRequested;
        taskPaneControl.EditHistoricalVersionRequested += OnEditHistoricalVersionRequested;
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
        taskPaneControl.BatchPropertyCardRequested -= OnBatchPropertyCardRequested;
        taskPaneControl.UpdateAllLatestRequested -= OnUpdateAllLatestRequested;
        taskPaneControl.AutomaticDrawingGenerateRequested -= OnAutomaticDrawingGenerateRequested;
        taskPaneControl.AutomaticDrawingOpenRequested -= OnAutomaticDrawingOpenRequested;
        taskPaneControl.AutomaticDrawingImportAnnotationsRequested -= OnAutomaticDrawingImportAnnotationsRequested;
        taskPaneControl.AutomaticDrawingSubmitRequested -= OnAutomaticDrawingSubmitRequested;
        taskPaneControl.VersionsRequested -= OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested -= OnOpenHistoryRequested;
        taskPaneControl.EditHistoricalVersionRequested -= OnEditHistoricalVersionRequested;
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
        var closedCurrentDocument = IsCurrentDocument(fileName);
        if (closedCurrentDocument)
        {
            ClearActiveDocumentContext();
        }

        LogOperation(string.Concat(
            "FileCloseNotify file=", fileName,
            " reason=", reason,
            " current=", closedCurrentDocument));
        ScheduleTreeRefresh();
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
            if (selectionManager == null)
            {
                return 0;
            }

            var selectedCount = selectionManager.GetSelectedObjectCount2(-1);
            if (selectedCount == 0)
            {
                taskPaneControl?.SelectRootNode();
                return 0;
            }
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

    private int OnSolidWorksIdle()
    {
        TryStartControlledOpenRequest();
        TryStartCheckoutHeartbeat();
        SynchronizeActiveDocumentContextOnIdle();
        TryRefreshPendingTree("IdleRefresh");
        return 0;
    }

    private void SynchronizeActiveDocumentContextOnIdle()
    {
        try
        {
            var activeDocument = application?.ActiveDoc as IModelDoc2;
            if (activeDocument == null)
            {
                if (currentTree != null
                    || currentProjectId.HasValue
                    || !string.IsNullOrWhiteSpace(currentDocumentIdentity))
                {
                    ClearActiveDocumentContext();
                    Interlocked.Exchange(ref pendingTreeRefresh, 0);
                    LogOperation("Idle context synchronized to no open document");
                }

                return;
            }

            var activePath = activeDocument.GetPathName();
            var activeIdentity = string.IsNullOrWhiteSpace(activePath)
                ? activeDocument.GetTitle()
                : activePath;
            if (currentTree == null
                || !string.Equals(currentDocumentIdentity, activeIdentity, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleTreeRefresh();
            }
        }
        catch (Exception exception)
        {
            // A closing COM document can become invalid between ActiveDoc and GetPathName.
            LogDiagnostic("SynchronizeActiveDocumentContextOnIdle", exception);
            ScheduleTreeRefresh();
        }
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
            TryOpenPendingPropertyWritebackDialog();
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

    private void TryOpenPendingPropertyWritebackDialog()
    {
        if (Volatile.Read(ref pendingPropertyWritebackDialog) == 0
            || currentTree == null
            || currentTree.IsReadOnlyPreview)
        {
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        if (!projectId.HasValue || projectId != pendingPropertyWritebackProjectId)
        {
            return;
        }

        Interlocked.Exchange(ref pendingPropertyWritebackDialog, 0);
        pendingPropertyWritebackProjectId = null;
        Interlocked.Exchange(ref openPropertyWritebackTab, 1);
        taskPaneControl.ShowStructureTab();
        OnBatchPropertyEditRequested(this, new CadTreeNodeEventArgs(currentTree));
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
        if (!node.DocumentId.HasValue)
        {
            return;
        }
        if (!node.CheckedOutAt.HasValue
            || !IsCheckedOutByCurrentUser(node)
            || node.CheckoutSessionId != checkoutSessionId)
        {
            checkoutReminderLevels.Remove(node.DocumentId.Value);
            return;
        }
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

    private void OnAuthenticationExpired(object sender, EventArgs eventArgs)
    {
        if (disconnecting)
        {
            return;
        }

        authenticatedUsername = string.Empty;
        availableProjects = Array.Empty<ProjectDto>();
        userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        taskPaneControl?.SetAuthenticatedUser(string.Empty, string.Empty);
        taskPaneControl?.SetConnectionState(false, "登录已过期");
        taskPaneControl?.SetCheckoutReminder("PLM登录已过期，请点击右上角“登录”重新登录。", true);
        LogOperation("Authentication expired; stopped authenticated requests");
    }

    private void OnOpenClientRequested(object sender, EventArgs eventArgs)
    {
        try
        {
            var arguments = currentProjectId.HasValue
                ? string.Concat("--project ", currentProjectId.Value.ToString("D"), " --tab documents")
                : string.Empty;
            StartDesktopClient(arguments);
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
        taskPaneControl.SetCheckoutReminder(string.Empty, false);
        var projects = await apiClient.GetProjectsAsync(lifetime.Token);
        availableProjects = projects;
        await RefreshUserDisplayNamesAsync();
        taskPaneControl.SetProjects(projects);
        if (currentTree != null && application?.ActiveDoc != null)
        {
            await ResolveProjectForCurrentDocumentAsync(currentTree, DocumentIdentity(currentTree));
        }
        TryStartControlledOpenRequest();
    }

    private async Task RefreshUserDisplayNamesAsync()
    {
        try
        {
            var directory = await apiClient.GetOrganizationDirectoryAsync(lifetime.Token);
            userDisplayNames = (directory?.Users ?? new List<OrganizationDirectoryUserDto>())
                .Where(user => !string.IsNullOrWhiteSpace(user?.Username) && !string.IsNullOrWhiteSpace(user.DisplayName))
                .GroupBy(user => user.Username.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (lifetime == null || lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LogDiagnostic("RefreshUserDisplayNames", exception);
        }
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
                "UPLM",
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
            string.Equals(component.Name2, node.ComponentSelectionName, StringComparison.OrdinalIgnoreCase));
        if (selectedComponent == null && !string.IsNullOrWhiteSpace(node.FullPath))
        {
            var pathMatches = components.Where(component =>
                {
                    try
                    {
                        return PathsEqual(component.GetPathName(), node.FullPath);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Take(2)
                .ToArray();
            selectedComponent = pathMatches.Length == 1 ? pathMatches[0] : null;
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
                MessageBox.Show(taskPaneControl, "释放申请已发送。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show(taskPaneControl, "审批已撤回，图档已恢复为工作中。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                MessageBox.Show(taskPaneControl, "图档已受控作废。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (node.IsReadOnlyPreview)
            {
                throw new InvalidOperationException("只读预览不能重命名。");
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
                throw new InvalidOperationException("SolidWorks未识别到该零部件实例，请刷新设计树后重试。");
            }

            var assemblyModel = application?.ActiveDoc as IModelDoc2
                ?? throw new InvalidOperationException("请先打开包含该图档的装配体。");
            if (!PathsEqual(assemblyModel.GetPathName(), currentTree.FullPath)
                || !renamingRoot && assemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException("请先激活当前设计树对应的装配体。");
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
                    PdmDocumentIdentityStore.TryWrite(newPath, node.DocumentId.Value, currentProjectId);
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
                    throw new InvalidOperationException("SolidWorks无法选中该零部件，请刷新设计树后重试。");
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
                return "SolidWorks拒绝了本次PLM环境下的重命名操作。";
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
                    ? "请先在设计树中为工程图获取编辑权限，再提交存档。"
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
        if (source.IsReadOnlyPreview || IsReadOnlyPreviewPath(source.FullPath))
        {
            throw new InvalidOperationException("只读预览不能生成或更新当前工程图。");
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
                "PLM自动中心线",
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
        if (IsReadOnlyPreviewPath(drawingPath))
        {
            throw new InvalidOperationException("只读预览工程图不能更新。");
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
        target.ProvenanceDocumentId = source.ProvenanceDocumentId;
        target.ProvenanceProjectId = source.ProvenanceProjectId;
        target.IsExternalProvenance = source.IsExternalProvenance;
        target.DrawingNumber = source.DrawingNumber;
        target.Description = source.Description;
        target.Material = source.Material;
        target.LifecycleState = source.LifecycleState;
        target.DrawingReviewLocked = source.DrawingReviewLocked;
        target.UpdatedAt = source.UpdatedAt;
        target.Revision = source.Revision;
        target.CurrentRevision = source.CurrentRevision;
        target.LatestRevision = source.LatestRevision;
        target.StoredVersionStateKnown = source.StoredVersionStateKnown;
        target.HasStoredVersion = source.HasStoredVersion;
        target.LatestVersionSha256 = source.LatestVersionSha256;
        target.LatestStoredSha256 = source.LatestStoredSha256;
        target.CheckedOutBy = source.CheckedOutBy;
        target.CheckedOutAt = source.CheckedOutAt;
        target.CheckoutSessionId = source.CheckoutSessionId;
        target.CheckoutMachine = source.CheckoutMachine;
        target.CheckoutLastHeartbeatAt = source.CheckoutLastHeartbeatAt;
        target.CheckoutSessionLost = source.CheckoutSessionLost;
        target.WorkState = source.WorkState;
        target.IsHistoricalPreview = source.IsHistoricalPreview;
        target.IsLatestReadOnlyPreview = source.IsLatestReadOnlyPreview;
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
            ShowError("请先登录PLM。");
            return;
        }
        if (node == null || !node.DocumentId.HasValue)
        {
            ShowError("该图档尚未入库，不能获取最新版本。");
            return;
        }
        if (node.IsReadOnlyPreview || IsReadOnlyPreviewContext(node))
        {
            ShowError("只读预览不能原地更新；请使用“获取权限”切换到编辑工作区。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }
        if (!TryBeginWorkspaceOperation("正在获取最新版本"))
        {
            ShowWorkspaceOperationBusy();
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
                ?? throw new InvalidOperationException("当前项目中未找到该图档，请刷新设计树后重试。");
            ApplyCheckoutDocument(node, serverDocument);
            ValidateUpdateLatestNode(node);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该图档尚无可获取的PLM版本。");
            ApplyLatestVersion(node, latest);

            if (File.Exists(node.FullPath))
            {
                var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), lifetime.Token);
                if (VersionMatchesLocalFile(latest, node.FullPath, localSha256))
                {
                    ApplyUpdatedWorkingVersion(node, latest);
                    taskPaneControl.SetTree(currentTree);
                    MessageBox.Show(taskPaneControl, "当前工作文件已是最新版本。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!versions.Any(version => VersionMatchesLocalFile(version, node.FullPath, localSha256)))
                {
                    throw new InvalidOperationException(string.Concat(
                        node.FileName,
                        "的本地内容无法对应任何PLM历史版本，可能存在待提交修改。为避免覆盖，已停止更新。"));
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
                "UPLM",
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
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private async void OnUpdateAllLatestRequested(object sender, EventArgs eventArgs)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }
        if (currentTree == null || currentTree.IsReadOnlyPreview)
        {
            ShowError("请先打开当前工作装配，只读预览不能更新工作区。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }

        RefreshLoadedDocumentModificationFlags(currentTree);
        var candidates = DistinctBatchItems(BuildBatchOperationItems(currentTree))
            .Select(item => item.Node)
            .Where(IsVersionOutdatedForWorkspaceUpdate)
            .ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show(taskPaneControl, "当前结构没有版本落后的受控图档。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            taskPaneControl,
            string.Concat(
                "将检查并更新 ", candidates.Length, " 个版本落后的图档。\r\n",
                "正在编辑、存在未保存修改或本地内容无法对应PLM历史版本的图档会自动跳过，不会覆盖。是否继续？"),
            "整体更新到最新版",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }
        if (!TryBeginWorkspaceOperation("正在整体更新到最新版"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }

        var stagedPaths = new List<string>();
        var reopenNodes = new List<CadTreeNode>();
        BatchProgressDialog progressDialog = null;
        CancellationTokenSource cancellation = null;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            progressDialog = new BatchProgressDialog(candidates.Length);
            progressDialog.CancelRequested += (_, __) => cancellation.Cancel();
            progressDialog.Show(taskPaneControl);
            progressDialog.BringToFront();
            progressDialog.Activate();
            activeBatchProgressDialog = progressDialog;

            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, cancellation.Token);
            var documentsById = projectDocuments.ToDictionary(document => document.Id);
            var eligible = new List<CadTreeNode>();
            var skipped = new List<string>();
            foreach (var node in candidates)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    if (node.DocumentId.HasValue && documentsById.TryGetValue(node.DocumentId.Value, out var serverDocument))
                    {
                        ApplyCheckoutDocument(node, serverDocument);
                    }
                    ValidateUpdateLatestNode(node);
                    eligible.Add(node);
                }
                catch (Exception exception)
                {
                    skipped.Add(string.Concat(node.FileName, "：", exception.Message));
                }
            }

            var preparations = new List<LatestWorkspacePreparation>();
            const int preparationBatchSize = 8;
            for (var offset = 0; offset < eligible.Count; offset += preparationBatchSize)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var batch = eligible.Skip(offset).Take(preparationBatchSize).ToArray();
                var batchResults = await Task.WhenAll(batch.Select(node =>
                    PrepareLatestWorkspaceUpdateAsync(node, cancellation.Token)));
                preparations.AddRange(batchResults);
                foreach (var preparation in batchResults)
                {
                    if (!string.IsNullOrWhiteSpace(preparation.StagedPath))
                    {
                        stagedPaths.Add(preparation.StagedPath);
                    }
                    if (!string.IsNullOrWhiteSpace(preparation.SkipReason))
                    {
                        skipped.Add(string.Concat(preparation.Node.FileName, "：", preparation.SkipReason));
                    }
                    progressDialog.ReportFile(
                        Math.Min(offset + Array.IndexOf(batchResults, preparation) + 1, eligible.Count),
                        eligible.Count,
                        preparation.Node.FileName,
                        string.IsNullOrWhiteSpace(preparation.SkipReason)
                            ? preparation.AlreadyLatest ? "本地已是最新版。" : "已准备最新版。"
                            : "已跳过。" );
                }
            }

            var alreadyLatest = 0;
            var plans = new List<WorkspaceUpdatePlan>();
            foreach (var preparation in preparations)
            {
                if (preparation.Latest != null)
                {
                    ApplyLatestVersion(preparation.Node, preparation.Latest);
                }
                if (!string.IsNullOrWhiteSpace(preparation.SkipReason))
                {
                    continue;
                }
                if (preparation.AlreadyLatest)
                {
                    ApplyUpdatedWorkingVersion(preparation.Node, preparation.Latest);
                    alreadyLatest++;
                    continue;
                }
                plans.Add(new WorkspaceUpdatePlan(preparation.Node, preparation.Latest, preparation.StagedPath));
            }

            if (plans.Count > 0)
            {
                var rootDocument = FindLoadedDocument(currentTree.FullPath);
                if (rootDocument?.GetSaveFlag() == true)
                {
                    throw new InvalidOperationException("顶层装配体存在未保存修改。为避免关闭时丢失修改，整体更新已停止；请先保存或放弃修改。");
                }

                if (rootDocument != null)
                {
                    reopenNodes.Add(currentTree);
                }
                foreach (var plan in plans.Where(plan => FindLoadedDocument(plan.Node.FullPath) != null))
                {
                    if (!reopenNodes.Any(node => PathsEqual(node.FullPath, plan.Node.FullPath)))
                    {
                        reopenNodes.Add(plan.Node);
                    }
                }

                CloseDocumentsForWorkspaceUpdate(plans.Select(plan => plan.Node.FullPath), currentTree.FullPath);
                ApplyWorkspaceUpdates(plans);
                foreach (var plan in plans)
                {
                    ApplyUpdatedWorkingVersion(plan.Node, plan.Version);
                }
            }

            taskPaneControl.SetTree(currentTree);
            var summary = string.Concat(
                "整体更新完成。\r\n已更新：", plans.Count, "个",
                "\r\n本地原本已是最新版：", alreadyLatest, "个",
                "\r\n安全跳过：", skipped.Count, "个",
                plans.Count > 0 ? "\r\n被替换的原文件已保留在本地工作区备份目录。" : string.Empty);
            if (skipped.Count > 0)
            {
                summary = string.Concat(summary, "\r\n\r\n", string.Join("\r\n", skipped.Take(8)), skipped.Count > 8 ? "\r\n……" : string.Empty);
            }
            progressDialog.Complete(summary, skipped.Count > 0);
        }
        catch (OperationCanceledException)
        {
            progressDialog?.Fail("整体更新已取消；尚未替换的文件保持不变。");
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnUpdateAllLatestRequested", exception);
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
            foreach (var stagedPath in stagedPaths)
            {
                DeleteWorkspaceStage(stagedPath);
            }
            foreach (var node in reopenNodes)
            {
                if (!string.IsNullOrWhiteSpace(node.FullPath)
                    && File.Exists(node.FullPath)
                    && FindLoadedDocument(node.FullPath) == null)
                {
                    OpenOrActivateDocumentOnSolidWorksThread(
                        node.FullPath,
                        ToSolidWorksDocumentType(node.Kind),
                        node.Configuration ?? string.Empty);
                }
            }
            if (ReferenceEquals(activeBatchProgressDialog, progressDialog))
            {
                activeBatchProgressDialog = null;
            }
            cancellation?.Dispose();
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private async Task<LatestWorkspacePreparation> PrepareLatestWorkspaceUpdateAsync(
        CadTreeNode node,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
            var latest = versions.FirstOrDefault();
            if (latest == null)
            {
                return new LatestWorkspacePreparation(node, null, string.Empty, false, "PLM中尚无可获取的存档版本。");
            }

            if (File.Exists(node.FullPath))
            {
                var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
                if (VersionMatchesLocalFile(latest, node.FullPath, localSha256))
                {
                    return new LatestWorkspacePreparation(node, latest, string.Empty, true, string.Empty);
                }
                if (!versions.Any(version => VersionMatchesLocalFile(version, node.FullPath, localSha256)))
                {
                    return new LatestWorkspacePreparation(node, latest, string.Empty, false, "本地内容无法对应任何PLM历史版本，可能存在待提交修改。");
                }
            }

            var stagedPath = await apiClient.DownloadVersionToWorkspaceStageAsync(
                node.DocumentId.Value,
                latest.Id,
                node.FileName,
                latest.Sha256,
                cancellationToken);
            return new LatestWorkspacePreparation(node, latest, stagedPath, false, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LatestWorkspacePreparation(node, null, string.Empty, false, exception.Message);
        }
    }

    private static bool IsVersionOutdatedForWorkspaceUpdate(CadTreeNode node) =>
        node != null
        && node.DocumentId.HasValue
        && !string.IsNullOrWhiteSpace(node.LatestRevision)
        && !string.IsNullOrWhiteSpace(node.CurrentRevision)
        && !string.Equals(node.CurrentRevision.TrimEnd('*'), node.LatestRevision, StringComparison.OrdinalIgnoreCase);

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
            throw new InvalidOperationException("该图档存在未保存修改或待提交内容，不能用PLM版本覆盖。");
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

    private void BeginControlledOpen(
        Guid projectId,
        Guid documentId,
        ControlledOpenMode mode,
        Guid? versionId = null,
        Guid? checkoutDocumentId = null)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM，再打开受控图档。");
            return;
        }
        if (Interlocked.Exchange(ref controlledOpenInProgress, 1) != 0)
        {
            ShowError("已有受控图档正在准备，请稍候。");
            return;
        }
        if (!TryBeginWorkspaceOperation("阶段1/3：正在读取PLM受控清单"))
        {
            Interlocked.Exchange(ref controlledOpenInProgress, 0);
            ShowWorkspaceOperationBusy();
            return;
        }

        ExecuteControlledOpenAsync(projectId, documentId, mode, versionId, checkoutDocumentId);
    }

    private async void ExecuteControlledOpenAsync(
        Guid requestedProjectId,
        Guid documentId,
        ControlledOpenMode mode,
        Guid? versionId,
        Guid? checkoutDocumentId)
    {
        string rootPath = null;
        string openWarning = null;
        Guid? missingReferenceRecoveryProjectId = null;
        Guid? preparedProjectId = null;
        try
        {
            var requestsLatestEdit = mode == ControlledOpenMode.LatestEdit;
            var opensPropertyWriteback = mode == ControlledOpenMode.PropertyWriteback;
            var opensLatestWorkingCopy = requestsLatestEdit || mode == ControlledOpenMode.LatestReadOnly || opensPropertyWriteback;
            var releasedOnly = mode == ControlledOpenMode.LatestReleased;
            var specificVersionId = mode == ControlledOpenMode.SpecificReadOnly ? versionId : null;
            LogOperation(string.Concat(
                "ControlledOpen manifest start project=",
                requestedProjectId,
                " document=",
                documentId,
                " mode=",
                mode));
            var manifest = await apiClient.CreateControlledOpenManifestAsync(
                documentId,
                specificVersionId,
                releasedOnly,
                requestsLatestEdit,
                lifetime.Token);
            LogOperation(string.Concat(
                "ControlledOpen manifest ready document=",
                documentId,
                " files=",
                manifest.Files?.Count ?? 0,
                " warnings=",
                manifest.Warnings?.Count ?? 0));
            taskPaneControl.SetWorkspaceOperationProgress(
                string.Concat("阶段2/3：正在准备本地文件（", manifest.Files?.Count ?? 0, "项）"),
                0,
                manifest.Files?.Count ?? 0);
            preparedProjectId = manifest.ProjectId;
            if (requestedProjectId != Guid.Empty && manifest.ProjectId != requestedProjectId)
            {
                throw new InvalidOperationException("客户端所选项目与图档所属项目不一致，已停止打开。请刷新项目图档后重试。");
            }
            if (opensLatestWorkingCopy)
            {
                var documents = await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
                var manifestDocumentIds = new HashSet<Guid>(manifest.Files.Select(file => file.DocumentId));
                var editTargetDocumentId = checkoutDocumentId ?? documentId;
                if (requestsLatestEdit && !manifestDocumentIds.Contains(editTargetDocumentId))
                {
                    throw new InvalidOperationException("所选图档不在当前受控装配清单中，不能切换到编辑工作区。");
                }

                var editableDocumentIds = new HashSet<Guid>(documents
                    .Where(item => manifestDocumentIds.Contains(item.Id)
                        && string.Equals(item.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                        && item.CheckoutSessionId == checkoutSessionId)
                    .Select(item => item.Id));
                foreach (var document in documents) TrackCheckoutDocument(document);
                var reusedWorkingCopy = controlledWorkspace.TryGetExistingWorkingRoot(
                    manifest,
                    out rootPath,
                    out var workingMatchesControlledFiles);
                if (!reusedWorkingCopy)
                {
                    rootPath = await controlledWorkspace.PrepareWorkingCopyAsync(manifest, editableDocumentIds, lifetime.Token);
                }
                else if (!workingMatchesControlledFiles)
                {
                    openWarning = "已保留Working工作区中可能尚未提交的本地文件；获取权限后请核对并提交或放弃编辑。";
                    LogOperation(string.Concat("ControlledOpen preserved local Working changes document=", documentId));
                }
                var preservedModifiedWorkingCopy = reusedWorkingCopy && !workingMatchesControlledFiles;
                if (requestsLatestEdit)
                {
                    var previous = documents.FirstOrDefault(item => item.Id == editTargetDocumentId);
                    var checkoutCreated = previous == null || string.IsNullOrWhiteSpace(previous.CheckedOutBy);
                    try
                    {
                        var checkedOut = await apiClient.CheckoutAsync(
                            editTargetDocumentId,
                            checkoutSessionId,
                            checkoutMachineName,
                            lifetime.Token);
                        TrackCheckoutDocument(checkedOut);
                        editableDocumentIds.Add(editTargetDocumentId);
                        rootPath = controlledWorkspace.ApplyWorkingPermissions(
                            manifest,
                            editableDocumentIds,
                            preservedModifiedWorkingCopy);
                        EnsureLoadedDocumentEditable(controlledWorkspace.GetWorkingFilePath(manifest, editTargetDocumentId));
                    }
                    catch
                    {
                        if (checkoutCreated)
                        {
                            try
                            {
                                var discarded = await apiClient.DiscardCheckoutAsync(editTargetDocumentId, checkoutSessionId, lifetime.Token);
                                TrackCheckoutDocument(discarded);
                            }
                            catch (Exception rollbackException)
                            {
                                LogDiagnostic(string.Concat("ControlledOpen checkout rollback.", editTargetDocumentId), rollbackException);
                            }
                        }
                        throw;
                    }
                }
                else if (reusedWorkingCopy)
                {
                    rootPath = controlledWorkspace.ApplyWorkingPermissions(
                        manifest,
                        editableDocumentIds,
                        preservedModifiedWorkingCopy);
                }
            }
            else
            {
                rootPath = await controlledWorkspace.PrepareReadOnlyAsync(
                    manifest,
                    mode == ControlledOpenMode.SpecificReadOnly,
                    lifetime.Token);
            }

            RememberControlledOpenManifest(manifest, Path.GetDirectoryName(rootPath));

            RememberExplicitProjectPath(rootPath, manifest.ProjectId);
            if (manifest.Warnings?.Count > 0)
            {
                var manifestWarning = string.Concat(
                    "打开不完整：",
                    string.Join("；", manifest.Warnings.Take(3)),
                    manifest.Warnings.Count > 3 ? $"；另有{manifest.Warnings.Count - 3}项" : string.Empty);
                openWarning = string.IsNullOrWhiteSpace(openWarning)
                    ? manifestWarning
                    : string.Concat(openWarning, "\r\n", manifestWarning);
                LogOperation(string.Concat("ControlledOpen warnings=", string.Join(" | ", manifest.Warnings)));
            }
            LogOperation(string.Concat("ControlledOpen prepared project=", manifest.ProjectCode, " document=", documentId, " revision=", manifest.RootRevision, " files=", manifest.Files.Count, " working=", opensLatestWorkingCopy, " checkoutCreated=", requestsLatestEdit));
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
            EndWorkspaceOperation();
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
            QueueOpenDocument(
                rootPath,
                DocumentKindFromPath(rootPath),
                string.Empty,
                mode == ControlledOpenMode.PropertyWriteback,
                preparedProjectId);
            if (!string.IsNullOrWhiteSpace(openWarning))
            {
                taskPaneControl.SetCheckoutReminder(openWarning, true);
            }
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

    private void QueueOpenDocument(
        string fullPath,
        CadDocumentKind kind,
        string configuration,
        bool openPropertyWriteback = false,
        Guid? propertyWritebackProjectId = null)
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
            ShowWorkspaceOperationBusy();
            return;
        }

        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed || Interlocked.Exchange(ref openOperationInProgress, 1) != 0)
        {
            return;
        }

        if (!TryBeginWorkspaceOperation("阶段3/3：正在由SolidWorks打开图档"))
        {
            Interlocked.Exchange(ref openOperationInProgress, 0);
            ShowWorkspaceOperationBusy();
            return;
        }

        try
        {
            LogOperation(string.Concat("Open queued path=", fullPath));
            taskPaneControl.BeginInvoke((Action)(() => OpenDocumentOnSolidWorksThread(
                fullPath,
                documentType,
                configuration ?? string.Empty,
                openPropertyWriteback,
                propertyWritebackProjectId)));
        }
        catch (Exception exception)
        {
            EndWorkspaceOperation();
            Interlocked.Exchange(ref openOperationInProgress, 0);
            LogDiagnostic("QueueOpenDocument", exception);
            ShowError(string.Concat("打开图档失败：", exception.Message));
        }
    }

    private void OpenDocumentOnSolidWorksThread(
        string fullPath,
        int documentType,
        string configuration,
        bool openPropertyWriteback = false,
        Guid? propertyWritebackProjectId = null)
    {
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            if (disconnecting || application == null)
            {
                return;
            }

            var openedDocument = OpenOrActivateDocumentOnSolidWorksThread(fullPath, documentType, configuration);
            if (openedDocument != null && openPropertyWriteback && propertyWritebackProjectId.HasValue)
            {
                pendingPropertyWritebackProjectId = propertyWritebackProjectId;
                Interlocked.Exchange(ref pendingPropertyWritebackDialog, 1);
            }
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
            EndWorkspaceOperation();
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
        var requestedNodes = eventArgs.Nodes
            .Where(candidate => candidate != null)
            .GroupBy(candidate => candidate.FullPath ?? candidate.InstancePath ?? candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var node = requestedNodes.FirstOrDefault() ?? eventArgs.Node;
        if (node == null || !apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
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
        if (!EnsureProjectArchiveAccess(currentProjectId.Value, "获取编辑权限"))
        {
            return;
        }

        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null;
        var acquireItems = requestedNodes.Select(candidate => new BatchOperationItem(candidate, 0)).ToArray();
        if (eventArgs.UsesCheckedSelection && requestedNodes.Length > 1)
        {
            if (requestedNodes.Any(IsReadOnlyPreviewContext))
            {
                ShowError("勾选项中包含只读预览，不能批量获取编辑权限。");
                return;
            }

            try
            {
                await ValidateBatchProjectAsync(acquireItems, currentProjectId.Value);
                var inheritedDrawingProjects = await ResolveInheritedDrawingProjectsAsync(
                    acquireItems,
                    currentProjectId.Value);
                var newDocumentCount = acquireItems.Count(item =>
                    BatchOperationDialog.RequiresNewDocumentProjectConfirmation(
                        item,
                        currentProjectId,
                        inheritedDrawingProjects));
                if (newDocumentCount > 0)
                {
                    if (!ConfirmNewDocumentProject(currentProjectId.Value, newDocumentCount))
                    {
                        return;
                    }

                    registrationDecisions = await ReviewRegistrationDuplicatesAsync(acquireItems, currentProjectId.Value);
                    if (registrationDecisions == null)
                    {
                        return;
                    }

                    await ValidateBatchProjectAsync(acquireItems, currentProjectId.Value);
                }
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
                return;
            }
        }

        if (IsLatestReadOnlyPreviewContext(node))
        {
            if (!node.DocumentId.HasValue || currentTree?.DocumentId == null)
            {
                ShowError("最新只读预览缺少受控图档标识，不能切换到编辑工作区。");
                return;
            }

            BeginControlledOpen(
                currentProjectId.Value,
                currentTree.DocumentId.Value,
                ControlledOpenMode.LatestEdit,
                null,
                node.DocumentId.Value);
            return;
        }

        if (!node.DocumentId.HasValue && acquireItems.Length == 1)
        {
            OpenBatchOperationDialog(
                currentProjectId,
                BatchOperationKind.AcquireLatestAndCheckout,
                string.IsNullOrWhiteSpace(node.FullPath) ? null : new[] { node.FullPath });
            return;
        }

        if (!TryBeginWorkspaceOperation("正在获取编辑权限"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }

        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            var result = await AcquireLatestAndCheckoutAsync(
                acquireItems,
                currentProjectId.Value,
                registrationDecisions);
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.RefreshAutomaticDrawingState();
            if (acquireItems.Any(item => item.Node.Kind == CadDocumentKind.Drawing))
            {
                taskPaneControl.SetAutomaticDrawingOperationResult("已获取工程图权限，可更新、手工编辑或重新执行自动标注。");
            }
            LogOperation(string.Concat(
                "Checkout completed files=", result.CheckedOutFiles,
                " updated=", result.UpdatedFiles,
                " selected=", acquireItems.Length));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private void OnBatchOperationRequested(object sender, EventArgs eventArgs)
    {
        OpenBatchOperationDialog(null, BatchOperationKind.CheckIn);
    }

    private void OnBatchPropertyCardRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        Interlocked.Exchange(ref openPropertyCardTab, 1);
        OnBatchPropertyEditRequested(this, eventArgs ?? new CadTreeNodeEventArgs(currentTree));
    }

    private async void OnBatchPropertyEditRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var openPropertyCard = Interlocked.Exchange(ref openPropertyCardTab, 0) != 0;
        var initialOperation = openPropertyCard
            ? PropertyOperationMode.PropertyCardAssignment
            : Interlocked.Exchange(ref openPropertyWritebackTab, 0) != 0
                ? PropertyOperationMode.PropertyWriteback
                : PropertyOperationMode.BatchEdit;
        if (initialOperation == PropertyOperationMode.PropertyWriteback && !apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }
        if (currentTree == null || currentTree.IsReadOnlyPreview)
        {
            ShowError("请先打开当前工作装配，只读预览不能执行属性操作。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        if (initialOperation == PropertyOperationMode.PropertyWriteback && !projectId.HasValue)
        {
            ShowError("请先选择当前项目。");
            return;
        }
        var operationItems = Array.Empty<BatchOperationItem>();
        var initiallySelectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (initialOperation != PropertyOperationMode.PropertyWriteback)
        {
            RefreshLoadedDocumentModificationFlags(currentTree);
            initiallySelectedPaths = new HashSet<string>((eventArgs.Nodes ?? Array.Empty<CadTreeNode>())
                .Where(node => !string.IsNullOrWhiteSpace(node?.FullPath))
                .Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
            operationItems = BuildBatchOperationItems(currentTree)
                .Where(item => !string.IsNullOrWhiteSpace(item.Node.FullPath)
                    && File.Exists(item.Node.FullPath)
                    && item.Node.Status != CadReferenceStatus.Virtual
                    && !IsSolidWorksTemporaryVirtualComponentPath(item.Node.FullPath))
                .ToArray();
            if (operationItems.Length == 0)
            {
                ShowError("当前结构中没有可编辑的SolidWorks图档。");
                return;
            }
        }

        try
        {
            IReadOnlyList<DocumentDto> projectDocuments = Array.Empty<DocumentDto>();
            ProjectDto selectedProject = null;
            if (initialOperation == PropertyOperationMode.PropertyWriteback
                && projectId.HasValue
                && apiClient.IsAuthenticated)
            {
                projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
                selectedProject = availableProjects.FirstOrDefault(project => project.Id == projectId.Value);
                if (selectedProject == null)
                {
                    throw new InvalidOperationException("当前归属项目不在用户的可用项目列表中，请刷新项目权限后重试。");
                }
            }
            var nativePropertyCards = DiscoverNativePropertyCards();
            var editItems = initialOperation != PropertyOperationMode.PropertyWriteback
                ? BuildBatchPropertyEditItems(
                    operationItems,
                    string.Empty,
                    string.Empty,
                    nativePropertyCards)
                : Array.Empty<BatchPropertyEditItem>();
            foreach (var item in editItems)
            {
                item.Selected = initiallySelectedPaths.Contains(item.OperationItem.Node.FullPath);
            }
            var nodesByDocument = EnumerateCadNodes(currentTree)
                .Where(node => node.DocumentId.HasValue && !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath))
                .GroupBy(node => node.DocumentId.Value)
                .ToDictionary(group => group.Key, group => group.First());
            var queuedWritebacks = initialOperation == PropertyOperationMode.PropertyWriteback
                ? (await apiClient.GetCadPropertyWritebacksAsync(projectId.Value, lifetime.Token))
                    .Where(item => item.Status == 0)
                    .ToArray()
                : Array.Empty<CadPropertyWritebackDto>();
            var documentsById = projectDocuments.ToDictionary(document => document.Id);
            var availableWritebacks = queuedWritebacks
                .Where(item => nodesByDocument.ContainsKey(item.SourceDocumentId)
                    && documentsById.TryGetValue(item.SourceDocumentId, out var document)
                    && document.Revision != null
                    && string.Equals(document.Revision.Display, item.ExpectedRevision, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(document.CheckedOutBy)
                        || string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                    && item.Properties?.Count > 0)
                .ToArray();
            var writebackPreviews = availableWritebacks.Select(item => new PropertyWritebackPreviewItem(
                item.Id,
                nodesByDocument[item.SourceDocumentId].FileName,
                item.ExpectedRevision,
                item.Properties == null || item.Properties.Count == 0
                    ? "-"
                    : string.Join("；", item.Properties.Select(pair => string.Concat(pair.Key, "=", pair.Value)).Take(8)),
                item.RequestedBy,
                item.RequestedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
                .ToArray();
            using (var dialog = new BatchPropertyEditDialog(
                editItems,
                writebackPreviews,
                queuedWritebacks.Length - availableWritebacks.Length,
                initialOperation,
                nativePropertyCards,
                initialOperation != PropertyOperationMode.PropertyWriteback
                    ? new Func<IReadOnlyList<BatchPropertyEditItem>, Action<string, int, int>, int>(
                        (selectedItems, reportProgress) => ReadCurrentBatchPropertyCardValues(
                            selectedItems,
                            nativePropertyCards,
                            reportProgress))
                    : null,
                initialOperation == PropertyOperationMode.BatchEdit && projectId.HasValue
                    ? new Func<IReadOnlyList<BatchPropertyEditItem>, Task<int>>(selectedItems =>
                        SynchronizeBatchPropertiesFromPlmAsync(selectedItems, projectId.Value))
                    : null,
                (changedItems, settingPropertyCards, reportProgress) =>
                    ExecuteLocalBatchPropertyOperation(changedItems, settingPropertyCards, reportProgress)))
            {
                if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
                {
                    return;
                }

                if (dialog.SelectedOperation == PropertyOperationMode.PropertyWriteback)
                {
                    var selectedWritebackIds = new HashSet<Guid>(dialog.SelectedWritebackIds);
                    await ExecuteCadPropertyWritebackAsync(
                        projectId.Value,
                        availableWritebacks.Where(item => selectedWritebackIds.Contains(item.Id)).ToArray(),
                        nodesByDocument,
                        dialog.ChangeNote);
                    return;
                }

                return;
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnBatchPropertyEditRequested", exception);
            ShowError(exception.Message);
        }
    }

    private int ReadCurrentBatchPropertyCardValues(
        IReadOnlyList<BatchPropertyEditItem> selectedItems,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards,
        Action<string, int, int> reportProgress)
    {
        var items = (selectedItems ?? Array.Empty<BatchPropertyEditItem>())
            .Where(item => item?.OperationItem?.Node != null)
            .ToArray();
        if (items.Length == 0)
        {
            throw new InvalidOperationException("当前没有可读取属性卡的图档。");
        }
        if (!TryBeginWorkspaceOperation("正在读取当前属性卡"))
        {
            throw new InvalidOperationException("已有工作文件操作正在进行，请稍候再试。");
        }

        Interlocked.Increment(ref refreshSuppressionDepth);
        var succeeded = 0;
        try
        {
            taskPaneControl.UseWaitCursor = true;
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var node = item.OperationItem.Node;
                IModelDoc2 document = null;
                var openedForRead = false;
                reportProgress?.Invoke(
                    string.Concat("读取当前属性卡", System.Environment.NewLine, item.FileName),
                    index,
                    items.Length);
                try
                {
                    document = FindLoadedDocument(node.FullPath);
                    if (document == null)
                    {
                        document = OpenDocumentInvisiblyForBatch(
                            node.FullPath,
                            ToSolidWorksDocumentType(node.Kind),
                            item.ConfigurationName);
                        openedForRead = true;
                    }

                    var activePropertyCard = ResolveActiveNativePropertyCard(node, nativePropertyCards);
                    item.SetActivePropertyCard(activePropertyCard);
                    var localProperties = ReadModelProperties(document, item.ConfigurationName);
                    ExtractBatchPropertyValues(
                        localProperties,
                        item.ConfigurationName,
                        out var values,
                        out var scopes,
                        out var propertyNames);
                    ApplyPropertyCardFieldScopes(
                        localProperties,
                        item.ConfigurationName,
                        activePropertyCard?.Fields,
                        node.Kind,
                        values,
                        scopes,
                        propertyNames);
                    item.ApplyLocalPropertyCardValues(values, scopes, propertyNames);
                    succeeded++;
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("ReadCurrentBatchPropertyCardValues.", item.FileName), exception);
                }
                finally
                {
                    if (openedForRead && document != null)
                    {
                        CloseBatchOpenedDocument(document);
                    }
                    reportProgress?.Invoke(
                        string.Concat("已读取", System.Environment.NewLine, item.FileName),
                        index + 1,
                        items.Length);
                }
            }

            LogOperation(string.Concat(
                "Current native property card values read documents=",
                succeeded,
                "/",
                items.Length));
            return succeeded;
        }
        finally
        {
            taskPaneControl.UseWaitCursor = false;
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private string ExecuteLocalBatchPropertyOperation(
        IReadOnlyList<BatchPropertyEditItem> changedItems,
        bool settingPropertyCards,
        Action<string, int, int> reportProgress)
    {
        var operationName = settingPropertyCards ? "正在批量设置本地属性卡" : "正在批量编辑本地属性";
        if (!TryBeginWorkspaceOperation(operationName))
        {
            ShowWorkspaceOperationBusy();
            return "已有工作文件操作正在进行，请稍候再试。";
        }

        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            taskPaneControl.UseWaitCursor = true;
            if (settingPropertyCards)
            {
                ConfigureNativePropertyCardFolders(changedItems);
            }

            var failures = new List<string>();
            var processed = 0;
            foreach (var item in changedItems)
            {
                taskPaneControl.SetWorkspaceOperationProgress(
                    string.Concat(settingPropertyCards ? "设置属性卡" : "写入属性", System.Environment.NewLine, item.FileName),
                    processed,
                    changedItems.Count);
                reportProgress?.Invoke(
                    string.Concat(settingPropertyCards ? "设置属性卡" : "写入属性", System.Environment.NewLine, item.FileName),
                    processed,
                    changedItems.Count);
                try
                {
                    if (settingPropertyCards)
                    {
                        ApplyNativePropertyCardAssignment(item);
                    }
                    else
                    {
                        ApplyBatchPropertyEdit(item);
                    }
                    item.AcceptChanges();
                }
                catch (Exception exception)
                {
                    failures.Add(string.Concat(item.FileName, "：", exception.Message));
                    LogDiagnostic(string.Concat("ExecuteLocalBatchPropertyOperation.", item.FileName), exception);
                }
                finally
                {
                    processed++;
                    taskPaneControl.SetWorkspaceOperationProgress(
                        string.Concat("已处理", System.Environment.NewLine, item.FileName),
                        processed,
                        changedItems.Count);
                    reportProgress?.Invoke(string.Concat("已处理", System.Environment.NewLine, item.FileName), processed, changedItems.Count);
                }
            }

            taskPaneControl.SetWorkspaceOperationProgress("本地属性操作完成", changedItems.Count, changedItems.Count);
            taskPaneControl.SetTree(currentTree);
            var succeeded = changedItems.Count - failures.Count;
            var message = string.Concat(
                settingPropertyCards ? "本地属性卡设置完成。" : "本地批量属性编辑完成。",
                "\r\n已处理：", changedItems.Count, "个",
                "\r\n成功：", succeeded, "个",
                "\r\n失败：", failures.Count, "个",
                "\r\n未执行PLM获取权限、项目登记或版本存档。");
            if (failures.Count > 0)
            {
                message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", failures.Take(8)));
            }
            return message;
        }
        finally
        {
            taskPaneControl.UseWaitCursor = false;
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private async Task ExecuteCadPropertyWritebackAsync(
        Guid projectId,
        IReadOnlyList<CadPropertyWritebackDto> available,
        IReadOnlyDictionary<Guid, CadTreeNode> nodesByDocument,
        string changeNote)
    {
        if (available == null || available.Count == 0)
        {
            throw new InvalidOperationException("当前结构没有可执行的待回写属性。");
        }
        if (string.IsNullOrWhiteSpace(changeNote))
        {
            throw new InvalidOperationException("请填写属性回写的存档说明。");
        }
        if (!TryBeginWorkspaceOperation("正在回写属性并提交存档"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }
        if (Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
        {
            EndWorkspaceOperation();
            ShowError("已有提交存档任务正在进行，请稍候再试。");
            return;
        }

        Interlocked.Increment(ref refreshSuppressionDepth);
        var started = new List<CadPropertyWritebackDto>();
        try
        {
            taskPaneControl.UseWaitCursor = true;
            foreach (var request in available)
            {
                try
                {
                    started.Add(await apiClient.StartCadPropertyWritebackAsync(request.Id, lifetime.Token));
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("StartCadPropertyWriteback.", request.Id), exception);
                }
            }
            if (started.Count == 0)
            {
                throw new InvalidOperationException("所有待写回任务均发生版本冲突，请返回客户端刷新BOM后重试。");
            }

            var operationItems = started.Select(item => new BatchOperationItem(nodesByDocument[item.SourceDocumentId], 0))
                .GroupBy(item => item.Node.DocumentId.Value)
                .Select(group => group.First())
                .ToArray();
            var drawingReviewWritebackIds = started
                .GroupBy(item => item.SourceDocumentId)
                .ToDictionary(group => group.Key, group => group.First().Id);
            await AcquireLatestAndCheckoutAsync(operationItems, projectId, null, drawingReviewWritebackIds);
            foreach (var group in started.GroupBy(item => item.SourceDocumentId))
            {
                var node = nodesByDocument[group.Key];
                foreach (var request in group)
                {
                    ApplyCadPropertyWriteback(node, request);
                }
            }

            var result = await CheckInBatchAsync(
                operationItems,
                projectId,
                changeNote,
                (completed, total, fileName, status) => taskPaneControl.SetWorkspaceOperationProgress(
                    string.Concat(status, string.IsNullOrWhiteSpace(fileName) ? string.Empty : string.Concat(" ", fileName)),
                    completed,
                    total),
                lifetime.Token,
                null,
                drawingReviewWritebackIds);
            var completed = 0;
            var failed = 0;
            foreach (var request in started)
            {
                var latest = (await apiClient.GetVersionsAsync(request.SourceDocumentId, lifetime.Token)).FirstOrDefault();
                if (latest != null && latest.Id != request.ExpectedVersionId)
                {
                    await apiClient.CompleteCadPropertyWritebackAsync(request.Id, latest.Id, lifetime.Token);
                    completed++;
                }
                else
                {
                    await apiClient.FailCadPropertyWritebackAsync(
                        request.Id,
                        "SolidWorks文件未生成新版本，请检查存档日志后重试。",
                        false,
                        lifetime.Token);
                    failed++;
                }
            }
            MessageBox.Show(
                taskPaneControl,
                string.Concat("PLM属性回写完成。\r\n成功：", completed, "条\r\n失败：", failed, "条\r\n新版本文件：", result.CreatedVersions, "个"),
                "UPLM",
                MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            foreach (var request in started)
            {
                try
                {
                    await apiClient.FailCadPropertyWritebackAsync(request.Id, exception.Message, false, lifetime.Token);
                }
                catch (Exception reportException)
                {
                    LogDiagnostic(string.Concat("FailCadPropertyWriteback.", request.Id), reportException);
                }
            }
            throw;
        }
        finally
        {
            taskPaneControl.UseWaitCursor = false;
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref checkInOperationInProgress, 0);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private void ApplyCadPropertyWriteback(CadTreeNode node, CadPropertyWritebackDto request)
    {
        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            document = FindLoadedDocument(node.FullPath);
            if (document == null)
            {
                document = OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), request.SourceConfiguration ?? string.Empty);
                openedForBatch = true;
            }
            if (document == null || !PathsEqual(document.GetPathName(), node.FullPath))
                throw new IOException(string.Concat(node.FileName, "未能安全加载，PLM属性未写入。"));
            EnsureDocumentEditable(document, node.FullPath);
            var configuration = request.SourceConfiguration?.Trim() ?? string.Empty;
            var manager = document.Extension.CustomPropertyManager[configuration];
            foreach (var property in request.Properties ?? new Dictionary<string, string>())
                SetCadCustomProperty(manager, property.Key, property.Value);

            var saveErrors = 0;
            var saveWarnings = 0;
            var saved = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
            if (!saved || saveErrors != 0)
                throw new IOException(string.Concat(node.FileName, "保存PLM属性失败，错误码：", saveErrors, "，警告码：", saveWarnings));
            node.WorkState = CadWorkState.PendingCheckIn;
        }
        finally
        {
            if (openedForBatch && document != null) CloseBatchOpenedDocument(document);
        }
    }

    private static void SetCadCustomProperty(CustomPropertyManager manager, string name, string value)
    {
        if (manager == null || string.IsNullOrWhiteSpace(name)) return;
        var normalized = value?.Trim() ?? string.Empty;
        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(existing => string.Equals(existing, name.Trim(), StringComparison.OrdinalIgnoreCase))) manager.Set2(name.Trim(), normalized);
        else manager.Add3(name.Trim(), (int)swCustomInfoType_e.swCustomInfoText, normalized, (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
        var raw = string.Empty;
        var resolved = string.Empty;
        var wasResolved = false;
        var linked = false;
        manager.Get6(name.Trim(), false, out raw, out resolved, out wasResolved, out linked);
        if (!string.Equals(raw ?? string.Empty, normalized, StringComparison.Ordinal))
            throw new InvalidOperationException(string.Concat("SolidWorks属性“", name, "”写入校验失败。"));
    }

    private IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> DiscoverNativePropertyCards()
    {
        var result = new Dictionary<CadDocumentKind, IReadOnlyList<string>>();
        try
        {
            var configuredFolders = application?.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swFileLocationsCustomPropertyFile) ?? string.Empty;
            var folders = configuredFolders
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => folder.Trim())
                .Concat(new[] { @"D:\SW模板" })
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            result[CadDocumentKind.Assembly] = FindNativePropertyCards(folders, "*.asmprp");
            result[CadDocumentKind.Part] = FindNativePropertyCards(folders, "*.prtprp");
            result[CadDocumentKind.Drawing] = FindNativePropertyCards(folders, "*.drwprp");
        }
        catch (Exception exception)
        {
            LogDiagnostic("DiscoverNativePropertyCards", exception);
        }
        return result;
    }

    private static IReadOnlyList<string> FindNativePropertyCards(IEnumerable<string> folders, string searchPattern)
    {
        return (folders ?? Array.Empty<string>())
            .SelectMany(folder => Directory.EnumerateFiles(folder, searchPattern, SearchOption.TopDirectoryOnly))
            .Select(Path.GetFullPath)
            .Where(filePath => !string.IsNullOrWhiteSpace(filePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(filePath => Path.GetFileName(filePath), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void ConfigureNativePropertyCardFolders(IEnumerable<BatchPropertyEditItem> selectedItems)
    {
        var folders = (selectedItems ?? Array.Empty<BatchPropertyEditItem>())
            .Select(item => item?.PropertyCardPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetDirectoryName)
            .Where(folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (folders.Length == 0)
        {
            throw new InvalidOperationException("未找到已确认属性卡所在的有效文件夹。");
        }

        var configuredValue = string.Join(";", folders);
        application.SetUserPreferenceStringValue(
            (int)swUserPreferenceStringValue_e.swFileLocationsCustomPropertyFile,
            configuredValue);
        var appliedValue = application.GetUserPreferenceStringValue(
            (int)swUserPreferenceStringValue_e.swFileLocationsCustomPropertyFile) ?? string.Empty;
        var appliedFolders = appliedValue
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => folder.Trim())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(Path.GetFullPath)
            .ToArray();
        var missing = folders.FirstOrDefault(folder => !appliedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(missing))
        {
            throw new InvalidOperationException(string.Concat("SolidWorks未接受属性卡文件位置：", missing));
        }
        LogOperation(string.Concat("Native property card folders configured=", configuredValue));
    }

    private void ApplyNativePropertyCardAssignment(BatchPropertyEditItem item)
    {
        var node = item?.OperationItem?.Node
            ?? throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.PropertyCardPath) || !File.Exists(item.PropertyCardPath))
        {
            throw new FileNotFoundException(string.Concat(node.FileName, "对应的原生属性卡不存在。"), item.PropertyCardPath);
        }

        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            document = FindLoadedDocument(node.FullPath);
            if (document == null)
            {
                document = OpenDocumentInvisiblyForBatch(
                    node.FullPath,
                    ToSolidWorksDocumentType(node.Kind),
                    node.Configuration ?? string.Empty);
                openedForBatch = true;
            }
            if (document == null || !PathsEqual(document.GetPathName(), node.FullPath))
            {
                throw new IOException(string.Concat(node.FileName, "未能安全加载，属性卡未设置。"));
            }

            EnsureDocumentEditable(document, node.FullPath);
            var templateFileName = Path.GetFileName(item.PropertyCardPath);
            var previousTemplateReference = document.Extension.CustomPropertyBuilderTemplate[false] ?? string.Empty;
            var previousTemplatePath = ResolveNativePropertyCardPath(previousTemplateReference, item.PropertyCardPath);
            NativePropertyCardTemplate previousTemplate = null;
            if (!string.IsNullOrWhiteSpace(previousTemplatePath))
            {
                previousTemplate = NativePropertyCardTemplate.Load(previousTemplatePath);
            }
            else if (!string.IsNullOrWhiteSpace(previousTemplateReference))
            {
                LogOperation(string.Concat(
                    "Previous native property card not found; obsolete fields preserved path=",
                    node.FullPath,
                    " template=",
                    previousTemplateReference));
            }
            if (node.Kind == CadDocumentKind.Assembly
                || node.Kind == CadDocumentKind.Part
                || node.Kind == CadDocumentKind.Drawing)
            {
                document.Extension.CustomPropertyBuilderTemplate[false] = templateFileName;
                var appliedTemplate = document.Extension.CustomPropertyBuilderTemplate[false] ?? string.Empty;
                if (!string.Equals(Path.GetFileName(appliedTemplate), templateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Concat(node.FileName, "未能保存所选属性卡：", templateFileName));
                }
            }
            RemoveObsoletePropertyCardFields(
                document,
                node,
                item.ConfigurationName,
                previousTemplate?.Fields ?? Array.Empty<NativePropertyCardField>(),
                item.PropertyCardFields);
            RemoveKnownLegacyPropertyCardFields(
                document,
                node,
                item.ConfigurationName,
                item.PropertyCardFields);
            foreach (var field in item.PropertyCardFields)
            {
                var configurationName = field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing
                    ? item.ConfigurationName
                    : string.Empty;
                EnsureBatchCustomProperty(document.Extension.CustomPropertyManager[configurationName], field.PropertyName);
                RemoveWrongScopePropertyCardField(document, node, item.ConfigurationName, field);
            }

            var saveErrors = 0;
            var saveWarnings = 0;
            var saved = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
            if (!saved || saveErrors != 0)
            {
                throw new IOException(string.Concat(node.FileName, "保存属性卡失败，错误码：", saveErrors, "，警告码：", saveWarnings));
            }
            if (node.Kind == CadDocumentKind.Assembly
                || node.Kind == CadDocumentKind.Part
                || node.Kind == CadDocumentKind.Drawing)
            {
                var savedTemplate = document.Extension.CustomPropertyBuilderTemplate[false] ?? string.Empty;
                if (!string.Equals(Path.GetFileName(savedTemplate), templateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Concat(node.FileName, "保存后未能重新读取所选属性卡：", templateFileName));
                }
            }
            foreach (var field in item.PropertyCardFields)
            {
                var configurationName = field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing
                    ? item.ConfigurationName
                    : string.Empty;
                var names = document.Extension.CustomPropertyManager[configurationName]?.GetNames() as string[]
                    ?? Array.Empty<string>();
                if (!names.Any(name => string.Equals(name, field.PropertyName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(string.Concat(node.FileName, "保存后未能重新读取属性卡字段：", field.PropertyName));
                }
            }
            LogOperation(string.Concat(
                "Native property card assigned path=",
                node.FullPath,
                " template=",
                templateFileName));
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
        }
    }

    private string ResolveNativePropertyCardPath(string templateReference, string selectedTemplatePath)
    {
        if (string.IsNullOrWhiteSpace(templateReference))
        {
            return string.Empty;
        }

        var normalizedReference = templateReference.Trim();
        if (File.Exists(normalizedReference))
        {
            return Path.GetFullPath(normalizedReference);
        }

        var templateFileName = Path.GetFileName(normalizedReference);
        if (string.IsNullOrWhiteSpace(templateFileName))
        {
            return string.Empty;
        }

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedFolder = Path.GetDirectoryName(selectedTemplatePath);
        if (!string.IsNullOrWhiteSpace(selectedFolder) && Directory.Exists(selectedFolder))
        {
            folders.Add(Path.GetFullPath(selectedFolder));
        }

        var configuredFolders = application.GetUserPreferenceStringValue(
            (int)swUserPreferenceStringValue_e.swFileLocationsCustomPropertyFile) ?? string.Empty;
        foreach (var folder in configuredFolders.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var normalizedFolder = folder.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedFolder) && Directory.Exists(normalizedFolder))
            {
                folders.Add(Path.GetFullPath(normalizedFolder));
            }
        }

        return folders
            .Select(folder => Path.Combine(folder, templateFileName))
            .FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static void RemoveObsoletePropertyCardFields(
        IModelDoc2 document,
        CadTreeNode node,
        string configurationName,
        IReadOnlyList<NativePropertyCardField> previousFields,
        IReadOnlyList<NativePropertyCardField> selectedFields)
    {
        if (previousFields == null || previousFields.Count == 0)
        {
            return;
        }

        var selectedKeys = new HashSet<string>(
            (selectedFields ?? Array.Empty<NativePropertyCardField>())
                .Select(field => PropertyCardFieldKey(field, node.Kind)),
            StringComparer.OrdinalIgnoreCase);
        var obsoleteFields = previousFields
            .Where(field => !selectedKeys.Contains(PropertyCardFieldKey(field, node.Kind)))
            .GroupBy(field => PropertyCardFieldKey(field, node.Kind), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (obsoleteFields.Length == 0)
        {
            return;
        }

        var removed = new List<string>();
        foreach (var field in obsoleteFields)
        {
            var configurationSpecific = field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing;
            var manager = document.Extension.CustomPropertyManager[
                configurationSpecific ? configurationName ?? string.Empty : string.Empty];
            var names = manager?.GetNames() as string[] ?? Array.Empty<string>();
            var existingName = names.FirstOrDefault(name =>
                string.Equals(name, field.PropertyName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(existingName))
            {
                continue;
            }

            manager.Delete2(existingName);
            names = manager.GetNames() as string[] ?? Array.Empty<string>();
            if (names.Any(name => string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(string.Concat("旧属性卡字段“", existingName, "”删除失败。"));
            }
            removed.Add(string.Concat(configurationSpecific ? "配置/" : "全局/", existingName));
        }

        if (removed.Count > 0)
        {
            LogOperation(string.Concat(
                "Obsolete native property card fields removed path=",
                node.FullPath,
                " fields=",
                string.Join(",", removed)));
        }
    }

    private static string PropertyCardFieldKey(NativePropertyCardField field, CadDocumentKind kind) =>
        string.Concat(
            field.ConfigurationSpecific && kind != CadDocumentKind.Drawing ? "配置|" : "全局|",
            field.PropertyName?.Trim() ?? string.Empty);

    private static void RemoveKnownLegacyPropertyCardFields(
        IModelDoc2 document,
        CadTreeNode node,
        string configurationName,
        IReadOnlyList<NativePropertyCardField> selectedFields)
    {
        var legacyFieldNames = new HashSet<string>(new[]
        {
            "图号",
            "名称",
            "材料",
            "规格",
            "零件名称",
            "分类",
            "属性",
            "文档名称",
            "物料编码",
            "NT",
            "BOMINFO",
            "TNR",
            "表面处理",
            "重量",
            "标签编号",
            "SUPPLIER",
            "种类",
            "单位",
            "版本"
        }, StringComparer.OrdinalIgnoreCase);
        var selectedKeys = new HashSet<string>(
            (selectedFields ?? Array.Empty<NativePropertyCardField>())
                .Select(field => PropertyCardFieldKey(field, node.Kind)),
            StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        RemoveKnownLegacyPropertyCardFields(
            document.Extension.CustomPropertyManager[string.Empty],
            "全局|",
            "全局/",
            legacyFieldNames,
            selectedKeys,
            removed);
        if (node.Kind != CadDocumentKind.Drawing && !string.IsNullOrWhiteSpace(configurationName))
        {
            RemoveKnownLegacyPropertyCardFields(
                document.Extension.CustomPropertyManager[configurationName],
                "配置|",
                string.Concat("配置:", configurationName, "/"),
                legacyFieldNames,
                selectedKeys,
                removed);
        }

        if (removed.Count > 0)
        {
            LogOperation(string.Concat(
                "Known legacy native property card fields removed path=",
                node.FullPath,
                " fields=",
                string.Join(",", removed)));
        }
    }

    private static void RemoveKnownLegacyPropertyCardFields(
        CustomPropertyManager manager,
        string scopeKey,
        string scopeDisplay,
        ISet<string> legacyFieldNames,
        ISet<string> selectedKeys,
        ICollection<string> removed)
    {
        var names = manager?.GetNames() as string[] ?? Array.Empty<string>();
        foreach (var existingName in names
            .Where(name => legacyFieldNames.Contains(name))
            .Where(name => !selectedKeys.Contains(string.Concat(scopeKey, name)))
            .ToArray())
        {
            manager.Delete2(existingName);
            var remainingNames = manager.GetNames() as string[] ?? Array.Empty<string>();
            if (remainingNames.Any(name => string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(string.Concat("旧属性卡字段“", existingName, "”删除失败。"));
            }
            removed.Add(string.Concat(scopeDisplay, existingName));
        }
    }

    private IReadOnlyList<BatchPropertyEditItem> BuildBatchPropertyEditItems(
        IReadOnlyList<BatchOperationItem> operationItems,
        string projectNumber,
        string projectName,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards)
    {
        var result = new List<BatchPropertyEditItem>();
        foreach (var operationItem in operationItems)
        {
            var node = operationItem.Node;
            var activePropertyCard = ResolveActiveNativePropertyCard(node, nativePropertyCards);
            var localProperties = ReadLoadedBatchPropertyValues(node);
            var configurationName = BatchConfigurationName(node, localProperties);
            ExtractBatchPropertyValues(localProperties, configurationName, out var values, out var scopes, out _);
            ApplyPropertyCardFieldScopes(
                localProperties,
                configurationName,
                activePropertyCard?.Fields,
                node.Kind,
                values,
                scopes,
                null);
            values["图号"] = string.IsNullOrWhiteSpace(values["图号"])
                ? Path.GetFileNameWithoutExtension(node.FileName)
                : values["图号"];
            values["名称"] = string.IsNullOrWhiteSpace(values["名称"])
                ? node.DisplayName ?? Path.GetFileNameWithoutExtension(node.FileName)
                : values["名称"];
            if (string.IsNullOrWhiteSpace(values["零件名称"]))
            {
                values["零件名称"] = values["名称"];
            }
            var resolvedProjectNumber = string.IsNullOrWhiteSpace(projectNumber)
                ? values["项目号"]
                : projectNumber;
            var resolvedProjectName = string.IsNullOrWhiteSpace(projectName)
                ? values["项目名称"]
                : projectName;

            var item = new BatchPropertyEditItem(
                operationItem,
                resolvedProjectNumber,
                resolvedProjectName,
                configurationName,
                values,
                scopes,
                Array.Empty<string>(),
                values["项目号"],
                values["项目名称"]);
            item.SetActivePropertyCard(activePropertyCard);
            result.Add(item);
        }
        return result;
    }

    private NativePropertyCardTemplate ResolveActiveNativePropertyCard(
        CadTreeNode node,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards)
    {
        if (node == null
            || nativePropertyCards == null
            || !nativePropertyCards.TryGetValue(node.Kind, out var candidates)
            || candidates == null
            || candidates.Count == 0)
        {
            return null;
        }

        var templateReference = string.Empty;
        try
        {
            var document = FindLoadedDocument(node.FullPath);
            templateReference = document?.Extension?.CustomPropertyBuilderTemplate[false] ?? string.Empty;
        }
        catch (Exception exception)
        {
            LogDiagnostic(string.Concat("ResolveActiveNativePropertyCard.", node.FileName), exception);
        }

        string selectedPath = null;
        if (!string.IsNullOrWhiteSpace(templateReference))
        {
            var templateFileName = Path.GetFileName(templateReference.Trim());
            selectedPath = candidates.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), templateFileName, StringComparison.OrdinalIgnoreCase));
            if (selectedPath == null && File.Exists(templateReference))
            {
                selectedPath = Path.GetFullPath(templateReference);
            }
        }
        else if (candidates.Count == 1)
        {
            selectedPath = candidates[0];
        }

        return string.IsNullOrWhiteSpace(selectedPath)
            ? null
            : NativePropertyCardTemplate.Load(selectedPath);
    }

    private async Task<int> SynchronizeBatchPropertiesFromPlmAsync(
        IReadOnlyList<BatchPropertyEditItem> items,
        Guid projectId)
    {
        if (!apiClient.IsAuthenticated)
        {
            throw new InvalidOperationException("请先登录PLM，再手动同步属性。");
        }

        var linkedItems = (items ?? Array.Empty<BatchPropertyEditItem>())
            .Where(item => item?.OperationItem?.Node?.DocumentId.HasValue == true)
            .ToArray();
        if (linkedItems.Length == 0)
        {
            throw new InvalidOperationException("当前列表没有已登记到PLM的图档。");
        }

        var projectsTask = apiClient.GetProjectsAsync(lifetime.Token);
        var mappingsTask = apiClient.GetBomPropertyMappingsAsync(lifetime.Token);
        var bomTask = Task.WhenAll(new[] { "Standard", "NonStandard", "Unclassified", "Virtual", "Electrical" }
            .Select(async kind => new
            {
                Kind = kind,
                Items = await apiClient.GetBomAsync(projectId, kind, lifetime.Token)
            }));
        await Task.WhenAll(projectsTask, mappingsTask, bomTask);
        var projects = await projectsTask;
        var project = projects.FirstOrDefault(candidate => candidate.Id == projectId)
            ?? availableProjects.FirstOrDefault(candidate => candidate.Id == projectId);
        if (project == null)
        {
            throw new InvalidOperationException("PLM中未找到当前项目，请刷新项目列表后重试。");
        }
        var mappings = await mappingsTask;
        var bomGroups = await bomTask;
        var bomItems = bomGroups
            .SelectMany(group => group.Items ?? new List<BomItemDto>())
            .Where(item => item?.SourceDocumentId.HasValue == true && !item.IsManuallyExcluded)
            .ToArray();
        var versionsByDocument = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        var documentIds = linkedItems
            .Select(item => item.OperationItem.Node.DocumentId.Value)
            .Distinct()
            .ToArray();
        const int batchSize = 8;
        for (var offset = 0; offset < documentIds.Length; offset += batchSize)
        {
            var batchIds = documentIds.Skip(offset).Take(batchSize).ToArray();
            var batch = await Task.WhenAll(batchIds.Select(async documentId => new
            {
                DocumentId = documentId,
                Versions = (IReadOnlyList<DocumentVersionDto>)await apiClient.GetVersionsAsync(documentId, lifetime.Token)
            }));
            foreach (var result in batch)
            {
                versionsByDocument[result.DocumentId] = result.Versions;
            }
        }

        var synchronizedCount = 0;
        foreach (var item in linkedItems)
        {
            var documentId = item.OperationItem.Node.DocumentId.Value;
            versionsByDocument.TryGetValue(documentId, out var versions);
            var snapshot = versions?.FirstOrDefault()?.PropertySnapshot
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractBatchPropertyValues(snapshot, item.ConfigurationName, out var values, out var scopes, out var propertyNames);
            ApplyPropertyCardFieldScopes(
                snapshot,
                item.ConfigurationName,
                item.EffectivePropertyCardFields,
                item.OperationItem.Node.Kind,
                values,
                scopes,
                propertyNames);

            var bomItem = bomItems
                .Where(candidate => candidate.SourceDocumentId == documentId)
                .OrderByDescending(candidate => string.Equals(
                    candidate.SourceConfiguration?.Trim() ?? string.Empty,
                    item.ConfigurationName?.Trim() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => candidate.Sequence)
                .FirstOrDefault();

            if (bomItem != null)
            {
                foreach (var mappingKey in new[]
                {
                    "kind", "unit", "drawingNumber", "name", "specification", "remark",
                    "brand", "material", "surfaceTreatment", "weight"
                }) RemoveMappedPlmProperty(values, scopes, propertyNames, mappings, mappingKey);
            }
            SetMappedPlmValue(values, scopes, propertyNames, mappings, "projectNumber", "项目号", project?.Code);
            SetMappedPlmValue(values, scopes, propertyNames, mappings, "projectName", "项目名称", project?.Name);
            if (bomItem != null)
            {
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "drawingNumber", "物料编码", bomItem.DrawingNumber);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "name", "物料名称", bomItem.Name);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "kind", "物料分类", PlmClassificationValue(bomItem.Kind));
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "unit", "单位", bomItem.Unit);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "specification", "型号", bomItem.Specification);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "remark", "备注", bomItem.Remark);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "brand", "品牌", bomItem.Brand);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "material", "材质", bomItem.Material);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "surfaceTreatment", "表面处理", bomItem.SurfaceTreatment);
                SetMappedPlmValue(values, scopes, propertyNames, mappings, "weight", "重量", bomItem.Weight);
            }

            var projectNumber = project?.Code ?? string.Empty;
            var projectName = project?.Name ?? string.Empty;
            item.ApplyPlmValues(projectNumber, projectName, values, scopes, propertyNames);
            synchronizedCount++;
        }

        if (synchronizedCount == 0)
        {
            throw new InvalidOperationException("当前已登记图档尚无可同步的PLM属性版本。");
        }

        LogOperation(string.Concat(
            "Batch property PLM data synchronized project=",
            projectId,
            " documents=",
            synchronizedCount));
        return synchronizedCount;
    }

    private static void RemoveMappedPlmProperty(
        IDictionary<string, string> values,
        IDictionary<string, string> scopes,
        IList<string> propertyNames,
        IReadOnlyList<BomPropertyMappingDto> mappings,
        string mappingKey)
    {
        var propertyName = MappedSolidWorksProperty(mappings, mappingKey, string.Empty);
        if (string.IsNullOrWhiteSpace(propertyName)) return;
        propertyName = BatchPropertyEditItem.NormalizePropertyName(propertyName);
        values.Remove(propertyName);
        scopes.Remove(propertyName);
        for (var index = propertyNames.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                BatchPropertyEditItem.NormalizePropertyName(propertyNames[index]),
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            {
                propertyNames.RemoveAt(index);
            }
        }
    }

    private static void SetMappedPlmValue(
        IDictionary<string, string> values,
        IDictionary<string, string> scopes,
        IList<string> propertyNames,
        IReadOnlyList<BomPropertyMappingDto> mappings,
        string mappingKey,
        string fallbackProperty,
        string value)
    {
        var propertyName = MappedSolidWorksProperty(mappings, mappingKey, fallbackProperty);
        if (string.IsNullOrWhiteSpace(propertyName)) return;
        propertyName = BatchPropertyEditItem.NormalizePropertyName(propertyName);
        values[propertyName] = value?.Trim() ?? string.Empty;
        scopes[propertyName] = "PLM";
        if (!propertyNames.Contains(propertyName, StringComparer.OrdinalIgnoreCase))
        {
            propertyNames.Add(propertyName);
        }
    }

    private static string MappedSolidWorksProperty(
        IReadOnlyList<BomPropertyMappingDto> mappings,
        string mappingKey,
        string fallback)
    {
        return (mappings ?? Array.Empty<BomPropertyMappingDto>())
            .FirstOrDefault(mapping => string.Equals(
                mapping?.PdmPropertyKey,
                mappingKey,
                StringComparison.OrdinalIgnoreCase))
            ?.SolidWorksProperty
            ?.Trim()
            ?? fallback;
    }

    private static string PlmClassificationValue(int kind)
    {
        switch (kind)
        {
            case 2: return "标准件";
            case 3: return "非标件";
            case 5: return "虚拟件";
            default: return string.Empty;
        }
    }

    private IReadOnlyDictionary<string, string> ReadLoadedBatchPropertyValues(CadTreeNode node)
    {
        var document = FindLoadedDocument(node.FullPath);
        return document == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadModelProperties(document, node.Configuration);
    }

    private static string BatchConfigurationName(CadTreeNode node, IReadOnlyDictionary<string, string> properties)
    {
        if (!string.IsNullOrWhiteSpace(node?.Configuration))
        {
            return node.Configuration.Trim();
        }

        const string prefix = "配置:";
        var configuredKey = properties?.Keys.FirstOrDefault(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return string.Empty;
        }

        var separator = configuredKey.IndexOf('/');
        return separator > prefix.Length ? configuredKey.Substring(prefix.Length, separator - prefix.Length) : string.Empty;
    }

    private static string BatchPropertyValue(
        IReadOnlyDictionary<string, string> properties,
        string configurationName,
        string name,
        string fallback,
        out string scope)
    {
        foreach (var storageName in BatchPropertyStorageNames(name))
        {
            if (!string.IsNullOrWhiteSpace(configurationName)
                && properties != null
                && properties.TryGetValue(string.Concat("配置:", configurationName, "/", storageName), out var configuredValue))
            {
                scope = string.Concat("配置:", configurationName);
                return configuredValue ?? string.Empty;
            }
            if (properties != null && properties.TryGetValue(string.Concat("全局/", storageName), out var globalValue))
            {
                scope = "全局";
                return globalValue ?? string.Empty;
            }
            if (properties != null && properties.TryGetValue(storageName, out var directValue))
            {
                scope = "全局";
                return directValue ?? string.Empty;
            }
        }

        scope = "全局";
        return fallback ?? string.Empty;
    }

    private static void ExtractBatchPropertyValues(
        IReadOnlyDictionary<string, string> properties,
        string configurationName,
        out Dictionary<string, string> values,
        out Dictionary<string, string> scopes,
        out List<string> propertyNames)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        propertyNames = new List<string>();
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties ?? new Dictionary<string, string>())
        {
            if (!TryParseBatchPropertyKey(
                    property.Key,
                    configurationName,
                    out var propertyName,
                    out var scope,
                    out var priority))
            {
                continue;
            }

            propertyName = BatchPropertyEditItem.NormalizePropertyName(propertyName);
            if (IsTechnicalBatchProperty(propertyName)
                || priorities.TryGetValue(propertyName, out var existingPriority) && existingPriority > priority)
            {
                continue;
            }

            if (!values.ContainsKey(propertyName))
            {
                propertyNames.Add(propertyName);
            }
            values[propertyName] = property.Value ?? string.Empty;
            scopes[propertyName] = scope;
            priorities[propertyName] = priority;
        }

        foreach (var identityName in BatchPropertyEditItem.EditablePropertyNames.Concat(new[] { "项目号", "项目名称" }))
        {
            if (!values.ContainsKey(identityName)) values[identityName] = string.Empty;
            if (!scopes.ContainsKey(identityName)) scopes[identityName] = "全局";
        }
    }

    private static void ApplyPropertyCardFieldScopes(
        IReadOnlyDictionary<string, string> properties,
        string configurationName,
        IReadOnlyList<NativePropertyCardField> fields,
        CadDocumentKind kind,
        IDictionary<string, string> values,
        IDictionary<string, string> scopes,
        IList<string> propertyNames)
    {
        if (fields == null || fields.Count == 0 || values == null || scopes == null)
        {
            return;
        }

        foreach (var field in fields.Where(candidate => !string.IsNullOrWhiteSpace(candidate?.PropertyName)))
        {
            var editorPropertyName = BatchPropertyEditItem.NormalizePropertyName(field.EditorPropertyName);
            if (string.IsNullOrWhiteSpace(editorPropertyName))
            {
                continue;
            }

            var configurationSpecific = field.ConfigurationSpecific && kind != CadDocumentKind.Drawing;
            var scope = configurationSpecific
                ? string.Concat("配置:", configurationName?.Trim() ?? string.Empty)
                : "全局";
            var storageName = field.PropertyName.Trim();
            var storageKey = configurationSpecific
                ? string.Concat(scope, "/", storageName)
                : string.Concat("全局/", storageName);
            var value = string.Empty;
            var found = properties != null && properties.TryGetValue(storageKey, out value);
            if (!found && !configurationSpecific && properties != null)
            {
                found = properties.TryGetValue(storageName, out value);
            }

            values[editorPropertyName] = found ? value ?? string.Empty : string.Empty;
            scopes[editorPropertyName] = scope;
            if (found
                && propertyNames != null
                && !propertyNames.Contains(editorPropertyName, StringComparer.OrdinalIgnoreCase))
            {
                propertyNames.Add(editorPropertyName);
            }
        }
    }

    private static bool TryParseBatchPropertyKey(
        string key,
        string configurationName,
        out string propertyName,
        out string scope,
        out int priority)
    {
        propertyName = string.Empty;
        scope = "全局";
        priority = 0;
        var normalized = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        const string globalPrefix = "全局/";
        const string configurationPrefix = "配置:";
        if (normalized.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            propertyName = normalized.Substring(globalPrefix.Length).Trim();
            priority = 2;
            return !string.IsNullOrWhiteSpace(propertyName);
        }
        if (normalized.StartsWith(configurationPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var separator = normalized.IndexOf('/');
            if (separator <= configurationPrefix.Length) return false;
            var candidateConfiguration = normalized.Substring(configurationPrefix.Length, separator - configurationPrefix.Length);
            if (string.IsNullOrWhiteSpace(configurationName)
                || !string.Equals(candidateConfiguration, configurationName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            propertyName = normalized.Substring(separator + 1).Trim();
            scope = string.Concat("配置:", configurationName);
            priority = 3;
            return !string.IsNullOrWhiteSpace(propertyName);
        }

        propertyName = normalized;
        priority = 1;
        return true;
    }

    private static bool IsTechnicalBatchProperty(string propertyName) =>
        new[] { "FileName", "Extension", "LastWriteTimeUtc", "SourceFileSha256" }
            .Any(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> BatchPropertyStorageNames(string editorPropertyName)
    {
        if (string.Equals(editorPropertyName, "材料", StringComparison.OrdinalIgnoreCase))
            return new[] { "材质", "材料" };
        if (string.Equals(editorPropertyName, "分类", StringComparison.OrdinalIgnoreCase))
            return new[] { "物料分类", "分类" };
        return new[] { editorPropertyName };
    }

    private static string BatchPropertyStorageName(string editorPropertyName) =>
        BatchPropertyStorageNames(editorPropertyName)[0];

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
                document = OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration ?? string.Empty);
                openedForBatch = true;
            }
            if (document == null || !PathsEqual(document.GetPathName(), node.FullPath))
            {
                throw new IOException(string.Concat(node.FileName, "未能安全加载，属性未写入。"));
            }
            EnsureDocumentEditable(document, node.FullPath);
            foreach (var field in item.PropertyCardFields)
            {
                var configurationName = field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing
                    ? item.ConfigurationName
                    : string.Empty;
                var manager = document.Extension.CustomPropertyManager[configurationName];
                EnsureBatchCustomProperty(manager, field.PropertyName);
            }
            foreach (var property in item.ChangedProperties())
            {
                var cardField = item.PropertyCardField(property.Key);
                var scope = item.PropertyScope(property.Key);
                if (cardField != null)
                {
                    RemoveWrongScopePropertyCardField(document, node, item.ConfigurationName, cardField);
                }
                var configurationName = cardField?.ConfigurationSpecific == true && node.Kind != CadDocumentKind.Drawing
                    ? item.ConfigurationName
                    : cardField == null && scope.StartsWith("配置:", StringComparison.OrdinalIgnoreCase)
                        ? item.ConfigurationName
                        : string.Empty;
                var manager = document.Extension.CustomPropertyManager[configurationName];
                SetBatchCustomProperty(
                    manager,
                    cardField?.PropertyName ?? BatchPropertyStorageName(property.Key),
                    property.Value,
                    item.OriginalValue(property.Key));
            }

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
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
        }
    }

    private static void SetBatchCustomProperty(CustomPropertyManager manager, string name, string value, string originalValue)
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

    private static void RemoveWrongScopePropertyCardField(
        IModelDoc2 document,
        CadTreeNode node,
        string configurationName,
        NativePropertyCardField field)
    {
        if (document == null || node == null || field == null || string.IsNullOrWhiteSpace(field.PropertyName))
        {
            return;
        }

        var expectedConfigurationSpecific = field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing;
        var normalizedConfigurationName = configurationName?.Trim() ?? string.Empty;
        if (!expectedConfigurationSpecific && string.IsNullOrWhiteSpace(normalizedConfigurationName))
        {
            return;
        }

        var wrongScopeName = expectedConfigurationSpecific ? string.Empty : normalizedConfigurationName;
        var manager = document.Extension.CustomPropertyManager[wrongScopeName];
        var names = manager?.GetNames() as string[] ?? Array.Empty<string>();
        var existingName = names.FirstOrDefault(name => string.Equals(
            name,
            field.PropertyName,
            StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(existingName))
        {
            return;
        }

        manager.Delete2(existingName);
        names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(name => string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(string.Concat("错误作用域属性“", existingName, "”删除失败。"));
        }

        LogOperation(string.Concat(
            "Wrong-scope native property removed path=",
            node.FullPath,
            " scope=",
            expectedConfigurationSpecific ? "全局" : string.Concat("配置:", normalizedConfigurationName),
            " field=",
            existingName));
    }

    private static void EnsureBatchCustomProperty(CustomPropertyManager manager, string name)
    {
        if (manager == null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalizedName = name.Trim();
        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(existing => string.Equals(existing, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        manager.Add3(
            normalizedName,
            (int)swCustomInfoType_e.swCustomInfoText,
            string.Empty,
            (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
        names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (!names.Any(existing => string.Equals(existing, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(string.Concat("SolidWorks属性“", normalizedName, "”创建失败。"));
        }
    }

    private async void OpenBatchOperationDialog(
        Guid? preferredProjectId,
        BatchOperationKind initialOperation,
        IReadOnlyCollection<string> initiallySelectedPaths = null)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }

        if (availableProjects.Count == 0)
        {
            ShowError("当前账号没有可用项目，无法执行整套操作。");
            return;
        }

        if (currentTree == null || currentTree.IsReadOnlyPreview)
        {
            ShowError("请先打开当前工作装配，只读预览不能执行整套操作。");
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
        var inheritedDrawingProjects = await ResolveInheritedDrawingProjectsAsync(items, initialProjectId);
        using (var dialog = new BatchOperationDialog(
            currentTree,
            items,
            availableProjects,
            initialProjectId,
            authenticatedUsername,
            initialOperation,
            initiallySelectedPaths,
            inheritedDrawingProjects,
            userDisplayNames))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
            {
                return;
            }

            if (dialog.Operation == BatchOperationKind.CheckIn
                && IsSolidWorksTemporaryVirtualComponentPath(currentTree.FullPath))
            {
                LogOperation(string.Concat(
                    "Batch check-in blocked temporary virtual component root path=",
                    currentTree.FullPath));
                ShowError(
                    "当前活动装配体是SolidWorks临时虚拟组件，不能作为项目根执行整体存档。\r\n"
                    + "请切换到实际顶层主装配，刷新设计树后再执行整体提交。");
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

            var operationDescription = dialog.Operation == BatchOperationKind.CheckIn
                ? "正在提交整套装配存档"
                : "正在整体获取最新文件及权限";
            if (!TryBeginWorkspaceOperation(operationDescription))
            {
                ShowWorkspaceOperationBusy();
                return;
            }

            var ownsCheckInOperation = false;
            if (dialog.Operation == BatchOperationKind.CheckIn)
            {
                if (Volatile.Read(ref openOperationInProgress) > 0
                    || Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
                {
                    EndWorkspaceOperation();
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
                        "UPLM",
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
                    Action<string> reportBatchStage = stage =>
                    {
                        progressDialog.SetStage(stage);
                        taskPaneControl.SetWorkspaceOperationProgress(stage, 0, 0);
                    };
                    Action<int, int, string, string> reportBatchFile = (completed, total, fileName, status) =>
                    {
                        progressDialog.ReportFile(completed, total, fileName, status);
                        taskPaneControl.SetWorkspaceOperationProgress(
                            string.Concat(status, string.IsNullOrWhiteSpace(fileName) ? string.Empty : string.Concat(" ", fileName)),
                            completed,
                            total);
                    };
                    previousBatchSynchronizationContext = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(new TaskPaneSynchronizationContext(taskPaneControl));
                    batchSynchronizationContextInstalled = true;
                    reportBatchStage("阶段1/3：正在分析本地变更…");

                    EnsureBatchCheckInSavedState(dialog.SelectedItems);
                    PreparePendingRenamesForCheckIn(dialog.SelectedItems.Select(item => item.Node));

                    LogOperation(string.Concat("Batch check-in plan start selected=", dialog.SelectedItems.Count));
                    var checkInPlan = await BuildBatchCheckInPlanAsync(
                        dialog.SelectedItems,
                        reportBatchStage,
                        batchPreflightTimeout.Token);
                    LogOperation(string.Concat(
                        "Batch check-in plan end included=", checkInPlan.Items.Count,
                        " skipped=", checkInPlan.SkippedFiles));
                    batchPreflightTimeout.Dispose();
                    batchPreflightTimeout = null;

                    reportBatchStage("阶段2/3：正在按待提交文件准备权限…");
                    LogOperation(string.Concat("Batch check-in execution start items=", checkInPlan.Items.Count));
                    var result = await CheckInBatchAsync(
                        checkInPlan.Items,
                        selectedProjectId,
                        dialog.ChangeNote,
                        reportBatchFile,
                        batchCancellation.Token,
                        null,
                        null,
                        registrationDecisions,
                        checkInPlan.Preflights);
                    LogOperation(string.Concat(
                        "Batch check-in execution end created=", result.CreatedVersions,
                        " unchanged=", result.UnchangedFiles,
                        " failures=", result.Failures.Count));
                    var message = string.Concat(
                        "归属项目：", selectedProjectDisplay,
                        "\r\n自动登记/准备权限：", result.PreparedPermissions, "个",
                        "\r\n生成新版本：", result.CreatedVersions, "个",
                        "\r\n无变更并结束编辑：", result.UnchangedFiles, "个",
                        "\r\n未变更且未获取权限：", checkInPlan.SkippedFiles, "个",
                        "\r\n失败：", result.Failures.Count, "个",
                        "\r\nBOM提醒：", result.BomWarnings.Count, "项");
                    if (result.Failures.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.Failures.Take(8)));
                    }
                    if (result.BomWarnings.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.BomWarnings.Distinct().Take(8)));
                    }

                    progressDialog.Complete(message, result.Failures.Count > 0, result.BomWarnings.Count > 0);
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

                EndWorkspaceOperation();
                ScheduleTreeRefresh();
            }
        }
    }

    private async Task<WorkspaceAcquireResult> AcquireLatestAndCheckoutAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null,
        IReadOnlyDictionary<Guid, Guid> drawingReviewWritebackIds = null)
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
                ValidateAcquireNode(node, node.DocumentId.HasValue
                    && drawingReviewWritebackIds?.ContainsKey(node.DocumentId.Value) == true);
                if (!node.DocumentId.HasValue)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(node.CheckedOutBy)
                    && !string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(string.Concat(node.FileName, "正在由", node.CheckedOutBy, "编辑，整套获取已停止。"));
                }

                if (IsCheckedOutByCurrentUser(node))
                {
                    LogOperation(string.Concat(
                        "Acquire preserves current checkout without version replacement path=",
                        node.FullPath));
                    continue;
                }

                var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
                var latest = versions.FirstOrDefault();
                ApplyLatestVersion(node, latest);
                if (latest == null)
                {
                    if (!File.Exists(node.FullPath))
                    {
                        throw new FileNotFoundException(string.Concat(node.FileName, "尚无PLM存档版本，本地文件也不存在。"));
                    }

                    continue;
                }

                var needsDownload = !File.Exists(node.FullPath);
                if (!needsDownload)
                {
                    var localSha256 = ComputeFileHash(node.FullPath);
                    if (VersionMatchesLocalFile(latest, node.FullPath, localSha256))
                    {
                        if (node.IsModifiedInSolidWorks)
                        {
                            LogOperation(string.Concat(
                                "Acquire permits loaded unsaved state because disk matches latest path=",
                                node.FullPath,
                                " revision=",
                                latest.Revision?.Display));
                        }
                        continue;
                    }

                    if (node.IsModifiedInSolidWorks)
                    {
                        throw new InvalidOperationException(string.Concat(node.FileName, "存在未保存修改，并且磁盘文件不是PLM最新版本，不能自动更新。请先另存修改或放弃修改。"));
                    }

                    if (!versions.Any(version => VersionMatchesLocalFile(version, node.FullPath, localSha256)))
                    {
                        throw new InvalidOperationException(string.Concat(
                            node.FileName,
                            "的本地内容无法对应任何PLM历史版本，可能包含未存档修改。为避免覆盖，整套获取已停止。"));
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
                    if (!node.DocumentId.HasValue)
                    {
                        throw new InvalidOperationException(string.Concat(
                            node.FileName,
                            "登记后未取得PLM图档标识，整套获取已停止。请刷新设计树后重试。"));
                    }

                    var wasAlreadyCheckedOut = IsCheckedOutByCurrentUser(node);
                    if (wasAlreadyCheckedOut)
                    {
                        historicalPartEditContexts.Remove(node.DocumentId.Value);
                        SetFileReadOnly(node.FullPath, false);
                        EnsureLoadedDocumentEditable(node.FullPath);
                        if (!node.IsModifiedInSolidWorks
                            && node.WorkState != CadWorkState.ModifiedUnsaved
                            && node.WorkState != CadWorkState.PendingCheckIn)
                        {
                            node.WorkState = CadWorkState.Editable;
                        }
                        checkedOut++;
                        continue;
                    }

                    Guid? drawingReviewWritebackId = null;
                    if (drawingReviewWritebackIds != null
                        && drawingReviewWritebackIds.TryGetValue(node.DocumentId.Value, out var writebackId))
                    {
                        drawingReviewWritebackId = writebackId;
                    }
                    var document = await apiClient.CheckoutAsync(
                        node.DocumentId.Value,
                        checkoutSessionId,
                        checkoutMachineName,
                        lifetime.Token,
                        drawingReviewWritebackId);
                    ApplyCheckoutDocument(node, document);
                    node.WorkState = CadWorkState.Editable;
                    historicalPartEditContexts.Remove(node.DocumentId.Value);
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
        ValidateNewAssemblyReferenceLocations(selectedItems, projectId);
        var projectDocuments = await apiClient.GetDocumentsAsync(projectId, lifetime.Token);
        // Older add-ins stored a copyable ID only. Never silently adopt it at a new path.
        var legacyMatches = DistinctBatchItems(selectedItems)
            .Where(item => !item.Node.DocumentId.HasValue
                && PdmDocumentIdentityStore.TryReadProvenance(item.Node.FullPath, out var legacyId, out _)
                && projectDocuments.Any(document => document.Id == legacyId))
            .ToArray();
        if (legacyMatches.Length > 0)
        {
            var choice = MessageBox.Show(taskPaneControl,
                $"{legacyMatches.Length}个文件带有目标项目的旧图档编号，但没有本机位置关联记录。\r\n"
                + "只有确认继续编辑目标项目的原图档，才应恢复关联；复制件用于新项目时，请取消并选择新项目。\r\n\r\n"
                + string.Join("\r\n", legacyMatches.Take(8).Select(item => item.Node.FileName))
                + "\r\n\r\n确认恢复为目标项目的原图档？这不会修改原图档内容。",
                "确认旧图档关联", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.OK) throw new InvalidOperationException("已取消恢复旧关联，尚未开始提交。可以从PLM重新获取，或选择其他目标项目独立入库。");
            foreach (var item in legacyMatches)
            {
                PdmDocumentIdentityStore.TryReadProvenance(item.Node.FullPath, out var legacyId, out _);
                ApplyRegisteredDocumentToMatchingInstances(item.Node, projectDocuments.Single(document => document.Id == legacyId));
            }
        }
        var projectDocumentIds = new HashSet<Guid>(projectDocuments.Select(document => document.Id));
        var mismatch = selectedItems.FirstOrDefault(item =>
            item?.Node?.DocumentId != null && !projectDocumentIds.Contains(item.Node.DocumentId.Value));
        if (mismatch != null)
        {
            throw new InvalidOperationException(string.Concat(
                mismatch.Node.FileName,
                "是其他项目的受控工作文件，不能直接改归属。请将整套图纸复制或打包到新目录，再作为新项目副本提交；原项目保持不变。"));
        }
    }

    private static void ValidateNewAssemblyReferenceLocations(IReadOnlyList<BatchOperationItem> items, Guid projectId)
    {
        var assemblies = DistinctBatchItems(items)
            .Where(item => item.Node.Kind == CadDocumentKind.Assembly && !item.Node.DocumentId.HasValue).ToArray();
        // Nested assemblies may reference sibling folders inside the same packed root.
        var nestedPaths = new HashSet<string>(assemblies.SelectMany(item => EnumerateCadNodes(item.Node).Skip(1))
            .Where(node => node.Kind == CadDocumentKind.Assembly && !string.IsNullOrWhiteSpace(node.FullPath))
            .Select(node => Path.GetFullPath(node.FullPath)), StringComparer.OrdinalIgnoreCase);
        foreach (var item in assemblies.Where(item => !nestedPaths.Contains(Path.GetFullPath(item.Node.FullPath))))
        {
            var directory = Path.GetDirectoryName(item.Node.FullPath);
            var outside = EnumerateCadNodes(item.Node).Skip(1)
                .Where(node => !string.IsNullOrWhiteSpace(node.FullPath)
                    && !IsPathWithinDirectory(node.FullPath, directory)
                    && PdmDocumentIdentityStore.ReadProjectId(node.FullPath) != projectId)
                .Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            if (outside.Length > 0)
                throw new InvalidOperationException("新装配体仍引用副本目录外的文件。为避免将原文件登记到新项目，请先将全部引用打包到新目录，或从PLM获取目标项目已有图档。\r\n" + string.Join("\r\n", outside));
        }
    }

    private bool ConfirmNewDocumentProject(Guid projectId, int documentCount)
    {
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (project == null) throw new InvalidOperationException("所选项目已不可用，请刷新后重试。");
        var parentProject = project.ParentProjectId.HasValue
            ? availableProjects.FirstOrDefault(item => item.Id == project.ParentProjectId.Value)
            : null;
        using (var dialog = new ProjectAdmissionConfirmationDialog(project, parentProject, documentCount, userDisplayNames))
        {
            return dialog.ShowDialog(taskPaneControl) == DialogResult.OK;
        }
    }

    private bool EnsureProjectArchiveAccess(Guid projectId, string action)
    {
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (project == null)
        {
            ShowError("当前归属项目已不可用，请刷新项目列表后重试。");
            return false;
        }
        if (project.CanSubmitArchive)
        {
            return true;
        }

        var target = project.ParentProjectId.HasValue
            ? string.Concat("子项目“", project.Code, "”")
            : "主项目图档";
        ShowError(string.Concat("当前账号对", target, "无存档权限，不能", action, "。只能浏览本人未负责的项目。"));
        return false;
    }

    private async Task<IReadOnlyDictionary<string, Guid>> ResolveInheritedDrawingProjectsAsync(
        IReadOnlyList<BatchOperationItem> items,
        Guid? projectId)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (!projectId.HasValue || items == null || items.Count == 0)
        {
            return result;
        }

        var candidates = items
            .Select(item => item?.Node)
            .Where(node => node != null
                && !node.DocumentId.HasValue
                && node.Kind == CadDocumentKind.Drawing
                && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length == 0)
        {
            return result;
        }

        foreach (var drawing in candidates)
        {
            ApplyDrawingModelRelation(drawing);
        }

        var relatedDocumentIds = candidates
            .Where(drawing => drawing.RelatedModelDocumentId.HasValue)
            .Select(drawing => drawing.RelatedModelDocumentId.Value)
            .Distinct()
            .ToArray();
        if (relatedDocumentIds.Length == 0)
        {
            return result;
        }

        try
        {
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var projectDocumentIds = new HashSet<Guid>(projectDocuments.Select(document => document.Id));
            foreach (var drawing in candidates.Where(drawing =>
                         drawing.RelatedModelDocumentId.HasValue
                         && projectDocumentIds.Contains(drawing.RelatedModelDocumentId.Value)))
            {
                result[drawing.FullPath] = projectId.Value;
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("ResolveInheritedDrawingProjectsAsync", exception);
        }

        return result;
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
            SourceSha256 = ComputeRegistrationFileHash(node)
        }).ToArray(), lifetime.Token);
        var matches = await apiClient.PreflightDocumentRegistrationAsync(projectId, candidates, lifetime.Token);
        var nodesByKey = nodes.ToDictionary(node => node.NodeId.ToString("N"), StringComparer.OrdinalIgnoreCase);
        var candidatesByKey = candidates.ToDictionary(candidate => candidate.CandidateKey, StringComparer.OrdinalIgnoreCase);
        var blockers = matches
            .Where(match => (RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.SameNameDifferentContent)
            .Select(match => string.Concat(
                nodesByKey.TryGetValue(match.CandidateKey, out var node) ? node.FileName : match.CandidateKey,
                " 与当前目标项目中的同名图档内容不同"))
            .ToArray();
        if (blockers.Length > 0)
        {
            throw new InvalidOperationException(string.Concat(
                "发现同名但内容不同的图档，已阻止入库：\r\n",
                string.Join("\r\n", blockers.Take(12)),
                "\r\n请引用当前目标项目中的现有图档、修改本地图号/文件名，或对已有图档获取权限后按新版本提交。"));
        }

        if (matches.Count != candidates.Length
            || matches.Select(match => match.CandidateKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Length
            || matches.Any(match => !nodesByKey.ContainsKey(match.CandidateKey)
                || !Enum.IsDefined(typeof(RegistrationMatchKind), match.MatchKind)))
            throw new InvalidOperationException("图档预检结果不完整，尚未开始登记，请刷新后重试。");

        var reusedCount = matches.Count(match => (RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.SameNameSameContent);
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (reusedCount > 0)
        {
            var choice = MessageBox.Show(taskPaneControl,
                $"目标项目：{project?.ToString() ?? projectId.ToString()}\r\n"
                + $"独立新建图档：{nodes.Length - reusedCount}个\r\n"
                + $"继续目标项目中同名且内容相同的图档：{reusedCount}个\r\n\r\n"
                + "复制或打包的副本将取得独立图档编号，不修改原项目及其历史版本。\r\n"
                + "仅校验当前目标项目内的同名文件；不会查询或比较其他项目中的图档。\r\n"
                + "当前目标项目内同名且内容相同的文件将继续已有登记，避免重复提交。\r\n\r\n确认按以上规则继续？",
                "确认图档登记方式", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.OK) return null;
        }
        var projectDocuments = reusedCount > 0 ? await apiClient.GetDocumentsAsync(projectId, lifetime.Token) : null;
        var decisions = new Dictionary<string, RegistrationDecision>(StringComparer.OrdinalIgnoreCase);
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
                var existing = projectDocuments.FirstOrDefault(document => document.Id == match.ExistingDocumentId)
                    ?? throw new InvalidOperationException(string.Concat("重复预检命中的图档已发生变化：", match.ExistingFileName));
                ApplyRegisteredDocumentToMatchingInstances(node, existing);
                continue;
            }

            if (matchKind == RegistrationMatchKind.SameContentDifferentName || matchKind == RegistrationMatchKind.SameContentOtherProject)
            {
                decisions[node.FullPath] = new RegistrationDecision(candidate.SourceSha256, true,
                    "用户已确认将复制/打包副本独立登记至目标项目；保留原图档及历史版本。");
            }
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
        return new RegistrationDecision(ComputeRegistrationFileHash(node), false, null);
    }

    private static string ComputeRegistrationFileHash(CadTreeNode node)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            throw new InvalidOperationException(string.Concat(
                "引用文件",
                node?.FileName ?? "未知图档",
                "的本地路径不存在：",
                node?.FullPath ?? "未提供路径",
                "。请在SolidWorks中将引用更新到实际工作文件并保存装配体，然后刷新设计树后重试。"));
        }

        try
        {
            return ComputeFileHash(node.FullPath);
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException(string.Concat(
                "引用文件",
                node.FileName,
                "的本地路径已失效：",
                node.FullPath,
                "。请在SolidWorks中更新引用并保存装配体，然后刷新设计树后重试。"));
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(string.Concat(
                "引用文件",
                node.FileName,
                "在首次存档检查期间已不可用：",
                node.FullPath,
                "。请确认文件位置后刷新设计树并重试。"));
        }
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
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var projectId = explicitProjectPaths.TryGetValue(Path.GetFullPath(fullPath), out var remembered)
            ? remembered : PdmDocumentIdentityStore.ReadProjectId(fullPath);
        return projectId.HasValue && availableProjects.Any(project => project.Id == projectId.Value) ? projectId : null;
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
                "。当前PLM以项目内文件名识别图档，请先处理同名冲突。"));
        }
    }

    private static void ValidateAcquireNode(CadTreeNode node, bool allowDrawingReviewWriteback = false)
    {
        if (node.IsReadOnlyPreview)
        {
            throw new InvalidOperationException("只读预览不能原地获取编辑权限，请先切换到编辑工作区。");
        }
        if (node.DrawingReviewLocked && !allowDrawingReviewWriteback)
        {
            throw new InvalidOperationException("图档正在进行图纸审核，只允许只读打开，不能获取编辑权限。");
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
                    "存在未保存修改。请先保存或放弃修改，再获取PLM最新版本。"));
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

    private void CloseLatestReadOnlyPreviewDocuments(bool discardTransientSaveFlags)
    {
        var loadedDocuments = (application?.GetDocuments() as Array)?.OfType<IModelDoc2>()
            .Where(document => IsLatestReadOnlyPreviewPath(document.GetPathName()))
            .ToArray() ?? Array.Empty<IModelDoc2>();
        var loaded = loadedDocuments
            .Select(document => new
            {
                Path = document.GetPathName(),
                Title = document.GetTitle(),
                DocumentType = document.GetType(),
                IsDirty = document.GetSaveFlag()
            })
            .ToArray();
        var dirtyDocuments = loaded.Where(document => document.IsDirty).ToArray();
        if (dirtyDocuments.Length > 0 && !discardTransientSaveFlags)
        {
            throw new InvalidOperationException("最新只读预览出现未保存修改，已停止切换。请先另存文件或关闭并放弃修改。");
        }
        if (dirtyDocuments.Length > 0)
        {
            LogOperation(string.Concat(
                "ControlledOpen discarded transient Latest save flags because modified Working copy was preserved documents=",
                dirtyDocuments.Length));
        }

        foreach (var document in loaded.OrderByDescending(document => document.DocumentType == (int)swDocumentTypes_e.swDocASSEMBLY))
        {
            if (FindLoadedDocument(document.Path) != null)
            {
                application.CloseDoc(document.Title);
            }
        }

        var remaining = (application?.GetDocuments() as Array)?.OfType<IModelDoc2>()
            .FirstOrDefault(document => IsLatestReadOnlyPreviewPath(document.GetPathName()));
        if (remaining != null)
        {
            throw new IOException(string.Concat(remaining.GetTitle(), "仍被SolidWorks占用，未切换到编辑工作区。"));
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
                "已获取PLM编辑权限，但SolidWorks仍以只读方式打开。请关闭该图档后重新获取权限。"));
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
                PdmDocumentIdentityStore.TryWrite(node.FullPath, node.DocumentId.Value, currentProjectId);
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
        var preflights = new Dictionary<string, BatchCheckInPreflight>(StringComparer.OrdinalIgnoreCase);
        var outdatedFiles = new List<string>();
        var skippedFiles = 0;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var node = item.Node;
            reportStage?.Invoke(string.Concat("阶段1/3：正在分析变更 ", index + 1, " / ", items.Count, "：", node.FileName));
            LogOperation(string.Concat("Batch preflight start path=", node.FullPath));
            ValidateAcquireNode(node);

            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                throw new FileNotFoundException(string.Concat(node.FileName, "的本地工作文件不存在，不能提交存档。"));
            }

            if (!string.IsNullOrWhiteSpace(node.CheckedOutBy)
                && !IsCheckedOutByCurrentUser(node)
                && !CanRecoverCurrentCheckoutSession(node))
            {
                throw new InvalidOperationException(string.Concat(node.FileName, "正在由", node.CheckedOutBy, "编辑，整套提交已停止。"));
            }

            if (!node.DocumentId.HasValue
                || IsCheckedOutByCurrentUser(node)
                || CanRecoverCurrentCheckoutSession(node)
                || node.IsModifiedInSolidWorks
                || node.WorkState == CadWorkState.ModifiedUnsaved)
            {
                checkInItems.Add(item);
                LogOperation(string.Concat("Batch preflight include direct path=", node.FullPath, " state=", node.WorkState));
                continue;
            }

            var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = NodeMatchesLatestFile(node, localSha256);
            DocumentVersionDto latest = null;
            IReadOnlyList<DocumentVersionDto> versions = null;
            if (node.Kind == CadDocumentKind.Assembly
                || !fileMatchesLatest
                || node.WorkState == CadWorkState.PendingCheckIn)
            {
                LogOperation(string.Concat("Batch preflight versions start path=", node.FullPath));
                versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
                latest = versions.FirstOrDefault();
                ApplyLatestVersion(node, latest);
                fileMatchesLatest = latest != null && VersionMatchesLocalFile(latest, node.FullPath, localSha256);
                preflights[node.FullPath] = BatchCheckInPreflight.Create(node.FullPath, versions, localSha256);
                LogOperation(string.Concat("Batch preflight versions end path=", node.FullPath, " count=", versions.Count));
            }

            var matchingHistoricalVersion = !fileMatchesLatest && versions != null
                ? versions.Skip(1).FirstOrDefault(version => VersionMatchesLocalFile(version, node.FullPath, localSha256))
                : null;
            if (matchingHistoricalVersion != null)
            {
                var localRevision = matchingHistoricalVersion.Revision?.Display ?? "历史版本";
                var latestRevision = latest?.Revision?.Display ?? "最新版本";
                outdatedFiles.Add(string.Concat(node.FileName, "（本地", localRevision, "，最新", latestRevision, "）"));
                LogOperation(string.Concat(
                    "Batch preflight block outdated path=", node.FullPath,
                    " local=", localRevision,
                    " latest=", latestRevision));
                continue;
            }

            if (fileMatchesLatest && latest != null)
            {
                ApplyResolvedWorkingVersionToMatchingInstances(node, latest);
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

        if (outdatedFiles.Count > 0)
        {
            throw new InvalidOperationException(string.Concat(
                "检测到", outdatedFiles.Count, "个本地文件仍是PLM历史版本，已停止整体存档，且未获取任何新权限。\r\n",
                string.Join("\r\n", outdatedFiles.Take(8)),
                outdatedFiles.Count > 8 ? "\r\n……" : string.Empty,
                "\r\n请先执行“整体获取最新文件及权限”，确认装配引用更新并保存后，再重新提交存档。"));
        }

        // The plan is built before any child is checked in. A parent assembly that is unchanged
        // at this point can become changed later in the same run when a selected child receives a
        // new revision. Keep every selected ancestor of a planned item in this fixed plan so the
        // existing child-first execution can re-evaluate and submit the parent in the same run.
        var selectedAssembliesByPath = items
            .Where(item => item.Node.Kind == CadDocumentKind.Assembly
                && !string.IsNullOrWhiteSpace(item.Node.FullPath))
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var plannedPaths = new HashSet<string>(
            checkInItems.Select(item => item.Node.FullPath),
            StringComparer.OrdinalIgnoreCase);
        var selectedAncestorAssemblies = checkInItems
            .SelectMany(item => item.Ancestors)
            .Where(ancestor => ancestor.Kind == CadDocumentKind.Assembly
                && !string.IsNullOrWhiteSpace(ancestor.FullPath))
            .Select(ancestor => selectedAssembliesByPath.TryGetValue(ancestor.FullPath, out var selectedAssembly)
                ? selectedAssembly
                : null)
            .Where(item => item != null && !plannedPaths.Contains(item.Node.FullPath))
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        foreach (var ancestorItem in selectedAncestorAssemblies)
        {
            if (!plannedPaths.Add(ancestorItem.Node.FullPath))
            {
                continue;
            }

            checkInItems.Add(ancestorItem);
            skippedFiles = Math.Max(0, skippedFiles - 1);
            LogOperation(string.Concat(
                "Batch preflight include selected ancestor path=", ancestorItem.Node.FullPath,
                " reason=selected descendant scheduled"));
        }

        var missingPreflights = checkInItems
            .Where(item => item.Node.DocumentId.HasValue && !preflights.ContainsKey(item.Node.FullPath))
            .ToArray();
        const int preflightBatchSize = 8;
        for (var offset = 0; offset < missingPreflights.Length; offset += preflightBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = missingPreflights.Skip(offset).Take(preflightBatchSize).ToArray();
            reportStage?.Invoke(string.Concat(
                "阶段1/3：正在批量读取PLM版本 ",
                Math.Min(offset + batch.Length, missingPreflights.Length),
                " / ", missingPreflights.Length));
            var loaded = await Task.WhenAll(batch.Select(async item =>
            {
                var versions = (IReadOnlyList<DocumentVersionDto>)await apiClient.GetVersionsAsync(
                    item.Node.DocumentId.Value,
                    cancellationToken);
                var localSha256 = await Task.Run(() => ComputeFileHash(item.Node.FullPath), cancellationToken);
                return BatchCheckInPreflight.Create(item.Node.FullPath, versions, localSha256);
            }));
            foreach (var preflight in loaded)
            {
                preflights[preflight.FullPath] = preflight;
            }
        }

        return new BatchCheckInPlan(checkInItems, skippedFiles, preflights);
    }
    private void EnsureBatchCheckInSavedState(IReadOnlyList<BatchOperationItem> selectedItems)
    {
        var unsavedDocuments = DistinctBatchItems(selectedItems)
            .Select(item => item.Node)
            .Where(node => node != null)
            .Select(node => new { Node = node, Document = FindLoadedDocument(node.FullPath) })
            .Where(item => item.Document?.GetSaveFlag() == true)
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (unsavedDocuments.Length == 0)
        {
            return;
        }

        var displayedFiles = string.Join("、", unsavedDocuments.Select(item => item.Node.FileName).Take(8));
        var remaining = unsavedDocuments.Length > 8
            ? string.Concat("等共", unsavedDocuments.Length, "个文件")
            : string.Empty;
        var confirmation = MessageBox.Show(
            taskPaneControl,
            string.Concat(
                "检测到以下SolidWorks文件尚未保存：\r\n",
                displayedFiles,
                remaining,
                "\r\n\r\n整体存档必须先保存磁盘文件。是否现在保存这些文件并继续？",
                "\r\n选择“否”将取消本次整体存档，不会提交任何文件。"),
            "整体存档前保存文件",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            throw new InvalidOperationException("整体存档已取消：尚有SolidWorks文件未保存。");
        }

        var failures = new List<string>();
        foreach (var item in unsavedDocuments.OrderBy(candidate => candidate.Node.Kind == CadDocumentKind.Assembly ? 1 : 0))
        {
            var errors = 0;
            var warnings = 0;
            var saved = item.Document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
            LogOperation(string.Concat(
                "Batch preflight save path=", item.Node.FullPath,
                " saved=", saved,
                " errors=", errors,
                " warnings=", warnings,
                " dirty=", item.Document.GetSaveFlag()));
            if (!saved || errors != 0 || item.Document.GetSaveFlag())
            {
                failures.Add(string.Concat(item.Node.FileName, "（错误码", errors, "，警告码", warnings, "）"));
                continue;
            }

            foreach (var matchingNode in EnumerateCadNodes(currentTree).Where(node => PathsEqual(node.FullPath, item.Node.FullPath)))
            {
                matchingNode.IsModifiedInSolidWorks = false;
                if (matchingNode.WorkState == CadWorkState.ModifiedUnsaved)
                {
                    matchingNode.WorkState = CadWorkState.PendingCheckIn;
                }
            }
        }

        taskPaneControl.SetTree(currentTree);
        if (failures.Count > 0)
        {
            throw new IOException(string.Concat(
                "以下文件保存失败，整体存档尚未开始：\r\n",
                string.Join("\r\n", failures.Take(8)),
                "\r\n请在SolidWorks中手动保存或放弃修改后重试。"));
        }
    }

    private async Task<bool> PrepareBatchCheckInPermissionAsync(
        BatchOperationItem item,
        Guid projectId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null)
    {
        var node = item?.Node;
        if (node == null || IsCheckedOutByCurrentUser(node))
        {
            return false;
        }

        var checkedOut = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            checkedOut = true;
            LogOperation(string.Concat("Batch check-in checkout end path=", node.FullPath));

            SetFileReadOnly(node.FullPath, false);
            LogOperation(string.Concat("Batch check-in editable-state start path=", node.FullPath));
            EnsureLoadedDocumentEditable(node.FullPath);
            LogOperation(string.Concat("Batch check-in editable-state end path=", node.FullPath));
            return true;
        }
        catch
        {
            if (checkedOut)
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
        IReadOnlyDictionary<string, BatchDocumentIdentity> identities = null,
        IReadOnlyDictionary<Guid, Guid> drawingReviewWritebackIds = null,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null,
        IReadOnlyDictionary<string, BatchCheckInPreflight> preflights = null)
    {
        var items = DistinctBatchItems(selectedItems);
        ValidateBatchFileNames(items);
        // The dialog can retain an earlier tree while the task pane refreshes asynchronously.
        // Resolve references on the tree being submitted, not just on currentTree.
        await ResolveBatchReferenceVersionsAsync(items, reportProgress, cancellationToken);
        var operationNodes = items.SelectMany(item => EnumerateCadNodes(item.Node)).Distinct().ToArray();
        var result = new BatchCheckInResult();
        var originalDocumentPath = (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty;
        var failedItems = new List<BatchOperationItem>();
        var projectReferenceRoot = await apiClient.GetReferenceTreeOrNullAsync(projectId, cancellationToken);
        var projectRootDocumentId = projectReferenceRoot?.DocumentId;
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

                LogOperation(string.Concat("Batch check-in file start path=", node?.FullPath));
                try
                {
                    if (node.Kind == CadDocumentKind.Assembly)
                    {
                        var failedDescendants = failedItems
                            .Where(failed => failed.Ancestors.Any(ancestor => PathsEqual(ancestor.FullPath, node.FullPath)))
                            .Select(failed => failed.Node?.FileName)
                            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        if (failedDescendants.Length > 0)
                        {
                            throw new InvalidOperationException(
                                $"子图档{string.Join("、", failedDescendants)}提交失败，父装配体未提交，避免生成不完整版本。");
                        }
                    }

                    PdmApiClient.ValidateCheckInReferences(node);
                    reportProgress?.Invoke(index, orderedItems.Length, node?.FileName, "阶段2/3：正在准备该文件权限…");
                    if (await PrepareBatchCheckInPermissionAsync(item, projectId, cancellationToken, registrationDecisions))
                    {
                        result.PreparedPermissions++;
                    }

                    reportProgress?.Invoke(index, orderedItems.Length, node?.FileName, "阶段3/3：正在检查版本和本地变更…");
                    ValidateBatchCheckInNode(node);
                    BatchDocumentIdentity identity = null;
                    identities?.TryGetValue(node.FullPath, out identity);
                    Guid? drawingReviewWritebackId = null;
                    if (node.DocumentId.HasValue && drawingReviewWritebackIds != null
                        && drawingReviewWritebackIds.TryGetValue(node.DocumentId.Value, out var writebackId))
                    {
                        drawingReviewWritebackId = writebackId;
                    }
                    BatchCheckInPreflight preflight = null;
                    preflights?.TryGetValue(node.FullPath, out preflight);
                    var fileResult = await CheckInBatchNodeAsync(
                        node,
                        projectId,
                        changeNote.Trim(),
                        node.Kind == CadDocumentKind.Assembly
                            && node.DocumentId.HasValue
                            && (projectRootDocumentId.HasValue
                                ? node.DocumentId.Value == projectRootDocumentId.Value
                                : item.Depth == 0),
                        cancellationToken,
                        identity,
                        drawingReviewWritebackId,
                        (uploaded, total) => reportProgress?.Invoke(
                            index,
                            orderedItems.Length,
                            node.FileName,
                            string.Concat(
                                "阶段3/3：正在上传 ",
                                total <= 0 ? 0 : Math.Min(100, uploaded * 100 / total),
                                "%…")),
                        stage => reportProgress?.Invoke(index, orderedItems.Length, node.FileName, stage),
                        preflight);
                    UpdateBatchDocumentInstances(node, operationNodes);
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
                    if (!string.IsNullOrWhiteSpace(fileResult.BomWarning))
                    {
                        result.BomWarnings.Add(string.Concat(node.FileName, "：", fileResult.BomWarning));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedItems.Add(item);
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

    private async Task ResolveBatchReferenceVersionsAsync(
        IReadOnlyList<BatchOperationItem> items,
        Action<int, int, string, string> reportProgress,
        CancellationToken cancellationToken)
    {
        var plannedPaths = new HashSet<string>(items.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        var references = items.SelectMany(item => item.Node.Children)
            .SelectMany(EnumerateCadNodes)
            .Where(node => node.Kind != CadDocumentKind.Drawing
                && node.DocumentId.HasValue
                && !plannedPaths.Contains(node.FullPath)
                && !PdmApiClient.HasResolvedReferenceVersion(node))
            .Distinct()
            .ToArray();
        var documentIds = references.Select(node => node.DocumentId.Value).Distinct().ToArray();
        var versionsByDocument = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        const int batchSize = 8;
        for (var offset = 0; offset < documentIds.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(offset, documentIds.Length, string.Empty, "阶段1/3：正在核对引用文件的已存档版本…");
            var batch = await Task.WhenAll(documentIds.Skip(offset).Take(batchSize).Select(async id => new
            {
                Id = id,
                Versions = await apiClient.GetVersionsAsync(id, cancellationToken)
            }));
            foreach (var entry in batch) versionsByDocument[entry.Id] = entry.Versions;
        }

        // Repeated component instances share one file hash and one versions request.
        foreach (var group in references.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = await Task.Run(() => ComputeFileHash(group.Key), cancellationToken);
            foreach (var node in group)
            {
                var matching = versionsByDocument[node.DocumentId.Value]
                    .FirstOrDefault(version => VersionMatchesFile(version, sha256));
                if (matching?.Revision == null)
                {
                    throw new InvalidOperationException($"引用文件{node.FileName}无法按文件内容匹配已存档版本，整体存档尚未开始。请将该文件纳入本次提交，或先更新该文件。");
                }
                node.CurrentRevision = node.IsModifiedInSolidWorks
                    ? string.Concat(matching.Revision.Display, "*")
                    : matching.Revision.Display;
            }
        }
    }

    private static void UpdateBatchDocumentInstances(CadTreeNode source, IEnumerable<CadTreeNode> operationNodes)
    {
        foreach (var node in operationNodes.Where(node => node != source && PathsEqual(node.FullPath, source.FullPath)))
        {
            CopyPdmIdentity(source, node);
            node.IsModifiedInSolidWorks = source.IsModifiedInSolidWorks;
        }
    }

    private static string BuildBomUpdateNotice(CheckInResultDto result, bool includeSuccess)
    {
        if (result == null || !result.VersionCreated)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(result.BomUpdateError))
        {
            return string.Concat("BOM自动更新失败：", result.BomUpdateError, " 请在客户端BOM页执行“重新对账”。");
        }

        if (result.BomUpdate == null)
        {
            return "BOM未返回自动更新结果，请在客户端BOM页检查。";
        }

        var mismatchCount = result.BomUpdate.UnclassifiedCount
            + result.BomUpdate.PendingRemovalCount
            + result.BomUpdate.ManualUnmatchedCount;
        if (mismatchCount > 0)
        {
            return string.Concat(
                "BOM已自动更新，但存在", mismatchCount, "条不匹配：待分类", result.BomUpdate.UnclassifiedCount,
                "条、待移除", result.BomUpdate.PendingRemovalCount,
                "条、人工待确认", result.BomUpdate.ManualUnmatchedCount,
                "条。请在客户端BOM页处理。");
        }

        return includeSuccess ? "BOM已按最新图档自动更新。" : string.Empty;
    }

    private static bool HasBomUpdateWarning(CheckInResultDto result) =>
        result?.VersionCreated == true
        && (!string.IsNullOrWhiteSpace(result.BomUpdateError)
            || result.BomUpdate == null
            || result.BomUpdate.UnclassifiedCount + result.BomUpdate.PendingRemovalCount + result.BomUpdate.ManualUnmatchedCount > 0);

    private void ValidateBatchCheckInNode(CadTreeNode node)
    {
        if (node == null || !node.DocumentId.HasValue)
        {
            throw new InvalidOperationException("图档尚未登记到PLM。");
        }

        if (node.IsReadOnlyPreview || IsReadOnlyPreviewPath(node.FullPath))
        {
            throw new InvalidOperationException("只读预览不能提交存档。");
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
        BatchDocumentIdentity identity = null,
        Guid? drawingReviewWritebackId = null,
        Action<long, long> reportUploadProgress = null,
        Action<string> reportFileStage = null,
        BatchCheckInPreflight preflight = null)
    {
        var uploadCopyPath = string.Empty;
        IModelDoc2 document = null;
        var openedForBatch = false;
        var operationTimer = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DocumentVersionDto> versions;
            if (preflight != null && preflight.MatchesCurrentFile(node.FullPath))
            {
                versions = preflight.Versions;
                LogOperation(string.Concat("Batch check-in reused preflight path=", node.FullPath, " count=", versions.Count));
            }
            else
            {
                LogOperation(string.Concat("Batch check-in versions start path=", node.FullPath));
                versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
                LogOperation(string.Concat("Batch check-in versions end path=", node.FullPath, " count=", versions.Count));
            }
            var latest = versions.FirstOrDefault();
            ApplyLatestVersion(node, latest);
            var referenceMatchesLatest = ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node);
            var referenceChanged = !referenceMatchesLatest;
            document = FindLoadedDocument(node.FullPath);
            var hasUnsavedChanges = document?.GetSaveFlag() == true;
            var localSha256 = preflight != null && preflight.MatchesCurrentFile(node.FullPath)
                ? preflight.LocalSha256
                : await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latest, node.FullPath, localSha256);
            var historicalEditMatchesLatest = HistoricalPartEditMatchesLatest(node, latest, node.FullPath, localSha256);
            LogOperation(string.Concat(
                "Batch check-in change detection path=", node.FullPath,
                " fileMatches=", fileMatchesLatest,
                " referenceMatches=", referenceMatchesLatest,
                " unsaved=", hasUnsavedChanges));
            if (identity == null && !referenceChanged && (fileMatchesLatest || historicalEditMatchesLatest) && !hasUnsavedChanges)
            {
                LogOperation(string.Concat("Batch check-in skipped unchanged path=", node.FullPath));
                await CompleteUnchangedEditAsync(node, node.FullPath, latest, projectId, cancellationToken);
                return new BatchNodeCheckInResult(false, null);
            }

            var documentPath = node.FullPath;
            IReadOnlyDictionary<string, string> modelProperties;
            if (document == null && latest != null)
            {
                reportFileStage?.Invoke("阶段3/3：正在读取PLM属性快照…");
                modelProperties = new Dictionary<string, string>(
                    latest.PropertySnapshot ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                LogOperation(string.Concat(
                    "Batch check-in reused PLM properties path=", node.FullPath,
                    " count=", modelProperties.Count,
                    " elapsedMs=", operationTimer.ElapsedMilliseconds));
            }
            else
            {
                if (document == null)
                {
                    reportFileStage?.Invoke("阶段3/3：首次存档，正在读取SolidWorks属性…");
                    var openTimer = Stopwatch.StartNew();
                    document = OpenDocumentSilentlyForBatch(
                        node.FullPath,
                        ToSolidWorksDocumentType(node.Kind),
                        node.Configuration ?? string.Empty);
                    openedForBatch = true;
                    LogOperation(string.Concat(
                        "Batch check-in SolidWorks open path=", node.FullPath,
                        " elapsedMs=", openTimer.ElapsedMilliseconds));
                }

                documentPath = document?.GetPathName() ?? string.Empty;
                if (document == null || !PathsEqual(documentPath, node.FullPath))
                {
                    throw new IOException("未能安全加载待提交图档。");
                }
                EnsureDocumentEditable(document, documentPath);

                if (!openedForBatch && document.GetSaveFlag())
                {
                    throw new InvalidOperationException(string.Concat(
                        node.FileName,
                        "在SolidWorks中处于未保存状态。为避免未变更图档误升版，请先手动保存确认内容，再提交存档；如无需保留修改，请使用“放弃编辑”。"));
                }

                modelProperties = ReadCheckInProperties(document, node);
            }

            localSha256 = preflight != null && preflight.MatchesCurrentFile(documentPath)
                ? preflight.LocalSha256
                : await Task.Run(() => ComputeFileHash(documentPath), cancellationToken);
            if (identity == null
                && !referenceChanged
                && ((!string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                        && VersionMatchesLocalFile(latest, documentPath, localSha256))
                    || HistoricalPartEditMatchesLatest(node, latest, documentPath, localSha256)))
            {
                await CompleteUnchangedEditAsync(node, documentPath, latest, projectId, cancellationToken);
                return new BatchNodeCheckInResult(false, null);
            }

            reportFileStage?.Invoke("阶段3/3：正在生成上传副本…");
            var copyTimer = Stopwatch.StartNew();
            uploadCopyPath = await Task.Run(
                () => CreateCheckInUploadCopy(documentPath, node.DocumentId.Value),
                cancellationToken);
            LogOperation(string.Concat(
                "Batch check-in copy completed path=", node.FullPath,
                " elapsedMs=", copyTimer.ElapsedMilliseconds));
            reportUploadProgress?.Invoke(0, Math.Max(1, new FileInfo(uploadCopyPath).Length));
            LogOperation(string.Concat("Batch check-in upload start path=", node.FullPath));
            var uploadTimer = Stopwatch.StartNew();
            var storedFile = await apiClient.UploadVersionFileAsync(
                projectId,
                uploadCopyPath,
                node.DocumentId.Value,
                documentPath,
                cancellationToken,
                reportUploadProgress);
            LogOperation(string.Concat(
                "Batch check-in upload end path=", node.FullPath,
                " elapsedMs=", uploadTimer.ElapsedMilliseconds));
            reportFileStage?.Invoke(isProjectRoot
                ? "阶段3/3：正在登记根版本并统一刷新BOM…"
                : "阶段3/3：正在登记PLM版本…");
            var registerTimer = Stopwatch.StartNew();
            var checkIn = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                BuildHistoricalPartChangeNote(node, changeNote),
                storedFile,
                modelProperties,
                checkoutSessionId,
                isProjectRoot,
                referenceChanged || identity != null,
                identity?.DrawingNumber,
                identity?.Name,
                cancellationToken,
                drawingReviewWritebackId);
            LogOperation(string.Concat(
                "Batch check-in register completed path=", node.FullPath,
                " projectRoot=", isProjectRoot,
                " elapsedMs=", registerTimer.ElapsedMilliseconds));
            ApplyCheckedInDocumentToMatchingInstances(node, checkIn.Document, checkIn.Version);
            historicalPartEditContexts.Remove(node.DocumentId.Value);
            if (!openedForBatch && document != null)
            {
                ProtectLoadedDocument(documentPath);
            }
            RememberControlledVersionIdentity(documentPath, node.DocumentId.Value, projectId, checkIn.Version);
            LogOperation(string.Concat(
                "Batch check-in file completed path=", node.FullPath,
                " elapsedMs=", operationTimer.ElapsedMilliseconds));
            return new BatchNodeCheckInResult(checkIn.VersionCreated, BuildBomUpdateNotice(checkIn, false));
        }
        finally
        {
            if (openedForBatch && document != null)
            {
                var closeTimer = Stopwatch.StartNew();
                CloseBatchOpenedDocument(document);
                LogOperation(string.Concat(
                    "Batch check-in SolidWorks close path=", node?.FullPath,
                    " elapsedMs=", closeTimer.ElapsedMilliseconds));
            }
            var cleanupTimer = Stopwatch.StartNew();
            DeleteCheckInUploadCopy(uploadCopyPath);
            LogOperation(string.Concat(
                "Batch check-in cleanup path=", node?.FullPath,
                " elapsedMs=", cleanupTimer.ElapsedMilliseconds,
                " totalMs=", operationTimer.ElapsedMilliseconds));
        }
    }

    private async Task CompleteUnchangedEditAsync(
        CadTreeNode node,
        string activePath,
        DocumentVersionDto latest,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var unchanged = await apiClient.CompleteEditWithoutChangesAsync(
            node.DocumentId.Value,
            checkoutSessionId,
            node.LatestStoredSha256,
            cancellationToken);
        ApplyCheckedInDocumentToMatchingInstances(node, unchanged, latest);
        historicalPartEditContexts.Remove(node.DocumentId.Value);
        ProtectLoadedDocument(activePath);
        RememberControlledVersionIdentity(activePath, node.DocumentId.Value, projectId, latest);
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


        if (IsReadOnlyPreviewContext(node))
        {
            ShowError("只读预览不能提交存档。请先使用“获取权限”切换到编辑工作区。 ");
            return;
        }

        if (currentProjectId.HasValue && !EnsureProjectArchiveAccess(currentProjectId.Value, "提交存档"))
        {
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
            ShowError("设计树存在缺失引用，不能提交存档。 ");
            return;
        }

        if (Volatile.Read(ref openOperationInProgress) > 0)
        {
            ShowError("正在打开图档或已有提交存档任务，请稍候再试。");
            return;
        }

        if (!TryBeginWorkspaceOperation(string.Concat("正在提交存档：", node.FileName)))
        {
            if (!BringActiveBatchProgressToFront())
            {
                ShowWorkspaceOperationBusy();
            }
            return;
        }

        if (Interlocked.Exchange(ref checkInOperationInProgress, 1) != 0)
        {
            EndWorkspaceOperation();
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
            EndWorkspaceOperation();
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


        if (requestedNodes.Any(IsReadOnlyPreviewContext))
        {
            ShowError("只读预览不能更改编辑状态。请先切换到当前工作文件。 ");
            return;
        }

        foreach (var requestedNode in requestedNodes)
        {
            if (!EnsureServerDocument(requestedNode) || !EnsureCurrentCheckoutSession(requestedNode, "放弃编辑"))
            {
                return;
            }
        }

        var failures = new List<string>();
        try
        {
            foreach (var requestedNode in requestedNodes)
            {
                try
                {
                    var document = await apiClient.DiscardCheckoutAsync(requestedNode.DocumentId.Value, checkoutSessionId, lifetime.Token);
                    var matchingInstances = FindMatchingDocumentInstances(requestedNode, document.Id);
                    ApplyCheckoutDocumentToMatchingInstances(matchingInstances, document);
                    foreach (var instance in matchingInstances)
                    {
                        instance.WorkState = CadWorkState.None;
                    }
                    historicalPartEditContexts.Remove(document.Id);
                }
                catch (Exception exception)
                {
                    failures.Add(string.Concat(requestedNode.FileName, "：", exception.Message));
                }
            }
            taskPaneControl.SetTree(currentTree);
            ScheduleTreeRefresh();
            if (failures.Count > 0)
            {
                ShowError(string.Concat("部分图档放弃编辑失败：\r\n", string.Join("\r\n", failures.Take(8))));
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private IReadOnlyList<CadTreeNode> FindMatchingDocumentInstances(CadTreeNode source, Guid documentId)
    {
        if (source == null || currentTree == null)
        {
            return source == null ? Array.Empty<CadTreeNode>() : new[] { source };
        }

        var matches = EnumerateCadNodes(currentTree)
            .Where(candidate => candidate != null
                && ((candidate.DocumentId.HasValue && candidate.DocumentId.Value == documentId)
                    || PathsEqual(candidate.FullPath, source.FullPath)))
            .Concat(new[] { source })
            .Distinct()
            .ToArray();
        return matches;
    }

    private void ApplyResolvedWorkingVersionToMatchingInstances(CadTreeNode source, DocumentVersionDto version)
    {
        var revision = version?.Revision?.Display ?? string.Empty;
        foreach (var node in EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, source.FullPath))
            .Concat(new[] { source }).Distinct())
        {
            ApplyLatestVersion(node, version);
            node.Revision = revision;
            node.CurrentRevision = node.IsModifiedInSolidWorks && !string.IsNullOrWhiteSpace(revision)
                ? string.Concat(revision, "*")
                : revision;
        }
    }

    private void ApplyCheckedInDocumentToMatchingInstances(
        CadTreeNode source,
        DocumentDto document,
        DocumentVersionDto version)
    {
        var revision = version?.Revision?.Display ?? document?.Revision?.Display ?? source.Revision;
        var documentId = document?.Id ?? source.DocumentId.Value;
        foreach (var node in FindMatchingDocumentInstances(source, documentId))
        {
            ApplyCheckoutDocument(node, document);
            ClearCheckoutState(node);
            node.Revision = revision;
            node.CurrentRevision = revision;
            node.WorkState = CadWorkState.None;
            node.IsModifiedInSolidWorks = false;
            ApplyLatestVersion(node, version);
        }
        lock (checkoutDocumentSync)
        {
            activeCheckoutDocumentIds.Remove(documentId);
        }
        checkoutReminderLevels.Remove(documentId);
        RefreshCheckoutReminder();
    }

    private static void ClearCheckoutState(CadTreeNode node)
    {
        node.CheckedOutBy = string.Empty;
        node.CheckedOutAt = null;
        node.CheckoutSessionId = null;
        node.CheckoutMachine = string.Empty;
        node.CheckoutLastHeartbeatAt = null;
        node.CheckoutSessionLost = false;
    }

    private async void PrepareAndCheckInOnSolidWorksThread(CadTreeNode node, Guid projectId)
    {
        var originalDocumentPath = string.Empty;
        var uploadCopyPath = string.Empty;
        var keepSubmittedDocumentActive = false;
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

            if (node.IsReadOnlyPreview || IsReadOnlyPreviewPath(node.FullPath))
            {
                ShowError("只读预览不能提交存档。请先使用“获取权限”切换到编辑工作区。 ");
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

            if (IsReadOnlyPreviewPath(activePath))
            {
                ShowError("只读预览不能提交存档。请先使用“获取权限”切换到编辑工作区。 ");
                return;
            }
            EnsureDocumentEditable(document, activePath);

            var currentVersions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latestVersion = currentVersions.FirstOrDefault();
            ApplyLatestVersion(node, latestVersion);
            var projectReferenceRoot = await apiClient.GetReferenceTreeOrNullAsync(projectId, lifetime.Token);
            var isProjectRoot = node.Kind == CadDocumentKind.Assembly
                && node.DocumentId.HasValue
                && (projectReferenceRoot?.DocumentId.HasValue == true
                    ? node.DocumentId.Value == projectReferenceRoot.DocumentId.Value
                    : currentTree != null && PathsEqual(node.FullPath, currentTree.FullPath));
            var referenceMatchesLatest = ReferenceSnapshotMatchesTree(latestVersion?.ReferenceSnapshot, node);
            var referenceChanged = !referenceMatchesLatest;
            var hasUnsavedChanges = document.GetSaveFlag();
            var currentSha256 = ComputeFileHash(activePath);
            var fileMatchesLatest = !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latestVersion, activePath, currentSha256);
            var historicalEditMatchesLatest = HistoricalPartEditMatchesLatest(node, latestVersion, activePath, currentSha256);
            LogOperation(string.Concat(
                "CheckIn change detection path=", activePath,
                " fileMatches=", fileMatchesLatest,
                " referenceMatches=", referenceMatchesLatest,
                " unsaved=", hasUnsavedChanges));

            if (hasUnsavedChanges)
            {
                keepSubmittedDocumentActive = true;
                LogOperation(string.Concat(
                    "CheckIn kept unsaved target active path=",
                    activePath));
                ShowError(string.Concat(
                    node.FileName,
                    "在SolidWorks中处于未保存状态。已保持该文件为当前文档，请按Ctrl+S保存确认内容，再提交存档；如无需保留修改，请使用“放弃编辑”。"));
                return;
            }

            if (!referenceChanged && (fileMatchesLatest || historicalEditMatchesLatest))
            {
                var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, checkoutSessionId, node.LatestStoredSha256, lifetime.Token);
                ApplyCheckedInDocumentToMatchingInstances(node, unchanged, latestVersion);
                historicalPartEditContexts.Remove(node.DocumentId.Value);
                ProtectLoadedDocument(activePath);
                RememberControlledVersionIdentity(activePath, node.DocumentId.Value, projectId, latestVersion);
                taskPaneControl.SetTree(currentTree);
                MessageBox.Show(taskPaneControl, string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"), "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            var modelProperties = ReadCheckInProperties(document, node);
            uploadCopyPath = CreateCheckInUploadCopy(document, activePath, node.DocumentId.Value);
            var storedFile = await apiClient.UploadVersionFileAsync(projectId, uploadCopyPath, node.DocumentId.Value, activePath, lifetime.Token);
            var result = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                BuildHistoricalPartChangeNote(node, changeNote),
                storedFile,
                modelProperties,
                checkoutSessionId,
                isProjectRoot,
                referenceChanged,
                null,
                null,
                lifetime.Token);
            ApplyCheckedInDocumentToMatchingInstances(node, result.Document, result.Version);
            historicalPartEditContexts.Remove(node.DocumentId.Value);
            ProtectLoadedDocument(activePath);
            RememberControlledVersionIdentity(activePath, node.DocumentId.Value, projectId, result.Version);
            taskPaneControl.SetTree(currentTree);
            var bomNotice = BuildBomUpdateNotice(result, true);
            MessageBox.Show(
                taskPaneControl,
                string.Concat(result.VersionCreated
                    ? string.Concat("提交存档成功，工作版本：", node.Revision)
                    : string.Concat("未检测到变更，已结束编辑，版本仍为", node.Revision, "。"),
                    string.IsNullOrWhiteSpace(bomNotice) ? string.Empty : string.Concat("\r\n\r\n", bomNotice)),
                "UPLM",
                MessageBoxButtons.OK,
                HasBomUpdateWarning(result) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
                if (!keepSubmittedDocumentActive)
                {
                    RestoreOriginalDocumentAfterCheckIn(originalDocumentPath, node?.FullPath ?? string.Empty);
                }
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
                EndWorkspaceOperation();
                ScheduleTreeRefresh();
            }
        }
    }

    private static void RememberControlledVersionIdentity(
        string path,
        Guid documentId,
        Guid projectId,
        DocumentVersionDto version)
    {
        if (version == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        PdmDocumentIdentityStore.TryWriteControlledVersion(
            path,
            documentId,
            projectId,
            version.Id,
            version.Revision?.Display,
            version.Sha256,
            new FileInfo(path).Length);
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

    private IModelDoc2 OpenDocumentInvisiblyForBatch(
        string fullPath,
        int documentType,
        string configuration)
    {
        var previousVisibility = application.GetDocumentVisible(documentType);
        try
        {
            application.DocumentVisible(false, documentType);
            LogOperation(string.Concat("Batch invisible open start path=", fullPath));
            return OpenDocumentSilentlyForBatch(fullPath, documentType, configuration);
        }
        finally
        {
            application.DocumentVisible(previousVisibility, documentType);
            LogOperation(string.Concat(
                "Batch document visibility restored type=",
                documentType,
                " visible=",
                previousVisibility));
        }
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

    private static bool IsSolidWorksTemporaryVirtualComponentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relativeSegments = fullPath.Substring(tempRoot.Length)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return relativeSegments.Length >= 3
                && relativeSegments[0].StartsWith("swx", StringComparison.OrdinalIgnoreCase)
                && relativeSegments.Any(segment => string.Equals(segment, "VC~~", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
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

    private bool IsLatestReadOnlyPreviewContext(CadTreeNode node)
    {
        if (node?.IsLatestReadOnlyPreview == true || IsLatestReadOnlyPreviewPath(node?.FullPath))
        {
            return true;
        }

        var activePath = (application?.ActiveDoc as IModelDoc2)?.GetPathName();
        return IsLatestReadOnlyPreviewPath(activePath);
    }

    private bool IsReadOnlyPreviewContext(CadTreeNode node) =>
        IsHistoricalPreviewContext(node) || IsLatestReadOnlyPreviewContext(node);

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

    private static bool IsLatestReadOnlyPreviewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var workspaceRoot = Path.GetFullPath(WorkspaceSettingsStore.GetWorkspaceRoot())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                && fullPath.IndexOf(string.Concat(Path.DirectorySeparatorChar, "Latest", Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReadOnlyPreviewPath(string path) =>
        IsHistoricalPreviewPath(path) || IsLatestReadOnlyPreviewPath(path);

    private static void MarkReadOnlyPreview(CadTreeNode node, bool inheritedHistoricalPreview, bool inheritedLatestPreview)
    {
        if (node == null)
        {
            return;
        }

        node.IsHistoricalPreview = inheritedHistoricalPreview || IsHistoricalPreviewPath(node.FullPath);
        node.IsLatestReadOnlyPreview = !node.IsHistoricalPreview
            && (inheritedLatestPreview || IsLatestReadOnlyPreviewPath(node.FullPath));
        foreach (var child in node.Children)
        {
            MarkReadOnlyPreview(child, node.IsHistoricalPreview, node.IsLatestReadOnlyPreview);
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

    private IReadOnlyDictionary<string, string> ReadCheckInProperties(IModelDoc2 document, CadTreeNode node)
    {
        var properties = ReadModelProperties(document, node.Configuration)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var card = ResolveActiveNativePropertyCard(node, DiscoverNativePropertyCards());
        if (card?.IsAvailable == true)
        {
            foreach (var field in card.Fields)
                Upton.Pdm.Domain.CadPropertyCardSnapshot.AddField(properties, field.PropertyName,
                    field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing);
        }
        return properties;
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
            ShowError("请先登录PLM。");
            return;
        }

        var node = taskPaneControl.SelectedNode;
        if (node?.DocumentId != eventArgs.DocumentId)
        {
            node = EnumerateCadNodes(currentTree).FirstOrDefault(candidate => candidate.DocumentId == eventArgs.DocumentId);
        }
        if (node == null || !node.DocumentId.HasValue)
        {
            ShowError("当前设计树中未找到该图档，请刷新后重试。");
            return;
        }
        if (node.IsReadOnlyPreview || IsReadOnlyPreviewContext(node))
        {
            ShowError("只读预览目录不能切换本地工作版本。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }
        if (!TryBeginWorkspaceOperation("正在切换图档版本"))
        {
            ShowWorkspaceOperationBusy();
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
                ?? throw new InvalidOperationException("当前项目中未找到该图档，请刷新设计树后重试。");
            ApplyCheckoutDocument(node, serverDocument);
            ValidateUpdateLatestNode(node);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该图档尚无可选择的PLM版本。");
            var selected = versions.FirstOrDefault(version => version.Id == eventArgs.Version.Id)
                ?? throw new InvalidOperationException("所选版本已不存在，请刷新版本列表后重试。");
            ApplyLatestVersion(node, latest);

            DocumentVersionDto current = null;
            var hasUnmatchedLocalContent = false;
            localFileExisted = File.Exists(node.FullPath);
            if (localFileExisted)
            {
                initialLocalSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), lifetime.Token);
                current = versions.FirstOrDefault(version => VersionMatchesLocalFile(version, node.FullPath, initialLocalSha256));
                if (current == null)
                {
                    hasUnmatchedLocalContent = true;
                }

                if (current?.Id == selected.Id)
                {
                    ProtectLoadedDocument(node.FullPath);
                    ApplySelectedWorkingVersion(node, selected, latest);
                    taskPaneControl.SetTree(currentTree);
                    MessageBox.Show(
                        taskPaneControl,
                        string.Concat("当前工作文件已经是", selected.Revision?.Display ?? "-", "，设计树已更新版本状态。"),
                        "UPLM",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            var selectedRevision = selected.Revision?.Display ?? "-";
            var latestRevision = latest.Revision?.Display ?? "-";
            var currentRevision = current?.Revision?.Display
                ?? (hasUnmatchedLocalContent ? "本地待提交内容" : "文件缺失");
            var confirmation = hasUnmatchedLocalContent
                ? string.Concat(
                    node.FileName, "的本地内容无法对应任何PLM历史版本，可能存在待提交修改。\r\n",
                    "继续将用", selectedRevision, "覆盖本地工作文件；原文件会自动备份到工作区_backup目录。\r\n",
                    "切换后设计树显示：", selectedRevision, " / ", latestRevision, "，所选版本保持只读。是否继续？")
                : string.Concat(
                    "将", node.FileName, "的本地工作版本从", currentRevision, "切换到", selectedRevision, "。\r\n",
                    "切换后设计树显示：", selectedRevision, " / ", latestRevision, "。\r\n",
                    "原工作文件将自动备份，所选版本保持只读。是否继续？");
            if (MessageBox.Show(
                    taskPaneControl,
                    confirmation,
                    "选择工作版本",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
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
                string.Concat("工作版本已切换为", selectedRevision, "。\r\n设计树版本：", selectedRevision, " / ", latestRevision, "。"),
                "UPLM",
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
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private async void OnEditHistoricalVersionRequested(object sender, DocumentVersionEventArgs eventArgs)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }

        var node = taskPaneControl.SelectedNode;
        if (node?.DocumentId != eventArgs.DocumentId)
        {
            node = EnumerateCadNodes(currentTree).FirstOrDefault(candidate => candidate.DocumentId == eventArgs.DocumentId);
        }
        if (node == null || !node.DocumentId.HasValue)
        {
            ShowError("当前设计树中未找到该零件，请刷新后重试。");
            return;
        }
        if (node.Kind != CadDocumentKind.Part)
        {
            ShowError("当前仅支持零件基于历史版本获取编辑。");
            return;
        }
        if (node.IsReadOnlyPreview || IsReadOnlyPreviewContext(node))
        {
            ShowError("只读预览目录不能基于历史版本编辑，请先切换到当前工作区。");
            return;
        }
        if (node.DrawingReviewLocked
            || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(node.DrawingReviewLocked ? "图纸审核中的零件不能获取编辑权限。" : "审批中或已作废的零件不能获取编辑权限。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!projectId.HasValue)
        {
            ShowError("未识别图档所属项目，请先选择当前项目。");
            return;
        }
        if (!TryBeginWorkspaceOperation("正在基于历史版本获取编辑权限"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }

        var stagedPath = string.Empty;
        var reopenPath = string.Empty;
        var reopenKind = CadDocumentKind.Other;
        var reopenConfiguration = string.Empty;
        string initialLocalSha256 = null;
        var localFileExisted = false;
        var checkoutAcquired = false;
        var editPrepared = false;
        var successMessage = string.Empty;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            RefreshLoadedDocumentModificationFlags(currentTree);
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var serverDocument = projectDocuments.FirstOrDefault(document => document.Id == node.DocumentId.Value)
                ?? throw new InvalidOperationException("当前项目中未找到该零件，请刷新设计树后重试。");
            ApplyCheckoutDocument(node, serverDocument);
            ValidateUpdateLatestNode(node);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该零件尚无可选择的PLM版本。");
            var selected = versions.FirstOrDefault(version => version.Id == eventArgs.Version.Id)
                ?? throw new InvalidOperationException("所选版本已不存在，请刷新版本列表后重试。");
            if (selected.Id == latest.Id)
            {
                throw new InvalidOperationException("所选版本已经是最新版本，请直接使用“获取权限”。");
            }
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
                        "的本地内容无法对应任何PLM历史版本，可能存在待提交修改。为避免覆盖，已停止获取编辑。"));
                }
            }

            var selectedRevision = selected.Revision?.Display ?? "-";
            var latestRevision = latest.Revision?.Display ?? "-";
            var currentRevision = current?.Revision?.Display ?? "文件缺失";
            var confirmation = string.Concat(
                "将零件", node.FileName, "的本地工作版本从", currentRevision, "切换到", selectedRevision, "并获取独占编辑权限。\r\n",
                "服务器最新版本为", latestRevision, "；提交时将从服务器最新版本继续升版（例如最新W4则生成W5），不会覆盖历史记录。\r\n",
                "若提交时内容与服务器最新版本完全一致，则只结束编辑、不生成新版本。\r\n",
                "原工作文件将自动备份。是否继续？");
            if (MessageBox.Show(taskPaneControl, confirmation, "基于历史版本获取编辑", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            var needsReplacement = current == null || current.Id != selected.Id;
            if (needsReplacement)
            {
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
                    throw new IOException("本地工作文件在版本准备期间发生变化，已停止获取编辑。");
                }
            }

            var checkout = await apiClient.CheckoutAsync(node.DocumentId.Value, checkoutSessionId, checkoutMachineName, lifetime.Token);
            checkoutAcquired = true;

            if (needsReplacement)
            {
                var rootPath = currentTree?.FullPath ?? string.Empty;
                CloseDocumentsForWorkspaceUpdate(new[] { node.FullPath }, rootPath);
                ApplyWorkspaceUpdates(new[] { new WorkspaceUpdatePlan(node, selected, stagedPath) });
            }

            ApplySelectedWorkingVersion(node, selected, latest);
            var matchingNodes = EnumerateCadNodes(currentTree)
                .Where(candidate => PathsEqual(candidate.FullPath, node.FullPath))
                .ToArray();
            ApplyCheckoutDocumentToMatchingInstances(matchingNodes, checkout);
            foreach (var matchingNode in matchingNodes)
            {
                matchingNode.WorkState = CadWorkState.Editable;
            }
            SetFileReadOnly(node.FullPath, false);
            EnsureLoadedDocumentEditable(node.FullPath);
            historicalPartEditContexts[node.DocumentId.Value] = new HistoricalPartEditContext(selected, latest);
            editPrepared = true;
            taskPaneControl.SetTree(currentTree);
            LogOperation(string.Concat(
                "Historical part edit acquired document=", node.DocumentId.Value,
                " source=", selectedRevision,
                " latest=", latestRevision,
                " path=", node.FullPath));
            successMessage = string.Concat(
                "已基于历史版本", selectedRevision, "获取编辑权限。\r\n",
                "设计树版本：", selectedRevision, " / ", latestRevision, "；提交后按最新版本继续升版。");
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnEditHistoricalVersionRequested", exception);
            if (checkoutAcquired && !editPrepared)
            {
                try
                {
                    var discarded = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
                    ApplyCheckoutDocumentToMatchingInstances(
                        EnumerateCadNodes(currentTree).Where(candidate => candidate.DocumentId == node.DocumentId),
                        discarded);
                    ProtectLoadedDocument(node.FullPath);
                }
                catch (Exception rollbackException)
                {
                    LogDiagnostic("Historical part edit checkout rollback", rollbackException);
                }
            }
            historicalPartEditContexts.Remove(node.DocumentId.Value);
            ShowError(exception.Message);
        }
        finally
        {
            DeleteWorkspaceStage(stagedPath);
            if (!string.IsNullOrWhiteSpace(reopenPath) && File.Exists(reopenPath))
            {
                OpenOrActivateDocumentOnSolidWorksThread(reopenPath, ToSolidWorksDocumentType(reopenKind), reopenConfiguration);
            }
            if (editPrepared)
            {
                try
                {
                    SetFileReadOnly(node.FullPath, false);
                    EnsureLoadedDocumentEditable(node.FullPath);
                }
                catch (Exception exception)
                {
                    LogDiagnostic("Historical part edit editable state", exception);
                    successMessage = string.Empty;
                    ShowError(string.Concat("已获取编辑权限，但SolidWorks未能切换为可编辑状态：", exception.Message));
                }
            }
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                MessageBox.Show(taskPaneControl, successMessage, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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
            node.IsLatestReadOnlyPreview = false;
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
            MarkReadOnlyPreview(currentTree, false, false);
            var controlledManifest = ApplyControlledOpenMetadata(currentTree);
            var activeDrawingSource = ResolveActiveDrawingSource(currentTree, previousTree);
            if (currentTree?.Kind == CadDocumentKind.Drawing
                && !currentTree.RelatedModelDocumentId.HasValue
                && activeDrawingSource?.DocumentId.HasValue == true)
            {
                currentTree.RelatedModelDocumentId = activeDrawingSource.DocumentId;
            }
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
            var resolvedProjectId = GetExplicitProjectId(tree.FullPath);

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
            ApplyMetadata(targetTree, byId);
            RefreshLoadedDocumentModificationFlags(targetTree);
            var versionsByDocument = await ApplyWorkingStatesAsync(targetTree);
            RefreshLoadedDocumentModificationFlags(targetTree);
            await ResolveRecoverableCheckoutSessionsAsync(targetTree, versionsByDocument);
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
            DrawingReviewLocked = document?.DrawingReviewLocked == true,
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
            if (!documents.TryGetValue(relation.DrawingDocumentId, out var drawing)
                || (CadDocumentKind)drawing.Kind != CadDocumentKind.Drawing)
            {
                continue;
            }

            var revision = drawing.Revision?.Display ?? string.Empty;
            if (existingDrawingIds.Contains(relation.DrawingDocumentId))
            {
                foreach (var existingDrawing in node.Children.Where(child =>
                             child.Kind == CadDocumentKind.Drawing
                             && child.DocumentId == relation.DrawingDocumentId))
                {
                    existingDrawing.Revision = revision;
                    existingDrawing.CurrentRevision = revision;
                    existingDrawing.LatestRevision = revision;
                }
                continue;
            }

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
                DrawingReviewLocked = drawing.DrawingReviewLocked,
                UpdatedAt = drawing.UpdatedAt
            });
            existingDrawingIds.Add(drawing.Id);
        }
    }

    private bool CanRecoverUnregisteredReferences(Guid rootDocumentId)
    {
        return currentTree != null
            && !currentTree.IsReadOnlyPreview
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
        node.DrawingReviewLocked = document.DrawingReviewLocked;
        node.UpdatedAt = document.UpdatedAt;
        var checkedOutByCurrentUser = !string.IsNullOrWhiteSpace(authenticatedUsername)
            && !string.IsNullOrWhiteSpace(document.CheckedOutBy)
            && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        var checkoutOnCurrentMachine = string.IsNullOrWhiteSpace(document.CheckoutMachine)
            || string.Equals(document.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase);
        node.CheckoutSessionLost = checkedOutByCurrentUser
            && checkoutOnCurrentMachine
            && document.CheckoutSessionId != checkoutSessionId;
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
        var matchingNodes = (currentTree == null
                ? Array.Empty<CadTreeNode>()
                : EnumerateCadNodes(currentTree)
                    .Where(candidate => PathsEqual(candidate.FullPath, source.FullPath)))
            .Concat(new[] { source })
            .Distinct()
            .ToArray();

        if (!PdmDocumentIdentityStore.TryWrite(source.FullPath, document.Id, document.ProjectId))
            throw new IOException("无法保存本机图档关联，已停止后续提交。请检查本机缓存目录写入权限后重试：" + source.FileName);
        foreach (var node in matchingNodes)
        {
            node.DocumentId = document.Id;
            node.ProvenanceDocumentId = document.Id;
            node.ProvenanceProjectId = document.ProjectId;
            node.IsExternalProvenance = !PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath);
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
                && candidate.CheckedOutAt.HasValue
                && IsCheckedOutByCurrentUser(candidate)
                && candidate.CheckoutSessionId == checkoutSessionId
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
        IReadOnlyDictionary<Guid, DocumentDto> documentsById)
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
        if (document != null)
        {
            node.DocumentId = document.Id;
            PdmDocumentIdentityStore.TryWrite(node.FullPath, document.Id, document.ProjectId);
            ApplyCheckoutDocument(node, document);
        }

        foreach (var child in node.Children)
        {
            ApplyMetadata(child, documentsById);
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

        ApplyControlledOpenMetadata(root, filesByPath, context.Value.ProjectId);
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
                if (!PdmDocumentIdentityStore.TryWriteControlledVersion(
                    fullPath,
                    file.DocumentId,
                    manifest.ProjectId,
                    file.VersionId,
                    file.Revision,
                    file.Sha256,
                    file.FileLength))
                    throw new IOException("无法保存受控工作区关联，未打开图档：" + file.FileName);
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
            node.ProvenanceDocumentId = documentId;
            node.ProvenanceProjectId = PdmDocumentIdentityStore.ReadProjectId(node.FullPath);
            node.IsExternalProvenance = false;
        }
        else if (PdmDocumentIdentityStore.TryReadProvenance(node.FullPath, out var provenanceDocumentId, out var provenanceProjectId))
        {
            node.DocumentId = null;
            node.ProvenanceDocumentId = provenanceDocumentId;
            node.ProvenanceProjectId = provenanceProjectId;
            node.IsExternalProvenance = true;
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
                PdmDocumentIdentityStore.TryWrite(targets[0].FullPath, sources[0].DocumentId.Value,
                    PdmDocumentIdentityStore.ReadProjectId(sources[0].FullPath) ?? currentProjectId);
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

    private static void ApplyControlledOpenMetadata(CadTreeNode node, IReadOnlyDictionary<string, ControlledOpenFileDto> filesByPath, Guid projectId)
    {
        if (node == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.FullPath)
            && filesByPath.TryGetValue(Path.GetFullPath(node.FullPath), out var file))
        {
            node.DocumentId = file.DocumentId;
            node.ProvenanceDocumentId = file.DocumentId;
            node.ProvenanceProjectId = projectId;
            node.IsExternalProvenance = false;
            PdmDocumentIdentityStore.TryWriteControlledVersion(
                node.FullPath,
                file.DocumentId,
                projectId,
                file.VersionId,
                file.Revision,
                file.Sha256,
                file.FileLength);
            node.OpenedVersionId = file.VersionId;
            node.OpenedRevision = file.Revision ?? string.Empty;
            node.Revision = node.OpenedRevision;
            node.CurrentRevision = node.OpenedRevision;
        }

        foreach (var child in node.Children)
        {
            ApplyControlledOpenMetadata(child, filesByPath, projectId);
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsPathWithinDirectory(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(NormalizeDirectory(directory), StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>>> ApplyWorkingStatesAsync(CadTreeNode root)
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
        const int versionBatchSize = 8;
        for (var offset = 0; offset < documentIds.Length; offset += versionBatchSize)
        {
            var batchIds = documentIds.Skip(offset).Take(versionBatchSize).ToArray();
            var batchTasks = batchIds.Select(async documentId => new
            {
                DocumentId = documentId,
                Versions = await GetTreeVersionsAsync(documentId)
            }).ToArray();
            var batchResults = await Task.WhenAll(batchTasks);
            foreach (var result in batchResults)
            {
                if (result.Versions == null)
                {
                    continue;
                }

                versionsByDocument[result.DocumentId] = result.Versions;
                var latest = result.Versions.FirstOrDefault();
                latestHashes[result.DocumentId] = VersionSourceSha256(latest) ?? string.Empty;
                latestStoredHashes[result.DocumentId] = latest?.Sha256 ?? string.Empty;
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
            node.StoredVersionStateKnown = node.DocumentId.HasValue
                && versionsByDocument.ContainsKey(node.DocumentId.Value);
            node.HasStoredVersion = node.DocumentId.HasValue
                && versionsByDocument.TryGetValue(node.DocumentId.Value, out var storedVersions)
                && storedVersions.Count > 0;
            if (node.DocumentId.HasValue && versionsByDocument.TryGetValue(node.DocumentId.Value, out var propertyVersions))
            {
                var properties = propertyVersions.FirstOrDefault()?.PropertySnapshot;
                node.Description = FindSnapshotProperty(properties, "描述", "Description");
                node.Material = FindSnapshotProperty(properties, "材料", "Material");
            }
            node.CurrentRevision = DetermineCurrentRevision(node, versionsByDocument, localHashes);
            node.WorkState = DetermineWorkState(node, localHashes, versionsByDocument);
        }

        return versionsByDocument;
    }

    private async Task ResolveRecoverableCheckoutSessionsAsync(
        CadTreeNode root,
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>> versionsByDocument)
    {
        if (root == null || Interlocked.Exchange(ref staleCheckoutResolutionInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref workspaceOperationInProgress) != 0
                || Volatile.Read(ref checkInOperationInProgress) != 0)
            {
                return;
            }

            var candidates = EnumerateCadNodes(root)
                .Where(node => !node.IsReadOnlyPreview
                    && node.DocumentId.HasValue
                    && CanRecoverCurrentCheckoutSession(node))
                .GroupBy(node => node.DocumentId.Value)
                .ToArray();
            if (candidates.Length == 0)
            {
                return;
            }

            var paths = candidates
                .SelectMany(group => group)
                .Select(node => node.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var localHashes = await Task.Run(() => ComputeFileHashes(paths));

            foreach (var group in candidates)
            {
                if (!ReferenceEquals(currentTree, root) || disconnecting)
                {
                    return;
                }

                var nodes = group.ToArray();
                var representative = nodes.FirstOrDefault(node =>
                    !string.IsNullOrWhiteSpace(node.FullPath)
                    && File.Exists(node.FullPath)
                    && localHashes.ContainsKey(node.FullPath));
                if (representative == null)
                {
                    LogOperation(string.Concat(
                        "Stale checkout kept because local file could not be verified document=",
                        group.Key));
                    continue;
                }

                var hasUnsavedChanges = nodes.Any(node => node.IsModifiedInSolidWorks);
                versionsByDocument.TryGetValue(group.Key, out var versions);
                var matchingVersion = hasUnsavedChanges
                    ? null
                    : (versions ?? Array.Empty<DocumentVersionDto>()).FirstOrDefault(version =>
                        LocalDocumentMatchesArchivedVersion(representative, version, localHashes[representative.FullPath]));
                var recoverAsEditable = hasUnsavedChanges || matchingVersion == null;

                try
                {
                    var checkout = await apiClient.CheckoutAsync(
                        group.Key,
                        checkoutSessionId,
                        checkoutMachineName,
                        lifetime.Token);
                    ApplyCheckoutDocumentToMatchingInstances(nodes, checkout);

                    if (recoverAsEditable)
                    {
                        foreach (var node in nodes)
                        {
                            node.WorkState = node.IsModifiedInSolidWorks
                                ? CadWorkState.ModifiedUnsaved
                                : CadWorkState.PendingCheckIn;
                        }

                        foreach (var path in nodes
                                     .Select(node => node.FullPath)
                                     .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            SetFileReadOnly(path, false);
                            EnsureLoadedDocumentEditable(path);
                        }

                        LogOperation(string.Concat(
                            "Stale checkout recovered as editable document=",
                            group.Key,
                            " unsaved=",
                            hasUnsavedChanges));
                        continue;
                    }

                    var released = await apiClient.DiscardCheckoutAsync(group.Key, checkoutSessionId, lifetime.Token);
                    ApplyCheckoutDocumentToMatchingInstances(nodes, released);
                    foreach (var node in nodes)
                    {
                        node.WorkState = CadWorkState.None;
                    }

                    foreach (var path in nodes
                                 .Select(node => node.FullPath)
                                 .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        ProtectLoadedDocument(path);
                    }

                    LogOperation(string.Concat(
                        "Stale checkout released unchanged document=",
                        group.Key,
                        " revision=",
                        matchingVersion.Revision?.Display ?? string.Empty));
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("ResolveRecoverableCheckoutSession.", group.Key), exception);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref staleCheckoutResolutionInProgress, 0);
        }
    }

    private static bool LocalDocumentMatchesArchivedVersion(
        CadTreeNode node,
        DocumentVersionDto version,
        string localSha256)
    {
        if (node == null
            || version == null
            || !VersionMatchesFile(version, localSha256)
            || !VersionFileNameMatches(version, node.FullPath))
        {
            return false;
        }

        return node.Kind != CadDocumentKind.Assembly
            || ReferenceSnapshotMatchesTree(version.ReferenceSnapshot, node);
    }

    private void ApplyCheckoutDocumentToMatchingInstances(
        IEnumerable<CadTreeNode> nodes,
        DocumentDto document)
    {
        foreach (var node in (nodes ?? Array.Empty<CadTreeNode>()).Where(node => node != null))
        {
            ApplyCheckoutDocument(node, document);
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
        node.StoredVersionStateKnown = true;
        node.HasStoredVersion = latest != null;
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

    private bool HistoricalPartEditMatchesLatest(
        CadTreeNode node,
        DocumentVersionDto latest,
        string fullPath,
        string localSha256)
    {
        if (node?.Kind != CadDocumentKind.Part
            || !node.DocumentId.HasValue
            || !historicalPartEditContexts.TryGetValue(node.DocumentId.Value, out var context)
            || !HistoricalVersionContentMatches(context.SourceVersion, latest))
        {
            return false;
        }

        return VersionMatchesLocalFile(context.SourceVersion, fullPath, localSha256);
    }

    private string BuildHistoricalPartChangeNote(CadTreeNode node, string changeNote)
    {
        if (node == null
            || !node.DocumentId.HasValue
            || !historicalPartEditContexts.TryGetValue(node.DocumentId.Value, out var context))
        {
            return changeNote ?? string.Empty;
        }

        var sourceRevision = context.SourceVersion?.Revision?.Display ?? "-";
        var latestRevision = context.LatestVersion?.Revision?.Display ?? "-";
        var sourceNote = string.Concat("基于历史版本", sourceRevision, "编辑，提交前最新版本", latestRevision);
        return string.IsNullOrWhiteSpace(changeNote)
            ? sourceNote
            : string.Concat(sourceNote, "；", changeNote.Trim());
    }

    private static bool HistoricalVersionContentMatches(DocumentVersionDto left, DocumentVersionDto right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var leftSourceSha256 = VersionSourceSha256(left);
        var rightSourceSha256 = VersionSourceSha256(right);
        if (!string.IsNullOrWhiteSpace(leftSourceSha256) && !string.IsNullOrWhiteSpace(rightSourceSha256))
        {
            return string.Equals(leftSourceSha256, rightSourceSha256, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(left.Sha256)
            && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCheckedOutByCurrentUser(CadTreeNode node) =>
        !string.IsNullOrWhiteSpace(authenticatedUsername)
        && !string.IsNullOrWhiteSpace(node?.CheckedOutBy)
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
        && node.CheckoutSessionId == checkoutSessionId
        && !node.CheckoutSessionLost;

    private bool CanRecoverCurrentCheckoutSession(CadTreeNode node) =>
        node?.CheckoutSessionLost == true
        && !string.IsNullOrWhiteSpace(authenticatedUsername)
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(node.CheckoutMachine)
            || string.Equals(node.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase));

    private bool EnsureCurrentCheckoutSession(CadTreeNode node, string operation)
    {
        if (IsCheckedOutByCurrentUser(node))
        {
            return true;
        }

        if (CanRecoverCurrentCheckoutSession(node))
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
            ? string.Concat(
                "当前图档尚未获取编辑权限，不能", operation,
                "。请先点击“获取”取得编辑权限，完成编辑并保存后再提交；整套装配请使用“整体”中的“获取最新并获取权限”。")
            : string.Concat("当前图档正在由", node.CheckedOutBy, "编辑，不能", operation, "。"));
        return false;
    }

    private CadWorkState DetermineWorkState(
        CadTreeNode node,
        IReadOnlyDictionary<string, string> localHashes,
        IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>> versionsByDocument)
    {
        if (node.IsReadOnlyPreview)
        {
            return CadWorkState.None;
        }

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
            ShowError("请先登录PLM。 ");
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
        byte[] iconBytes;
        using (var stream = typeof(PdmAddin).Assembly.GetManifestResourceStream("Upton.Pdm.SolidWorks.Assets.PdmClient.png"))
        using (var buffer = new MemoryStream())
        {
            stream?.CopyTo(buffer);
            iconBytes = buffer.ToArray();
        }

        string iconVersion;
        using (var hash = SHA256.Create())
        {
            iconVersion = BitConverter.ToString(hash.ComputeHash(iconBytes)).Replace("-", string.Empty).Substring(0, 12);
        }

        var iconPath = Path.Combine(directory, string.Concat("plm-taskpane-", iconVersion, ".bmp"));
        if (File.Exists(iconPath))
        {
            return iconPath;
        }

        using (var bitmap = new Bitmap(20, 20))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            if (iconBytes.Length > 0)
            {
                using (var stream = new MemoryStream(iconBytes))
                using (var image = Image.FromStream(stream))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, new Rectangle(0, 0, 20, 20));
                }
            }
            bitmap.Save(iconPath, System.Drawing.Imaging.ImageFormat.Bmp);
        }

        return iconPath;
    }

    private bool TryBeginWorkspaceOperation(string description)
    {
        if (Interlocked.CompareExchange(ref workspaceOperationInProgress, 1, 0) != 0)
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(description) ? "正在处理PLM工作文件" : description.Trim();
        Interlocked.Exchange(ref workspaceOperationDescription, normalized);
        taskPaneControl?.SetWorkspaceOperationState(true, normalized);
        return true;
    }

    private void EndWorkspaceOperation()
    {
        Interlocked.Exchange(ref workspaceOperationDescription, string.Empty);
        taskPaneControl?.SetWorkspaceOperationState(false, string.Empty);
        Interlocked.Exchange(ref workspaceOperationInProgress, 0);
    }

    private void ShowWorkspaceOperationBusy()
    {
        if (BringActiveBatchProgressToFront())
        {
            return;
        }

        var description = Volatile.Read(ref workspaceOperationDescription);
        ShowError(string.Concat(
            "当前PLM操作正在进行：",
            string.IsNullOrWhiteSpace(description) ? "正在处理工作文件" : description,
            "。\r\n任务窗格顶部已显示进度，完成后按钮会自动恢复，请勿重复操作。"));
    }

    private void ShowError(string message)
    {
        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
        {
            return;
        }

        try
        {
            MessageBox.Show(taskPaneControl, message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private sealed class LatestWorkspacePreparation
    {
        public LatestWorkspacePreparation(
            CadTreeNode node,
            DocumentVersionDto latest,
            string stagedPath,
            bool alreadyLatest,
            string skipReason)
        {
            Node = node;
            Latest = latest;
            StagedPath = stagedPath ?? string.Empty;
            AlreadyLatest = alreadyLatest;
            SkipReason = skipReason ?? string.Empty;
        }

        public CadTreeNode Node { get; }
        public DocumentVersionDto Latest { get; }
        public string StagedPath { get; }
        public bool AlreadyLatest { get; }
        public string SkipReason { get; }
    }

    private sealed class HistoricalPartEditContext
    {
        public HistoricalPartEditContext(DocumentVersionDto sourceVersion, DocumentVersionDto latestVersion)
        {
            SourceVersion = sourceVersion;
            LatestVersion = latestVersion;
        }

        public DocumentVersionDto SourceVersion { get; }
        public DocumentVersionDto LatestVersion { get; }
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
        public BatchNodeCheckInResult(bool versionCreated, string bomWarning)
        {
            VersionCreated = versionCreated;
            BomWarning = bomWarning;
        }

        public bool VersionCreated { get; }
        public string BomWarning { get; }
    }

    private sealed class BatchCheckInPlan
    {
        public BatchCheckInPlan(
            IReadOnlyList<BatchOperationItem> items,
            int skippedFiles,
            IReadOnlyDictionary<string, BatchCheckInPreflight> preflights)
        {
            Items = items;
            SkippedFiles = skippedFiles;
            Preflights = preflights ?? new Dictionary<string, BatchCheckInPreflight>(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<BatchOperationItem> Items { get; }
        public int SkippedFiles { get; }
        public IReadOnlyDictionary<string, BatchCheckInPreflight> Preflights { get; }
    }

    private sealed class BatchCheckInPreflight
    {
        private BatchCheckInPreflight(
            string fullPath,
            IReadOnlyList<DocumentVersionDto> versions,
            string localSha256,
            long fileLength,
            long lastWriteUtcTicks)
        {
            FullPath = fullPath ?? string.Empty;
            Versions = versions ?? Array.Empty<DocumentVersionDto>();
            LocalSha256 = localSha256 ?? string.Empty;
            FileLength = fileLength;
            LastWriteUtcTicks = lastWriteUtcTicks;
        }

        public string FullPath { get; }
        public IReadOnlyList<DocumentVersionDto> Versions { get; }
        public string LocalSha256 { get; }
        private long FileLength { get; }
        private long LastWriteUtcTicks { get; }

        public static BatchCheckInPreflight Create(
            string fullPath,
            IReadOnlyList<DocumentVersionDto> versions,
            string localSha256)
        {
            var file = new FileInfo(fullPath);
            return new BatchCheckInPreflight(
                fullPath,
                versions,
                localSha256,
                file.Exists ? file.Length : -1,
                file.Exists ? file.LastWriteTimeUtc.Ticks : -1);
        }

        public bool MatchesCurrentFile(string path)
        {
            if (!PathsEqual(FullPath, path))
            {
                return false;
            }
            var file = new FileInfo(path);
            return file.Exists
                && file.Length == FileLength
                && file.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
        }
    }

    private sealed class BatchCheckInResult
    {
        public int PreparedPermissions { get; set; }
        public int CreatedVersions { get; set; }
        public int UnchangedFiles { get; set; }
        public List<string> Failures { get; } = new List<string>();
        public List<string> BomWarnings { get; } = new List<string>();
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
