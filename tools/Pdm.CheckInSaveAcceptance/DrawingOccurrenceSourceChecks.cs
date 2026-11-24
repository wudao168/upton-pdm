using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void DrawingOccurrenceSourceScenario(string output)
    {
        var folder = Directory.CreateDirectory(Path.Combine(output, Path.GetRandomFileName())).FullName;
        var store = Type("PdmDocumentIdentityStore");
        var previous = new[] { "bindingDirectory", "workspaceRootResolver", "isolatedReadScope" }
            .Select(name => store.GetField(name, Flags)).ToDictionary(field => field, field => field.GetValue(null));
        try
        {
            store.GetMethod("ConfigureBrowserReadScope", Flags).Invoke(null, new object[] { folder });
            RunDrawingOccurrenceSourceCases(folder);
        }
        finally { foreach (var value in previous) value.Key.SetValue(null, value.Value); }
    }

    static void RunDrawingOccurrenceSourceCases(string output)
    {
        var success = new HashSet<string> { "loaded", "deferred", "two-consistent-owners", "suppressed-history",
            "invalid-loaded", "invalid-suppressed-history", "invalid-lightweight-owner", "two-valid-views",
            "bound-original", "bound-acquired", "invalid-lightweight-leaf", "invalid-fully-lightweight-leaf" };
        foreach (var mode in success.Concat(new[] { "other-view-config", "other-root-config", "other-owner-config",
            "other-child-config", "foreign-loaded", "foreign-deferred", "two-conflicting-owners",
            "duplicate-instance", "wrong-parent", "missing-instance", "visible-history", "view-only-root", "uncontrolled-view",
            "invalid-with-parent", "invalid-two-configurations", "valid-empty-config", "invalid-root-component", "second-view-config",
            "suppressed-owner", "unresolved-owner", "external-intermediate", "different-intermediate-config",
            "invalid-intermediate", "missing-intermediate", "duplicate-intermediate", "id-mismatch-placeholder",
            "id-mismatch-owner", "id-mismatch-intermediate", "id-mismatch-leaf", "stale-handle-known-config",
            "wrong-root-version", "missing-root-version", "unbound-modified", "bound-other-document", "bound-other-project",
            "bound-other-version", "bound-other-sha", "bound-changed-no-proof", "bound-changed-after-proof" }))
        {
            var folder = Directory.CreateDirectory(Path.Combine(output, "drawing-occurrence-" + mode)).FullName;
            var rootPath = Path.Combine(folder, "Root.SLDASM");
            var ownerPath = Path.Combine(folder, "F1.SLDASM");
            var partPath = Path.Combine(folder, "Sensor.SLDPRT");
            var stationPath = Path.Combine(folder, "F1.05.SLDASM");
            var bracketPath = Path.Combine(folder, "Bracket.SLDASM");
            var drawingPath = Path.Combine(folder, "Station.SLDDRW");
            var foreign = Path.Combine(folder, "outside", "Sensor.SLDPRT");
            var history = Path.Combine(folder, "outside", "Historical.SLDPRT");
            var manifest = New("ControlledOpenManifestDto");
            var rootId = Guid.NewGuid();
            var rootVersion = Guid.NewGuid();
            var project = Guid.NewGuid();
            Set(manifest, "RootRelativePath", Path.GetFileName(rootPath));
            Set(manifest, "RootDocumentId", rootId);
            Set(manifest, "RootVersionId", mode == "missing-root-version" ? Guid.Empty : mode == "wrong-root-version" ? Guid.NewGuid() : rootVersion);
            Set(manifest, "ProjectId", project);
            var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
            foreach (var path in new[] { rootPath, ownerPath, stationPath, bracketPath, partPath, drawingPath })
            {
                File.WriteAllText(path, Path.GetFileName(path));
                var file = New("ControlledOpenFileDto");
                Set(file, "DocumentId", path == rootPath ? rootId : Guid.NewGuid());
                Set(file, "VersionId", path == rootPath ? rootVersion : Guid.NewGuid());
                Set(file, "IsRoot", path == rootPath); Set(file, "Configuration", "Default");
                Set(file, "FileName", Path.GetFileName(path)); Set(file, "RelativePath", Path.GetFileName(path));
                Set(file, "Sha256", WorkingReuseHash(path)); Set(file, "FileLength", new FileInfo(path).Length);
                files.Add(file);
            }
            Set(manifest, "Files", files);
            if (mode.StartsWith("bound-", StringComparison.Ordinal))
            {
                var saved = Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion", Flags)
                    .Invoke(null, new object[] { rootPath, mode == "bound-other-document" ? Guid.NewGuid() : rootId,
                        mode == "bound-other-project" ? Guid.NewGuid() : project,
                        mode == "bound-other-version" ? Guid.NewGuid() : rootVersion, "W1",
                        mode == "bound-other-sha" ? new string('A', 64) : WorkingReuseHash(rootPath), new FileInfo(rootPath).Length });
                TreeCheck((bool)saved, mode + "：写入隔离测试工作区的原始版本绑定失败。");
            }
            var retained = mode == "bound-acquired" || mode == "bound-changed-after-proof";
            if (retained || mode == "unbound-modified" || mode == "bound-changed-no-proof")
                File.AppendAllText(rootPath, "本地保存的装配修改");
            if (retained)
            {
                ((IDictionary)Get(manifest, "RetainedWorkingHashes"))[rootId] = WorkingReuseHash(rootPath);
                if (mode == "bound-changed-after-proof") File.AppendAllText(rootPath, "预检后的再次改动");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(foreign)); File.Copy(partPath, foreign, true);
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
            object Mock(string suffix, Func<IMethodCallMessage, object> read) =>
                new Proxy(interop.GetType("SolidWorks.Interop.sldworks." + suffix, true), read).GetTransparentProxy();
            object Configuration(string name)
            {
                var cfg = Mock("Configuration", m => m.MethodName == "get_Name" ? name : throw new Exception(m.MethodName));
                return Mock("ConfigurationManager", m => m.MethodName == "get_ActiveConfiguration" ? cfg : throw new Exception(m.MethodName));
            }
            var historyCase = mode == "suppressed-history" || mode == "visible-history" || mode == "invalid-suppressed-history";
            var invalid = mode.StartsWith("invalid-", StringComparison.Ordinal) && mode != "invalid-root-component";
            var rootScans = 0;
            var native = new List<object>();
            var placeholders = new List<object>();
            object Model(string path, bool assembly, bool root = false)
            {
                Func<IMethodCallMessage, object> read = m => {
                    if (m.MethodName == "GetType" && (m.MethodBase as MethodInfo)?.ReturnType == typeof(Type)) return modelType;
                    switch (m.MethodName)
                    {
                        case "GetPathName": return path;
                        case "GetTitle": return Path.GetFileName(path);
                        case "GetType": return assembly ? 2 : 1;
                        case "get_ConfigurationManager": return Configuration(root && mode == "other-root-config" ? "Alternative" : "Default");
                        case "GetDesignTable": case "get_Extension": case "GetEquationMgr": return null;
                        case "GetComponents":
                            TreeCheck(!(bool)m.Args[0], "实例查找须读取完整根的所有层级。");
                            if (root) { rootScans++; return native.ToArray(); }
                            return placeholders.ToArray();
                        default: throw new Exception("原生模型被意外修改或切换：" + m.MethodName);
                    }
                };
                return assembly ? new AssemblyProxy(modelType, read, root && mode == "view-only-root").GetTransparentProxy()
                    : new Proxy(modelType, read).GetTransparentProxy();
            }
            object Occurrence(string name, string path, object loaded, string configuration, bool suppressed, int? state = null, object parent = null) =>
                Mock("Component2", m => {
                    switch (m.MethodName)
                    {
                        case "get_Name2": return name;
                        case "GetPathName": return path;
                        case "GetModelDoc2": return loaded;
                        case "get_ReferencedConfiguration": return configuration;
                        case "IsSuppressed": return suppressed || state == 1 || state == 4;
                        case "GetSuppression2": return state ?? (suppressed ? 0 : 2);
                        case "GetParent": return parent;
                        case "get_IsVirtual": return false;
                        default: throw new Exception("意外读取组件：" + m.MethodName);
                    }
                });
            var localOwner = Model(ownerPath, true);
            var viewOwner = mode == "uncontrolled-view" ? Model(Path.Combine(folder, "outside", "F1.SLDASM"), true) : localOwner;
            var rootModel = Model(rootPath, true, true);
            const string relative = "F1.05-1/Bracket-1/Sensor-1";
            var parentHandle = mode == "invalid-with-parent" ? Occurrence("F1.05-1", ownerPath, localOwner, "Default", false) : null;
            int? placeholderState = invalid || mode == "stale-handle-known-config" ? -1 : mode == "id-mismatch-placeholder" ? 5 : (int?)null;
            placeholders.Add(Occurrence(relative, "", null, invalid || mode == "valid-empty-config" ? "" : "默认",
                mode == "suppressed-history", placeholderState, parentHandle));
            var second = mode.StartsWith("two-", StringComparison.Ordinal) || mode == "invalid-two-configurations" || mode == "different-intermediate-config";
            foreach (var owner in second ? new[] { "F1-1", "F1-2" } : new[] { "F1-1" })
            {
                var lightweightOwner = mode == "invalid-lightweight-owner";
                var unresolvedOwner = mode == "unresolved-owner";
                native.Add(Occurrence(owner, ownerPath, lightweightOwner || unresolvedOwner ? null : localOwner,
                    mode == "other-owner-config" ? "Alternative" : "Default", mode == "suppressed-owner",
                    lightweightOwner ? (int?)1 : unresolvedOwner ? -1 : mode == "id-mismatch-owner" ? 5 : (int?)null));
                native.Add(Occurrence(owner + "/F1.05-1", stationPath, null, "Default", false, 1));
                var bracketSource = mode == "external-intermediate" ? Path.Combine(folder, "outside", "Bracket.SLDASM") : bracketPath;
                var bracket = Occurrence(owner + "/F1.05-1/Bracket-1", bracketSource, null,
                    mode == "different-intermediate-config" && owner == "F1-2" ? "Other" : "Default", false,
                    mode == "invalid-intermediate" ? -1 : mode == "id-mismatch-intermediate" ? 5 : 1);
                if (mode != "missing-intermediate") native.Add(bracket);
                if (mode == "duplicate-intermediate") native.Add(bracket);
                var source = historyCase ? history : mode.StartsWith("foreign-", StringComparison.Ordinal) ||
                    mode == "two-conflicting-owners" && owner == "F1-2" ? foreign : partPath;
                var lightweightLeaf = mode == "invalid-lightweight-leaf" || mode == "invalid-fully-lightweight-leaf";
                var loaded = historyCase || mode == "deferred" || mode == "foreign-deferred" || lightweightLeaf ? null : Model(source, false);
                if (mode == "missing-instance") continue;
                var name = owner + "/" + (mode == "wrong-parent" ? "F1.05-2/Bracket-1/Sensor-1" : relative);
                var configuration = mode == "other-child-config" || mode == "invalid-two-configurations" && owner == "F1-2" ? "Alternative" : "默认";
                var element = Occurrence(name, source, loaded, configuration, historyCase,
                    mode == "invalid-root-component" ? (int?)-1 : mode == "id-mismatch-leaf" ? 5
                        : lightweightLeaf ? mode == "invalid-fully-lightweight-leaf" ? 4 : 1 : (int?)null);
                native.Add(element);
                if (mode == "duplicate-instance") native.Add(element);
            }
            object View(string configuration) => new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), m =>
                m.MethodName == "get_ReferencedDocument" ? viewOwner :
                m.MethodName == "get_ReferencedConfiguration" ? configuration :
                throw new Exception("意外读取视图：" + m.MethodName)).GetTransparentProxy();
            var views = new List<object> { null, View(mode == "other-view-config" ? "Alternative" : "Default") };
            if (mode == "two-valid-views" || mode == "second-view-config") views.Add(View(mode == "second-view-config" ? "Alternative" : "Default"));
            var drawing = new SheetReferenceModelProxy(modelType, true, m => {
                switch (m.MethodName)
                {
                    case "GetType": return 3;
                    case "GetPathName": return drawingPath;
                    case "GetViews": return new[] { views.ToArray() };
                    case "get_Extension": case "GetEquationMgr": case "GetDesignTable": return null;
                    default: throw new Exception(m.MethodName);
                }
            }).GetTransparentProxy();
            var app = new Proxy(appType, m => m.MethodName == "GetOpenDocumentByName" && (string)m.Args[0] == rootPath
                ? rootModel : throw new Exception("实例校验不能打开文件、改变配置或查找其他窗口：" + m.MethodName)).GetTransparentProxy();
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception failure = null;
            try
            {
                using (var scope = (IDisposable)Type("ControlledWorkingCopyScope").GetConstructors(Flags).Single()
                    .Invoke(new object[] { drawingPath, manifest }))
                    TreeCallStatic("PdmAddin", "ValidateControlledLoadedSources", drawing, drawingPath, manifest, app, missing, true);
            }
            catch (TargetInvocationException error) { failure = error.InnerException; }
            TreeCheck(success.Contains(mode) ? failure == null : failure is InvalidOperationException,
                mode + "：实际工程图来源判定异常。 " + failure);
            TreeCheck(mode == "suppressed-history" || mode == "invalid-suppressed-history" ? missing.SetEquals(new[] { history }) : missing.Count == 0,
                mode + "：历史缺件提示不符。");
            TreeCheck(rootScans <= 1, mode + "：每个工程图视图只需读取一次当前总装。");
            Console.WriteLine("PASS: drawing occurrence source " + mode);
        }
    }
}
