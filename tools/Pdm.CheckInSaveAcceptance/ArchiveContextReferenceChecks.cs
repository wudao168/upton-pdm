using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static void ArchiveContextReferenceScenario(string output, string fixture)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        var classify = Type("NativeDesignDependencies").GetMethod("IsAssemblyContext", Flags);
        bool Context(string model, string owner, string feature, int status) =>
            (bool)classify.Invoke(null, new object[] { model, owner, feature, status });
        Check(Context(@"C:\模型\总装.SLDASM", "零件<1>", "M10 螺纹孔1", 3), "装配内孔特征的上下文单独识别");
        Check(Context(@"C:\模型\总装.SLDASM", "零件<1>", "M10 螺纹孔2", 4), "未就绪的旧上下文仍保留关联而非擅自断开");
        Check(!Context(@"C:\模型\总装.SLDASM", "", "派生特征", 3), "从装配派生的真实文件来源不被当成上下文忽略");
        Check(!Context(@"C:\模型\源零件.SLDPRT", "零件<1>", "孔", 3), "普通零件来源仍是必须核对的真实依赖");
        Check(!Context(@"C:\模型\总装.SLDASM", "零件<1>", "孔", 0), "已断开关联不恢复为活动上下文");

        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        var ordinaryOverlap = false;
        var ext = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
        {
            if (call.MethodName != "ListExternalFileReferences2") throw new Exception("禁止其他原生调用：" + call.MethodName);
            var models = new[] { @"C:\模型\总装.SLDASM", @"C:\模型\旧装.SLDASM", ordinaryOverlap ? @"C:\模型\总装.SLDASM" : @"C:\模型\派生源.SLDPRT" };
            var components = new[] { @"C:\模型\孔源.SLDPRT", "", "默认" };
            return new ReturnMessage(null, new object[] { models, components, new[] { "孔1", "孔2", "派生" },
                new[] { "面", "面", "实体" }, new[] { 3, 4, 3 }, new string[3], new[] { "零件<1>", "零件<1>", "" },
                new int[3], new string[3] }, 9, call.LogicalCallContext, call);
        }).GetTransparentProxy();
        var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager", true), call =>
            call.MethodName == "GetFeatures" ? new object[0] : call.MethodName == "GetFeatureCount" ? (object)0 : throw new Exception(call.MethodName)).GetTransparentProxy();
        var sketches = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager", true), call =>
            call.MethodName == "GetSketchBlockDefinitions" ? new object[0] : throw new Exception(call.MethodName)).GetTransparentProxy();
        var model = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            call.MethodName == "GetType" ? (object)1 : call.MethodName == "get_Extension" ? ext
                : call.MethodName == "get_FeatureManager" ? manager : call.MethodName == "get_SketchManager" ? sketches
                : call.MethodName == "GetEquationMgr" || call.MethodName == "GetDesignTable" ? null : throw new Exception("禁止打开或改写图纸：" + call.MethodName)).GetTransparentProxy();
        var read = Type("NativeDesignDependencies").GetMethod("ReadCheckIn", Flags).Invoke(null, new[] { model });
        var sources = ((IEnumerable)Get(read, "Sources")).Cast<string>().ToArray();
        var contexts = ((IEnumerable)Get(read, "Contexts")).Cast<string>().ToArray();
        Check(contexts.Length == 2 && sources.Contains(@"C:\模型\总装.SLDASM") && sources.Contains(@"C:\模型\旧装.SLDASM"), "上下文文件仍纳入受控收集，未删除关联");
        var geometryOwners = ((IEnumerable)Get(read, "Owners")).Cast<object>();
        Check(!sources.Contains(@"C:\模型\孔源.SLDPRT") && sources.Contains(@"C:\模型\派生源.SLDPRT") && !sources.Contains("默认")
            && geometryOwners.Any(owner => (string)Type("NativeDesignDependencies+ContextOwner").GetField("Component", Flags).GetValue(owner) == @"C:\模型\孔源.SLDPRT"),
            "几何组件保留为上下文来源，普通派生文件仍保留为文件依赖");
        ordinaryOverlap = true;
        var overlap = Type("NativeDesignDependencies").GetMethod("ReadCheckIn", Flags).Invoke(null, new[] { model });
        Check(!((IEnumerable)Get(overlap, "Contexts")).Cast<string>().Contains(@"C:\模型\总装.SLDASM"), "同一文件另有普通派生用途时，不允许上下文标记隐藏真实依赖");

        var rows = ((object[])new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(fixture)))
            .Cast<Dictionary<string, object>>().ToArray();
        var nodes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var original = (string)row["path"]; var path = Path.Combine(output, Path.GetFileName(original));
            File.WriteAllText(path, "隔离测试数据"); paths[original] = path;
            var node = New("CadTreeNode"); nodes[original] = node;
            Set(node, "FullPath", path); Set(node, "FileName", Path.GetFileName(path)); Set(node, "DisplayName", Path.GetFileNameWithoutExtension(path));
            Set(node, "InstancePath", Path.GetFileName(path)); Set(node, "DocumentId", (Guid?)Guid.NewGuid()); Set(node, "Revision", "W1"); Set(node, "CurrentRevision", "W1");
            Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? "Assembly" : "Part"));
        }
        object Link(object target, bool context)
        {
            var link = New("CadTreeNode");
            foreach (var property in new[] { "FullPath", "FileName", "DisplayName", "DocumentId", "Kind", "Revision", "CurrentRevision" }) Set(link, property, Get(target, property));
            Set(link, "InstancePath", "依赖/" + Get(target, "FileName")); Set(link, "Role", Enum.Parse(Type("CadReferenceRole"), "ExternalFileDependency"));
            Set(link, "IsContextReference", context); return link;
        }
        foreach (var row in rows)
        {
            var path = (string)row["path"]; var owner = nodes[path]; var children = (IList)Get(owner, "Children");
            foreach (Dictionary<string, object> component in (object[])row["components"])
                if (!(bool)component["isVirtual"] && string.Equals((string)component["parentPath"], path, StringComparison.OrdinalIgnoreCase)
                    && nodes.TryGetValue((string)component["path"], out var child) && !children.Contains(child)) children.Add(child);
            var models = (object[])row["externalModels"]; var owners = (object[])row["externalOwners"];
            var features = (object[])row["externalFeatures"]; var statuses = (object[])row["externalStatuses"];
            var nativeContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < models.Length; i++)
                if (Context((string)models[i], (string)owners[i], (string)features[i], Convert.ToInt32(statuses[i]))) nativeContexts.Add((string)models[i]);
            var saved = (object[])row["savedReferences"];
            for (var i = 1; i < saved.Length; i += 2)
                foreach (var source in ((string)saved[i]).Split('|'))
                    if (!string.Equals(path, source, StringComparison.OrdinalIgnoreCase) && nodes.TryGetValue(source, out var target)
                        && !children.Cast<object>().Any(child => Equals(Get(child, "FullPath"), Get(target, "FullPath"))))
                        children.Add(Link(target, nativeContexts.Contains(source)));
        }
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));
        foreach (var node in nodes.Values) items.Add(New("BatchOperationItem", node, 0));
        var validate = Type("ArchiveReferenceCycles").GetMethod("Validate", Flags);
        validate.Invoke(null, new object[] { items });
        Check(nodes.Count == 68, "真实设备布局的68份只读引用记录通过；上下文回指不再形成提交循环");
        var part = nodes.Single(pair => Path.GetFileName(pair.Key) == "70004220.M2-01.SLDPRT").Value;
        var partLinks = ((IList)Get(part, "Children")).Cast<object>().Where(node => (bool)Get(node, "IsContextReference")).ToArray();
        Check(partLinks.Length == 2, "M2-01到70004220.03及设备布局的两个上下文均保留为独立记录");
        var snapshot = Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot", Flags).Invoke(null,
            new object[] { part, new HashSet<string>(paths.Values, StringComparer.OrdinalIgnoreCase) });
        var root = (Dictionary<string, object>)TreeCall(snapshot, "CreateRoot");
        Check(((object[])root["children"]).Cast<Dictionary<string, object>>().Count(node => Convert.ToBoolean(node["isContextReference"])) == 2,
            "提交快照保留上下文类型及身份，不悄悄删除引用");
        // Replay the actual recovery/check-in reader against fake COM and owned files.
        // No business file, running SolidWorks session, or live HTTP endpoint is used.
        var modelIndex = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var original = (string)row["path"]; var path = paths[original];
            var indexes = Enumerable.Range(0, ((object[])row["externalModels"]).Length)
                .Where(index => paths.ContainsKey((string)((object[])row["externalModels"])[index])).ToArray();
            object[] Column(string column, bool mapPath = false) => indexes.Select(index =>
            {
                var value = ((object[])row[column])[index];
                return mapPath && value is string text && paths.TryGetValue(text, out var mapped) ? mapped : value;
            }).ToArray();
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
            {
                if (call.MethodName == "ListExternalFileReferences2")
                    return new ReturnMessage(null, new object[] { Column("externalModels", true), Column("externalComponents", true),
                        Column("externalFeatures"), Column("externalTypes"), Column("externalStatuses"), new string[indexes.Length],
                        Column("externalOwners"), Column("externalOptions"), Column("externalConfigurations") }, 9, call.LogicalCallContext, call);
                if (call.MethodName == "GetOLEObjects" || call.MethodName == "GetRenderMaterials") return new object[0];
                if (call.MethodName == "GetOLEObjectCount") return 0;
                throw new Exception("禁止额外引用处理：" + call.MethodName);
            }).GetTransparentProxy();
            var fake = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                call.MethodName == "GetType" ? (object)(path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                : call.MethodName == "GetPathName" ? path : call.MethodName == "GetSaveFlag" ? (object)false
                : call.MethodName == "get_Extension" ? extension : call.MethodName == "get_FeatureManager" ? manager
                : call.MethodName == "get_SketchManager" ? sketches : call.MethodName == "GetEquationMgr" || call.MethodName == "GetDesignTable" ? null
                : throw new Exception("禁止打开、保存或修改原图：" + call.MethodName)).GetTransparentProxy();
            modelIndex[path] = fake;
            foreach (var link in ((IList)Get(nodes[original], "Children")).Cast<object>())
                if ((int)Get(link, "Role") == 1) Set(link, "IsContextReference", false);
            Set(nodes[original], "ConfirmedArchivePaths", new HashSet<string>(paths.Values, StringComparer.OrdinalIgnoreCase));
        }
        var rowIndex = rows.ToDictionary(row => paths[(string)row["path"]], StringComparer.OrdinalIgnoreCase);
        var nativeApp = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SldWorks", true), call =>
        {
            if (call.MethodName == "GetDocuments") return modelIndex.Values.ToArray();
            if (call.MethodName == "GetDocumentDependencies2")
            {
                var saved = (object[])rowIndex[(string)call.Args[0]]["savedReferences"]; var values = new List<object>();
                for (var i = 1; i < saved.Length; i += 2)
                    if (paths.TryGetValue((string)saved[i], out var mapped)) { values.Add(saved[i - 1]); values.Add(mapped); }
                return values.ToArray();
            }
            throw new Exception("禁止加载其他图档：" + call.MethodName);
        }).GetTransparentProxy();
        var plugin = New("PdmAddin"); SetField(plugin, "application", nativeApp);
        SetField(plugin, "currentTree", nodes.Single(pair => Path.GetFileName(pair.Key) == "设备布局.SLDASM").Value);
        var completeTask = (System.Threading.Tasks.Task)TreeCall(plugin, "CompletePdmCheckInDependenciesAsync", items, Guid.NewGuid(), System.Threading.CancellationToken.None);
        completeTask.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "context-replay.json"), new JavaScriptSerializer().Serialize(new {
            contexts = ((IEnumerable)Get(part, "DesignContextPaths")).Cast<string>().ToArray(),
            links = ((IList)Get(part, "Children")).Cast<object>().Select(link => new { file=Get(link,"FileName"), context=Get(link,"IsContextReference"), role=Get(link,"Role") }).ToArray()
        }));
        validate.Invoke(null, new object[] { items });
        Check(partLinks.All(link => (bool)Get(link, "IsContextReference")), "真实检入引用收集入口能恢复正确上下文类型，零原生加载或保存");
        Check(((IEnumerable)Get(completeTask, "Result")).Cast<object>().Count() == 68, "上下文修复不扩大68份已选文件的上传范围");
        foreach (var link in partLinks) Set(link, "IsContextReference", false);
        try { validate.Invoke(null, new object[] { items }); throw new Exception("真实循环未拦截"); }
        catch (TargetInvocationException error)
        {
            Check(error.InnerException is InvalidOperationException && error.InnerException.Message.Contains("已确认文件依赖存在循环")
                && error.InnerException.Message.Contains("70004220.M2-01"), "普通装配或特征循环仍提前拦截并列出循环分组图档");
        }
        foreach (var link in partLinks) Set(link, "IsContextReference", true);
        var invalid = partLinks[0]; Set(invalid, "Role", Enum.Parse(Type("CadReferenceRole"), "Component"));
        try { validate.Invoke(null, new object[] { items }); throw new Exception("不允许上下文标记隐藏成员"); }
        catch (TargetInvocationException error) { Check(error.InnerException.Message.Contains("上下文关联记录"), "上下文标记不能掩盖真实装配成员关系"); }
    }
}
