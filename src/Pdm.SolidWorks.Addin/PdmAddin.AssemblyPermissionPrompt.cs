using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private PendingAssemblyOperation pendingAssemblyOperation;
    private bool assemblyPermissionPromptActive;

    // IDs from SOLIDWORKS swCommands_e; File > New (1) is deliberately excluded.
    internal static bool IsAssemblyInsertionCommand(int command) => command is 13 or 91 or 295 or 585 or 821 or 1258 or 1259 or 1325 or 1638;

    private static bool IsSelectedComponentChange(int command) => command is 14 or 15 or 20 or 21 or 575 or 584
        or 864 or 957 or 959 or 1260 or 1324 or 1503 or 1504;

    private static string AssemblyStructureOperation(int command)
    {
        if (IsAssemblyInsertionCommand(command)) return "插入或粘贴组件";
        switch (command)
        {
            case 90: case 574: case 701: case 750: case 1343: case 1922: case 1944:
            case 2195: case 2517: case 2518: case 2521: case 2819:
                return "修改装配配合";
            case 575: return "替换组件";
            case 584: return "剪切组件";
            case 1260: case 1324: return "解散子装配体";
            case 1016: case 1941: case 1942: case 1943: case 3108: case 3109: case 3126:
                return "阵列组件";
            default: return IsSelectedComponentChange(command) ? "修改组件" : null;
        }
    }

    private int OnAssemblyCommandOpening(int command, int userCommand)
    {
        var previous = nativeUiCommandId;
        if (command > 0) { nativeUiCommandId = command; unknownSavedEpoch = 0; }
        var operation = AssemblyStructureOperation(command);
        if (operation == null) return 0;
        try
        {
            var document = application?.ActiveDoc as IModelDoc2;
            if (!(document is IAssemblyDoc assembly)) return 0;
            var blockedOwners = AssemblyCommandOwners(document, assembly, command).Where(path => !CanEditAssemblyStructure(path)).ToArray();
            if (blockedOwners.Length == 0) return 0;
            QueueAssemblyPermission(document, blockedOwners, command, operation);
            if (nativeUiCommandId == command) nativeUiCommandId = previous;
            return 1; // CommandOpenPreNotify: cancel before the PropertyManager opens.
        }
        catch (Exception error)
        {
            if (nativeUiCommandId == command) nativeUiCommandId = previous;
            LogDiagnostic("修改结构前核对所属装配体权限", error);
            taskPaneControl?.SetCheckoutReminder("已停止“" + operation + "”：请核对所属装配体并获取编辑权限后重试。", true);
            return 1;
        }
    }

    private int OnAssemblyFileDropPreNotify(string fileName)
    {
        try
        {
            var document = assemblyEventDocument;
            if (!(document is IAssemblyDoc assembly)) return 0;
            var blockedOwners = AssemblyCommandOwners(document, assembly, 0).Where(path => !CanEditAssemblyStructure(path)).ToArray();
            if (blockedOwners.Length == 0) return 0;
            QueueAssemblyPermission(document, blockedOwners, 0, "拖入组件");
            return 1;
        }
        catch (Exception error)
        {
            LogDiagnostic("拖入组件前核对所属装配体权限", error);
            taskPaneControl?.SetCheckoutReminder("已停止拖入：请先获取所属装配体权限，再重新拖入组件。", true);
            return 1;
        }
    }

    private static IEnumerable<string> AssemblyCommandOwners(IModelDoc2 document, IAssemblyDoc assembly, int command)
    {
        var target = assembly.GetEditTarget() as IModelDoc2 ?? document;
        if (!(target is IAssemblyDoc)) yield break;
        var foundSelectionOwner = false;
        if (IsSelectedComponentChange(command) && document.SelectionManager is ISelectionMgr selection)
            for (var index = 1; index <= selection.GetSelectedObjectCount2(-1); index++)
                if (selection.GetSelectedObject6(index, -1) is IComponent2 component)
                {
                    foundSelectionOwner = true;
                    yield return AssemblyComponentOwnerPath(document, component);
                }
        if (foundSelectionOwner) yield break;
        var path = target.GetPathName();
        if (!ReferenceEquals(target, document) && (string.IsNullOrWhiteSpace(path) || IsSolidWorksTemporaryVirtualComponentPath(path)))
            path = assembly.GetEditTargetComponent() is IComponent2 component ? AssemblyComponentOwnerPath(document, component) : document.GetPathName();
        yield return path;
    }

    private void QueueAssemblyPermission(IModelDoc2 document, IEnumerable<string> ownerPaths, int command, string operation)
    {
        var owners = ownerPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (owners.Any(path => IsReadOnlyPreviewPath(path) || IsHistoricalDerivedPath(path)
            || EnumerateCadNodes(currentTree).Any(node => PathsEqual(node.FullPath, path) && (node.IsReadOnlyPreview || node.IsHistoricalPreview))))
        {
            taskPaneControl?.SetCheckoutReminder("已停止“" + operation + "”：历史或只读预览不能直接编辑，请从工作区打开当前工作图档后操作。", true);
            return;
        }
        taskPaneControl?.SetCheckoutReminder("本次" + operation + "需要先获取所属装配体权限；正在准备获取提示，当前装配关系保持不变。", true);
        if (taskPaneControl?.IsHandleCreated != true || disconnecting || pendingAssemblyOperation != null || assemblyPermissionPromptActive) return;
        var selection = document.SelectionManager as ISelectionMgr;
        var selected = Enumerable.Range(1, selection?.GetSelectedObjectCount2(-1) ?? 0)
            .Select(index => selection.GetSelectedObject6(index, -1)).ToArray();
        pendingAssemblyOperation = new PendingAssemblyOperation { Document = document, Path = document.GetPathName(),
            OwnerPaths = owners, Command = command,
            Operation = operation, Selection = selected, Configuration = document.ConfigurationManager?.ActiveConfiguration?.Name };
        LogOperation("装配操作已在修改前暂停 operation=" + operation + " owners=" + string.Join(";", pendingAssemblyOperation.OwnerPaths));
    }

    private bool TryStartAssemblyPermissionPrompt()
    {
        if (pendingAssemblyOperation == null || assemblyPermissionPromptActive || disconnecting
            || Volatile.Read(ref workspaceOperationInProgress) != 0 || Volatile.Read(ref batchDialogInProgress) != 0
            || Volatile.Read(ref controlledOpenInProgress) != 0 || Volatile.Read(ref openOperationInProgress) != 0) return false;
        var pending = pendingAssemblyOperation; pendingAssemblyOperation = null;
        assemblyPermissionPromptActive = true;
        _ = AcquireAssemblyForPendingOperationAsync(pending);
        return true;
    }

    private bool PendingAssemblyContextMatches(PendingAssemblyOperation pending)
    {
        var active = application?.ActiveDoc as IModelDoc2;
        if (disconnecting || active == null || !ReferenceEquals(active, pending.Document) || !PathsEqual(active.GetPathName(), pending.Path)) return false;
        if (string.IsNullOrWhiteSpace(pending.Configuration)
            || !string.Equals(active.ConfigurationManager?.ActiveConfiguration?.Name, pending.Configuration, StringComparison.OrdinalIgnoreCase)) return false;
        if (pending.Command != 16 && active is IAssemblyDoc assembly
            && pending.OwnerPaths.Any(path => !AssemblyCommandOwners(active, assembly, pending.Command).Any(owner => PathsEqual(path, owner)))) return false;
        var selection = active.SelectionManager as ISelectionMgr;
        if ((selection?.GetSelectedObjectCount2(-1) ?? 0) != pending.Selection.Length) return false;
        for (var index = 0; index < pending.Selection.Length; index++)
            if (!ReferenceEquals(selection.GetSelectedObject6(index + 1, -1), pending.Selection[index])) return false;
        return true;
    }

    private async Task AcquireAssemblyForPendingOperationAsync(PendingAssemblyOperation pending)
    {
        var operationStarted = false;
        try
        {
            if (!PendingAssemblyContextMatches(pending)) return;
            if (apiClient?.IsAuthenticated != true) { ShowError("请先登录PLM，再获取所属装配体权限。"); return; }
            var question = "本次“" + pending.Operation + "”需要先获取以下装配体的编辑权限：\r\n"
                + string.Join("\r\n", pending.OwnerPaths.Select(Path.GetFileName)) + "\r\n\r\n是否现在获取？取消将保持当前装配不变。";
            if (TopMostMessageBox.Show(taskPaneControl, question, "获取所属装配体权限", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                taskPaneControl.SetCheckoutReminder("已取消“" + pending.Operation + "”，未获取新的权限，当前装配保持不变。", false);
                return;
            }
            if (!PendingAssemblyContextMatches(pending)) return;
            if (!TryBeginWorkspaceOperation("正在获取所属装配体编辑权限")) { ShowWorkspaceOperationBusy(); return; }
            operationStarted = true; Interlocked.Increment(ref refreshSuppressionDepth);
            var nodes = pending.OwnerPaths.Select(path =>
            {
                if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) || !binding.ProjectId.HasValue)
                    throw new InvalidOperationException("所属装配体的项目关联无法核对，请从工作区获取后重试：" + Path.GetFileName(path));
                return EnumerateCadNodes(currentTree).FirstOrDefault(node => node.DocumentId == binding.DocumentId && PathsEqual(node.FullPath, path))
                    ?? new CadTreeNode { DocumentId = binding.DocumentId, FullPath = path, FileName = Path.GetFileName(path), Kind = CadDocumentKind.Assembly };
            }).ToArray();
            var projects = pending.OwnerPaths.Select(PdmDocumentIdentityStore.ReadProjectId).Distinct().ToArray();
            if (projects.Length != 1 || !projects[0].HasValue) throw new InvalidOperationException("所选装配体不属于同一项目，请分别获取权限后操作。");
            await AcquireExplicitlyFromPlmAsync(nodes.Select(node => new BatchOperationItem(node, 0)).ToArray(), projects[0].Value);
            if (!PendingAssemblyContextMatches(pending) || pending.OwnerPaths.Any(path => !CanEditAssemblyStructure(path)))
            {
                taskPaneControl.SetCheckoutReminder("获取操作已结束，当前窗口、选择或权限仍需核对；原装配和权限记录已保留，请确认当前PLM工作副本后重新操作。", true);
                return;
            }
            // Replay only the existing commands that reopen the insertion UI. Other
            // commands may apply a structure change immediately to the selection.
            if (!IsAssemblyInsertionCommand(pending.Command) || pending.Command is 585 or 821)
                taskPaneControl.SetCheckoutReminder("所属装配体权限已获取，请重新执行“" + pending.Operation + "”；原装配保持不变。", false);
            else if (!RunTrackedAssemblyCommand(pending.Document, pending.Command))
                taskPaneControl.SetCheckoutReminder("所属装配体权限已获取，请重新执行原操作。", false);
        }
        catch (Exception error)
        {
            LogDiagnostic("装配操作前获取编辑权限，原操作未自动保存或替换图档", error);
            ShowError("未继续执行“" + pending.Operation + "”，当前装配和文件已保留。\r\n" + ArchivePreflightError(error));
        }
        finally
        {
            assemblyPermissionPromptActive = false;
            if (operationStarted) { Interlocked.Decrement(ref refreshSuppressionDepth); EndWorkspaceOperation(); SchedulePermissionStateRefresh(); }
        }
    }

    private sealed class PendingAssemblyOperation
    {
        public IModelDoc2 Document;
        public string Path, Operation, Configuration;
        public string[] OwnerPaths;
        public int Command;
        public object[] Selection;
    }
}
