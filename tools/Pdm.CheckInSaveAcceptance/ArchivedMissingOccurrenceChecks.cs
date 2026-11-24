using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
