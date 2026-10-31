using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void NativeFileOpenSafetyScenario(string output)
    {
        Directory.CreateDirectory(output);
        var calls = new List<string>();
        var uiActive = false;
        var addin = New("PdmAddin");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly
            .GetType("SolidWorks.Interop.sldworks.SldWorks", true);
        var app = new Proxy(appType, call =>
        {
            calls.Add(call.MethodName);
            switch (call.MethodName)
            {
                case "get_StartupProcessCompleted": return true;
                case "get_ActiveDoc": return null;
                case "GetRunningCommandInfo": return new ReturnMessage(null, new object[] { uiActive ? 13 : -3, uiActive ? "插入组件" : "", uiActive }, 3, call.LogicalCallContext, call);
                default:
                    if (call.MethodName.StartsWith("add_", StringComparison.Ordinal)
                        || call.MethodName.StartsWith("remove_", StringComparison.Ordinal)) return null;
                    throw new InvalidOperationException("加载中不允许调用：" + call.MethodName);
            }
        }).GetTransparentProxy();
        SetField(addin, "application", app);
        void Check(bool value, string message) { TreeCheck(value, message); Console.WriteLine("PASS: " + message); }
        TreeCall(addin, "WireSolidWorksEvents");
        Check(calls.Contains("add_FileOpenPreNotify") && calls.Contains("add_FileOpenPostNotify"), "同时监听文件开始打开与打开完成事件");
        var commandCloseSubscribed = calls.Contains("add_CommandCloseNotify");
        calls.Clear();
        Check(!(bool)TreeCall(addin, "IsSolidWorksCommandActive"), "对照：SolidWorks没有报告活动命令时，原命令检查返回空闲");
        var root = Path.Combine(output, "正在加载的总装.SLDASM");
        SetField(addin, "pendingTreeRefreshForceScan", 1);
        Check((int)TreeCall(addin, "OnFileOpening", root) == 0, "文件打开前回调允许SolidWorks继续加载");
        calls.Clear();
        Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), "即使命令接口报告空闲，原生加载期间仍判定为忙碌");
        TreeCall(addin, "OnActiveDocumentChanged");
        TreeCall(addin, "OnSolidWorksIdle");
        TreeCall(addin, "TryRefreshPendingTree", "NestedOpenIdle");
        TreeCall(addin, "RefreshTree", false, false);
        Check(!calls.Contains("get_ActiveDoc") && !calls.Contains("GetRunningCommandInfo")
            && !calls.Contains("GetDocumentDependencies2"), "嵌套Idle、文档切换和直接刷新均不进入原生结构或工程图依赖读取");
        Check((int)Field(addin, "pendingTreeRefresh") == 1 && (int)Field(addin, "pendingTreeRefreshForceScan") == 1,
            "加载期间保留待刷新的结构事件，不丢弃强制刷新");
        Check((int)TreeCall(addin, "OnFileOpened", root) == 0, "文件打开完成回调正常返回");
        calls.Clear();
        Check(!(bool)TreeCall(addin, "IsSolidWorksCommandActive") && calls.Contains("GetRunningCommandInfo"), "收到打开完成事件后恢复正常命令检查");
        Check((int)Field(addin, "pendingTreeRefresh") == 1, "打开完成后安排实际设计树刷新");
        foreach (var command in new[] { 2, 13, 1258, 585 })
        {
            TreeCall(addin, "OnAssemblyCommandOpening", command, 0);
            TreeCall(addin, "OnFileOpening", root);
            Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), command + "：命令引发文件加载后继续阻止原生扫描");
            var close = Type("PdmAddin").GetMethod("OnNativeCommandClosed", Flags);
            if (close != null) close.Invoke(addin, new object[] { command, -1 });
            Check(!(bool)TreeCall(addin, "IsSolidWorksCommandActive"), command + "：取消对应命令后不遗留永久加载标记");
        }
        TreeCall(addin, "OnAssemblyCommandOpening", 2, 0);
        TreeCall(addin, "OnFileOpening", root);
        TreeCall(addin, "OnNativeCommandClosed", 13, -1);
        Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), "其他命令取消不能解除正在打开文件的保护");
        TreeCall(addin, "OnNativeCommandClosed", 2, 0);
        Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), "未知结束原因不能作为加载完成依据");
        TreeCall(addin, "OnNativeCommandClosed", 2, -2);
        Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), "文件打开命令确认后仍须等待原生加载完成通知");
        TreeCall(addin, "OnFileOpened", root);
        Check(!(bool)TreeCall(addin, "IsSolidWorksCommandActive"), "真正文件打开完成后恢复空闲");
        foreach (var operation in new[] { "openOperationInProgress", "controlledOpenInProgress" })
        {
            TreeCall(addin, "OnAssemblyCommandOpening", 13, 0);
            TreeCall(addin, "OnFileOpening", root);
            SetField(addin, operation, 1);
            TreeCall(addin, "OnNativeCommandClosed", 13, -1);
            Check((bool)TreeCall(addin, "IsSolidWorksCommandActive"), operation + "：受控API加载期间不能因UI结束而提前解除保护");
            SetField(addin, operation, 0);
            TreeCall(addin, "OnFileOpened", root);
        }
        TreeCall(addin, "OnAssemblyCommandOpening", 13, 0);
        TreeCall(addin, "OnFileOpening", root);
        TreeCall(addin, "OnAssemblyCommandOpening", -2, 0);
        TreeCall(addin, "OnFileOpening", root);
        TreeCall(addin, "OnNativeCommandClosed", 13, -2);
        Check(!(bool)TreeCall(addin, "IsSolidWorksCommandActive"), "插入完成及嵌套打开不丢失所属命令，恢复正常存档准备");
        uiActive = true;
        var busy = (string)TreeCall(addin, "ArchiveRequestBusyReason");
        Check(busy != null && busy.Contains("尚未开始存档") && busy.Contains("完成或取消"), "原生命令真正活动时提供明确提醒，不创建失败存档");
        Check((int)Field(addin, "nativeFileOpenInProgress") == 0, "存档前提醒不伪造文件打开或修改加载标记");
        uiActive = false;
        Check(TreeCall(addin, "ArchiveRequestBusyReason") == null, "命令结束后允许正常创建存档清单");
        Check((string)TreeCall(addin, "ArchivePreflightError", new InvalidOperationException("请先完成SolidWorks当前命令，再核对存档引用位置。"))
            == "请先完成SolidWorks当前命令，再核对存档引用位置。", "准备阶段保留具体中文原因，不替换成泛化失败");
        Check(commandCloseSubscribed, "订阅命令确认和取消通知，补齐加载状态收尾");
        foreach (var mode in new[] { "started", "declined", "failed" })
        {
            SetField(addin, "nativeUiCommandId", 91);
            var extensionType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true);
            var extension = new Proxy(extensionType, call =>
            {
                if (call.MethodName != "RunCommand") throw new Exception("只能重放原命令");
                Check((int)Field(addin, "nativeUiCommandId") == 13, mode + "：API重放前已建立真实插入来源，不依赖CommandOpen通知");
                if (mode == "failed") throw new InvalidOperationException("隔离命令失败");
                if (mode == "declined") return false;
                TreeCall(addin, "OnFileOpening", root);
                Check((int)Field(addin, "nativeFileOpenCommandId") == 13, "API触发文件打开时携带插入命令13");
                TreeCall(addin, "OnNativeCommandClosed", 13, -2);
                return true;
            }).GetTransparentProxy();
            var model = new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                call.MethodName == "get_Extension" ? extension : throw new Exception("重放不另行保存或开关文件")).GetTransparentProxy();
            var failed = false;
            try { TreeCall(addin, "RunTrackedAssemblyCommand", model, 13); }
            catch (TargetInvocationException) { failed = true; }
            Check(failed == (mode == "failed"), mode + "：失败仍由原操作错误处理，未吞掉异常");
            Check(mode == "started" ? !(bool)TreeCall(addin, "IsSolidWorksCommandActive") : (int)Field(addin, "nativeUiCommandId") == 91,
                mode + "：完成后解除匹配的插入保护，失败或未启动时恢复原来源");
        }
        TreeCall(addin, "UnwireSolidWorksEvents");
        Check(calls.Contains("remove_FileOpenPreNotify") && calls.Contains("remove_FileOpenPostNotify"), "插件退出时完整解绑新增事件");
        Check(calls.Contains("remove_CommandCloseNotify"), "插件退出时解绑命令结束通知");
    }
}
