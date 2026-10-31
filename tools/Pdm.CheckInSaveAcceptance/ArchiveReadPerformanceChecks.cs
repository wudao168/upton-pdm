using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void ArchiveReadPerformanceScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { TreeCheck(ok, text); Console.WriteLine("PASS: " + text); }
        var plugin = New("PdmAddin"); var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
        var root = LinkedNode(output, "总装.SLDASM", "Assembly"); var part = LinkedNode(output, "零件.SLDPRT", "Part");
        var drawing = LinkedNode(output, "配套.SLDDRW", "Drawing"); var unrelatedDrawing = LinkedNode(output, "未勾选.SLDDRW", "Drawing");
        var unrelatedModel = LinkedNode(output, "其他结构.SLDASM", "Assembly");
        ((IList)Get(root, "Children")).Add(part); ((IList)Get(root, "Children")).Add(drawing); ((IList)Get(root, "Children")).Add(unrelatedDrawing);
        ((IList)Get(drawing, "ReferencedModels")).Add(root); ((IList)Get(unrelatedDrawing, "ReferencedModels")).Add(unrelatedModel);
        var closed = LinkedNode(output, "压缩子装配.SLDASM", "Assembly"); Set(closed, "Status", Enum.Parse(Type("CadReferenceStatus"), "Suppressed"));
        ((IList)Get(root, "Children")).Add(closed);
        var embedded = New("CadTreeNode"); Set(embedded, "FullPath", Path.Combine(output, "cache", "嵌入.SLDASM"));
        Set(embedded, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly")); Set(embedded, "Status", Enum.Parse(Type("CadReferenceStatus"), "Virtual"));
        ((IList)Get(root, "Children")).Add(embedded);
        var confirmed = new HashSet<string>(new[] { root, part, drawing }.Select(node => (string)Get(node, "FullPath")), StringComparer.OrdinalIgnoreCase);
        foreach (var node in new[] { root, part, drawing, closed, unrelatedDrawing, unrelatedModel }) Set(node, "ConfirmedArchivePaths", confirmed);
        var nativeCalls = 0;
        var forbiddenApp = new Proxy(appType, call => { if (call.MethodName == "GetType") return appType; nativeCalls++; throw new Exception("文件清单不得触发原生打开、激活、保存或Pack and Go：" + call.MethodName); }).GetTransparentProxy();
        SetField(plugin, "application", forbiddenApp); var scanner = New("SolidWorksReferenceTreeScanner", forbiddenApp);
        var before = WorkingReuseHash((string)Get(root, "FullPath"));
        var files = ((IEnumerable)TreeCall(plugin, "ReadSelectedNativeInventory", scanner, root)).Cast<string>().ToArray();
        Check(nativeCalls == 0, "正常存档文件清单零GetPackAndGo、激活或临时打开");
        Check(files.Length == 4 && files[0] == (string)Get(root, "FullPath") && files.Contains((string)Get(closed, "FullPath")), "保留根文件、已选工程图和实际已知压缩引用");
        Check(!files.Contains((string)Get(unrelatedDrawing, "FullPath")) && !files.Contains((string)Get(unrelatedModel, "FullPath")), "未勾选工程图不会牵入其他独立结构");
        Check(!files.Contains((string)Get(embedded, "FullPath")) && ((IList)Get(root, "Children")).Contains(embedded), "虚拟内容保留嵌入层级，不制造独立上传文件");
        Check((bool)Get(root, "StructureIndexPending"), "未展开子结构明确保持待补全，不冒充完整索引");
        Check(WorkingReuseHash((string)Get(root, "FullPath")) == before, "收集清单不修改已保存的原生文件");

        var documents = 500; var paths = Enumerable.Range(0, documents).Select(i => Path.Combine(output, "DOC-" + i + ".SLDPRT")).ToArray();
        var pathReads = 0; var collectionReads = 0; var closedIndex = -1;
        var models = Enumerable.Range(0, documents).Select(i => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
        {
            if (call.MethodName == "GetPathName") { pathReads++; if (closedIndex == -2 && i == 1) throw new InvalidComObjectException("模拟已释放包装对象"); if (closedIndex == i) throw new COMException("模拟已关闭文档", unchecked((int)0x80010108)); return paths[i]; }
            throw new Exception("索引不能修改或加载文档：" + call.MethodName);
        }).GetTransparentProxy()).ToArray();
        var indexedApp = new Proxy(appType, call => call.MethodName == "GetDocuments" ? ReadCollection() : throw new Exception("索引不能操作应用：" + call.MethodName)).GetTransparentProxy();
        object ReadCollection() { collectionReads++; return models; }
        SetField(plugin, "application", indexedApp); var context = New("PdmAddin+ArchiveReadContext", plugin);
        for (var index = 0; index < documents * 3; index++)
            if (!ReferenceEquals(TreeCall(context, "FindLoaded", paths[index % documents]), models[index % documents])) throw new Exception("索引返回错误文件");
        Check(collectionReads == 1 && pathReads <= documents * 4, "1500次查找只枚举一次500个文档，不重复全量COM路径遍历");
        SetField(plugin, "nativeLoadEpoch", 1); TreeCall(context, "FindLoaded", paths[0]);
        Check(collectionReads == 2, "原生新增加载使本轮文档索引失效并重新建立");
        closedIndex = 0;
        Check(TreeCall(context, "FindLoaded", paths[0]) == null, "已关闭COM文档失效时返回未加载，不复用旧对象");
        closedIndex = -2;
        Check(TreeCall(context, "FindLoaded", paths[1]) == null, "已释放的COM包装对象同样失效，不使整个存档异常退出");
        Check(TreeCall(context, "FindLoaded", Path.Combine(output, "other", "DOC-1.SLDPRT")) == null, "其他目录同名文件不被错误匹配");

        var featureReads = 0; const int featureCount = 1200;
        var features = Enumerable.Range(0, featureCount).Select(i => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Feature", true), call =>
            call.MethodName == "get_Name" ? "F" + i : call.MethodName == "GetTypeName2" ? "Boss"
                : call.MethodName == "get_Is3DInterconnectFeature" ? (object)false : throw new Exception(call.MethodName)).GetTransparentProxy()).ToArray();
        var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager", true), call =>
            call.MethodName == "GetFeatures" ? FeatureInventory() : call.MethodName == "GetFeatureCount" ? (object)featureCount : throw new Exception(call.MethodName)).GetTransparentProxy();
        object FeatureInventory() { featureReads++; return features; }
        var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
            call.MethodName == "ListExternalFileReferences2" ? null : throw new Exception(call.MethodName)).GetTransparentProxy();
        var sketches = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager", true), call =>
            call.MethodName == "GetSketchBlockDefinitions" ? new object[0] : throw new Exception(call.MethodName)).GetTransparentProxy();
        var model = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            call.MethodName == "GetType" ? (object)1 : call.MethodName == "get_Extension" ? extension
                : call.MethodName == "get_FeatureManager" ? manager : call.MethodName == "get_SketchManager" ? sketches
                : call.MethodName == "GetEquationMgr" || call.MethodName == "GetDesignTable" ? null : throw new Exception(call.MethodName)).GetTransparentProxy();
        var sources = (IEnumerable)Type("NativeDesignDependencies").GetMethod("ReadManaged", Flags).Invoke(null, new[] { model });
        Check(featureReads == 1 && !sources.Cast<object>().Any(), "每个模型的1200个特征仅建立一次目录，同时保留导入、库特征等来源检查");
    }
}
