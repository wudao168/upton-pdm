using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static void ArchivedMissingOccurrenceScenario(string output)
    {
        var root = Directory.CreateDirectory(Path.GetFullPath(output)).FullName;
        var componentType = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly
            .GetType("SolidWorks.Interop.sldworks.IComponent2", true);
        var archivedType = Type("ArchivedMissingOccurrence");
        foreach (var mode in new[] { "identical", "reordered", "nested-prefix", "removed", "added", "changed-instance",
            "swapped-configurations", "lightweight", "resolved", "foreign-path", "archived-foreign", "virtual",
            "empty-configuration", "archived-loaded", "empty-archived", "identical-duplicates", "loaded-model", "exists" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, mode)).FullName;
            var missing = Path.Combine(directory, "缺失的传感器.SLDPRT");
            var other = Path.Combine(directory, "other", Path.GetFileName(missing));
            var sibling = Path.Combine(directory, "可读的受控零件.SLDPRT");
            File.WriteAllText(sibling, "existing part");
            if (mode == "exists") File.WriteAllText(missing, "unexpected materialized file");

            object Saved(string name, string configuration, string path, int suppression = 0)
            {
                var row = Activator.CreateInstance(archivedType);
                Set(row, "Path", path);
                Set(row, "Configuration", configuration);
                Set(row, "Instance", name);
                Set(row, "Suppression", suppression);
                return row;
            }
            object Native(string name, string configuration, string path, int suppression = 0, bool virtualComponent = false)
            {
                return new Proxy(componentType, call =>
                {
                    switch (call.MethodName)
                    {
                        case "get_Name2": return name;
                        case "get_ReferencedConfiguration": return configuration;
                        case "GetPathName": return path;
                        case "GetSuppression": case "GetSuppression2": return suppression;
                        case "GetModelDoc2": return mode == "loaded-model" && name == "传感器-2" ? new object() : null;
                        case "get_IsVirtual": return virtualComponent;
                        default: throw new InvalidOperationException("unexpected native call " + call.MethodName);
                    }
                }).GetTransparentProxy();
            }
            const string first = "传感器-1", second = "传感器-2", standard = "默认", alternate = "短型";
            var archived = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(archivedType));
            archived.Add(Saved("正常件-1", standard, sibling, 2));
            if (mode != "empty-archived")
            {
                archived.Add(Saved(first, standard, missing));
                archived.Add(Saved(mode == "identical-duplicates" ? first : second,
                    mode == "identical-duplicates" ? standard : alternate, mode == "archived-foreign" ? other : missing, mode == "archived-loaded" ? 2 : 0));
            }
            var current = new List<object> { Native("正常件-1", standard, sibling, 2) };
            current.Add(Native(mode == "nested-prefix" ? "父装-1/" + first : first, mode == "swapped-configurations" ? alternate : standard, missing));
            if (mode != "removed")
            {
                var name = mode == "changed-instance" ? "传感器-9" : mode == "identical-duplicates" ? first : second;
                var config = mode == "empty-configuration" ? "" : mode == "swapped-configurations" || mode == "identical-duplicates" ? standard : alternate;
                current.Add(Native(name, config, mode == "foreign-path" ? other : missing,
                    mode == "lightweight" ? 1 : mode == "resolved" ? 2 : 0, mode == "virtual"));
            }
            if (mode == "added") current.Add(Native("传感器-3", alternate, missing));
            if (mode == "reordered") current.Reverse();
            var components = Array.CreateInstance(componentType, current.Count);
            for (var i = 0; i < current.Count; i++) components.SetValue(current[i], i);

            var accepted = (bool)TreeCallStatic("PdmAddin", "ArchivedMissingOccurrencesMatch", missing, components, archived);
            var expected = mode == "identical" || mode == "reordered" || mode == "nested-prefix";
            TreeCheck(accepted == expected, "历史缺件实例比对有误：" + mode);
            Console.WriteLine("PASS: archived missing occurrence " + mode);
        }
        VerifyArchivedOccurrenceReuseAsync(root).GetAwaiter().GetResult();
        VerifyArchivedToolboxAdvisoryAsync(root).GetAwaiter().GetResult();
        VerifyCachedToolboxOccurrences(root);
    }

    private static void VerifyCachedToolboxOccurrences(string directory)
    {
        const string assemblyName = "CACHE-OWNER.SLDASM";
        const string partName = "hexagon socket head cap screws gb.sldprt";
        const string partConfig = "GB_FASTENER_SCREWS_HSHCS M8X12-N";
        const string instance = "hexagon socket head cap screws gb-2";
        const string original = @"Z:\old-cache\GB_FASTENER_SCREWS_HSHCS M8X12-N.SLDPRT";
        const string library = @"C:\SOLIDWORKS Data\browser\gb\hexagon socket head cap screws gb.sldprt";
        var rootPath = Path.Combine(directory, assemblyName);
        var cachedPath = Path.Combine(directory, partName);
        File.WriteAllText(rootPath, "Assembly with a Toolbox reference excluded from the archive index");
        File.WriteAllText(cachedPath, "Toolbox library copy cached as a project file");
        const string ordinaryName = "NORMAL.SLDPRT";
        var ordinaryPath = Path.Combine(directory, ordinaryName);
        File.WriteAllText(ordinaryPath, "Ordinary controlled component");
        var ordinaryId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var warning = "Toolbox引用提醒：" + original + "；Toolbox引用待核对，本次不上传；实例 ROOT-1/OWNER-1/" + instance
            + "；实际来源 " + library + "；原有关联保留，结构待补全";
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        System.Type NativeType(string name) => interop.GetType("SolidWorks.Interop.sldworks." + name, true);
        object Native(string name, Func<System.Runtime.Remoting.Messaging.IMethodCallMessage, object> call)
            => new Proxy(NativeType(name), call).GetTransparentProxy();
        var config = Native("Configuration", call => call.MethodName == "get_Name" ? "Default" : throw new Exception(call.MethodName));
        var manager = Native("ConfigurationManager", call => call.MethodName == "get_ActiveConfiguration" ? config : throw new Exception(call.MethodName));
        var app = Native("ISldWorks", call => call.MethodName == "GetUserPreferenceStringValue" ? string.Empty : throw new Exception(call.MethodName));
        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
        var ordinary = Native("Component2", call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return ordinaryPath;
                case "get_ReferencedConfiguration": return "Default";
                case "get_IsVirtual": return false;
                case "GetModelDoc2": return null;
                default: throw new Exception(call.MethodName);
            }
        });
        foreach (var mode in new[] { "archived-cached", "missing-after-archive", "wrong-owner", "wrong-instance",
            "foreign-copy", "foreign-loaded-model", "native-copy", "ordinary-part", "ordinary-removed" })
        {
            var partPath = mode == "foreign-copy" ? Path.Combine(directory, "foreign", partName) : cachedPath;
            var type = Native("ModelDocExtension", call => call.MethodName == "get_ToolboxPartType"
                ? (object)(mode == "native-copy" ? 2 : 0) : throw new Exception(call.MethodName));
            var part = Native("ModelDoc2", call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return mode == "foreign-loaded-model" ? Path.Combine(directory, "foreign", partName) : partPath;
                    case "get_Extension": return type;
                    default: throw new Exception(call.MethodName);
                }
            });
            var child = Native("Component2", call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return partPath;
                    case "get_Name2": return "OWNER-1/" + (mode == "wrong-instance" ? partName + "-3" : instance);
                    case "get_ReferencedConfiguration": return partConfig;
                    case "get_IsVirtual": return false;
                    case "GetModelDoc2": return part;
                    default: throw new Exception(call.MethodName);
                }
            });
            var root = new AssemblyProxy(NativeType("ModelDoc2"), call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return rootPath;
                    case "get_ConfigurationManager": return manager;
                    case "GetComponents":
                        return mode == "missing-after-archive" ? new[] { ordinary }
                            : mode == "ordinary-removed" ? new[] { child } : new[] { ordinary, child };
                    default: throw new Exception(call.MethodName);
                }
            }).GetTransparentProxy();
            var manifest = json.Deserialize(json.Serialize(new
            {
                rootDocumentId = parentId,
                files = new[] {
                    new { documentId = parentId, fileName = assemblyName, relativePath = assemblyName },
                    new { documentId = partId, fileName = partName, relativePath = partName },
                    new { documentId = ordinaryId, fileName = ordinaryName, relativePath = ordinaryName } },
                referenceIssues = mode is "native-copy" or "ordinary-part" ? new object[0] : new object[] {
                    new { isToolbox = true, ownerDocumentId = mode == "wrong-owner" ? Guid.NewGuid() : parentId, reason = warning } }
            }), Type("ControlledOpenManifestDto"));
            var snapshot = json.Deserialize(json.Serialize(new
            {
                documentId = parentId, fileName = assemblyName, configuration = "Default",
                children = new object[] { new { documentId = ordinaryId, fileName = ordinaryName, configuration = "Default", kind = 1 } }
                    .Concat(mode == "missing-after-archive" ? new object[] {
                        new { fileName = Path.GetFileName(original), instancePath = "OWNER-1/" + instance, configuration = partConfig, kind = 1 } }
                        : Array.Empty<object>()).ToArray()
            }), Type("DocumentReferenceNodeDto"));
            var exactSnapshots = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentReferenceNodeDto")));
            exactSnapshots.Add(parentId, snapshot);
            var conflict = string.Empty;
            var result = (bool)TreeCallStatic("PdmAddin", "WorkingReuseLoadedContextForEditMatches", root, directory,
                manifest, snapshot, exactSnapshots, null, null, (Action<string>)(reason => conflict = reason),
                null, null, false, null, null, app, null, null);
            TreeCheck(result == (mode is "archived-cached" or "missing-after-archive" or "native-copy"),
                "Toolbox实例核对范围错误：" + mode + " " + conflict);
            Console.WriteLine("PASS: Toolbox advisory preserves checkout reference scope " + mode);
        }
    }

    private static async Task VerifyArchivedToolboxAdvisoryAsync(string root)
    {
        var missing = Path.Combine(root, "moved-standard-part.SLDPRT");
        var version = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var owner = New("ControlledOpenFileDto");
        Set(owner, "DocumentId", documentId);
        Set(owner, "VersionId", version);
        Set(owner, "FileName", "OWNER.SLDASM");
        var componentType = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly
            .GetType("SolidWorks.Interop.sldworks.IComponent2", true);
        var component = new Proxy(componentType, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return missing;
                case "GetSuppression": case "GetSuppression2": return 1;
                case "GetModelDoc2": return null;
                case "get_IsVirtual": return false;
                case "get_Name2": return "螺钉-1";
                case "get_ReferencedConfiguration": return "Default";
                default: throw new InvalidOperationException("unexpected native call " + call.MethodName);
            }
        }).GetTransparentProxy();
        var components = Array.CreateInstance(componentType, 1);
        components.SetValue(component, 0);
        var scope = New("PdmAddin+WorkingAssemblyReferenceScope");
        SetField(scope, "Components", components);
        SetField(scope, "Configuration", "Default");
        SetField(scope, "HistoricalMissingPaths", new[] { missing });
        var policy = Type("ArchiveMissingReferencePolicy");
        var parser = policy.GetMethod("ArchivedToolboxReferences", Flags);
        var record = "Toolbox引用提醒：" + missing + "；Toolbox引用待核对，本次不上传；实例 OWNER/螺钉-1；实际来源 未加载";
        var parsed = ((IEnumerable)parser.Invoke(null, new object[] { new[] { record,
            "存档引用提示：" + missing + "；引用文件缺失，未勾选，本次不上传；原有关联保留" } })).Cast<object>().ToArray();
        TreeCheck(parsed.Length == 1 && (string)Field(parsed[0], "InstancePath") == "OWNER/螺钉-1",
            "只有明确登记的Toolbox提醒保留其原有实例信息");
        var advisory = Array.CreateInstance(parsed[0].GetType(), 1);
        advisory.SetValue(parsed[0], 0);
        SetField(scope, "HistoricalToolboxReferences", advisory);
        var plugin = New("PdmAddin");
        var files = Array.CreateInstance(Type("ControlledOpenFileDto"), 0);
        var proof = New("PdmAddin+WorkingAssemblyReferenceProof");
        var direct = new Dictionary<Guid, HashSet<string>> { [documentId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { missing } };
        await (Task)TreeCall(plugin, "VerifyArchivedMissingOccurrencesAsync", scope, owner, files, proof, direct, null);
        TreeCheck(((IEnumerable)Field(proof, "RetainedMissing")).Cast<string>().SequenceEqual(new[] { missing })
            && ((IEnumerable)Field(proof, "RetainedToolbox")).Cast<object>().Count() == 1,
            "同一归档父版本的Toolbox缺件只提醒，加载状态变化不触发普通缺件阻断");
        var unrelated = New("PdmAddin+WorkingAssemblyReferenceProof");
        direct[documentId].Clear();
        SetField(scope, "Components", Array.CreateInstance(componentType, 0));
        await (Task)TreeCall(plugin, "VerifyArchivedMissingOccurrencesAsync", scope, owner, files, unrelated, direct, null);
        TreeCheck(!((IEnumerable)Field(unrelated, "RetainedMissing")).Cast<string>().Any(),
            "来自别的装配的提醒不能放行当前父版本没有的引用");
        Console.WriteLine("PASS: archived Toolbox advisory remains non-blocking and scoped to its saved owner");
    }

    private static async Task VerifyArchivedOccurrenceReuseAsync(string root)
    {
        var archive = Path.Combine(root, "PARENT.SLDASM");
        File.WriteAllText(archive, "archived parent");
        var plugin = New("PdmAddin");
        using var lifetime = new CancellationTokenSource();
        SetField(plugin, "lifetime", lifetime);
        var document = New("ControlledOpenFileDto");
        Set(document, "DocumentId", Guid.NewGuid());
        Set(document, "VersionId", Guid.NewGuid());
        Set(document, "FileName", Path.GetFileName(archive));
        Set(document, "FileLength", new FileInfo(archive).Length);
        Set(document, "Sha256", WorkingReuseHash(archive));
        var reads = 0;
        NativeMissingAcquisition.InstallReader(plugin, (_, configuration, __) =>
        {
            reads++;
            return new[] { new NativeMissingAcquisition.Row { Path = Path.Combine(root, "missing.SLDPRT"),
                Instance = "sensor-1", Configuration = configuration, Suppression = 0 } };
        });
        var read = plugin.GetType().GetMethod("ReadVerifiedArchivedOccurrencesAsync", Flags);
        Task Read(string configuration) => (Task)read.Invoke(plugin, new object[] { document, archive, configuration, null });
        await Read("Default");
        await Read("Default");
        TreeCheck(reads == 1, "同一存档父版本和配置重复启动原生读取器");
        await Read("Other");
        TreeCheck(reads == 2, "另一个配置复用了原始配置证据");
        Set(document, "VersionId", Guid.NewGuid());
        await Read("Default");
        TreeCheck(reads == 3, "新版本复用了旧版本证据");
        File.WriteAllText(archive, "modified parent");
        try
        {
            await Read("Default");
            throw new InvalidOperationException("缓存命中后接受了被替换的源字节");
        }
        catch (IOException) { }
        TreeCheck(reads == 3, "损坏的原件触发新的原生读取");
        File.WriteAllText(archive, "archived parent");
        plugin.GetType().GetMethod("ResetArchivedOccurrenceEvidence", Flags).Invoke(plugin, Array.Empty<object>());
        await Read("Default");
        TreeCheck(reads == 4, "重新登录后继续复用了上次证据");
        lifetime.Cancel();
        try
        {
            await Read("Default");
            throw new InvalidOperationException("取消的获取操作仍接受缓存证据");
        }
        catch (OperationCanceledException) { }
        TreeCheck(reads == 4, "取消后执行了新的原生读取");
        Console.WriteLine("PASS: versioned archive occurrence reuse; repeat readers 2 -> 1; config, version, hash, login and cancellation rechecked");
    }
}
