using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Diagnostics;

internal static partial class Program
{
    static void UnloadedImportDescendantScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        object ProxyFor(string name, Func<System.Runtime.Remoting.Messaging.IMethodCallMessage, object> call)
        { var type = interop.GetType("SolidWorks.Interop.sldworks." + name, true); return new Proxy(type, m => m.MethodName == "GetType" ? type : call(m)).GetTransparentProxy(); }
        var rootPath = Path.Combine(output, "ROOT.SLDASM"); var ownerA = Path.Combine(output, "OWNER-A.SLDASM"); var ownerB = Path.Combine(output, "OWNER-B.SLDASM");
        foreach (var path in new[] { rootPath, ownerA, ownerB }) File.WriteAllText(path, "saved native container");
        var root = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "GetPathName" ? rootPath : call.MethodName == "GetType" ? (object)2 : throw new Exception("不应查询根图档其他内容：" + call.MethodName)).GetTransparentProxy();
        var source = Path.Combine(output, "source.stp"); var cacheRoot = Path.Combine(Path.GetTempPath(), "swx-fixture", "IC~~");
        var proven = true; var featureName = "IMPORT-1"; var queries = 0;
        var feature = ProxyFor("Feature", call => call.MethodName == "get_Name" ? featureName : call.MethodName == "get_Is3DInterconnectFeature" ? (object)proven : throw new Exception(call.MethodName));
        object Parent(string path) => ProxyFor("Component2", call => call.MethodName == "GetModelDoc2" ? null : call.MethodName == "GetPathName" ? path : call.MethodName == "get_IsVirtual" ? (object)false : call.MethodName == "GetParent" ? null : call.MethodName == "FeatureByName" ? ReadFeature() : throw new Exception("禁止改写或解析：" + call.MethodName));
        object ReadFeature() { queries++; return feature; }
        object Component(string name, object parent, string imported, bool loaded = false, string path = null)
        {
            path = path ?? Path.Combine(cacheRoot, name.Split('/').Last() + ".SLDPRT");
            var model = loaded ? ProxyFor("ModelDoc2", call => call.MethodName == "GetPathName" ? path : throw new Exception(call.MethodName)) : null;
            return ProxyFor("Component2", call => call.MethodName == "GetModelDoc2" ? model : call.MethodName == "GetPathName" ? path
                : call.MethodName == "GetImportedPath" ? imported : call.MethodName == "GetParent" ? parent : call.MethodName == "get_Name2" ? name
                : throw new Exception("禁止解析、保存或修改组件：" + call.MethodName));
        }
        var app = ProxyFor("ISldWorks", call => throw new Exception("不应打开、激活或读取打包清单：" + call.MethodName));
        object Storage(bool inventory = false) { var s = New("ImportedComponentStorage", app); Set(s, "AllowInventory", inventory); return s; }
        object Chain(string owner, string nestedSource = null, string leafSource = null)
        {
            var importedRoot = Component("PARENT/IMPORT-1", Parent(owner), source, true);
            var nested = Component("PARENT/IMPORT-1/NESTED-1", importedRoot, nestedSource ?? source, true);
            return Component("PARENT/IMPORT-1/NESTED-1/PART-1", nested, leafSource ?? source);
        }
        var storage = Storage();
        Check((string)TreeCall(storage, "FindOwner", Chain(ownerA), root) == ownerA, "压缩/未加载内部子件在同源父导入结构中继承保存归属");
        Check((string)TreeCall(storage, "FindOwner", Chain(ownerA), root) == ownerA && queries == 1, "同一父导入实例复用一次原生特征证明");
        Check((string)TreeCall(storage, "FindOwner", Chain(ownerB), root) == ownerB && queries == 2, "同一缓存零件在不同父装配实例中分别确认归属");
        Check(TreeCall(Storage(), "FindOwner", Chain(ownerA, Path.Combine(output, "other", "source.stp")), root) == null, "不同目录同名导入来源不能继承归属");
        Check(TreeCall(Storage(), "FindOwner", Chain(ownerA, ""), root) == null, "祖先导入来源为空时保持待核对");
        Check(TreeCall(Storage(), "FindOwner", Chain(ownerA, leafSource: ""), root) == null, "子件导入来源为空时保持待核对");
        Check(TreeCall(Storage(), "FindOwner", Chain(ownerA, "relative-source.stp"), root) == null, "相对或无法确认的来源路径不作同源证明");
        Check(TreeCall(Storage(), "FindOwner", Component("PART-1", Parent(ownerA), source), root) == null, "未加载顶层缓存没有导入祖先时仍不放行");
        proven = false;
        Check(TreeCall(Storage(true), "FindOwner", Chain(ownerA), root) == null, "缺少原生导入特征时不以打包清单替代证明");
        proven = true; featureName = "OTHER-1";
        Check(TreeCall(Storage(), "FindOwner", Chain(ownerA), root) == null, "不接受其他实例的同名或错误特征证明");
        featureName = "IMPORT-1";
        Check(TreeCall(Storage(), "FindOwner", Chain(Path.Combine(output, "MISSING.SLDASM")), root) == null, "保存父文件缺失时保持阻止");
        var scanner = New("SolidWorksReferenceTreeScanner", app);
        SetField(scanner, "scannedModel", root); SetField(scanner, "scannedDocumentPath", rootPath); SetField(scanner, "importedStorage", Storage());
        ((Stopwatch)Field(scanner, "progressClock")).Start();
        var node = New("CadTreeNode"); Set(node, "FullPath", Path.Combine(cacheRoot, "PART-1.SLDPRT")); Set(node, "FileName", "PART-1.SLDPRT");
        Set(node, "ImportedSourcePath", source); Set(node, "IsSuppressed", true); Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
        var pending = (IList)Field(scanner, "importedComponents");
        pending.Add(Activator.CreateInstance(pending.GetType().GetGenericArguments()[0], node, Chain(ownerA)));
        TreeCall(scanner, "CompleteImportedStorage");
        Check((bool)Get(node, "IsStoredImport") && (bool)Get(node, "IsSuppressed") && (string)Get(node, "ImportedStorageOwnerPath") == ownerA,
            "实际扫描器写回归属，同时保留压缩标记");
        Check(pending.Count == 0, "扫描完成后清理本轮待核对列表，不增加独立文件操作");
        Check(new[] { rootPath, ownerA, ownerB }.All(path => File.ReadAllText(path) == "saved native container"), "不解析、不保存、不改名，原生文件字节不变");
    }
}
