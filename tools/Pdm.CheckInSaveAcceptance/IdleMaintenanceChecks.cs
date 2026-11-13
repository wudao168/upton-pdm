using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task IdleMaintenanceScenario(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        var plugin = New("PdmAddin");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var scans = 0;
        var pathReads = 0;
        var models = Enumerable.Range(0, 514).Select(i => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
        {
            if (call.MethodName == "GetPathName") { pathReads++; return Path.Combine(output, "View", i + ".SLDPRT"); }
            throw new Exception("周期维护不得修改模型：" + call.MethodName);
        }).GetTransparentProxy()).ToArray();
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            if (call.MethodName == "GetDocuments") { scans++; return models; }
            throw new Exception("不应执行原生命令：" + call.MethodName);
        }).GetTransparentProxy());
        using (var client = (IDisposable)New("PdmApiClient", "http://idle-maintenance.invalid/"))
        {
            SetField(plugin, "controlledWorkspace", New("ControlledWorkspaceManager", client, output));
            SetField(plugin, "backgroundMaintenanceIdleCheck", new Func<bool>(() => false));
            for (var i = 0; i < 200; i++) TreeCall(plugin, "RunPeriodicNativeMaintenance");
            Check(scans == 0 && pathReads == 0, "连续旋转缩放期间200次空闲回调，零已加载图档遍历");
            TreeCall(plugin, "PublishCacheProtection", true, null);
            Check(scans == 0, "交互期间主动操作仍能发布忙碌保护，不遍历模型");
            SetField(plugin, "nextCacheProtectionUtc", DateTime.MinValue);
            SetField(plugin, "backgroundMaintenanceIdleCheck", new Func<bool>(() => true));
            TreeCall(plugin, "RunPeriodicNativeMaintenance");
            for (var i = 0; i < 100 && (int)Field(plugin, "cacheProtectionWorker") != 0; i++) await Task.Delay(10);
            Check(scans == 1 && pathReads == 514 && (int)Field(plugin, "cacheProtectionWorker") == 0,
                "交互暂停后恢复514份已加载图档保护，后台发布完成");
            for (var i = 0; i < 100; i++) TreeCall(plugin, "RunPeriodicNativeMaintenance");
            Check(scans == 1, "高频空闲通知不重复执行尚未到期的缓存扫描");
            SetField(plugin, "editJournal", New("EditDraftJournal", Path.Combine(output, "journal"), "fixture"));
            var pending = (IList)TreeCall(plugin, "PrepareClosedEdits");
            Check(pending.Count == 0 && scans == 1, "无活动或遗留编辑记录时，编辑维护不遍历任何CAD模型");
            var state = New("CacheInstanceState");
            Set(state, "ProcessId", 123); Set(state, "StartedUtcTicks", 456L); Set(state, "RecordedUtc", DateTime.UtcNow.AddSeconds(-11));
            Check(!(bool)Type("CacheInstanceRegistry").GetMethod("Matches", Flags).Invoke(null, new object[] { state, 123, 456L, DateTime.UtcNow }),
                "持续交互导致保护过期时保持禁止跨进程清理，不误删打开图档");
        }
    }
}
