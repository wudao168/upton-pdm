using System;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    private sealed class ReuseReferenceLoadFixture
    {
        internal static readonly string[] Cases = {
            "reuse-load-seven", "reuse-load-resident", "reuse-load-unconfirmed", "reuse-load-foreign",
            "reuse-load-new-command", "reuse-load-new-load", "reuse-load-busy", "reuse-load-wrong-active",
            "reuse-load-open-latest", "reuse-load-version-conflict"
        };

        private readonly object plugin;
        private readonly object owner;
        private readonly object resident;
        private readonly string[] paths;
        private readonly string foreign;
        private readonly string mode;
        private bool emitted, idle, search;

        internal ReuseReferenceLoadFixture(object plugin, string mode, object owner, Type modelType, string root, string[] paths)
        {
            this.plugin = plugin;
            this.mode = mode;
            this.owner = owner;
            this.paths = paths;
            foreign = Path.Combine(Path.GetDirectoryName(root), "不在本次清单.SLDASM");
            resident = new Proxy(modelType, call =>
            {
                if (call.MethodName == "GetPathName") return paths.Last();
                if (call.MethodName == "GetBlockingState") return 0;
                throw new Exception("只允许校验引用身份和阻塞状态 " + call.MethodName);
            }).GetTransparentProxy();
            SetField(plugin, "assemblyEventDocument", owner);
            SetField(plugin, "assemblyEventDocumentPath", root);
            SetField(plugin, "nativeUiCommandId", 2);
        }

        private void Open(string path) => TreeCheck((int)TreeCall(plugin, "OnFileOpening", path) == 0, "未能模拟原生引用加载");
        private bool IsLoading => (int)Field(plugin, "nativeFileOpenInProgress") != 0;

        internal bool NativeCall(IMethodCallMessage call, out object result)
        {
            result = null;
            switch (call.MethodName)
            {
                case "GetDocuments":
                    if (!emitted)
                    {
                        emitted = true;
                        foreach (var path in paths)
                        {
                            if (path == paths[3] && mode == "reuse-load-foreign") Open(foreign);
                            if (path == paths[3] && mode == "reuse-load-new-command")
                                TreeCall(plugin, "OnAssemblyCommandOpening", 1, 0);
                            Open(path);
                            if (path != paths.Last() || mode != "reuse-load-unconfirmed" && mode != "reuse-load-resident")
                                TreeCall(plugin, "OnNativeDocumentLoaded", Path.GetFileName(path), path);
                        }
                    }
                    return false;
                case "get_StartupProcessCompleted": result = true; return true;
                case "GetCurrentWorkingDirectory":
                case "GetSearchFolders": result = ""; return true;
                case "SetCurrentWorkingDirectory":
                case "SetSearchFolders": result = true; return true;
                case "GetUserPreferenceToggle": result = search; return true;
                case "SetUserPreferenceToggle": search = (bool)call.Args[1]; result = true; return true;
                case "get_CommandInProgress": result = idle && mode == "reuse-load-busy"; return true;
                case "get_ActiveDoc": result = idle && mode == "reuse-load-wrong-active" ? resident : owner; return true;
                case "GetDocumentCount": result = 8; return true;
                case "GetOpenDocumentByName":
                    result = mode == "reuse-load-resident" && (string)call.Args[0] == paths.Last() ? resident : null;
                    return true;
                case "GetRunningCommandInfo":
                    // Once reconciliation has succeeded, stop unrelated idle work.
                    var busy = idle && !IsLoading;
                    result = new ReturnMessage(null, new object[] { busy ? 13 : -3, "", busy }, 3, call.LogicalCallContext, call);
                    return true;
                default: return false;
            }
        }

        internal void Verify()
        {
            TreeCheck(emitted && IsLoading, mode + " 原生引用加载保护提前消失");
            idle = true;
            TreeCall(plugin, "OnSolidWorksIdle");
            TreeCheck(IsLoading, mode + " 一次空闲不足以证明加载完成");
            if (mode == "reuse-load-new-load") Open(paths.First());
            TreeCall(plugin, "OnSolidWorksIdle");
            var expectedCompletion = mode == "reuse-load-seven" || mode == "reuse-load-resident" || mode == "reuse-load-open-latest";
            TreeCheck(!IsLoading == expectedCompletion, mode + " 实际获取后的加载结束判定不符");
        }
    }
}
