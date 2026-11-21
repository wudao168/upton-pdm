using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

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
    }
}
