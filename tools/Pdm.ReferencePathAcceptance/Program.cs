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

internal static class Program
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
            var app = new Proxy(appType, call => { throw new Exception("Unexpected native mutation " + call.MethodName); }).GetTransparentProxy();
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
            var alias = ExpectPathFailure(() => InvokeStatic("NativeArchiveManifest", "Validate", root, new[] { rootPath, partPath }));
            Require(alias.Message.Contains("实例原始引用映射") && ((IList)Get(root, "Children")).Count == 1, "alias failure changed file scope");
            Pass("invalid saved alias blocks scope without dropping or substituting files");
            Set(part, "SourceReferencePath", partPath);
            foreach (var raw in new[] { folder + "\\坏\t引用.SLDPRT", "" })
            {
                var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call => {
                    if (call.MethodName == "set_IncludeDrawings" || call.MethodName == "set_IncludeSuppressed") return null;
                    if (call.MethodName == "GetDocumentNames") { var output = (object[])call.Args.Clone(); output[0] = new[] { rootPath, raw, partPath }; return new ReturnMessage(true, output, output.Length, call.LogicalCallContext, call); }
                    throw new Exception("Unexpected PackAndGo write: " + call.MethodName);
                }).GetTransparentProxy();
                var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => call.MethodName == "GetPackAndGo" ? pack : throw new Exception(call.MethodName)).GetTransparentProxy();
                var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "get_Extension" ? extension : throw new Exception(call.MethodName)).GetTransparentProxy();
                var error = ExpectPathFailure(() => Invoke(scanner, "AddNativeAssociatedDrawings", model, root));
                Require(error.Message.Contains("清单第2项") && ((IList)Get(root, "Children")).Count == 1, "native path context missing or files skipped");
                Pass("PackAndGo entry identified and whole submission blocked: " + (raw.Length == 0 ? "empty" : "control character"));
            }
            var drawing = Path.Combine(folder, "总装图.SLDDRW"); File.WriteAllText(drawing, "drawing bytes");
            ImportedReferences(folder, rootPath, appType, interop);
            var cwd = "original working directory";
            var drawingReference = folder + "\\坏|工程图模型.SLDPRT";
            app = new Proxy(appType, call => {
                switch (call.MethodName) { case "GetCurrentWorkingDirectory": return cwd; case "SetCurrentWorkingDirectory": cwd = (string)call.Args[0]; return true;
                    case "GetDocumentDependencies2": return new[] { "PART", drawingReference }; default: throw new Exception("Unexpected write " + call.MethodName); }
            }).GetTransparentProxy();
            scanner = New("SolidWorksReferenceTreeScanner", app); SetField(scanner, "strictStructure", true);
            var drawingError = ExpectPathFailure(() => Invoke(scanner, "ReadDrawingReferences", drawing));
            Require(drawingError.Message.Contains(drawing) && drawingError.Message.Contains("工程图已保存引用") && cwd == "original working directory", "drawing context lost or current directory changed");
            Pass("drawing dependency failure retains exact owner and restores working directory");
            drawingReference = "";
            drawingError = ExpectPathFailure(() => Invoke(scanner, "ReadDrawingReferences", drawing));
            Require(drawingError.Message.Contains("引用路径为空") && cwd == "original working directory", "empty drawing reference was silently skipped");
            Pass("empty drawing model reference blocks whole submission without skipping the file");
            var report = (string)InvokeStatic("PluginDiagnosticLog", "BuildReport", "QA", folder);
            Require(report.Contains("路径诊断") && report.Contains("U+0000") && report.Contains("\\u0000") && report.Contains("清单第2项"), "exported report lost raw path diagnostics");
            Require(File.ReadAllText(rootPath) == "unchanged assembly bytes" && File.ReadAllText(partPath) == "unchanged part bytes", "source bytes changed");
            File.WriteAllText(Path.Combine(folder, "diagnostic-report.txt"), report);
            Pass("exported diagnostic report retains escaped paths and unchanged source bytes");
            Console.WriteLine("PASS total: " + passed); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
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
            Require(Get(imported, "Status").ToString() == "Virtual" && (string)Get(imported, "ImportedSourcePath") == sourcePath, "IC cache was treated as independent native drawing");
            Pass("scanner verifies IC cache with GetImportedPath without saving or breaking link");
            var root = Node(rootPath, "Assembly"); ((IList)Get(root, "Children")).Add(imported);
            var files = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { rootPath, cache + "|" + sourcePath });
            Require(files.Length == 3, "composite was dropped");
            var external = (System.Collections.Generic.IReadOnlyList<string>)InvokeStatic("NativeArchiveManifest", "Validate", root, files);
            var children = (IList)Get(root, "Children");
            Require(external.Count == 0 && children.Count == 2, "verified STEP source was omitted or treated as optional external data");
            var sourceNode = children.Cast<object>().Single(node => (string)Get(node, "FullPath") == sourcePath);
            Require((bool)Get(sourceNode, "IsImportedSourceFile") && Get(sourceNode, "Role").ToString() == "ExternalFileDependency", "STEP source is missing atomic file-dependency role");
            var items = (IList)InvokeStatic("PdmAddin", "BuildBatchOperationItems", root);
            Require(items.Count == 2, "expected parent plus STEP source, without independently registering cache");
            InvokeStatic("PdmAddin", "ValidateAcquireNode", sourceNode, false);
            var plugin = New("PdmAddin");
            SetField(plugin, "application", app);
            var properties = Invoke(plugin, "ReadArchivePreflightProperties", sourceNode, null, null);
            Require(properties != null, "STEP preflight attempted native import");
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                var http = Type("PdmApiClient").GetField("httpClient", Flags);
                ((HttpClient)http.GetValue(client)).Dispose();
                http.SetValue(client, new HttpClient(new ReadOnlyVersions()) { BaseAddress = new Uri("http://fixture.invalid/") });
                SetField(plugin, "apiClient", client); Set(sourceNode, "DocumentId", Guid.NewGuid());
                var prepare = (Task)Invoke(plugin, "PrepareBatchCheckInFileAsync", sourceNode, CancellationToken.None, null, null, null, null);
                prepare.GetAwaiter().GetResult();
                var prepared = Get(prepare, "Result"); var copy = (string)Get(prepared, "UploadCopyPath");
                try { Require(File.ReadAllText(copy) == File.ReadAllText(sourcePath), "STEP upload copy changed source bytes"); }
                finally { InvokeStatic("PdmAddin", "DeleteCheckInUploadCopy", copy); }
                Pass("STEP source prepares exact upload bytes without calling SolidWorks import or save");
            }
            InvokeStatic("PdmApiClient", "ValidateCheckInReferences", root, new System.Collections.Generic.HashSet<string>(new[] { rootPath, sourcePath }, StringComparer.OrdinalIgnoreCase));
            InvokeStatic("NativeArchiveManifest", "Validate", root, files);
            Require(children.Count == 2, "rechecking duplicated STEP source");
            Pass("STEP source is mandatory in same atomic batch, excluded from BOM, deduplicated on recheck");
            Set(imported, "ImportedSourcePath", sourcePath + ".different");
            ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { cache + "|" + sourcePath }), "未能对应");
            Set(imported, "ImportedSourcePath", sourcePath);
            Pass("IC source mismatch blocks whole batch without substitution");
            File.Delete(sourcePath);
            ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "Validate", root, files), "记录路径中找不到文件");
            File.WriteAllText(sourcePath, "exact original STEP bytes");
            Pass("missing STEP source blocks entire batch rather than skipping file");
            foreach (var malformed in new[] { cache + "|", cache + "|" + sourcePath + "|extra", cache + "|" + folder + "\\坏\t源.stp" })
                ExpectPathFailure(() => InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { malformed }));
            Pass("malformed composites retain strict path diagnostics");
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
            ExpectInvalid(() => InvokeStatic("NativeArchiveManifest", "Validate", root, files), "文件为空");
            Require(children.Count == 2, "failed source probe changed archive structure");
            File.WriteAllText(sourcePath, "exact original STEP bytes");
            Pass("empty STEP blocks whole batch without mutating scope");
            foreach (var error in new Exception[] { new UnauthorizedAccessException("raw permission denied"), new System.Security.SecurityException("raw security failure"), new DirectoryNotFoundException("raw directory failure"), new IOException("raw network failure", unchecked((int)0x80070035)), new IOException("raw IO failure") })
            {
                var reason = (string)InvokeStatic("NativeArchiveManifest", "SourceReadFailureReason", error);
                Require(!reason.Contains("raw") && !reason.Contains("已删除") && !reason.Contains("找不到文件"), "unreadable source misreported as missing or leaked raw English error");
            }
            Pass("permission, directory, network and IO causes have distinct Chinese diagnostics");
        }
        finally { File.Delete(cache); Directory.Delete(cacheFolder); Directory.Delete(Path.GetDirectoryName(cacheFolder)); }
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
