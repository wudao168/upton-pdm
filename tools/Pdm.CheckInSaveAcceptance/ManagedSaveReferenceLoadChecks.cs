using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void ManagedSaveReferenceLoadScenario(string output)
    {
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var bindings = identity.GetField("bindingDirectory", Flags);
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var oldBindings = bindings.GetValue(null);
        var oldRoot = resolver.GetValue(null);
        try
        {
            bindings.SetValue(null, Path.Combine(output, "bindings"));
            resolver.SetValue(null, new Func<string>(() => output));
            foreach (var mode in new[]
            {
                "seven-known", "idle", "newly-loaded-reference", "unknown-before-known", "failed-root-save",
                "ui-command", "api-command", "blocked-root", "dirty-again", "different-active-document",
                "new-load-after-save", "new-load-during-check", "command-during-check", "command-during-save",
                "read-only-parent-owned-child", "read-only-parent-unowned-child"
            })
            {
                ManagedSaveReferenceLoadCase(Path.Combine(output, "View", mode), mode);
                Console.WriteLine("PASS: 受控总装保存后的引用加载 " + mode);
            }
        }
        finally
        {
            bindings.SetValue(null, oldBindings);
            resolver.SetValue(null, oldRoot);
        }
    }

    static void ManagedSaveReferenceLoadCase(string directory, string mode)
    {
        Directory.CreateDirectory(directory);
        var plugin = New("PdmAddin");
        var rootPath = Path.Combine(directory, "总装.SLDASM");
        File.WriteAllText(rootPath, "验收工作副本");
        var tree = New("CadTreeNode");
        var id = Guid.NewGuid();
        TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWrite", rootPath, id, (Guid?)Guid.NewGuid()), "绑定临时总装失败");
        Set(tree, "DocumentId", id);
        Set(tree, "FullPath", rootPath);
        Set(tree, "FileName", Path.GetFileName(rootPath));
        Set(tree, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
        var readOnlyParent = mode.StartsWith("read-only-parent-", StringComparison.Ordinal);
        var ownedChild = mode == "read-only-parent-owned-child";
        if (!readOnlyParent)
        {
            Set(tree, "CheckedOutBy", "tester");
            Set(tree, "CheckoutSessionId", Field(plugin, "checkoutSessionId"));
        }
        SetField(plugin, "authenticatedUsername", "tester");
        SetField(plugin, "currentTree", tree);

        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        var componentType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.Component2", true);
        var components = new List<object>();
        var references = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var paths = Enumerable.Range(1, 7).Select(i => Path.Combine(directory, "引用" + i
            + (readOnlyParent && i == 1 ? ".SLDASM" : ".SLDPRT"))).ToArray();
        var unknown = Path.Combine(directory, "未关联.SLDPRT");
        var unexpected = new List<string>();
        object Unexpected(IMethodCallMessage call)
        {
            unexpected.Add(call.MethodName);
            throw new InvalidOperationException("隔离回归不允许的原生操作 " + call.MethodName);
        }
        var childSaves = 0;
        foreach (var path in paths)
        {
            var editedChild = readOnlyParent && path == paths.First();
            if (mode != "newly-loaded-reference" || path != paths.Last())
            {
                var child = New("CadTreeNode");
                Set(child, "FullPath", path);
                Set(child, "FileName", Path.GetFileName(path));
                Set(child, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
                if (editedChild)
                {
                    var childId = Guid.NewGuid();
                    File.WriteAllText(path, "子装配工作副本");
                    Set(child, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
                    Set(child, "DocumentId", childId);
                    TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWrite", path, childId, (Guid?)Guid.NewGuid()), "绑定子装配失败");
                    if (ownedChild)
                    {
                        Set(child, "CheckedOutBy", "tester");
                        Set(child, "CheckoutSessionId", Field(plugin, "checkoutSessionId"));
                    }
                }
                ((IList)Get(tree, "Children")).Add(child);
            }
            var model = new AssemblyProxy(modelType, call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return path;
                    case "GetSaveFlag": return editedChild;
                    case "GetType" when editedChild: return 2;
                    case "IsOpenedReadOnly" when editedChild: return !ownedChild;
                    case "Save3" when editedChild:
                        TreeCheck(ownedChild && (int)call.Args[0] == 1, "仅允许独立保存本会话子装配");
                        childSaves++;
                        return new ReturnMessage(true, new object[] { call.Args[0], 0, 0 }, 3, call.LogicalCallContext, call);
                    case "GetBlockingState": return 0;
                    case "GetUpdateStamp": return 23;
                    default: return Unexpected(call);
                }
            }).GetTransparentProxy();
            references.Add(path, model);
            object ComponentInstance() => new Proxy(componentType, call =>
            {
                switch (call.MethodName)
                {
                    case "GetModelDoc2": return model;
                    case "GetPathName": return path;
                    default: return Unexpected(call);
                }
            }).GetTransparentProxy();
            components.Add(ComponentInstance());
            if (path == paths.First()) components.Add(ComponentInstance());
        }

        void Opening(string path) => TreeCheck((int)TreeCall(plugin, "OnFileOpening", path) == 0, "引用打开被意外取消");
        var originalInstances = components.ToArray();
        var saves = 0;
        var scans = 0;
        var dirty = true;
        var afterSave = false;
        var enteredNewLoad = false;
        var duringSaveProtected = false;
        var owner = new DiscardAssemblyProxy(modelType, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return rootPath;
                case "GetType": return 2;
                case "IsOpenedReadOnly": return readOnlyParent;
                case "GetSaveFlag": return dirty;
                case "GetBlockingState": return afterSave && mode == "blocked-root" ? 1 : 0;
                case "GetUpdateStamp": return 12;
                case "GetComponents":
                    scans++;
                    foreach (var path in paths.Take(6))
                    {
                        Opening(path);
                        if (mode == "unknown-before-known" && path == paths[2]) Opening(unknown);
                    }
                    return components.ToArray();
                case "Save3":
                    saves++;
                    TreeCheck((int)call.Args[0] == 1, "只允许静默保存本次已签出的总装");
                    if (mode == "command-during-save") TreeCall(plugin, "OnAssemblyCommandOpening", 1, 0);
                    Opening(paths.Last());
                    TreeCall(plugin, "ReconcileNativeCommandBeforeUserOperation");
                    duringSaveProtected = (bool)TreeCall(plugin, "IsSolidWorksCommandActive");
                    var success = mode != "failed-root-save";
                    if (success)
                    {
                        File.WriteAllText(rootPath, "已保存组件实例数=" + components.Count);
                        dirty = false;
                        TreeCall(plugin, "OnAssemblyFileSaved", 0, rootPath);
                    }
                    return new ReturnMessage(success, new object[] { call.Args[0], success ? 0 : 1, 0 }, 3, call.LogicalCallContext, call);
                default: return Unexpected(call);
            }
        }).GetTransparentProxy();
        SetField(plugin, "assemblyEventDocument", owner);
        SetField(plugin, "assemblyEventDocumentPath", rootPath);
        var app = new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "get_ActiveDoc": return afterSave && mode == "different-active-document" ? references[paths.First()] : owner;
                case "get_StartupProcessCompleted": return true;
                case "get_CommandInProgress": return afterSave && mode == "api-command";
                case "GetDocumentCount": return references.Count + 1;
                case "GetOpenDocumentByName": return references.TryGetValue((string)call.Args[0], out var doc) ? doc : null;
                case "GetRunningCommandInfo":
                    if (afterSave && (mode == "new-load-during-check" || mode == "command-during-check") && !enteredNewLoad)
                    {
                        enteredNewLoad = true;
                        if (mode == "command-during-check") TreeCall(plugin, "OnAssemblyCommandOpening", 1, 0);
                        else Opening(unknown);
                    }
                    // Stop unrelated idle work after the load was reconciled.
                    var busy = afterSave && (mode == "ui-command" || mode == "idle" && (int)Field(plugin, "nativeFileOpenInProgress") == 0);
                    return new ReturnMessage(null, new object[] { busy ? 13 : -3, busy ? "插入组件" : "", busy }, 3, call.LogicalCallContext, call);
                default: return Unexpected(call);
            }
        }).GetTransparentProxy();
        SetField(plugin, "application", app);
        TreeCall(plugin, "OnAssemblyComponentAdded", 2, "引用1-2");
        TreeCheck((bool)TreeCall(plugin, "TryQueueManagedSave", 2), "已签出装配未能排入保存");
        TreeCheck((bool)TreeCall(plugin, "TrySavePendingManagedDocument"), "已签出装配未执行受控保存");
        if (readOnlyParent)
        {
            TreeCheck(scans == 1 && saves == 0 && childSaves == (ownedChild ? 1 : 0) && dirty
                && File.ReadAllText(rootPath) == "验收工作副本" && unexpected.Count == 0,
                "只读总装的普通保存只能写入当前会话已授权的子装配，不改变总装和其他图档");
            return;
        }
        TreeCheck(scans == 1 && saves == 1 && duringSaveProtected, "保存中应仅扫描一次、保存宿主一次并保持加载保护");
        TreeCheck((int)Field(plugin, "workspaceOperationInProgress") == 0 && !(bool)Field(plugin, "managedSaveInProgress"), "保存边界没有正常退出");

        var diskBefore = File.ReadAllBytes(rootPath);
        afterSave = true;
        if (mode == "new-load-after-save") Opening(unknown);
        if (mode == "dirty-again") dirty = true;
        var dirtyBefore = dirty;
        if (mode == "idle")
            TreeCall(plugin, "OnSolidWorksIdle");
        else
            TreeCall(plugin, "ReconcileNativeCommandBeforeUserOperation");
        var eligible = mode == "seven-known" || mode == "idle" || mode == "newly-loaded-reference" || mode == "dirty-again";
        var nativeOpen = (int)Field(plugin, "nativeFileOpenInProgress");
        TreeCheck(nativeOpen == (eligible ? 0 : 1), mode + " 保存后原生加载状态不符，nativeOpen=" + nativeOpen);
        if (eligible && mode != "idle")
            TreeCheck((bool)TreeCall(plugin, "CanStartArchiveRequest"), "成功保存后无法从存档入口继续");
        else if (!eligible)
            TreeCheck(TreeCall(plugin, "ArchiveRequestBusyReason") != null, "存档入口应继续保护未完成的原生操作");
        TreeCheck(dirty == dirtyBefore && diskBefore.SequenceEqual(File.ReadAllBytes(rootPath)), "校对改变了工作副本或原生修改状态");
        TreeCheck(originalInstances.Length == 8 && components.Count == originalInstances.Length && originalInstances.Select((item, index) => ReferenceEquals(item, components[index])).All(equal => equal) && Field(plugin, "currentTree") == tree,
            "原总装、原组件和同一零件的第二个实例必须全部保留");
        TreeCheck(unexpected.Count == 0 && scans == 1 && saves == 1,
            "校对不得重新读取结构、修改或保存文件 " + string.Join(",", unexpected));
    }
}
