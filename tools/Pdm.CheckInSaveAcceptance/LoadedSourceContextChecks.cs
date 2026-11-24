using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void LoadedSourceContextScenario(string output)
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        foreach (var mode in new[] { "nested", "reordered", "multiple-instances", "foreign-descendant", "suppressed-history", "missing-lightweight", "virtual" })
        {
            var folder = Path.GetFullPath(Path.Combine(output, mode));
            var rootPath = Path.Combine(folder, "Root.SLDASM");
            var childPath = Path.Combine(folder, "Child.SLDASM");
            var leafPath = Path.Combine(folder, "Leaf.SLDPRT");
            var external = Path.Combine(output, "another", "Leaf.SLDPRT");
            var manifest = New("ControlledOpenManifestDto");
            var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
            foreach (var path in new[] { rootPath, childPath, leafPath })
            {
                var file = New("ControlledOpenFileDto");
                Set(file, "FileName", Path.GetFileName(path)); Set(file, "RelativePath", Path.GetFileName(path));
                files.Add(file);
            }
            Set(manifest, "Files", files);
            var scans = 0; var reroots = 0;
            object[] rootComponents = null;
            object Model(string path, bool assembly, bool root = false)
            {
                Func<IMethodCallMessage, object> invoke = call =>
                {
                    if (call.MethodName == "GetType" && (call.MethodBase as MethodInfo)?.ReturnType == typeof(Type)) return modelType;
                    switch (call.MethodName)
                    {
                        case "GetPathName": return path;
                        case "GetType": return assembly ? 2 : 1;
                        case "GetComponents":
                            if (!root) { reroots++; throw new Exception("不能用子装配独立窗口的活动配置再次遍历实例"); }
                            if ((bool)call.Args[0]) throw new Exception("来源核对必须覆盖根装配所有层级");
                            scans++; return rootComponents;
                        case "GetDesignTable": case "GetEquationMgr": case "get_Extension": return null;
                        default: throw new Exception("来源核对不能保存、关闭、重建或切换配置：" + call.MethodName);
                    }
                };
                return assembly ? new AssemblyProxy(modelType, invoke).GetTransparentProxy() : new Proxy(modelType, invoke).GetTransparentProxy();
            }
            object Component(string path, object model, bool embedded = false) =>
                new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
                {
                    switch (call.MethodName)
                    {
                        case "GetPathName": return path;
                        case "GetModelDoc2": return model;
                        case "get_IsVirtual": return embedded;
                        case "get_Name2": return Path.GetFileName(path);
                        case "IsSuppressed": return model == null;
                        case "GetSuppression2": return model != null ? 2 : mode == "missing-lightweight" ? 1 : 0;
                        default: throw new Exception(call.MethodName);
                    }
                }).GetTransparentProxy();
            var child = Model(childPath, true);
            var leaf = Model(mode == "foreign-descendant" || mode == "virtual" ? external : leafPath, false);
            var rows = new List<object> { Component(childPath, child), Component(leafPath, leaf, mode == "virtual") };
            if (mode == "multiple-instances") rows.Add(Component(childPath, child));
            if (mode == "reordered") rows.Reverse();
            if (mode == "suppressed-history" || mode == "missing-lightweight") rows.Add(Component(Path.Combine(output, "missing", "History.SLDPRT"), null));
            rootComponents = rows.ToArray();
            var root = Model(rootPath, true, true);
            var app = new Proxy(appType, call => throw new Exception("来源核对不能重新打开任何图档：" + call.MethodName)).GetTransparentProxy();
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception failure = null;
            try { TreeCallStatic("PdmAddin", "ValidateControlledLoadedSources", root, rootPath, manifest, app, missing, true); }
            catch (TargetInvocationException error) { failure = error.InnerException; }
            TreeCheck(scans == 1 && reroots == 0, "重复读取了实例或切换到子装配活动配置：" + mode);
            TreeCheck(mode == "foreign-descendant" || mode == "missing-lightweight" ? failure is InvalidOperationException : failure == null,
                "来源核对结果错误：" + mode + " " + failure);
            TreeCheck(missing.Count == (mode == "suppressed-history" ? 1 : 0), "历史缺件记录错误");
            Console.WriteLine("PASS: loaded source context " + mode + "; one root scan, no child reroot or native mutations");
        }
        DrawingOccurrenceSourceScenario(output);
    }
}
