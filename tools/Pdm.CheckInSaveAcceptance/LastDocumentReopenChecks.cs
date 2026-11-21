using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Remoting.Messaging;
using System.Windows.Forms;

internal static partial class Program
{
    static void LastDocumentReopenScenario(string output)
    {
        foreach (var mode in new[]
        {
            "closed", "manifest-reference", "wrong-manifest-host", "nested-idle-only", "wrong-host", "root-loading", "unrelated-reference", "mixed-load",
            "api-busy", "user-command", "other-window", "background-documents", "workspace-busy",
            "new-load-after-close", "new-load-in-query", "stale-close", "assembly-rebind", "part-rebind", "drawing-rebind"
        })
        {
            LastDocumentReopenCase(Path.GetFullPath(output), mode);
            Console.WriteLine("PASS: 关闭最后图档后再次打开 " + mode);
        }
    }

    static void LastDocumentReopenCase(string directory, string mode)
    {
        var addin = New("PdmAddin");
        var documentType = mode == "part-rebind" ? 1 : mode == "drawing-rebind" ? 3 : 2;
        var rootPath = Path.Combine(directory, documentType == 1 ? "零件.SLDPRT" : documentType == 3 ? "工程图.SLDDRW" : "总装.SLDASM");
        var referencePath = Path.Combine(directory, "组件.SLDASM");
        var unrelatedPath = Path.Combine(directory, "另一装配.SLDASM");
        var project = Guid.NewGuid();
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        var unexpected = new List<string>();
        var destroyHandlers = new Dictionary<string, Delegate>();
        var unbound = new List<string>();
        object Reject(IMethodCallMessage call)
        {
            unexpected.Add(call.MethodName);
            throw new InvalidOperationException("验收期间禁止打开、保存、关闭或扫描原生图档：" + call.MethodName);
        }
        object Model(string key, string path) => new AssemblyProxy(modelType, call =>
        {
            if (call.MethodName == "add_DestroyNotify2") destroyHandlers[key] = (Delegate)call.Args[0];
            if (call.MethodName == "remove_DestroyNotify2") unbound.Add(key);
            if (call.MethodName.StartsWith("add_") || call.MethodName.StartsWith("remove_")) return null;
            switch (call.MethodName)
            {
                case "GetType": return call.MethodBase.DeclaringType == typeof(object) ? (object)modelType : documentType;
                case "GetPathName": return path;
                case "GetTitle": return Path.GetFileName(path);
                case "GetBlockingState": return 0;
                default: return Reject(call);
            }
        }).GetTransparentProxy();
        var owner = Model("first", rootPath);
        var other = Model("other", unrelatedPath);
        var reopenedOwner = Model("second", rootPath);
        object active = owner;
        var count = 1;
        var reentrantLoad = false;
        SetField(addin, "backgroundMaintenanceIdleCheck", (Func<bool>)(() => false));
        SetField(addin, "application", new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "get_StartupProcessCompleted": return true;
                case "get_CommandInProgress": return mode == "api-busy";
                case "GetRunningCommandInfo":
                    var busy = mode == "user-command";
                    return new ReturnMessage(null, new object[] { busy ? 13 : -3, busy ? "插入" : "", busy }, 3, call.LogicalCallContext, call);
                case "get_ActiveDoc": return active;
                case "GetDocumentCount":
                    if (reentrantLoad)
                    {
                        reentrantLoad = false;
                        TreeCall(addin, "OnFileOpening", unrelatedPath);
                    }
                    return count;
                default: return Reject(call);
            }
        }).GetTransparentProxy());
        using (var pane = (Control)New("PdmTaskPaneControl", "重复打开验收"))
        using (var api = (IDisposable)New("PdmApiClient", "http://reopen.invalid/"))
        {
            ((HttpClient)Field(api, "httpClient")).DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "isolated-reopen");
            SetField(addin, "apiClient", api);
            SetField(addin, "taskPaneControl", pane);
            var root = New("CadTreeNode");
            Set(root, "FileName", Path.GetFileName(rootPath));
            Set(root, "FullPath", rootPath);
            Set(root, "Kind", Enum.Parse(Type("CadDocumentKind"), documentType == 1 ? "Part" : documentType == 3 ? "Drawing" : "Assembly"));
            var reference = New("CadTreeNode");
            Set(reference, "FileName", Path.GetFileName(referencePath));
            Set(reference, "FullPath", referencePath);
            if (mode == "manifest-reference" || mode == "wrong-manifest-host")
            {
                var manifest = New("ControlledOpenManifestDto");
                var hostName = Path.GetFileName(mode == "manifest-reference" ? rootPath : unrelatedPath);
                Set(manifest, "RootRelativePath", hostName);
                var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                foreach (var name in new[] { hostName, Path.GetFileName(referencePath) })
                {
                    var item = New("ControlledOpenFileDto");
                    Set(item, "FileName", name);
                    Set(item, "RelativePath", name);
                    Set(item, "IsRoot", name == hostName);
                    files.Add(item);
                }
                Set(manifest, "Files", files);
                ((IDictionary)Field(addin, "controlledOpenManifests")).Add(directory, manifest);
            }
            else ((IList)Get(root, "Children")).Add(reference);
            SetField(addin, "currentTree", root);
            SetField(addin, "currentDocumentIdentity", rootPath);
            SetField(addin, "currentProjectId", project);
            TreeCall(pane, "SetTree", root);
            TreeCall(addin, "BindAssemblyEvents");
            if (mode.EndsWith("-rebind", StringComparison.Ordinal) || mode == "stale-close")
            {
                active = reopenedOwner;
                TreeCall(addin, "BindAssemblyEvents");
                TreeCheck(unbound.Contains("first") && destroyHandlers.ContainsKey("second"), "重新打开同路径的模型必须重新订阅关闭事件");
                if (mode != "stale-close") return;
            }
            SetField(addin, "nativeUiCommandId", 2);
            if (mode == "mixed-load") TreeCall(addin, "OnFileOpening", unrelatedPath);
            TreeCall(addin, "OnFileOpening", mode == "root-loading" ? rootPath : mode == "unrelated-reference" ? unrelatedPath : referencePath);
            var opens = (Queue<Action>)Field(addin, "queuedControlledOpens");
            // A marker isolates the queue dispatch from the network transfer verified in the native smoke test.
            var dispatched = 0;
            opens.Enqueue(() =>
            {
                TreeCheck(Field(addin, "currentTree") == null && Field(pane, "rootNode") == null
                    && Field(addin, "currentProjectId") == null, "出队时仍显示旧图档或项目");
                dispatched++;
            });
            if (mode == "wrong-host")
            {
                var observer = Type("PdmAddin").GetMethod("RecordBoundDocumentClosing", Flags);
                observer?.Invoke(addin, new object[] { 0, referencePath });
            }
            else if (mode != "nested-idle-only" && destroyHandlers.TryGetValue("first", out var notify))
                notify.DynamicInvoke(0);
            active = mode == "other-window" ? other : null;
            count = mode == "background-documents" || active != null ? 1 : 0;
            if (mode == "workspace-busy") SetField(addin, "workspaceOperationInProgress", 1);
            if (mode == "new-load-after-close") TreeCall(addin, "OnFileOpening", unrelatedPath);
            TreeCall(addin, "OnSolidWorksIdle");
            TreeCheck((int)Field(addin, "nativeFileOpenInProgress") != 0 && dispatched == 0,
                mode + "：单次空闲不得启动排队的打开");
            reentrantLoad = mode == "new-load-in-query";
            TreeCall(addin, "OnSolidWorksIdle");
            TreeCall(addin, "OnSolidWorksIdle");
            var recovered = mode == "closed" || mode == "manifest-reference";
            TreeCheck(((int)Field(addin, "nativeFileOpenInProgress") == 0) == recovered,
                mode + "：原生加载状态错误");
            TreeCheck((Field(addin, "currentTree") == null) == recovered && (Field(pane, "rootNode") == null) == recovered,
                mode + "：设计树或任务窗格残留错误的宿主");
            TreeCheck((Field(addin, "currentProjectId") == null) == recovered, mode + "：项目上下文错误");
            TreeCheck(dispatched == (recovered ? 1 : 0) && opens.Count == (recovered ? 0 : 1), mode + "：排队请求重复、丢失或未恢复");
            TreeCheck(unexpected.Count == 0, mode + "：进行了预期外原生操作 " + string.Join(",", unexpected));
        }
    }
}
