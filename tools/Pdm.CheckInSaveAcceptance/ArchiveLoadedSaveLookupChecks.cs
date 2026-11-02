using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task ArchiveLoadedSaveLookupScenario(string output)
    {
        Directory.CreateDirectory(output);
        var plugin = New("PdmAddin");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        var thread = Thread.CurrentThread.ManagedThreadId;
        var paths = Enumerable.Range(0, 100).Select(i => Path.GetFullPath(Path.Combine(output, "PART-" + i + ".SLDPRT"))).ToArray();
        var enumerations = 0; var pathReads = 0;
        var models = paths.Select(path => new Proxy(modelType, call =>
        {
            TreeCheck(Thread.CurrentThread.ManagedThreadId == thread, "文档读取离开CAD线程");
            if (call.MethodName == "GetPathName") { pathReads++; return path; }
            if (call.MethodName == "GetSaveFlag") return false;
            throw new Exception("未修改图档不应执行：" + call.MethodName);
        }).GetTransparentProxy()).ToArray();
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            if (call.MethodName == "GetDocuments") { enumerations++; return models; }
            throw new Exception("仅允许读取已打开文档：" + call.MethodName);
        }).GetTransparentProxy());
        Array Items(string[] names)
        {
            var values = Array.CreateInstance(Type("BatchOperationItem"), names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                var node = New("CadTreeNode"); Set(node, "FullPath", names[i]); Set(node, "FileName", Path.GetFileName(names[i]));
                Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
                values.SetValue(New("BatchOperationItem", node, 0), i);
            }
            return values;
        }
        async Task CheckFiles(string[] names) => await (Task)TreeCall(plugin, "EnsureBatchCheckInSavedStateAsync", Items(names), Guid.NewGuid(), CancellationToken.None);
        await CheckFiles(paths);
        TreeCheck(enumerations == 1, "保存状态核对重复枚举已打开文档：" + enumerations);
        TreeCheck(pathReads == 200, "保存状态核对未保持线性文档路径读取：" + pathReads);
        Console.WriteLine("PASS: 100个文件只枚举一次文档，路径读取200次，所有原生调用保持所属线程且没有保存行为");
        enumerations = 0; pathReads = 0;
        await CheckFiles(new[] { Path.Combine(output, "sub", "..", "PART-0.SLDPRT") });
        TreeCheck(enumerations == 1 && pathReads == 101, "归一化路径未直接命中索引");
        Console.WriteLine("PASS: 非精确路径直接归一化匹配，不丢失已打开文档或重复枚举");
        enumerations = 0; pathReads = 0;
        await CheckFiles(paths.Concat(Enumerable.Range(0, 100).Select(i => Path.Combine(output, "CLOSED-" + i + ".SLDPRT"))).ToArray());
        TreeCheck(enumerations == 1 && pathReads == 200, "未打开文件触发了逐文件全量枚举");
        Console.WriteLine("PASS: 100个已打开及100个未打开文件共享一次枚举，未命中不重复扫描");
        var contextType = Type("PdmAddin").GetNestedType("ArchiveReadContext", Flags);
        var context = Activator.CreateInstance(contextType, Flags, null, new[] { plugin }, null);
        var late = Path.GetFullPath(Path.Combine(output, "LATE.SLDPRT"));
        TreeCheck(TreeCall(context, "FindLoaded", late) == null, "不存在的文档应未命中");
        models = models.Concat(new[] { new Proxy(modelType, call => call.MethodName == "GetPathName" ? late : throw new Exception(call.MethodName)).GetTransparentProxy() }).ToArray();
        SetField(plugin, "nativeLoadEpoch", (int)Field(plugin, "nativeLoadEpoch") + 1);
        TreeCheck(TreeCall(context, "FindLoaded", late) != null, "文档加载后未使索引失效");
        Console.WriteLine("PASS: 新文档加载事件使未命中缓存失效，重新读取可找到新文档");
        var longPath = @"C:\native\" + new string('x', 270) + @"\LONG.SLDPRT"; var longSaveReads = 0;
        models = new[] { new Proxy(modelType, call =>
        {
            if (call.MethodName == "GetPathName") return longPath;
            if (call.MethodName == "GetSaveFlag") { longSaveReads++; return false; }
            throw new Exception(call.MethodName);
        }).GetTransparentProxy() };
        await CheckFiles(new[] { longPath });
        TreeCheck(longSaveReads > 0, "原生已打开长路径被归一化失败误当成未打开");
        Console.WriteLine("PASS: 已打开长路径保留精确匹配，不漏掉保存状态检查");
        var originalPath = paths[0]; var observedPath = originalPath;
        models = new[] { new Proxy(modelType, call => call.MethodName == "GetPathName" ? observedPath : throw new Exception(call.MethodName)).GetTransparentProxy() };
        context = Activator.CreateInstance(contextType, Flags, null, new[] { plugin }, null);
        TreeCheck(TreeCall(context, "FindLoaded", originalPath) != null, "未建立原文档索引");
        observedPath = paths[1];
        models = new[] { new Proxy(modelType, call => call.MethodName == "GetPathName" ? originalPath : throw new Exception(call.MethodName)).GetTransparentProxy() };
        TreeCheck(TreeCall(context, "FindLoaded", originalPath) != null, "失效的已命中文档未重建索引");
        Console.WriteLine("PASS: 已命中文档失效时定向刷新一次，保留重新打开文档的识别");
        var equal = Type("PdmAddin").GetMethod("PathsEqual", Flags);
        TreeCheck((bool)equal.Invoke(null, new object[] { paths[0], paths[0].ToUpperInvariant() }), "相同路径大小写匹配失效");
        TreeCheck(!(bool)equal.Invoke(null, new object[] { "", "" }), "空路径不能相等");
        TreeCheck(!(bool)equal.Invoke(null, new object[] { paths[0], Path.Combine(output, "other", "PART-0.SLDPRT") }), "不同目录同名文件误匹配");
        Console.WriteLine("PASS: 保留大小写、空路径和不同目录同名文件的匹配规则");
    }
}
