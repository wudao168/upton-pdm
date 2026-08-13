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
    private readonly Dictionary<string, ControlledOpenManifestDto> readOnlyOpenManifests = new Dictionary<string, ControlledOpenManifestDto>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> explicitProjectPaths = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
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
    private int treeRefreshInProgress;
    private int controlledOpenInProgress;
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
            readOnlyOpenManifests.Clear();
            explicitProjectPaths.Clear();
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
        taskPaneControl.NodeSelected += OnNodeSelected;
        taskPaneControl.OpenRequested += OnOpenRequested;
        taskPaneControl.GetLatestVersionRequested += OnGetLatestVersionRequested;
        taskPaneControl.CheckoutRequested += OnCheckoutRequested;
        taskPaneControl.CheckInRequested += OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested += OnDiscardCheckoutRequested;
        taskPaneControl.BatchOperationRequested += OnBatchOperationRequested;
        taskPaneControl.VersionsRequested += OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested += OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested += OnCompareVersionsRequested;
        taskPaneControl.ControlledOpenRequested += OnControlledOpenRequested;
        taskPaneControl.ProjectBrowseRequested += OnProjectBrowseRequested;
    }

    private void UnwireEvents()
    {
        if (taskPaneControl == null)
        {
            return;
        }

        taskPaneControl.LoginRequested -= OnLoginRequested;
        taskPaneControl.RefreshRequested -= OnRefreshRequested;
        taskPaneControl.NodeSelected -= OnNodeSelected;
        taskPaneControl.OpenRequested -= OnOpenRequested;
        taskPaneControl.GetLatestVersionRequested -= OnGetLatestVersionRequested;
        taskPaneControl.CheckoutRequested -= OnCheckoutRequested;
        taskPaneControl.CheckInRequested -= OnCheckInRequested;
        taskPaneControl.DiscardCheckoutRequested -= OnDiscardCheckoutRequested;
        taskPaneControl.BatchOperationRequested -= OnBatchOperationRequested;
        taskPaneControl.VersionsRequested -= OnVersionsRequested;
        taskPaneControl.OpenHistoryRequested -= OnOpenHistoryRequested;
        taskPaneControl.CompareVersionsRequested -= OnCompareVersionsRequested;
        taskPaneControl.ControlledOpenRequested -= OnControlledOpenRequested;
        taskPaneControl.ProjectBrowseRequested -= OnProjectBrowseRequested;
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
        ScheduleTreeRefresh();
        return 0;
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
            model?.Extension.SelectByID2(eventArgs.Node.ComponentSelectionName, "COMPONENT", 0, 0, 0, false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
        }
        catch
        {
            // Selection synchronization must not break the task pane.
        }
    }

    private void OnOpenRequested(object sender, CadTreeNodeEventArgs eventArgs) =>
        BeginControlledOpen(eventArgs.Node, ControlledOpenMode.LatestReadOnly);

    private void OnGetLatestVersionRequested(object sender, CadTreeNodeEventArgs eventArgs) =>
        BeginControlledOpen(eventArgs.Node, ControlledOpenMode.LatestReadOnly);

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
            if (!forEdit)
            {
                readOnlyOpenManifests[NormalizeDirectory(controlledWorkspace.GetReadOnlyDirectory(manifest))] = manifest;
            }
            if (forEdit)
            {
                var documents = await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
                var rootDocument = documents.FirstOrDefault(item => item.Id == manifest.RootDocumentId)
                    ?? throw new InvalidOperationException("打开清单的根图档不存在。");
                var alreadyCheckedOut = string.Equals(rootDocument.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);
                var oldWorkingRoot = controlledWorkspace.GetWorkingRootPath(manifest);
                var oldWorkingOpen = File.Exists(oldWorkingRoot) && FindLoadedDocument(oldWorkingRoot) != null;
                if (oldWorkingOpen)
                {
                    EnsureWorkspaceDocumentsAreSaved();
                    CloseDocumentsForWorkspaceUpdate(controlledWorkspace.GetWorkingFilePaths(manifest), oldWorkingRoot);
                }

                if (!alreadyCheckedOut)
                {
                    await apiClient.CheckoutAsync(manifest.RootDocumentId, lifetime.Token);
                }
                try
                {
                    rootPath = controlledWorkspace.PromoteToEditable(manifest, readOnlyRootPath);
                }
                catch
                {
                    if (!alreadyCheckedOut)
                    {
                        try { await apiClient.DiscardCheckoutAsync(manifest.RootDocumentId, lifetime.Token); }
                        catch (Exception rollbackException) { LogDiagnostic("ControlledOpen checkout rollback", rollbackException); }
                    }
                    throw;
                }
            }

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
            MessageBox.Show(
                taskPaneControl,
                string.Concat("已获取最新工作文件和编辑权限。", result.UpdatedFiles > 0 ? string.Concat("\r\n已更新本地文件：", result.UpdatedFiles, "个。") : "\r\n本地已是最新版本。"),
                "UPTON PDM",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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

    private async void OpenBatchOperationDialog(Guid? preferredProjectId, BatchOperationKind initialOperation)
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

        var items = BuildBatchOperationItems(currentTree);
        if (items.Count == 0)
        {
            ShowError("当前结构中没有可操作的SolidWorks图档。");
            return;
        }

        var initialProjectId = preferredProjectId ?? GetExplicitProjectId(currentTree.FullPath);
        using (var dialog = new BatchOperationDialog(items, availableProjects, initialProjectId, authenticatedUsername, initialOperation))
        {
            if (dialog.ShowDialog(taskPaneControl) != DialogResult.OK)
            {
                return;
            }

            var selectedProjectId = dialog.SelectedProjectId.Value;
            var selectedProjectDisplay = dialog.SelectedProjectDisplay;
            try
            {
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
            try
            {
                if (dialog.Operation == BatchOperationKind.AcquireLatestAndCheckout)
                {
                    var result = await AcquireLatestAndCheckoutAsync(dialog.SelectedItems, selectedProjectId);
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
                    var preparationItems = dialog.SelectedItems
                        .Where(item => !IsCheckedOutByCurrentUser(item.Node))
                        .ToArray();
                    var preparedPermissions = 0;
                    if (preparationItems.Length > 0)
                    {
                        var preparation = await AcquireLatestAndCheckoutAsync(preparationItems, selectedProjectId);
                        preparedPermissions = preparation.CheckedOutFiles;
                    }

                    var result = await CheckInBatchAsync(dialog.SelectedItems, selectedProjectId, dialog.ChangeNote);
                    var message = string.Concat(
                        "整套提交完成。\r\n归属项目：", selectedProjectDisplay,
                        "\r\n自动登记/准备权限：", preparedPermissions, "个",
                        "\r\n生成新版本：", result.CreatedVersions, "个",
                        "\r\n无变更并结束编辑：", result.UnchangedFiles, "个",
                        "\r\n失败：", result.Failures.Count, "个");
                    if (result.Failures.Count > 0)
                    {
                        message = string.Concat(message, "\r\n\r\n", string.Join("\r\n", result.Failures.Take(8)));
                    }

                    MessageBox.Show(
                        taskPaneControl,
                        message,
                        "UPTON PDM",
                        MessageBoxButtons.OK,
                        result.Failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }

                RememberExplicitProjectPaths(dialog.SelectedItems, selectedProjectId);
                currentProjectId = selectedProjectId;
                taskPaneControl.SelectProject(selectedProjectId);
            }
            catch (Exception exception)
            {
                LogDiagnostic("OnBatchOperationRequested", exception);
                ShowError(exception.Message);
            }
            finally
            {
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
        Guid projectId)
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
                var registered = await apiClient.RegisterDocumentAsync(projectId, node, lifetime.Token);
                node.DocumentId = registered.Id;
                node.Revision = registered.Revision?.Display ?? node.Revision;
                node.CheckedOutBy = registered.CheckedOutBy;
            }

            var checkedOut = 0;
            var newlyCheckedOut = new List<CadTreeNode>();
            try
            {
                foreach (var item in items)
                {
                    var node = item.Node;
                    var wasAlreadyCheckedOut = IsCheckedOutByCurrentUser(node);
                    var document = await apiClient.CheckoutAsync(node.DocumentId.Value, lifetime.Token);
                    node.CheckedOutBy = document.CheckedOutBy;
                    node.Revision = document.Revision?.Display ?? node.Revision;
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
                        var discarded = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, lifetime.Token);
                        node.CheckedOutBy = discarded.CheckedOutBy;
                        node.Revision = discarded.Revision?.Display ?? node.Revision;
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
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "UPTON PDM",
            "workspace-backup",
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
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectBatchOperationItems(root, 0, result, paths);
        return result;
    }

    private static void CollectBatchOperationItems(CadTreeNode node, int depth, ICollection<BatchOperationItem> target, ISet<string> paths)
    {
        if (node == null)
        {
            return;
        }

        if (ToSolidWorksDocumentType(node.Kind) != (int)swDocumentTypes_e.swDocNONE
            && !string.IsNullOrWhiteSpace(node.FullPath)
            && paths.Add(node.FullPath))
        {
            target.Add(new BatchOperationItem(node, depth));
        }

        foreach (var child in node.Children)
        {
            CollectBatchOperationItems(child, depth + 1, target, paths);
        }
    }

    private async Task<BatchCheckInResult> CheckInBatchAsync(
        IReadOnlyList<BatchOperationItem> selectedItems,
        Guid projectId,
        string changeNote)
    {
        if (string.IsNullOrWhiteSpace(changeNote))
        {
            throw new InvalidOperationException("整套提交必须填写变更说明。");
        }

        var items = DistinctBatchItems(selectedItems);
        ValidateBatchFileNames(items);
        var result = new BatchCheckInResult();
        var originalDocumentPath = (application.ActiveDoc as IModelDoc2)?.GetPathName() ?? string.Empty;

        try
        {
            foreach (var item in items
                .OrderByDescending(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.Node.Kind == CadDocumentKind.Assembly ? 1 : 0)
                .ThenBy(candidate => candidate.Node.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var node = item.Node;
                try
                {
                    ValidateBatchCheckInNode(node);
                    var fileResult = await CheckInBatchNodeAsync(
                        node,
                        projectId,
                        changeNote.Trim(),
                        item.Depth == 0 && node.Kind == CadDocumentKind.Assembly);
                    if (fileResult.VersionCreated)
                    {
                        result.CreatedVersions++;
                    }
                    else
                    {
                        result.UnchangedFiles++;
                    }
                }
                catch (Exception exception)
                {
                    LogDiagnostic(string.Concat("CheckInBatch.", node?.FileName), exception);
                    result.Failures.Add(string.Concat(node?.FileName ?? "未知图档", "：", exception.Message));
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
        bool isProjectRoot)
    {
        var uploadCopyPath = string.Empty;
        try
        {
            var document = OpenOrActivateDocumentOnSolidWorksThread(
                node.FullPath,
                ToSolidWorksDocumentType(node.Kind),
                node.Configuration ?? string.Empty);
            var activePath = document?.GetPathName() ?? string.Empty;
            if (document == null || !PathsEqual(activePath, node.FullPath))
            {
                throw new IOException("未能安全激活图档。");
            }
            EnsureDocumentEditable(document, activePath);

            var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token);
            var latest = versions.FirstOrDefault();
            ApplyLatestVersion(node, latest);
            var referenceChanged = !ReferenceSnapshotMatchesTree(latest?.ReferenceSnapshot, node);
            if (!referenceChanged
                && !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && !document.GetSaveFlag()
                && VersionMatchesLocalFile(latest, activePath, ComputeFileHash(activePath)))
            {
                await CompleteUnchangedEditAsync(node, activePath);
                return new BatchNodeCheckInResult(false);
            }

            var saveErrors = 0;
            var saveWarnings = 0;
            LogOperation(string.Concat("Batch check-in Save3 start path=", activePath));
            var saved = document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErrors, ref saveWarnings);
            LogOperation(string.Concat("Batch check-in Save3 end path=", activePath, " saved=", saved, " errors=", saveErrors, " warnings=", saveWarnings));
            if (!saved || saveErrors != 0)
            {
                throw new IOException((saveErrors & (int)swFileSaveError_e.swReadOnlySaveError) != 0
                    ? "当前图档为只读文件，请先获取编辑权限。"
                    : string.Concat("SolidWorks保存失败，错误码：", saveErrors, "，警告码：", saveWarnings));
            }

            if (!referenceChanged
                && !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latest, activePath, ComputeFileHash(activePath)))
            {
                await CompleteUnchangedEditAsync(node, activePath);
                return new BatchNodeCheckInResult(false);
            }

            var modelProperties = ReadModelProperties(document);
            uploadCopyPath = CreateCheckInUploadCopy(document, activePath, node.DocumentId.Value);
            var storedFile = await apiClient.UploadVersionFileAsync(
                projectId,
                uploadCopyPath,
                node.DocumentId.Value,
                activePath,
                lifetime.Token);
            var checkIn = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                changeNote,
                storedFile,
                modelProperties,
                isProjectRoot,
                referenceChanged,
                lifetime.Token);
            node.CheckedOutBy = checkIn.Document.CheckedOutBy;
            node.Revision = checkIn.Version?.Revision?.Display ?? checkIn.Document.Revision?.Display ?? node.Revision;
            node.WorkState = CadWorkState.None;
            ApplyLatestVersion(node, checkIn.Version);
            ProtectLoadedDocument(activePath);
            return new BatchNodeCheckInResult(checkIn.VersionCreated);
        }
        finally
        {
            DeleteCheckInUploadCopy(uploadCopyPath);
        }
    }

    private async Task CompleteUnchangedEditAsync(CadTreeNode node, string activePath)
    {
        var unchanged = await apiClient.CompleteEditWithoutChangesAsync(
            node.DocumentId.Value,
            node.LatestStoredSha256,
            lifetime.Token);
        node.CheckedOutBy = unchanged.CheckedOutBy;
        node.Revision = unchanged.Revision?.Display ?? node.Revision;
        node.WorkState = CadWorkState.None;
        ProtectLoadedDocument(activePath);
    }

    private void OnCheckInRequested(object sender, CadTreeNodeEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node == null)
        {
            return;
        }


        if (IsHistoricalPreviewContext(node))
        {
            ShowError("历史版本为只读预览，不能提交存档。请关闭历史版本并打开当前工作文件。 ");
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
            OpenBatchOperationDialog(currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath), BatchOperationKind.CheckIn);
            return;
        }

        if (!EnsureServerDocument(node))
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
            ShowError("已有PDM工作文件任务正在进行，请稍候再试。");
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
            var referenceChanged = !ReferenceSnapshotMatchesTree(latestVersion?.ReferenceSnapshot, node);

            if (!referenceChanged
                && !document.GetSaveFlag()
                && !string.IsNullOrWhiteSpace(node.LatestStoredSha256))
            {
                var unchangedSha256 = ComputeFileHash(activePath);
                if (VersionMatchesLocalFile(latestVersion, activePath, unchangedSha256))
                {
                    var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, node.LatestStoredSha256, lifetime.Token);
                    node.CheckedOutBy = unchanged.CheckedOutBy;
                    node.Revision = unchanged.Revision?.Display ?? node.Revision;
                    node.WorkState = CadWorkState.None;
                    ProtectLoadedDocument(activePath);
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
                var message = (saveErrors & (int)swFileSaveError_e.swReadOnlySaveError) != 0
                    ? "当前图档为只读文件，不能提交存档。请确认打开的是当前工作文件，并已获取编辑权限。"
                    : string.Concat("SolidWorks保存图档失败，未提交存档。错误码：", saveErrors, "，警告码：", saveWarnings);
                ShowError(message);
                return;
            }

            var currentSha256 = ComputeFileHash(activePath);
            if (!referenceChanged
                && !string.IsNullOrWhiteSpace(node.LatestStoredSha256)
                && VersionMatchesLocalFile(latestVersion, activePath, currentSha256))
            {
                var unchanged = await apiClient.CompleteEditWithoutChangesAsync(node.DocumentId.Value, node.LatestStoredSha256, lifetime.Token);
                node.CheckedOutBy = unchanged.CheckedOutBy;
                node.Revision = unchanged.Revision?.Display ?? node.Revision;
                node.WorkState = CadWorkState.None;
                ProtectLoadedDocument(activePath);
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
            var result = await apiClient.CheckInAsync(
                node.DocumentId.Value,
                projectId,
                node,
                changeNote,
                storedFile,
                modelProperties,
                isProjectRoot,
                referenceChanged,
                lifetime.Token);
            node.CheckedOutBy = result.Document.CheckedOutBy;
            node.Revision = result.Version?.Revision?.Display ?? result.Document.Revision?.Display ?? node.Revision;
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
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "UPTON PDM",
                    "Workspace"))
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
            if (taskPaneControl.SelectedNode?.DocumentId == documentId)
                taskPaneControl.ShowVersions(documentId.Value, eventArgs.Node.FileName, versions);
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

    private void OnOpenHistoryRequested(object sender, DocumentVersionEventArgs eventArgs) =>
        BeginControlledOpen(currentProjectId ?? Guid.Empty, eventArgs.DocumentId, ControlledOpenMode.SpecificReadOnly, eventArgs.Version.Id);

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
        if (application?.ActiveDoc == null)
        {
            ClearActiveDocumentContext();
            return;
        }

        try
        {
            taskPaneControl.SetProjectContextAvailable(true);
            var scannedTree = scanner.ScanActiveDocument();
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
            if (controlledManifest != null)
            {
                currentProjectId = controlledManifest.ProjectId;
                taskPaneControl.SelectProject(controlledManifest.ProjectId);
            }
            taskPaneControl.SetTree(currentTree);
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
            if (!resolvedProjectId.HasValue && !string.IsNullOrWhiteSpace(tree.FileName))
            {
                var documentLookups = await Task.WhenAll(availableProjects.Select(async project => new
                {
                    ProjectId = project.Id,
                    Documents = await apiClient.GetDocumentsAsync(project.Id, lifetime.Token)
                }));
                var matchingProjects = documentLookups
                    .Where(result => result.Documents.Any(document =>
                        string.Equals(document.FileName, tree.FileName, StringComparison.OrdinalIgnoreCase)))
                    .Select(result => result.ProjectId)
                    .Distinct()
                    .Take(2)
                    .ToArray();
                if (matchingProjects.Length == 1)
                {
                    resolvedProjectId = matchingProjects[0];
                    RememberExplicitProjectPath(tree.FullPath, resolvedProjectId.Value);
                }
            }

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

            var byFileName = documents
                .GroupBy(document => document.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            ApplyMetadata(targetTree, byFileName);
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
            var treeTask = apiClient.GetReferenceTreeAsync(projectId, lifetime.Token);
            await Task.WhenAll(documentsTask, treeTask);
            var documents = documentsTask.Result.ToDictionary(item => item.Id);
            taskPaneControl.SetProjectTree(projectId, MapProjectTree(treeTask.Result, documents));
        }
        catch (Exception exception)
        {
            taskPaneControl?.SetProjectTree(projectId, null);
            LogDiagnostic("LoadProjectTreeAsync", exception);
        }
    }

    private static CadTreeNode MapProjectTree(DocumentReferenceNodeDto source, IReadOnlyDictionary<Guid, DocumentDto> documents)
    {
        if (source == null) return null;
        documents.TryGetValue(source.DocumentId ?? Guid.Empty, out var document);
        var node = new CadTreeNode
        {
            DocumentId = source.DocumentId,
            InstancePath = source.InstancePath ?? string.Empty,
            ComponentSelectionName = string.Empty,
            FileName = source.FileName ?? document?.FileName ?? string.Empty,
            FullPath = string.Empty,
            DisplayName = source.DisplayName ?? document?.Name ?? source.FileName ?? string.Empty,
            Kind = (CadDocumentKind)source.Kind,
            Configuration = source.Configuration ?? string.Empty,
            Quantity = Math.Max(1, source.Quantity),
            Status = (CadReferenceStatus)source.Status,
            Revision = source.Revision?.Display ?? document?.Revision?.Display ?? string.Empty,
            CurrentRevision = source.Revision?.Display ?? document?.Revision?.Display ?? string.Empty,
            LatestRevision = document?.Revision?.Display ?? source.Revision?.Display ?? string.Empty,
            CheckedOutBy = source.CheckedOutBy ?? document?.CheckedOutBy
        };
        var seenInstancePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in source.Children ?? new List<DocumentReferenceNodeDto>())
        {
            if (!string.IsNullOrWhiteSpace(child.InstancePath) && !seenInstancePaths.Add(child.InstancePath))
            {
                continue;
            }
            node.Children.Add(MapProjectTree(child, documents));
        }
        return node;
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

    private static void ApplyMetadata(CadTreeNode node, IReadOnlyDictionary<string, DocumentDto> documents)
    {
        if (node == null)
        {
            return;
        }

        if (!node.IsHistoricalPreview
            && !string.IsNullOrWhiteSpace(node.FileName)
            && documents.TryGetValue(node.FileName, out var document))
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

    private ControlledOpenManifestDto ApplyControlledOpenMetadata(CadTreeNode root)
    {
        if (root?.IsHistoricalPreview != true || string.IsNullOrWhiteSpace(root.FullPath))
        {
            return null;
        }

        var rootPath = Path.GetFullPath(root.FullPath);
        var context = readOnlyOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(rootPath, pair.Key));
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

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current) =>
        ReferenceSnapshotMatchesTree(archived, current, true);

    private static bool ReferenceSnapshotMatchesTree(DocumentReferenceNodeDto archived, CadTreeNode current, bool isRoot)
    {
        if (archived == null || current == null
            || archived.DocumentId != current.DocumentId
            || !string.Equals(archived.InstancePath ?? string.Empty, current.InstancePath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(archived.FileName ?? string.Empty, current.FileName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(archived.DisplayName ?? string.Empty, current.DisplayName ?? string.Empty, StringComparison.Ordinal)
            || archived.Kind != (int)current.Kind
            || !string.Equals(archived.Configuration ?? string.Empty, current.Configuration ?? string.Empty, StringComparison.Ordinal)
            || archived.Quantity != current.Quantity
            || archived.Status != (int)current.Status)
        {
            return false;
        }

        if (!isRoot
            && !string.Equals(
                archived.Revision?.Display ?? string.Empty,
                current.Revision ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var archivedChildren = archived.Children ?? new List<DocumentReferenceNodeDto>();
        if (archivedChildren.Count != current.Children.Count)
        {
            return false;
        }

        for (var index = 0; index < archivedChildren.Count; index++)
        {
            if (!ReferenceSnapshotMatchesTree(archivedChildren[index], current.Children[index], false))
            {
                return false;
            }
        }

        return true;
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
    }

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
        && string.Equals(node.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase);

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

    private sealed class BatchCheckInResult
    {
        public int CreatedVersions { get; set; }
        public int UnchangedFiles { get; set; }
        public List<string> Failures { get; } = new List<string>();
    }
}
