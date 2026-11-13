using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static void ReferenceActivationScenario(string output)
    {
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        foreach (var mode in new[] { "complete", "no-post", "no-activation", "no-idle", "same-document", "command", "blocked", "new-load", "insertion", "uncached" })
        {
            var plugin = New("PdmAddin"); var folder = Path.GetFullPath(output);
            var owner = Path.Combine(folder, "总装.SLDASM"); var child = Path.Combine(folder, "零件.SLDPRT");
            Directory.CreateDirectory(folder); File.WriteAllText(owner, "mock owner"); File.WriteAllText(child, "mock child");
            var project = Guid.NewGuid(); var tree = New("CadTreeNode");
            Set(tree, "FullPath", owner); Set(tree, "FileName", "总装.SLDASM");
            if (mode != "uncached") TreeCall(plugin, "CacheActiveTree", owner, tree, (Guid?)project);
            SetField(plugin, "currentDocumentIdentity", mode == "same-document" ? owner : child);
            var manifest = New("ControlledOpenManifestDto"); Set(manifest, "ProjectId", project);
            var files = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
            foreach (var name in new[] { "总装.SLDASM", "零件.SLDPRT" })
            {
                var file = New("ControlledOpenFileDto"); Set(file, "FileName", name); Set(file, "RelativePath", name);
                Set(file, "IsRoot", name.EndsWith("SLDASM")); files.Add(file);
            }
            Set(manifest, "Files", files); ((IDictionary)Field(plugin, "controlledOpenManifests")).Add(folder, manifest);
            object Model(string path) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return path;
                    case "GetBlockingState": return mode == "blocked" ? 1 : 0;
                    case "GetUpdateStamp": return 7;
                    default: throw new Exception("树恢复不得修改或遍历模型：" + call.MethodName);
                }
            }).GetTransparentProxy();
            var rootModel = Model(owner); var childModel = Model(child);
            SetField(plugin, "application", new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SldWorks", true), call =>
            {
                switch (call.MethodName)
                {
                    case "get_StartupProcessCompleted": return true;
                    case "get_CommandInProgress": return false;
                    case "GetRunningCommandInfo": return new ReturnMessage(null, new object[] { 0, "", mode == "command" }, 3, call.LogicalCallContext, call);
                    case "get_ActiveDoc": return rootModel;
                    case "GetDocumentCount": return 2;
                    case "GetOpenDocumentByName":
                        if (mode == "new-load") SetField(plugin, "nativeLoadEpoch", 2);
                        return childModel;
                    default: throw new Exception("树恢复不得打开、保存或激活模型：" + call.MethodName);
                }
            }).GetTransparentProxy());
            SetField(plugin, "nativeFileOpenInProgress", 1); SetField(plugin, "nativeLoadEpoch", 1);
            SetField(plugin, "pendingOpenPath", child); SetField(plugin, "pendingOpenIdleEpoch", mode == "no-idle" ? -1 : 1);
            if (mode != "no-post") TreeCall(plugin, "OnNativeDocumentLoaded", "零件", child);
            if (mode != "no-activation") TreeCall(plugin, "OnActiveModelDocumentChanged");
            if (mode == "insertion") SetField(plugin, "unknownAddedEpoch", 1);
            var recovered = (bool)TreeCall(plugin, "TryCompleteFinishedNativeOpen");
            if (recovered != (mode == "complete") || ((int)Field(plugin, "nativeFileOpenInProgress") == 0) != recovered)
                throw new Exception("引用加载后的切换恢复判定错误：" + mode);
            Console.WriteLine("PASS: 总装切换仅在加载后通知、激活、空闲及稳定状态齐全时恢复：" + mode);
        }
    }

    static async Task MaintenanceJournalScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        using (var client = (IDisposable)New("PdmApiClient", "http://maintenance.invalid/"))
        using (var lifetime = new CancellationTokenSource())
        {
            var http = (HttpClient)Field(client, "httpClient");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
            var scope = (string)Get(client, "EditJournalScope") + "|tester";
            var journal = New("EditDraftJournal", Path.Combine(Path.GetFullPath(output), "journal"), scope);
            var closed = New("LocalEditDraft"); Set(closed, "DocumentId", Guid.NewGuid()); Set(closed, "SessionId", Guid.NewGuid()); Set(closed, "Closed", true);
            TreeCall(journal, "Save", closed);
            var directory = (string)Field(journal, "directory");
            var template = File.ReadAllText(Directory.GetFiles(directory, "*.xml")[0]);
            for (var i = 0; i < 3600; i++) File.WriteAllText(Path.Combine(directory, "history-" + i + ".xml"), template);
            foreach (var mode in new[] { "read", "write-race", "account-change", "busy" })
            {
                var plugin = New("PdmAddin"); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
                SetField(plugin, "authenticatedUsername", "tester"); SetField(plugin, "editJournal", journal); SetField(plugin, "editJournalScope", scope);
                SetField(plugin, "backgroundMaintenanceIdleCheck", new Func<bool>(() => true));
                var ticks = 0;
                using (var timer = new System.Windows.Forms.Timer { Interval = 1 })
                {
                    timer.Tick += (_, __) =>
                    {
                        ticks++;
                        if (ticks != 1) return;
                        if (mode == "write-race") TreeCall(journal, "Save", closed);
                        if (mode == "account-change") SetField(plugin, "authenticatedUsername", "another");
                        if (mode == "busy") SetField(plugin, "workspaceOperationInProgress", 1);
                    };
                    timer.Start();
                    await (Task)TreeCall(plugin, "MaintainEditsAsync");
                    timer.Stop();
                }
                if (ticks == 0 || Directory.GetFiles(directory, "*.xml").Length != 3601
                    || ((IDictionary)Field(plugin, "localEdits")).Count != 0)
                    throw new Exception("后台维护阻塞界面、删除历史或错误恢复过期快照：" + mode);
                Console.WriteLine("PASS: 3601条历史在后台读取，UI持续处理且历史完整，校验并发变化：" + mode);
            }
        }
    }
}
