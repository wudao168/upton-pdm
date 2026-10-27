using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
    static void NativeFileOpenSafetyScenario(string output)
    {
        Directory.CreateDirectory(output);
        var calls = new List<string>();
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
                case "GetRunningCommandInfo": call.Args[0] = 0; call.Args[1] = ""; call.Args[2] = false; return true;
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
        TreeCall(addin, "UnwireSolidWorksEvents");
        Check(calls.Contains("remove_FileOpenPreNotify") && calls.Contains("remove_FileOpenPostNotify"), "插件退出时完整解绑新增事件");
    }
}
