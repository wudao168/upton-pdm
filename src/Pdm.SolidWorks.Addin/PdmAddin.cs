using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
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
public sealed partial class PdmAddin : ISwAddin
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
    private CancellationTokenSource metadataRefreshCancellation;
    private readonly SemaphoreSlim clientUpdateGate = new SemaphoreSlim(1, 1);
    private readonly object clientUpdateStatusSync = new object();
    private readonly object metadataRefreshSync = new object();
    private readonly object metadataCacheSync = new object();
    private readonly object fileHashCacheSync = new object();
    private readonly object activeTreeCacheSync = new object();
    private readonly Dictionary<Guid, ProjectDocumentsCacheEntry> projectDocumentsCache = new Dictionary<Guid, ProjectDocumentsCacheEntry>();
    private readonly Dictionary<Guid, DocumentVersionsCacheEntry> documentVersionsCache = new Dictionary<Guid, DocumentVersionsCacheEntry>();
    private readonly Dictionary<string, FileHashCacheEntry> fileHashCache = new Dictionary<string, FileHashCacheEntry>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveTreeCacheEntry> activeTreeCache = new Dictionary<string, ActiveTreeCacheEntry>(StringComparer.OrdinalIgnoreCase);
    private CadTreeNode currentTree;
    private string currentTreeCacheKey = string.Empty;
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
    private IModelDoc2 assemblyEventDocument;
    private string assemblyEventDocumentPath = string.Empty;
    private DPartDocEvents_Event partEvents;
    private string partEventDocumentPath = string.Empty;
    private DDrawingDocEvents_Event drawingEvents;
    private string drawingEventDocumentPath = string.Empty;
    private string authenticatedUsername = string.Empty;
    private string authenticatedSessionJson = string.Empty;
    private int openOperationInProgress;
    private int checkInOperationInProgress;
    private int batchDialogInProgress;
    private int workspaceOperationInProgress;
    private string workspaceOperationDescription = string.Empty;
    private BatchProgressDialog activeBatchProgressDialog;
    private int treeRefreshInProgress;
    private DateTime nextFailedTreeRefreshUtc;
    private string failedTreeRefreshKey = string.Empty;
    private int controlledOpenInProgress;
    private int refreshSuppressionDepth;
    private TaskPaneSynchronizationContext taskPaneSynchronizationContext;
    private int pendingTreeRefresh;
    private int pendingTreeRefreshForceScan;
    private long pendingTreeRefreshScheduledAt;
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
    private readonly HashSet<Guid> pendingArchiveDocumentIds = new HashSet<Guid>();
    private readonly Dictionary<Guid, int> checkoutReminderLevels = new Dictionary<Guid, int>();
    private readonly Dictionary<Guid, HistoricalEditContext> historicalEditContexts = new Dictionary<Guid, HistoricalEditContext>();
    private DateTime nextCheckoutHeartbeatUtc = DateTime.MinValue;
    private int checkoutHeartbeatSeconds = 180;
    private int checkoutReminderHours = 4;
    private int checkoutStrongReminderHours = 8;
    private bool disconnecting;
    private string addinDirectory = string.Empty;
    private PluginSettings pluginSettings = new PluginSettings();
    private ClientBootstrapConfiguration currentBootstrap;
    private PluginUpdateSnapshot clientUpdateSnapshot = new PluginUpdateSnapshot();
    private const int ActiveDocumentRefreshDebounceMilliseconds = 200;
    private const int StructuralRefreshDebounceMilliseconds = 800;
    private const int ActiveTreeCacheCapacity = 8;
    private const int MetadataRefreshDelayMilliseconds = 300;
    private static readonly TimeSpan NavigationMetadataCacheDuration = TimeSpan.FromSeconds(2);
    private const long OperationLogMaxBytes = 10L * 1024L * 1024L;
    private const int OperationLogBackupCount = 3;
    private static readonly ConcurrentQueue<string> OperationLogQueue = new ConcurrentQueue<string>();
    private static int operationLogDrainScheduled;

    public bool ConnectToSW(object thisSw, int cookie)
    {
        // The locally owned archive reader needs no interactive task pane or listeners.
        if (ArchiveNativeProcessWorkspace.ConnectReaderAddin()) { archiveReaderAddin = true; return true; }
        var connectTimer = Stopwatch.StartNew();
        ClientDiagnostics.Initialize("solidworks", ClientPackageUpdater.GetInstalledVersion(Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location)));
        try
        {
            disconnecting = false;
            application = (ISldWorks)thisSw;
            if (!application.SetAddinCallbackInfo2(0, this, cookie))
            {
                return false;
            }

            lifetime = new CancellationTokenSource();
            pluginSettings = PluginSettingsStore.Load();
            var bootstrap = ClientBootstrapLoader.LoadLocalConfiguration();
            currentBootstrap = bootstrap;
            apiClient = new PdmApiClient(bootstrap.ApiBaseUrl);
            apiClient.BeforeCheckout = FinishPendingCloseBeforeCheckoutAsync;
            apiClient.AuthenticationExpired += OnAuthenticationExpired;
            controlledWorkspace = new ControlledWorkspaceManager(apiClient);
            controlledOpenListener = new SolidWorksOpenRequestListener();
            controlledOpenListener.Start();
            scanner = new SolidWorksReferenceTreeScanner(application);
            addinDirectory = Path.GetDirectoryName(typeof(PdmAddin).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            taskPaneControl = new PdmTaskPaneControl(ClientPackageUpdater.GetInstalledVersion(addinDirectory));
            taskPaneControl.SetPlannedTaskScrollInterval(pluginSettings.PlannedTaskScrollIntervalSeconds);
            taskPaneControl.CreateControl();
            WireEvents();
            WireSolidWorksEvents();

            taskPaneView = application.CreateTaskpaneView2(CreateTaskPaneIcon(), "UPLM");
            taskPaneView.DisplayWindowFromHandlex64(taskPaneControl.Handle.ToInt64());
            taskPaneSynchronizationContext = new TaskPaneSynchronizationContext(taskPaneControl);
            taskPaneControl.SetConnectionState(false, "等待SolidWorks启动完成");
            RefreshTree(false);
            SetInitialUpdateSnapshot();
            LogOperation("ConnectToSW success elapsedMs=" + connectTimer.ElapsedMilliseconds + " serverSync=deferred pid=" + Process.GetCurrentProcess().Id);
            return true;
        }
        catch (Exception exception)
        {
            LogDiagnostic("ConnectToSW", exception);
            DisconnectFromSW();
            return false;
        }
    }

    private async Task MonitorThemeAsync()
    {
        var cancellationToken = lifetime.Token;
        while (!disconnecting && !cancellationToken.IsCancellationRequested)
        {
            await RefreshThemeAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshThemeAsync(CancellationToken cancellationToken)
    {
        var client = apiClient;
        var username = authenticatedUsername;
        if (client == null || !client.IsAuthenticated || string.IsNullOrWhiteSpace(username)) return;
        try
        {
            var profile = await client.GetUserThemeAsync(cancellationToken).ConfigureAwait(false);
            if (!disconnecting && ReferenceEquals(client, apiClient) && string.Equals(username, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
            {
                taskPaneControl?.ApplyTheme(profile.Theme);
                SyncWorkHoursTheme(profile.Theme);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LogDiagnostic("同步账号主题", error); }
    }

    private async Task MonitorClientUpdatesAsync()
    {
        while (!disconnecting && lifetime != null && !lifetime.IsCancellationRequested)
        {
            try
            {
                pluginSettings = PluginSettingsStore.Load();
                if (pluginSettings.AutomaticUpdatesEnabled)
                {
                    await CheckClientUpdateAsync(pluginSettings.ServerAddress, true, lifetime.Token).ConfigureAwait(false);
                }
                var pollSeconds = currentBootstrap?.PollSeconds ?? 30;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, pollSeconds)), lifetime.Token).ConfigureAwait(false);
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
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, currentBootstrap?.PollSeconds ?? 30)), lifetime.Token).ConfigureAwait(false);
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
        if (archiveReaderAddin) { archiveReaderAddin = false; return true; }
        disconnecting = true;
        try
        {
            FinishClosedEditsBeforeDisconnect();
            CancelMetadataRefresh();
            CancelProjectNavigation();
            lifetime?.Cancel();
            ArchiveSession.Current?.CloseForShutdown();
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
            taskPaneSynchronizationContext = null;
            Interlocked.Exchange(ref serverInitializationScheduled, 0);
            apiClient = null;
            controlledWorkspace = null;
            controlledOpenListener = null;
            pendingControlledOpenRequest = null;
            queuedControlledOpens.Clear();
            queuedControlledOpenKeys.Clear();
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
            authenticatedSessionJson = string.Empty;
            availableProjects = Array.Empty<ProjectDto>();
            userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            currentDocumentIdentity = string.Empty;
            currentTreeCacheKey = string.Empty;
            currentProjectId = null;
            controlledOpenManifests.Clear();
            explicitProjectPaths.Clear();
            pendingAssemblyItemRenames.Clear();
            lock (checkoutDocumentSync) { activeCheckoutDocumentIds.Clear(); pendingArchiveDocumentIds.Clear(); }
            checkoutReminderLevels.Clear();
            historicalEditContexts.Clear();
            lock (metadataCacheSync)
            {
                projectDocumentsCache.Clear();
                documentVersionsCache.Clear();
            }
            lock (fileHashCacheSync) fileHashCache.Clear();
            lock (activeTreeCacheSync) activeTreeCache.Clear();
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
        taskPaneControl.WorkHoursRequested += OnWorkHoursRequested;
        taskPaneControl.PlannedTasksRefreshRequested += OnPlannedTasksRefreshRequested;
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
        taskPaneControl.SettingsRequested += OnSettingsRequested;
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
        taskPaneControl.WorkHoursRequested -= OnWorkHoursRequested;
        taskPaneControl.PlannedTasksRefreshRequested -= OnPlannedTasksRefreshRequested;
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
        taskPaneControl.SettingsRequested -= OnSettingsRequested;
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
            applicationEvents.FileOpenPreNotify += OnFileOpening;
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
        UnwireDrawingEvents();
        if (applicationEvents == null)
        {
            return;
        }

        try
        {
            applicationEvents.ActiveDocChangeNotify -= OnActiveDocumentChanged;
            applicationEvents.ActiveModelDocChangeNotify -= OnActiveDocumentChanged;
            applicationEvents.FileOpenPreNotify -= OnFileOpening;
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
            var isDrawing = activeDocument != null && activeDocument.GetType() == (int)swDocumentTypes_e.swDocDRAWING;
            if (isAssembly && assemblyEvents != null && string.Equals(activePath, assemblyEventDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (isPart && partEvents != null && string.Equals(activePath, partEventDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (isDrawing && drawingEvents != null && string.Equals(activePath, drawingEventDocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnwireAssemblyEvents();
            UnwirePartEvents();
            UnwireDrawingEvents();
            if (isDrawing)
            {
                drawingEvents = activeDocument as DDrawingDocEvents_Event;
                if (drawingEvents != null)
                {
                    drawingEventDocumentPath = activePath;
                    drawingEvents.FileSavePostNotify += OnDrawingFileSaved;
                }
                return;
            }
            if (isPart)
            {
                partEvents = activeDocument as DPartDocEvents_Event;
                if (partEvents != null)
                {
                    partEventDocumentPath = activePath;
                    partEvents.UserSelectionPostNotify += OnSolidWorksSelectionChanged;
                    partEvents.ModifyNotify += OnDocumentStateChanged;
                    partEvents.FileSavePostNotify += OnAssemblyFileSaved;
                    partEvents.RegenPostNotify += OnDocumentStateChanged;
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
            assemblyEventDocument = activeDocument;

            assemblyEvents.UserSelectionPostNotify += OnSolidWorksSelectionChanged;
            assemblyEvents.RegenPostNotify += OnDocumentStateChanged;
            assemblyEvents.ModifyNotify += OnDocumentStateChanged;
            assemblyEvents.FileSavePostNotify += OnAssemblyFileSaved;
            assemblyEvents.ActiveConfigChangePostNotify += OnAssemblyTreeChanged;
            assemblyEvents.UndoPostNotify += OnAssemblyTreeChanged;
            assemblyEvents.RedoPostNotify += OnAssemblyTreeChanged;
            assemblyEvents.AddItemNotify += OnAssemblyItemChanged;
            assemblyEvents.DeleteItemNotify += OnAssemblyItemChanged;
            assemblyEvents.DeleteSelectionPreNotify += OnAssemblyDeleteSelectionPreNotify;
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
            assemblyEvents.RegenPostNotify -= OnDocumentStateChanged;
            assemblyEvents.ModifyNotify -= OnDocumentStateChanged;
            assemblyEvents.FileSavePostNotify -= OnAssemblyFileSaved;
            assemblyEvents.ActiveConfigChangePostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.UndoPostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.RedoPostNotify -= OnAssemblyTreeChanged;
            assemblyEvents.AddItemNotify -= OnAssemblyItemChanged;
            assemblyEvents.DeleteItemNotify -= OnAssemblyItemChanged;
            assemblyEvents.DeleteSelectionPreNotify -= OnAssemblyDeleteSelectionPreNotify;
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
        assemblyEventDocument = null;
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
            partEvents.ModifyNotify -= OnDocumentStateChanged;
            partEvents.FileSavePostNotify -= OnAssemblyFileSaved;
            partEvents.RegenPostNotify -= OnDocumentStateChanged;
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

    private void UnwireDrawingEvents()
    {
        if (drawingEvents == null)
        {
            return;
        }

        try
        {
            drawingEvents.FileSavePostNotify -= OnDrawingFileSaved;
        }
        catch (Exception exception)
        {
            LogDiagnostic("UnwireDrawingEvents", exception);
        }

        drawingEvents = null;
        drawingEventDocumentPath = string.Empty;
    }

    private int OnActiveDocumentChanged()
    {
        if (Volatile.Read(ref nativeFileOpenInProgress) != 0)
        {
            ScheduleTreeRefresh(forceScan: false);
            return 0;
        }
        if (ImportedComponentStorage.IsReadingInventory) return 0;
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return 0;
        if (Volatile.Read(ref checkInOperationInProgress) > 0)
        {
            ScheduleTreeRefresh(forceScan: false);
            return 0;
        }
        CancelMetadataRefresh();
        CacheCurrentTreeContext();
        try
        {
            var activeKey = BuildActiveTreeCacheKey(application?.ActiveDoc as IModelDoc2);
            if (!string.Equals(activeKey, currentTreeCacheKey, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref pendingTreeRefreshForceScan, 0);
            }
        }
        catch
        {
            Interlocked.Exchange(ref pendingTreeRefreshForceScan, 0);
        }
        ScheduleTreeRefresh(forceScan: false);
        return 0;
    }

    private int nativeFileOpenInProgress;

    private int OnFileOpening(string fileName)
    {
        Interlocked.Exchange(ref nativeFileOpenInProgress, 1);
        ScheduleTreeRefresh(forceScan: false);
        return 0;
    }

    private int OnFileOpened(string fileName)
    {
        Interlocked.Exchange(ref nativeFileOpenInProgress, 0);
        InvalidateTreeCacheForPath(fileName);
        ScheduleTreeRefresh(forceScan: false);
        return 0;
    }

    private int OnFileCreated(object newDocument, int documentType, string templateName)
    {
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnFileClosed(string fileName, int reason)
    {
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return 0;
        nextEditMaintenanceUtc = DateTime.MinValue;
        foreach (var entry in localEdits.Values.Where(entry => PathsEqual(entry.SourcePath, fileName))) entry.ObservedOpen = true;
        InvalidateTreeCacheForPath(fileName);
        var closedCurrentDocument = IsCurrentDocument(fileName);
        if (closedCurrentDocument)
        {
            ClearActiveDocumentContext();
        }

        LogOperation(string.Concat(
            "FileCloseNotify file=", fileName,
            " reason=", reason,
            " current=", closedCurrentDocument));
        ScheduleTreeRefresh(forceScan: false);
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
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnDocumentStateChanged()
    {
        ScheduleTreeRefresh(forceScan: false);
        return 0;
    }

    private int OnSolidWorksSelectionChanged()
    {
        if (IsSolidWorksCommandActive()) return 0;
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
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnDrawingFileSaved(int saveType, string fileName)
    {
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return 0;
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh(forceScan: false);
        return 0;
    }

    private int OnAssemblyItemChanged(int entityType, string itemName)
    {
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyItemRenamed(int entityType, string oldName, string newName)
    {
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return 0;
        if (!string.IsNullOrWhiteSpace(oldName) && !string.IsNullOrWhiteSpace(newName))
        {
            pendingAssemblyItemRenames.Add(new AssemblyItemRename(oldName, newName));
            LogOperation(string.Concat("Assembly item renamed old=", oldName, " new=", newName));
        }
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentStateChanged(object component, string componentName, short oldState, short newState)
    {
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentReorganized(string sourceName, string targetName)
    {
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private int OnAssemblyComponentConfigurationChanged(string componentName, string oldConfigurationName, string newConfigurationName)
    {
        InvalidateCurrentTreeCache();
        ScheduleTreeRefresh();
        return 0;
    }

    private void ScheduleTreeRefresh(bool restartDebounce = true, bool forceScan = true)
    {
        if (disconnecting || Volatile.Read(ref draftSnapshotInProgress) != 0)
        {
            return;
        }

        if (forceScan)
        {
            Interlocked.Exchange(ref pendingTreeRefreshForceScan, 1);
        }
        var wasPending = Interlocked.Exchange(ref pendingTreeRefresh, 1);
        if (restartDebounce || wasPending == 0)
        {
            Interlocked.Exchange(ref pendingTreeRefreshScheduledAt, Stopwatch.GetTimestamp());
        }
    }

    private int OnSolidWorksIdle()
    {
        // A document passed at process launch can emit idle while native loading is
        // still initializing. Resolving its components in that window is unsafe.
        try { if (application == null || !application.StartupProcessCompleted) return 0; }
        catch (COMException) { return 0; }
        // SolidWorks can send idle notifications while an Insert Component/Mate command is open.
        // Scanning (which may resolve components), saving, or activating documents can interrupt it.
        if (IsSolidWorksCommandActive()) return 0;
        TryInitializeServerAfterStartup();
        TryStartQueuedControlledOpen();
        TryStartControlledOpenRequest();
        TryMaintainEdits();
        SynchronizeActiveDocumentContextOnIdle();
        TryRefreshPendingTree("IdleRefresh");
        return 0;
    }

    private bool commandDeferred;

    private bool IsSolidWorksCommandActive()
    {
        // Native file opening pumps OnIdleNotify while configuration workers still
        // load references. GetRunningCommandInfo can report inactive in that pump.
        if (application == null || disconnecting || Volatile.Read(ref nativeFileOpenInProgress) != 0) return true;
        try
        {
            application.GetRunningCommandInfo(out var commandId, out var title, out var active);
            if (active && !commandDeferred)
                LogOperation("正在执行SolidWorks命令，暂缓后台扫描和草稿保存：" + title + "（" + commandId + "）");
            else if (!active && commandDeferred)
                LogOperation("SolidWorks命令已结束，恢复待处理的后台操作。");
            commandDeferred = active;
            return active;
        }
        catch (Exception exception)
        {
            if (!commandDeferred) LogDiagnostic("读取SolidWorks命令状态失败，暂缓后台操作", exception);
            commandDeferred = true;
            return true;
        }
    }

    private void SynchronizeActiveDocumentContextOnIdle()
    {
        if (DeferBackgroundTreeRefresh()) return;
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
                    Interlocked.Exchange(ref pendingTreeRefreshForceScan, 0);
                    Interlocked.Exchange(ref pendingTreeRefreshScheduledAt, 0);
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
                ScheduleTreeRefresh(false, false);
            }
        }
        catch (Exception exception)
        {
            // A closing COM document can become invalid between ActiveDoc and GetPathName.
            LogDiagnostic("SynchronizeActiveDocumentContextOnIdle", exception);
            ScheduleTreeRefresh(false, false);
        }
    }

    private void TryRefreshPendingTree(string source)
    {
        if (disconnecting
            || Volatile.Read(ref batchDialogInProgress) > 0
            || Volatile.Read(ref refreshSuppressionDepth) > 0
            || Volatile.Read(ref openOperationInProgress) > 0
            || Volatile.Read(ref workspaceOperationInProgress) > 0
            || Volatile.Read(ref pendingTreeRefresh) == 0
            || !PendingTreeRefreshDebounceElapsed()
            || taskPaneControl == null
            || taskPaneControl.IsDisposed
            || !taskPaneControl.IsHandleCreated
            || IsSolidWorksCommandActive()
            || Interlocked.Exchange(ref treeRefreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref pendingTreeRefresh, 0);
            var forceScan = Interlocked.Exchange(ref pendingTreeRefreshForceScan, 0) != 0;
            Interlocked.Exchange(ref pendingTreeRefreshScheduledAt, 0);
            LogOperation(string.Concat(source, " start forceScan=", forceScan));
            BindAssemblyEvents();
            LogOperation(string.Concat(source, " document events bound; reading design tree"));
            RefreshTree(false, !forceScan);
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

    private bool PendingTreeRefreshDebounceElapsed()
    {
        var scheduledAt = Interlocked.Read(ref pendingTreeRefreshScheduledAt);
        if (scheduledAt <= 0)
        {
            return true;
        }

        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - scheduledAt) * 1000L / Stopwatch.Frequency;
        var debounceMilliseconds = Volatile.Read(ref pendingTreeRefreshForceScan) != 0
            ? StructuralRefreshDebounceMilliseconds
            : ActiveDocumentRefreshDebounceMilliseconds;
        return elapsedMilliseconds >= debounceMilliseconds;
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
        lock (checkoutDocumentSync) documentIds = activeCheckoutDocumentIds.Where(id => !pendingCloseDocumentIds.Contains(id)
            && !pendingArchiveDocumentIds.Contains(id)).ToArray();
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
            var response = await apiClient.HeartbeatEditSessionAsync(checkoutSessionId, checkoutMachineName, documentIds, lifetime.Token).ConfigureAwait(false);
            if (response.Settings != null)
            {
                checkoutHeartbeatSeconds = Math.Max(30, response.Settings.CheckoutHeartbeatSeconds);
                checkoutReminderHours = Math.Max(1, response.Settings.CheckoutReminderHours);
                checkoutStrongReminderHours = Math.Max(checkoutReminderHours + 1, response.Settings.CheckoutStrongReminderHours);
                idleReleaseEnabled = response.Settings.CheckoutAutoReleaseEnabled;
                idleReleaseMinutes = Math.Max(5, response.Settings.CheckoutIdleReleaseMinutes);
                idleReleaseCountdownSeconds = Math.Max(30, response.Settings.CheckoutReleaseCountdownSeconds);
            }
            var lost = new HashSet<Guid>(response.LostDocumentIds ?? new List<Guid>());
            if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed) return;
            GetTaskPaneSynchronizationContext().Post(_ =>
            {
                if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed) return;
                lock (checkoutDocumentSync)
                {
                    lost.IntersectWith(activeCheckoutDocumentIds);
                    lost.ExceptWith(pendingArchiveDocumentIds);
                }
                var appliedLost = new HashSet<Guid>();
                foreach (var node in EnumerateTree(currentTree).Where(node => node.DocumentId.HasValue && documentIds.Contains(node.DocumentId.Value)))
                {
                    if (lost.Contains(node.DocumentId.Value))
                    {
                        if (ApplyLostCheckoutSession(node, response.ServerTime)) appliedLost.Add(node.DocumentId.Value);
                        continue;
                    }
                    if (node.CheckoutSessionId != checkoutSessionId || !IsCheckedOutByCurrentUser(node)) continue;
                    node.CheckoutLastHeartbeatAt = response.ServerTime;
                    UpdateCheckoutReminder(node, response.ServerTime);
                }
                lock (checkoutDocumentSync)
                    foreach (var id in appliedLost) activeCheckoutDocumentIds.Remove(id);
                if (appliedLost.Count > 0)
                    taskPaneControl.SetCheckoutReminder("编辑权限已被释放或转移。本地修改已保留，请重新打开并核对版本后继续编辑。", true);
                else if ((response.ReleaseRequestedDocumentIds ?? new List<Guid>()).Any(id => shownEditRequests.Add(id)))
                    taskPaneControl.SetCheckoutReminder("有人需要编辑您打开的图档。完成后关闭图档，系统会保留草稿、结束编辑并通知对方。", false);
                if (appliedLost.Count > 0 && !DeferBackgroundTreeRefresh())
                    taskPaneControl.SetTree(currentTree);
            }, null);
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

    private bool ApplyLostCheckoutSession(CadTreeNode node, DateTime serverTime)
    {
        if (node?.DocumentId == null || node.CheckoutSessionId != checkoutSessionId || !IsCheckedOutByCurrentUser(node)
            || node.CheckedOutAt > serverTime || node.CheckoutLastHeartbeatAt > serverTime) return false;
        lock (checkoutDocumentSync)
            if (pendingArchiveDocumentIds.Contains(node.DocumentId.Value)) return false;
        node.CheckoutSessionLost = true;
        node.CheckoutSessionId = null;
        node.WorkState = CadWorkState.EditingByOther;
        SetFileReadOnly(node.FullPath, true, "服务器确认正式图档编辑权限丢失；图档=" + node.DocumentId);
        return true;
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
        CancelProjectNavigation();
        lock (metadataCacheSync)
        {
            navigationVersions.Clear(); navigationVersionStamps.Clear(); navigationRelations.Clear();
            projectDocumentsCache.Clear(); documentVersionsCache.Clear();
        }
        if (disconnecting)
        {
            return;
        }

        authenticatedUsername = string.Empty;
        authenticatedSessionJson = string.Empty;
        availableProjects = Array.Empty<ProjectDto>();
        userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        taskPaneControl?.SetUserDisplayNames(userDisplayNames);
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

    private void OnSettingsRequested(object sender, EventArgs eventArgs)
    {
        var serverAddress = string.IsNullOrWhiteSpace(pluginSettings?.ServerAddress)
            ? PluginSettingsStore.ServerAddressFromConfiguration(currentBootstrap)
            : pluginSettings.ServerAddress;
        using (var dialog = new PluginSettingsDialog(
            new PluginSettings
            {
                ServerAddress = serverAddress,
                AutomaticUpdatesEnabled = pluginSettings?.AutomaticUpdatesEnabled != false,
                PlannedTaskScrollIntervalSeconds = pluginSettings?.PlannedTaskScrollIntervalSeconds ?? 30,
            },
            GetClientUpdateSnapshot(),
            GetClientUpdateSnapshot,
            TestPluginConnectionAsync,
            address => CheckClientUpdateAsync(address, false, lifetime.Token),
            address => CheckClientUpdateAsync(address, true, lifetime.Token, true),
            SavePluginSettingsAsync) { CleanTemporaryCopies = CleanArchiveTemporaryCopiesAsync })
        {
            dialog.ShowDialog(taskPaneControl);
        }
    }

    private async Task AuthenticateAsync(string username, string password)
    {
        taskPaneControl.SetConnectionState(false, "正在登录");
        var response = await apiClient.LoginAsync(username, password, lifetime.Token);
        authenticatedUsername = response.Username ?? string.Empty;
        authenticatedSessionJson = new JavaScriptSerializer().Serialize(new
        {
            accessToken = response.AccessToken,
            expiresAt = response.ExpiresAt,
            resumeToken = response.ResumeToken,
            username = response.Username,
            displayName = response.DisplayName,
            role = response.Role,
            roles = response.Roles ?? new List<string>(),
            permissions = response.Permissions ?? new List<string>(),
            primaryCompanyId = response.PrimaryCompanyId,
            activeCompanyId = response.ActiveCompanyId,
            activeCompanyName = response.ActiveCompanyName,
            crossCompanyView = response.CrossCompanyView,
            accessibleCompanies = (response.AccessibleCompanies ?? new List<CompanyOptionDto>())
                .Select(company => new { id = company.Id, name = company.Name, code = company.Code })
                .ToArray()
        });
        taskPaneControl.SetAuthenticatedUser(response.DisplayName, response.Username);
        _ = RefreshThemeAsync(lifetime.Token);
        try { standardLibraryArchiveAllowed = (await apiClient.GetStandardStructureCapabilitiesAsync(lifetime.Token)).CanArchive; }
        catch (Exception error) { standardLibraryArchiveAllowed = false; LogDiagnostic("读取标准库部门权限", error); }
        taskPaneControl.SetConnectionState(true, "服务正常");
        taskPaneControl.SetCheckoutReminder(string.Empty, false);
        var projects = await apiClient.GetProjectsAsync(lifetime.Token);
        availableProjects = projects;
        await RefreshUserDisplayNamesAsync();
        taskPaneControl.SetProjects(projects);
        _ = RecoverInterruptedArchiveBatchesAsync();
        _ = RefreshPlannedTasksAsync();
        if (currentTree != null && application?.ActiveDoc != null)
        {
            await ResolveProjectForCurrentDocumentAsync(currentTree, DocumentIdentity(currentTree));
        }
        TryStartControlledOpenRequest();
    }

    private bool refreshingPlannedTasks;
    private DateTime nextPlannedTasksRefreshUtc = DateTime.MinValue;
    private string plannedTasksRefreshUser;

    private DateTime nextArchiveBatchRecoveryUtc = DateTime.MinValue;
    private int archiveBatchRecoveryInProgress;
    private bool archiveRecoveryNeedsReview;

    private async Task RecoverInterruptedArchiveBatchesAsync() => await ReconcileArchiveBatchesAsync();

    private async Task<bool> ReconcileArchiveBatchesAsync()
    {
        if (Volatile.Read(ref checkInOperationInProgress) != 0 || Volatile.Read(ref workspaceOperationInProgress) != 0
            || Interlocked.Exchange(ref archiveBatchRecoveryInProgress, 1) != 0) return false;
        try
        {
        InterruptedArchiveBatch[] batches;
        try { batches = ArchiveBatchJournal.Recoverable(apiClient.EditJournalScope, authenticatedUsername).ToArray(); }
        catch (Exception error)
        { LogDiagnostic("读取中断存档记录", error); taskPaneControl.SetCheckoutReminder("本机存档恢复记录无法读取，请检查缓存目录权限；存档结果请刷新项目核对。", true); return false; }
        foreach (var batch in batches)
        {
            try
            {
                var receipt = await apiClient.GetArchiveBatchReceiptOrNullAsync(batch.BatchId, lifetime.Token);
                if (receipt == null)
                {
                    // Cancellation locks the same pending records as commit. A concurrently
                    // completing server transaction wins before the second receipt lookup.
                    await apiClient.CancelArchiveRegistrationAsync(batch.BatchId, batch.SessionId, lifetime.Token);
                    receipt = await apiClient.GetArchiveBatchReceiptOrNullAsync(batch.BatchId, lifetime.Token);
                    if (receipt == null)
                        ClearCanceledArchiveIdentities(currentTree == null ? Array.Empty<CadTreeNode>() : EnumerateArchiveNodes(currentTree),
                            batch.ReservedDocumentIds ?? new List<Guid>());
                }
                var message = receipt != null
                    ? "上次中断的存档已在服务器整批完成，请刷新项目核对。批次：" + batch.BatchId
                    : "上次中断的存档未提交，暂存登记已清理，本地文件保留。请重新整体存档。批次：" + batch.BatchId;
                PluginDiagnosticLog.Record("批次恢复", message);
                taskPaneControl.SetCheckoutReminder(message, receipt == null);
                ArchiveBatchJournal.Complete(batch.BatchId);
                if (receipt != null) { archiveRecoveryNeedsReview = true; ScheduleTreeRefresh(); }
            }
            catch (Exception error)
            {
                LogDiagnostic("中断存档恢复：" + batch.BatchId, error);
                taskPaneControl.SetCheckoutReminder("上次中断的存档结果暂未确认，请恢复服务器连接后重新登录核对。批次：" + batch.BatchId, true);
                return false;
            }
        }
        return true;
        }
        finally { Interlocked.Exchange(ref archiveBatchRecoveryInProgress, 0); }
    }

    private async void OnPlannedTasksRefreshRequested(object sender, EventArgs eventArgs)
    {
        await RefreshPlannedTasksAsync();
    }

    private async Task RefreshPlannedTasksAsync()
    {
        if (refreshingPlannedTasks || apiClient == null || !apiClient.IsAuthenticated) return;
        if (plannedTasksRefreshUser == authenticatedUsername && DateTime.UtcNow < nextPlannedTasksRefreshUtc) return;
        plannedTasksRefreshUser = authenticatedUsername;
        nextPlannedTasksRefreshUtc = DateTime.UtcNow.AddMinutes(5);
        refreshingPlannedTasks = true;
        var username = authenticatedUsername;
        try
        {
            var projects = await apiClient.GetProjectsAsync(lifetime.Token);
            var messages = new List<string>();
            foreach (var project in projects.Where(project => (project.Designers ?? new List<string>())
                .Any(designer => string.Equals(designer, username, StringComparison.OrdinalIgnoreCase))).OrderBy(project => project.Code))
            {
                var plan = await apiClient.GetProjectPlanAsync(project.Id, lifetime.Token);
                messages.AddRange((plan?.Tasks ?? new List<ProjectPlanTaskDto>())
                    .Where(task => string.Equals(task.Assignee, username, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(task => task.PlannedFinish)
                    .Select(task => string.Concat(project.Code, " · ", task.Name, "  计划完成：", task.PlannedFinish)));
            }
            if (string.Equals(username, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                taskPaneControl?.SetPlannedTasks(messages);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { LogDiagnostic("RefreshPlannedTasks", exception); }
        finally { refreshingPlannedTasks = false; }
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

        taskPaneControl?.SetUserDisplayNames(userDisplayNames);
    }

    private string ResolveUserDisplayName(string username)
    {
        var value = username?.Trim() ?? string.Empty;
        return userDisplayNames.TryGetValue(value, out var displayName) && !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : value;
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
            TopMostMessageBox.Show(
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
        if (IsSolidWorksCommandActive()) return;
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
        try
        {
            await apiClient.RequestCheckoutReleaseAsync(eventArgs.Node.DocumentId.Value, "需要继续编辑该图档，请在完成后关闭图档。", lifetime.Token);
            taskPaneControl.SetCheckoutReminder("已通知编辑人员，图档结束编辑后会在消息中心通知您。", false);
        }
        catch (Exception exception) { ShowError(exception.Message); }
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
                TopMostMessageBox.Show(taskPaneControl, "审批已撤回，图档已恢复为工作中。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                TopMostMessageBox.Show(taskPaneControl, "图档已受控作废。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (node.IsReadOnlyPreview)
            {
                throw new InvalidOperationException("只读预览不能重命名。");
            }
            if (node.DocumentId.HasValue && !IsCheckedOutByCurrentUser(node))
            {
                throw new InvalidOperationException("请先获取该图档的编辑权限。");
            }
            if (currentTree?.DocumentId.HasValue == true && !IsCheckedOutByCurrentUser(currentTree))
            {
                throw new InvalidOperationException("重命名会修改装配引用，请先获取当前装配体的编辑权限。");
            }
            EnsureLocalRenameFileWritable(node);
            if (currentTree != null && !PathsEqual(node.FullPath, currentTree.FullPath))
            {
                EnsureLocalRenameFileWritable(currentTree);
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

                ApplyDocumentRenameWithLinkedDrawing(node, newBaseName, assemblyModel, renamingRoot);
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("Rename document", exception);
            ShowError(string.Concat("重命名失败：", RenamePreparationError(exception)));
        }
    }

    private static string NormalizeRenamedDocumentName(string requestedName, string extension)
        => BatchRenameRule.NormalizeFileBaseName(requestedName, extension);

    private void ApplyControlledDocumentRename(
        CadTreeNode node,
        string requestedBaseName,
        IModelDoc2 assemblyModel,
        bool renamingRoot)
    {
        var extension = Path.GetExtension(node.FileName);
        var currentBaseName = Path.GetFileNameWithoutExtension(node.FileName);
        var newBaseName = NormalizeRenamedDocumentName(requestedBaseName, extension);
        if (string.Equals(currentBaseName, newBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var oldPath = Path.GetFullPath(node.FullPath);
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? string.Empty, string.Concat(newBaseName, extension));
        var matchingNodes = EnumerateCadNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, oldPath))
            .Concat(new[] { node }).Distinct().ToArray();
        if (File.Exists(newPath) && !PathsEqual(oldPath, newPath))
        {
            throw new IOException(string.Concat("目标文件已存在：", Path.GetFileName(newPath)));
        }

        if (renamingRoot)
        {
            assemblyModel.ClearSelection2(true);
            PdmDocumentIdentityStore.TryReadLocalMetadata(oldPath, out var previousMetadata);
            RenameRootDocumentAndReferences(node, assemblyModel, newPath);

            foreach (var matching in matchingNodes)
            {
                matching.FullPath = newPath;
                matching.SourceReferencePath = newPath;
                matching.FileName = Path.GetFileName(newPath);
                matching.DisplayName = newBaseName;
                matching.DrawingNumber = newBaseName;
                matching.IsModifiedInSolidWorks = false;
                matching.IsRenamePendingSave = false;
                matching.Status = CadReferenceStatus.Normal;
                matching.CurrentRevision = "本地修改";
                matching.WorkState = CadWorkState.PendingCheckIn;
            }
            if (node.DocumentId.HasValue)
            {
                if (previousMetadata?.DocumentId == node.DocumentId && previousMetadata.VersionId.HasValue)
                    PdmDocumentIdentityStore.TryWriteControlledVersion(newPath, node.DocumentId.Value, previousMetadata.ProjectId,
                        previousMetadata.VersionId.Value, previousMetadata.Revision, previousMetadata.Sha256, previousMetadata.FileLength ?? new FileInfo(newPath).Length);
                else PdmDocumentIdentityStore.TryWrite(newPath, node.DocumentId.Value, currentProjectId);
            }
            if (currentProjectId.HasValue)
            {
                RememberExplicitProjectPath(newPath, currentProjectId.Value);
            }
            currentDocumentIdentity = newPath;
            InvalidateTreeCacheForPath(oldPath);
            InvalidateTreeCacheForPath(newPath);
            taskPaneControl.SetTree(currentTree);
            ScheduleTreeRefresh();
            LogOperation(string.Concat("Root document rename saved old=", oldPath, " new=", newPath, " document=", node.DocumentId));
            return;
        }

        if (!TrySelectComponentForNode(assemblyModel, node, out var renameComponent))
        {
            throw new InvalidOperationException("SolidWorks无法选中该零部件，请刷新设计树后重试。");
        }
        EnsureRenameComponentLoaded(assemblyModel, node, renameComponent);
        if (!TrySelectComponentForNode(assemblyModel, node, out _))
            throw new InvalidOperationException("加载模型后无法重新选中零部件，请刷新设计树后重试：" + node.FileName);

        var renameStatus = (swRenameDocumentError_e)assemblyModel.Extension.RenameDocument(newBaseName);
        assemblyModel.ClearSelection2(true);
        if (renameStatus == swRenameDocumentError_e.swRenameDocumentError_PatternedComponent)
        {
            var components = ((assemblyModel as IAssemblyDoc)?.GetComponents(false) as object[] ?? Array.Empty<object>())
                .OfType<IComponent2>();
            foreach (var component in components)
            {
                if (!PathsEqual(component.GetPathName(), oldPath)) continue;
                var patterned = false;
                for (var ancestor = component; ancestor != null; ancestor = ancestor.GetParent())
                {
                    if (ancestor.IsPatternInstance())
                    {
                        patterned = true;
                        break;
                    }
                }
                if (patterned) continue;
                assemblyModel.ClearSelection2(true);
                var selectData = (assemblyModel.SelectionManager as ISelectionMgr)?.CreateSelectData();
                if (!component.Select4(false, selectData, false)) continue;
                EnsureRenameComponentLoaded(assemblyModel, node, component);
                if (!component.Select4(false, selectData, false)) continue;
                renameStatus = (swRenameDocumentError_e)assemblyModel.Extension.RenameDocument(newBaseName);
                assemblyModel.ClearSelection2(true);
                if (renameStatus != swRenameDocumentError_e.swRenameDocumentError_PatternedComponent) break;
            }
        }
        if (renameStatus != swRenameDocumentError_e.swRenameDocumentError_None)
        {
            LogOperation("Native component rename failed path=" + oldPath + " status=" + renameStatus + " code=" + (int)renameStatus);
            throw new InvalidOperationException(string.Concat(RenameDocumentErrorText(renameStatus), " 错误码：", (int)renameStatus, "。"));
        }

        foreach (var matchingNode in matchingNodes)
        {
            matchingNode.FullPath = newPath;
            matchingNode.FileName = Path.GetFileName(newPath);
            matchingNode.DisplayName = newBaseName;
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
        LogOperation(string.Concat("Document rename staged old=", oldPath, " new=", newPath, " document=", node.DocumentId));
    }

    private void ApplyDocumentRenameWithLinkedDrawing(
        CadTreeNode node,
        string requestedBaseName,
        IModelDoc2 assemblyModel,
        bool renamingRoot)
    {
        EnsureLocalRenameFileWritable(node, true);
        var linkedDrawing = BuildLinkedDrawingRenamePlan(node, requestedBaseName, true);
        ApplyControlledDocumentRename(node, requestedBaseName, assemblyModel, renamingRoot);
        if (linkedDrawing != null)
        {
            ApplyLinkedDrawingRename(linkedDrawing);
        }
    }

    private void EnsureRenameComponentLoaded(IModelDoc2 assemblyModel, CadTreeNode node, IComponent2 component)
    {
        try
        {
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null)
            {
                var chain = new Stack<IComponent2>();
                for (var current = component; current != null; current = current.GetParent()) chain.Push(current);
                while (chain.Count > 0)
                {
                    var current = chain.Pop();
                    var state = current.GetSuppression();
                    if (state == (int)swComponentSuppressionState_e.swComponentSuppressed)
                        throw new InvalidOperationException("组件或其上级处于压缩状态，尚未执行重命名。请先还原该组件后重试：" + node.FileName);
                    if (state == (int)swComponentSuppressionState_e.swComponentLightweight
                        || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                    {
                        var result = current.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        LogOperation("Rename resolve component=" + current.Name2 + " result=" + result);
                    }
                }
                model = component.GetModelDoc2() as IModelDoc2;
                if (model == null)
                {
                    if (node.Status == CadReferenceStatus.Virtual)
                        throw new InvalidOperationException("嵌入模型尚未在父装配体中加载。请在父装配体内还原该组件后重试；无需独立登记或保存该组件：" + node.FileName);
                    // Property reading may have closed a temporary model. Keep this native model
                    // loaded until the parent saves the pending rename and its references.
                    OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration ?? string.Empty);
                    model = component.GetModelDoc2() as IModelDoc2;
                    if (model == null)
                        throw new InvalidOperationException("已读取文件，但装配实例的模型仍未加载，尚未执行重命名。请还原组件、检查引用路径并刷新设计树：" + node.FileName);
                }
            }
            if (!PathsEqual(model.GetPathName(), node.FullPath))
                throw new InvalidOperationException("组件实际加载了其他路径的同名文件，已停止重命名。请核对引用路径：" + node.FileName);
            EnsureDocumentEditable(model, node.FullPath);
        }
        finally
        {
            if (!PathsEqual((application.ActiveDoc as IModelDoc2)?.GetPathName(), assemblyModel.GetPathName()))
            {
                var errors = 0;
                var active = application.ActivateDoc3(assemblyModel.GetTitle(), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
                if (active == null || !PathsEqual(active.GetPathName(), assemblyModel.GetPathName()))
                    throw new InvalidOperationException("模型加载后未能恢复主装配体，尚未执行重命名。请激活主装配体后重试。");
            }
        }
        LogOperation("Rename model ready path=" + node.FullPath + " configuration=" + node.Configuration);
    }

    private LinkedDrawingRenamePlan BuildLinkedDrawingRenamePlan(
        CadTreeNode model,
        string requestedBaseName,
        bool prepareLocalFiles = false)
    {
        if (model == null || model.Kind != CadDocumentKind.Part && model.Kind != CadDocumentKind.Assembly)
        {
            return null;
        }

        var modelPath = Path.GetFullPath(model.FullPath);
        var modelDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        var modelBaseName = Path.GetFileNameWithoutExtension(modelPath);
        var drawings = EnumerateCadNodes(currentTree)
            .Where(candidate => candidate.Kind == CadDocumentKind.Drawing)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.FullPath))
            .GroupBy(candidate => Path.GetFullPath(candidate.FullPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var explicitMatches = model.DocumentId.HasValue
            ? drawings.Where(drawing => drawing.RelatedModelDocumentId == model.DocumentId).ToArray()
            : Array.Empty<CadTreeNode>();
        // Multiple drawings can legitimately reference one model. Only its unique
        // same-named drawing follows the filename; all root references are migrated.
        CadTreeNode drawingNode = explicitMatches.Length == 1 ? explicitMatches[0]
            : explicitMatches.FirstOrDefault(drawing => PathsEqual(drawing.FullPath,
                Path.Combine(modelDirectory, modelBaseName + ".SLDDRW")));
        if (drawingNode == null)
        {
            var sameNameMatches = drawings
                .Where(drawing => !drawing.RelatedModelDocumentId.HasValue)
                .Where(drawing => string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(drawing.FullPath)) ?? string.Empty,
                    modelDirectory,
                    StringComparison.OrdinalIgnoreCase))
                .Where(drawing => string.Equals(
                    Path.GetFileNameWithoutExtension(drawing.FullPath),
                    modelBaseName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (sameNameMatches.Length > 1)
            {
                throw new InvalidOperationException("发现多个同名工程图，无法确定唯一联动对象。");
            }
            drawingNode = sameNameMatches.FirstOrDefault();
        }

        var drawingPath = drawingNode?.FullPath;
        if (string.IsNullOrWhiteSpace(drawingPath))
        {
            var sameNameDrawingPath = Path.Combine(modelDirectory, string.Concat(modelBaseName, ".SLDDRW"));
            if (!File.Exists(sameNameDrawingPath))
            {
                return null;
            }
            drawingPath = sameNameDrawingPath;
        }

        drawingPath = Path.GetFullPath(drawingPath);
        if (!File.Exists(drawingPath))
        {
            throw new FileNotFoundException("关联工程图的本地文件不存在。", drawingPath);
        }
        EnsureDrawingCanBeChanged(drawingPath, true, prepareLocalFiles);
        if (FindLoadedDocument(drawingPath) != null)
        {
            throw new InvalidOperationException("关联工程图当前已打开。请先保存并关闭工程图，再执行模型联动改名。");
        }

        var newBaseName = NormalizeRenamedDocumentName(requestedBaseName, ".SLDDRW");
        var newDrawingPath = Path.Combine(Path.GetDirectoryName(drawingPath) ?? string.Empty, string.Concat(newBaseName, ".SLDDRW"));
        if (!PathsEqual(drawingPath, newDrawingPath) && File.Exists(newDrawingPath))
        {
            throw new IOException(string.Concat("关联工程图目标文件已存在：", Path.GetFileName(newDrawingPath)));
        }

        var newModelPath = Path.Combine(modelDirectory, string.Concat(
            NormalizeRenamedDocumentName(requestedBaseName, Path.GetExtension(modelPath)),
            Path.GetExtension(modelPath)));
        return new LinkedDrawingRenamePlan(drawingNode, drawingPath, newDrawingPath, modelPath, newModelPath);
    }

    private void ApplyLinkedDrawingRename(LinkedDrawingRenamePlan plan)
    {
        if (plan == null)
        {
            return;
        }

        File.Move(plan.OldDrawingPath, plan.NewDrawingPath);
        try
        {
            var references = ReadRenameReferences(plan.NewDrawingPath, false);
            if ((references.Any(path => PathsEqual(path, plan.OldModelPath)) || !references.Any(path => PathsEqual(path, plan.NewModelPath)))
                && !application.ReplaceReferencedDocument(
                    plan.NewDrawingPath,
                    plan.OldModelPath,
                    plan.NewModelPath))
            {
                throw new InvalidOperationException("SolidWorks未能更新工程图中的模型引用。");
            }
        }
        catch
        {
            if (File.Exists(plan.NewDrawingPath) && !File.Exists(plan.OldDrawingPath))
            {
                File.Move(plan.NewDrawingPath, plan.OldDrawingPath);
            }
            throw;
        }

        if (plan.DrawingNode != null)
        {
            foreach (var matchingNode in EnumerateCadNodes(currentTree)
                .Where(candidate => PathsEqual(candidate.FullPath, plan.OldDrawingPath)))
            {
                matchingNode.FullPath = plan.NewDrawingPath;
                matchingNode.FileName = Path.GetFileName(plan.NewDrawingPath);
                matchingNode.DisplayName = Path.GetFileNameWithoutExtension(plan.NewDrawingPath) + " 工程图";
                matchingNode.DrawingNumber = Path.GetFileNameWithoutExtension(plan.NewDrawingPath);
                matchingNode.IsModifiedInSolidWorks = false;
                matchingNode.IsRenamePendingSave = false;
                matchingNode.Status = CadReferenceStatus.Normal;
                matchingNode.CurrentRevision = "本地修改";
                matchingNode.WorkState = CadWorkState.PendingCheckIn;
                if (matchingNode.DocumentId.HasValue)
                {
                    PdmDocumentIdentityStore.TryWrite(
                        plan.NewDrawingPath,
                        matchingNode.DocumentId.Value,
                        currentProjectId);
                }
            }
        }
        if (currentProjectId.HasValue)
        {
            RememberExplicitProjectPath(plan.NewDrawingPath, currentProjectId.Value);
        }
        LogOperation(string.Concat(
            "Linked drawing rename completed old=", plan.OldDrawingPath,
            " new=", plan.NewDrawingPath,
            " model=", plan.NewModelPath));
    }

    private void EnsureLocalRenameFileWritable(CadTreeNode node, bool prepareForSolidWorksRename = false)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            throw new FileNotFoundException("本地图档不存在。", node?.FullPath);
        }
        var loaded = FindLoadedDocument(node.FullPath);
        if (!node.DocumentId.HasValue)
        {
            if (prepareForSolidWorksRename)
            {
                PrepareUnregisteredLocalFileForSolidWorksRename(node.FullPath, loaded);
            }
            return;
        }
        if ((File.GetAttributes(node.FullPath) & FileAttributes.ReadOnly) != 0)
        {
            throw new InvalidOperationException("图档仍为只读状态，请重新获取编辑权限后重试。");
        }
        if (loaded?.IsOpenedReadOnly() == true)
        {
            throw new InvalidOperationException("图档当前以只读方式打开，请重新获取编辑权限后重试。");
        }
    }

    private Task<ClientBootstrapConfiguration> LoadConfiguredBootstrapAsync(
        PluginSettings settings,
        CancellationToken cancellationToken)
    {
        return ClientBootstrapLoader.LoadAsync(cancellationToken);
    }

    private void SetInitialUpdateSnapshot()
    {
        var installedVersion = ClientPackageUpdater.GetInstalledVersion(addinDirectory);
        var snapshot = new PluginUpdateSnapshot
        {
            InstalledVersion = installedVersion,
            Status = pluginSettings.AutomaticUpdatesEnabled ? "等待自动检查更新" : "自动更新已关闭"
        };
        if (ClientPackageUpdater.TryGetPendingUpdate("solidworks-addin", out var pendingVersion, out var pendingError))
        {
            snapshot.AvailableVersion = pendingVersion;
            snapshot.Status = string.IsNullOrWhiteSpace(pendingError)
                ? "更新包已下载，请点击安装更新"
                : string.Concat("更新失败：", pendingError);
            snapshot.UpdateAvailable = true;
            snapshot.ReadyToInstall = ClientPackageUpdater.PendingUpdateHasFileHashes("solidworks-addin");
        }
        SetClientUpdateSnapshot(snapshot);
    }

    private PluginUpdateSnapshot GetClientUpdateSnapshot()
    {
        lock (clientUpdateStatusSync) return clientUpdateSnapshot.Clone();
    }

    private void SetClientUpdateSnapshot(PluginUpdateSnapshot snapshot)
    {
        lock (clientUpdateStatusSync) clientUpdateSnapshot = snapshot?.Clone() ?? new PluginUpdateSnapshot();
    }

    private void ChangeClientUpdateSnapshot(Action<PluginUpdateSnapshot> change)
    {
        lock (clientUpdateStatusSync)
        {
            var snapshot = clientUpdateSnapshot.Clone();
            change(snapshot);
            clientUpdateSnapshot = snapshot;
        }
    }

    private async Task<PluginUpdateSnapshot> CheckClientUpdateAsync(
        string serverAddress,
        bool install,
        CancellationToken cancellationToken,
        bool launchInstaller = false)
    {
        await clientUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.Busy = true;
                snapshot.ProgressPercentage = null;
                snapshot.Status = "正在检查更新…";
            });

            var installedVersion = ClientPackageUpdater.GetInstalledVersion(addinDirectory);
            var checkedAt = DateTimeOffset.Now;

            var repairFailedUpdate = false;
            if (ClientPackageUpdater.TryGetPendingUpdate("solidworks-addin", out var pendingVersion, out var pendingError))
            {
                repairFailedUpdate = !string.IsNullOrWhiteSpace(pendingError)
                    || !ClientPackageUpdater.PendingUpdateHasFileHashes("solidworks-addin");
                if (!repairFailedUpdate)
                {
                    if (launchInstaller) LaunchVisiblePluginInstaller();
                    var pendingSnapshot = new PluginUpdateSnapshot
                    {
                        InstalledVersion = installedVersion,
                        AvailableVersion = pendingVersion,
                        LastCheckedAt = checkedAt,
                        Status = launchInstaller ? "安装窗口已打开，请保存图纸并关闭 SolidWorks，在安装窗口查看结果" : "更新包已下载，请点击安装更新",
                        UpdateAvailable = !launchInstaller,
                        ReadyToInstall = true,
                        ProgressPercentage = 100
                    };
                    SetClientUpdateSnapshot(pendingSnapshot);
                    return pendingSnapshot.Clone();
                }
            }

            // 已下载并校验的包可以离线安装，不依赖再次连接更新服务器。
            var bootstrap = string.IsNullOrWhiteSpace(serverAddress)
                ? await ClientBootstrapLoader.LoadAsync(cancellationToken, false).ConfigureAwait(false)
                : await ClientBootstrapLoader.LoadAsync(
                    PluginSettingsStore.BuildBootstrapUrl(serverAddress), cancellationToken, false).ConfigureAwait(false);
            var availableVersion = bootstrap?.SolidWorksAddin?.Version?.Trim() ?? string.Empty;
            var updateAvailable = repairFailedUpdate
                || ClientPackageUpdater.IsUpdateAvailable(installedVersion, availableVersion);
            if (!updateAvailable)
            {
                var serverVersionIsOlder = ClientPackageUpdater.IsUpdateAvailable(availableVersion, installedVersion);
                var currentSnapshot = new PluginUpdateSnapshot
                {
                    InstalledVersion = installedVersion,
                    AvailableVersion = availableVersion,
                    LastCheckedAt = checkedAt,
                    Status = string.IsNullOrWhiteSpace(availableVersion)
                        ? "服务器未发布插件版本"
                        : serverVersionIsOlder
                            ? "服务器发布版本低于当前版本，已阻止降级"
                            : "已是最新版本"
                };
                SetClientUpdateSnapshot(currentSnapshot);
                return currentSnapshot.Clone();
            }

            if (!install)
            {
                var availableSnapshot = new PluginUpdateSnapshot
                {
                    InstalledVersion = installedVersion,
                    AvailableVersion = availableVersion,
                    LastCheckedAt = checkedAt,
                    Status = repairFailedUpdate ? "检测到更新安装不完整，可立即修复" : "发现新版本",
                    UpdateAvailable = true
                };
                SetClientUpdateSnapshot(availableSnapshot);
                return availableSnapshot.Clone();
            }

            ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.InstalledVersion = installedVersion;
                snapshot.AvailableVersion = availableVersion;
                snapshot.LastCheckedAt = checkedAt;
                snapshot.UpdateAvailable = true;
                snapshot.Busy = true;
                snapshot.Status = "正在下载并校验更新包…";
            });
            var progress = new CallbackProgress<ClientUpdateProgress>(value => ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.ProgressPercentage = value.Percentage;
                snapshot.Status = value.Percentage.HasValue
                    ? string.Concat("正在下载更新包：", value.Percentage.Value, "%")
                    : "正在下载更新包…";
            }));
            await ClientPackageUpdater.StageAsync(
                "solidworks-addin",
                bootstrap.SolidWorksAddin,
                addinDirectory,
                cancellationToken,
                progress).ConfigureAwait(false);
            if (launchInstaller) LaunchVisiblePluginInstaller();
            var stagedSnapshot = new PluginUpdateSnapshot
            {
                InstalledVersion = installedVersion,
                AvailableVersion = availableVersion,
                LastCheckedAt = checkedAt,
                ProgressPercentage = 100,
                Status = launchInstaller ? "安装窗口已打开，请保存图纸并关闭 SolidWorks，在安装窗口查看结果" : "更新包已下载，请点击安装更新",
                UpdateAvailable = !launchInstaller,
                ReadyToInstall = true
            };
            SetClientUpdateSnapshot(stagedSnapshot);
            return stagedSnapshot.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string message = "连接更新服务器超时，请检查服务器地址和网络连接后重试。";
            ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.Busy = false;
                snapshot.Status = message;
                snapshot.LastCheckedAt = DateTimeOffset.Now;
            });
            throw new TimeoutException(message);
        }
        catch (OperationCanceledException)
        {
            ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.Busy = false;
                snapshot.Status = "更新检查已取消";
            });
            throw;
        }
        catch (Exception exception)
        {
            ChangeClientUpdateSnapshot(snapshot =>
            {
                snapshot.Busy = false;
                snapshot.Status = string.Concat("更新检查失败：", exception.Message);
                snapshot.LastCheckedAt = DateTimeOffset.Now;
            });
            throw;
        }
        finally
        {
            clientUpdateGate.Release();
        }
    }

    private void LaunchVisiblePluginInstaller()
    {
        if (!ClientPackageUpdater.TryLaunchPendingUpdate("solidworks-addin", Process.GetCurrentProcess().Id, string.Empty, true))
            throw new InvalidOperationException("未能打开安装窗口，可能已有安装进程。请检查更新状态后重试。");
    }

    private async Task<PluginConnectionResult> TestPluginConnectionAsync(string serverAddress)
    {
        var normalizedAddress = PluginSettingsStore.NormalizeServerAddress(serverAddress);
        try
        {
            var bootstrap = await ClientBootstrapLoader.LoadAsync(
                PluginSettingsStore.BuildBootstrapUrl(normalizedAddress),
                lifetime.Token,
                false).ConfigureAwait(false);
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
            using (var response = await client.GetAsync(
                new Uri(new Uri(bootstrap.ApiBaseUrl), "health"),
                lifetime.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
            return new PluginConnectionResult
            {
                Configuration = bootstrap,
                Message = "连接成功，PLM服务正常"
            };
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("连接服务器超时，请检查服务器地址和网络连接后重试。");
        }
    }

    private async Task<PluginConnectionResult> SavePluginSettingsAsync(PluginSettings requestedSettings)
    {
        if (requestedSettings == null) throw new ArgumentNullException(nameof(requestedSettings));
        if (GetClientUpdateSnapshot().Busy)
            throw new InvalidOperationException("插件更新正在进行，请等待完成后再切换服务器。");
        if (Volatile.Read(ref openOperationInProgress) != 0
            || Volatile.Read(ref checkInOperationInProgress) != 0
            || Volatile.Read(ref workspaceOperationInProgress) != 0
            || Volatile.Read(ref automaticDrawingOperationInProgress) != 0)
            throw new InvalidOperationException("当前图档操作尚未完成，请完成后再切换服务器。");

        var normalizedAddress = PluginSettingsStore.NormalizeServerAddress(requestedSettings.ServerAddress);
        var currentServerAddress = string.IsNullOrWhiteSpace(pluginSettings?.ServerAddress)
            ? PluginSettingsStore.ServerAddressFromConfiguration(currentBootstrap)
            : PluginSettingsStore.NormalizeServerAddress(pluginSettings.ServerAddress);
        var addressChanged = !string.Equals(
            normalizedAddress.TrimEnd('/'),
            currentServerAddress.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
        var connection = addressChanged
            ? await TestPluginConnectionAsync(normalizedAddress)
            : new PluginConnectionResult
            {
                Configuration = currentBootstrap,
                Message = "设置已保存并应用"
            };
        if (connection.Configuration == null)
            throw new InvalidOperationException("当前服务器配置尚未加载，请先测试连接。");
        var serverChanged = currentBootstrap == null
            || !string.Equals(
                currentBootstrap.ApiBaseUrl.TrimEnd('/'),
                connection.Configuration.ApiBaseUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        if (serverChanged)
        {
            lock (checkoutDocumentSync)
            {
                if (activeCheckoutDocumentIds.Count > 0)
                    throw new InvalidOperationException("当前服务器仍有本机编辑中的图档，请先提交存档或放弃编辑后再切换服务器。");
            }
        }

        var settings = new PluginSettings
        {
            ServerAddress = normalizedAddress,
            AutomaticUpdatesEnabled = requestedSettings.AutomaticUpdatesEnabled,
            PlannedTaskScrollIntervalSeconds = requestedSettings.PlannedTaskScrollIntervalSeconds,
        };
        PluginSettingsStore.Save(settings);
        pluginSettings = settings;
        taskPaneControl.SetPlannedTaskScrollInterval(settings.PlannedTaskScrollIntervalSeconds);
        currentBootstrap = connection.Configuration;

        if (serverChanged)
        {
            var replacement = new PdmApiClient(connection.Configuration.ApiBaseUrl);
            replacement.BeforeCheckout = FinishPendingCloseBeforeCheckoutAsync;
            replacement.AuthenticationExpired += OnAuthenticationExpired;
            var previous = apiClient;
            if (previous != null) previous.AuthenticationExpired -= OnAuthenticationExpired;
            apiClient = replacement;
            controlledWorkspace = new ControlledWorkspaceManager(replacement);
            authenticatedUsername = string.Empty;
            authenticatedSessionJson = string.Empty;
            availableProjects = Array.Empty<ProjectDto>();
            userDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            currentProjectId = null;
            controlledOpenManifests.Clear();
            lock (metadataCacheSync) projectDocumentsCache.Clear();
            lock (metadataCacheSync) documentVersionsCache.Clear();
            CancelProjectNavigation();
            lock (metadataCacheSync) { navigationVersions.Clear(); navigationVersionStamps.Clear(); navigationRelations.Clear(); }
            taskPaneControl.SetProjects(availableProjects);
            taskPaneControl.SetUserDisplayNames(userDisplayNames);
            taskPaneControl.SetAuthenticatedUser(string.Empty, string.Empty);
            taskPaneControl.SetConnectionState(false, "服务器已切换，请重新登录");
            previous?.Dispose();
            connection.Message = "设置已保存并应用，服务器已切换，请重新登录";
        }
        else
        {
            connection.Message = "设置已保存并应用";
        }

        if (!settings.AutomaticUpdatesEnabled)
        {
            ChangeClientUpdateSnapshot(snapshot =>
            {
                if (!snapshot.Busy) snapshot.Status = "自动更新已关闭，可手动检查更新";
            });
        }
        return connection;
    }

    private static void PrepareUnregisteredLocalFileForSolidWorksRename(string path, IModelDoc2 loadedDocument)
    {
        SetFileReadOnly(path, false);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            throw new IOException(string.Concat(Path.GetFileName(path), "无法解除本地只读属性。"));
        }
        if (loadedDocument?.IsOpenedReadOnly() == true
            && (!loadedDocument.SetReadOnlyState(false) || loadedDocument.IsOpenedReadOnly()))
        {
            throw new IOException(string.Concat(Path.GetFileName(path), "无法切换为SolidWorks可编辑状态。"));
        }
    }

    private IReadOnlyDictionary<Guid, string> ValidateBatchDocumentRenames(
        IReadOnlyList<BatchDocumentRenameRequest> requestedRenames)
    {
        var requests = (requestedRenames ?? Array.Empty<BatchDocumentRenameRequest>())
            .Where(request => request?.Item?.OperationItem?.Node != null)
            .GroupBy(request => request.Item.OperationItem.Node.FullPath ?? request.NodeId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var errors = new Dictionary<Guid, string>();
        if (requests.Length == 0)
        {
            return errors;
        }

        IModelDoc2 activeDocument = null;
        try
        {
            activeDocument = application?.ActiveDoc as IModelDoc2;
        }
        catch
        {
            activeDocument = null;
        }
        if (currentTree == null || activeDocument == null || !PathsEqual(activeDocument.GetPathName(), currentTree.FullPath))
        {
            foreach (var request in requests)
            {
                AddBatchRenameError(errors, request.NodeId, "请先激活当前设计树对应的主图档");
            }
        }
        if (currentTree?.DocumentId.HasValue == true && !IsCheckedOutByCurrentUser(currentTree))
        {
            foreach (var request in requests)
            {
                AddBatchRenameError(errors, request.NodeId, "请先获取当前主图档的编辑权限");
            }
        }
        if (currentTree != null)
        {
            try
            {
                EnsureLocalRenameFileWritable(currentTree);
            }
            catch (Exception exception)
            {
                foreach (var request in requests)
                {
                    AddBatchRenameError(errors, request.NodeId, string.Concat("当前主图档不可写：", exception.Message));
                }
            }
        }

        var targetPaths = new List<KeyValuePair<BatchDocumentRenameRequest, string>>();
        foreach (var request in requests)
        {
            var node = request.Item.OperationItem.Node;
            if (node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly)
            {
                AddBatchRenameError(errors, request.NodeId, "仅支持零件和装配体");
            }
            if (node.IsReadOnlyPreview)
            {
                AddBatchRenameError(errors, request.NodeId, "只读预览不能重命名");
            }
            if (node.DrawingReviewLocked
                || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase))
            {
                AddBatchRenameError(errors, request.NodeId, "图档正在审批、审核锁定或已作废");
            }
            if (node.IsExternalProvenance)
            {
                AddBatchRenameError(errors, request.NodeId, "工作区外的PLM来源副本不能批量重命名；请从所属项目打开受控图档");
            }
            else if (node.ProvenanceProjectId.HasValue && currentProjectId.HasValue && node.ProvenanceProjectId != currentProjectId)
            {
                AddBatchRenameError(errors, request.NodeId, "图档属于其他项目，不能在当前项目批量重命名");
            }
            if (node.DocumentId.HasValue && !IsCheckedOutByCurrentUser(node))
            {
                AddBatchRenameError(errors, request.NodeId, "请先获取该图档的编辑权限");
            }
            if (node.IsRenamePendingSave)
            {
                AddBatchRenameError(errors, request.NodeId, "已有重命名待保存，请先提交存档");
            }
            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                AddBatchRenameError(errors, request.NodeId, "本地图档不存在");
                continue;
            }

            var renamingRoot = currentTree != null && PathsEqual(node.FullPath, currentTree.FullPath);
            if (!renamingRoot && string.IsNullOrWhiteSpace(node.ComponentSelectionName))
            {
                AddBatchRenameError(errors, request.NodeId, "SolidWorks未识别到零部件实例");
            }
            if (!renamingRoot && activeDocument != null && activeDocument.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                AddBatchRenameError(errors, request.NodeId, "子件重命名需要激活主装配体");
            }

            try
            {
                EnsureLocalRenameFileWritable(node);
                var oldPath = Path.GetFullPath(node.FullPath);
                var extension = Path.GetExtension(node.FileName);
                var newBaseName = NormalizeRenamedDocumentName(request.NewBaseName, extension);
                var newPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(oldPath) ?? string.Empty, string.Concat(newBaseName, extension)));
                if (PathsEqual(oldPath, newPath))
                {
                    AddBatchRenameError(errors, request.NodeId, "文件名没有变化");
                }
                else if (File.Exists(newPath))
                {
                    AddBatchRenameError(errors, request.NodeId, string.Concat("目标文件已存在：", Path.GetFileName(newPath)));
                }
                targetPaths.Add(new KeyValuePair<BatchDocumentRenameRequest, string>(request, newPath));

                var linkedDrawing = BuildLinkedDrawingRenamePlan(node, newBaseName);
                if (linkedDrawing != null)
                {
                    targetPaths.Add(new KeyValuePair<BatchDocumentRenameRequest, string>(request, linkedDrawing.NewDrawingPath));
                }
            }
            catch (Exception exception)
            {
                AddBatchRenameError(errors, request.NodeId, exception.Message);
            }
        }

        foreach (var duplicate in targetPaths
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            foreach (var pair in duplicate)
            {
                AddBatchRenameError(errors, pair.Key.NodeId, string.Concat("批量结果重名：", Path.GetFileName(pair.Value)));
            }
        }
        return errors;
    }

    private IReadOnlyDictionary<Guid, string> ExecuteBatchDocumentRenames(
        IReadOnlyList<BatchDocumentRenameRequest> requestedRenames)
    {
        var requests = (requestedRenames ?? Array.Empty<BatchDocumentRenameRequest>())
            .Where(request => request?.Item?.OperationItem?.Node != null)
            .GroupBy(request => request.Item.OperationItem.Node.FullPath ?? request.NodeId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(request => request.Item.OperationItem.Depth)
            .ThenBy(request => request.Item.FileName, StringComparer.CurrentCulture)
            .ToArray();
        var validationErrors = ValidateBatchDocumentRenames(requests);
        if (validationErrors.Count > 0)
        {
            return requests.ToDictionary(
                request => request.NodeId,
                request => validationErrors.TryGetValue(request.NodeId, out var error)
                    ? string.Concat("不可执行：", error)
                    : "未执行：其他图档检查未通过");
        }
        if (!TryBeginWorkspaceOperation("正在批量重命名图档"))
        {
            return requests.ToDictionary(request => request.NodeId, _ => "未执行：已有工作文件操作正在进行");
        }

        var statuses = requests.ToDictionary(request => request.NodeId, _ => "未处理");
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            taskPaneControl.UseWaitCursor = true;
            var activeDocument = application?.ActiveDoc as IModelDoc2
                ?? throw new InvalidOperationException("请先激活当前设计树对应的主图档。");
            var loadFailures = new Dictionary<Guid, string>();
            var checkedModels = 0;
            foreach (var request in requests)
            {
                var node = request.Item.OperationItem.Node;
                if (PathsEqual(node.FullPath, currentTree?.FullPath)) continue;
                try
                {
                    taskPaneControl.SetWorkspaceOperationProgress("重命名前检查：正在加载 " + node.FileName, checkedModels++, requests.Length);
                    if (!TrySelectComponentForNode(activeDocument, node, out var component))
                        throw new InvalidOperationException("无法选中装配实例，请刷新设计树后重试。");
                    EnsureRenameComponentLoaded(activeDocument, node, component);
                }
                catch (Exception error)
                {
                    loadFailures[request.NodeId] = "不可执行：" + RenamePreparationError(error);
                    LogDiagnostic("Batch rename load preflight." + node.FileName, error);
                }
            }
            if (loadFailures.Count > 0)
                return requests.ToDictionary(request => request.NodeId, request => loadFailures.TryGetValue(request.NodeId, out var failure)
                    ? failure : "未执行：其他图档的模型加载检查未通过，本批次尚未重命名");
            for (var index = 0; index < requests.Length; index++)
            {
                var request = requests[index];
                var node = request.Item.OperationItem.Node;
                taskPaneControl.SetWorkspaceOperationProgress(
                    string.Concat("重命名图档", System.Environment.NewLine, node.FileName),
                    index,
                    requests.Length);
                try
                {
                    var renamingRoot = currentTree != null && PathsEqual(node.FullPath, currentTree.FullPath);
                    ApplyDocumentRenameWithLinkedDrawing(node, request.NewBaseName, activeDocument, renamingRoot);
                    statuses[request.NodeId] = "已完成";
                }
                catch (Exception exception)
                {
                    statuses[request.NodeId] = string.Concat("失败：", RenamePreparationError(exception));
                    LogDiagnostic(string.Concat("ExecuteBatchDocumentRenames.", node.FileName), exception);
                    for (var remaining = index + 1; remaining < requests.Length; remaining++)
                    {
                        statuses[requests[remaining].NodeId] = string.Concat("未执行：因 ", node.FileName, " 改名失败而停止");
                    }
                    break;
                }
            }
            var completed = statuses.Count(pair => string.Equals(pair.Value, "已完成", StringComparison.Ordinal));
            taskPaneControl.SetWorkspaceOperationProgress("批量重命名结束", completed, requests.Length);
            taskPaneControl.SetTree(currentTree);
            LogOperation(string.Concat("Batch document rename completed=", completed, "/", requests.Length));
            return statuses;
        }
        finally
        {
            taskPaneControl.UseWaitCursor = false;
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }
    }

    private static void AddBatchRenameError(
        IDictionary<Guid, string> errors,
        Guid nodeId,
        string message)
    {
        var normalized = message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)) return;
        if (errors.TryGetValue(nodeId, out var existing) && !string.IsNullOrWhiteSpace(existing)) return;
        errors[nodeId] = normalized;
    }

    private static string RenameDocumentErrorText(swRenameDocumentError_e error)
    {
        switch (error)
        {
            case swRenameDocumentError_e.swRenameDocumentError_NoModelLoaded:
                return "零部件的模型未加载，请还原组件并刷新设计树后重试。";
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
                return "布线零部件不支持直接重命名。";
            case swRenameDocumentError_e.swRenameDocumentError_ToolboxComponent:
                return "Toolbox零部件不支持直接重命名。";
            case swRenameDocumentError_e.swRenameDocumentError_PatternedComponent:
                return "选中的是阵列实例，且未找到可重命名的原始实例。";
            case swRenameDocumentError_e.swRenameDocumentError_VirtualComponent:
            case swRenameDocumentError_e.swRenameDocumentError_InvalidVirtualComponent:
                return "该零部件类型不支持直接重命名。";
            case swRenameDocumentError_e.swRenameDocumentError_NotAllowedWithPDM:
                return "SolidWorks拒绝了本次PLM环境下的重命名操作。";
            default:
                return "SolidWorks未能重命名该组件，请检查组件加载状态、文件权限和目标名称。";
        }
    }

    private static string RenamePreparationError(Exception error) =>
        error is InvalidOperationException && error.Message.Any(character => character >= '\u4e00' && character <= '\u9fff')
            ? error.Message : "模型加载检查失败，请检查引用路径、文件权限及组件状态后重试；技术详情已记录到诊断日志。";

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
            if (!existed) Directory.CreateDirectory(Path.GetDirectoryName(drawingPath));
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
                    : string.Concat("该工程图正在由", ResolveUserDisplayName(drawingNode.CheckedOutBy), "编辑。"));
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
            throw new InvalidOperationException("历史版本不能直接生成当前工程图，请先打开需要出图的当前模型版本。");
        }
        if (string.IsNullOrWhiteSpace(source.FullPath) || !File.Exists(source.FullPath))
        {
            throw new FileNotFoundException("本地三维模型不存在，不能生成工程图。");
        }
    }

    private IModelDoc2 EnsureAutomaticDrawingSourceLoaded(CadTreeNode source)
    {
        var loaded = FindLoadedDocument(source.FullPath);
        if (loaded != null)
        {
            return loaded;
        }

        var errors = 0;
        var warnings = 0;
        var documentType = source.Kind == CadDocumentKind.Assembly
            ? (int)swDocumentTypes_e.swDocASSEMBLY
            : (int)swDocumentTypes_e.swDocPART;
        LogOperation(string.Concat("Automatic drawing source OpenDoc6 start path=", source.FullPath));
        loaded = application.OpenDoc6(
            source.FullPath,
            documentType,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            source.Configuration ?? string.Empty,
            ref errors,
            ref warnings);
        LogOperation(string.Concat(
            "Automatic drawing source OpenDoc6 end path=", source.FullPath,
            " errors=", errors,
            " warnings=", warnings,
            " null=", loaded == null));
        if (loaded == null)
        {
            throw new InvalidOperationException(string.Concat(
                "SolidWorks未能加载三维模型，不能生成工程图。错误码：", errors,
                "，警告码：", warnings));
        }
        return loaded;
    }

    private void CreateAutomaticDrawing(CadTreeNode source, string drawingPath, AutomaticDrawingOptions options)
    {
        var template = ResolveDrawingTemplate(options?.TemplatePath);
        EnsureAutomaticDrawingSourceLoaded(source);
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

            EnsureDrawingSheetEditMode(drawing);
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
        EnsureAutomaticDrawingSourceLoaded(source);
        var drawingModel = OpenOrActivateDocumentOnSolidWorksThread(
            drawingPath,
            (int)swDocumentTypes_e.swDocDRAWING,
            string.Empty);
        var drawing = drawingModel as IDrawingDoc;
        if (drawing == null)
        {
            throw new InvalidOperationException("关联文件不是SolidWorks工程图。");
        }

        var switchedFromTemplateEdit = EnsureDrawingSheetEditMode(drawing);
        ApplyGbDrawingStandard(drawingModel);
        TryResolveDrawingViews(drawing, drawingModel);
        var existingViews = EnumerateDrawingModelViews(drawing).ToList();
        var firstModelView = existingViews.FirstOrDefault();
        if (switchedFromTemplateEdit || firstModelView == null || !HasVisibleDrawingGeometry(existingViews))
        {
            LogOperation(string.Concat(
                "Automatic drawing rebuilding empty views path=", drawingPath,
                " existingViewCount=", existingViews.Count,
                " switchedFromTemplateEdit=", switchedFromTemplateEdit));
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

    private static bool EnsureDrawingSheetEditMode(IDrawingDoc drawing)
    {
        if (drawing == null || drawing.GetEditSheet())
        {
            return false;
        }

        drawing.EditSheet();
        return true;
    }

    private static void TryResolveDrawingViews(IDrawingDoc drawing, IModelDoc2 drawingModel)
    {
        try
        {
            drawing.ResolveOutOfDateLightWeightComponents();
            foreach (var view in EnumerateDrawingModelViews(drawing))
            {
                PrepareAutomaticDrawingView(view);
            }
            drawingModel.ForceRebuild3(false);
        }
        catch (COMException)
        {
            // Some drawing modes do not expose lightweight resolution; geometry checks below remain authoritative.
        }
    }

    private static bool HasVisibleDrawingGeometry(IEnumerable<IView> views)
    {
        foreach (var view in views ?? Enumerable.Empty<IView>())
        {
            try
            {
                var referencedModel = view.GetReferencedModelName() ?? string.Empty;
                var outline = GetDrawingViewOutline(view);
                if (!view.GetVisible()
                    || string.IsNullOrWhiteSpace(referencedModel)
                    || outline.Width <= 0.000001f
                    || outline.Height <= 0.000001f)
                {
                    continue;
                }

                var splinePointCount = 0;
                var polylinePointCount = 0;
                if (view.GetLineCount2(0) > 0
                    || view.GetArcCount() > 0
                    || view.GetEllipseCount() > 0
                    || view.GetParabolaCount() > 0
                    || view.GetSplineCount(out splinePointCount) > 0
                    || view.GetPolyLineCount5(0, out polylinePointCount) > 0)
                {
                    return true;
                }
            }
            catch (COMException)
            {
                // Continue checking the remaining model views.
            }
        }
        return false;
    }

    private static void PrepareAutomaticDrawingView(IView view)
    {
        if (view == null)
        {
            return;
        }

        view.SetLightweightToResolved();
        view.SetVisible(true, true);
        view.SetDisplayMode3(
            false,
            (int)swDisplayMode_e.swHIDDEN,
            false,
            true);
        view.UpdateViewDisplayGeometry();
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

    private static string ReadResolvedCustomProperty(IModelDoc2 model, string configuration, string propertyName)
    {
        if (model?.Extension == null || string.IsNullOrWhiteSpace(propertyName)) return string.Empty;
        foreach (var scope in new[] { string.Empty, configuration?.Trim() ?? string.Empty }.Distinct(StringComparer.Ordinal))
        {
            var manager = model.Extension.CustomPropertyManager[scope];
            var raw = string.Empty;
            var resolved = string.Empty;
            var wasResolved = false;
            var linked = false;
            manager.Get6(propertyName.Trim(), false, out raw, out resolved, out wasResolved, out linked);
            var value = (wasResolved ? resolved : raw)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
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

        var primaryView = CreateAutomaticDrawingModelView(
            drawing,
            sourcePath,
            new[] { "*Front", "*前视", "*前视图", "*正视图" },
            sheetWidth: width * 0.30d,
            sheetHeight: height * 0.66d,
            "主视图");
        var topView = CreateAutomaticDrawingModelView(
            drawing,
            sourcePath,
            new[] { "*Top", "*上视", "*俯视", "*俯视图" },
            sheetWidth: width * 0.30d,
            sheetHeight: height * 0.30d,
            "俯视图");
        var sideView = CreateAutomaticDrawingModelView(
            drawing,
            sourcePath,
            new[] { "*Right", "*右视", "*右视图" },
            sheetWidth: width * 0.67d,
            sheetHeight: height * 0.66d,
            "右视图");
        IView isometricView = null;
        var createdViews = new List<IView> { primaryView, topView, sideView };

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
            isometricView,
            options);

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
            isometricView,
            options);
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
        PrepareAutomaticDrawingView(view);
        return view;
    }

    private static IView CreateAutomaticDrawingModelView(
        IDrawingDoc drawing,
        string sourcePath,
        IReadOnlyList<string> orientations,
        double sheetWidth,
        double sheetHeight,
        string viewName)
    {
        foreach (var orientation in orientations ?? Array.Empty<string>())
        {
            var view = drawing.CreateDrawViewFromModelView3(
                sourcePath,
                orientation,
                sheetWidth,
                sheetHeight,
                0d) as IView;
            if (view != null)
            {
                PrepareAutomaticDrawingView(view);
                return view;
            }
        }
        throw new InvalidOperationException(string.Concat(
            "SolidWorks未能创建", viewName,
            "。请确认零件可正常打开，并检查标准视图是否完整。"));
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
        IView isometricView,
        AutomaticDrawingOptions options)
    {
        var rules = AutomaticDrawingRuleStore.Load();
        var primaryViewMaximumSheetFraction = AutomaticDrawingOptions.NormalizePrimaryViewMaximumSheetFraction(
            options?.PrimaryViewMaximumSheetFraction
                ?? AutomaticDrawingOptions.DefaultPrimaryViewMaximumSheetFraction);
        var frameLeft = sheetWidth * 0.05;
        var frameRight = sheetWidth * 0.95;
        var frameBottom = sheetHeight * 0.18;
        var frameTop = sheetHeight * 0.94;
        var maximumScale = GetMaximumDrawingViewScale(
            frontView,
            sheetWidth * primaryViewMaximumSheetFraction,
            sheetHeight * primaryViewMaximumSheetFraction);
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
        var targetScale = SelectStandardDrawingScale(maximumScale, rules);

        ApplyDrawingSheetScale(sheet, targetScale);
        ApplyAutomaticDrawingViewScales(
            frontView,
            topView,
            sideView,
            isometricView,
            targetScale,
            targetScale,
            targetScale,
            targetScale * rules.IsometricScaleRatio);
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
        var boundaryFactor = 1.0d;
        foreach (var view in new[] { frontView, topView, sideView, isometricView }.Where(view => view != null))
        {
            boundaryFactor = Math.Min(boundaryFactor, GetDrawingViewBoundaryScaleFactor(
                view,
                frameLeft + annotationFrameReserve,
                frameRight - annotationFrameReserve,
                frameBottom + annotationFrameReserve,
                frameTop - annotationFrameReserve));
        }
        boundaryFactor = Math.Min(boundaryFactor, GetVerticalDrawingViewGapScaleFactor(
            frontView, topView, annotationViewGap));
        boundaryFactor = Math.Min(boundaryFactor, GetHorizontalDrawingViewGapScaleFactor(
            frontView, sideView, annotationViewGap));
        boundaryFactor = Math.Min(boundaryFactor, GetVerticalDrawingViewGapScaleFactor(
            sideView, isometricView, annotationViewGap));
        boundaryFactor = Math.Min(boundaryFactor, GetHorizontalDrawingViewGapScaleFactor(
            topView, isometricView, annotationViewGap));
        if (boundaryFactor < 0.999d)
        {
            var reducedScale = SelectStandardDrawingScale(targetScale * boundaryFactor, rules);
            if (reducedScale < targetScale)
            {
                ApplyDrawingSheetScale(sheet, reducedScale);
                ApplyAutomaticDrawingViewScales(
                    frontView,
                    topView,
                    sideView,
                    isometricView,
                    reducedScale,
                    reducedScale,
                    reducedScale,
                    reducedScale * rules.IsometricScaleRatio);
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
            return 1.0d;
        }
        var upperOutline = GetDrawingViewOutline(upperView);
        var lowerOutline = GetDrawingViewOutline(lowerView);
        var upperPosition = GetDrawingViewPosition(upperView);
        var lowerPosition = GetDrawingViewPosition(lowerView);
        var currentExtents = upperPosition.Y - upperOutline.Top
            + lowerOutline.Bottom - lowerPosition.Y;
        var availableExtents = upperPosition.Y - lowerPosition.Y - minimumGap;
        return currentExtents > 0d
            ? Math.Max(0.05d, availableExtents / currentExtents)
            : 1.0d;
    }

    private static double GetHorizontalDrawingViewGapScaleFactor(
        IView leftView,
        IView rightView,
        double minimumGap)
    {
        if (leftView == null || rightView == null)
        {
            return 1.0d;
        }
        var leftOutline = GetDrawingViewOutline(leftView);
        var rightOutline = GetDrawingViewOutline(rightView);
        var leftPosition = GetDrawingViewPosition(leftView);
        var rightPosition = GetDrawingViewPosition(rightView);
        var currentExtents = leftOutline.Right - leftPosition.X
            + rightPosition.X - rightOutline.Left;
        var availableExtents = rightPosition.X - leftPosition.X - minimumGap;
        return currentExtents > 0d
            ? Math.Max(0.05d, availableExtents / currentExtents)
            : 1.0d;
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

    private static void ApplyAutomaticDrawingViewScales(
        IView frontView,
        IView topView,
        IView sideView,
        IView isometricView,
        double frontScale,
        double topScale,
        double sideScale,
        double isometricScale)
    {
        frontView.UseParentScale = false;
        frontView.ScaleDecimal = frontScale;
        if (topView != null)
        {
            topView.UseParentScale = false;
            topView.ScaleDecimal = topScale;
        }
        if (sideView != null)
        {
            sideView.UseParentScale = false;
            sideView.ScaleDecimal = sideScale;
        }
        if (isometricView != null)
        {
            isometricView.UseParentScale = false;
            isometricView.ScaleDecimal = isometricScale;
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

    private void EnsureDrawingCanBeChanged(
        string drawingPath,
        bool allowUnregisteredReadOnly = false,
        bool prepareUnregisteredForSolidWorksRename = false)
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
                : string.Concat("该工程图正在由", ResolveUserDisplayName(drawingNode.CheckedOutBy), "编辑。"));
        }

        var loaded = FindLoadedDocument(drawingPath);
        if (allowUnregisteredReadOnly && drawingNode?.DocumentId.HasValue != true)
        {
            if (prepareUnregisteredForSolidWorksRename)
            {
                PrepareUnregisteredLocalFileForSolidWorksRename(drawingPath, loaded);
            }
            return;
        }

        if ((File.GetAttributes(drawingPath) & FileAttributes.ReadOnly) != 0)
        {
            throw new InvalidOperationException(drawingNode?.DocumentId.HasValue == true
                ? "关联工程图为只读文件。请先获取工程图编辑权限。"
                : "未入库的关联工程图为本地只读文件，不能联动改名。");
        }

        if (loaded?.IsOpenedReadOnly() == true)
        {
            throw new InvalidOperationException(drawingNode?.DocumentId.HasValue == true
                ? "关联工程图当前以只读方式打开。请关闭后获取编辑权限。"
                : "未入库的关联工程图当前以只读方式打开，不能联动改名。");
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
            if (string.IsNullOrWhiteSpace(drawingRoot.FullPath)) return null;
            referencedPath = Path.Combine(Path.GetDirectoryName(drawingRoot.FullPath) ?? string.Empty, referencedPath);
        }

        if (string.IsNullOrWhiteSpace(referencedPath) || !File.Exists(referencedPath))
        {
            if (string.IsNullOrWhiteSpace(drawingRoot.FullPath)) return null;
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
        target.DrawingVersion = source.DrawingVersion;
        target.PublishedVersion = source.PublishedVersion;
        target.OpenedVersionId = source.OpenedVersionId;
        target.OpenedRevision = source.OpenedRevision;
        target.StoredVersionStateKnown = source.StoredVersionStateKnown;
        target.HasStoredVersion = source.HasStoredVersion;
        target.LatestVersionSha256 = source.LatestVersionSha256;
        target.LatestStoredSha256 = source.LatestStoredSha256;
        target.LatestStructureIndexPending = source.LatestStructureIndexPending;
        target.StoredReferenceWarnings = source.StoredReferenceWarnings;
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
        if (node?.DocumentId.HasValue == true)
        {
            BeginControlledOpen(node, IsCheckedOutByCurrentUser(node) ? ControlledOpenMode.LatestEdit : ControlledOpenMode.LatestReadOnly);
            return;
        }
        if (node == null || string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            ShowError("当前结构引用的本地图档不存在，不能打开。");
            return;
        }

        QueueOpenDocument(node.FullPath, node.Kind, node.Configuration);
    }

    private void OnUpdateLatestRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (!EnsureServerDocument(node)) return;
        BeginControlledOpen(node, IsCheckedOutByCurrentUser(node) ? ControlledOpenMode.LatestEdit : ControlledOpenMode.LatestReadOnly);
    }
    private void OnUpdateAllLatestRequested(object sender, EventArgs eventArgs)
    {
        if (!EnsureServerDocument(currentTree)) return;
        BeginControlledOpen(currentTree, IsCheckedOutByCurrentUser(currentTree) ? ControlledOpenMode.LatestEdit : ControlledOpenMode.LatestReadOnly);
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
                : string.Concat("该图档正在由", ResolveUserDisplayName(node.CheckedOutBy), "编辑，不能更新工作区。"));
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

        BeginControlledOpen(eventArgs.ProjectId ?? Guid.Empty, eventArgs.Node.DocumentId.Value, eventArgs.Mode, eventArgs.VersionId);
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
        => BeginControlledOpenCore(projectId, documentId, mode, versionId, checkoutDocumentId, null);

    private void BeginControlledOpenCore(Guid projectId, Guid documentId, ControlledOpenMode mode,
        Guid? versionId, Guid? checkoutDocumentId, IReadOnlyCollection<Guid> checkoutDocumentIds)
    {
        if (taskPaneControl == null || taskPaneControl.IsDisposed) return;
        var uiContext = GetTaskPaneSynchronizationContext();
        if (!uiContext.IsCurrentThread)
        {
            uiContext.Post(_ => BeginControlledOpenCore(projectId, documentId, mode, versionId, checkoutDocumentId, checkoutDocumentIds), null);
            return;
        }
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM，再打开受控图档。");
            return;
        }
        if (Volatile.Read(ref controlledOpenInProgress) != 0 || Volatile.Read(ref openOperationInProgress) != 0
            || Volatile.Read(ref workspaceOperationInProgress) != 0 || IsSolidWorksCommandActive())
        {
            QueueControlledOpen(projectId, documentId, mode, versionId, checkoutDocumentId, checkoutDocumentIds);
            return;
        }
        Interlocked.Exchange(ref controlledOpenInProgress, 1);
        if (!TryBeginWorkspaceOperation("阶段1/3：正在读取PLM受控清单"))
        {
            Interlocked.Exchange(ref controlledOpenInProgress, 0);
            QueueControlledOpen(projectId, documentId, mode, versionId, checkoutDocumentId, checkoutDocumentIds);
            return;
        }

        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            _ = ExecuteControlledOpenAsync(projectId, documentId, mode, versionId, checkoutDocumentId, checkoutDocumentIds);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private async Task<bool> ExecuteControlledOpenAsync(
        Guid requestedProjectId,
        Guid documentId,
        ControlledOpenMode mode,
        Guid? versionId,
        Guid? checkoutDocumentId,
        IReadOnlyCollection<Guid> checkoutDocumentIds,
        bool reportErrors = true)
    {
        string rootPath = null;
        string openWarning = null;
        Guid? preparedProjectId = null;
        ControlledOpenManifestDto manifest = null;
        var openElapsed = Stopwatch.StartNew();
        controlledWorkspace.PreparationProgress = (message, completed, total) =>
            taskPaneControl?.SetWorkspaceOperationProgress(message, completed, total);
        var newEditCheckouts = new List<Guid>();
        var requestedEditIds = new HashSet<Guid>(checkoutDocumentIds ?? new[] { checkoutDocumentId ?? documentId });
        var editableDocumentIds = new HashSet<Guid>();
        bool openedForEdit = false;
        var requestedOpenDocumentId = documentId;
        controlledOpenFailureMessage = null;
        PlmOpenWindow[] previousWindows = null;
        try
        {
            await apiClient.RefreshDocumentRulesAsync(lifetime.Token);
            var requestsLatestEdit = mode == ControlledOpenMode.LatestEdit;
            var opensPropertyWriteback = mode == ControlledOpenMode.PropertyWriteback;
            var opensLatestWorkingCopy = requestsLatestEdit || opensPropertyWriteback || mode == ControlledOpenMode.LatestReadOnly;
            var releasedOnly = mode == ControlledOpenMode.LatestReleased;
            var specificVersionId = mode == ControlledOpenMode.SpecificReadOnly ? versionId : null;
            LogOperation(string.Concat(
                "ControlledOpen manifest start project=",
                requestedProjectId,
                " document=",
                documentId,
                " mode=",
                mode));
            try
            {
                manifest = await apiClient.CreateControlledOpenManifestAsync(documentId, specificVersionId,
                    releasedOnly, requestsLatestEdit, lifetime.Token);
            }
            catch (InvalidOperationException exception) when (specificVersionId == null && !releasedOnly
                && exception.Message.Contains("引用快照包含同一图档的不同版本"))
            {
                taskPaneControl.UseWaitCursor = false;
                if (TopMostMessageBox.Show(taskPaneControl,
                    exception.Message + "\r\n\r\n是否明确采用最新受控子件打开？确认后请核对结构，并通过整体存档记录本次引用更新。",
                    "确认更新旧装配引用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) throw;
                manifest = await apiClient.CreateControlledOpenManifestAsync(documentId, null, false,
                    requestsLatestEdit, lifetime.Token, true);
            }
            LogOperation(string.Concat(
                "ControlledOpen manifest ready document=",
                documentId,
                " files=",
                manifest.Files?.Count ?? 0,
                " warnings=",
                manifest.Warnings?.Count ?? 0));
            LogOperation("打开清单就绪 elapsedMs=" + openElapsed.ElapsedMilliseconds);
            taskPaneControl.SetWorkspaceOperationProgress(
                string.Concat("阶段2/3：正在准备本地文件（", manifest.Files?.Count ?? 0, "项）"),
                0,
                manifest.Files?.Count ?? 0);
            preparedProjectId = manifest.ProjectId;
            if (requestedProjectId != Guid.Empty && manifest.ProjectId != requestedProjectId)
            {
                LogOperation("本地项目关联已过期，按PLM图档所属项目打开 document=" + documentId
                    + " requestedProject=" + requestedProjectId + " serverProject=" + manifest.ProjectId);
            }
            var opensDrawing = DocumentKindFromPath(manifest.RootRelativePath) == CadDocumentKind.Drawing;
            if ((mode == ControlledOpenMode.LatestReadOnly || requestsLatestEdit && requestedEditIds.SetEquals(new[] { documentId }))
                && await TryOpenDrawingInLoadedContextAsync(manifest, requestsLatestEdit))
                return true;
            if (opensDrawing) EnsureDrawingOpenHasNoConflictingWindows(manifest);
            if (!opensDrawing && (mode == ControlledOpenMode.LatestReadOnly || mode == ControlledOpenMode.LatestEdit))
                await RestoreCurrentDiscardedEditsForOpenAsync(manifest);
            if (opensLatestWorkingCopy)
            {
                if (requestsLatestEdit && !opensDrawing)
                {
                    var owner = FindLoadedEditContext(manifest);
                    if (owner.Value != null)
                    {
                        var serverOwner = await apiClient.CreateControlledOpenManifestAsync(owner.Value.RootDocumentId,
                            null, false, false, lifetime.Token);
                        manifest = MergeEditContext(serverOwner, manifest);
                        LogOperation("编辑上下文从PLM重新读取，不使用旧本地副本；上下文=" + serverOwner.RootRelativePath
                            + "；编辑目标=" + (checkoutDocumentId ?? requestedOpenDocumentId));
                    }
                }
                // Opening latest prepares a reusable working context without acquiring
                // or restoring any edit permissions, including this session's leases.
                IReadOnlyList<DocumentDto> documents = mode == ControlledOpenMode.LatestReadOnly
                    ? Array.Empty<DocumentDto>()
                    : await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
                foreach (var id in requestedEditIds.Where(id => !manifest.Files.Any(file => file.DocumentId == id)).ToArray())
                {
                    var additional = await apiClient.CreateControlledOpenManifestAsync(id, null, false, requestsLatestEdit, lifetime.Token);
                    if (additional.ProjectId != manifest.ProjectId) throw new InvalidOperationException("所选图档属于不同项目，请分别获取。");
                    manifest = MergeEditContext(manifest, additional);
                }
                var manifestDocumentIds = new HashSet<Guid>(manifest.Files.Select(file => file.DocumentId));
                editableDocumentIds = new HashSet<Guid>(documents
                    .Where(item => manifestDocumentIds.Contains(item.Id)
                        && string.Equals(item.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                        && item.CheckoutSessionId == checkoutSessionId)
                    .Select(item => item.Id));
                foreach (var document in documents) TrackCheckoutDocument(document);
                rootPath = await controlledWorkspace.PreparePlmWorkingCopyAsync(manifest, editableDocumentIds, lifetime.Token);
                if (requestsLatestEdit)
                {
                    foreach (var editTargetDocumentId in requestedEditIds)
                    {
                        var previous = documents.FirstOrDefault(item => item.Id == editTargetDocumentId);
                        var checkoutCreated = previous == null || string.IsNullOrWhiteSpace(previous.CheckedOutBy);
                        plmAuthoritativeCheckouts.Add(editTargetDocumentId);
                        try
                        {
                            var checkedOut = await apiClient.CheckoutAsync(editTargetDocumentId, checkoutSessionId, checkoutMachineName, lifetime.Token);
                            TrackCheckoutDocument(checkedOut);
                            if (checkoutCreated) newEditCheckouts.Add(editTargetDocumentId);
                            editableDocumentIds.Add(editTargetDocumentId);
                        }
                        finally { plmAuthoritativeCheckouts.Remove(editTargetDocumentId); }
                    }
                    rootPath = controlledWorkspace.ApplyWorkingPermissions(manifest, editableDocumentIds, false);
                }
            }
            else
            {
                rootPath = await controlledWorkspace.PrepareReadOnlyAsync(
                    manifest,
                    mode == ControlledOpenMode.SpecificReadOnly,
                    lifetime.Token);
                var snapshotDirectory = Path.GetDirectoryName(rootPath);
                if (manifest.Files.Any(file => FindLoadedDocument(Path.Combine(snapshotDirectory, file.RelativePath ?? file.FileName)) != null))
                    rootPath = await controlledWorkspace.DownloadFreshServerCopyAsync(manifest,
                        mode == ControlledOpenMode.SpecificReadOnly, lifetime.Token);
            }

            if (requestsLatestEdit)
            {
                previousWindows = CapturePlmOpenWindows();
                // Reference preparation can fail on a native sharing lock. Finish
                // it while the original windows and recovery records still exist.
                ControlledReferenceMaterializer.Prepare(application, rootPath, manifest);
            }
            if (IsControlledReadOnlyMode(mode) || requestsLatestEdit || opensPropertyWriteback)
            {
                // Viewing a drawing must never replace its model's open assembly context.
                if (DocumentKindFromPath(manifest.RootRelativePath) == CadDocumentKind.Drawing)
                    EnsureDrawingOpenHasNoConflictingWindows(manifest);
                else
                {
                    openWarning = requestsLatestEdit ? CloseLocalContextsForPlmOpenCore(manifest, true, rootPath)
                        : CloseLocalContextsForPlmOpen(manifest, rootPath);
                    if (!requestsLatestEdit) RetireLocalRecoveryForPlmOpen(manifest);
                }
            }

            RememberControlledOpenManifest(manifest, Path.GetDirectoryName(rootPath));
            if (mode != ControlledOpenMode.SpecificReadOnly && mode != ControlledOpenMode.LatestReleased)
            {
                var receivedAt = DateTime.UtcNow;
                foreach (var file in manifest.Files)
                    if (!PdmDocumentIdentityStore.TryRecordServerMaterialization(
                        ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(rootPath), file), receivedAt))
                        LogOperation("PLM取回已准备，当前副本显示标记稍后重试：" + file.FileName);
            }

            RememberExplicitProjectPath(rootPath, manifest.ProjectId);
            if (requestsLatestEdit)
            {
                using (new ControlledReferenceSearchScope(application, rootPath, manifest))
                {
                    var opened = OpenOrActivateDocumentOnSolidWorksThread(rootPath,
                        ToSolidWorksDocumentType(DocumentKindFromPath(rootPath)), string.Empty,
                        readOnly: !editableDocumentIds.Contains(manifest.RootDocumentId));
                    if (opened == null) throw new IOException("SolidWorks未能打开编辑工作文件，原图和草稿已保留。");
                    ValidateControlledLoadedPaths(opened, rootPath, manifest, application);
                    if (requestedOpenDocumentId != manifest.RootDocumentId)
                    {
                        var requestedPath = controlledWorkspace.GetWorkingFilePath(manifest, requestedOpenDocumentId);
                        var requestedModel = OpenOrActivateDocumentOnSolidWorksThread(requestedPath, ToSolidWorksDocumentType(DocumentKindFromPath(requestedPath)), string.Empty,
                            readOnly: !editableDocumentIds.Contains(requestedOpenDocumentId));
                        if (requestedModel == null)
                            throw new IOException("未能恢复所选图档窗口，原图和草稿已保留。");
                        ValidateControlledLoadedPaths(requestedModel, rootPath, manifest, application);
                    }
                }
                foreach (var id in requestedEditIds)
                    EnsureLoadedDocumentEditable(controlledWorkspace.GetWorkingFilePath(manifest, id));
                openedForEdit = true;
                await AcquireLinkedDrawingPermissionsAsync(manifest.ProjectId,
                    requestedEditIds.ToArray(), Path.GetDirectoryName(rootPath), lifetime.Token);
                RetireLocalRecoveryForPlmOpenCore(manifest, Path.GetDirectoryName(rootPath));
            }
            if (manifest.LocalCompatibilityNotes.Count > 0)
            {
                var note = "已保留存档几何；未归档的旧来源已固定，详情见日志。";
                openWarning = string.IsNullOrWhiteSpace(openWarning) ? note : openWarning + "\r\n" + note;
            }
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
            LogOperation("打开文件准备完成 elapsedMs=" + openElapsed.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            LogDiagnostic("ExecuteControlledOpenAsync", exception);
            controlledOpenFailureMessage = OpenDocumentErrorMessage(exception);
            if (previousWindows != null)
            {
                try { RestorePlmOpenWindows(previousWindows, rootPath); }
                catch (Exception restoreError)
                {
                    LogDiagnostic("PLM获取失败后恢复原窗口", restoreError);
                    controlledOpenFailureMessage += "部分原窗口尚未恢复，原文件及恢复记录已保留，请查看日志中的原路径。";
                    taskPaneControl?.SetCheckoutReminder("获取未完成，部分原窗口尚未恢复；原文件及恢复记录已保留，请查看日志中的原路径。", true);
                }
                controlledOpenManifests.Remove(NormalizeDirectory(Path.GetDirectoryName(rootPath)));
            }
            foreach (var newEditCheckout in newEditCheckouts)
            {
                try
                {
                    var editPath = controlledWorkspace.GetWorkingFilePath(manifest, newEditCheckout);
                    var loaded = FindLoadedDocument(editPath);
                    if (loaded?.GetSaveFlag() != true)
                    {
                        var released = await apiClient.DiscardCheckoutAsync(newEditCheckout, checkoutSessionId, lifetime.Token);
                        TrackCheckoutDocument(released);
                        ProtectLoadedDocument(editPath);
                    }
                    else LogOperation("Controlled open failure retained dirty edit and checkout document=" + newEditCheckout);
                }
                catch (Exception rollbackException) { LogDiagnostic("Controlled open failure retained checkout", rollbackException); }
            }
            if (reportErrors) ShowError(controlledOpenFailureMessage);
            rootPath = null;
        }
        finally
        {
            controlledWorkspace.PreparationProgress = null;
            EndWorkspaceOperation();
            Interlocked.Exchange(ref controlledOpenInProgress, 0);
        }

        if (openedForEdit) ScheduleTreeRefresh();
        if (!string.IsNullOrWhiteSpace(rootPath) && !openedForEdit)
        {
            QueueOpenDocument(
                rootPath,
                DocumentKindFromPath(rootPath),
                string.Empty,
                mode == ControlledOpenMode.PropertyWriteback,
                preparedProjectId,
                IsControlledReadOnlyMode(mode) ? manifest : null);
        }
        if (!string.IsNullOrWhiteSpace(rootPath) && !string.IsNullOrWhiteSpace(openWarning))
            taskPaneControl.SetCheckoutReminder(openWarning, true);
        return !string.IsNullOrWhiteSpace(rootPath);
    }

    private void TryStartControlledOpenRequest()
    {
        if (disconnecting || Volatile.Read(ref controlledOpenInProgress) > 0
            || Volatile.Read(ref openOperationInProgress) > 0 || Volatile.Read(ref workspaceOperationInProgress) > 0 || controlledOpenListener == null)
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
        if (IsSolidWorksCommandActive()) return;

        var request = pendingControlledOpenRequest;
        if (!Enum.TryParse(request.Mode, false, out ControlledOpenMode mode))
        {
            pendingControlledOpenRequest = null;
            ShowError("客户端发送了不支持的SolidWorks打开模式。");
            return;
        }
        if (taskPaneControl != null && !taskPaneControl.IsDisposed)
        {
            GetTaskPaneSynchronizationContext().Post(_ => BeginControlledOpen(request.ProjectId, request.DocumentId, mode, request.VersionId), null);
            pendingControlledOpenRequest = null;
        }
    }

    private TaskPaneSynchronizationContext GetTaskPaneSynchronizationContext() => taskPaneSynchronizationContext
        ?? (taskPaneSynchronizationContext = new TaskPaneSynchronizationContext(taskPaneControl));

    private void QueueOpenDocument(
        string fullPath,
        CadDocumentKind kind,
        string configuration,
        bool openPropertyWriteback = false,
        Guid? propertyWritebackProjectId = null,
        ControlledOpenManifestDto historicalManifest = null)
    {
        var documentType = ToSolidWorksDocumentType(kind);
        if (documentType == (int)swDocumentTypes_e.swDocNONE)
        {
            ShowError("该文件类型不能在SolidWorks中直接打开。 ");
            return;
        }

        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed) return;
        var uiContext = GetTaskPaneSynchronizationContext();
        if (!uiContext.IsCurrentThread)
        {
            uiContext.Post(_ => QueueOpenDocument(fullPath, kind, configuration, openPropertyWriteback,
                propertyWritebackProjectId, historicalManifest), null);
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
            uiContext.Post(_ => OpenDocumentOnSolidWorksThread(
                fullPath,
                documentType,
                configuration ?? string.Empty,
                openPropertyWriteback,
                propertyWritebackProjectId,
                historicalManifest), null);
        }
        catch (Exception exception)
        {
            EndWorkspaceOperation();
            Interlocked.Exchange(ref openOperationInProgress, 0);
            LogDiagnostic("QueueOpenDocument", exception);
            ShowError(string.Concat("打开图档失败：", OpenDocumentErrorMessage(exception)));
        }
    }

    private void OpenDocumentOnSolidWorksThread(
        string fullPath,
        int documentType,
        string configuration,
        bool openPropertyWriteback = false,
        Guid? propertyWritebackProjectId = null,
        ControlledOpenManifestDto historicalManifest = null)
    {
        var openElapsed = Stopwatch.StartNew();
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            if (disconnecting || application == null)
            {
                return;
            }

            IModelDoc2 openedDocument;
            if (historicalManifest != null)
            {
                openedDocument = OpenControlledReadOnlyDocument(fullPath, documentType, configuration, historicalManifest);
            }
            else
            {
                openedDocument = OpenOrActivateDocumentOnSolidWorksThread(fullPath, documentType, configuration);
            }
            if (openedDocument != null && openPropertyWriteback && propertyWritebackProjectId.HasValue)
            {
                pendingPropertyWritebackProjectId = propertyWritebackProjectId;
                Interlocked.Exchange(ref pendingPropertyWritebackDialog, 1);
            }
            if (openedDocument != null && historicalManifest?.LocalCompatibilityNotes.Count > 0)
                taskPaneControl?.SetCheckoutReminder("已保留存档几何；未归档的旧来源已固定，详情见日志。", true);
        }
        catch (Exception exception)
        {
            LogDiagnostic("OpenDocumentOnSolidWorksThread", exception);
            ShowError(string.Concat("打开图档失败：", OpenDocumentErrorMessage(exception)));
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref openOperationInProgress, 0);
            EndWorkspaceOperation();
            LogOperation("SolidWorks打开结束 elapsedMs=" + openElapsed.ElapsedMilliseconds);
            taskPaneControl?.SetTreeLoadingStatus("正在刷新实际设计树…");
            ScheduleTreeRefresh(forceScan: false);
        }
    }

    private static string OpenDocumentErrorMessage(Exception exception)
    {
        if (exception is InvalidOperationException && exception.Message.Any(character => character >= '\u4e00' && character <= '\u9fff')
            && !exception.Message.Contains("Invoke")) return exception.Message;
        if (exception is IOException && exception.Message.Any(character => character >= '\u4e00' && character <= '\u9fff'))
            return exception.Message;
        if (exception is UnauthorizedAccessException)
            return "当前账号不能读取或写入图档所在位置，请检查该目录的访问权限。";
        if (exception is IOException)
            return "图档读写失败：" + NativeArchiveManifest.SourceReadFailureReason(exception) + "。本地文件已保留。";
        return "SolidWorks暂未完成打开图档，请确认UPLM插件已加载，等待窗口就绪后重新打开；技术详情已写入诊断日志。";
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

    private IModelDoc2 FindLoadedBatchDocument(CadTreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var loaded = FindLoadedDocument(node.FullPath);
        if (loaded != null)
        {
            return loaded;
        }

        try
        {
            var rootDocument = FindLoadedDocument(currentTree?.FullPath)
                ?? application?.ActiveDoc as IModelDoc2;
            if (rootDocument?.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return null;
            }

            var components = ((rootDocument as IAssemblyDoc)?.GetComponents(false) as Array)
                ?.OfType<IComponent2>()
                .ToArray()
                ?? Array.Empty<IComponent2>();
            var nameMatches = components.Where(candidate =>
                !string.IsNullOrWhiteSpace(node.ComponentSelectionName)
                && string.Equals(candidate.Name2, node.ComponentSelectionName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var component = !string.IsNullOrWhiteSpace(node.FullPath)
                ? nameMatches.FirstOrDefault(candidate =>
                    {
                        try
                        {
                            return PathsEqual(candidate.GetPathName(), node.FullPath);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                : null;
            if (component == null && nameMatches.Length == 1)
            {
                component = nameMatches[0];
            }
            if (component == null && !string.IsNullOrWhiteSpace(node.FullPath))
            {
                var pathMatches = components.Where(candidate =>
                    {
                        try
                        {
                            return PathsEqual(candidate.GetPathName(), node.FullPath);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .Take(2)
                    .ToArray();
                component = pathMatches.Length == 1 ? pathMatches[0] : null;
            }

            var componentDocument = component?.GetModelDoc2() as IModelDoc2;
            if (componentDocument != null)
            {
                LogOperation(string.Concat(
                    "Batch property reused component memory document name=", node.ComponentSelectionName,
                    " path=", node.FullPath,
                    " modified=", componentDocument.GetSaveFlag()));
            }
            return componentDocument;
        }
        catch (Exception exception)
        {
            LogDiagnostic(string.Concat("FindLoadedBatchDocument.", node.FileName), exception);
            return null;
        }
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

        // Registered selections are resolved on the server before any local-file
        // validation. This also covers missing files, old previews and stale drafts.
        if (requestedNodes.Any(item => item.DocumentId.HasValue))
        {
            if (!TryBeginWorkspaceOperation("正在从PLM获取编辑副本")) { ShowWorkspaceOperationBusy(); return; }
            Interlocked.Increment(ref refreshSuppressionDepth);
            try
            {
                var projectId = currentProjectId ?? (await apiClient.CreateControlledOpenManifestAsync(
                    requestedNodes.First(item => item.DocumentId.HasValue).DocumentId.Value, null, false, false, lifetime.Token)).ProjectId;
                await AcquireExplicitlyFromPlmAsync(requestedNodes.Select(item => new BatchOperationItem(item, 0)).ToArray(), projectId);
            }
            catch (Exception error) { ShowError(error.Message); }
            finally { Interlocked.Decrement(ref refreshSuppressionDepth); EndWorkspaceOperation(); SchedulePermissionStateRefresh(); }
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

        if (IsHistoricalDerivedPath(currentTree?.FullPath))
        {
            await AcquireHistoricalDerivedDocumentsAsync(requestedNodes);
            return;
        }

        if (requestedNodes.Length <= 1 && !PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
            && PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var imported)
            && imported.ProjectId == currentProjectId && imported.VersionId.HasValue)
        {
            var rootId = imported.DocumentId;
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(currentTree?.FullPath, out var importedRoot)
                && importedRoot.ProjectId == currentProjectId && importedRoot.VersionId.HasValue)
                rootId = importedRoot.DocumentId;
            BeginControlledOpen(currentProjectId.Value, rootId, ControlledOpenMode.LatestEdit, null, imported.DocumentId);
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
            var result = await AcquireExplicitlyFromPlmAsync(
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

    private async Task AcquireHistoricalDerivedDocumentsAsync(IReadOnlyCollection<CadTreeNode> requestedNodes)
    {
        var manifest = ApplyControlledOpenMetadata(currentTree);
        if (manifest == null)
        {
            ShowError("未找到当前历史派生工作区的受控清单，请重新打开历史版本后重试。");
            return;
        }
        if (!TryBeginWorkspaceOperation("正在获取历史派生图档编辑权限"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }

        var acquiredDocumentIds = new List<Guid>();
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            RefreshLoadedDocumentModificationFlags(currentTree);
            var filesByDocumentId = manifest.Files.ToDictionary(file => file.DocumentId);
            var targets = requestedNodes
                .Where(node => node?.DocumentId.HasValue == true)
                .GroupBy(node => node.DocumentId.Value)
                .Select(group => group.First())
                .ToArray();
            if (targets.Length == 0 || targets.Any(node => !filesByDocumentId.ContainsKey(node.DocumentId.Value)))
            {
                throw new InvalidOperationException("所选图档不属于当前历史派生快照，不能在此工作区获取权限。");
            }

            var projectDocuments = await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
            var documentsById = projectDocuments.ToDictionary(document => document.Id);
            foreach (var target in targets)
            {
                if (!documentsById.TryGetValue(target.DocumentId.Value, out var document))
                {
                    throw new InvalidOperationException(string.Concat(target.FileName, "不在当前项目或无权访问。"));
                }
                if (!string.IsNullOrWhiteSpace(document.CheckedOutBy)
                    && (document.CheckoutSessionId != checkoutSessionId
                        || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(string.Concat(target.FileName, "正在由", ResolveUserDisplayName(document.CheckedOutBy), "编辑。"));
                }
                if (target.IsModifiedInSolidWorks
                    || target.WorkState == CadWorkState.ModifiedUnsaved
                    || target.WorkState == CadWorkState.PendingCheckIn)
                {
                    throw new InvalidOperationException(string.Concat(target.FileName, "存在未保存修改或待提交内容。"));
                }
            }

            foreach (var target in targets)
            {
                var document = documentsById[target.DocumentId.Value];
                if (document.CheckoutSessionId != checkoutSessionId
                    || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    document = await apiClient.CheckoutAsync(
                        target.DocumentId.Value,
                        checkoutSessionId,
                        checkoutMachineName,
                        lifetime.Token);
                    acquiredDocumentIds.Add(target.DocumentId.Value);
                    documentsById[target.DocumentId.Value] = document;
                }
                TrackCheckoutDocument(document);

                var versions = await apiClient.GetVersionsAsync(target.DocumentId.Value, lifetime.Token);
                var latest = versions.FirstOrDefault()
                    ?? throw new InvalidOperationException(string.Concat(target.FileName, "尚无PLM版本。"));
                var sourceFile = filesByDocumentId[target.DocumentId.Value];
                var source = versions.FirstOrDefault(version => version.Id == sourceFile.VersionId)
                    ?? throw new InvalidOperationException(string.Concat(target.FileName, "的历史快照版本已不存在。"));
                if (source.Id == latest.Id)
                {
                    historicalEditContexts.Remove(target.DocumentId.Value);
                }
                else
                {
                    historicalEditContexts[target.DocumentId.Value] = new HistoricalEditContext(source, latest);
                }
            }

            var editableDocumentIds = documentsById.Values
                .Where(document => document.CheckoutSessionId == checkoutSessionId
                    && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
                .Select(document => document.Id)
                .ToArray();
            controlledWorkspace.ApplyHistoricalDerivedPermissions(manifest, editableDocumentIds);
            foreach (var target in targets)
            {
                var document = documentsById[target.DocumentId.Value];
                ApplyCheckoutDocumentToMatchingInstances(
                    EnumerateCadNodes(currentTree).Where(candidate => candidate.DocumentId == target.DocumentId),
                    document);
                SetFileReadOnly(target.FullPath, false);
                EnsureLoadedDocumentEditable(target.FullPath);
            }
            taskPaneControl.SetTree(currentTree);
            LogOperation(string.Concat(
                "Historical derived child checkout completed files=", targets.Length,
                " newlyAcquired=", acquiredDocumentIds.Count));
        }
        catch (Exception exception)
        {
            foreach (var documentId in acquiredDocumentIds.AsEnumerable().Reverse())
            {
                try
                {
                    var discarded = await apiClient.DiscardCheckoutAsync(documentId, checkoutSessionId, lifetime.Token);
                    TrackCheckoutDocument(discarded);
                    historicalEditContexts.Remove(documentId);
                }
                catch (Exception rollbackException)
                {
                    LogDiagnostic(string.Concat("Historical derived child checkout rollback.", documentId), rollbackException);
                }
            }
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
        if (Volatile.Read(ref workspaceOperationInProgress) != 0)
        {
            ShowWorkspaceOperationBusy();
            return;
        }
        if (initialOperation == PropertyOperationMode.PropertyWriteback && !apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }
        if (currentTree == null) RefreshTree(false);
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
                .Where(item => BatchLocalDocumentRule.CanEdit(
                    FindLoadedBatchDocument(item.Node) != null,
                    !string.IsNullOrWhiteSpace(item.Node.FullPath) && File.Exists(item.Node.FullPath),
                    item.Node.Status == CadReferenceStatus.Virtual,
                    IsSolidWorksTemporaryVirtualComponentPath(item.Node.FullPath)))
                .ToArray();
            if (operationItems.Length == 0)
            {
                ShowError("当前结构中没有可编辑的SolidWorks图档。");
                return;
            }
        }

        var propertyReadOperationStarted = false;
        try
        {
            if (initialOperation != PropertyOperationMode.PropertyWriteback)
            {
                if (!TryBeginWorkspaceOperation("正在批量读取图纸属性"))
                {
                    ShowWorkspaceOperationBusy();
                    return;
                }
                propertyReadOperationStarted = true;
                taskPaneControl.SetWorkspaceOperationProgress(
                    string.Concat("准备读取图纸属性，共 ", operationItems.Length, " 个图档"),
                    0,
                    operationItems.Length);
            }

            IReadOnlyList<DocumentDto> projectDocuments = Array.Empty<DocumentDto>();
            var selectedProject = projectId.HasValue
                ? availableProjects.FirstOrDefault(project => project.Id == projectId.Value)
                : null;
            if (initialOperation == PropertyOperationMode.PropertyWriteback
                && projectId.HasValue
                && apiClient.IsAuthenticated)
            {
                projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
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
                     nativePropertyCards,
                     (message, completed, total) => taskPaneControl.SetWorkspaceOperationProgress(
                         string.Concat(message, "（", completed, "/", total, "）"),
                         completed,
                         total))
                 : Array.Empty<BatchPropertyEditItem>();
            if (propertyReadOperationStarted)
            {
                taskPaneControl.SetWorkspaceOperationProgress(
                    string.Concat("图纸属性读取完成，共 ", editItems.Count, " 个图档"),
                    editItems.Count,
                    editItems.Count);
                EndWorkspaceOperation();
                propertyReadOperationStarted = false;
            }
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
                ResolveUserDisplayName(item.RequestedBy),
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
                    ExecuteLocalBatchPropertyOperation(changedItems, settingPropertyCards, reportProgress),
                ValidateBatchDocumentRenames,
                ExecuteBatchDocumentRenames,
                (IReadOnlyList<string>)(selectedProject?.SerialNumbers ?? new List<string>()),
                currentTree?.FileName,
                taskPaneControl.Handle))
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
        finally
        {
            if (propertyReadOperationStarted)
            {
                EndWorkspaceOperation();
            }
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
                    document = FindLoadedBatchDocument(node);
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
                    var unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var localProperties = ReadEditableProperties(document, item.ConfigurationName, node.Kind != CadDocumentKind.Drawing, unread);
                    item.SetUnreadProperties(unread);
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
                var assignable = changedItems.Where(item => !item.HasUnreadProperties && item.PropertyCardFields.Count > 0).ToArray();
                if (assignable.Length > 0) ConfigureNativePropertyCardFolders(assignable);
            }

            var failures = new List<string>();
            var skipped = new List<string>();
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
                        if (!ApplyNativePropertyCardAssignment(item))
                        {
                            skipped.Add(item.FileName + "：" + (item.HasUnreadProperties
                                ? "部分属性未读取，已保留原属性卡及字段值。"
                                : "尚未确认新的属性卡，已保留原属性卡及字段值。"));
                            continue;
                        }
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
            var succeeded = changedItems.Count - failures.Count - skipped.Count;
            var message = string.Concat(
                settingPropertyCards ? "本地属性卡设置完成。" : "本地批量属性编辑完成。",
                "\r\n已处理：", changedItems.Count, "个",
                "\r\n成功：", succeeded, "个",
                "\r\n失败：", failures.Count, "个",
                "\r\n跳过：", skipped.Count, "个",
                "\r\n未执行PLM获取权限、项目登记或版本存档。");
            if (failures.Count > 0)
            {
                message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", failures.Take(8)));
            }
            if (skipped.Count > 0)
                message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", skipped.Take(8)));
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
            TopMostMessageBox.Show(
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

    private bool ApplyNativePropertyCardAssignment(BatchPropertyEditItem item)
    {
        var node = item?.OperationItem?.Node
            ?? throw new ArgumentNullException(nameof(item));
        if (item.HasUnreadProperties || item.PropertyCardFields.Count == 0)
        {
            PluginDiagnosticLog.Record("属性卡", "已跳过属性卡设置，保留原属性卡及字段值：" + node.FileName
                + (item.HasUnreadProperties ? "；部分属性尚未读取。" : "；尚未确认新的属性卡。"));
            return false;
        }
        if (string.IsNullOrWhiteSpace(item.PropertyCardPath) || !File.Exists(item.PropertyCardPath))
        {
            throw new FileNotFoundException(string.Concat(node.FileName, "对应的原生属性卡不存在。"), item.PropertyCardPath);
        }

        IModelDoc2 document = null;
        var openedForBatch = false;
        try
        {
            document = FindLoadedBatchDocument(node);
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
            return true;
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
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards,
        Action<string, int, int> reportProgress = null)
    {
        var result = new List<BatchPropertyEditItem>();
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            for (var index = 0; index < operationItems.Count; index++)
            {
                var operationItem = operationItems[index];
                var node = operationItem.Node;
                reportProgress?.Invoke(string.Concat("正在读取图纸属性：", node.FileName), index, operationItems.Count);
                var activePropertyCard = ResolveActiveNativePropertyCard(node, nativePropertyCards);
                var unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var localProperties = ReadBatchPropertyValues(node, unread);
                if (localProperties == null)
                {
                    reportProgress?.Invoke("属性未能读取，已跳过并保留原值：" + node.FileName, index + 1, operationItems.Count);
                    continue;
                }
                var configurationName = BatchConfigurationName(node, localProperties);
                ExtractBatchPropertyValues(localProperties, configurationName, out var values, out var scopes, out var propertyNames);
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
                    propertyNames,
                    values["项目号"],
                    values["项目名称"]);
                item.SetActivePropertyCard(activePropertyCard);
                item.SetUnreadProperties(unread);
                result.Add(item);
                reportProgress?.Invoke(string.Concat("已读取图纸属性：", node.FileName), index + 1, operationItems.Count);
            }
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
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
            var document = FindLoadedBatchDocument(node);
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
        else
        {
            var defaultFileName = DefaultNativePropertyCardFileName(node.Kind);
            selectedPath = candidates.FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                defaultFileName,
                StringComparison.OrdinalIgnoreCase));
            if (selectedPath == null && candidates.Count == 1)
            {
                selectedPath = candidates[0];
            }
        }

        return string.IsNullOrWhiteSpace(selectedPath)
            ? null
            : NativePropertyCardTemplate.Load(selectedPath);
    }

    private static string DefaultNativePropertyCardFileName(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "asm.asmprp";
            case CadDocumentKind.Part: return "part.prtprp";
            case CadDocumentKind.Drawing: return "drw.drwprp";
            default: return string.Empty;
        }
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
            case 6: return "部件图";
            default: return string.Empty;
        }
    }

    private IReadOnlyDictionary<string, string> ReadBatchPropertyValues(CadTreeNode node, ISet<string> unreadProperties = null)
    {
        IModelDoc2 document = null;
        var openedForRead = false;
        try
        {
            document = FindLoadedBatchDocument(node);
            if (document == null)
            {
                if (node.Status == CadReferenceStatus.Virtual)
                    throw new InvalidOperationException("嵌入组件尚未在父装配体内加载，请还原该组件后再读取属性：" + node.FileName);
                document = OpenDocumentInvisiblyForBatch(
                    node.FullPath,
                    ToSolidWorksDocumentType(node.Kind),
                    node.Configuration ?? string.Empty);
                openedForRead = true;
            }
            if (document == null || node.Status != CadReferenceStatus.Virtual && !PathsEqual(document.GetPathName(), node.FullPath))
                throw new InvalidOperationException("未能安全读取指定图档，请核对引用路径：" + node.FileName);
            return ReadEditableProperties(document, node.Configuration, node.Kind != CadDocumentKind.Drawing,
                unreadProperties ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            LogDiagnostic(string.Concat("ReadBatchPropertyValues.", node.FileName), exception);
            PluginDiagnosticLog.Record("属性待读取", "已跳过图档的属性编辑并保留原值，其他图档可继续：" + node.FileName);
            return null;
        }
        finally
        {
            if (openedForRead && document != null)
            {
                CloseBatchOpenedDocument(document);
            }
        }
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

            var storageName = propertyName;
            propertyName = BatchPropertyEditItem.NormalizePropertyName(propertyName);
            priority = priority * 10 + BatchPropertyStoragePriority(propertyName, storageName);
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

            if (found || !values.ContainsKey(editorPropertyName))
            {
                values[editorPropertyName] = found ? value ?? string.Empty : string.Empty;
            }
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

    private static int BatchPropertyStoragePriority(string editorPropertyName, string storageName)
    {
        var names = BatchPropertyStorageNames(editorPropertyName);
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], storageName, StringComparison.OrdinalIgnoreCase))
            {
                return names.Count - index;
            }
        }
        return 0;
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
            document = FindLoadedBatchDocument(node);
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
            foreach (var field in item.PropertyCardFields.Where(field => item.IsPropertyApplicable(field.EditorPropertyName)))
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
                var storageName = cardField?.PropertyName ?? BatchPropertyStorageName(property.Key);
                SetBatchCustomProperty(
                    manager,
                    storageName,
                    property.Value,
                    item.OriginalValue(property.Key));
                RemoveLegacyBatchPropertyAliases(manager, property.Key, storageName);
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

    private static void RemoveLegacyBatchPropertyAliases(
        CustomPropertyManager manager,
        string editorPropertyName,
        string retainedStorageName)
    {
        if (manager == null)
        {
            return;
        }

        var aliases = BatchPropertyStorageNames(editorPropertyName)
            .Where(name => !string.Equals(name, retainedStorageName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (aliases.Length == 0)
        {
            return;
        }

        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        foreach (var alias in aliases)
        {
            var existingName = names.FirstOrDefault(name => string.Equals(name, alias, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(existingName))
            {
                continue;
            }

            manager.Delete2(existingName);
            names = manager.GetNames() as string[] ?? Array.Empty<string>();
            if (names.Any(name => string.Equals(name, existingName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(string.Concat("旧属性“", existingName, "”迁移失败。"));
            }
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
        IReadOnlyCollection<string> initiallySelectedPaths = null,
        bool initialStandardLibrary = false, int initialStandardDrawingType = 0)
    {
        if (taskPaneControl == null || taskPaneControl.IsDisposed) return;
        if (ArchiveSession.Current?.WorkflowStarted == true) { ArchiveSession.Current.BringToFront(); return; }
        if (taskPaneControl.InvokeRequired)
        {
            taskPaneControl.BeginInvoke((Action)(() => OpenBatchOperationDialog(preferredProjectId, initialOperation, initiallySelectedPaths)));
            return;
        }
        if (Volatile.Read(ref workspaceOperationInProgress) != 0)
        {
            ShowWorkspaceOperationBusy();
            return;
        }
        if (Interlocked.Exchange(ref batchDialogInProgress, 1) != 0) { ArchiveSession.Current?.BringToFront(); return; }
        Interlocked.Increment(ref refreshSuppressionDepth);
        CancelMetadataRefresh();
        ScheduleTreeRefresh(false, false);
        LogCheckInCheckpoint("batch-dialog-entry", currentTree?.FullPath);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new TaskPaneSynchronizationContext(taskPaneControl));
        var archiveSession = initialOperation == BatchOperationKind.CheckIn ? EnsureArchiveSession() : ArchiveSession.Current;
        try
        {
            if (archiveSession != null)
            {
                archiveSession.WorkflowStarted = true;
                await archiveSession.Ready;
                archiveSession.Token.ThrowIfCancellationRequested();
            }
            await OpenBatchOperationDialogAsync(preferredProjectId, initialOperation, initiallySelectedPaths, initialStandardLibrary, initialStandardDrawingType);
        }
        catch (OperationCanceledException) when (archiveSession?.Token.IsCancellationRequested == true)
        {
            archiveSession.SetStage("已收到取消请求，正在结束本次存档操作…");
        }
        catch (Exception exception)
        {
            LogDiagnostic("OpenBatchOperationDialog", exception);
            ShowError("整体操作未完成，请保留本地文件并重试；如仍失败，请提供插件诊断日志。");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            Interlocked.Decrement(ref refreshSuppressionDepth);
            Interlocked.Exchange(ref batchDialogInProgress, 0);
            LogCheckInCheckpoint("batch-dialog-exit", currentTree?.FullPath);
            if (archiveSession != null)
            {
                archiveSession.FinishIfRunning();
                await archiveSession.Closed;
                archiveSession.Dispose();
            }
        }
    }

    private async Task OpenBatchOperationDialogAsync(
        Guid? preferredProjectId,
        BatchOperationKind initialOperation,
        IReadOnlyCollection<string> initiallySelectedPaths, bool initialStandardLibrary, int initialStandardDrawingType)
    {
        if (!apiClient.IsAuthenticated)
        {
            ShowError("请先登录PLM。");
            return;
        }

        var operationToken = ArchiveOperationToken;
        ArchiveSession.Current?.SetStage("正在读取项目和标准库权限…");
        StandardStructureViewDto standardLibraryView = null;
        try { standardLibraryView = await apiClient.GetStandardStructureCapabilitiesAsync(operationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { LogDiagnostic("读取存档窗口标准库权限", error); }
        standardLibraryArchiveAllowed = standardLibraryView?.CanArchive == true;
        if (availableProjects.Count == 0 && !standardLibraryArchiveAllowed)
        {
            ShowError("当前账号没有可用项目，无法执行整套操作。");
            return;
        }

        ArchiveSession.Current?.SetStage("正在读取当前设计树…");
        if (currentTree == null) RefreshTree(false);
        if (currentTree == null || (initialOperation == BatchOperationKind.CheckIn && currentTree.IsReadOnlyPreview))
        {
            ShowError("请先打开当前工作装配，只读预览不能执行整套操作。");
            return;
        }

        // Reuse the assembly tree. Discover new drawings without opening or rescanning models.
        if (initialOperation == BatchOperationKind.CheckIn)
        {
            var locationAliases = ResolveArchiveFileLocations(currentTree);
            initiallySelectedPaths = initiallySelectedPaths?.Select(path => locationAliases.TryGetValue(path, out var actual) ? actual : path).ToArray();
            scanner.RefreshRelatedDrawings(currentTree);
            RestorePersistedDocumentIdentities(currentTree);
        }
        // Selected files are checked again before any save or upload.
        LogCheckInCheckpoint("batch-dialog-build-items", currentTree.FullPath);
        var items = BuildBatchOperationItems(currentTree);
        if (items.Count == 0)
        {
            ShowError("当前结构中没有可操作的SolidWorks图档。");
            return;
        }

        if (initialOperation == BatchOperationKind.CheckIn)
        {
            try
            {
                await ResolveSuppressedAssemblyReferencesAsync(currentTree, operationToken);
                await ReadMissingSuppressedStructuresAsync(currentTree, operationToken);
                // Display-tree failures may be transient. The confirmed selection is rescanned
                // strictly in PrepareArchiveScopeAsync before registration or upload.
                taskPaneControl.SetTree(currentTree);
                items = BuildBatchOperationItems(currentTree);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is IOException)
            { currentTree.StructureIndexPending = true; LogDiagnostic("读取压缩装配体结构，文件清单将在提交时核对", exception); }
            catch (OperationCanceledException) { PluginDiagnosticLog.Record("取消", "已取消存档前结构检查，尚未上传任何文件。"); return; }
        }

        var initialProjectId = preferredProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        ArchiveSession.Current?.SetStage("正在核对图档归属，准备项目和文件选择…");
        var inheritedDrawingProjects = await ResolveInheritedDrawingProjectsAsync(items, initialProjectId);
        LogCheckInCheckpoint("batch-dialog-create", currentTree.FullPath);
        ArchiveSelection dialog;
        while (true)
        {
            ArchiveSession.Current?.SetStage("请选择本次存档项目和图档，点击确认存档后将自动检查并完成提交。");
            dialog = ShowArchiveSelection(() => CreateBatchOperationDialog(
                currentTree,
                items,
                availableProjects,
                initialProjectId,
                authenticatedUsername,
                initialOperation,
                initiallySelectedPaths,
                inheritedDrawingProjects,
                userDisplayNames, standardLibraryArchiveAllowed, initialStandardLibrary, initialStandardDrawingType));
            LogCheckInCheckpoint("batch-dialog-show", currentTree.FullPath);
            var dialogResult = dialog.Result;
            LogCheckInCheckpoint("batch-dialog-result-" + dialogResult, currentTree.FullPath);
            if (dialogResult == DialogResult.Retry && !string.IsNullOrWhiteSpace(dialog.AdditionalDrawingFolder))
            {
                scanner.AddRelatedDrawings(currentTree, dialog.AdditionalDrawingFolder, true);
                taskPaneControl.SetTree(currentTree);
                items = BuildBatchOperationItems(currentTree);
                initiallySelectedPaths = dialog.SelectedItems.Select(item => item.Node.FullPath).ToArray();
                initialProjectId = dialog.SelectedProjectId ?? initialProjectId;
                initialStandardLibrary = dialog.ArchiveToStandardLibrary;
                initialStandardDrawingType = dialog.StandardLibraryDrawingType;
                inheritedDrawingProjects = await ResolveInheritedDrawingProjectsAsync(items, initialProjectId);
                continue;
            }
            if (dialogResult != DialogResult.OK)
            {
                return;
            }
            break;
        }

        {

            if (dialog.ArchiveToStandardLibrary)
            {
                await ArchiveStandardStructureAsync(dialog.StandardLibraryDrawingType, dialog.ChangeNote, currentTree.FullPath);
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
            var selectedItems = dialog.SelectedItems;
            IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions;
            try
            {
                if (dialog.Operation == BatchOperationKind.CheckIn)
                {
                    if (!await ReconcileArchiveBatchesAsync())
                    {
                        ShowError("上次整批存档结果尚未核对，请确认服务器连接后重试。本地文件和恢复记录已保留。");
                        return;
                    }
                    if (archiveRecoveryNeedsReview)
                    {
                        archiveRecoveryNeedsReview = false;
                        ScheduleTreeRefresh();
                        ShowError("上次整批存档已在服务器完成，请刷新设计树并核对版本后再提交，避免重复升版。");
                        return;
                    }
                    ArchiveSession.Current?.SetStage("正在复核本次选择的结构、引用文件和图档归属…");
                    selectedItems = await PrepareArchiveScopeAsync(selectedItems, selectedProjectId, operationToken);
                    await ValidateArchiveInputsBeforePreparationAsync(selectedItems, selectedProjectId, operationToken);
                }
                registrationDecisions = null;
                if (dialog.Operation == BatchOperationKind.CheckIn)
                {
                    await ValidateBatchProjectAsync(selectedItems, selectedProjectId);
                    registrationDecisions = await ReviewRegistrationDuplicatesAsync(selectedItems, selectedProjectId);
                    if (registrationDecisions == null) return;
                    await ValidateBatchProjectAsync(selectedItems, selectedProjectId);
                }
            }
            catch (Exception exception)
            {
                LogDiagnostic("整套存档预检查；根图档=" + currentTree.FullPath + "；项目=" + selectedProjectId, exception);
                ShowError(ArchivePreflightError(exception));
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
            List<IModelDoc2> preloadedDocuments = null;
            try
            {
                if (dialog.Operation == BatchOperationKind.AcquireLatestAndCheckout)
                {
                    var result = await AcquireExplicitlyFromPlmAsync(dialog.SelectedItems, selectedProjectId, registrationDecisions);
                    var acquireMessage = string.Concat(
                            "整套获取完成。\r\n归属项目：", selectedProjectDisplay,
                            "\r\n已获取权限：", result.CheckedOutFiles, "个",
                            "\r\n已更新本地文件：", result.UpdatedFiles, "个",
                            "\r\n本地已是最新或首次登记：", result.CheckedOutFiles - result.UpdatedFiles, "个");
                    foreach (var item in dialog.SelectedItems) ArchiveSession.Current?.TrackFile(item.Node, "已获取权限（服务器已确认）");
                    if (ArchiveSession.Current != null) ArchiveSession.Current.Complete(acquireMessage);
                    else TopMostMessageBox.Show(
                        taskPaneControl,
                        acquireMessage,
                        "UPLM",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                    batchPreflightTimeout = CancellationTokenSource.CreateLinkedTokenSource(batchCancellation.Token);
                    batchPreflightTimeout.CancelAfter(TimeSpan.FromMinutes(15));
                    progressDialog = new BatchProgressDialog(selectedItems.Count);
                    if (!progressDialog.IsSessionProxy) progressDialog.CancelRequested += (_, __) => batchCancellation.Cancel();
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
                    reportBatchStage("阶段1/4：正在分析本地变更…");

                    var checkInPlan = await TryBuildUnchangedArchivePlanAsync(selectedItems, reportBatchStage, batchPreflightTimeout.Token);
                    if (checkInPlan == null)
                    {
                        preloadedDocuments = await PreloadBatchCheckInDocumentsAsync(selectedItems, reportBatchStage, batchCancellation.Token);
                        var sourcesSaved = await EnsureBatchCheckInSavedStateAsync(selectedItems, selectedProjectId, batchCancellation.Token);
                        if (sourcesSaved)
                        {
                            batchCancellation.Token.ThrowIfCancellationRequested();
                            // Saving can change the registration fingerprint and duplicate decision.
                            selectedItems = await PrepareArchiveScopeAsync(selectedItems, selectedProjectId, batchCancellation.Token);
                            registrationDecisions = await ReviewRegistrationDuplicatesAsync(selectedItems, selectedProjectId);
                            if (registrationDecisions == null) return;
                            await ValidateBatchProjectAsync(selectedItems, selectedProjectId);
                        }
                        PreparePendingRenamesForCheckIn(selectedItems.Select(item => item.Node));

                        LogOperation(string.Concat("Batch check-in plan start selected=", selectedItems.Count));
                        checkInPlan = await BuildBatchCheckInPlanAsync(
                            selectedItems,
                            reportBatchStage,
                            batchPreflightTimeout.Token);
                    }
                    LogOperation(string.Concat(
                        "Batch check-in plan end included=", checkInPlan.Items.Count,
                        " skipped=", checkInPlan.SkippedFiles));
                    checkInPlan = await UpdateLinkedDrawingsAsync(checkInPlan, selectedProjectId, batchCancellation.Token);
                    ValidateHistoricalReferenceScope(currentTree, checkInPlan.Items);
                    batchPreflightTimeout.Dispose();
                    batchPreflightTimeout = null;

                    // The operation dialog already confirms the selected scope. Ask again
                    // only if preparation really adds a file beyond that selection.
                    if (checkInPlan.Items.Any(item => !selectedItems.Any(selected => PathsEqual(selected.Node.FullPath, item.Node.FullPath))))
                    {
                        var rows = BuildArchiveScopeRows(selectedItems, checkInPlan);
                        if (ArchiveSession.Current != null)
                        {
                            if (ArchiveSession.Current.ShowDialog(() => new ArchiveScopeReviewDialog(rows, checkInPlan.Items.Count, checkInPlan.SkippedFiles),
                                (_, answer) => answer) != DialogResult.OK) return;
                        }
                        else using (var scopeReview = new ArchiveScopeReviewDialog(rows, checkInPlan.Items.Count, checkInPlan.SkippedFiles))
                            if (scopeReview.ShowDialog(progressDialog) != DialogResult.OK) return;
                        batchCancellation.Token.ThrowIfCancellationRequested();
                    }
                    reportBatchStage("正在准备全部文件权限…");
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
                        checkInPlan.Preflights, dialog.KeepEditing, dialog.HandoffTo);
                    foreach (var pendingDrawing in EnumerateCadNodes(currentTree).Where(node => !string.IsNullOrWhiteSpace(node.DrawingUpdatePendingReason)))
                        result.BomWarnings.Add(pendingDrawing.FileName + "：关联工程图待更新，本次未提交该工程图；更新并审核后才能用于本次变更发布。原因：" + pendingDrawing.DrawingUpdatePendingReason);
                    LogOperation(string.Concat(
                        "Batch check-in execution end created=", result.CreatedVersions,
                        " unchanged=", result.UnchangedFiles,
                        " failures=", result.Failures.Count));
                    var message = string.Concat(
                        "当前结构存档结果\r\n归属项目：", selectedProjectDisplay,
                        "\r\n本次工程提交图档：", checkInPlan.Items.Count(item => item.Node.HistoricalRepair == null), "个（含关联工程图）",
                        "\r\n自动登记/准备权限：", result.PreparedPermissions, "个",
                        "\r\n生成新版本：", result.CreatedVersions, "个",
                        "\r\n历史资料补齐：", result.HistoricalRepairs, "个（不计工程变更，原装配版本保留）",
                        "\r\n无变更，仅处理编辑权限（不升版）：", result.UnchangedFiles, "个",
                        "\r\n未变更且未获取权限：", checkInPlan.SkippedFiles, "个",
                        "\r\n失败：", result.Failures.Count, "个",
                        "\r\n存档提醒：", result.BomWarnings.Count, "项");
                    if (result.Failures.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n失败明细：\r\n", string.Join("\r\n", result.Failures));
                    }
                    if (result.BomWarnings.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.BomWarnings.Distinct()));
                    }

                    if (EnumerateArchiveNodes(currentTree).Any(node => node.UnmanagedReferences.Count > 0))
                        message += "\r\n其他未确认的路径记录已保留在诊断日志中；本次按实际图档和有效设计依赖存档。";
                    message = string.Concat(message, "\r\n\r\n成功明细：\r\n", string.Join("\r\n", result.Successes));
                    var skipped = selectedItems.Where(item => !checkInPlan.Items.Any(included => PathsEqual(included.Node.FullPath, item.Node.FullPath)))
                        .Select(item => string.Concat(item.Node.FileName, !string.IsNullOrWhiteSpace(item.Node.DrawingUpdatePendingReason)
                            ? "：关联工程图待更新，未提交" : item.Node.DrawingReviewLocked ? "：审核中，未提交" : "：未变更，未提交"));
                    message = string.Concat(message, "\r\n\r\n跳过明细：\r\n", string.Join("\r\n", skipped));
                    LogOperation(string.Concat("Batch check-in result\r\n", message));
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
                        ? "存档前检查超过15分钟，整批提交已停止，尚未产生本批次存档版本。本地修改和编辑权限已保留。"
                        : "整批存档已取消。本批次只会全部提交或全部回滚；请刷新项目确认取消时是否已整批完成。本地修改已保留。 ");
                }
            }
            catch (Exception exception)
            {
                LogDiagnostic("OnBatchOperationRequested", exception);
                if (progressDialog != null && !progressDialog.IsDisposed)
                {
                    progressDialog.Fail(ArchivePreflightError(exception));
                }
                else
                {
                    ShowError(exception.Message);
                }
            }
            finally
            {
                if (preloadedDocuments != null)
                    ClosePreloadedArchiveDocuments(preloadedDocuments);
                preloadedArchiveDrawings.Clear();
                preloadedOwnedDrawings.Clear();
                foreach (var node in EnumerateArchiveNodes(currentTree)) node.HistoricalRepair = null;
                if (batchSynchronizationContextInstalled)
                {
                    SynchronizationContext.SetSynchronizationContext(previousBatchSynchronizationContext);
                }

                if (ReferenceEquals(activeBatchProgressDialog, progressDialog))
                {
                    activeBatchProgressDialog = null;
                }

                batchPreflightTimeout?.Dispose();
                if (progressDialog?.IsSessionProxy == true) progressDialog.Dispose();
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
                    throw new InvalidOperationException(string.Concat(node.FileName, "正在由", ResolveUserDisplayName(node.CheckedOutBy), "编辑，整套获取已停止。"));
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
                        historicalEditContexts.Remove(node.DocumentId.Value);
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
                    historicalEditContexts.Remove(node.DocumentId.Value);
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

            await AcquireLinkedDrawingPermissionsAsync(projectId,
                items.Where(item => item.Node.Kind != CadDocumentKind.Drawing && item.Node.DocumentId.HasValue).Select(item => item.Node.DocumentId.Value),
                Path.GetDirectoryName(rootPath), lifetime.Token);
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
        var projectDocuments = await apiClient.GetDocumentsAsync(projectId, ArchiveOperationToken);
        foreach (var node in DistinctBatchItems(selectedItems).SelectMany(item => new[] { item.Node }.Concat(item.Node.ReferencedModels)))
        {
            if (node.DocumentId.HasValue
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata)
                || metadata.ProjectId != projectId) continue;
            var registered = projectDocuments.FirstOrDefault(candidate => candidate.Id == metadata.DocumentId);
            if (registered == null) continue;
            ApplyRegisteredDocumentToMatchingInstances(node, registered);
            node.OpenedVersionId = metadata.VersionId;
            node.CurrentRevision = metadata.Revision;
        }
        // Older add-ins stored a copyable ID only. Never silently adopt it at a new path.
        var legacyMatches = DistinctBatchItems(selectedItems)
            .Where(item => !item.Node.DocumentId.HasValue
                && PdmDocumentIdentityStore.TryReadProvenance(item.Node.FullPath, out var legacyId, out _)
                && projectDocuments.Any(document => document.Id == legacyId))
            .ToArray();
        if (legacyMatches.Length > 0)
        {
            var choice = TopMostMessageBox.Show(taskPaneControl,
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
        SynchronizeBatchFileIdentities(selectedItems);
    }

    private static void SynchronizeBatchFileIdentities(IEnumerable<BatchOperationItem> items)
    {
        var nodes = items.SelectMany(item => EnumerateArchiveNodes(item.Node)).Distinct()
            .Where(node => !string.IsNullOrWhiteSpace(node.FullPath));
        foreach (var group in nodes.GroupBy(node => Path.GetFullPath(node.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId.Value).Distinct().ToArray();
            if (ids.Length > 1) throw new InvalidOperationException("同一本地文件的实例存在不同PLM图档编号，请刷新并核对关联：" + group.First().FileName);
            var source = group.Where(node => node.DocumentId.HasValue)
                .OrderByDescending(PdmApiClient.HasResolvedReferenceVersion).ThenByDescending(node => node.HasStoredVersion).FirstOrDefault();
            if (source == null) continue;
            foreach (var node in group.Where(node => node != source)) CopyPdmIdentity(source, node);
        }
    }

    private static IEnumerable<CadTreeNode> EnumerateArchiveNodes(CadTreeNode root)
    {
        var pending = new Stack<CadTreeNode>();
        var visited = new HashSet<CadTreeNode>();
        if (root != null) pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node == null || node.IsOutsideNativeFileScope || !visited.Add(node)) continue;
            yield return node;
            foreach (var child in node.Children) pending.Push(child);
            foreach (var model in node.ReferencedModels) pending.Push(model);
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
                    && RequiresExternalReferenceCopy(node, projectId))
                .Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            if (outside.Length > 0)
                PluginDiagnosticLog.Record("存档提醒", "目录外引用按实际图档身份和版本处理，目录位置不阻止存档：" + string.Join("、", outside));
        }
    }

    private static bool RequiresExternalReferenceCopy(CadTreeNode node, Guid projectId)
    {
        // Imported native files may live in any temporary directory. Location alone is not ownership.
        if (PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata))
            return metadata.ProjectId != projectId;
        return PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
            || PdmDocumentIdentityStore.TryReadProvenance(node.FullPath, out _, out _);
    }

    private bool ConfirmNewDocumentProject(Guid projectId, int documentCount)
    {
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (project == null) throw new InvalidOperationException("所选项目已不可用，请刷新后重试。");
        var parentProject = project.ParentProjectId.HasValue
            ? availableProjects.FirstOrDefault(item => item.Id == project.ParentProjectId.Value)
            : null;
        if (ArchiveSession.Current != null)
            return ArchiveSession.Current.ShowDialog(() => new ProjectAdmissionConfirmationDialog(project, parentProject, documentCount, userDisplayNames),
                (_, answer) => answer) == DialogResult.OK;
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
        var message = string.Concat("当前账号对", target, "无存档权限，不能", action, "。只能浏览本人未负责的项目。");
        if (!DocumentRuleGate.ShouldBlock("A03", message)) return true; // Server authorization remains authoritative.
        ShowError(message);
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
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, ArchiveOperationToken);
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
        }).ToArray(), ArchiveOperationToken);
        var matches = await apiClient.PreflightDocumentRegistrationAsync(projectId, candidates, ArchiveOperationToken);
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
        var pendingCount = matches.Count(match => (RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.PendingFirstArchive);
        var project = availableProjects.FirstOrDefault(item => item.Id == projectId);
        if (matches.Any(match => nodesByKey[match.CandidateKey].HistoricalRepair == null
            && ((RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.SameNameSameContent
                || (RegistrationMatchKind)match.MatchKind == RegistrationMatchKind.PendingFirstArchive)))
        {
            var choice = TopMostMessageBox.Show(taskPaneControl,
                $"目标项目：{project?.ToString() ?? projectId.ToString()}\r\n"
                + $"独立新建图档：{nodes.Length - reusedCount - pendingCount}个\r\n"
                + $"继续目标项目中同名且内容相同的图档：{reusedCount}个\r\n\r\n"
                + $"继续本人尚未完成的首次存档：{pendingCount}个（沿用原编号，不覆盖已存档版本）\r\n\r\n"
                + "复制或打包的副本将取得独立图档编号，不修改原项目及其历史版本。\r\n"
                + "仅校验当前目标项目内的同名文件；不会查询或比较其他项目中的图档。\r\n"
                + "当前目标项目内同名且内容相同的文件将继续已有登记，避免重复提交。\r\n\r\n确认按以上规则继续？",
                "确认图档登记方式", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.OK) return null;
        }
        var projectDocuments = reusedCount + pendingCount > 0 ? await apiClient.GetDocumentsAsync(projectId, ArchiveOperationToken) : null;
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

            if (matchKind == RegistrationMatchKind.SameNameSameContent || matchKind == RegistrationMatchKind.PendingFirstArchive)
            {
                var existing = projectDocuments.FirstOrDefault(document => document.Id == match.ExistingDocumentId)
                    ?? throw new InvalidOperationException(string.Concat("重复预检命中的图档已发生变化：", match.ExistingFileName));
                if (matchKind == RegistrationMatchKind.PendingFirstArchive)
                    await RestorePendingFirstArchiveAsync(node, existing, projectId, ArchiveOperationToken);
                else
                    ApplyPreparedDocumentToMatchingInstances(node, existing, node.HistoricalRepair == null);
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

    private async Task RestorePendingFirstArchiveAsync(CadTreeNode node, DocumentDto existing, Guid projectId, CancellationToken cancellationToken)
    {
        if (existing.ProjectId != projectId || existing.Kind != (int)node.Kind || existing.State != 0
            || !string.Equals(existing.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authenticatedUsername)
            || !string.Equals(existing.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(existing.CheckoutMachine)
                && !string.Equals(existing.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("首次存档登记的归属或编辑权限已变化，请使用原存档电脑核对图档并重新获取权限：" + node.FileName);
        if ((await apiClient.GetVersionsAsync(existing.Id, cancellationToken)).Count != 0)
            throw new InvalidOperationException("该图档已经产生存档版本，不能按首次存档重试。请打开已有图档获取权限后提交修改：" + node.FileName);

        ApplyRegisteredDocumentToMatchingInstances(node, existing);
        LogOperation("已恢复首次存档登记，沿用原图档编号：" + node.FileName + "，编号：" + existing.Id);
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
        => DocumentRuleGate.Validate(() => ValidateBatchFileNamesCore(items));

    private static void ValidateBatchFileNamesCore(IReadOnlyList<BatchOperationItem> items)
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
                "同名文件的内容、身份或未保存修改尚不一致：", conflict.Key,
                "。原文件均保留，请确认应采用哪份内容；需要同时保留时请为其中一份另行命名。"));
        }
    }

    private static void ValidateAcquireNode(CadTreeNode node, bool allowDrawingReviewWriteback = false)
    {
        if (node.IsReadOnlyPreview)
        {
            DocumentRuleGate.Fail("A09", new InvalidOperationException("只读预览不能原地获取编辑权限，请先切换到编辑工作区。"));
        }
        if (node.DrawingReviewLocked && !allowDrawingReviewWriteback)
        {
            DocumentRuleGate.Fail("A07", new InvalidOperationException("图档正在进行图纸审核，只允许只读打开，不能获取编辑权限。"));
        }

        if (ToSolidWorksDocumentType(node.Kind) == (int)swDocumentTypes_e.swDocNONE && !node.IsImportedSourceFile)
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

    private void CloseConflictingLoadedDocumentsForHistoricalOpen(
        ControlledOpenManifestDto manifest,
        string historicalDirectory)
    {
        if (manifest?.Files == null || string.IsNullOrWhiteSpace(historicalDirectory))
        {
            return;
        }

        var expectedPaths = manifest.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.FileName))
            .ToDictionary(
                file => file.FileName,
                file => Path.GetFullPath(Path.Combine(historicalDirectory, file.RelativePath ?? file.FileName)),
                StringComparer.OrdinalIgnoreCase);
        var allDocuments = CaptureLoadedDocumentSnapshots();
        var relevantDocuments = allDocuments
            .Where(document => expectedPaths.ContainsKey(Path.GetFileName(document.Path)))
            .Where(document => !PathsEqual(document.Path, expectedPaths[Path.GetFileName(document.Path)]))
            .ToArray();
        if (relevantDocuments.Length == 0) return;
        var loaded = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Where(document => !string.IsNullOrWhiteSpace(document.GetPathName())).ToArray();
        var references = loaded.GroupBy(document => document.GetPathName(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => LoadedReferencePaths(group.First()).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var readOnlyPaths = new HashSet<string>(loaded.Where(document => document.IsOpenedReadOnly()).Select(document => document.GetPathName()), StringComparer.OrdinalIgnoreCase);
        relevantDocuments = ExpandReadOnlyReferenceOwners(allDocuments, relevantDocuments, references, readOnlyPaths);
        EnsureNoUnselectedReferenceOwners(relevantDocuments.Select(document => document.Path).ToArray());
        var dirty = relevantDocuments
            .Where(document => document.HasUnsavedChanges && !document.HasDiscardableReadOnlySaveFlag)
            .Select(document => Path.GetFileName(document.Path))
            .ToArray();
        if (dirty.Length > 0)
        {
            throw new InvalidOperationException(string.Concat(
                    "以下同名图档存在未保存修改，不能切换工作文件：",
                string.Join("、", dirty.Take(6)),
                "。请先保存或放弃修改后重试。"));
        }
        var loadedDocuments = relevantDocuments;

        var transientReadOnly = loadedDocuments
            .Where(document => document.HasUnsavedChanges && document.HasDiscardableReadOnlySaveFlag)
            .Select(document => document.Path)
            .ToArray();
        if (transientReadOnly.Length > 0)
        {
            LogOperation(string.Concat(
                "Controlled open preserves read-only pending states in recovery copies documents=",
                transientReadOnly.Length,
                " paths=",
                string.Join(" | ", transientReadOnly.Take(6))));
        }

        PreserveReadOnlyContextStates(loadedDocuments);

        foreach (var document in loadedDocuments)
        {
            application.CloseDoc(document.Title);
            LogOperation(string.Concat("Historical controlled open closed conflicting document path=", document.Path));
        }

        var remaining = CaptureLoadedDocumentSnapshots()
            .Select(document => document.Path)
            .FirstOrDefault(path => expectedPaths.TryGetValue(Path.GetFileName(path), out var expectedPath)
                && !PathsEqual(path, expectedPath));
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            throw new IOException(string.Concat(
                Path.GetFileName(remaining),
                "仍被SolidWorks中的装配或工程图引用，未能切换工作文件。请保存并关闭引用它的图档后重试；原文件及草稿已保留。"));
        }
    }

    private LoadedDocumentSnapshot[] CaptureLoadedDocumentSnapshots()
    {
        var documents = application?.GetDocuments() as Array;
        if (documents == null)
        {
            return Array.Empty<LoadedDocumentSnapshot>();
        }

        var snapshots = new List<LoadedDocumentSnapshot>();
        foreach (var item in documents)
        {
            if (!(item is IModelDoc2 document))
            {
                continue;
            }

            try
            {
                var path = document.GetPathName() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var hasUnsavedChanges = document.GetSaveFlag();
                snapshots.Add(new LoadedDocumentSnapshot(
                    path,
                    document.GetTitle(),
                    document.GetType(),
                    hasUnsavedChanges,
                    hasUnsavedChanges && HasDiscardableReadOnlySaveFlag(document, path)));
            }
            catch (COMException exception) when (exception.ErrorCode == unchecked((int)0x80010108))
            {
                LogOperation("Historical controlled open ignored disconnected document proxy while refreshing loaded documents");
            }
        }

        return snapshots.ToArray();
    }

    private static bool HasDiscardableReadOnlySaveFlag(IModelDoc2 document, string path)
    {
        if (document == null || !document.IsOpenedReadOnly())
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
    }

    private static HashSet<string> ValidateHistoricalControlledOpenReferences(
        IModelDoc2 openedDocument,
        string rootPath,
        ControlledOpenManifestDto manifest,
        ISldWorks application)
    {
        if (openedDocument == null
            || openedDocument.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
            || manifest?.Files == null)
        {
            return null;
        }

        var historicalDirectory = Path.GetDirectoryName(rootPath) ?? string.Empty;
        var expectedPaths = manifest.Files
            .Where(file => !file.IsRoot
                && !string.IsNullOrWhiteSpace(file.FileName)
                && !string.Equals(Path.GetExtension(file.FileName), ".SLDDRW", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                file => file.FileName,
                file => Path.GetFullPath(Path.Combine(historicalDirectory, file.RelativePath ?? file.FileName)),
                StringComparer.OrdinalIgnoreCase);
        if (expectedPaths.Count == 0)
        {
            // A parent-only package may legitimately contain embedded components.
            // It must still reject any ordinary component actually loaded elsewhere.
            return application != null ? ValidateControlledLoadedSources(openedDocument, rootPath, manifest, application) : null;
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<string>();
        void AddMismatch(string ruleId, string message)
        {
            if (DocumentRuleGate.ShouldBlock(ruleId, message)) mismatches.Add(message);
        }
        var importedStorage = application == null ? null : new ImportedComponentStorage(application);
        var components = (openedDocument as IAssemblyDoc)?.GetComponents(false) as Array;
        if (components != null)
        {
            foreach (var component in components.OfType<IComponent2>())
            {
                var savedPath = component.GetPathName() ?? string.Empty;
                var loadedModel = component.GetModelDoc2() as IModelDoc2;
                var actualPath = loadedModel?.GetPathName() ?? string.Empty;
                var importedCache = NativeArchiveManifest.IsInterconnectCachePath(actualPath)
                    || NativeArchiveManifest.IsInterconnectCachePath(savedPath);
                if (importedCache)
                {
                    var ownerPath = importedStorage?.FindOwner(component, openedDocument);
                    if (!string.IsNullOrWhiteSpace(ownerPath))
                    {
                        var ownerFile = manifest.Files.FirstOrDefault(candidate => PathsEqual(ownerPath,
                            candidate.IsRoot ? rootPath : Path.Combine(historicalDirectory, candidate.RelativePath ?? candidate.FileName)));
                        if (ownerFile == null || !ImportedComponentStorage.IsStableOwner(ownerPath))
                            AddMismatch("F22", "导入组件的保存归属未匹配本次受控图档：" + component.Name2);
                        else if (verifiedFiles.Add(ownerFile.FileName) && !ControlledReferenceMaterializer.Matches(ownerPath, ownerFile))
                            AddMismatch("F10", ownerFile.FileName + "：导入组件所属文件内容校验失败");
                        else matched.Add(ownerFile.FileName);
                        continue;
                    }
                    // Older archives may explicitly contain the imported source. Keep
                    // validating that exact controlled companion; do not discover others.
                    actualPath = component.GetImportedPath();
                    savedPath = actualPath;
                }
                var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(savedPath) ? actualPath : savedPath);
                if (string.IsNullOrWhiteSpace(fileName) || !expectedPaths.TryGetValue(fileName, out var expectedPath))
                {
                    if (importedCache) AddMismatch("F22", "导入组件未匹配所属原生图档或既有受控源文件：" + component.Name2);
                    continue;
                }

                var file = manifest.Files.First(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                if (verifiedFiles.Add(fileName) && !ControlledReferenceMaterializer.Matches(expectedPath, file))
                {
                    AddMismatch("F10", fileName + "：受控文件缺失或内容校验失败");
                    continue;
                }
                if (loadedModel == null)
                {
                    deferredFiles.Add(fileName);
                    PluginDiagnosticLog.Record("组件加载", fileName + "：受控文件完整，当前未加载；保留原加载状态。");
                    continue;
                }
                if (PathsEqual(actualPath, expectedPath))
                {
                    matched.Add(fileName);
                }
                else
                {
                    AddMismatch("F20", string.Concat(fileName, " 实际路径：", actualPath));
                }
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(string.Concat(
                "受控装配体引用校验失败，已关闭本次打开：",
                string.Join("；", mismatches.Distinct(StringComparer.OrdinalIgnoreCase).Take(6)),
                "。当前工作文件未被修改。"));
        }

        LogOperation(string.Concat(
            "Historical controlled open reference validation passed root=", rootPath,
            " files=", matched.Count, " suppressedOrLightweight=", deferredFiles.Count));
        return application != null ? ValidateControlledLoadedSources(openedDocument, rootPath, manifest, application) : null;
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
                PdmDocumentIdentityStore.TryReadLocalMetadata(plan.Node.FullPath, out var previousMetadata);
                plan.OriginalMetadata = previousMetadata;
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
                if (!string.Equals(actualSha256, plan.Version.Sha256, StringComparison.OrdinalIgnoreCase)
                    && !PdmDocumentIdentityStore.MatchesResolvedVersion(plan.StagedPath, plan.Version.Id, plan.Version.Sha256, actualSha256))
                {
                    throw new InvalidDataException(string.Concat(plan.Node.FileName, "更新后的文件校验失败。"));
                }
                ControlledReferenceMaterializer.CopyResolvedBinding(plan.StagedPath, plan.Node.FullPath);
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
                PdmDocumentIdentityStore.RestoreLocalMetadata(plan.Node.FullPath, plan.OriginalMetadata);
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

    private static void SetFileReadOnly(string path, bool readOnly, string reason = "图档权限同步")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        var updated = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (attributes == updated) return;
        File.SetAttributes(path, updated);
        PluginDiagnosticLog.Record("只读属性", "路径=" + path + "；原只读=" + ((attributes & FileAttributes.ReadOnly) != 0)
            + "；现只读=" + readOnly + "；原因=" + reason);
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

    private void ProtectLoadedDocument(string path, bool controlledWorkspaceOnly = false)
    {
        if (controlledWorkspaceOnly && !PdmDocumentIdentityStore.IsControlledWorkspacePath(path)) return;
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
        for (var index = 0; index < result.Count; index++)
            foreach (var model in result[index].Node.ReferencedModels)
                if (!string.IsNullOrWhiteSpace(model.FullPath) && !itemsByPath.ContainsKey(model.FullPath))
                    CollectBatchOperationItems(model, result[index].Depth + 1, result, itemsByPath, Array.Empty<CadTreeNode>());
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
        if (node == null || node.IsOutsideNativeFileScope)
        {
            return;
        }

        var childAncestors = ancestors;
        if ((ToSolidWorksDocumentType(node.Kind) != (int)swDocumentTypes_e.swDocNONE || node.IsImportedSourceFile)
            && (node.DocumentId.HasValue || !string.IsNullOrWhiteSpace(node.FullPath))
            && node.Status != CadReferenceStatus.Virtual
            && !IsSolidWorksTemporaryVirtualComponentPath(node.FullPath))
        {
            if (!itemsByPath.TryGetValue(BatchOperationDialog.SelectionKey(node), out var item))
            {
                item = new BatchOperationItem(node, depth);
                itemsByPath.Add(BatchOperationDialog.SelectionKey(node), item);
                target.Add(item);
            }

            // A part's modelling context may point back to its assembly. It is a
            // file dependency, not ownership of that assembly by the part.
            if (node.Role != CadReferenceRole.ExternalFileDependency) item.AddAncestors(ancestors);
            childAncestors = ancestors.Concat(new[] { node }).ToArray();
        }

        foreach (var child in node.Children)
        {
            CollectBatchOperationItems(child, depth + 1, target, itemsByPath, childAncestors);
        }
    }

    private void ValidatePlannedWriteAccess(CadTreeNode node)
    {
        ValidateAcquireNode(node);
        if (!string.IsNullOrWhiteSpace(node.CheckedOutBy) && !IsCheckedOutByCurrentUser(node) && !CanRecoverCurrentCheckoutSession(node))
            DocumentRuleGate.Fail("A05", new InvalidOperationException(node.FileName + "正在由" + ResolveUserDisplayName(node.CheckedOutBy) + "编辑，不能提交该文件的新内容。"));
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
        // 正在图纸审核中的图纸会被跳过而不是中断整套提交：它只需要只读打开，不影响其它图档提交。
        var reviewLockedFiles = new List<string>();
        var skippedFiles = 0;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var node = item.Node;
            reportStage?.Invoke(string.Concat("阶段1/4：正在分析变更 ", index + 1, " / ", items.Count, "：", node.FileName));
            LogOperation(string.Concat("Batch preflight start path=", node.FullPath));
            if (SuppressedAssemblyArchive.CanReuse(node) && !IsCheckedOutByCurrentUser(node)
                && !CanRecoverCurrentCheckoutSession(node))
            {
                skippedFiles++;
                LogOperation("Batch preflight keep archived suppressed reference path=" + node.FullPath);
                continue;
            }
            if (node.DrawingReviewLocked)
            {
                reviewLockedFiles.Add(node.FileName);
                LogOperation(string.Concat("Batch preflight skip review-locked path=", node.FullPath));
                continue;
            }


            if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
            {
                throw new FileNotFoundException(string.Concat(node.FileName, "的本地工作文件不存在，不能提交存档。"));
            }



            var loadedForPlan = application != null && (node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved)
                ? FindLoadedDocument(node.FullPath) : null;
            var ownsPendingSave = !node.DocumentId.HasValue || IsCheckedOutByCurrentUser(node)
                || loadedForPlan?.IsOpenedReadOnly() != true;
            if (!node.DocumentId.HasValue
                || IsCheckedOutByCurrentUser(node)
                || CanRecoverCurrentCheckoutSession(node)
                || ownsPendingSave && (node.IsModifiedInSolidWorks || node.WorkState == CadWorkState.ModifiedUnsaved))
            {
                ValidatePlannedWriteAccess(node);
                checkInItems.Add(item);
                LogOperation(string.Concat("Batch preflight include direct path=", node.FullPath, " state=", node.WorkState));
                continue;
            }

            var localSha256 = await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = NodeMatchesLatestFile(node, localSha256);
            DocumentVersionDto latest = null;
            IReadOnlyList<DocumentVersionDto> versions = null;
            if (node.Kind == CadDocumentKind.Assembly || node.Kind == CadDocumentKind.Drawing
                || !fileMatchesLatest
                || node.WorkState == CadWorkState.PendingCheckIn || node.LatestStructureIndexPending != false)
            {
                LogOperation(string.Concat("Batch preflight versions start path=", node.FullPath));
                // An unchanged part needs only the latest index state when that
                // metadata is unknown; ordinary populated trees add no requests.
                var indexOnlyRead = node.Kind == CadDocumentKind.Part && fileMatchesLatest
                    && node.WorkState != CadWorkState.PendingCheckIn;
                versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken, indexOnlyRead ? 1 : (int?)null);
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
                if (!IsCheckedOutByCurrentUser(node) && !CanRecoverCurrentCheckoutSession(node))
                {
                    node.CurrentRevision = localRevision;
                    node.OpenedVersionId = matchingHistoricalVersion.Id;
                    skippedFiles++;
                    LogOperation("未编辑的参考图档保留实际引用版本，不获取权限或升版：" + node.FileName
                        + "；引用=" + localRevision + "；最新=" + latestRevision);
                    continue;
                }
                var outdatedMessage = string.Concat(node.FileName, "（本地", localRevision, "，最新", latestRevision, "）");
                if (!DocumentRuleGate.ShouldBlock("D15", outdatedMessage))
                {
                    checkInItems.Add(item);
                    continue;
                }
                outdatedFiles.Add(outdatedMessage);
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

            var referenceMatchesLatest = node.Role == CadReferenceRole.ExternalFileDependency || node.Kind != CadDocumentKind.Assembly
                || latest != null && OwnAssemblyStructureMatches(latest.ReferenceSnapshot, node);
            referenceMatchesLatest &= !ArchiveIndexWasCompleted(latest?.ReferenceSnapshot, node);
            if (!fileMatchesLatest || !referenceMatchesLatest)
            {
                ValidatePlannedWriteAccess(node);
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
                "检测到", outdatedFiles.Count, "个本地文件仍是PLM历史版本，已停止整体存档，且未上传任何文件。\r\n",
                string.Join("\r\n", outdatedFiles.Take(8)),
                outdatedFiles.Count > 8 ? "\r\n……" : string.Empty,
                "\r\n请先执行“整体获取最新文件及权限”，确认装配引用更新并保存后，再重新提交存档。"));
        }

        if (reviewLockedFiles.Count > 0)
        {
            LogOperation(string.Concat(
                "Batch preflight skipped review-locked count=", reviewLockedFiles.Count,
                " files=", string.Join("、", reviewLockedFiles.Take(8))));
        }

        // A child version change does not acquire or submit unchanged ancestor assemblies.

        var missingPreflights = checkInItems
            .Where(item => item.Node.DocumentId.HasValue && !preflights.ContainsKey(item.Node.FullPath))
            .ToArray();
        const int preflightBatchSize = 8;
        for (var offset = 0; offset < missingPreflights.Length; offset += preflightBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = missingPreflights.Skip(offset).Take(preflightBatchSize).ToArray();
            reportStage?.Invoke(string.Concat(
                "阶段1/4：正在批量读取PLM版本 ",
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

        await IncludeChangedModelDrawingsAsync(items, checkInItems, preflights, cancellationToken);
        return new BatchCheckInPlan(checkInItems, items.Count(item => !checkInItems.Any(included => PathsEqual(included.Node.FullPath, item.Node.FullPath))), preflights);
    }
    private async Task<bool> EnsureBatchCheckInSavedStateAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var changedAssemblyPaths = await ChangedReadOnlyAssembliesAsync(selectedItems, cancellationToken);
        var loadedDocuments = DistinctBatchItems(selectedItems)
            .Where(item => item.Node != null && item.Node.HistoricalRepair == null)
            .Select(item => new { Item = item, Node = item.Node, Document = FindLoadedDocument(item.Node.FullPath) })
            .Where(item => item.Document != null)
            // Our read-only drawing windows contain generated view data. Preserve it in
            // the upload copy instead of rewriting the user's saved drawing on each archive.
            .Where(item => item.Node.Kind != CadDocumentKind.Drawing || !preloadedOwnedDrawings.Contains(item.Node.FullPath))
            .Where(item => item.Node.Kind == CadDocumentKind.Drawing || !item.Node.DocumentId.HasValue
                || IsCheckedOutByCurrentUser(item.Node) || !item.Document.IsOpenedReadOnly()
                || changedAssemblyPaths.Contains(item.Node.FullPath))
            .GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var unsavedDocuments = loadedDocuments.Where(item => item.Document.GetSaveFlag()).ToArray();
        if (unsavedDocuments.Length == 0)
        {
            return false;
        }

        var saveOrder = OrderBatchItemsForSave(DistinctBatchItems(selectedItems))
            .Select((item, index) => new { item.Node.FullPath, Index = index })
            .ToDictionary(item => item.FullPath, item => item.Index, StringComparer.OrdinalIgnoreCase);

        PluginDiagnosticLog.Record("存档准备", "按本次确认范围自动保存" + unsavedDocuments.Length + "个文件："
            + string.Join("、", unsavedDocuments.Select(item => item.Node.FileName).Take(8)));

        var failures = new List<string>();
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var item in loadedDocuments.OrderBy(candidate => saveOrder[candidate.Node.FullPath]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Saving a child can dirty a previously clean parent. Inspect at its turn,
                // after all children, and save drawings only after their models are stable.
                if (!item.Document.GetSaveFlag()) continue;
                taskPaneControl.SetWorkspaceOperationProgress(string.Concat("正在获取保存权限：", item.Node.FileName), 0, 0);
                ArchiveSession.Current?.ReportFile(0, 0, item.Node.FileName, "正在核对本地修改和保存权限…");
                if (item.Node.DocumentId.HasValue)
                    await PrepareBatchCheckInPermissionAsync(item.Item, projectId, cancellationToken);
                // Even an existing checkout can still be loaded read-only in SolidWorks.
                EnsureDocumentEditable(item.Document, item.Node.FullPath);
                taskPaneControl.SetWorkspaceOperationProgress(string.Concat("正在保存：", item.Node.FileName), 0, 0);
                ArchiveSession.Current?.ReportFile(0, 0, item.Node.FileName, "正在保存本地修改…");
                var errors = 0;
                var warnings = 0;
                var options = swSaveAsOptions_e.swSaveAsOptions_Silent;
                if (item.Node.Kind == CadDocumentKind.Assembly && EnumerateCadNodes(item.Node).Any(node => node.Status == CadReferenceStatus.Virtual))
                    options |= swSaveAsOptions_e.swSaveAsOptions_IncludeVirtualSubAsmComps;
                var saved = item.Document.Save3((int)options, ref errors, ref warnings);
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

            if (failures.Count > 0 || loadedDocuments.All(item => !item.Document.GetSaveFlag())) break;
            if (pass < 2) LogOperation("子件保存后有父装配更新，仅补保存仍有修改的图档；轮次：" + (pass + 2));
        }
        taskPaneControl.SetTree(currentTree);
        foreach (var item in loadedDocuments.Where(item => item.Document.GetSaveFlag()))
            if (!failures.Any(failure => failure.StartsWith(item.Node.FileName + "（", StringComparison.Ordinal)))
                failures.Add(item.Node.FileName + "（统一保存后仍有未保存状态）");
        if (failures.Count > 0)
        {
            throw new IOException(string.Concat(
                "以下文件保存失败，整体存档尚未开始：\r\n",
                string.Join("\r\n", failures.Take(8)),
                "\r\n本次尚未上传文件，已获取的编辑权限会保留。请直接保存这些图档后重试，无需修改子零件。"));
        }
        return true;
    }

    private async Task<bool> PrepareBatchCheckInPermissionAsync(
        BatchOperationItem item,
        Guid projectId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null,
        Guid? archiveBatchId = null, ICollection<Guid> reservedDocumentIds = null)
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
                    cancellationToken, archiveBatchId, checkoutSessionId, checkoutMachineName);
                if (archiveBatchId.HasValue)
                {
                    ArchiveBatchJournal.AddReservedDocument(archiveBatchId.Value, registered.Id);
                    reservedDocumentIds?.Add(registered.Id);
                }
                ApplyPreparedDocumentToMatchingInstances(node, registered, !archiveBatchId.HasValue);
                LogOperation(string.Concat("Batch check-in register end path=", node.FullPath, " document=", node.DocumentId));
            }

            LogOperation(string.Concat("Batch check-in checkout start path=", node.FullPath));
            var document = IsCheckedOutByCurrentUser(node) && archiveBatchId.HasValue
                ? null : await apiClient.CheckoutAsync(node.DocumentId.Value, checkoutSessionId, checkoutMachineName, cancellationToken);
            if (document != null) ApplyCheckoutDocument(node, document);
            node.WorkState = CadWorkState.Editable;
            checkedOut = true;
            LogOperation(string.Concat("Batch check-in checkout end path=", node.FullPath));

            // Check-in reads the saved native file, including shared read-only sources.
            // Only the earlier save step needs to make a dirty document editable.
            return true;
        }
        catch
        {
            if (checkedOut) LogOperation("存档准备未完成，已保留本地内容及有效编辑权限：" + node.FileName);
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
        IReadOnlyDictionary<string, BatchCheckInPreflight> preflights = null, bool keepEditing = false, string handoffTo = null)
    {
        var items = DistinctBatchItems(selectedItems);
        if (items.Count == 0) return new BatchCheckInResult();
        archiveReadAliases.Clear();
        ArchiveSession.Current?.SetPhase(2, "正在准备文件权限、引用及上传副本…");
        items = await CompletePdmCheckInDependenciesAsync(items, projectId, cancellationToken);
        if (items.Any(item => item.Node.ReferenceWarnings.Count > 0))
            await apiClient.RequireReferenceWarningsSupportAsync(cancellationToken);
        ValidateHistoricalReferenceScope(currentTree, items);
        ValidateHistoricalRepairProofs(items, projectId, cancellationToken);
        ValidateBatchFileNames(items);
        // The dialog can retain an earlier tree while the task pane refreshes asynchronously.
        // Resolve references on the tree being submitted, not just on currentTree.
        SynchronizeBatchFileIdentities(items);
        var referenceVersions = new BatchReferenceVersionCache(projectId, checkoutSessionId, apiClient.EditJournalScope, authenticatedUsername);
        await ResolveBatchReferenceVersionsCachedAsync(items, projectId, reportProgress, cancellationToken, referenceVersions);
        var operationNodes = items.SelectMany(item => EnumerateArchiveNodes(item.Node)).Distinct().ToArray();
        var referenceConfigurations = operationNodes.Where(node => node.Kind != CadDocumentKind.Drawing && node.Status != CadReferenceStatus.Virtual
                && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)group.Select(node => node.Configuration).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new BatchCheckInResult();
        archiveWarnings.Clear();
        var originalDocumentPath = (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty;
        var failedItems = new List<BatchOperationItem>();
        var projectReferenceRoot = await apiClient.GetReferenceTreeOrNullAsync(projectId, cancellationToken);
        var projectRootDocumentId = projectReferenceRoot?.DocumentId;
        var selectedRoot = items.FirstOrDefault(candidate => candidate.Depth == 0 && candidate.Node.Kind == CadDocumentKind.Assembly)?.Node;
        var rootIncompatibility = selectedRoot == null
            ? string.Empty
            : BatchProjectRootRule.IncompatibilityReason(
                projectRootDocumentId,
                projectReferenceRoot?.FileName,
                selectedRoot.DocumentId,
                selectedRoot.FileName);
        if (!string.IsNullOrWhiteSpace(rootIncompatibility))
        {
            LogOperation(string.Concat(
                "Batch check-in blocked project root mismatch existing=", projectReferenceRoot?.FileName,
                " selected=", selectedRoot?.FileName));
            throw new InvalidOperationException(rootIncompatibility);
        }
        var orderedItems = items
            .OrderBy(candidate => candidate.Node.Kind == CadDocumentKind.Drawing ? 1 : 0)
            .ThenByDescending(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.Node.Kind == CadDocumentKind.Assembly ? 1 : 0)
            .ThenBy(candidate => candidate.Node.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // A new drawing needs its related model's PLM id during registration. Register every
        // model first, while preserving the child-first order for the later check-in phase.
        var registrationItems = orderedItems
            .OrderBy(candidate => BatchCheckInRegistrationOrderRule.Priority(
                candidate.Node.Kind == CadDocumentKind.Drawing))
            .ToArray();
        var batchId = Guid.NewGuid();
        var reservedDocumentIds = new List<Guid>();
        var originalReadOnly = orderedItems.Where(item => !item.Node.DocumentId.HasValue && item.Node.HistoricalRepair == null)
            .ToDictionary(item => item.Node.FullPath, item => (File.GetAttributes(item.Node.FullPath) & FileAttributes.ReadOnly) != 0,
                StringComparer.OrdinalIgnoreCase);
        var committed = false;
        var commitAttempted = false;
        var registrationCleaned = false;
        var preparedFiles = new Dictionary<string, PreparedArchiveFile>(StringComparer.OrdinalIgnoreCase);
        var initialSaveFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in operationNodes.Where(node => !string.IsNullOrWhiteSpace(node.FullPath)))
            if (!initialSaveFlags.ContainsKey(node.FullPath))
                initialSaveFlags[node.FullPath] = FindLoadedDocument(node.FullPath)?.GetSaveFlag() == true;

        try
        {
            var preparationFailures = new List<string>();
            ArchiveNativeCopyWorkspace.ValidateLoadedImports(application,
                orderedItems.Where(item => item.Node.Kind == CadDocumentKind.Assembly).Select(item => item.Node.FullPath));
            // 登记仅预留本批次身份；全部检查通过前不上传文件、不产生正式图档或版本。
            var submissionPaths = new HashSet<string>(
                orderedItems.Select(item => item.Node?.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);
            var nativePropertyCards = DiscoverNativePropertyCards();
            var inspectedProperties = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var inspectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localFailures = new List<string>();
            for (var index = 0; index < orderedItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = orderedItems[index].Node;
                reportProgress?.Invoke(index, orderedItems.Length, node.FileName, "存档前检查：正在核对文件、引用结构和属性…");
                try
                {
                    PdmApiClient.ValidateCheckInReferences(node, submissionPaths);
                    var hash = ComputeRegistrationFileHash(node);
                    inspectedProperties[node.FullPath] = ReadArchivePreflightProperties(node, nativePropertyCards, referenceConfigurations);
                    if (node.Kind == CadDocumentKind.Drawing) ValidateDrawingModelsSaved(node, initialSaveFlags);
                    if (!string.Equals(hash, ComputeRegistrationFileHash(node), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("文件在检查期间发生变化，请保存并重新检查。");
                    inspectedHashes[node.FullPath] = hash;
                }
                catch (Exception exception)
                {
                    LogDiagnostic("Archive local preflight." + node.FileName, exception);
                    localFailures.Add(node.FileName + "：" + ArchivePreflightError(exception));
                    if (exception is ArchiveReadAliasConflictException) break;
                }
                await Task.Yield();
            }
            ThrowArchivePreflightFailures(localFailures);
            try { ArchiveBatchJournal.Begin(batchId, checkoutSessionId, apiClient.EditJournalScope, authenticatedUsername); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { throw new InvalidOperationException("无法保存本机存档恢复记录，尚未上传任何文件。请检查本机磁盘空间及缓存目录权限后重试。", error); }
            for (var index = 0; index < registrationItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = registrationItems[index];
                var node = item.Node;
                try
                {
                    PdmApiClient.ValidateCheckInReferences(node, submissionPaths);
                    reportProgress?.Invoke(index, registrationItems.Length, node?.FileName, "阶段2/4：正在准备文件权限…");
                    if (await PrepareBatchCheckInPermissionAsync(item, projectId, cancellationToken, registrationDecisions, batchId, reservedDocumentIds)
                        && node.HistoricalRepair == null)
                        result.PreparedPermissions++;
                    ValidateBatchCheckInNode(node);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("CheckInBatch.Prepare.", node?.FileName), exception);
                    ThrowOnArchiveConnectionFailure(exception);
                    var failure = string.Concat(node?.FileName ?? "未知图档", "：", ArchivePreflightError(exception));
                    preparationFailures.Add(failure);
                    result.Failures.Add(failure);
                    failedItems.Add(item);
                }
            }

            ThrowArchivePreflightFailures(preparationFailures);
            SynchronizeBatchFileIdentities(items);
            // Registration/permission preparation can update file identities after the first
            // reference pass. Recheck the held submission tree before serializing its snapshot.
            await ResolveBatchReferenceVersionsCachedAsync(items, projectId, reportProgress, cancellationToken, referenceVersions);
            SynchronizeBatchFileIdentities(items);

            // Finish every native read/rebuild/copy before the first upload. Drawing loading
            // can change references or a model's save flag, including files inspected earlier.
            var copyFailures = new List<string>();
            for (var index = 0; index < orderedItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = orderedItems[index].Node;
                try
                {
                    BatchDocumentIdentity identity = null;
                    identities?.TryGetValue(node.FullPath, out identity);
                    BatchCheckInPreflight preflight = null;
                    preflights?.TryGetValue(node.FullPath, out preflight);
                    reportProgress?.Invoke(index, orderedItems.Length, node.FileName, "存档前检查：正在准备上传副本并核对工程图…");
                    var prepared = await PrepareBatchCheckInFileAsync(node, cancellationToken, identity,
                        preflight, nativePropertyCards, initialSaveFlags);
                    inspectedProperties.TryGetValue(node.FullPath, out var inspected);
                    preparedFiles[node.FullPath] = new PreparedArchiveFile(node,
                        PreserveInspectedConfigurationProperties(node, prepared.Properties, inspected), prepared.UploadCopyPath,
                        prepared.SourceHash, prepared.Latest, prepared.StoredFile);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    LogDiagnostic("Archive copy preflight." + node.FileName, exception);
                    ThrowOnArchiveConnectionFailure(exception);
                    copyFailures.Add(node.FileName + "：" + ArchivePreflightError(exception));
                    if (exception is ArchiveReadAliasConflictException) break;
                }
            }
            ThrowArchivePreflightFailures(copyFailures);
            // PDM check-in stores the saved native file and its fixed reference
            // versions. It does not rebuild or detach a second copy of the package.
            ValidateBatchSavedState(orderedItems, initialSaveFlags);
            SynchronizeBatchFileIdentities(items);
            await ResolveBatchReferenceVersionsCachedAsync(items, projectId, reportProgress, cancellationToken, referenceVersions);
            SynchronizeBatchFileIdentities(items);
            await PrepareArchiveSnapshotsAsync(preparedFiles.Values, submissionPaths, cancellationToken);

            await PreflightBatchCheckInItemsAsync(
                orderedItems.Where(item => !failedItems.Contains(item)).ToArray(),
                projectId,
                projectReferenceRoot,
                identities,
                drawingReviewWritebackIds,
                submissionPaths,
                reportProgress,
                cancellationToken,
                failedItems,
                result.Failures,
                inspectedProperties,
                preparedFiles, keepEditing, handoffTo);

            ThrowArchivePreflightFailures(result.Failures);
            var changedFiles = orderedItems.Where(item => !string.Equals(inspectedHashes[item.Node.FullPath],
                ComputeRegistrationFileHash(item.Node), StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Node.FileName + "：文件在预检后发生变化，请重新检查。").ToArray();
            ThrowArchivePreflightFailures(changedFiles);
            ValidateBatchSavedState(orderedItems, initialSaveFlags);

            if (ArchiveSession.Current != null)
            {
                ArchiveSession.Current.ReportPreparedFiles(orderedItems);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowArchivePreflightFailures(orderedItems.Where(item => !string.Equals(inspectedHashes[item.Node.FullPath],
                    ComputeRegistrationFileHash(item.Node), StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Node.FileName + "：文件在准备完成后发生变化，已停止本批次，请保存后重新准备。"));
                ValidateBatchSavedState(orderedItems, initialSaveFlags);
                await PreflightBatchCheckInItemsAsync(orderedItems, projectId, projectReferenceRoot, identities,
                    drawingReviewWritebackIds, submissionPaths, reportProgress, cancellationToken,
                    failedItems, result.Failures, inspectedProperties, preparedFiles, keepEditing, handoffTo);
                ThrowArchivePreflightFailures(result.Failures);
            }
            ValidateHistoricalRepairProofs(items, projectId, cancellationToken);
            ArchiveSession.Current?.SetPhase(4, "校验已通过，正在上传本次图档并等待整批提交…");
            var staged = new List<StagedArchiveFile>();
            for (var index = 0; index < orderedItems.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = orderedItems[index];
                var node = item.Node;
                if (failedItems.Contains(item)) continue;
                LogOperation(string.Concat("Batch check-in file start path=", node?.FullPath));
                try
                {
                    if (node.Kind == CadDocumentKind.Assembly)
                    {
                        var failedDescendants = failedItems
                            .Where(failed => BatchCheckInDependencyRule.BlocksParentAssembly(
                                    failed.Node.Kind == CadDocumentKind.Drawing)
                                && failed.Ancestors.Any(ancestor => PathsEqual(ancestor.FullPath, node.FullPath)))
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

                    reportProgress?.Invoke(index, orderedItems.Length, node?.FileName, "阶段4/4：正在检查版本和本地变更…");
                    BatchDocumentIdentity identity = null;
                    identities?.TryGetValue(node.FullPath, out identity);
                    Guid? drawingReviewWritebackId = null;
                    if (node.DocumentId.HasValue && drawingReviewWritebackIds != null
                        && drawingReviewWritebackIds.TryGetValue(node.DocumentId.Value, out var writebackId))
                    {
                        drawingReviewWritebackId = writebackId;
                    }
                    staged.Add(await UploadPreparedArchiveFileAsync(
                        preparedFiles[node.FullPath],
                        projectId,
                        changeNote.Trim(),
                        IsIndependentArchiveRoot(node, projectReferenceRoot, item.Depth),
                        cancellationToken,
                        identity,
                        drawingReviewWritebackId,
                        (uploaded, total) => reportProgress?.Invoke(
                            index,
                            orderedItems.Length,
                            node.FileName,
                            string.Concat(
                                "阶段4/4：正在上传 ",
                                total <= 0 ? 0 : Math.Min(100, uploaded * 100 / total),
                                "%…")),
                        submissionPaths, batchId, keepEditing, handoffTo));
                    reportProgress?.Invoke(index + 1, orderedItems.Length, node.FileName, "文件已暂存，等待整批提交。");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedItems.Add(item);
                    LogDiagnostic(string.Concat("CheckInBatch.", node?.FileName), exception);
                    throw new InvalidOperationException("整批存档已停止，本批次尚未产生存档版本：" + node?.FileName + "：" + ArchivePreflightError(exception), exception);
                }
            }
            // E03: all uploads and reference snapshots are frozen. Later local edits
            // remain in the working file and must not invalidate this immutable batch.
            LogOperation("整批存档提交编号：" + batchId);
            reportProgress?.Invoke(0, 0, string.Empty, "阶段4/4：全部" + orderedItems.Length + "个文件暂存完成，正在提交整批版本，等待服务器确认…");
            commitAttempted = true;
            ArchiveBatchReceiptDto receipt;
            try { receipt = await apiClient.CommitArchiveBatchAsync(batchId, checkoutSessionId, staged.Select(file => file.Request).ToArray(), cancellationToken); }
            catch (Exception error)
            {
                if (error.Data["ArchiveBatchNotCommitted"] is bool notCommitted && notCommitted) commitAttempted = false;
                throw;
            }
            committed = true;
            foreach (var warning in archiveWarnings) result.BomWarnings.Add(warning);
            if (staged.Any(file => file.Node.StructureIndexPending))
                result.BomWarnings.Add("原生图档已存档，部分结构索引待补全；已有BOM保留，不按未读出的结构删除物料。");
            else if (receipt.FollowupPending)
                result.BomWarnings.Add("图档版本已整批保存，BOM及通知正在服务器后台更新；无需重复存档，稍后刷新即可。");
            var synchronizedFiles = 0;
            foreach (var file in staged)
            {
                reportProgress?.Invoke(synchronizedFiles, staged.Count, file.Node.FileName, "阶段4/4：服务器已提交，正在同步本地图档状态…");
                foreach (var notice in file.Node.ReferenceWarnings)
                    result.BomWarnings.Add("引用提示：" + file.Node.FileName + "；" + notice);
                var saved = receipt.Results.Single(entry => entry.Document.Id == file.Node.DocumentId);
                var beforeVersion = file.Latest?.DrawingVersion ?? file.Node.DrawingVersion;
                try
                {
                    ApplyCheckedInDocumentToMatchingInstances(file.Node, saved.Document, saved.Version);
                    UpdateBatchDocumentInstances(file.Node, operationNodes);
                    var warning = file.Node.HistoricalRepair == null && !file.Node.NativeIsolationOnly && !file.NativeIsolation
                        ? RememberControlledVersionIdentityAndManifest(file.Node.FullPath, saved.Document.Id, projectId, saved.Version ?? file.Latest) : null;
                    var laterChanges = FindLoadedDocument(file.Node.FullPath)?.GetSaveFlag() == true
                        || File.Exists(file.Node.FullPath) && !string.Equals(file.SourceHash, ComputeFileHash(file.Node.FullPath), StringComparison.OrdinalIgnoreCase);
                    if (file.Node.HistoricalRepair == null && keepEditing && laterChanges && (saved.Version ?? file.Latest) is DocumentVersionDto continuedBase)
                    {
                        // 基准推进到已提交的固定副本；保留随后变化的真实文件，不把它标成已存档内容。
                        if (PdmDocumentIdentityStore.TryWriteControlledVersion(file.Node.FullPath, saved.Document.Id, projectId,
                            continuedBase.Id, continuedBase.Revision?.Display, file.SourceHash, file.SourceLength))
                            warning = null;
                    }
                    if (string.IsNullOrWhiteSpace(warning) && !laterChanges && saved.Document.CheckoutSessionId != checkoutSessionId)
                    {
                        if (file.Node.HistoricalRepair == null && !file.Node.NativeIsolationOnly && (file.Node.Role != CadReferenceRole.ExternalFileDependency
                            || PdmDocumentIdentityStore.IsControlledWorkspacePath(file.Node.FullPath)))
                            ProtectLoadedDocument(file.Node.FullPath, controlledWorkspaceOnly: true);
                    }
                    else if (!string.IsNullOrWhiteSpace(warning)) result.BomWarnings.Add(file.Node.FileName + "：" + warning);
                    if (laterChanges)
                    {
                        file.Node.IsModifiedInSolidWorks = FindLoadedDocument(file.Node.FullPath)?.GetSaveFlag() == true;
                        file.Node.WorkState = CadWorkState.PendingCheckIn;
                        result.BomWarnings.Add(file.Node.FileName + "：固定副本已存档，之后的本地修改已保留，尚未包含在本次版本中。");
                    }
                    if (saved.Document.CheckoutSessionId == checkoutSessionId)
                    {
                        RememberLocalEdit(file.Node, saved.Document);
                        result.Successes.Add(file.Node.FileName + "：存档后继续保留编辑权限");
                    }
                    else if (!string.IsNullOrWhiteSpace(handoffTo)) result.Successes.Add(file.Node.FileName + "：已存档并交接给" + handoffTo + "，预留5分钟接手");
                }
                catch (Exception syncError)
                {
                    LogDiagnostic("整批存档已成功，本机状态同步失败：" + file.Node.FileName, syncError);
                    result.BomWarnings.Add(file.Node.FileName + "：服务器已整批存档成功，本机状态同步失败，请刷新设计树后核对。");
                }
                var confirmedVersion = saved.Document.DrawingVersion ?? saved.Version?.DrawingVersion ?? file.Node.DrawingVersion;
                ArchiveSession.Current?.TrackFile(file.Node,
                    file.Node.HistoricalRepair != null ? "历史资料已补齐（服务器已确认，不计工程变更）"
                        : saved.VersionCreated ? (file.Node.ReferenceWarnings.Count > 0 ? "已存档（有引用提示）" : "已存档（服务器已确认）") + (string.IsNullOrWhiteSpace(beforeVersion) ? string.Empty : "：" + beforeVersion + " → " + confirmedVersion)
                        : "未变更，已按选择处理权限（不升版）",
                    (confirmedVersion ?? "版本待刷新") + " / " + (saved.Document.PublishedVersion ?? "未发布"));
                if (saved.VersionCreated)
                {
                    if (file.Node.HistoricalRepair != null) result.HistoricalRepairs++;
                    else result.CreatedVersions++;
                    result.Successes.Add(file.Node.FileName + (file.Node.HistoricalRepair != null
                        ? "：历史资料已补齐（服务器已确认，不计工程变更）" : file.ReferenceSnapshot?.HasPendingStructureIndex == true
                        ? "：原生文件已存档，结构待补全" : "：已整批存档"));
                    if (file.ReferenceSnapshot?.HasPendingStructureIndex == true)
                        result.BomWarnings.Add(file.Node.FileName + "：结构或图模关联待补全；已有BOM保留，补全前不能正式发布。");
                }
                else { result.UnchangedFiles++; result.Successes.Add(file.Node.FileName + (file.NativeIsolation
                    ? "：独立固化副本已保存，原工程版本不升版" : keepEditing ? "：无变更，继续编辑" : !string.IsNullOrWhiteSpace(handoffTo) ? "：无变更，已交接编辑权限（不升版）" : "：无变更，已结束编辑（不升版）")); }
                reportProgress?.Invoke(++synchronizedFiles, staged.Count, string.Empty, "阶段4/4：本地图档状态已同步：" + file.Node.FileName);
            }
        }
        finally
        {
            foreach (var file in preparedFiles.Values) file.Node.NativeIsolationOnly = false;
            foreach (var file in preparedFiles.Values) DeleteCheckInUploadCopy(file.UploadCopyPath);
            archiveReadAliases.Clear();
            if (!committed)
                foreach (var item in orderedItems)
                    ArchiveSession.Current?.TrackFile(item.Node, commitAttempted
                        ? "结果待核实，请勿重复存档" : "未提交，本批未生成存档版本");
            if (!committed && reservedDocumentIds.Count > 0)
            {
                if (!commitAttempted)
                {
                    using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        try { await apiClient.CancelArchiveRegistrationAsync(batchId, checkoutSessionId, cleanup.Token); registrationCleaned = true; }
                        catch (Exception error) { LogDiagnostic("整批存档暂存登记清理，项目未新增图档", error); }
                    }
                }
                if (registrationCleaned && !commitAttempted)
                    ClearCanceledArchiveIdentities(items.SelectMany(item => EnumerateArchiveNodes(item.Node))
                        .Concat(EnumerateArchiveNodes(currentTree)), reservedDocumentIds);
                if (!commitAttempted)
                {
                    foreach (var state in originalReadOnly)
                    {
                        try { SetFileReadOnly(state.Key, state.Value, "未提交批次恢复操作前属性；批次=" + batchId); }
                        catch (Exception error) { LogDiagnostic("存档失败后恢复文件属性；批次=" + batchId + "；路径=" + state.Key, error); }
                    }
                }
                PluginDiagnosticLog.Record("批次", commitAttempted
                    ? "整批存档结果待核对，已保留批次恢复记录。连接恢复后自动核对，请勿重复提交。批次：" + batchId
                    : "整批存档未成功，新图档未登记到项目，本地文件保留。批次：" + batchId);
            }
            if (committed || !commitAttempted && (reservedDocumentIds.Count == 0 || registrationCleaned))
            {
                try { ArchiveBatchJournal.Complete(batchId); }
                catch (Exception error) { LogDiagnostic("存档批次记录清理：" + batchId, error); }
            }
            RestoreOriginalDocumentAfterCheckIn(originalDocumentPath, (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty);
            taskPaneControl.SetTree(currentTree);
        }

        return result;
    }

    private void ClearCanceledArchiveIdentities(IEnumerable<CadTreeNode> nodes, IReadOnlyCollection<Guid> documentIds)
    {
        var canceled = new HashSet<Guid>(documentIds);
        foreach (var node in nodes.Distinct().Where(node => node.DocumentId.HasValue && canceled.Contains(node.DocumentId.Value)))
        {
            node.DocumentId = null; node.ProvenanceDocumentId = null; node.ProvenanceProjectId = null;
            node.IsExternalProvenance = false; node.Revision = node.CurrentRevision = node.LatestRevision = string.Empty;
            node.CheckedOutBy = null; node.CheckedOutAt = null; node.CheckoutSessionId = null;
            node.CheckoutSessionLost = false;
            node.CheckoutMachine = string.Empty; node.CheckoutLastHeartbeatAt = null; node.WorkState = CadWorkState.None;
            node.StoredVersionStateKnown = true; node.HasStoredVersion = false;
        }
        lock (checkoutDocumentSync)
            foreach (var id in canceled) { activeCheckoutDocumentIds.Remove(id); pendingArchiveDocumentIds.Remove(id); }
    }

    private async Task PreflightBatchCheckInItemsAsync(
        IReadOnlyList<BatchOperationItem> orderedItems,
        Guid projectId,
        DocumentReferenceNodeDto projectReferenceRoot,
        IReadOnlyDictionary<string, BatchDocumentIdentity> identities,
        IReadOnlyDictionary<Guid, Guid> drawingReviewWritebackIds,
        ICollection<string> plannedPaths,
        Action<int, int, string, string> reportProgress,
        CancellationToken cancellationToken,
        ICollection<BatchOperationItem> failedItems = null,
        ICollection<string> failureMessages = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> inspectedProperties = null,
        IReadOnlyDictionary<string, PreparedArchiveFile> preparedFiles = null, bool keepEditing = false, string handoffTo = null)
    {
        ArchiveSession.Current?.SetPhase(3, "正在逐项校验本次存档的文件、引用和服务器条件…");
        var failures = new List<string>();
        for (var index = 0; index < orderedItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = orderedItems[index];
            var node = item.Node;
            try
            {
                reportProgress?.Invoke(index, orderedItems.Count, node?.FileName, "阶段3/4：正在执行存档前校验…");
                BatchDocumentIdentity identity = null;
                identities?.TryGetValue(node.FullPath, out identity);
                Guid? drawingReviewWritebackId = null;
                if (node.DocumentId.HasValue && drawingReviewWritebackIds != null
                    && drawingReviewWritebackIds.TryGetValue(node.DocumentId.Value, out var writebackId))
                    drawingReviewWritebackId = writebackId;
                var isProjectRoot = IsIndependentArchiveRoot(node, projectReferenceRoot, item.Depth);
                PreparedArchiveFile prepared = null;
                preparedFiles?.TryGetValue(node.FullPath, out prepared);
                await apiClient.PreflightCheckInAsync(
                    node.DocumentId.Value,
                    projectId,
                    node,
                    prepared?.Properties ?? (inspectedProperties != null && inspectedProperties.TryGetValue(node.FullPath, out var properties)
                        ? properties : new Dictionary<string, string>()),
                    checkoutSessionId,
                    isProjectRoot,
                    identity?.DrawingNumber,
                    identity?.Name,
                    cancellationToken,
                    drawingReviewWritebackId,
                    plannedPaths,
                    HistoricalArchiveBase(node),
                    prepared?.ReferenceSnapshot, prepared?.NativeIsolationKeepEditing ?? (node.HistoricalRepair == null && keepEditing),
                    node.NativeIsolationOnly ? null : node.HistoricalRepair == null ? handoffTo : null);
                LogOperation(string.Concat("Batch check-in preflight passed path=", node.FullPath));
                reportProgress?.Invoke(index + 1, orderedItems.Count, node.FileName, "阶段3/4：存档前校验已通过");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDiagnostic(string.Concat("CheckInBatch.Preflight.", node?.FileName), exception);
                ThrowOnArchiveConnectionFailure(exception);
                failures.Add(string.Concat(node?.FileName ?? "未知图档", "：", ArchivePreflightError(exception)));
                failedItems?.Add(item);
                failureMessages?.Add(failures[failures.Count - 1]);
            }
        }

        if (failures.Count > 0 && failedItems == null)
            throw new InvalidOperationException(string.Concat(
                "提交存档前校验未通过，未上传任何文件：\r\n",
                string.Join("\r\n", failures.Take(8)),
                failures.Count > 8 ? "\r\n……" : string.Empty,
                "\r\n请修正以上问题后重新提交。"));
    }

    private static void ThrowArchivePreflightFailures(IEnumerable<string> failures, bool uploadStarted = false)
    {
        var issues = failures.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().ToArray();
        if (issues.Length > 0)
            throw new InvalidOperationException((uploadStarted
                ? "上传后复核未通过，本批次尚未产生存档版本，已停止整批提交。请处理以下问题后重试：\r\n"
                : "存档前检查未通过，本次尚未上传任何文件。请处理以下问题后重试：\r\n") + string.Join("\r\n", issues));
    }

    private static string ArchivePreflightError(Exception exception)
    {
        if (IsArchiveConnectionFailure(exception))
            return "PLM服务器连接中断、响应超时或服务暂时不可用。请检查网络及服务器状态，恢复后重新整体存档。";
        if (exception is InvalidOperationException && exception.Message.Any(character => character >= '\u4e00' && character <= '\u9fff'))
            return exception.Message;
        if (exception is IOException && (exception.Message.StartsWith("图档在存档期间", StringComparison.Ordinal)
            || exception.Message.StartsWith("缺少存档前的文件校验记录", StringComparison.Ordinal))) return exception.Message;
        if (exception is IOException && exception.Message.Contains("已获取PLM编辑权限，但SolidWorks仍以只读方式打开。"))
            return "需要保存本地修改，但SolidWorks文档仍处于只读状态，尚未提交。请先将修改另存到可写位置，或取得源文件写入权限并保存后重试；原窗口和修改均已保留。";
        if (exception is IOException || exception is UnauthorizedAccessException)
            return "本地文件无法读取或被占用，请检查文件路径和读写权限后重试。";
        return "图档信息读取或校验失败，请保存文件、刷新设计树后重试；技术详情已写入诊断日志。";
    }

    private static bool IsArchiveConnectionFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current is HttpRequestException || current is PdmApiClient.ServiceUnavailableException || current is System.Net.WebException
                || current is TimeoutException || current is TaskCanceledException) return true;
        return false;
    }

    private static void ThrowOnArchiveConnectionFailure(Exception exception)
    {
        if (IsArchiveConnectionFailure(exception))
            throw new InvalidOperationException("已停止存档前检查，本次尚未上传任何文件，也未产生存档版本。"
                + ArchivePreflightError(exception), exception);
    }

    private IReadOnlyDictionary<string, string> ReadArchivePreflightProperties(CadTreeNode node,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards,
        IReadOnlyDictionary<string, IReadOnlyList<string>> referenceConfigurations = null)
    {
        if (node.Role == CadReferenceRole.ExternalFileDependency || node.HistoricalRepair != null) return new Dictionary<string, string>();
        var document = FindArchiveReadDocument(node);
        var opened = false;
        var readAlias = document != null && !PathsEqual(document.GetPathName(), node.FullPath);
        try
        {
            if (document == null)
            {
                document = OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind),
                    node.Kind == CadDocumentKind.Drawing ? string.Empty : node.Configuration, true);
                opened = PathsEqual(document?.GetPathName(), node.FullPath);
                if (!opened) { ValidateArchiveReadAlias(node, document); readAlias = true; }
            }
            if (!opened && HasPendingArchiveSave(document, node) && !(node.Kind == CadDocumentKind.Drawing && preloadedOwnedDrawings.Contains(node.FullPath)))
                throw new InvalidOperationException("图档尚未保存，请保存后重新检查。");
            if (node.Kind == CadDocumentKind.Drawing) scanner.UpdateDrawingReferences(document, node);
            if (node.Kind != CadDocumentKind.Drawing && (string.IsNullOrWhiteSpace(node.Configuration)
                || document.GetConfigurationByName(node.Configuration) == null))
                throw new InvalidOperationException("找不到总装引用的配置：" + node.Configuration);
            var properties = ReadArchiveProperties(document, node, nativePropertyCards, referenceConfigurations);
            if (readAlias)
            {
                ValidateArchiveReadAlias(node, document);
                archiveReadAliases[node.FullPath] = document;
            }
            return properties;
        }
        finally
        {
            if (opened && !CloseArchiveReadDocument(document))
                throw new InvalidOperationException("属性检查后无法关闭临时图档，请关闭该图档后重试。");
        }
    }

    private void ValidateDrawingModelsSaved(CadTreeNode drawing, IReadOnlyDictionary<string, bool> initialSaveFlags)
    {
        foreach (var model in drawing.ReferencedModels)
        {
            if (FindLoadedDocument(model.FullPath)?.GetSaveFlag() != true) continue;
            var initialKnown = initialSaveFlags != null && initialSaveFlags.TryGetValue(model.FullPath, out _);
            var becameDirty = initialKnown && !initialSaveFlags[model.FullPath];
            PluginDiagnosticLog.Record("存档提醒", "工程图参考模型有未保存标记：工程图=" + drawing.FileName + "；模型=" + model.FileName
                + "；路径=" + model.FullPath + "；配置=" + model.Configuration + "；检查期间变为未保存=" + becameDirty);
            archiveWarnings.Add("参考模型“" + model.FileName + "”的未保存状态已保留；仅新增工程图时沿用已确认的模型版本，模型内存修改不计入本次单独工程图存档。");
            // Reference-only models keep their in-memory edits. The drawing records the
            // resolved stored model version; models selected for upload are checked separately.
        }
    }

    private void ValidateBatchSavedState(IReadOnlyList<BatchOperationItem> items, IReadOnlyDictionary<string, bool> initialSaveFlags, bool uploadStarted = false)
    {
        var failures = new List<string>();
        foreach (var node in DistinctBatchItems(items).Select(item => item.Node))
        {
            if (node.Kind == CadDocumentKind.Drawing) continue; // Drawing copies contain their rebuilt display data.
            if (!HasPendingArchiveSave(FindLoadedDocument(node.FullPath), node)) continue;
            var becameDirty = initialSaveFlags != null && initialSaveFlags.TryGetValue(node.FullPath, out var dirty) && !dirty;
            failures.Add(node.FileName + "：" + (becameDirty ? "检查期间模型变为未保存状态，可能由加载引用、配置切换或重建引起。" : "模型存在未保存修改。")
                + "请核对并保存模型后重新整体存档。配置：" + node.Configuration + "；路径：" + node.FullPath);
        }
        ThrowArchivePreflightFailures(failures.Distinct().ToArray(), uploadStarted);
    }

    private bool HasPendingArchiveSave(IModelDoc2 document, CadTreeNode node)
    {
        if (document?.GetSaveFlag() != true) return false;
        if (!node.IsModifiedInSolidWorks && document.IsOpenedReadOnly())
        {
            archiveWarnings.Add(node.FileName + "：只读加载产生的待保存状态已保留，本次采用已保存文件内容，不额外获取权限或升版。");
            return false;
        }
        return true;
    }

    private CadTreeNode ReadSuppressedAssemblyStructure(CadTreeNode node)
    {
        if (IsSolidWorksCommandActive())
            throw new InvalidOperationException("请先完成SolidWorks当前操作，再读取压缩子装配体并存档。");
        if (string.IsNullOrWhiteSpace(node.Configuration))
            throw new InvalidOperationException("压缩子装配体缺少引用配置，请刷新设计树后重试：" + node.FileName);
        var document = FindLoadedDocument(node.FullPath);
        var opened = false;
        try
        {
            if (document == null)
            {
                var nameCollision = (application.GetDocuments() as Array)?.OfType<IModelDoc2>().Any(model =>
                    string.Equals(Path.GetFileName(model.GetPathName()), node.FileName, StringComparison.OrdinalIgnoreCase)
                    && !PathsEqual(model.GetPathName(), node.FullPath)) == true;
                if (nameCollision) throw new InvalidOperationException("SolidWorks已打开其他路径的同名图档，请先关闭该同名文件后重试：" + node.FileName);
                document = OpenDocumentInvisiblyForBatch(node.FullPath, (int)swDocumentTypes_e.swDocASSEMBLY, node.Configuration, true);
                if (!PathsEqual(document.GetPathName(), node.FullPath))
                    throw new InvalidOperationException("SolidWorks读取了其他路径的同名装配体，已停止存档：" + node.FileName);
                opened = true;
            }
            if (document.GetSaveFlag())
                throw new InvalidOperationException("子装配体存在未保存修改，请先保存后再整体存档：" + node.FileName);
            if (!string.Equals(document.ConfigurationManager?.ActiveConfiguration?.Name, node.Configuration, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("子装配体当前打开配置与总装引用配置不同，请关闭该子装配体窗口后重试：" + node.FileName + "，引用配置：" + node.Configuration);
            var structure = new SolidWorksReferenceTreeScanner(application).ScanDocument(document, true);
            ResolveArchiveFileLocations(structure);
            RestorePersistedDocumentIdentities(structure);
            PluginDiagnosticLog.Record("结构", "已只读读取压缩子装配体：" + node.FileName + "，配置：" + node.Configuration
                + "，直接子件：" + structure.Children.Count(child => child.Kind != CadDocumentKind.Drawing) + "；总装压缩状态保持不变。");
            return structure;
        }
        finally
        {
            if (opened && !CloseBatchOpenedDocument(document))
                throw new InvalidOperationException("读取压缩结构后无法关闭临时图档，已停止存档：" + node.FileName);
        }
    }

    private async Task ReadMissingSuppressedStructuresAsync(CadTreeNode root, CancellationToken token)
    {
        if (!EnumerateCadNodes(root).Any(node =>
            (!string.IsNullOrWhiteSpace(node.SuppressedLocalStructureSha256) && !SuppressedAssemblyLocalStructure.CanUse(node))
            || (SuppressedAssemblyArchive.IsUnexpanded(node)
                && !SuppressedAssemblyArchive.CanReuse(node) && !SuppressedAssemblyLocalStructure.CanUse(node)))) return;
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var progress = new BatchProgressDialog(0) { Text = "存档前结构检查" })
        {
            if (!progress.IsSessionProxy) progress.CancelRequested += (_, __) => cancellation.Cancel();
            progress.Show(taskPaneControl);
            await SuppressedAssemblyLocalStructure.ExpandAsync(root, async node =>
            {
                progress.SetStage("正在只读检查压缩子装配体：" + node.FileName);
                progress.Refresh();
                await Task.Yield();
                cancellation.Token.ThrowIfCancellationRequested();
                try { return ReadSuppressedAssemblyStructure(node); }
                catch (Exception exception) when (!(exception is OperationCanceledException))
                { LogDiagnostic("压缩结构读取." + node.FileName, exception); throw new InvalidOperationException(node.FileName + "：" + ArchivePreflightError(exception), exception); }
            }, cancellation.Token);
        }
    }

    private async Task ResolveSuppressedAssemblyReferencesAsync(CadTreeNode root, CancellationToken cancellationToken)
    {
        var nodes = EnumerateCadNodes(root).Where(node => SuppressedAssemblyArchive.IsUnexpanded(node)
            || node.Kind == CadDocumentKind.Assembly && node.DocumentId.HasValue && !node.IsModifiedInSolidWorks
                && node.Children.Any(child => child.Kind != CadDocumentKind.Drawing && !child.DocumentId.HasValue
                    && child.Status != CadReferenceStatus.Virtual && !IsSolidWorksTemporaryVirtualComponentPath(child.FullPath))).ToArray();
        var histories = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveSession.Current?.ReportFile(completed++, nodes.Length, node.FileName, "正在核对压缩装配体的历史版本和本地文件…");
            node.SuppressedArchiveVersion = null;
            node.SuppressedArchiveSha256 = null;
            if (!node.DocumentId.HasValue || node.IsModifiedInSolidWorks || !File.Exists(node.FullPath)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!histories.TryGetValue(node.DocumentId.Value, out var history))
                histories[node.DocumentId.Value] = history = await apiClient.GetVersionsAsync(node.DocumentId.Value, cancellationToken);
            if (!hashes.TryGetValue(node.FullPath, out var sha256))
                hashes[node.FullPath] = sha256 = await Task.Run(() => ArchiveFileFingerprint.Read(node.FullPath, cancellationToken), cancellationToken);
            var matches = history.Where(version => version.ReferenceSnapshot?.DocumentId == node.DocumentId
                && VersionMatchesLocalFile(version, node.FullPath, sha256) && SuppressedAssemblyArchive.MatchesConfiguration(node, version)
                && SuppressedAssemblyArchive.HasCompleteReferences(version.ReferenceSnapshot)).ToArray();
            var matching = matches.FirstOrDefault(version => version.Id == node.OpenedVersionId)
                ?? matches.FirstOrDefault(version => version.Revision?.Display == node.CurrentRevision)
                ?? matches.FirstOrDefault();
            if (matching?.Revision == null) continue;
            node.SuppressedArchiveVersion = matching;
            node.SuppressedArchiveSha256 = sha256;
            if (!SuppressedAssemblyArchive.IsUnexpanded(node)) node.SuppressedLocalStructureSha256 = sha256;
            node.CurrentRevision = matching.Revision.Display;
            node.OpenedVersionId = matching.Id;
            PluginDiagnosticLog.Record("引用", $"压缩子装配体沿用已存档版本：{node.FileName}，{node.CurrentRevision}，无需重复存档。");
            if (matching.ReferenceSnapshot.Children == null || matching.ReferenceSnapshot.Children.Count == 0)
                PluginDiagnosticLog.Record("提醒", $"{node.FileName}的历史版本{node.CurrentRevision}未记录子结构，本次保留原引用；如需补齐结构，请在总装确认采用已补齐的新版本后存档。");
        }
    }

    private async Task ResolveBatchReferenceVersionsAsync(
        IReadOnlyList<BatchOperationItem> items,
        Guid projectId,
        Action<int, int, string, string> reportProgress,
        CancellationToken cancellationToken) => await ResolveBatchReferenceVersionsCachedAsync(items, projectId, reportProgress, cancellationToken,
            new BatchReferenceVersionCache(projectId, checkoutSessionId, apiClient.EditJournalScope, authenticatedUsername));

    private async Task ResolveBatchReferenceVersionsCachedAsync(
        IReadOnlyList<BatchOperationItem> items,
        Guid projectId,
        Action<int, int, string, string> reportProgress,
        CancellationToken cancellationToken,
        BatchReferenceVersionCache referenceVersions)
    {
        cancellationToken.ThrowIfCancellationRequested();
        referenceVersions.ValidateScope(projectId, checkoutSessionId, apiClient.EditJournalScope, authenticatedUsername);
        ValidateHistoricalRepairProofs(items, projectId, cancellationToken);
        // A save/rebuild can change file contents without an immediately observable mtime.
        // Re-read content at each checkpoint; reuse a hash only within this resolution pass.
        var verifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plannedPaths = new HashSet<string>(items.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        // A previously archived component can be present in this subproject even when the
        // source assembly's local file has no binding for this project's document ID.
        var unbound = items.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Where(node => (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
                && node.Status != CadReferenceStatus.Virtual && !IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
                && !node.DocumentId.HasValue
                && !string.IsNullOrWhiteSpace(node.FullPath)
                && !plannedPaths.Contains(node.FullPath)
                && File.Exists(node.FullPath))
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unbound.Length > 0)
        {
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId, cancellationToken);
            foreach (var group in unbound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = group.First();
                if (PdmDocumentIdentityStore.TryReadLocalMetadata(group.Key, out var localMetadata)
                    && localMetadata.ProjectId.HasValue && localMetadata.ProjectId != projectId)
                    throw new InvalidOperationException($"引用文件{first.FileName}仍绑定其他项目，不能直接改归属。请先复制为当前项目副本，或从当前项目工作区获取。");
                var candidates = projectDocuments.Where(document =>
                    string.Equals(document.FileName, first.FileName, StringComparison.OrdinalIgnoreCase)
                    && document.Kind == (int)first.Kind).ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException($"引用文件{first.FileName}尚未登记到当前子项目。请将该子件纳入本次存档，或从当前子项目工作区获取后再提交装配体。");
                var history = await referenceVersions.GetHistoryAsync(candidates[0].Id, apiClient, cancellationToken);
                var fileHash = await Task.Run(() => ArchiveFileFingerprint.Read(group.Key, cancellationToken), cancellationToken);
                verifiedHashes[group.Key] = fileHash;
                var matching = history.FirstOrDefault(version => version.MatchesFile(fileHash));
                if (string.IsNullOrWhiteSpace(matching?.Revision))
                    throw new InvalidOperationException($"当前子项目中的同名图档{first.FileName}与装配体引用的本地文件内容不同。请核对是否应复用，需独立修改时先另编号再存档。");
                foreach (var node in group)
                {
                    node.DocumentId = candidates[0].Id;
                    node.CurrentRevision = matching.Revision;
                    node.OpenedVersionId = matching.Id;
                }
                LogOperation($"Batch reference matched current project path={group.Key} document={candidates[0].Id} revision={matching.Revision}");
                PluginDiagnosticLog.Record("引用", $"已核对当前子项目的受控图档：{first.FileName}，版本{matching.Revision}。");
            }
        }
        var references = items.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Where(node => node.Kind != CadDocumentKind.Drawing
                && node.Status != CadReferenceStatus.Virtual && !IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
                && node.DocumentId.HasValue
                && !plannedPaths.Contains(node.FullPath))
            .Distinct()
            .ToArray();
        var documentIds = references.Select(node => node.DocumentId.Value).Distinct().ToArray();
        var versionsByDocument = new Dictionary<Guid, IReadOnlyList<ArchivedReferenceVersion>>();
        const int batchSize = 8;
        for (var offset = 0; offset < documentIds.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress?.Invoke(offset, documentIds.Length, string.Empty, "阶段1/4：正在核对引用文件的已存档版本…");
            var batch = await Task.WhenAll(documentIds.Skip(offset).Take(batchSize).Select(async id => new
            {
                Id = id,
                Versions = await referenceVersions.GetHistoryAsync(id, apiClient, cancellationToken)
            }));
            foreach (var entry in batch) versionsByDocument[entry.Id] = entry.Versions;
        }

        // Repeated component instances share one file hash and one versions request.
        var resolved = new List<Tuple<CadTreeNode, Guid, ArchivedReferenceVersion>>();
        foreach (var group in references.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!verifiedHashes.TryGetValue(group.Key, out var sha256))
                sha256 = await Task.Run(() => ArchiveFileFingerprint.Read(group.Key, cancellationToken), cancellationToken);
            foreach (var node in group)
            {
                var history = versionsByDocument[node.DocumentId.Value];
                var matching = history.FirstOrDefault(version => version.Id == node.OpenedVersionId && version.MatchesFile(sha256, node.FullPath))
                    ?? history.FirstOrDefault(version => version.MatchesFile(sha256, node.FullPath));
                if (string.IsNullOrWhiteSpace(matching?.Revision))
                {
                    throw new InvalidOperationException($"引用文件{node.FileName}无法按文件内容匹配已存档版本，整体存档尚未开始。请将该文件纳入本次提交，或先更新该文件。");
                }
                resolved.Add(Tuple.Create(node, node.DocumentId.Value, matching));
            }
        }
        // Publish every repeated instance after the final await. Navigation refreshes
        // must not clear an earlier instance while another file hash is being read.
        foreach (var entry in resolved)
        {
            if (entry.Item1.DocumentId != entry.Item2)
                throw new InvalidOperationException("引用图档身份在核对期间发生变化，请重新检查后提交：" + entry.Item1.FileName);
            entry.Item1.CurrentRevision = entry.Item1.IsModifiedInSolidWorks ? entry.Item3.Revision + "*" : entry.Item3.Revision;
            entry.Item1.OpenedVersionId = entry.Item3.Id;
            entry.Item1.OpenedRevision = entry.Item3.Revision;
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

    private static bool IsIndependentArchiveRoot(CadTreeNode node, DocumentReferenceNodeDto projectRoot, int depth)
    {
        if (depth != 0 || !node.DocumentId.HasValue
            || (node.Kind != CadDocumentKind.Assembly && node.Kind != CadDocumentKind.Part)) return false;
        if (projectRoot == null) return true;
        var roots = projectRoot.DocumentId.HasValue ? new[] { projectRoot }
            : (projectRoot.Children ?? new List<DocumentReferenceNodeDto>()).ToArray();
        if (roots.Any(root => root.DocumentId == node.DocumentId)) return true;
        bool Contains(DocumentReferenceNodeDto root) => root.DocumentId == node.DocumentId
            || (root.Children ?? new List<DocumentReferenceNodeDto>()).Any(Contains);
        return !Contains(projectRoot);
    }

    private Guid? HistoricalArchiveBase(CadTreeNode node) => node.DocumentId.HasValue
        && historicalEditContexts.TryGetValue(node.DocumentId.Value, out var context)
            ? context.LatestVersion?.Id : null;

    private void ValidateBatchCheckInNode(CadTreeNode node)
    {
        if (node == null || !node.DocumentId.HasValue)
        {
            throw new InvalidOperationException("图档尚未登记到PLM。");
        }

        if (node.IsReadOnlyPreview || IsReadOnlyPreviewPath(node.FullPath))
        {
            DocumentRuleGate.Fail("A09", new InvalidOperationException("只读预览不能提交存档。"));
        }

        if (node.HasBlockingIssue)
        {
            DocumentRuleGate.Fail("B09", new InvalidOperationException("存在缺失引用，不能提交存档。"));
        }

        if (!IsCheckedOutByCurrentUser(node))
        {
            DocumentRuleGate.Fail(string.IsNullOrWhiteSpace(node.CheckedOutBy) ? "A06" : "A05", new InvalidOperationException(string.IsNullOrWhiteSpace(node.CheckedOutBy)
                ? "尚未获取编辑权限。"
                : string.Concat("正在由", ResolveUserDisplayName(node.CheckedOutBy), "编辑。")));
        }

        if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
        {
            throw new FileNotFoundException("本地图档不存在。");
        }

        if (ToSolidWorksDocumentType(node.Kind) == (int)swDocumentTypes_e.swDocNONE && !node.IsImportedSourceFile)
        {
            throw new InvalidOperationException("该文件类型不能提交存档。");
        }
    }

    private async Task<PreparedArchiveFile> PrepareBatchCheckInFileAsync(
        CadTreeNode node,
        CancellationToken cancellationToken,
        BatchDocumentIdentity identity = null,
        BatchCheckInPreflight preflight = null,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards = null,
        IReadOnlyDictionary<string, bool> initialSaveFlags = null)
    {
        var uploadCopyPath = string.Empty;
        IModelDoc2 document = null;
        var openedForBatch = false;
        var readAlias = false;
        var prepared = false;
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
            var referenceMatchesLatest = node.Role == CadReferenceRole.ExternalFileDependency
                || (node.Kind == CadDocumentKind.Assembly ? OwnAssemblyStructureMatches(latest?.ReferenceSnapshot, node)
                    : ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node));
            var indexCompleted = ArchiveIndexWasCompleted(latest?.ReferenceSnapshot, node);
            var referenceChanged = !referenceMatchesLatest;
            document = FindArchiveReadDocument(node);
            readAlias = document != null && !PathsEqual(document.GetPathName(), node.FullPath);
            var hasUnsavedChanges = HasPendingArchiveSave(document, node);
            var localSha256 = preflight != null && preflight.MatchesCurrentFile(node.FullPath)
                ? preflight.LocalSha256
                : await Task.Run(() => ComputeFileHash(node.FullPath), cancellationToken);
            var fileMatchesLatest = !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latest, node.FullPath, localSha256);
            var historicalEditMatchesLatest = HistoricalEditMatchesLatest(node, latest, node.FullPath, localSha256);
            LogOperation(string.Concat(
                "Batch check-in change detection path=", node.FullPath,
                " fileMatches=", fileMatchesLatest,
                " referenceMatches=", referenceMatchesLatest,
                " unsaved=", hasUnsavedChanges));
            if (identity == null && !node.RequiresModelUpdate && !referenceChanged && !indexCompleted
                && (fileMatchesLatest || historicalEditMatchesLatest) && !hasUnsavedChanges)
            {
                if (node.Kind == CadDocumentKind.Drawing) ValidateDrawingModelsSaved(node, initialSaveFlags);
                var unchangedFile = new StoredVersionFile(latest.StorageRelativePath, latest.FileLength, latest.Sha256, latest.PropertySnapshot);
                return new PreparedArchiveFile(node, latest.PropertySnapshot, string.Empty, localSha256, latest, unchangedFile);
            }

            var documentPath = node.FullPath;
            IReadOnlyDictionary<string, string> modelProperties;
            if (node.Role == CadReferenceRole.ExternalFileDependency || node.HistoricalRepair != null)
            {
                if (latest?.ReferenceSnapshot != null && latest.ReferenceSnapshot.Role != (int)CadReferenceRole.ExternalFileDependency && !fileMatchesLatest)
                    throw new InvalidOperationException("上下文图档已有独立存档结构且内容已改变，请先提交该图档，再提交当前总装，避免覆盖原结构：" + node.FileName);
                modelProperties = new Dictionary<string, string>(latest?.PropertySnapshot ?? new Dictionary<string, string>());
            }
            else if (document == null && latest != null && fileMatchesLatest
                && !(node.Kind == CadDocumentKind.Drawing && (referenceChanged || node.RequiresModelUpdate)))
            {
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
                    var openTimer = Stopwatch.StartNew();
                    document = node.Kind == CadDocumentKind.Drawing
                        ? OpenDrawingForArchive(node.FullPath)
                        : OpenDocumentInvisiblyForBatch(
                        node.FullPath,
                        ToSolidWorksDocumentType(node.Kind),
                        node.Kind == CadDocumentKind.Drawing ? string.Empty : node.Configuration ?? string.Empty,
                        readOnly: true);
                    if (!PathsEqual(document?.GetPathName(), node.FullPath))
                    {
                        ValidateArchiveReadAlias(node, document);
                        readAlias = true;
                    }
                    else openedForBatch = true;
                    LogOperation(string.Concat(
                        "Batch check-in SolidWorks open path=", node.FullPath,
                        " elapsedMs=", openTimer.ElapsedMilliseconds));
                }

                documentPath = readAlias ? node.FullPath : document?.GetPathName() ?? string.Empty;
                if (document == null || !PathsEqual(documentPath, node.FullPath))
                {
                    throw new IOException("未能安全加载待提交图档。");
                }
                if (!openedForBatch && HasPendingArchiveSave(document, node)
                    && !(node.Kind == CadDocumentKind.Drawing && preloadedOwnedDrawings.Contains(node.FullPath)))
                {
                    throw new InvalidOperationException(string.Concat(
                        node.FileName,
                        "在SolidWorks中处于未保存状态。为避免未变更图档误升版，请先手动保存确认内容，再提交存档；如无需保留修改，请使用“放弃编辑”。"));
                }

                LogCheckInCheckpoint("properties-start", documentPath);
                modelProperties = ReadArchiveProperties(document, node, nativePropertyCards);
                LogCheckInCheckpoint("properties-end", documentPath);
                if (readAlias) ValidateArchiveReadAlias(node, document);
                if (node.Kind == CadDocumentKind.Drawing)
                {
                    ValidateDrawingModelsSaved(node, initialSaveFlags);
                    // Linked model changes were rebuilt and saved before the batch was frozen.
                    scanner.UpdateDrawingReferences(document, node);
                    ValidateDrawingModelsSaved(node, initialSaveFlags);
                    uploadCopyPath = CreateCheckInUploadCopy(document, documentPath, node.DocumentId.Value);
                    ValidateDrawingModelsSaved(node, initialSaveFlags);
                }
                if (openedForBatch)
                {
                    var temporaryDocument = document;
                    document = null;
                    openedForBatch = false;
                    // CloseDoc releases UI resources, but assembly references keep model data loaded.
                    // Do not require the referenced model to disappear from GetDocuments.
                    if (!CloseArchiveReadDocument(temporaryDocument))
                    {
                        archiveWarnings.Add(node.FileName + "：临时读取窗口未能自动关闭，文档及内容已保留，不影响已准备的存档副本。");
                    }
                }
            }

            var copyTimer = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(uploadCopyPath))
                uploadCopyPath = await Task.Run(
                    () => CreateCheckInUploadCopy(documentPath, node.DocumentId.Value),
                    cancellationToken);
            LogOperation(string.Concat(
                "Batch check-in copy completed path=", node.FullPath,
                " elapsedMs=", copyTimer.ElapsedMilliseconds));
            CheckInUploadHashes.VerifySource(documentPath, localSha256, cancellationToken);
            prepared = true;
            return new PreparedArchiveFile(node, modelProperties, uploadCopyPath, localSha256, latest, null);
        }
        finally
        {
            if (openedForBatch && document != null) CloseArchiveReadDocument(document);
            if (!prepared) DeleteCheckInUploadCopy(uploadCopyPath);
        }
    }

    private async Task<StagedArchiveFile> UploadPreparedArchiveFileAsync(
        PreparedArchiveFile file, Guid projectId, string changeNote, bool isProjectRoot, CancellationToken cancellationToken,
        BatchDocumentIdentity identity, Guid? drawingReviewWritebackId, Action<long, long> reportUploadProgress,
        ICollection<string> plannedPaths, Guid? archiveBatchId = null, bool keepEditing = false, string handoffTo = null)
    {
        var node = file.Node;
        var storedFile = file.StoredFile;
        if (storedFile == null)
        {
            reportUploadProgress?.Invoke(0, Math.Max(1, new FileInfo(file.UploadCopyPath).Length));
            LogOperation(string.Concat("Batch check-in upload start path=", node.FullPath));
            var uploadTimer = Stopwatch.StartNew();
            storedFile = await apiClient.UploadVersionFileAsync(
                projectId,
                file.UploadCopyPath,
                node.DocumentId.Value,
                node.FullPath,
                cancellationToken,
                reportUploadProgress,
                exactCopySourceSha256: node.Kind == CadDocumentKind.Drawing ? null : file.SourceHash,
                expectedSourceSha256: file.SourceHash,
                archiveBatchId: archiveBatchId, checkoutSessionId: checkoutSessionId, frozenUploadSha256: file.UploadCopySha256);
            LogOperation(string.Concat(
                "Batch check-in upload end path=", node.FullPath,
                " elapsedMs=", uploadTimer.ElapsedMilliseconds));
        }
        if (file.NativeIsolationSourceVersionId.HasValue)
            file.ReferenceSnapshot = file.ReferenceSnapshot.WithNativeIsolation(new NativeFileIsolationDto
            {
                SourceVersionId = file.NativeIsolationSourceVersionId.Value, SourceSha256 = file.NativeIsolationSourceSha256,
                RelativePath = storedFile.RelativePath, Length = storedFile.Length, Sha256 = storedFile.Sha256,
                GeometryEvidenceSha256 = file.NativeIsolationEvidence, PolicyVersion = 1
            });
        var request = await apiClient.PrepareArchiveBatchFileAsync(node.DocumentId.Value, projectId, node, BuildHistoricalChangeNote(node, changeNote),
            storedFile, file.Properties, checkoutSessionId, isProjectRoot, identity?.DrawingNumber, identity?.Name, drawingReviewWritebackId,
            HistoricalArchiveBase(node), plannedPaths, cancellationToken, file.ReferenceSnapshot,
            file.NativeIsolationKeepEditing ?? (node.HistoricalRepair == null && keepEditing),
            node.NativeIsolationOnly ? null : node.HistoricalRepair == null ? handoffTo : null);
        return new StagedArchiveFile(node, request, file.SourceHash, file.Latest) { ReferenceSnapshot = file.ReferenceSnapshot,
            SourceLength = file.SourceLength, NativeIsolation = file.NativeIsolationSourceVersionId.HasValue };
    }

    private async void OnCheckInRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        if (taskPaneControl == null || taskPaneControl.IsDisposed) return;
        if (taskPaneControl.InvokeRequired) { taskPaneControl.BeginInvoke((Action)(() => OnCheckInRequested(sender, eventArgs))); return; }
        if (ArchiveSession.Current != null) { ArchiveSession.Current.BringToFront(); return; }
        if (Volatile.Read(ref workspaceOperationInProgress) != 0) { ShowWorkspaceOperationBusy(); return; }
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(GetTaskPaneSynchronizationContext());
        var session = EnsureArchiveSession();
        try
        {
            await session.Ready;
            session.Token.ThrowIfCancellationRequested();
            StartCheckInRequest(eventArgs);
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (Exception error) { LogDiagnostic("打开存档窗口", error); session.Fail(ArchivePreflightError(error)); }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            if (!session.WorkflowStarted)
            {
                session.FinishIfRunning();
                await session.Closed;
                session.Dispose();
            }
        }
    }

    private void StartCheckInRequest(CadTreeNodeEventArgs eventArgs)
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

        if (node.Kind == CadDocumentKind.Drawing && requestedNodes.Length <= 1
            && (!ReferenceEquals(currentTree, node) || IsNewDrawingInLatestPreview(node)))
        {
            try
            {
                node = PrepareDrawingCheckInContext(node);
                requestedNodes = new[] { node };
            }
            catch (Exception exception)
            {
                LogDiagnostic("Prepare drawing check-in", exception);
                ShowError(ArchivePreflightError(exception));
                return;
            }
        }


        if (IsReadOnlyPreviewContext(node))
        {
            ShowError("只读预览不能提交存档。请先使用“获取权限”切换到编辑工作区。 ");
            return;
        }

        if (!standardLibraryArchiveAllowed && currentProjectId.HasValue && !EnsureProjectArchiveAccess(currentProjectId.Value, "提交存档"))
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

        OpenBatchOperationDialog(currentProjectId, BatchOperationKind.CheckIn,
            (requestedNodes.Length > 0 ? requestedNodes : new[] { node })
                .Select(candidate => candidate.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
    }

    private bool BringActiveBatchProgressToFront()
    {
        if (ArchiveSession.Current?.BringToFront() == true) return true;
        var dialog = activeBatchProgressDialog;
        if (dialog == null || dialog.IsDisposed)
        {
            return false;
        }

        if (dialog.WindowState == FormWindowState.Minimized)
        {
            dialog.WindowState = FormWindowState.Normal;
        }

        dialog.TopMost = false;
        if (!dialog.Visible) dialog.Show(taskPaneControl);
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
        if (requestedNodes.Length == 0) requestedNodes = new[] { node };
        LogOperation("收到放弃编辑请求 path=" + node.FullPath + " selected=" + requestedNodes.Length
            + " owner=" + node.CheckedOutBy + " active=" + (application?.ActiveDoc as IModelDoc2)?.GetPathName());
        if (requestedNodes.Any(IsReadOnlyPreviewContext))
        {
            ShowError("只读预览不能更改编辑状态。请先切换到当前工作文件。 ");
            return;
        }

        foreach (var requestedNode in requestedNodes)
        {
            if (!EnsureServerDocument(requestedNode) || !string.IsNullOrWhiteSpace(requestedNode.CheckedOutBy)
                && !EnsureCurrentCheckoutSession(requestedNode, "放弃编辑"))
            {
                return;
            }
        }

        if (!TryBeginWorkspaceOperation("正在放弃编辑并恢复服务器版本"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }
        try
        {
            await DiscardSelectedWorkingFilesAsync(requestedNodes);
            taskPaneControl.SetTree(currentTree);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            EndWorkspaceOperation();
            SchedulePermissionStateRefresh();
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
            // Reading drawing views during preparation replaces reference objects.
            // Synchronize the current views after commit, only for this actual file.
            .Concat(EnumerateArchiveNodes(currentTree).Where(candidate => candidate.DocumentId == documentId
                && PathsEqual(candidate.FullPath, source.FullPath)))
            .Concat(new[] { source })
            .Distinct()
            .ToArray();
        return matches;
    }

    private void ApplyResolvedWorkingVersionToMatchingInstances(CadTreeNode source, DocumentVersionDto version)
    {
        var revision = version?.Revision?.Display ?? string.Empty;
        foreach (var node in EnumerateArchiveNodes(currentTree).Where(candidate => PathsEqual(candidate.FullPath, source.FullPath)
            && (!candidate.DocumentId.HasValue || candidate.DocumentId == source.DocumentId))
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
        var keepEditing = document?.CheckoutSessionId == checkoutSessionId && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
        FinishLocalEdit(documentId);
        foreach (var node in FindMatchingDocumentInstances(source, documentId))
        {
            ApplyCheckoutDocument(node, document);
            if (!keepEditing) ClearCheckoutState(node);
            node.Revision = revision;
            node.CurrentRevision = revision;
            node.WorkState = keepEditing ? CadWorkState.Editable : CadWorkState.None;
            node.IsModifiedInSolidWorks = false;
            node.RequiresModelUpdate = false;
            node.DrawingUpdatePendingReason = null;
            ApplyLatestVersion(node, version);
        }
        lock (checkoutDocumentSync)
        {
            if (keepEditing) activeCheckoutDocumentIds.Add(documentId);
            else activeCheckoutDocumentIds.Remove(documentId);
            pendingArchiveDocumentIds.Remove(documentId);
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

    private static void RememberControlledVersionIdentity(
        string path,
        Guid documentId,
        Guid projectId,
        DocumentVersionDto version)
    {
        if (version == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new IOException("本地文件或已存档版本不存在，无法更新校验记录。");
        var localSha256 = ComputeFileHash(path);
        if (PdmDocumentIdentityStore.MatchesResolvedVersion(path, version.Id, version.Sha256, localSha256))
            return; // Reference relocation did not change this file's archived design or base version.
        if (!VersionMatchesFile(version, localSha256))
            throw new IOException("本地文件在存档期间发生变化，仍有内容尚未存档。");
        if (!PdmDocumentIdentityStore.TryWriteControlledVersion(
            path,
            documentId,
            projectId,
            version.Id,
            version.Revision?.Display,
            localSha256,
            new FileInfo(path).Length))
            throw new IOException("无法写入本地校验记录，请检查磁盘空间和工作区权限。");
    }

    private string RememberControlledVersionIdentityAndManifest(
        string path,
        Guid documentId,
        Guid projectId,
        DocumentVersionDto version)
    {
        try
        {
            RememberControlledVersionIdentity(path, documentId, projectId, version);
            var revision = version.Revision?.Display ?? string.Empty;
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var recorded))
                throw new IOException("无法读取刚写入的本地校验记录。");
            var fullPath = Path.GetFullPath(path);
            completedWorkingArchives[fullPath] = new CompletedWorkingArchive { ProjectId = projectId, DocumentId = documentId,
                VersionId = version.Id, StoredSha256 = version.Sha256, StoredLength = version.FileLength,
                SourceSha256 = recorded.Sha256, SourceLength = recorded.FileLength.Value,
                Username = authenticatedUsername, ServerScope = apiClient?.EditJournalScope };
            foreach (var context in controlledOpenManifests
                .Where(pair => pair.Value?.ProjectId == projectId && IsPathWithinDirectory(fullPath, pair.Key))
                .ToArray())
            {
                foreach (var file in (context.Value.Files ?? new List<ControlledOpenFileDto>())
                    .Where(file => file.DocumentId == documentId))
                {
                    file.VersionId = version.Id;
                    file.Revision = revision;
                    file.Sha256 = recorded.Sha256;
                    file.FileLength = recorded.FileLength.Value;
                }
                if (context.Value.RootDocumentId == documentId)
                {
                    context.Value.RootVersionId = version.Id;
                    context.Value.RootRevision = revision;
                }
            }

            var projectCode = availableProjects.FirstOrDefault(project => project.Id == projectId)?.Code;
            if (string.IsNullOrWhiteSpace(projectCode)) return string.Empty;
            var cachedPath = CacheCheckedInVersionInProjectView(
                WorkspaceSettingsStore.GetWorkspaceRoot(),
                projectCode,
                path,
                documentId,
                projectId,
                version);
            if (string.IsNullOrWhiteSpace(cachedPath))
                throw new IOException("工作区缓存未同步，已有副本可能包含尚未保存到服务器的内容。");
            if (!string.IsNullOrWhiteSpace(cachedPath) && !PathsEqual(cachedPath, path))
            {
                LogOperation(string.Concat("Checked-in file cached in project workspace source=", path, " target=", cachedPath));
            }
            return string.Empty;
        }
        catch (Exception exception)
        {
            // The PLM version is already committed. A local cache failure must not report the
            // completed archive as failed or overwrite an existing working file.
            LogDiagnostic(string.Concat("Cache checked-in file in project workspace.", Path.GetFileName(path)), exception);
            return string.Concat(Path.GetFileName(path), "：服务器存档已完成，但本地文件或校验记录未能同步。请先备份本地文件，再检查磁盘空间及权限并重新获取；若本地有新增修改，请获取编辑权限后重新存档。");
        }
    }

    private static string CacheCheckedInVersionInProjectView(
        string workspaceRoot,
        string projectCode,
        string sourcePath,
        Guid documentId,
        Guid projectId,
        DocumentVersionDto version)
    {
        if (version == null
            || string.IsNullOrWhiteSpace(workspaceRoot)
            || string.IsNullOrWhiteSpace(projectCode)
            || string.IsNullOrWhiteSpace(sourcePath)
            || !File.Exists(sourcePath))
        {
            return string.Empty;
        }

        var source = Path.GetFullPath(sourcePath);
        var projectDirectory = Path.GetFullPath(
            Path.Combine(ControlledWorkspaceManager.ProjectViewDirectory(workspaceRoot, projectCode), "图档缓存", documentId.ToString("N")));
        var target = Path.GetFullPath(Path.Combine(projectDirectory, Path.GetFileName(source)));
        if (!IsPathWithinDirectory(target, projectDirectory))
        {
            return string.Empty;
        }

        var sourceSha256 = ComputeFileHash(source);
        if (!VersionMatchesFile(version, sourceSha256))
        {
            return string.Empty;
        }
        // A managed source already serves the project; avoid creating another bound copy.
        if (IsPathWithinDirectory(source, ControlledWorkspaceManager.ProjectViewDirectory(workspaceRoot, projectCode)))
            return source;

        var created = false;
        var temporaryPath = string.Empty;
        try
        {
            if (!PathsEqual(source, target))
            {
                Directory.CreateDirectory(projectDirectory);
                if (File.Exists(target))
                {
                    if (!string.Equals(ComputeFileHash(target), sourceSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                }
                else
                {
                    temporaryPath = string.Concat(target, ".", Guid.NewGuid().ToString("N"), ".pdm-cache");
                    File.Copy(source, temporaryPath, false);
                    if (!string.Equals(ComputeFileHash(temporaryPath), sourceSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                    File.Move(temporaryPath, target);
                    temporaryPath = string.Empty;
                    created = true;
                }
            }

            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(
                target,
                documentId,
                projectId,
                version.Id,
                version.Revision?.Display,
                sourceSha256,
                new FileInfo(target).Length))
            {
                if (created)
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                return string.Empty;
            }
            return target;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
            {
                try
                {
                    File.SetAttributes(temporaryPath, FileAttributes.Normal);
                    File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
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
        var saveOptions = swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy;
        if (document.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
            saveOptions |= swSaveAsOptions_e.swSaveAsOptions_IncludeVirtualSubAsmComps;
        if (document.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            saveOptions |= swSaveAsOptions_e.swSaveAsOptions_OverrideSaveEmodel | swSaveAsOptions_e.swSaveAsOptions_UpdateInactiveViews;
        var copied = document.Extension.SaveAs(
            copyPath,
            (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)saveOptions,
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
        try
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }
        }
        catch
        {
            DeleteCheckInUploadCopy(copyPath);
            throw;
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

    private static bool IsControlledReadOnlyMode(ControlledOpenMode mode) =>
        mode == ControlledOpenMode.LatestReadOnly || mode == ControlledOpenMode.LatestReleased || mode == ControlledOpenMode.SpecificReadOnly;

    private static void EnsureDocumentOpenedReadOnly(IModelDoc2 document, string path)
    {
        if (document.IsOpenedReadOnly()) return;
        if (document.GetSaveFlag())
            throw new InvalidOperationException(Path.GetFileName(path) + "存在未保存修改，请先保存或另存后再只读打开。");
        if (!document.SetReadOnlyState(true) || !document.IsOpenedReadOnly())
            throw new IOException(Path.GetFileName(path) + "未能切换为只读状态，已停止打开。请关闭该图档后重试。");
    }

    private IModelDoc2 OpenOrActivateDocumentOnSolidWorksThread(string fullPath, int documentType, string configuration, bool readOnly = false)
    {
        var activeDocument = application.ActiveDoc as IModelDoc2;
        var document = activeDocument != null && PathsEqual(activeDocument.GetPathName(), fullPath)
            ? activeDocument : FindLoadedDocument(fullPath);
        if (document == null)
        {
            var openErrors = 0;
            var openWarnings = 0;
            LogOperation(string.Concat("OpenDoc6 start path=", fullPath));
            document = application.OpenDoc6(
                fullPath,
                documentType,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | (readOnly ? swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0)),
                configuration,
                ref openErrors,
                ref openWarnings);
            LogOperation(string.Concat("OpenDoc6 end path=", fullPath, " errors=", openErrors, " warnings=", openWarnings, " null=", document == null,
                " returned=", document?.GetPathName(), " active=", (application.ActiveDoc as IModelDoc2)?.GetPathName()));
            if (document == null)
            {
                if ((openErrors & (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen) != 0)
                {
                    ShowError("SolidWorks中已打开同名图档，无法同时打开该历史版本。请关闭同名历史窗口后重新获取。");
                }
                else
                {
                    ShowError(openErrors == 0 ? "SolidWorks尚未返回目标图档，当前窗口和文件已保留，请稍后重试。"
                        : string.Concat("SolidWorks打开文件失败，错误码：", openErrors));
                }
                return null;
            }
        }
        else
        {
            LogOperation(string.Concat("Open reused loaded document path=", fullPath));
        }

        var returnedPath = document.GetPathName();
        var activePath = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        LogOperation("Open identity requested=" + fullPath + " returned=" + returnedPath + " active=" + activePath);
        if (!PathsEqual(returnedPath, fullPath))
        {
            ShowError("SolidWorks返回了另一目录中的同名图档，已停止后续切换和权限操作。当前窗口和文件已保留，请核对同名文档所在目录后重新打开。");
            return null;
        }
        if (readOnly) EnsureDocumentOpenedReadOnly(document, fullPath);
        returnedPath = document.GetPathName();
        activePath = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        if (PathsEqual(activePath, fullPath) && PathsEqual(returnedPath, fullPath))
        {
            LogOperation("Open skipped verified-active requested=" + fullPath + " returned=" + returnedPath + " active=" + activePath);
            return document;
        }
        var documentName = Path.GetFileName(fullPath);
        var sameNamePaths = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(model => model.GetPathName()).Where(path => !string.IsNullOrWhiteSpace(path)
                && string.Equals(Path.GetFileName(path), documentName, StringComparison.OrdinalIgnoreCase)
                && !PathsEqual(path, fullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sameNamePaths.Length > 0)
        {
            LogOperation("Activate identity conflict requested=" + fullPath + " returned=" + returnedPath + " active=" + activePath
                + " conflicts=" + string.Join(";", sameNamePaths));
            ShowError("目标副本已加载，但另一目录中的同名图档仍处于打开状态，暂未切换窗口。所有文件和窗口均已保留，请核对同名文档所在目录后重试。");
            return null;
        }
        var activationErrors = 0;
        LogOperation("ActivateDoc3 start name=" + documentName + " requested=" + fullPath);
        var activated = application.ActivateDoc3(
            documentName,
            false,
            (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
            ref activationErrors) as IModelDoc2;
        var activatedPath = activated?.GetPathName();
        activePath = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        LogOperation("ActivateDoc3 end name=" + documentName + " requested=" + fullPath + " returned=" + activatedPath
            + " active=" + activePath + " errors=" + activationErrors + " null=" + (activated == null));
        if (activated == null || (activationErrors & (int)swActivateDocError_e.swGenericActivateError) != 0
            || !PathsEqual(activatedPath, fullPath) || !PathsEqual(activePath, fullPath))
        {
            ShowError("SolidWorks尚未切换到所需图档，当前窗口和文件已保留。请核对已打开文档所在目录后重试，详细路径已写入诊断日志。");
            return null;
        }

        return activated;
    }

    private IModelDoc2 OpenDocumentSilentlyForBatch(string fullPath, int documentType, string configuration, bool readOnly = false)
    {
        var openErrors = 0;
        var openWarnings = 0;
        LogOperation(string.Concat("Batch fallback OpenDoc6 start path=", fullPath));
        if (readOnly) LogCheckInCheckpoint("open-start", fullPath);
        var document = application.OpenDoc6(
            fullPath,
            documentType,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent
                | (readOnly ? swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0)),
            configuration,
            ref openErrors,
            ref openWarnings);
        LogOperation(string.Concat("Batch fallback OpenDoc6 end path=", fullPath, " actualPath=", document?.GetPathName(), " errors=", openErrors, " warnings=", openWarnings, " null=", document == null));
        if (readOnly) LogCheckInCheckpoint("open-end", fullPath);
        if (document == null)
        {
            if (openErrors == (int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen)
                throw new ArchiveReadAliasConflictException("SolidWorks已有同名图档打开，当前无法安全读取预期文件：" + fullPath
                    + "。原窗口已保留，请核对已打开的同名图档及其完整路径后重试。");
            throw new IOException(string.Concat(
                Path.GetFileName(fullPath),
                "未在当前装配中加载，SolidWorks静默读取失败。错误码：",
                openErrors));
        }

        return document;
    }

    private IModelDoc2 OpenDrawingForArchive(string fullPath)
    {
        // An invisible drawing can save successfully while omitting the display data eDrawings needs.
        var type = (int)swDocumentTypes_e.swDocDRAWING;
        var previousVisibility = application.GetDocumentVisible(type);
        try
        {
            application.DocumentVisible(true, type);
            return OpenDocumentSilentlyForBatch(fullPath, type, string.Empty, readOnly: true);
        }
        finally
        {
            application.DocumentVisible(previousVisibility, type);
        }
    }

    private IModelDoc2 OpenDocumentInvisiblyForBatch(
        string fullPath,
        int documentType,
        string configuration,
        bool readOnly = false)
    {
        var previousVisibility = application.GetDocumentVisible(documentType);
        try
        {
            application.DocumentVisible(false, documentType);
            LogOperation(string.Concat("Batch invisible open start path=", fullPath));
            return OpenDocumentSilentlyForBatch(fullPath, documentType, configuration, readOnly);
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

    private bool CloseBatchOpenedDocument(IModelDoc2 document)
    {
        try
        {
            var title = document.GetTitle();
            if (string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("临时图档名称为空，已取消关闭，避免误关闭当前装配体。");
            if (Volatile.Read(ref checkInOperationInProgress) > 0) LogCheckInCheckpoint("close-start", title);
            LogOperation(string.Concat("Batch fallback CloseDoc start title=", title));
            application.CloseDoc(title);
            LogOperation(string.Concat("Batch fallback CloseDoc end title=", title));
            if (Volatile.Read(ref checkInOperationInProgress) > 0) LogCheckInCheckpoint("close-end", title);
            return true;
        }
        catch (Exception exception)
        {
            LogDiagnostic("CloseBatchOpenedDocument", exception);
            return false;
        }
    }

    private bool CloseArchiveReadDocument(IModelDoc2 document)
    {
        if (!CloseBatchOpenedDocument(document)) return false;
        // Release only the reference opened by archive preparation, never a user's window.
        if (Marshal.IsComObject(document)) Marshal.ReleaseComObject(document);
        return true;
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

    internal static bool IsSolidWorksTemporaryVirtualComponentPath(string path)
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
        if (node != null) return node.IsHistoricalPreview || IsHistoricalPreviewPath(node.FullPath);
        if (node?.IsHistoricalPreview == true || IsHistoricalPreviewPath(node?.FullPath))
        {
            return true;
        }

        var activePath = (application?.ActiveDoc as IModelDoc2)?.GetPathName();
        return IsHistoricalPreviewPath(activePath);
    }

    private bool IsLatestReadOnlyPreviewContext(CadTreeNode node)
    {
        if (node != null) return !IsNewDrawingInLatestPreview(node)
            && (node.IsLatestReadOnlyPreview || IsLatestReadOnlyPreviewPath(node.FullPath));
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

    private static bool IsHistoricalDerivedPath(string path)
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
                && fullPath.IndexOf(
                    string.Concat(Path.DirectorySeparatorChar, ".uplm", Path.DirectorySeparatorChar, "Derived", Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReadOnlyPreviewPath(string path) =>
        IsHistoricalPreviewPath(path) || IsLatestReadOnlyPreviewPath(path);

    private void MarkReadOnlyPreview(CadTreeNode node, bool inheritedHistoricalPreview, bool inheritedLatestPreview)
    {
        var workspaceRoot = string.Empty;
        try { workspaceRoot = Path.GetFullPath(WorkspaceSettingsStore.GetWorkspaceRoot()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
        catch { }
        var historyRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UPTON-PDM", "history")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        void Visit(CadTreeNode current, bool historicalParent, bool latestParent)
        {
            if (current == null) return;
            var fullPath = string.Empty;
            try { if (!string.IsNullOrWhiteSpace(current.FullPath)) fullPath = Path.GetFullPath(current.FullPath); }
            catch { }
            var inWorkspace = workspaceRoot.Length > 0 && fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase);
            var inherit = current.Kind != CadDocumentKind.Drawing;
            current.IsHistoricalPreview = inherit && historicalParent || fullPath.StartsWith(historyRoot, StringComparison.OrdinalIgnoreCase)
                || inWorkspace && fullPath.IndexOf(Path.DirectorySeparatorChar + "ReadOnly" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
            current.IsLatestReadOnlyPreview = !current.IsHistoricalPreview && !IsNewDrawingInLatestPreview(current)
                && (inherit && latestParent || inWorkspace && fullPath.IndexOf(Path.DirectorySeparatorChar + "Latest" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var child in current.Children) Visit(child, current.IsHistoricalPreview, current.IsLatestReadOnlyPreview);
        }
        Visit(node, inheritedHistoricalPreview, inheritedLatestPreview);
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

    private IReadOnlyDictionary<string, string> ReadCheckInProperties(IModelDoc2 document, CadTreeNode node, bool useCached = false,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> nativePropertyCards = null, bool requireComplete = false)
    {
        var properties = ReadModelProperties(document, node.Configuration, useCached,
                includeConfiguration: node.Kind != CadDocumentKind.Drawing, requireComplete: requireComplete)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        // Preserve legacy markers for PDF stamp cleanup without writing to the native drawing.
        if (node.Kind == CadDocumentKind.Drawing && !useCached)
        {
            foreach (var propertyName in new[]
            {
                "UPLM_QR_CONTENT",
                "UPLM_QR_RULE_VERSION",
                "UPLM_QR_SOURCE_PROPERTY",
                "UPLM_QR_RENDER_VERSION",
                "UPLM_QR_POSITION"
            })
            {
                var value = ReadResolvedCustomProperty(document, string.Empty, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                    properties[string.Concat("全局/", propertyName)] = value;
            }
        }
        var card = ResolveActiveNativePropertyCard(node, nativePropertyCards ?? DiscoverNativePropertyCards());
        if (card?.IsAvailable == true)
        {
            foreach (var field in card.Fields)
                Upton.Pdm.Domain.CadPropertyCardSnapshot.AddField(properties, field.PropertyName,
                    field.ConfigurationSpecific && node.Kind != CadDocumentKind.Drawing);
        }
        return properties;
    }

    private static string ReadSnapshotProperty(IReadOnlyDictionary<string, string> properties, string propertyName)
    {
        if (properties == null || string.IsNullOrWhiteSpace(propertyName)) return string.Empty;
        var suffix = string.Concat("/", propertyName.Trim());
        var match = properties.FirstOrDefault(item =>
            (string.Equals(item.Key, propertyName.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(item.Value));
        return match.Value?.Trim() ?? string.Empty;
    }

    internal static IReadOnlyDictionary<string, string> ReadModelProperties(IModelDoc2 document, string configurationName = null,
        bool useCached = false, bool includeConfiguration = true, bool requireComplete = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document == null) return result;
        ReadPropertyManager(document.Extension.CustomPropertyManager[string.Empty], "全局", result, useCached, requireComplete);
        if (!includeConfiguration) return result;
        if (string.IsNullOrWhiteSpace(configurationName))
            configurationName = document.ConfigurationManager?.ActiveConfiguration?.Name;
        if (!string.IsNullOrWhiteSpace(configurationName))
            ReadPropertyManager(document.Extension.CustomPropertyManager[configurationName], string.Concat("配置:", configurationName), result, useCached, requireComplete);
        return result;
    }

    private static void ReadPropertyManager(CustomPropertyManager manager, string scope, IDictionary<string, string> target, bool useCached = false, bool requireComplete = false)
    {
        if (requireComplete && manager == null) throw new InvalidOperationException("无法读取" + scope + "属性管理器。");
        var rawNames = manager?.GetNames() as Array;
        var names = rawNames?.Cast<object>().OfType<string>().ToArray() ?? Array.Empty<string>();
        if (requireComplete && (manager.Count != names.Length || rawNames != null && rawNames.Length != names.Length))
            throw new InvalidOperationException("无法完整读取" + scope + "属性名称，请保存并重新检查。");
        foreach (var name in names)
        {
            var raw = string.Empty;
            var resolved = string.Empty;
            var wasResolved = false;
            var linked = false;
            var readResult = manager.Get6(name, useCached, out raw, out resolved, out wasResolved, out linked);
            if (requireComplete && readResult == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                throw new InvalidOperationException("属性在读取期间不存在或读取失败：" + scope + "/" + name);
            // A cached evaluated value is usable even when WasResolved is false.
            target[string.Concat(scope, "/", name)] = wasResolved || (useCached && !string.IsNullOrEmpty(resolved)) ? resolved : raw;
        }
    }

    private void OnOpenHistoryRequested(object sender, DocumentVersionEventArgs eventArgs)
    {
        if (!apiClient.IsAuthenticated) { ShowError("请先登录PLM。"); return; }
        if (eventArgs.Version == null) { ShowError("请选择需要打开的PLM版本。"); return; }
        BeginControlledOpen(currentProjectId ?? Guid.Empty, eventArgs.DocumentId,
            ControlledOpenMode.SpecificReadOnly, eventArgs.Version.Id);
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
            ShowError("当前设计树中未找到该图档，请刷新后重试。");
            return;
        }
        if (node.Kind != CadDocumentKind.Part && node.Kind != CadDocumentKind.Assembly)
        {
            ShowError("仅零件和装配体支持基于历史版本获取编辑。");
            return;
        }
        if (node.DrawingReviewLocked
            || string.Equals(node.LifecycleState, "InReview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.LifecycleState, "Obsolete", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(node.DrawingReviewLocked ? "图纸审核中的图档不能获取编辑权限。" : "审批中或已作废的图档不能获取编辑权限。");
            return;
        }

        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (!TryBeginWorkspaceOperation("正在基于历史版本获取编辑权限"))
        {
            ShowWorkspaceOperationBusy();
            return;
        }

        var derivedRootPath = string.Empty;
        ControlledOpenManifestDto manifest = null;
        var checkoutAcquired = false;
        var editPrepared = false;
        var successMessage = string.Empty;
        Interlocked.Increment(ref refreshSuppressionDepth);
        try
        {
            var serverManifest = await apiClient.CreateControlledOpenManifestAsync(node.DocumentId.Value,
                eventArgs.Version.Id, false, false, lifetime.Token);
            projectId = serverManifest.ProjectId;
            var projectDocuments = await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token);
            var serverDocument = projectDocuments.FirstOrDefault(document => document.Id == node.DocumentId.Value)
                ?? throw new InvalidOperationException("当前项目中未找到该图档，请刷新设计树后重试。");
            ApplyCheckoutDocument(node, serverDocument);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault()
                ?? throw new InvalidOperationException("该图档尚无可选择的PLM版本。");
            var selected = versions.FirstOrDefault(version => version.Id == eventArgs.Version.Id)
                ?? throw new InvalidOperationException("所选版本已不存在，请刷新版本列表后重试。");
            if (selected.Id == latest.Id)
            {
                throw new InvalidOperationException("所选版本已经是最新版本，请直接使用“获取权限”。");
            }
            ApplyLatestVersion(node, latest);

            var selectedRevision = selected.Revision?.Display ?? "-";
            var latestRevision = latest.Revision?.Display ?? "-";
            var confirmation = node.Kind == CadDocumentKind.Assembly
                ? string.Concat(
                    "将基于装配体", node.FileName, "的历史版本", selectedRevision, "创建独立派生工作区。\r\n",
                    "整套引用将固定为该历史快照；仅根装配体获取编辑权限，子件保持只读，需要修改时再单独获取。\r\n",
                    "服务器最新版本为", latestRevision, "；提交将从最新版本继续升版，不覆盖任何历史记录。是否继续？")
                : string.Concat(
                    "将基于零件", node.FileName, "的历史版本", selectedRevision, "创建独立派生工作区并获取编辑权限。\r\n",
                    "服务器最新版本为", latestRevision, "；提交将从最新版本继续升版，不覆盖任何历史记录。\r\n",
                    "若提交时内容与服务器最新版本完全一致，则只结束编辑、不生成新版本。是否继续？");
            if (TopMostMessageBox.Show(taskPaneControl, confirmation, "基于历史版本获取编辑", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            manifest = serverManifest;
            if (manifest == null
                || manifest.RootDocumentId != node.DocumentId.Value
                || manifest.RootVersionId != selected.Id)
            {
                throw new InvalidDataException("PLM返回的历史版本清单与所选图档不一致，已停止获取编辑。");
            }
            if (manifest.ProjectId != projectId.Value)
            {
                throw new InvalidOperationException("历史版本所属项目与当前项目不一致，已停止获取编辑。");
            }
            if (manifest.Files == null || manifest.Files.Count == 0)
            {
                throw new InvalidDataException("PLM返回的历史版本清单为空。");
            }
            if (manifest.Warnings?.Count > 0)
            {
                throw new InvalidDataException(string.Concat(
                    "历史版本引用快照不完整：",
                    string.Join("；", manifest.Warnings.Take(5)),
                    manifest.Warnings.Count > 5 ? "；其余问题请在PLM中处理后重试" : string.Empty));
            }
            var duplicateFileName = manifest.Files
                .Where(file => !string.IsNullOrWhiteSpace(file.FileName))
                .GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateFileName != null)
            {
                throw new InvalidDataException(string.Concat(
                    "历史版本包含重名引用“", duplicateFileName.Key, "”，不能安全派生编辑。请先消除重名或引用冲突。"));
            }

            var manifestDocumentIds = new HashSet<Guid>(manifest.Files.Select(file => file.DocumentId));
            var unavailableDocument = manifestDocumentIds.FirstOrDefault(documentId => projectDocuments.All(document => document.Id != documentId));
            if (unavailableDocument != Guid.Empty)
            {
                throw new InvalidOperationException("历史版本包含当前项目之外或无权访问的引用，已停止获取编辑。");
            }
            var existingCheckout = projectDocuments.FirstOrDefault(document =>
                manifestDocumentIds.Contains(document.Id) && !string.IsNullOrWhiteSpace(document.CheckedOutBy)
                && (document.CheckoutSessionId != checkoutSessionId || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)));
            if (existingCheckout != null)
            {
                throw new InvalidOperationException(string.Concat(
                    existingCheckout.FileName ?? "清单内图档",
                    "正在由", ResolveUserDisplayName(existingCheckout.CheckedOutBy), "编辑，请先完成或放弃该编辑后重试。"));
            }
            derivedRootPath = await controlledWorkspace.PrepareHistoricalDerivedCopyAsync(manifest, lifetime.Token);
            CloseLocalContextsForPlmOpen(manifest);
            RetireLocalRecoveryForPlmOpen(manifest);

            DocumentDto checkout;
            plmAuthoritativeCheckouts.Add(node.DocumentId.Value);
            try { checkout = await apiClient.CheckoutAsync(node.DocumentId.Value, checkoutSessionId, checkoutMachineName, lifetime.Token); }
            finally { plmAuthoritativeCheckouts.Remove(node.DocumentId.Value); }
            checkoutAcquired = string.IsNullOrWhiteSpace(serverDocument.CheckedOutBy);
            TrackCheckoutDocument(checkout);
            derivedRootPath = controlledWorkspace.ApplyHistoricalDerivedPermissions(
                manifest,
                new[] { node.DocumentId.Value });
            RememberControlledOpenManifest(manifest, Path.GetDirectoryName(derivedRootPath));
            RememberExplicitProjectPath(derivedRootPath, manifest.ProjectId);
            historicalEditContexts[node.DocumentId.Value] = new HistoricalEditContext(selected, latest);
            editPrepared = true;

            var matchingNodes = EnumerateCadNodes(currentTree)
                .Where(candidate => candidate.DocumentId == node.DocumentId)
                .ToArray();
            ApplyCheckoutDocumentToMatchingInstances(matchingNodes, checkout);
            LogOperation(string.Concat(
                "Historical derived edit acquired document=", node.DocumentId.Value,
                " kind=", node.Kind,
                " source=", selectedRevision,
                " latest=", latestRevision,
                " path=", derivedRootPath,
                " files=", manifest.Files.Count));
            successMessage = string.Concat(
                "已基于历史版本", selectedRevision, "创建独立编辑副本；提交后从最新版本", latestRevision, "继续升版。",
                node.Kind == CadDocumentKind.Assembly ? " 根装配体可编辑，子件保持只读。" : string.Empty);
        }
        catch (Exception exception)
        {
            LogDiagnostic("OnEditHistoricalVersionRequested", exception);
            if (checkoutAcquired && !editPrepared)
            {
                try
                {
                    var discarded = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
                    TrackCheckoutDocument(discarded);
                    ApplyCheckoutDocumentToMatchingInstances(
                        EnumerateCadNodes(currentTree).Where(candidate => candidate.DocumentId == node.DocumentId),
                        discarded);
                    if (manifest != null)
                    {
                        controlledWorkspace.ApplyHistoricalDerivedPermissions(manifest, Array.Empty<Guid>());
                    }
                }
                catch (Exception rollbackException)
                {
                    LogDiagnostic("Historical derived edit checkout rollback", rollbackException);
                }
            }
            historicalEditContexts.Remove(node.DocumentId.Value);
            ShowError(exception.Message);
        }
        finally
        {
            Interlocked.Decrement(ref refreshSuppressionDepth);
            EndWorkspaceOperation();
            ScheduleTreeRefresh();
        }

        if (editPrepared && !string.IsNullOrWhiteSpace(derivedRootPath))
        {
            taskPaneControl.SetCheckoutReminder(successMessage, false);
            QueueOpenDocument(
                derivedRootPath,
                DocumentKindFromPath(derivedRootPath),
                string.Empty,
                false,
                manifest.ProjectId,
                manifest);
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

    private void StartDesktopClient(string arguments)
    {
        var executable = FindDesktopClientExecutable();
        var handoffId = string.IsNullOrWhiteSpace(authenticatedSessionJson)
            ? string.Empty
            : DesktopAuthHandoffStore.Create(authenticatedSessionJson);
        var launchArguments = string.IsNullOrWhiteSpace(handoffId)
            ? arguments ?? string.Empty
            : string.Concat(arguments ?? string.Empty, string.IsNullOrWhiteSpace(arguments) ? string.Empty : " ", "--auth-handoff ", handoffId);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = launchArguments,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true
            });
        }
        catch
        {
            DesktopAuthHandoffStore.Delete(handoffId);
            throw;
        }
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

    private void RefreshTree(bool showErrors, bool allowCachedTree = false)
    {
        if (IsSolidWorksCommandActive())
        {
            ScheduleTreeRefresh(forceScan: !allowCachedTree);
            return;
        }
        var activeDocument = application?.ActiveDoc as IModelDoc2;
        if (activeDocument == null)
        {
            ClearActiveDocumentContext();
            return;
        }

        var previousTree = currentTree;
        var previousKey = currentTreeCacheKey;
        var activeTreeKey = BuildActiveTreeCacheKey(activeDocument);
        var treeElapsed = Stopwatch.StartNew();
        if (!showErrors && string.Equals(failedTreeRefreshKey, activeTreeKey, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow < nextFailedTreeRefreshUtc) return;
        if (string.Equals(failedTreeRefreshKey, activeTreeKey, StringComparison.OrdinalIgnoreCase)) allowCachedTree = false;
        try
        {
            if (allowCachedTree && TryRestoreActiveTreeFromCache(activeTreeKey))
            {
                return;
            }

            taskPaneControl.SetProjectContextAvailable(true);
            taskPaneControl.SetTreeLoadingStatus("正在核对实际组件和嵌入结构…");
            var scannedTree = scanner.ScanActiveDocument();
            RestorePersistedDocumentIdentities(scannedTree);
            CarryForwardDocumentIdentities(previousTree, scannedTree);
            var documentIdentity = DocumentIdentity(scannedTree);
            var documentChanged = !string.Equals(currentDocumentIdentity, documentIdentity, StringComparison.OrdinalIgnoreCase);
            if (documentChanged)
            {
                CancelMetadataRefresh();
                Interlocked.Increment(ref projectResolutionGeneration);
                currentProjectId = null;
                taskPaneControl.SelectProject(null);
                currentDocumentIdentity = documentIdentity;
            }

            currentTree = scannedTree;
            currentTreeCacheKey = activeTreeKey;
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
            CacheActiveTree(activeTreeKey, currentTree, currentProjectId);
            taskPaneControl.SetTree(currentTree);
            taskPaneControl.SetActiveDrawingSource(activeDrawingSource);
            if (string.Equals(failedTreeRefreshKey, activeTreeKey, StringComparison.OrdinalIgnoreCase))
                taskPaneControl.SetCheckoutReminder(string.Empty, false);
            failedTreeRefreshKey = string.Empty;
            LogOperation(string.Concat("RefreshTree success nodes=", CountTreeNodes(currentTree), " path=", currentTree?.FullPath ?? string.Empty));
            LogOperation("设计树实际结构就绪 elapsedMs=" + treeElapsed.ElapsedMilliseconds);
            taskPaneControl.SetTreeLoadingStatus("结构已显示，正在核对图档版本和权限…");
            if (currentProjectId.HasValue && apiClient.IsAuthenticated)
            {
                _ = RefreshMetadataAsync(currentProjectId.Value, BeginMetadataRefresh());
            }
            else if (apiClient.IsAuthenticated)
            {
                _ = ResolveProjectForCurrentDocumentAsync(currentTree, documentIdentity);
            }
            else taskPaneControl.SetTreeLoadingStatus(string.Empty);
        }
        catch (Exception exception)
        {
            if (application.ActiveDoc == null)
            {
                ClearActiveDocumentContext();
                return;
            }

            LogDiagnostic("RefreshTree", exception);
            CancelMetadataRefresh();
            failedTreeRefreshKey = activeTreeKey;
            nextFailedTreeRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            ScheduleTreeRefresh(false, false);
            if (previousTree != null && string.Equals(previousKey, activeTreeKey, StringComparison.OrdinalIgnoreCase))
            {
                currentTree = previousTree;
                currentTreeCacheKey = previousKey;
            }
            else
            {
                currentTree = null;
                currentTreeCacheKey = string.Empty;
                taskPaneControl.ClearTree();
            }
            taskPaneControl.SetProjectContextAvailable(true);
            taskPaneControl.SetCheckoutReminder("设计树暂未刷新完成，当前图档仍已打开；请稍后刷新。存档时会重新核对实际内容。", true);
            taskPaneControl.SetTreeLoadingStatus("结构尚未核对完整，请稍后刷新。");
            if (showErrors)
            {
                ShowError(ArchivePreflightError(exception));
            }
        }
    }

    private void ClearActiveDocumentContext()
    {
        Interlocked.Increment(ref projectResolutionGeneration);
        currentTree = null;
        currentTreeCacheKey = string.Empty;
        currentDocumentIdentity = string.Empty;
        currentProjectId = null;
        taskPaneControl.SetProjectContextAvailable(false);
        taskPaneControl.SelectProject(null);
        taskPaneControl.ClearTree();
        taskPaneControl.SetConnectionState(false, "未打开图档");
        taskPaneControl.SetTreeLoadingStatus(string.Empty);
    }

    private async Task ResolveProjectForCurrentDocumentAsync(CadTreeNode tree, string documentIdentity)
    {
        if (tree == null || availableProjects.Count == 0 || !apiClient.IsAuthenticated)
        {
            if (ReferenceEquals(currentTree, tree)) taskPaneControl?.SetTreeLoadingStatus(string.Empty);
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
                    await RefreshMetadataAsync(currentProjectId.Value, BeginMetadataRefresh());
                }
                else taskPaneControl.SetTreeLoadingStatus(string.Empty);
            }
        }
        catch (Exception exception)
        {
            LogDiagnostic("ResolveProjectForCurrentDocumentAsync", exception);
        }
    }

    private static string DocumentIdentity(CadTreeNode tree) =>
        string.IsNullOrWhiteSpace(tree?.FullPath) ? tree?.FileName ?? string.Empty : tree.FullPath;

    private static string BuildActiveTreeCacheKey(IModelDoc2 document)
    {
        if (document == null)
        {
            return string.Empty;
        }
        var path = document.GetPathName() ?? string.Empty;
        var identity = string.IsNullOrWhiteSpace(path) ? document.GetTitle() ?? string.Empty : path;
        var configuration = document.ConfigurationManager?.ActiveConfiguration?.Name ?? string.Empty;
        return string.Concat(identity.Trim(), "\n", configuration.Trim());
    }

    private bool TryRestoreActiveTreeFromCache(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return false;
        }

        ActiveTreeCacheEntry entry;
        lock (activeTreeCacheSync)
        {
            if (!activeTreeCache.TryGetValue(cacheKey, out entry) || entry?.Tree == null)
            {
                return false;
            }
            entry.LastAccessUtc = DateTime.UtcNow;
        }

        var previousTree = currentTree;
        var documentIdentity = DocumentIdentity(entry.Tree);
        var documentChanged = !string.Equals(currentTreeCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase);
        if (documentChanged)
        {
            CancelMetadataRefresh();
            Interlocked.Increment(ref projectResolutionGeneration);
        }

        currentTree = entry.Tree;
        currentTreeCacheKey = cacheKey;
        currentDocumentIdentity = documentIdentity;
        currentProjectId = entry.ProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        taskPaneControl.SetProjectContextAvailable(true);
        taskPaneControl.SelectProject(currentProjectId);
        // This tree already carries its verified local base and preview flags.
        // Switching windows must not hash or rewrite every occurrence again.
        var controlledManifest = controlledOpenManifests.FirstOrDefault(pair => !string.IsNullOrWhiteSpace(currentTree.FullPath) &&
            IsPathWithinDirectory(currentTree.FullPath, pair.Key)).Value;
        if (controlledManifest != null)
        {
            currentProjectId = controlledManifest.ProjectId;
            taskPaneControl.SelectProject(currentProjectId);
        }
        var activeDrawingSource = ResolveActiveDrawingSource(currentTree, previousTree);
        taskPaneControl.SetTree(currentTree);
        taskPaneControl.SetActiveDrawingSource(activeDrawingSource);
        CacheActiveTree(cacheKey, currentTree, currentProjectId);
        LogOperation(string.Concat(
            "RefreshTree cache hit nodes=", CountTreeNodes(currentTree),
            " path=", currentTree.FullPath ?? string.Empty));
        if (currentProjectId.HasValue && apiClient.IsAuthenticated)
        {
            _ = RefreshMetadataAsync(currentProjectId.Value, BeginMetadataRefresh());
        }
        else if (apiClient.IsAuthenticated)
        {
            _ = ResolveProjectForCurrentDocumentAsync(currentTree, documentIdentity);
        }
        return true;
    }

    private void CacheCurrentTreeContext()
    {
        if (currentTree == null || string.IsNullOrWhiteSpace(currentTreeCacheKey)
            || Volatile.Read(ref pendingTreeRefreshForceScan) != 0)
        {
            return;
        }
        CacheActiveTree(currentTreeCacheKey, currentTree, currentProjectId);
    }

    private void CacheActiveTree(string cacheKey, CadTreeNode tree, Guid? projectId)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || tree == null)
        {
            return;
        }
        lock (activeTreeCacheSync)
        {
            activeTreeCache[cacheKey] = new ActiveTreeCacheEntry(tree, projectId, DateTime.UtcNow);
            while (activeTreeCache.Count > ActiveTreeCacheCapacity)
            {
                var oldest = activeTreeCache.OrderBy(pair => pair.Value.LastAccessUtc).First();
                activeTreeCache.Remove(oldest.Key);
            }
        }
    }

    private void InvalidateCurrentTreeCache()
    {
        if (Volatile.Read(ref draftSnapshotInProgress) != 0) return;
        string cacheKey = null;
        try
        {
            cacheKey = BuildActiveTreeCacheKey(application?.ActiveDoc as IModelDoc2);
        }
        catch
        {
            cacheKey = currentTreeCacheKey;
        }
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return;
        }
        lock (activeTreeCacheSync)
        {
            var separator = cacheKey.IndexOf('\n');
            var changedPath = separator < 0 ? cacheKey : cacheKey.Substring(0, separator);
            var isAssembly = string.Equals(Path.GetExtension(changedPath), ".SLDASM", StringComparison.OrdinalIgnoreCase);
            foreach (var key in activeTreeCache.Where(pair => string.Equals(pair.Key, cacheKey, StringComparison.OrdinalIgnoreCase)
                || isAssembly && EnumerateCadNodes(pair.Value.Tree).Any(node => PathsEqual(node.FullPath, changedPath)))
                .Select(pair => pair.Key).ToArray()) activeTreeCache.Remove(key);
        }
    }

    private void InvalidateTreeCacheForPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        var normalized = fileName.Trim();
        string normalizedName;
        try
        {
            normalizedName = Path.GetFileName(normalized);
        }
        catch
        {
            normalizedName = normalized;
        }
        lock (activeTreeCacheSync)
        {
            foreach (var key in activeTreeCache.Keys
                .Where(key =>
                {
                    var separator = key.IndexOf('\n');
                    var identity = separator < 0 ? key : key.Substring(0, separator);
                    string identityName;
                    try
                    {
                        identityName = Path.GetFileName(identity);
                    }
                    catch
                    {
                        identityName = identity;
                    }
                    return string.Equals(identity, normalized, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(identityName, normalizedName, StringComparison.OrdinalIgnoreCase);
                })
                .ToArray())
            {
                activeTreeCache.Remove(key);
            }
        }
    }

    private static int CountTreeNodes(CadTreeNode node)
    {
        return node == null ? 0 : 1 + node.Children.Sum(CountTreeNodes);
    }

    private CancellationToken BeginMetadataRefresh()
    {
        CancellationTokenSource previous;
        CancellationTokenSource next;
        lock (metadataRefreshSync)
        {
            previous = metadataRefreshCancellation;
            previous?.Cancel();
            next = lifetime == null
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            metadataRefreshCancellation = next;
            metadataRefreshOwners[next.Token] = next;
        }

        return next.Token;
    }

    private void CancelMetadataRefresh()
    {
        CancellationTokenSource cancellation;
        lock (metadataRefreshSync)
        {
            cancellation = metadataRefreshCancellation;
            metadataRefreshCancellation = null;
            cancellation?.Cancel();
        }

    }

    private async Task RefreshMetadataCoreAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (DeferBackgroundTreeRefresh()) return;
        var targetTree = currentTree;
        var metadataElapsed = Stopwatch.StartNew();
        try
        {
            await Task.Delay(MetadataRefreshDelayMilliseconds, cancellationToken);
            var documents = await GetProjectDocumentsForNavigationAsync(projectId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disconnecting
                || taskPaneControl == null
                || taskPaneControl.IsDisposed
                || !ReferenceEquals(currentTree, targetTree)
                || currentProjectId != projectId)
            {
                return;
            }

            if (DeferBackgroundTreeRefresh()) return;
            var baseNodes = EnumerateCadNodes(targetTree).Where(node => node.DocumentId.HasValue && !string.IsNullOrWhiteSpace(node.FullPath)).ToArray();
            var basePaths = baseNodes.Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var localBases = await Task.Run(() =>
            {
                var bases = new Dictionary<string, PdmControlledFileMetadata>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in basePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)) bases[path] = metadata;
                }
                return bases;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(currentTree, targetTree) || currentProjectId != projectId || DeferBackgroundTreeRefresh()) return;
            foreach (var node in baseNodes)
                if (localBases.TryGetValue(node.FullPath, out var metadata) && metadata.DocumentId == node.DocumentId && metadata.ProjectId == projectId)
                {
                    node.OpenedVersionId = metadata.VersionId;
                    node.OpenedRevision = metadata.Revision ?? string.Empty;
                    node.CurrentRevision = node.OpenedRevision;
                }
            var byId = documents.ToDictionary(document => document.Id);
            InvalidateChangedVersionHistories(documents);
            RememberNavigationVersions(documents);
            var relations = await GetNavigationRelationsAsync(projectId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(currentTree, targetTree) || currentProjectId != projectId) return;
            if (DeferBackgroundTreeRefresh()) return;
            ReconcileProjectDrawings(targetTree, byId, relations);
            BindDesignDrawingFiles(targetTree);
            if (IsSolidWorksCommandActive()) { ScheduleTreeRefresh(); return; }
            ApplyMetadata(targetTree, byId);
            BindDesignDrawingFiles(targetTree);
            MarkReadOnlyPreview(targetTree, false, false);
            RefreshLoadedDocumentModificationFlags(targetTree);
            var versionsByDocument = await ApplyWorkingStatesAsync(targetTree, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (DeferBackgroundTreeRefresh()) return;
            if (IsSolidWorksCommandActive()) { ScheduleTreeRefresh(); return; }
            await ResolveRecoverableCheckoutSessionsAsync(targetTree, versionsByDocument);
            cancellationToken.ThrowIfCancellationRequested();
            if (DeferBackgroundTreeRefresh()) return;
            if (!ReferenceEquals(currentTree, targetTree) || currentProjectId != projectId)
            {
                return;
            }
            taskPaneControl.SetTree(targetTree);
            taskPaneControl.SetConnectionState(true, "服务正常");
            taskPaneControl.SetTreeLoadingStatus(string.Empty);
            LogOperation("设计树版本状态就绪 documents=" + versionsByDocument.Count + " elapsedMs=" + metadataElapsed.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer active document owns the current metadata refresh.
        }
        catch (Exception exception)
        {
            if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
            {
                return;
            }

            taskPaneControl.SetConnectionState(false, "服务不可用");
            taskPaneControl.SetTreeLoadingStatus("版本状态尚未核对完整，请稍后刷新。");
            LogDiagnostic("读取设计树版本状态", exception);
            ShowError("读取图档版本和权限失败，请检查服务连接后刷新重试。");
        }
    }

    private async Task<IReadOnlyList<DocumentDto>> GetProjectDocumentsForNavigationAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        lock (metadataCacheSync)
        {
            ProjectDocumentsCacheEntry cached;
            if (projectDocumentsCache.TryGetValue(projectId, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached.Documents;
            }
        }

        var documents = await apiClient.GetDocumentsAsync(projectId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (metadataCacheSync)
        {
            projectDocumentsCache[projectId] = new ProjectDocumentsCacheEntry(
                documents,
                DateTime.UtcNow.Add(NavigationMetadataCacheDuration));
        }

        return documents;
    }

    private static CadTreeNode CompleteProjectWorkspaceTree(
        Guid projectId,
        CadTreeNode root,
        IReadOnlyList<CadTreeNode> independentRoots,
        IReadOnlyDictionary<Guid, DocumentDto> documents,
        IReadOnlyList<DocumentModelDrawingRelationDto> relations)
    {
        var referenced = new HashSet<Guid>(EnumerateCadNodes(root)
            .Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId.Value));
        var structures = independentRoots.Where(node => node != null).ToDictionary(node => node.DocumentId.Value);
        foreach (var structure in structures.Values)
        {
            ReconcileProjectDrawings(structure, documents, relations);
            foreach (var child in structure.Children.SelectMany(EnumerateCadNodes))
                if (child.DocumentId.HasValue) referenced.Add(child.DocumentId.Value);
        }
        var independent = new CadTreeNode { DisplayName = "独立图档 / 待纳入结构" };
        foreach (var document in documents.Values
                     .Where(document => document.ProjectId == projectId && !referenced.Contains(document.Id))
                     .OrderBy(document => document.DrawingNumber, StringComparer.CurrentCultureIgnoreCase))
        {
            if (structures.TryGetValue(document.Id, out var structure))
            {
                independent.Children.Add(structure);
                continue;
            }
            independent.Children.Add(MapProjectTree(new DocumentReferenceNodeDto
            {
                DocumentId = document.Id,
                FileName = document.FileName,
                DisplayName = document.Name,
                Kind = document.Kind,
                Configuration = document.Kind == (int)CadDocumentKind.Drawing ? "工程图" : "默认",
                Quantity = 1
            }, documents, true));
        }
        if (independent.Children.Count == 0) return root;

        ReconcileProjectDrawings(independent, documents, relations);
        var attachedDrawings = new HashSet<Guid>(independent.Children
            .SelectMany(node => node.Children)
            .Where(node => node.Kind == CadDocumentKind.Drawing && node.DocumentId.HasValue)
            .Select(node => node.DocumentId.Value));
        independent.Children.RemoveAll(node => node.Kind == CadDocumentKind.Drawing
            && node.DocumentId.HasValue && attachedDrawings.Contains(node.DocumentId.Value));

        var displayRoot = new CadTreeNode { DisplayName = "项目图档" };
        if (root?.DocumentId != null) displayRoot.Children.Add(root);
        else if (root != null) displayRoot.Children.AddRange(root.Children);
        displayRoot.Children.Add(independent);
        return displayRoot;
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
            Role = (CadReferenceRole)source.Role,
            Revision = displayedRevision,
            CurrentRevision = displayedRevision,
            LatestRevision = document?.Revision?.Display ?? source.Revision?.Display ?? string.Empty,
            DrawingVersion = document?.DrawingVersion ?? "待核对",
            PublishedVersion = document?.PublishedVersion ?? "未发布",
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
                    existingDrawing.DrawingVersion = drawing.DrawingVersion ?? "待核对";
                    existingDrawing.PublishedVersion = drawing.PublishedVersion ?? "未发布";
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

    private static void BindDesignDrawingFiles(CadTreeNode tree)
    {
        foreach (var model in EnumerateCadNodes(tree).Where(node => node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly).ToArray())
        {
            if (!model.DocumentId.HasValue) continue;
            var draftPath = DrawingDraftPath.ForSource(model.FullPath, model.ProvenanceProjectId, model.DocumentId);
            if (File.Exists(draftPath) && !model.Children.Any(child => child.Kind == CadDocumentKind.Drawing && PathsEqual(child.FullPath, draftPath)))
                model.Children.Add(new CadTreeNode { FullPath = draftPath, FileName = Path.GetFileName(draftPath),
                    DisplayName = Path.GetFileNameWithoutExtension(draftPath), Kind = CadDocumentKind.Drawing,
                    RelatedModelDocumentId = model.DocumentId });
        }
        var paths = EnumerateCadNodes(tree).Where(node => node.Kind == CadDocumentKind.Drawing && File.Exists(node.FullPath))
            .Where(node => node.DocumentId.HasValue).GroupBy(node => node.DocumentId.Value)
            .ToDictionary(group => group.Key, group => group.First().FullPath);
        foreach (var model in EnumerateCadNodes(tree).Where(node => node.Kind != CadDocumentKind.Drawing))
            foreach (var drawing in model.Children.Where(child => child.Kind == CadDocumentKind.Drawing))
            {
                if (string.IsNullOrWhiteSpace(drawing.FullPath) && drawing.DocumentId.HasValue
                    && paths.TryGetValue(drawing.DocumentId.Value, out var path)) drawing.FullPath = path;
                if (!drawing.ReferencedModels.Any(reference => reference.DocumentId == model.DocumentId))
                    drawing.ReferencedModels.Add(model);
            }
    }

    private static IReadOnlyList<BatchOperationItem> IncludeSelectedModelDrawings(IReadOnlyList<BatchOperationItem> selected,
        IReadOnlyList<BatchOperationItem> all)
    {
        var models = selected.Select(item => item.Node).Where(node => node.Kind != CadDocumentKind.Drawing).ToArray();
        var paths = new HashSet<string>(models.Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>(models.Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId.Value));
        var complete = DistinctBatchItems(selected.Concat(all.Where(item => item.Node.Kind == CadDocumentKind.Drawing
            && item.Node.ReferencedModels.Any(model => paths.Contains(model.FullPath)
                || model.DocumentId.HasValue && ids.Contains(model.DocumentId.Value)))).ToArray());
        var newReferences = new HashSet<string>(complete.SelectMany(item => item.Node.ReferencedModels)
            .Where(model => !model.DocumentId.HasValue).Select(model => model.FullPath), StringComparer.OrdinalIgnoreCase);
        return DistinctBatchItems(complete.Concat(all.Where(item => newReferences.Contains(item.Node.FullPath)
            || item.Ancestors.Any(ancestor => newReferences.Contains(ancestor.FullPath)))));
    }

    private async Task CompleteRelatedDrawingFilesAsync(CadTreeNode tree, Guid projectId, CancellationToken token)
    {
        var documents = await apiClient.GetDocumentsAsync(projectId, token);
        ResetRemovedRegistrationBindings(tree, projectId, documents);
        foreach (var drawing in EnumerateCadNodes(tree).Where(node => node.Kind == CadDocumentKind.Drawing && !node.DocumentId.HasValue))
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(drawing.FullPath, out var source)
                && source.ProjectId == projectId && documents.Any(document => document.Id == source.DocumentId && document.Kind == (int)CadDocumentKind.Drawing))
            {
                drawing.DocumentId = source.DocumentId; drawing.OpenedVersionId = source.VersionId;
                drawing.Revision = drawing.CurrentRevision = source.Revision;
            }
        var relations = await apiClient.GetDocumentRelationsAsync(projectId, token);
        ReconcileProjectDrawings(tree, documents.ToDictionary(document => document.Id), relations);
        BindDesignDrawingFiles(tree);
        var projectCode = availableProjects.FirstOrDefault(project => project.Id == projectId)?.Code;
        foreach (var group in EnumerateCadNodes(tree).Where(node => node.Kind == CadDocumentKind.Drawing
            && node.DocumentId.HasValue && !File.Exists(node.FullPath)).GroupBy(node => node.DocumentId.Value))
        {
            token.ThrowIfCancellationRequested();
            var drawing = group.First();
            try
            {
            var version = (await apiClient.GetVersionsAsync(group.Key, token, 1)).FirstOrDefault();
            if (version == null || string.IsNullOrWhiteSpace(projectCode))
                throw new InvalidOperationException("已关联工程图尚无可获取的存档文件，不能完整提交：" + drawing.FileName);
            var directory = Path.Combine(ControlledWorkspaceManager.ProjectViewDirectory(WorkspaceSettingsStore.GetWorkspaceRoot(), projectCode),
                "图档缓存", "plm-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(directory, Path.GetFileName(drawing.FileName));
            Directory.CreateDirectory(directory);
            if (!File.Exists(target))
            {
                var downloaded = await apiClient.DownloadVersionToWorkspaceStageAsync(group.Key, version.Id, drawing.FileName, version.Sha256, token);
                try { File.Copy(downloaded, target, false); ControlledReferenceMaterializer.CopyResolvedBinding(downloaded, target); }
                finally { DeleteWorkspaceStage(downloaded); }
            }
            if (!VersionMatchesLocalFile(version, target, ComputeFileHash(target)))
                throw new InvalidOperationException("工程图缓存校验不一致，请备份并重新获取后提交：" + target);
            SetFileReadOnly(target, true);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, group.Key, projectId, version.Id,
                version.Revision?.Display, version.Sha256, version.FileLength))
                throw new IOException("无法写入工程图受控校验记录：" + drawing.FileName);
            foreach (var instance in group)
            {
                instance.FullPath = target; instance.OpenedVersionId = version.Id;
                instance.Revision = instance.CurrentRevision = version.Revision?.Display;
                instance.IsOutsideNativeFileScope = false;
                instance.DrawingUpdatePendingReason = null;
            }
            PluginDiagnosticLog.Record("工程图", "已补齐关联工程图本地文件：" + drawing.FileName);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (tree.Kind != CadDocumentKind.Drawing)
            {
                LogDiagnostic("关联工程图暂未补齐，模型可继续存档：" + drawing.FileName, error);
                foreach (var instance in group)
                {
                    instance.DrawingUpdatePendingReason = ArchivePreflightError(error);
                    instance.IsOutsideNativeFileScope = true;
                    instance.StructureIndexPending = true;
                }
            }
        }
    }

    private static void ResetRemovedRegistrationBindings(CadTreeNode tree, Guid projectId, IReadOnlyCollection<DocumentDto> documents)
    {
        var ids = new HashSet<Guid>(documents.Select(document => document.Id));
        foreach (var node in EnumerateArchiveNodes(tree).Where(node => node.DocumentId.HasValue && !ids.Contains(node.DocumentId.Value)))
        {
            // A registration-only binding has no archived version to preserve. A removed
            // failed registration may be prepared again; foreign/historical bindings stay intact.
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var binding)
                || binding.ProjectId != projectId || binding.DocumentId != node.DocumentId || binding.VersionId.HasValue) continue;
            node.DocumentId = null; node.ProvenanceDocumentId = null; node.ProvenanceProjectId = null;
            node.Revision = node.CurrentRevision = node.LatestRevision = string.Empty;
            node.CheckedOutBy = null; node.CheckoutSessionId = null; node.WorkState = CadWorkState.None;
            node.StoredVersionStateKnown = true; node.HasStoredVersion = false;
            PluginDiagnosticLog.Record("批次", "已解除失败登记的本机临时关联，图档将随整批成功后登记：" + node.FileName);
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
        node.DrawingVersion = document.DrawingVersion ?? "待核对";
        node.PublishedVersion = document.PublishedVersion ?? "未发布";
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
        RememberLocalEdit(node, document);
        if (string.IsNullOrWhiteSpace(document.CheckedOutBy) && node.DocumentId.HasValue)
        {
            checkoutReminderLevels.Remove(node.DocumentId.Value);
            RefreshCheckoutReminder();
        }
    }

    private void ApplyRegisteredDocumentToMatchingInstances(CadTreeNode source, DocumentDto document) =>
        ApplyPreparedDocumentToMatchingInstances(source, document, true);

    private void ApplyPreparedDocumentToMatchingInstances(CadTreeNode source, DocumentDto document, bool persistIdentity)
    {
        if (source == null || document == null) return;
        if (!persistIdentity)
            lock (checkoutDocumentSync) pendingArchiveDocumentIds.Add(document.Id);
        var matchingNodes = (currentTree == null
                ? Array.Empty<CadTreeNode>()
                : EnumerateCadNodes(currentTree)
                    .Where(candidate => PathsEqual(candidate.FullPath, source.FullPath)))
            .Concat(new[] { source })
            .Distinct()
            .ToArray();

        if (persistIdentity && !PdmDocumentIdentityStore.TryWrite(source.FullPath, document.Id, document.ProjectId))
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
                var matchesManifest = ControlledReferenceMaterializer.Matches(fullPath, file);
                if (!matchesManifest && PdmDocumentIdentityStore.TryReadLocalMetadata(fullPath, out var previous)
                    && previous.DocumentId == file.DocumentId && previous.ProjectId == manifest.ProjectId && previous.VersionId.HasValue)
                    continue; // Preserved edits keep their original version, not the latest manifest's version.
                if (!matchesManifest)
                    throw new IOException("已保留本地文件，但无法确认其原始存档版本；请从原工作副本恢复版本关联后重试：" + file.FileName);
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

        if (node.IsStoredImport)
        {
            node.DocumentId = null;
            node.ProvenanceDocumentId = null;
            node.ProvenanceProjectId = null;
            node.IsExternalProvenance = false;
            node.Revision = node.CurrentRevision = node.LatestRevision = string.Empty;
        }
        else if (PdmDocumentIdentityStore.TryRead(node.FullPath, out var documentId))
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
            var matchesDownloadedVersion = ControlledReferenceMaterializer.Matches(node.FullPath, file);
            if (matchesDownloadedVersion)
                PdmDocumentIdentityStore.TryWriteControlledVersion(node.FullPath, file.DocumentId, projectId,
                    file.VersionId, file.Revision, file.Sha256, file.FileLength);
            var hasBase = PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var localBase)
                && localBase.DocumentId == file.DocumentId;
            node.OpenedVersionId = hasBase ? localBase.VersionId : (Guid?)null;
            node.OpenedRevision = hasBase ? localBase.Revision ?? string.Empty : string.Empty;
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

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DocumentVersionDto>>> ApplyWorkingStatesAsync(
        CadTreeNode root,
        CancellationToken cancellationToken)
    {
        var nodes = EnumerateCadNodes(root).ToArray();
        var latestHashes = new Dictionary<Guid, string>();
        var latestStoredHashes = new Dictionary<Guid, string>();
        var versionsByDocument = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        var pathsToHash = nodes.Where(node => node.DocumentId.HasValue
                && !string.IsNullOrWhiteSpace(node.FullPath) && File.Exists(node.FullPath))
            .Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var localHashes = await Task.Run(() => ComputeCachedFileHashes(pathsToHash, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var nodesByDocument = nodes.Where(node => node.DocumentId.HasValue)
            .GroupBy(node => node.DocumentId.Value).ToDictionary(group => group.Key, group => (IReadOnlyList<CadTreeNode>)group.ToArray());
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
                Versions = await GetNavigationTreeVersionsAsync(documentId, nodesByDocument[documentId], localHashes, cancellationToken)
            }).ToArray();
            var batchResults = await Task.WhenAll(batchTasks);
            cancellationToken.ThrowIfCancellationRequested();
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

        await CompleteDrawingNavigationHistoriesAsync(nodes, versionsByDocument, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in versionsByDocument)
        {
            latestHashes[entry.Key] = VersionSourceSha256(entry.Value.FirstOrDefault()) ?? string.Empty;
            latestStoredHashes[entry.Key] = entry.Value.FirstOrDefault()?.Sha256 ?? string.Empty;
        }
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
            node.LatestStructureIndexPending = node.DocumentId.HasValue
                && versionsByDocument.TryGetValue(node.DocumentId.Value, out var indexVersions)
                ? ArchivedIndexPending(indexVersions.FirstOrDefault()?.ReferenceSnapshot) : (bool?)null;
            if (node.DocumentId.HasValue && versionsByDocument.TryGetValue(node.DocumentId.Value, out var propertyVersions))
            {
                var properties = propertyVersions.FirstOrDefault()?.PropertySnapshot;
                node.Description = FindSnapshotProperty(properties, "描述", "Description");
                node.Material = FindSnapshotProperty(properties, "材料", "Material");
            }
            if (node.DocumentId.HasValue && versionsByDocument.TryGetValue(node.DocumentId.Value, out var businessVersions))
            {
                var latestBusiness = businessVersions.FirstOrDefault();
                node.DrawingVersion = latestBusiness?.DrawingVersion ?? "未存档";
                node.PublishedVersion = latestBusiness?.LatestPublishedVersion ?? "未发布";
            }
            node.CurrentRevision = DetermineCurrentRevision(node, versionsByDocument, localHashes);
            node.WorkState = DetermineWorkState(node, localHashes, versionsByDocument);
        }

        foreach (var drawing in nodes.Where(node => node.Kind == CadDocumentKind.Drawing && node.DocumentId.HasValue))
        {
            if (!versionsByDocument.TryGetValue(drawing.DocumentId.Value, out var history) || history.Count == 0) continue;
            var archived = history[0].ReferenceSnapshot;
            var referenced = drawing.ReferencedModels.Select(reference => nodes.FirstOrDefault(model => model.Kind != CadDocumentKind.Drawing
                && PathsEqual(model.FullPath, reference.FullPath)) ?? reference).ToArray();
            var pending = referenced.Any(model => HasPendingDrawingModelChange(model, archived, versionsByDocument));
            drawing.DrawingReferenceIndexPending = DrawingModelIndexMissing(archived, drawing);
            drawing.DrawingUpdatePendingReason = pending ? "关联模型有修改或工程图尚未对应当前模型版本。" : null;
            drawing.RequiresModelUpdate = pending;
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
            if (DeferBackgroundTreeRefresh()) return;

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
                if (DeferBackgroundTreeRefresh()) return;
                if (IsSolidWorksCommandActive()) { ScheduleTreeRefresh(); return; }
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
                    if (DeferBackgroundTreeRefresh()) return;
                    ApplyCheckoutDocumentToMatchingInstances(nodes, checkout);

                    if (IsSolidWorksCommandActive()) { ScheduleTreeRefresh(); return; }

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
                    if (DeferBackgroundTreeRefresh()) return;
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
            || !VersionMatchesLocalFile(version, node.FullPath, localSha256)
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

    private async Task<IReadOnlyList<DocumentVersionDto>> GetTreeVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (metadataCacheSync)
            {
                DocumentVersionsCacheEntry cached;
                if (documentVersionsCache.TryGetValue(documentId, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                {
                    return cached.Versions;
                }
            }

            var versions = await apiClient.GetVersionsAsync(documentId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (metadataCacheSync)
            {
                documentVersionsCache[documentId] = new DocumentVersionsCacheEntry(
                    versions,
                    DateTime.UtcNow.AddMinutes(5));
            }

            return versions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        return !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath)
            && (VersionMatchesFile(version, sha256)
                || version != null && PdmDocumentIdentityStore.MatchesResolvedVersion(fullPath, version.Id, version.Sha256, sha256));
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

    internal static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current) =>
        ReferenceSnapshotMatchesTree(archived, current, true);

    private static bool OwnAssemblyStructureMatches(DocumentReferenceNodeDto archived, CadTreeNode current)
    {
        if (archived == null || archived.DocumentId != current.DocumentId || archived.Kind != (int)current.Kind
            || !string.Equals(archived.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)) return false;
        // Different saved configurations can have different components without any file edit.
        // File-content and property comparisons still detect actual changes independently.
        if (!string.Equals(archived.Configuration, current.Configuration, StringComparison.OrdinalIgnoreCase)) return true;
        var before = (archived.Children ?? new List<DocumentReferenceNodeDto>())
            .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency).ToList();
        var after = current.Children.Where(child => child.Kind != CadDocumentKind.Drawing && child.Role != CadReferenceRole.ExternalFileDependency).ToList();
        if (before.Count != after.Count) return false;
        foreach (var child in before)
        {
            var index = after.FindIndex(candidate => candidate.DocumentId == child.DocumentId
                && candidate.Kind == (CadDocumentKind)child.Kind
                && string.Equals(candidate.FileName, child.FileName, StringComparison.OrdinalIgnoreCase)
                && candidate.Configuration == child.Configuration && candidate.Quantity == child.Quantity
                && NormalizeReferenceStatus((int)candidate.Status) == NormalizeReferenceStatus(child.Status));
            if (index < 0) return false;
            after.RemoveAt(index);
        }
        return true;
    }

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current, bool isRoot,
        Func<DocumentReferenceNodeDto, CadTreeNode, bool> equivalentDrawingVersion = null) =>
        ReferenceSnapshotMatchesTree(archived, current, isRoot, equivalentDrawingVersion, null);

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current, bool isRoot,
        Func<DocumentReferenceNodeDto, CadTreeNode, bool> equivalentDrawingVersion,
        Func<DocumentReferenceNodeDto, CadTreeNode, bool> equivalentDrawingConfiguration)
    {
        if (archived == null || current == null
            || archived.DocumentId != current.DocumentId
            || !string.Equals(archived.FileName ?? string.Empty, current.FileName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || archived.Kind != (int)current.Kind
            || archived.Role != (int)current.Role)
        {
            return false;
        }

        if (current.Kind == CadDocumentKind.Drawing)
        {
            // An unloaded model may be represented by a native-inventory context node.
            // Its established drawing view still exists; do not mistake its tree role
            // for removal of the view. Unrelated external dependencies stay excluded.
            var currentModels = current.ReferencedModels.Where(model => (model.Role != CadReferenceRole.ExternalFileDependency
                || model.DocumentId.HasValue && (archived.Children ?? new List<DocumentReferenceNodeDto>()).Any(reference =>
                    reference.Role != (int)CadReferenceRole.ExternalFileDependency && reference.DocumentId == model.DocumentId
                    && reference.Kind == (int)model.Kind && string.Equals(reference.FileName, model.FileName, StringComparison.OrdinalIgnoreCase)))
                && (current.DrawingScopePaths == null || current.DrawingScopePaths.Contains(model.FullPath))).ToList();
            var scopedIds = new HashSet<Guid?>(currentModels.Select(model => model.DocumentId));
            var archivedModels = (archived.Children ?? new List<DocumentReferenceNodeDto>())
                .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency
                    && (current.DrawingScopePaths == null || scopedIds.Contains(child.DocumentId))).ToList();
            if (archivedModels.Count != currentModels.Count) return false;
            foreach (var model in archivedModels)
            {
                var index = currentModels.FindIndex(candidate => candidate.DocumentId == model.DocumentId
                    && (string.Equals(candidate.Configuration ?? string.Empty, model.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                        || equivalentDrawingConfiguration?.Invoke(model, candidate) == true)
                    && (string.Equals((candidate.CurrentRevision ?? string.Empty).TrimEnd('*'), model.Revision?.Display ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                        || equivalentDrawingVersion?.Invoke(model, candidate) == true));
                if (index < 0) return false;
                currentModels.RemoveAt(index);
            }
            return true;
        }

        // A standalone file can be represented by any occurrence in its parent.
        // That occurrence's quantity, suppression and part configuration are not file edits.
        if ((!isRoot || current.Kind != CadDocumentKind.Part)
                && !string.Equals(archived.Configuration ?? string.Empty, current.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !isRoot && archived.Quantity != current.Quantity
            || NormalizeReferenceStatus(archived.Status, isRoot) != NormalizeReferenceStatus((int)current.Status, isRoot))
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

        // An unloaded instance is not a deletion of the children in its verified version.
        if (!isRoot && SuppressedAssemblyArchive.CanReuse(current)) return true;

        var archivedChildren = (archived.Children ?? new List<DocumentReferenceNodeDto>())
            .Where(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency)
            .ToArray();
        var currentChildren = current.Children
            .Where(child => child.Kind != CadDocumentKind.Drawing && child.Role != CadReferenceRole.ExternalFileDependency)
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

    private static int NormalizeReferenceStatus(int status, bool fileRoot = false)
    {
        return status == (int)CadReferenceStatus.Hidden || status == (int)CadReferenceStatus.Lightweight
            || fileRoot && status == (int)CadReferenceStatus.Suppressed
            ? (int)CadReferenceStatus.Normal
            : status;
    }

    internal static bool NodeMatchesLatestFile(CadTreeNode node, string sha256)
    {
        if (node?.DocumentId != null && PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var baseline)
            && baseline.DocumentId == node.DocumentId && baseline.VersionId.HasValue
            && PdmDocumentIdentityStore.MatchesResolvedVersion(node.FullPath, baseline.VersionId.Value, node.LatestStoredSha256, sha256))
            return true;
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
        node.LatestStructureIndexPending = ArchivedIndexPending(latest?.ReferenceSnapshot);
        node.StoredReferenceWarnings = latest?.ReferenceSnapshot?.ReferenceWarnings?.ToArray() ?? Array.Empty<string>();
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

    private bool HistoricalEditMatchesLatest(
        CadTreeNode node,
        DocumentVersionDto latest,
        string fullPath,
        string localSha256)
    {
        if (node?.Kind != CadDocumentKind.Part
            || !node.DocumentId.HasValue
            || !historicalEditContexts.TryGetValue(node.DocumentId.Value, out var context)
            || !HistoricalVersionContentMatches(context.SourceVersion, latest))
        {
            return false;
        }

        return VersionMatchesLocalFile(context.SourceVersion, fullPath, localSha256);
    }

    private string BuildHistoricalChangeNote(CadTreeNode node, string changeNote)
    {
        if (node?.HistoricalRepair != null)
            return "历史资料补齐；所属装配=" + node.HistoricalRepair.OwnerNode.FileName
                + "；原存档版本=" + node.HistoricalRepair.OwnerVersionId
                + "；原文件校验=" + node.HistoricalRepair.FileSha256 + "；不计本次工程变更。";
        if (node == null
            || !node.DocumentId.HasValue
            || !historicalEditContexts.TryGetValue(node.DocumentId.Value, out var context))
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
            : string.Concat("当前图档正在由", ResolveUserDisplayName(node.CheckedOutBy), "编辑，不能", operation, "。"));
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

    private IReadOnlyDictionary<string, string> ComputeCachedFileHashes(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                var length = file.Length;
                var lastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks;
                FileHashCacheEntry cached;
                lock (fileHashCacheSync)
                {
                    fileHashCache.TryGetValue(path, out cached);
                }

                if (cached != null
                    && cached.Length == length
                    && cached.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
                {
                    hashes[path] = cached.Sha256;
                    continue;
                }

                var sha256 = ComputeFileHash(path);
                cancellationToken.ThrowIfCancellationRequested();
                file.Refresh();
                if (file.Length == length && file.LastWriteTimeUtc.Ticks == lastWriteTimeUtcTicks)
                {
                    lock (fileHashCacheSync)
                    {
                        if (fileHashCache.Count >= 4096)
                        {
                            fileHashCache.Clear();
                        }

                        fileHashCache[path] = new FileHashCacheEntry(length, lastWriteTimeUtcTicks, sha256);
                    }
                }

                hashes[path] = sha256;
            }
            catch (OperationCanceledException)
            {
                throw;
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
        return ArchiveFileFingerprint.Read(path);
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
        PluginDiagnosticLog.Record("提醒", message);
        if (ArchiveSession.Current != null) { ArchiveSession.Current.Fail(message); return; }
        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed)
        {
            return;
        }

        try
        {
            TopMostMessageBox.Show(taskPaneControl, message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            LogDiagnostic("ShowError", exception);
        }
    }

    private static void LogDiagnostic(string operation, Exception exception)
    {
        ClientDiagnostics.Record("error", operation, exception.ToString());
        try
        {
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            var line = ClientLogRedaction.Clean(string.Concat(DateTime.Now.ToString("O"), " ", operation, " ", exception, System.Environment.NewLine));
            RotateOperationLogIfNeeded(Path.Combine(directory, "addin-errors.log"));
            File.AppendAllText(Path.Combine(directory, "addin-errors.log"), line);
        }
        catch
        {
            // Diagnostics must never escape into SolidWorks.
        }
    }

    private static void LogOperation(string message)
    {
        message = ClientLogRedaction.Clean(message);
        ClientDiagnostics.Record("runtime", "插件操作", message);
        OperationLogQueue.Enqueue(string.Concat(
            DateTime.Now.ToString("O"),
            " ",
            message,
            System.Environment.NewLine));
        ScheduleOperationLogDrain();
    }

    private static void LogCheckInCheckpoint(string stage, string fullPath)
    {
        ClientDiagnostics.Record("runtime", "存档步骤/" + stage, fullPath);
        // Write before entering native code: an in-process crash can lose queued log entries.
        try
        {
            var label = stage switch
            {
                "batch-dialog-entry" => "打开整体操作",
                "batch-dialog-build-items" => "整理待处理图档",
                "batch-dialog-create" => "创建整体操作窗口",
                "batch-dialog-show" => "等待选择项目和提交范围",
                "batch-dialog-result-Cancel" => "已取消整体操作",
                "batch-dialog-result-OK" => "已确认整体操作范围",
                "open-start" => "开始加载临时图档",
                "open-end" => "临时图档加载完成",
                "properties-start" => "开始读取图档属性",
                "properties-end" => "图档属性读取完成",
                "close-start" => "开始关闭临时读取窗口",
                "close-end" => "临时读取窗口关闭完成",
                _ => null
            };
            if (label != null) PluginDiagnosticLog.Record("步骤", label + "：" + fullPath);
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "addin-checkin.log");
            RotateOperationLogIfNeeded(path);
            using (var process = Process.GetCurrentProcess())
            {
                File.AppendAllText(path, string.Concat(
                    DateTime.Now.ToString("O"), " stage=", stage, " path=", fullPath,
                    " thread=", Thread.CurrentThread.ManagedThreadId,
                    " apartment=", Thread.CurrentThread.GetApartmentState(),
                    " privateMB=", process.PrivateMemorySize64 / (1024 * 1024),
                    " version=", typeof(PdmAddin).Assembly.GetName().Version,
                    System.Environment.NewLine));
            }
        }
        catch
        {
            // Diagnostics must never escape into SolidWorks.
        }
    }

    private static void ScheduleOperationLogDrain()
    {
        if (Interlocked.Exchange(ref operationLogDrainScheduled, 1) == 0)
        {
            _ = Task.Run((Action)DrainOperationLogQueue);
        }
    }

    private static void DrainOperationLogQueue()
    {
        try
        {
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "addin-operations.log");
            RotateOperationLogIfNeeded(path);

            var buffer = new StringBuilder();
            string line;
            while (OperationLogQueue.TryDequeue(out line))
            {
                buffer.Append(line);
            }

            if (buffer.Length > 0)
            {
                File.AppendAllText(path, buffer.ToString());
            }
        }
        catch
        {
            // Operation markers must never escape into SolidWorks.
        }
        finally
        {
            Interlocked.Exchange(ref operationLogDrainScheduled, 0);
            if (!OperationLogQueue.IsEmpty)
            {
                ScheduleOperationLogDrain();
            }
        }
    }

    private static void RotateOperationLogIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < OperationLogMaxBytes)
        {
            return;
        }

        for (var index = OperationLogBackupCount; index >= 1; index--)
        {
            var source = index == 1 ? path : string.Concat(path, ".", index - 1);
            var destination = string.Concat(path, ".", index);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
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
        public PdmControlledFileMetadata OriginalMetadata { get; set; }
    }

    private sealed class LoadedDocumentSnapshot
    {
        public LoadedDocumentSnapshot(
            string path,
            string title,
            int documentType,
            bool hasUnsavedChanges,
            bool hasDiscardableReadOnlySaveFlag)
        {
            Path = path;
            Title = title;
            DocumentType = documentType;
            HasUnsavedChanges = hasUnsavedChanges;
            HasDiscardableReadOnlySaveFlag = hasDiscardableReadOnlySaveFlag;
        }

        public string Path { get; }
        public string Title { get; }
        public int DocumentType { get; }
        public bool HasUnsavedChanges { get; }
        public bool HasDiscardableReadOnlySaveFlag { get; }
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

    private sealed class HistoricalEditContext
    {
        public HistoricalEditContext(DocumentVersionDto sourceVersion, DocumentVersionDto latestVersion)
        {
            SourceVersion = sourceVersion;
            LatestVersion = latestVersion;
        }

        public DocumentVersionDto SourceVersion { get; }
        public DocumentVersionDto LatestVersion { get; }
    }

    private sealed class FileHashCacheEntry
    {
        public FileHashCacheEntry(long length, long lastWriteTimeUtcTicks, string sha256)
        {
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            Sha256 = sha256 ?? string.Empty;
        }

        public long Length { get; }
        public long LastWriteTimeUtcTicks { get; }
        public string Sha256 { get; }
    }

    private sealed class ActiveTreeCacheEntry
    {
        public ActiveTreeCacheEntry(CadTreeNode tree, Guid? projectId, DateTime lastAccessUtc)
        {
            Tree = tree;
            ProjectId = projectId;
            LastAccessUtc = lastAccessUtc;
        }

        public CadTreeNode Tree { get; }
        public Guid? ProjectId { get; }
        public DateTime LastAccessUtc { get; set; }
    }

    private sealed class ProjectDocumentsCacheEntry
    {
        public ProjectDocumentsCacheEntry(IReadOnlyList<DocumentDto> documents, DateTime expiresUtc)
        {
            Documents = documents ?? Array.Empty<DocumentDto>();
            ExpiresUtc = expiresUtc;
        }

        public IReadOnlyList<DocumentDto> Documents { get; }
        public DateTime ExpiresUtc { get; }
    }

    private sealed class DocumentVersionsCacheEntry
    {
        public DocumentVersionsCacheEntry(IReadOnlyList<DocumentVersionDto> versions, DateTime expiresUtc)
        {
            Versions = versions ?? Array.Empty<DocumentVersionDto>();
            ExpiresUtc = expiresUtc;
        }

        public IReadOnlyList<DocumentVersionDto> Versions { get; }
        public DateTime ExpiresUtc { get; }
    }

    private sealed class LinkedDrawingRenamePlan
    {
        public LinkedDrawingRenamePlan(
            CadTreeNode drawingNode,
            string oldDrawingPath,
            string newDrawingPath,
            string oldModelPath,
            string newModelPath)
        {
            DrawingNode = drawingNode;
            OldDrawingPath = oldDrawingPath;
            NewDrawingPath = newDrawingPath;
            OldModelPath = oldModelPath;
            NewModelPath = newModelPath;
        }

        public CadTreeNode DrawingNode { get; }
        public string OldDrawingPath { get; }
        public string NewDrawingPath { get; }
        public string OldModelPath { get; }
        public string NewModelPath { get; }
    }

    private enum RegistrationMatchKind
    {
        New,
        SameNameSameContent,
        SameNameDifferentContent,
        SameContentDifferentName,
        SameContentOtherProject,
        PendingFirstArchive
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

    private sealed class StagedArchiveFile
    {
        public StagedArchiveFile(CadTreeNode node, object request, string sourceHash, DocumentVersionDto latest)
        { Node = node; Request = request; SourceHash = sourceHash; Latest = latest; }
        public CadTreeNode Node { get; }
        public object Request { get; }
        public string SourceHash { get; }
        public DocumentVersionDto Latest { get; }
        public PdmApiClient.ArchiveReferenceSnapshot ReferenceSnapshot { get; set; }
        public long SourceLength { get; set; }
        public bool NativeIsolation { get; set; }
    }

    private sealed class PreparedArchiveFile
    {
        public PreparedArchiveFile(CadTreeNode node, IReadOnlyDictionary<string, string> properties, string uploadCopyPath,
            string sourceHash, DocumentVersionDto latest, StoredVersionFile storedFile)
        {
            Node = node; Properties = properties; UploadCopyPath = uploadCopyPath; SourceHash = sourceHash; Latest = latest; StoredFile = storedFile;
            SourceLength = File.Exists(node.FullPath) ? new FileInfo(node.FullPath).Length : 0;
            UploadCopySha256 = string.IsNullOrWhiteSpace(uploadCopyPath) ? null : ComputeFileHash(uploadCopyPath);
        }
        public CadTreeNode Node { get; }
        public IReadOnlyDictionary<string, string> Properties { get; }
        public string UploadCopyPath { get; }
        public string UploadCopySha256 { get; }
        public string SourceHash { get; }
        public DocumentVersionDto Latest { get; }
        public StoredVersionFile StoredFile { get; }
        public long SourceLength { get; }
        public PdmApiClient.ArchiveReferenceSnapshot ReferenceSnapshot { get; set; }
        public Guid? NativeIsolationSourceVersionId { get; set; }
        public string NativeIsolationSourceSha256 { get; set; }
        public string NativeIsolationEvidence { get; set; }
        public bool? NativeIsolationKeepEditing { get; set; }
        public bool NativeIsolationVerified { get; set; }
    }

    private static IReadOnlyList<string[]> BuildArchiveScopeRows(IReadOnlyList<BatchOperationItem> selected, BatchCheckInPlan plan)
    {
        var planned = new HashSet<string>(plan.Items.Select(item => item.Node.FullPath), StringComparer.OrdinalIgnoreCase);
        return selected.Concat(plan.Items).GroupBy(item => item.Node.FullPath, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var node = group.First().Node;
            var included = planned.Contains(node.FullPath);
            var reason = "未变化，沿用已存档版本";
            if (included)
            {
                plan.Preflights.TryGetValue(node.FullPath, out var preflight);
                var latest = preflight?.Versions.FirstOrDefault();
                reason = !node.DocumentId.HasValue || latest == null ? "首次登记存档" :
                    !VersionMatchesLocalFile(latest, node.FullPath, preflight.LocalSha256) ? "保存的文件内容或属性有变化" :
                    node.Kind == CadDocumentKind.Drawing ? "核对本次关联模型版本及工程图" :
                    node.Kind == CadDocumentKind.Assembly ? "所选范围内的装配引用、配置或子件版本有变化" : "核对本次属性及引用变化";
            }
            return new[] { node.FileName, node.Configuration, included ? "拟提交" : "复用已有版本", reason };
        }).ToArray();
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
        public List<string> Successes { get; } = new List<string>();
        public int PreparedPermissions { get; set; }
        public int CreatedVersions { get; set; }
        public int HistoricalRepairs { get; set; }
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
