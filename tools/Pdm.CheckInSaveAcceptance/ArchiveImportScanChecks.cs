using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ArchiveImportScanScenario(Form host, string output)
    {
        NativeImportFeatureOwnership();
        Directory.CreateDirectory(output);
        var ownerPath = Path.Combine(Path.GetFullPath(output), "总装.SLDASM");
        File.WriteAllText(ownerPath, "isolated native storage fixture");
        var cachePath = Path.Combine(Path.GetTempPath(), "swx-scan-fixture", "IC~~", "导入.SLDASM");
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "导入-1" };
        var lookups = 0;
        Action onLookup = null;
        object Model(string path) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true),
            call => call.MethodName == "GetPathName" ? path : throw new Exception("不得写入模型：" + call.MethodName)).GetTransparentProxy();
        object Feature(string name) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Feature", true),
            call => call.MethodName == "get_Name" ? name : call.MethodName == "get_Is3DInterconnectFeature" ? true
            : throw new Exception("不得修改特征：" + call.MethodName)).GetTransparentProxy();
        object Root(string path) => new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
        {
            if (call.MethodName == "GetPathName") return path;
            if (call.MethodName == "GetType") return 2;
            if (call.MethodName == "FeatureByName")
            {
                lookups++; onLookup?.Invoke();
                var name = (string)call.Args[0]; return features.Contains(name) ? Feature(name) : null;
            }
            throw new Exception("不得解析、保存或切换图档：" + call.MethodName);
        }).GetTransparentProxy();
        object Component(string name, object parent = null) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
        {
            switch (call.MethodName)
            {
                case "GetModelDoc2": return Model(cachePath);
                case "GetPathName": return cachePath;
                case "GetImportedPath": return @"Z:\history\source.STEP";
                case "GetParent": return parent;
                case "get_Name2": return name;
                case "GetType": return interop.GetType("SolidWorks.Interop.sldworks.Component2", true);
                default: throw new Exception("不得修改组件：" + call.MethodName);
            }
        }).GetTransparentProxy();
        var root = Root(ownerPath);
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var app = new Proxy(appType, call => call.MethodName == "GetType" ? appType
            : throw new Exception("不得打开文件或读取打包清单：" + call.MethodName)).GetTransparentProxy();
        object Storage() { var storage = New("ImportedComponentStorage", app); Set(storage, "AllowInventory", false); return storage; }
        string Owner(object storage, object component, object currentRoot) => (string)TreeCall(storage, "FindOwner", component, currentRoot);
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        var top = Component("导入-1");
        var members = Enumerable.Range(1, 250).Select(index => Component("导入-1/内部件-" + index, top)).ToArray();
        var storageReader = Storage();
        Check(members.All(member => Owner(storageReader, member, root) == ownerPath), "导入内部层级全部保留随原生总装保存的归属");
        Check(lookups == 1, "250个同一导入层级成员只查询一次原生导入特征，实际次数=" + lookups);
        var before = lookups;
        var unknown = Component("未知导入-1");
        Check(Enumerable.Range(0, 20).All(index => Owner(storageReader, Component("未知导入-1/件-" + index, unknown), root) == null)
            && lookups == before + 1, "非完整读取中未确认的同一导入特征只查一次，仍保持归属待核对");
        features.Add("未知导入-1");
        Check(Owner(Storage(), unknown, root) == ownerPath, "新一轮扫描重新读取特征证据，不沿用前次待核对结果");
        var otherOwner = Path.Combine(Path.GetFullPath(output), "另一总装.SLDASM"); File.WriteAllText(otherOwner, "other native fixture");
        before = lookups;
        Check(Owner(storageReader, top, Root(otherOwner)) == otherOwner && lookups == before + 1,
            "相同导入实例名位于不同所属文件时分别验证，不交叉沿用归属");

        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        try
        {
            object Scanner(IEnumerable<object> components)
            {
                var scanner = New("SolidWorksReferenceTreeScanner", app);
                SetField(scanner, "scannedModel", root); SetField(scanner, "scannedDocumentPath", ownerPath);
                SetField(scanner, "importedStorage", Storage());
                ((Stopwatch)Field(scanner, "progressClock")).Start();
                var pending = (IList)Field(scanner, "importedComponents");
                var entryType = pending.GetType().GetGenericArguments()[0];
                foreach (var component in components)
                {
                    var node = New("CadTreeNode"); Set(node, "FullPath", cachePath); Set(node, "FileName", "导入.SLDASM");
                    Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
                    pending.Add(Activator.CreateInstance(entryType, node, component));
                }
                return scanner;
            }
            var scanner = Scanner(members);
            before = lookups;
            TreeCall(scanner, "CompleteImportedStorage");
            Check(lookups == before + 1 && ((IList)Field(scanner, "importedComponents")).Count == 0,
                "实际扫描器批量核对只读取一次特征证据并释放本轮待核对列表");
            Check(ArchiveHistory(session).Contains("正在核对导入组件存储归属")
                && ArchiveUi(session, () => ((Control)Field(ArchiveWindow(session), "progress")).Text).Replace(" ", "").Contains("250/250"),
                "导入组件归属核对进入原存档页面并显示实际完成数和总数");
            scanner = Scanner(members);
            before = lookups;
            onLookup = () => TreeCall(session, "RequestCancellation");
            var canceled = false;
            try { TreeCall(scanner, "CompleteImportedStorage"); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is OperationCanceledException) { canceled = true; }
            Check(canceled && lookups == before + 1 && ((IList)Field(scanner, "importedComponents")).Count == 0,
                "扫描中取消在下个组件前安全退出，不继续核对全量层级");
            Check(File.ReadAllText(ownerPath) == "isolated native storage fixture", "归属优化和取消不改写原生文件、不解析或切换文档");
        }
        finally
        {
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }
}
