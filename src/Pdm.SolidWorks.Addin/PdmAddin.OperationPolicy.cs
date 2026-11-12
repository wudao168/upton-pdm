using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Queue<Action> queuedControlledOpens = new Queue<Action>();
    private readonly HashSet<string> queuedControlledOpenKeys = new HashSet<string>();

    private int OnAssemblyDeleteSelectionPreNotify()
    {
        try
        {
            var document = assemblyEventDocument;
            var selection = document?.SelectionManager as ISelectionMgr;
            if (selection == null) return 0;
            var blockedOwners = new List<string>();
            for (var index = 1; index <= selection.GetSelectedObjectCount2(-1); index++)
            {
                var type = (swSelectType_e)selection.GetSelectedObjectType3(index, -1);
                string ownerPath;
                if (type == swSelectType_e.swSelCOMPONENTS)
                {
                    var component = selection.GetSelectedObject6(index, -1) as IComponent2;
                    if (component == null) throw new InvalidOperationException("所选装配实例尚未确认。");
                    ownerPath = AssemblyComponentOwnerPath(document, component);
                }
                else if (type is swSelectType_e.swSelMATES or swSelectType_e.swSelMATEGROUP or swSelectType_e.swSelMATEGROUPS
                    or swSelectType_e.swSelMATESUPPLEMENT || type == swSelectType_e.swSelBODYFEATURES
                    && selection.GetSelectedObject6(index, -1) is IFeature feature
                    && feature.GetTypeName2().StartsWith("Mate", StringComparison.OrdinalIgnoreCase))
                {
                    var occurrence = selection.GetSelectedObjectsComponent4(index, -1) as IComponent2;
                    ownerPath = document.GetPathName();
                    if (occurrence != null)
                    {
                        var containingPath = occurrence.GetPathName();
                        ownerPath = !string.IsNullOrWhiteSpace(containingPath) && DocumentKindFromPath(containingPath) == CadDocumentKind.Assembly
                            && !occurrence.IsVirtual ? containingPath : AssemblyComponentOwnerPath(document, occurrence);
                    }
                }
                else continue;
                if (CanEditAssemblyStructure(ownerPath)) continue;
                blockedOwners.Add(ownerPath);
                LogOperation("未获取所属装配体权限，删除前已拦截 path=" + ownerPath);
            }
            if (blockedOwners.Count > 0) { QueueAssemblyPermission(document, blockedOwners, 16, "删除组件或配合"); return 1; }
            return 0;
        }
        catch (Exception error)
        {
            LogDiagnostic("删除装配实例前核对权限", error);
            taskPaneControl?.SetCheckoutReminder("已取消删除：暂未确认所属装配体的有效编辑权限，请核对后重试。当前结构未因本次删除而改变。", true);
            return 1;
        }
    }

    private static string AssemblyComponentOwnerPath(IModelDoc2 document, IComponent2 component)
    {
        for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
        {
            var path = (parent.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? parent.GetPathName();
            if (!parent.IsVirtual && !string.IsNullOrWhiteSpace(path) && !IsSolidWorksTemporaryVirtualComponentPath(path)
                && !NativeArchiveManifest.IsInterconnectCachePath(path)) return path;
        }
        return document.GetPathName();
    }

    private bool CanEditAssemblyStructure(string ownerPath)
    {
        if (string.IsNullOrWhiteSpace(ownerPath)) return true; // A new, unmanaged local assembly.
        var node = currentTree == null ? null : EnumerateCadNodes(currentTree).FirstOrDefault(candidate => PathsEqual(candidate.FullPath, ownerPath));
        PdmDocumentIdentityStore.TryReadLocalMetadata(ownerPath, out var binding);
        var id = binding?.DocumentId ?? node?.DocumentId;
        if (node?.IsReadOnlyPreview == true || node?.IsHistoricalPreview == true || IsReadOnlyPreviewPath(ownerPath)
            || IsHistoricalDerivedPath(ownerPath)) return false;
        if (!id.HasValue) return !controlledOpenManifests.Any(context => context.Value.Files.Any(file =>
            PathsEqual(ownerPath, Path.Combine(context.Key, file.RelativePath ?? file.FileName))));
        if (node?.DocumentId.HasValue == true && node.DocumentId != id
            || node != null && !IsCheckedOutByCurrentUser(node)) return false;
        lock (checkoutDocumentSync)
            if (!activeCheckoutDocumentIds.Contains(id.Value)) return false;
        return localEdits.TryGetValue(id.Value, out var edit) && edit.DocumentId == id && edit.SessionId == checkoutSessionId
            && !edit.Closed && !edit.DiscardPending && !edit.PendingClose && !edit.RecoveryRequired
            && !edit.CheckoutSessionLost && !edit.AcquisitionRollbackPending && PathsEqual(edit.SourcePath, ownerPath);
    }

    private void QueueControlledOpen(Guid projectId, Guid documentId, ControlledOpenMode mode,
        Guid? versionId, Guid? checkoutDocumentId, IReadOnlyCollection<Guid> checkoutDocumentIds)
        => EnqueueControlledOpen(projectId, documentId, mode, versionId, checkoutDocumentId, checkoutDocumentIds, null);

    private void EnqueueControlledOpen(Guid projectId, Guid documentId, ControlledOpenMode mode,
        Guid? versionId, Guid? checkoutDocumentId, IReadOnlyCollection<Guid> checkoutDocumentIds, string referencePath)
    {
        var ids = checkoutDocumentIds?.ToArray();
        var key = projectId + "|" + documentId + "|" + mode + "|" + versionId + "|" + checkoutDocumentId
            + "|" + string.Join(",", (ids ?? Array.Empty<Guid>()).OrderBy(id => id)) + "|" + referencePath;
        if (queuedControlledOpenKeys.Add(key))
            queuedControlledOpens.Enqueue(() =>
            {
                queuedControlledOpenKeys.Remove(key);
                BeginControlledOpenCore(projectId, documentId, mode, versionId, checkoutDocumentId, ids, referencePath);
            });
        taskPaneControl?.SetCheckoutReminder("当前操作完成后将自动打开所选PLM图档；重复请求已合并，无需再次点击。", false);
        LogOperation("[A10] PLM打开请求已排队 document=" + documentId + " queued=" + queuedControlledOpens.Count);
    }

    private void TryStartQueuedControlledOpen()
    {
        if (disconnecting || taskPaneControl == null || taskPaneControl.IsDisposed || apiClient == null || !apiClient.IsAuthenticated
            || Volatile.Read(ref controlledOpenInProgress) != 0 || Volatile.Read(ref openOperationInProgress) != 0
            || Volatile.Read(ref workspaceOperationInProgress) != 0 || IsSolidWorksCommandActive()
            || queuedControlledOpens.Count == 0) return;
        queuedControlledOpens.Dequeue()();
    }
}
