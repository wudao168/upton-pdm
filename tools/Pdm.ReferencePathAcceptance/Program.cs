using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly assembly;
    static int passed;
    static int Main(string[] args)
    {
        var dll = Path.GetFullPath(args[0]);
        var folder = Path.GetFullPath(args[1]);
        if (Directory.Exists(folder)) throw new Exception("Fixture directory already exists");
        Directory.CreateDirectory(folder);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = Path.Combine(Path.GetDirectoryName(dll), new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        assembly = Assembly.LoadFrom(dll);
        if (args.Length > 2 && args[2] == "--isolation") return ArchiveIsolationRegressions(folder);
        try
        {
            var rootPath = Path.Combine(folder, "S700130.00-00.SLDASM");
            File.WriteAllText(rootPath, "unchanged assembly bytes");
            var partPath = Path.Combine(folder, "LM30UU-2021122303492761.SLDPRT");
            File.WriteAllText(partPath, "unchanged part bytes");
            foreach (var valid in new[] { rootPath, @"C:\Users\bai.hm.UPTON-DOMAIN\Desktop\新建文件夹 (3)\S700130.00-00.SLDASM", @"\\server\共享目录\零件 (1)&#%.SLDPRT" })
                Require(Resolve(valid, folder) == valid, "valid Chinese/space/UNC path changed");
            Require(Resolve(@"子目录\组件.SLDASM", folder) == Path.Combine(folder, @"子目录\组件.SLDASM"), "relative reference did not use owning directory");
            Pass("valid Chinese, spaces, parentheses, UNC and relative paths preserved");
            foreach (var character in new[] { '"', '<', '>', '|', '\0', '\t', '\r', '\n' })
            {
                var invalid = folder + "\\坏" + character + "引用.SLDPRT";
                var error = ExpectPathFailure(() => Resolve(invalid, folder));
                Require(error.Message.Contains("U+" + ((int)character).ToString("X4")) && error.Message.Contains("第" + (invalid.IndexOf(character) + 1) + "个字符")
                    && error.Message.Contains("原始引用：") && error.InnerException != null, "missing character position or original exception");
                Require(error.Message.Contains("组件实例：ROOT/PART-1") && error.Message.Contains("引用配置：CFG-A"), "missing instance/configuration");
                if (char.IsControl(character)) Require(error.Message.Contains("\\u" + ((int)character).ToString("X4")), "control character was not escaped");
                Pass("invalid character identified without silently removing it: U+" + ((int)character).ToString("X4"));
            }
            ExpectPathFailure(() => Resolve("", folder)); Pass("empty reference blocks submission");
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            object activeNative = null;
            var app = new Proxy(appType, call => call.MethodName == "get_ActiveDoc" || call.MethodName == "ActivateDoc3" ? activeNative : throw new Exception("Unexpected native mutation " + call.MethodName)).GetTransparentProxy();
            var scanner = New("SolidWorksReferenceTreeScanner", app);
            SetField(scanner,"strictStructure",false);SetField(scanner,"scannedDocumentPath",rootPath);SetField(scanner,"activeDocumentDirectory",folder);
            var displayError=ExpectPathFailure(()=>Invoke(scanner,"ReadComponentFilePath",folder+"\\坏|显示引用.SLDPRT","普通设计树读取","ROOT/PART-1","CFG-A"));
            Require(displayError.Message.Contains("普通设计树读取")&&displayError.Message.Contains("ROOT/PART-1"),"display scan swallowed the original invalid reference");
            Pass("ordinary tree scan preserves exact bad reference instead of returning empty");
            var unsavedModel=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                switch(call.MethodName){case "GetPathName":return "";case "GetTitle":return "未保存零件";case "GetType":return 1;
                    case "get_ConfigurationManager":return null;case "GetSaveFlag":return true;default:throw new Exception("Unexpected unsaved read "+call.MethodName);}
            }).GetTransparentProxy();
            var unsaved=Invoke(scanner,"ScanDocument",unsavedModel,false);
            Require((string)Get(unsaved,"FullPath")==string.Empty,"unsaved document acquired an invented disk path");
            Pass("unsaved display document does not pass empty path into GetDirectoryName");
            SetField(scanner, "strictStructure", true); SetField(scanner, "scannedDocumentPath", rootPath); SetField(scanner, "activeDocumentDirectory", folder);
            foreach (var malformedLoaded in new[] { false, true })
            foreach (var unavailableConfiguration in new[] { false, true })
            {
                var bad = folder + "\\坏|零件.SLDPRT";
                var loaded = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "GetPathName" ? bad : throw new Exception(call.MethodName)).GetTransparentProxy();
                var component = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call => {
                    switch (call.MethodName) { case "GetPathName": return malformedLoaded ? partPath : bad; case "get_Name2": return "ROOT/PART-1"; case "get_ReferencedConfiguration": return unavailableConfiguration ? throw new InvalidOperationException("Configuration not loaded") : "CFG-A"; case "GetModelDoc2": return loaded; default: throw new Exception(call.MethodName); }
                }).GetTransparentProxy();
                var error = ExpectPathFailure(() => Invoke(scanner, "ResolveComponentPath", component));
                Require(error.Message.Contains(malformedLoaded ? "实际加载路径" : "原始引用"), "wrong component path source");
                if (unavailableConfiguration) Require(error.Message.Contains("未推断配置"), "unavailable diagnostic configuration masked the bad reference");
                Pass("strict component path blocks invalid " + (malformedLoaded ? "loaded path instead of falling back" : "saved reference instead of returning empty"));
            }
            var root = Node(rootPath, "Assembly"); var part = Node(partPath, "Part");
            ((IList)Get(root, "Children")).Add(part); Set(part, "SourceReferencePath", folder + "\\坏|引用.SLDPRT");
            InvokeStatic("NativeArchiveManifest", "Validate", root, new[] { rootPath, partPath });
            Require(((IList)Get(root, "Children")).Count == 1, "historical alias changed actual tree scope");
            Pass("historical alias is not used to validate an already resolved real tree file");
            Set(part, "SourceReferencePath", partPath);
            foreach (var raw in new[] { folder + "\\坏\t引用.SLDPRT", "" })
            {
                var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call => {
                    if (call.MethodName == "set_IncludeDrawings" || call.MethodName == "set_IncludeSuppressed") return null;
                    if (call.MethodName == "GetDocumentNames") { var output = (object[])call.Args.Clone(); output[0] = new[] { rootPath, raw, partPath }; return new ReturnMessage(true, output, output.Length, call.LogicalCallContext, call); }
                    throw new Exception("Unexpected PackAndGo write: " + call.MethodName);
                }).GetTransparentProxy();
                var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => call.MethodName == "GetPackAndGo" ? pack : throw new Exception(call.MethodName)).GetTransparentProxy();
                var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "get_Extension" ? extension : call.MethodName == "GetPathName" ? rootPath : call.MethodName == "GetTitle" ? Path.GetFileName(rootPath) : throw new Exception(call.MethodName)).GetTransparentProxy();
                activeNative = model;
                var outsideFiles=(System.Collections.Generic.IReadOnlyList<string>)Invoke(scanner,"AddNativeAssociatedDrawings",model,root);
                var outside=(System.Collections.Generic.IReadOnlyList<string>)InvokeStatic("NativeArchiveManifest","Validate",root,outsideFiles);
                Require(outside.Count==1 && ((IList)Get(root,"Children")).Count==1,"outside malformed path entered or blocked the tree");
                Pass("outside path retained as diagnostic without blocking tree-only archive");
            }
            var drawing = Path.Combine(folder, "总装图.SLDDRW"); File.WriteAllText(drawing, "drawing bytes");
            ImportedReferences(folder, rootPath, appType, interop);
            ImportedInventoryContext(folder, rootPath, appType, interop);
            DependencyOrigins(folder, rootPath, partPath, appType);
            ContextFileClosure(folder, rootPath, partPath, appType);
            BrokenContextEvidence(folder, partPath, appType, interop);
            TreeScopeCompleteness(folder, rootPath, partPath);
            DrawingCopyPriority(folder, rootPath, partPath, appType);
            OutsideAliases(folder, rootPath, partPath);
            CandidateDrawingScope(folder, rootPath, partPath, appType, interop);
            DrawingVersionScope(folder, partPath);
            var cwd = "original working directory";
            var drawingReference = folder + "\\坏|工程图模型.SLDPRT";
            app = new Proxy(appType, call => {
                switch (call.MethodName) { case "GetCurrentWorkingDirectory": return cwd; case "SetCurrentWorkingDirectory": cwd = (string)call.Args[0]; return true;
                    case "GetDocumentDependencies2": return new[] { "PART", drawingReference }; default: throw new Exception("Unexpected write " + call.MethodName); }
            }).GetTransparentProxy();
            scanner = New("SolidWorksReferenceTreeScanner", app); SetField(scanner, "strictStructure", true);
            ExpectInvalid(() => Invoke(scanner, "AddRelatedDrawingPaths", root, new[] { drawing }, true), "未能对应到树内模型");
            Require(cwd == "original working directory", "drawing working directory changed");
            Pass("known drawing with no verifiable tree model still blocks and restores working directory");
            drawingReference = "";
            ExpectInvalid(() => Invoke(scanner, "AddRelatedDrawingPaths", root, new[] { drawing }, true), "未能对应到树内模型");
            Require(cwd == "original working directory", "empty drawing reference changed working directory");
            Pass("empty drawing model reference blocks whole submission without skipping the file");
            var report = (string)InvokeStatic("PluginDiagnosticLog", "BuildReport", "QA", folder);
            Require(report.Contains("路径诊断") && report.Contains("U+0000") && report.Contains("\\u0000") && report.Contains("结构内原生引用"), "exported report lost raw path diagnostics");
            Require(File.ReadAllText(rootPath) == "unchanged assembly bytes" && File.ReadAllText(partPath) == "unchanged part bytes", "source bytes changed");
            File.WriteAllText(Path.Combine(folder, "diagnostic-report.txt"), report);
            Pass("exported diagnostic report retains escaped paths and unchanged source bytes");
            Console.WriteLine("PASS total: " + passed); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    static void OutsideAliases(string folder, string rootPath, string partPath)
    {
        var root = Node(rootPath, "Assembly"); var part = Node(partPath, "Part");
        ((IList)Get(root, "Children")).Add(part);
        var old = Path.Combine(folder, "old-part.SLDPRT");
        Set(part, "SourceReferencePath", old);
        foreach (var mode in new[] { "missing", "different", "locked" })
        {
            if (mode != "missing") File.WriteAllText(old, "different outside bytes");
            using (var hold = mode == "locked" ? new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            {
                var outside = (System.Collections.Generic.IReadOnlyList<string>)InvokeStatic("NativeArchiveManifest", "Validate", root, new[] { rootPath, old, partPath });
                Require(outside.SequenceEqual(new[] { old }) && ((IList)Get(root, "Children")).Count == 1, "old alias blocked, expanded or replaced the real tree");
            }
            Pass("outside saved alias cannot affect tree archive: " + mode);
        }
        Set(part, "FullPath", Path.Combine(folder, "missing-real-part.SLDPRT"));
        ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "Validate", root, new[] { rootPath, old }), "树内图档缺失");
        Pass("existing old alias cannot substitute a missing actual tree file");
    }

    static void CandidateDrawingScope(string folder, string rootPath, string partPath, Type appType, Assembly interop)
    {
        var drawing = Path.Combine(folder, "candidate.SLDDRW"); File.WriteAllText(drawing, "unchanged candidate bytes");
        foreach (var mode in new[] { "unreadable", "outside-invalid", "outside-readable", "related", "related-outside-invalid", "related-partial", "known-unreadable", "known-missing" })
        {
            var root = Node(rootPath, "Assembly"); var part = Node(partPath, "Part");
            ((IList)Get(root, "Children")).Add(part);
            if (mode.StartsWith("known")) ((IList)Get(part, "Children")).Add(Node(drawing, "Drawing"));
            object activeNative = null;
            var app = new Proxy(appType, call => {
                if (call.MethodName == "get_ActiveDoc" || call.MethodName == "ActivateDoc3") return activeNative;
                if (call.MethodName == "GetCurrentWorkingDirectory") return folder;
                if (call.MethodName == "SetCurrentWorkingDirectory") return true;
                if (call.MethodName != "GetDocumentDependencies2") throw new Exception("Unexpected native mutation: " + call.MethodName);
                if (mode.EndsWith("unreadable") || mode == "related-partial" && (bool)call.Args[2]) throw new IOException("simulated read failure");
                if (mode.StartsWith("related")) return mode == "related-outside-invalid"
                    ? new[] { "PART", partPath, "OUTSIDE", folder + "\\bad|outside.SLDPRT" } : new[] { "PART", partPath };
                return new[] { "OUTSIDE", mode == "outside-invalid" ? folder + "\\bad|outside.SLDPRT" : Path.Combine(folder, "not-in-tree.SLDPRT") };
            }).GetTransparentProxy();
            var scanner = New("SolidWorksReferenceTreeScanner", app);
            var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call => {
                if (call.MethodName == "set_IncludeDrawings" || call.MethodName == "set_IncludeSuppressed") return null;
                if (call.MethodName == "GetDocumentNames") { var output = (object[])call.Args.Clone(); output[0] = new[] { rootPath, partPath, drawing }; return new ReturnMessage(true, output, output.Length, call.LogicalCallContext, call); }
                throw new Exception(call.MethodName);
            }).GetTransparentProxy();
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => call.MethodName == "GetPackAndGo" ? pack : throw new Exception(call.MethodName)).GetTransparentProxy();
            var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "get_Extension" ? extension : call.MethodName == "GetPathName" ? rootPath : call.MethodName == "GetTitle" ? Path.GetFileName(rootPath) : throw new Exception(call.MethodName)).GetTransparentProxy();
                activeNative = model;
            if (mode == "known-missing") File.Delete(drawing);
            if (mode == "related-partial" || mode == "known-unreadable")
                ExpectInvalid(() => Invoke(scanner, "AddNativeAssociatedDrawings", model, root), "引用未完整读取");
            else if (mode == "known-missing")
                ExpectInvalid(() => Invoke(scanner, "AddNativeAssociatedDrawings", model, root), "工程图不存在");
            else
            {
                Invoke(scanner, "AddNativeAssociatedDrawings", model, root);
                Require(((IList)Get(part, "Children")).Count == (mode.StartsWith("related") ? 1 : 0), "drawing discovery lost a real drawing or included an unrelated candidate");
            }
            if (mode == "known-missing") File.WriteAllText(drawing, "unchanged candidate bytes");
            Require(File.ReadAllText(drawing) == "unchanged candidate bytes", "discovery altered candidate bytes");
            Pass("native candidate discovery and required drawing integrity: " + mode);
        }
    }

    static void DrawingVersionScope(string folder, string partPath)
    {
        var current = Node(Path.Combine(folder, "scoped-drawing.SLDDRW"), "Drawing");
        Set(current, "DocumentId", Guid.NewGuid());
        var part = Node(partPath, "Part"); Set(part, "DocumentId", Guid.NewGuid()); Set(part, "CurrentRevision", "W1");
        ((IList)Get(current, "ReferencedModels")).Add(part);
        Set(current, "DrawingScopePaths", new System.Collections.Generic.HashSet<string>(new[] { partPath }, StringComparer.OrdinalIgnoreCase));
        var archived = New("DocumentReferenceNodeDto");
        Set(archived, "Children", Activator.CreateInstance(archived.GetType().GetProperty("Children").PropertyType));
        Set(archived, "DocumentId", Get(current, "DocumentId")); Set(archived, "FileName", Get(current, "FileName")); Set(archived, "Kind", (int)Get(current, "Kind"));
        var archivedPart = New("DocumentReferenceNodeDto"); Set(archivedPart, "DocumentId", Get(part, "DocumentId")); Set(archivedPart, "Configuration", Get(part, "Configuration"));
        var revision = New("RevisionDto"); Set(revision, "Display", "W1"); Set(archivedPart, "Revision", revision);
        ((IList)Get(archived, "Children")).Add(archivedPart);
        var outside = New("DocumentReferenceNodeDto"); Set(outside, "DocumentId", Guid.NewGuid()); ((IList)Get(archived, "Children")).Add(outside);
        var compare = Type("PdmAddin").GetMethods(Flags).Single(method => method.Name == "ReferenceSnapshotMatchesTree" && method.GetParameters().Length == 2);
        Func<bool> matches = () => (bool)compare.Invoke(null, new[] { archived, current });
        Require(matches(), "old outside drawing model triggered a change");
        Set(revision, "Display", "W2"); Require(!matches(), "actual tree model revision change was ignored");
        Set(revision, "Display", "W1"); Set(archivedPart, "Configuration", "changed"); Require(!matches(), "actual model configuration change was ignored");
        Set(archivedPart, "Configuration", Get(part, "Configuration"));
        Set(current, "DrawingScopePaths", null); Require(!matches(), "standalone drawing lost one of its required model references");
        Require(((IList)Get(archived, "Children")).Count == 2, "historical snapshot was rewritten");
        Pass("drawing version comparison ignores outside history only within explicit tree scope, preserving real changes and standalone references");
    }
    static void ImportedReferences(string folder, string rootPath, Type appType, Assembly interop)
    {
        foreach (var name in new[] { "CDL01-150-P-002", "CDL21-150-P-002" })
        {
            var native = @"C:\Users\bai.hm.UPTON-DOMAIN\AppData\Local\Temp\swx12192\IC~~\" + name + ".stp.SLDPRT";
            var source = @"C:\Users\bai.hm.UPTON-DOMAIN\Desktop\YIHEDA_" + name + "\\源文件.stp";
            // Decode the exact business-client format without needing that client's disk.
            var parts = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", Node(rootPath, "Assembly"), new[] { native + "|" + source });
            Require(parts.SequenceEqual(new[] { native, source }), "import composite lost native or source path");
            Pass("business STEP composite retains both paths: " + name);
        }
        var cacheFolder = Path.Combine(Path.GetTempPath(), "swx-pdm-import-" + Guid.NewGuid().ToString("N"), "IC~~");
        Directory.CreateDirectory(cacheFolder);
        var cache = Path.Combine(cacheFolder, "CDL21-150-P-002.stp.SLDPRT");
        var sourcePath = Path.Combine(folder, "原始STEP.stp");
        File.WriteAllText(cache, "native import cache"); File.WriteAllText(sourcePath, "exact original STEP bytes");
        try
        {
            var app = new Proxy(appType, call => call.MethodName == "get_ActiveDoc" ? null : call.MethodName == "GetDocuments" ? new object[0] : throw new Exception("Unexpected native write " + call.MethodName)).GetTransparentProxy();
            var scanner = New("SolidWorksReferenceTreeScanner", app);
            SetField(scanner, "strictStructure", true); SetField(scanner, "activeDocumentDirectory", folder); SetField(scanner, "scannedDocumentPath", rootPath);
            var component = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call => {
                switch (call.MethodName) {
                    case "GetPathName": return cache; case "GetImportedPath": return sourcePath;
                    case "get_Name2": return "STEP-1"; case "get_ReferencedConfiguration": return "Default";
                    case "get_IsVirtual": case "get_IsSpeedPak": case "IsHidden": return false;
                    case "GetSuppression2": return 2; case "GetModelDoc2": return null; case "GetChildren": return new object[0];
                    default: throw new Exception("Unexpected component mutation " + call.MethodName);
                }
            }).GetTransparentProxy();
            var imported = Invoke(scanner, "BuildComponent", component, "ROOT/STEP-1", new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase), false, null, false);
            Require(Get(imported, "Status").ToString() != "Virtual" && (string)Get(imported, "ImportedSourcePath") == sourcePath, "IC cache was treated as independent native drawing");
            Pass("scanner verifies IC cache with GetImportedPath without saving or breaking link");
            var root = Node(rootPath, "Assembly"); ((IList)Get(root, "Children")).Add(imported);
            var files = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { rootPath, cache + "|" + sourcePath });
            Require(files.Length == 3, "composite was dropped");
            ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "Validate", root, files), "树内导入组件");
            File.Delete(sourcePath);
            ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "Validate", root, files), "树内导入组件");
            Require(((IList)Get(root,"Children")).Count==1,"outside STEP was registered or cache was silently omitted");
            ((IList)Get(root,"Children")).Clear();
            var outside=(System.Collections.Generic.IReadOnlyList<string>)InvokeStatic("NativeArchiveManifest","Validate",root,files);
            Require(outside.Count==2 && ((IList)Get(root,"Children")).Count==0,"outside STEP/cache blocked or expanded real tree");
            File.WriteAllText(sourcePath, "exact original STEP bytes");
            Pass("IC cache cannot masquerade as embedded; outside STEP existence does not control tree archive");
            foreach (var malformed in new[] { cache + "|", cache + "|" + sourcePath + "|extra", cache + "|" + folder + "\\坏\t源.stp" })
            {
                var historical = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { malformed });
                InvokeStatic("NativeArchiveManifest", "Validate", root, historical);
                Require(historical.Length > 0, "historical diagnostic was silently discarded");
            }
            Pass("malformed historical inventory entries are diagnosed without blocking the valid current tree");
            Require((bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", rootPath, cache, sourcePath, new[] { rootPath }), "native container inventory did not establish storage boundary");
            Require(!(bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", rootPath, cache, sourcePath, new[] { rootPath, cache }), "independent cache was treated as contained");
            Require((bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", rootPath, cache, sourcePath, new[] { rootPath, sourcePath }), "outside STEP record changed verified native storage ownership");
            Require(!(bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", rootPath, cache, sourcePath, new[] { sourcePath }), "missing owner was accepted");
            Require(!(bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", rootPath, cache, sourcePath, new[] { sourcePath, rootPath }), "non-native inventory root was accepted");
            var nestedOwner = Path.Combine(folder, "导入组件所属装配.SLDASM"); File.WriteAllText(nestedOwner, "persisted nested import");
            Require((bool)InvokeStatic("ImportedComponentStorage", "InventoryOwns", nestedOwner, cache, sourcePath, new[] { rootPath, nestedOwner }), "verified whole-tree inventory did not retain the nearest native owner");
            Set(imported, "Status", Enum.Parse(Get(imported,"Status").GetType(), "Virtual"));
            Set(imported, "ImportedStorageOwnerPath", rootPath);
            ((IList)Get(root,"Children")).Add(imported);
            InvokeStatic("NativeArchiveManifest", "Validate", root, new[] { rootPath });
            InvokeStatic("PdmApiClient", "ValidateArchiveReferenceCompleteness", root);
            var items=(IEnumerable)InvokeStatic("PdmAddin","BuildBatchOperationItems",root);
            Require(items.Cast<object>().Count()==1,"stored imports became independently uploaded files");
            Set(imported,"IsModifiedInSolidWorks",true);
            ExpectInvalid(()=>InvokeStatic("PdmApiClient","ValidateArchiveReferenceCompleteness",root),"未保存");
            Set(imported,"IsModifiedInSolidWorks",false);
            Set(imported,"ImportedStorageOwnerPath",Path.Combine(folder,"missing-owner.SLDASM"));
            ExpectInvalid(()=>InvokeStatic("PdmApiClient","ValidateArchiveReferenceCompleteness",root),"树内导入组件");
            ((IList)Get(root,"Children")).Clear();
            Pass("native container owns import hierarchy; dirty or missing owners never bypass archive protection");
            Require(File.ReadAllText(cache) == "native import cache" && File.ReadAllText(sourcePath) == "exact original STEP bytes", "source bytes changed");
            Require(!(bool)InvokeStatic("NativeArchiveManifest", "IsInterconnectCachePath", Path.Combine(folder, "IC~~", "ordinary.SLDPRT")), "ordinary directory classified as temp cache");
            Pass("native and STEP bytes preserved; ordinary IC-named directories remain real files");
            File.SetAttributes(sourcePath, FileAttributes.ReadOnly);
            Require(InvokeStatic("NativeArchiveManifest", "ProbeImportedSource", sourcePath) == null, "read-only source was treated as unreadable");
            Require((File.GetAttributes(sourcePath) & FileAttributes.ReadOnly) != 0, "probe changed read-only attribute");
            File.SetAttributes(sourcePath, FileAttributes.Normal);
            Pass("read-only STEP is readable and probing preserves its attributes");
            using (var locked = new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(((string)InvokeStatic("NativeArchiveManifest", "ProbeImportedSource", sourcePath)).Contains("独占"), "locked file was misreported as missing");
            Pass("exclusive lock is diagnosed separately from missing source");
            File.WriteAllText(sourcePath, "");
            Require((string)InvokeStatic("NativeArchiveManifest", "ProbeImportedSource", sourcePath) == "文件为空", "empty source was accepted");
            InvokeStatic("NativeArchiveManifest", "Validate", root, files);
            Require(((IList)Get(root,"Children")).Count == 0, "source probe changed tree-only archive structure");
            File.WriteAllText(sourcePath, "exact original STEP bytes");
            Pass("empty outside STEP does not change or block tree-only scope");
            foreach (var error in new Exception[] { new UnauthorizedAccessException("raw permission denied"), new System.Security.SecurityException("raw security failure"), new DirectoryNotFoundException("raw directory failure"), new IOException("raw network failure", unchecked((int)0x80070035)), new IOException("raw IO failure") })
            {
                var reason = (string)InvokeStatic("NativeArchiveManifest", "SourceReadFailureReason", error);
                Require(!reason.Contains("raw") && !reason.Contains("已删除") && !reason.Contains("找不到文件"), "unreadable source misreported as missing or leaked raw English error");
            }
            Pass("permission, directory, network and IO causes have distinct Chinese diagnostics");
        }
        finally { File.Delete(cache); Directory.Delete(cacheFolder); Directory.Delete(Path.GetDirectoryName(cacheFolder)); }
    }
    static void DrawingCopyPriority(string folder, string rootPath, string partPath, Type appType)
    {
        var root=Node(rootPath,"Assembly");var part=Node(partPath,"Part");((IList)Get(root,"Children")).Add(part);
        var drawing=Path.ChangeExtension(partPath,".SLDDRW");File.WriteAllText(drawing,"current drawing bytes");
        ((IList)Get(part,"Children")).Add(Node(drawing,"Drawing"));
        var oldDirectory=Path.Combine(folder,"旧目录");Directory.CreateDirectory(oldDirectory);
        var old=Path.Combine(oldDirectory,Path.GetFileName(drawing));File.WriteAllText(old,"different old drawing bytes");
        var noRead=new Proxy(appType,call=>throw new Exception("Outside copy was read: "+call.MethodName)).GetTransparentProxy();
        using(var locked=new FileStream(old,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Invoke(New("SolidWorksReferenceTreeScanner",noRead),"AddRelatedDrawingPaths",root,new[]{old},true);
        Require(((IList)Get(part,"Children")).Count==1 && File.ReadAllText(drawing)=="current drawing bytes","old copy replaced current drawing");
        var peerPath=Path.Combine(oldDirectory,"ANOTHER.SLDPRT");File.WriteAllText(peerPath,"another real model");
        var peer=Node(peerPath,"Part");((IList)Get(root,"Children")).Add(peer);
        var app=new Proxy(appType,call=>{
            if(call.MethodName=="GetCurrentWorkingDirectory")return folder;
            if(call.MethodName=="SetCurrentWorkingDirectory")return true;
            if(call.MethodName=="GetDocumentDependencies2")return new[]{"ANOTHER",peerPath};
            throw new Exception("Unexpected native mutation: "+call.MethodName);
        }).GetTransparentProxy();
        Invoke(New("SolidWorksReferenceTreeScanner",app),"AddRelatedDrawingPaths",root,new[]{old},true);
        Require(((IList)Get(peer,"Children")).Count==1,"same filename beside another actual model was silently discarded");
        Pass("locked old drawing copy cannot replace current drawing; actual peer model drawing is retained");
    }

    static void TreeScopeCompleteness(string folder, string rootPath, string partPath)
    {
        foreach (var exists in new[] { false, true })
        {
            var root = Node(rootPath, "Assembly"); ((IList)Get(root, "Children")).Add(Node(partPath, "Part"));
            var outside = new[] { Path.Combine(folder,"外部总装.SLDASM"), Path.Combine(folder,"树外零件.SLDPRT"), Path.Combine(folder,"外部源.step") };
            foreach (var file in outside) { if (exists) File.WriteAllText(file,"outside content"); else if (File.Exists(file)) File.Delete(file); }
            var result = (System.Collections.Generic.IReadOnlyList<string>)InvokeStatic("NativeArchiveManifest", "Validate", root, new[] {rootPath,partPath}.Concat(outside).ToArray());
            Require(result.Count == 3 && ((IList)Get(root,"Children")).Count == 1,"outside existence changed real tree or archive result");
            Pass("outside assembly, part and STEP do not change tree archive: " + exists);
        }
        foreach (var kind in new[]{"Part","Drawing"})
        {
            var root=Node(rootPath,"Assembly");var missing=Node(Path.Combine(folder,"缺失树内文件."+(kind=="Part"?"SLDPRT":"SLDDRW")),kind);
            ((IList)Get(root,"Children")).Add(missing);
            ExpectInvalid(()=>InvokeStatic("NativeArchiveManifest","Validate",root,new[]{rootPath}),"树内图档缺失");
            Pass("missing in-tree file blocks even when omitted from native manifest: "+kind);
        }
        var legacy=Node(rootPath,"Assembly");var part=Node(partPath,"Part");Set(legacy,"DocumentId",Guid.NewGuid());Set(part,"DocumentId",Guid.NewGuid());
        Set(legacy,"Revision","W1");Set(part,"Revision","W1");Set(part,"CurrentRevision","W1");
        ((IList)Get(legacy,"Children")).Add(part);
        var extra=Node(Path.Combine(folder,"历史外围.SLDASM"),"Assembly");Set(extra,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
        ((IList)Get(legacy,"Children")).Add(extra);
        var selected=(IList)InvokeStatic("PdmAddin","BuildBatchOperationItems",legacy);
        Require(selected.Count==2,"legacy auxiliary entry returned to new submission");
        var capture=InvokeStatic("PdmApiClient","CaptureArchiveSnapshot",legacy,new System.Collections.Generic.HashSet<string>(new[]{rootPath,partPath}));
        var captured=Invoke(capture,"CreateRoot");var json=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(captured);
        Require(!json.Contains("历史外围")&&json.Contains("treeScopeOnly"),"snapshot lost scope policy or retained auxiliary entry");
        Pass("legacy auxiliary history is excluded from new tree-scoped selection and snapshot");
    }

    static void BrokenContextEvidence(string folder, string partPath, Type appType, Assembly interop)
    {
        var target = Path.Combine(folder, "历史总装.SLDASM");
        foreach (var mode in new[] { "broken", "active", "out-of-context", "dangling", "locked", "mixed", "dirty", "multi-config", "wrong-path", "incomplete" })
        {
            var states = new[] { 0, mode == "active" ? 3 : mode == "out-of-context" ? 4 : mode == "dangling" ? 5 : mode == "locked" ? 1 : mode == "mixed" ? 3 : 0 };
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => {
                if (call.MethodName == "ListExternalFileReferencesCount") return 2;
                if (call.MethodName == "ListExternalFileReferences") {
                    var outputs = new object[] { new[] { target, target }, new[] { "", "" }, new[] { "孔", "草图" }, new[] { "面", "点" },
                        mode == "incomplete" ? new[] { 0 } : states, new[] { "面1", "点1" }, new[] { "零件", "零件" }, 0, null };
                    return new ReturnMessage(null, outputs, outputs.Length, call.LogicalCallContext, call);
                }
                throw new Exception("Unexpected feature mutation " + call.MethodName);
            }).GetTransparentProxy();
            var doc = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
                switch (call.MethodName) { case "GetPathName": return mode == "wrong-path" ? partPath + ".other" : partPath;
                    case "GetSaveFlag": return mode == "dirty"; case "GetConfigurationNames": return mode == "multi-config" ? new[] { "A", "B" } : new[] { "A" };
                    case "get_Extension": return extension; default: throw new Exception("Unexpected document mutation " + call.MethodName); }
            }).GetTransparentProxy();
            var app = new Proxy(appType, call => call.MethodName == "GetOpenDocumentByName" ? doc : throw new Exception(call.MethodName)).GetTransparentProxy();
            var verified = (bool)Invoke(New("SolidWorksReferenceTreeScanner", app), "IsVerifiedBrokenContextReference", partPath, target);
            Require(verified == (mode == "broken"), "invalid classification of historical reference: " + mode);
            Pass("historical reference requires complete native broken-state evidence: " + mode);
        }
    }

    static void ContextFileClosure(string folder, string rootPath, string partPath, Type appType)
    {
        var context = Path.Combine(folder, "上下文总装.SLDASM");
        var nestedDirectory = Path.Combine(folder, "其他配置"); Directory.CreateDirectory(nestedDirectory);
        var nested = Path.Combine(nestedDirectory, "外部零件.SLDPRT");
        foreach (var mode in new[] { "success", "missing", "malformed", "read-error", "changed" })
        {
            File.WriteAllText(context, "context bytes"); File.WriteAllText(nested, "nested bytes");
            var root = Node(rootPath, "Assembly"); ((IList)Get(root, "Children")).Add(Node(partPath, "Part"));
            var cwd = folder; var calls = new System.Collections.Generic.Dictionary<string, int>();
            var app = new Proxy(appType, call => {
                if (call.MethodName == "GetCurrentWorkingDirectory") return cwd;
                if (call.MethodName == "SetCurrentWorkingDirectory") { cwd = (string)call.Args[0]; return true; }
                if (call.MethodName == "GetOpenDocumentByName") return null;
                if (call.MethodName != "GetDocumentDependencies2") throw new Exception("Unexpected CAD mutation: " + call.MethodName);
                var owner = (string)call.Args[0]; calls[owner] = calls.ContainsKey(owner) ? calls[owner] + 1 : 1;
                if (owner == rootPath) return new[] { "part", partPath };
                if (owner == partPath) return new[] { "context", context };
                if (owner != context) return null;
                if (mode == "malformed") return new[] { "broken pair" };
                if (mode == "read-error") throw new IOException("native failure");
                if (mode == "changed") File.AppendAllText(context, "changed during read");
                return new[] { "back-reference", rootPath, "nested", mode == "missing" ? nested + ".missing.SLDPRT" : nested };
            }).GetTransparentProxy();
            var scanner = New("SolidWorksReferenceTreeScanner", app);
            try
            {
                var files = (string[])Invoke(scanner, "CompleteNativeFileDependencies", root, new[] { rootPath, partPath, context });
                Require(mode == "success", "incomplete context closure was accepted: " + mode);
                Require(files.Length == 4 && files.Contains(nested) && calls.Values.All(value => value == 1), "context closure missing or duplicate file queries");
                InvokeStatic("NativeArchiveManifest", "Validate", root, files);
                var children = ((IEnumerable)Get(root, "Children")).Cast<object>().ToArray();
                Require(children.Length == 1, "diagnostic closure expanded tree-only archive");
                var addin = New("PdmAddin"); SetField(addin, "application", new Proxy(appType, call => throw new Exception("File dependency opened or rebuilt: " + call.MethodName)).GetTransparentProxy());
                foreach (var child in children.Skip(1))
                    Require(((System.Collections.Generic.IReadOnlyDictionary<string, string>)Invoke(addin, "ReadArchivePreflightProperties", child, null, null)).Count == 0,
                        "file dependency unexpectedly read BOM properties");
            }
            catch (TargetInvocationException error) when (mode != "success" && error.InnerException is IOException)
            { Require(((IList)Get(root, "Children")).Count == 1, "failed closure changed submission tree"); }
            Require(cwd == folder, "context closure changed user working directory");
            Pass("context file closure preserves back-references and blocks incomplete data: " + mode);
        }
    }

    static void ImportedInventoryContext(string folder, string ownerPath, Type appType, Assembly interop)
    {
        var otherPath = Path.Combine(folder, "另一装配.SLDASM");
        File.WriteAllText(otherPath, "other unchanged assembly");
        var constants = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location), "SolidWorks.Interop.swconst.dll"));
        var dontRebuild = (int)Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swRebuildOnActivation_e", true), "swDontRebuildActiveDoc");
        foreach (var mode in new[] { "inactive", "active", "wrong-inventory", "read-error", "activation-error" })
        {
            var calls = 0; var activations = 0; object owner = null, other = null, active = null;
            var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call => {
                if (call.MethodName.StartsWith("set_")) return null;
                if (call.MethodName == "GetDocumentNames")
                {
                    calls++;
                    if (mode == "read-error") throw new IOException("injected inventory failure");
                    var output = (object[])call.Args.Clone();
                    output[0] = active == owner && mode != "wrong-inventory" ? new[] { ownerPath } : new[] { otherPath, ownerPath };
                    return new ReturnMessage(true, output, output.Length, call.LogicalCallContext, call);
                }
                throw new Exception("Unexpected native write " + call.MethodName);
            }).GetTransparentProxy();
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
                call.MethodName == "GetPackAndGo" ? pack : throw new Exception(call.MethodName)).GetTransparentProxy();
            object Document(string path) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
                if (call.MethodName == "GetPathName") return path;
                if (call.MethodName == "GetTitle") return Path.GetFileName(path);
                if (call.MethodName == "get_Extension") return extension;
                throw new Exception("Unexpected save/rebuild " + call.MethodName);
            }).GetTransparentProxy();
            owner = Document(ownerPath); other = Document(otherPath); active = mode == "active" ? owner : other;
            var previous = active;
            var app = new Proxy(appType, call => {
                if (call.MethodName == "get_ActiveDoc") return active;
                if (call.MethodName != "ActivateDoc3") throw new Exception("Unexpected native write " + call.MethodName);
                Require(!(bool)call.Args[1] && (int)call.Args[2] == dontRebuild, "activation rebuilt a document");
                Require((bool)Type("ImportedComponentStorage").GetProperty("IsReadingInventory", Flags).GetValue(null), "temporary activation was not guarded");
                Require((int)Invoke(New("PdmAddin"), "OnActiveDocumentChanged") == 0, "temporary activation scheduled a tree refresh");
                activations++;
                if (mode != "activation-error") active = (string)call.Args[0] == Path.GetFileName(ownerPath) ? owner : other;
                var output = (object[])call.Args.Clone(); output[3] = 0;
                return new ReturnMessage(active, output, output.Length, call.LogicalCallContext, call);
            }).GetTransparentProxy();
            var failed = false;
            try { var files = (string[])Invoke(New("ImportedComponentStorage", app), "ReadInventory", owner); Require(files[0] == ownerPath, "wrong active inventory accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException || error.InnerException is IOException) { failed = true; }
            Require(failed == (mode != "inactive" && mode != "active"), "unexpected inventory outcome " + mode);
            Require(active == previous, "original active document not restored " + mode);
            Require(!(bool)Type("ImportedComponentStorage").GetProperty("IsReadingInventory", Flags).GetValue(null), "inventory guard leaked " + mode);
            Require(activations == (mode == "active" || mode == "activation-error" ? 1 : 2), "unexpected document switches " + mode);
            Require(calls == (mode == "activation-error" ? 0 : 1), "unexpected inventory reads " + mode);
            Require(File.ReadAllText(ownerPath) == "unchanged assembly bytes" && File.ReadAllText(otherPath) == "other unchanged assembly", "CAD bytes changed");
            Pass("import storage inventory uses its owner and restores active document without saving: " + mode);
        }
    }

    static void DependencyOrigins(string folder, string rootPath, string partPath, Type appType)
    {
        var collection = Path.Combine(folder, "四立柱组件集合.SLDASM");
        var other = Path.Combine(folder, "四立柱机构-30(2500N).SLDASM");
        File.WriteAllText(collection, "context assembly bytes"); File.WriteAllText(other, "other context bytes");
        var root = Node(rootPath, "Assembly"); ((IList)Get(root, "Children")).Add(Node(partPath, "Part"));
        foreach (var mode in new[] { "success", "read-error", "malformed" })
        {
            var cwd = "original work directory"; var calls = new System.Collections.Generic.Dictionary<string, int>();
            var app = new Proxy(appType, call => {
                if (call.MethodName == "GetCurrentWorkingDirectory") return cwd;
                if (call.MethodName == "SetCurrentWorkingDirectory") { cwd = (string)call.Args[0]; return true; }
                if (call.MethodName != "GetDocumentDependencies2") throw new Exception("Unexpected native write " + call.MethodName);
                var owner = (string)call.Args[0]; calls[owner] = calls.ContainsKey(owner) ? calls[owner] + 1 : 1;
                if ((bool)call.Args[1] || !(bool)call.Args[2] || (bool)call.Args[3]) throw new Exception("Dependency query flags changed");
                if (owner != partPath) return null;
                if (mode == "read-error") throw new IOException("simulated native read error");
                if (mode == "malformed") return new[] { "incomplete pair" };
                return new[] { "CTX", collection, "OTHER", other, "DUPLICATE", other, "SAME-NAME", Path.Combine(folder, "other-folder", Path.GetFileName(collection)) };
            }).GetTransparentProxy();
            var scanner = New("SolidWorksReferenceTreeScanner", app);
            var origins = (System.Collections.Generic.IReadOnlyList<string>)Invoke(scanner, "TraceUnmatchedAssemblyDependencies", root,
                new[] { rootPath, partPath, collection, other, partPath });
            Require(origins.Count == (mode == "success" ? 2 : 0) && calls.Count == 4 && calls.Values.All(count => count == 1)
                && cwd == "original work directory", "dependency diagnostics lost origin, repeated queries or changed working directory");
            Require(((IList)Get(root, "Children")).Count == 1 && File.ReadAllText(collection) == "context assembly bytes",
                "diagnostics changed assembly structure or bytes");
            Pass("non-component assembly provenance is read-only, exact-path and deduplicated: " + mode);
        }
    }
    static void ExpectInvalid(Action action, string contains) { try { action(); } catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException && e.InnerException.Message.Contains(contains)) { return; } throw new Exception("Expected blocked import: " + contains); }
    sealed class ReadOnlyVersions : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get || !request.RequestUri.AbsolutePath.EndsWith("/versions")) throw new Exception("Unexpected business mutation " + request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
    static object Node(string path, string kind) { var node = New("CadTreeNode"); Set(node, "FullPath", path); Set(node, "FileName", Path.GetFileName(path)); Set(node, "SourceReferencePath", path); Set(node, "InstancePath", "ROOT/PART-1"); Set(node, "Configuration", "CFG-A"); Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), kind)); return node; }
    static string Resolve(string path, string directory) => (string)InvokeStatic("ArchiveReferencePath", "Resolve", path, directory, "读取测试引用", "所属总装.SLDASM", "ROOT/PART-1", "CFG-A");
    static Exception ExpectPathFailure(Action action) { try { action(); } catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "ArchiveReferencePathException") { return e.InnerException; } throw new Exception("Invalid path was accepted or wrong exception escaped"); }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pass(string message) { passed++; Console.WriteLine("PASS " + message); }
    static Type Type(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Flags & ~BindingFlags.Static, null, args, null);
    static object Get(object target, string name) => target.GetType().GetProperty(name, Flags).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
    static void SetField(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    static object InvokeStatic(string type, string name, params object[] args) => Type(type).GetMethod(name, Flags).Invoke(null, args);
    sealed class Proxy : RealProxy
    {
        readonly Func<IMethodCallMessage, object> callback;
        readonly Type proxiedType;
        public Proxy(Type type, Func<IMethodCallMessage, object> callback) : base(type) { this.callback = callback; proxiedType = type; }
        public override IMessage Invoke(IMessage message) { var call = (IMethodCallMessage)message; try { var value = call.MethodName == "GetType" && ((MethodInfo)call.MethodBase).ReturnType == typeof(Type) ? proxiedType : callback(call); return value as IMessage ?? new ReturnMessage(value, call.Args, call.ArgCount, call.LogicalCallContext, call); } catch (Exception error) { return new ReturnMessage(error, call); } }
    }
}
