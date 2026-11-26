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
    private IModelDoc2 pendingManagedSave;
    private string pendingManagedSavePath;
    private bool managedSaveInProgress;

    private bool TryQueueManagedSave(int command)
    {
        // SOLIDWORKS swCommands_Save=2. Save All, Save As and unregistered
        // documents retain their explicit native semantics.
        if (command != 2 || managedSaveInProgress || disconnecting
            || Volatile.Read(ref checkInOperationInProgress) != 0 || Volatile.Read(ref batchDialogInProgress) != 0
            || Volatile.Read(ref workspaceOperationInProgress) != 0) return false;
        if (currentTree == null || currentTree.DocumentId == null) return false;
        var document = application?.ActiveDoc as IModelDoc2;
        if (document == null) return false;
        var path = document.GetPathName();
        var node = FindCadNodeByPath(currentTree, path);
        if (node?.DocumentId == null || !PdmDocumentIdentityStore.IsControlledWorkspacePath(path)
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) || binding.DocumentId != node.DocumentId) return false;
        if (!IsCheckedOutByCurrentUser(node) && !(document is IAssemblyDoc))
        {
            taskPaneControl?.SetCheckoutReminder("请先获取当前图档的编辑权限，再保存修改。未修改的引用图档无需一并获取。", true);
            return true;
        }
        pendingManagedSave = document;
        pendingManagedSavePath = path;
        return true;
    }

    private bool TrySavePendingManagedDocument()
    {
        if (pendingManagedSave == null || managedSaveInProgress || disconnecting
            || IsSolidWorksCommandActive() || Volatile.Read(ref workspaceOperationInProgress) != 0) return false;
        var document = pendingManagedSave; var path = pendingManagedSavePath;
        pendingManagedSave = null; pendingManagedSavePath = null;
        try
        {
            var node = FindCadNodeByPath(currentTree, path);
            if (!ReferenceEquals(application.ActiveDoc, document) || !PathsEqual(document.GetPathName(), path)
                || node?.DocumentId == null)
                throw new InvalidOperationException("图档已切换，本次尚未保存。请切回原图档后重试。");
            if (!TryBeginWorkspaceOperation("正在保存已获取权限的图档"))
            {
                pendingManagedSave = document; pendingManagedSavePath = path;
                return false;
            }
            managedSaveInProgress = true;
            var load = node.Kind == CadDocumentKind.Assembly && Volatile.Read(ref nativeFileOpenInProgress) == 0
                ? new AssemblyReferenceLoad(document, path, forReuse: false) : null;
            assemblyReferenceLoad = load;
            var saved = false;
            try
            {
                saved = SaveManagedDocumentScope(document, node);
                if (load?.Observed.Count > 0)
                    load.References.UnionWith(EnumerateTree(node).Where(child => child != node).Select(child => child.FullPath));
                InvalidateCurrentTreeCache(); ScheduleTreeRefresh(forceScan: false);
            }
            finally
            {
                FinishAssemblyReferenceOperation(load, saved);
                managedSaveInProgress = false;
                EndWorkspaceOperation();
            }
        }
        catch (Exception error)
        {
            LogDiagnostic("保存已获取权限的图档", error);
            ShowError(error is InvalidOperationException ? error.Message : "图档保存未完成，原窗口和修改已保留。请核对文件写入权限后重试；技术详情已写入诊断日志。");
        }
        return true;
    }

    private bool SaveManagedDocumentScope(IModelDoc2 document, CadTreeNode root)
    {
        var plan = new List<Tuple<IModelDoc2, CadTreeNode>>();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Use actual loaded components, never open/resolve models merely to save.
        // Drawing model references deliberately stay outside the drawing save scope.
        if (document is IAssemblyDoc assemblyDocument)
        {
            var loaded = ((assemblyDocument.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                .Select(component => component.GetModelDoc2() as IModelDoc2).Where(model => model != null)
                .Where(model => !PathsEqual(model.GetPathName(), root.FullPath))
                .GroupBy(model => model.GetPathName(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            assemblyReferenceLoad?.References.UnionWith(loaded.Keys);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in EnumerateTree(root).Reverse().Where(node => node.Kind != CadDocumentKind.Drawing))
            {
                if (string.IsNullOrWhiteSpace(child.FullPath) || !seen.Add(child.FullPath)
                    || !loaded.TryGetValue(child.FullPath, out var model) || !model.GetSaveFlag()) continue;
                if (child.DocumentId.HasValue && IsCheckedOutByCurrentUser(child)) plan.Add(Tuple.Create(model, child));
                else skipped.Add(child.FileName);
            }
            foreach (var item in loaded.Where(item => !seen.Contains(item.Key) && item.Value.GetSaveFlag()))
                skipped.Add(Path.GetFileName(item.Key));
        }
        if (IsCheckedOutByCurrentUser(root)) plan.Add(Tuple.Create(document, root));
        else if (document.GetSaveFlag()) skipped.Add(root.FileName);
        if (plan.Count == 0)
        {
            taskPaneControl?.SetCheckoutReminder("当前范围没有可保存的已授权图档。已有修改已保留。", true);
            return false;
        }
        // Check the complete save scope before the first write, then recheck each
        // target immediately before writing. Never infer permission from read/write.
        foreach (var item in plan) RequireManagedSaveTarget(item.Item1, item.Item2);
        foreach (var item in plan)
        {
            RequireManagedSaveTarget(item.Item1, item.Item2);
            SaveManagedDocumentOnly(item.Item1, item.Item2.FullPath);
            LogOperation("已保存获取权限的工作图档，未级联另存引用：" + item.Item2.FullPath);
        }
        var reminder = skipped.Count == 0 ? "已保存本次已获取权限的图档。" :
            "已保存获取权限的图档；以下图档尚未获取权限，未保存状态已保留："
            + string.Join("、", skipped.Take(6)) + "。如需保留这些修改，请核对并获取对应图档权限。";
        taskPaneControl?.SetCheckoutReminder(reminder, skipped.Count > 0);
        return true;
    }

    private void RequireManagedSaveTarget(IModelDoc2 document, CadTreeNode node)
    {
        if (node?.DocumentId == null || !IsCheckedOutByCurrentUser(node)
            || !PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var binding)
            || binding.DocumentId != node.DocumentId)
            throw new InvalidOperationException("图档的编辑权限或工作副本关联需要核对，未继续保存：" + node?.FileName);
        RequireWritableManagedDocument(document, node.FullPath);
    }

    private static void RequireWritableManagedDocument(IModelDoc2 document, string path)
    {
        var expected = string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2
            : string.Equals(Path.GetExtension(path), ".SLDDRW", StringComparison.OrdinalIgnoreCase) ? 3 : 0;
        if (document == null || expected == 0 || document.GetType() != expected
            || !PathsEqual(document.GetPathName(), path) || document.IsOpenedReadOnly() || !File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException("图档不是可写的原工作副本，尚未保存：" + Path.GetFileName(path)
                + "。请核对图档并获取权限后重试。");
    }

    private static void SaveManagedDocumentOnly(IModelDoc2 document, string path)
    {
        RequireWritableManagedDocument(document, path);
        var errors = 0; var warnings = 0;
        // Deliberately omit SaveReferenced. Do not clear model dirty flags, change
        // global read-only-save preferences, close models, or rewrite their paths.
        if (!document.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
            throw new InvalidOperationException("图档保存未完成：" + Path.GetFileName(path) + "；" + ArchiveNativeSaveFailure(errors));
    }
}
