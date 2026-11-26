using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task AssemblyContextScopeMetadataReplay(string captureDirectory)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var rows = json.Deserialize<object[]>(File.ReadAllText(Path.Combine(captureDirectory, "real-all-versions.json")));
        var versions = rows.Cast<Dictionary<string, object>>().Select(raw =>
        {
            raw.Remove("Revision");
            return json.Deserialize(json.Serialize(raw), Type("DocumentVersionDto"));
        }).ToArray();
        var rawEntry = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(captureDirectory, "real-cache-entry.json")));
        var manifest = json.Deserialize(json.Serialize(rawEntry["Manifest"]), Type("ControlledOpenManifestDto"));
        var files = ((IEnumerable)Get(manifest, "Files")).Cast<object>().ToArray();
        var parentId = (Guid)Get(manifest, "RootDocumentId");
        var target = files.Single(file => (string)Get(file, "FileName") == "70005255.F02.SLDASM");
        var targetId = (Guid)Get(target, "DocumentId");
        Set(manifest, "RootDocumentId", targetId); Set(manifest, "RootVersionId", Get(target, "VersionId"));
        Set(manifest, "RootRelativePath", Get(target, "RelativePath"));
        foreach (var file in files) Set(file, "IsRoot", Get(file, "DocumentId").Equals(targetId));
        var plugin = New("PdmAddin"); var reads = new HashSet<Guid>();
        using (var lifetime = new CancellationTokenSource())
        using (var client = (IDisposable)New("PdmApiClient", "http://real-metadata.invalid/"))
        using (var handler = new ReferenceRecoveryHandler(request =>
        {
            TreeCheck(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/versions"), "真实快照重放仅允许版本查询");
            var id = Guid.Parse(request.RequestUri.AbsolutePath.Split('/').Reverse().Skip(1).First());
            lock (reads) reads.Add(id);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(versions.Where(v => Get(v, "DocumentId").Equals(id)).ToArray())) };
        }))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://real-metadata.invalid/") })
        {
            SetField(client, "httpClient", http); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
            var cache = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentVersionDto")));
            var task = (Task)TreeCall(plugin, "ReadRequiredOperationFileIdsAsync", manifest, cache); await task;
            var required = new HashSet<Guid>(((IEnumerable)Get(task, "Result")).Cast<Guid>());
            TreeCheck(required.Contains(targetId) && !required.Contains(parentId) && !reads.Contains(parentId), "真实F02配置Default环境总装不作为设计驱动");
            var open = (Task)TreeCall(plugin, "VerifiedOpenContextIdsAsync", manifest, true); await open;
            var environment = new HashSet<Guid>(((IEnumerable)Get(open, "Result")).Cast<Guid>());
            TreeCheck(files.All(f => environment.Contains((Guid)Get(f, "DocumentId")) ==
                (!required.Contains((Guid)Get(f, "DocumentId")) && !((string)Get(f, "FileName")).EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase))),
                "真实子装配的普通打开和获取使用一致的引用范围");
            Console.WriteLine("PASS: 真实70005255.F02快照重放，下载清单=" + files.Length + "，设计依赖=" + required.Count + "，环境=" + environment.Count);
        }
    }

    static async Task ContextScopeMetadataReplay(string capture)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var rows = json.Deserialize<object[]>(File.ReadAllText(capture));
        var raw = (Dictionary<string, object>)rows[0];
        raw.Remove("Revision"); // DB label is not the API's RevisionDto; the reference snapshot remains exact.
        var version = json.Deserialize(json.Serialize(raw), Type("DocumentVersionDto"));
        var snapshot = Get(version, "ReferenceSnapshot");
        var references = ((IEnumerable)Get(snapshot, "Children")).Cast<object>().ToArray();
        TreeCheck((string)Get(snapshot, "FileName") == "70005255.M01.03.01-02.SLDPRT" && references.Length == 2,
            "真实P700017-14零件及两个存档环境记录");
        var manifest = New("ControlledOpenManifestDto"); var id = (Guid)Get(version, "DocumentId");
        Set(manifest, "RootDocumentId", id); Set(manifest, "RootVersionId", Get(version, "Id"));
        Set(manifest, "RootRelativePath", Get(snapshot, "FileName"));
        var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType); Set(manifest, "Files", files);
        var root = New("ControlledOpenFileDto"); Set(root, "DocumentId", id); Set(root, "VersionId", Get(version, "Id"));
        Set(root, "Sha256", Get(version, "Sha256")); Set(root, "FileLength", Get(version, "FileLength"));
        Set(root, "FileName", Get(snapshot, "FileName")); Set(root, "Configuration", Get(snapshot, "Configuration")); files.Add(root);
        foreach (var reference in references.GroupBy(item => Get(item, "DocumentId")).Select(group => group.First()))
        {
            var environment = New("ControlledOpenFileDto"); Set(environment, "DocumentId", Get(reference, "DocumentId"));
            Set(environment, "FileName", Get(reference, "FileName")); Set(environment, "Configuration", ""); files.Add(environment);
        }
        while (files.Count < 522)
        {
            var file = New("ControlledOpenFileDto"); Set(file, "DocumentId", Guid.NewGuid());
            Set(file, "FileName", "环境下载成员" + files.Count + ".SLDPRT"); files.Add(file);
        }
        var plugin = New("PdmAddin"); var reads = 0;
        using (var lifetime = new CancellationTokenSource())
        using (var client = (IDisposable)New("PdmApiClient", "http://real-metadata.invalid/"))
        using (var handler = new ReferenceRecoveryHandler(request =>
        {
            TreeCheck(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/" + id + "/versions"), "重放只读取目标零件版本");
            reads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { version })) };
        }))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://real-metadata.invalid/") })
        {
            SetField(client, "httpClient", http); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
            var cache = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentVersionDto")));
            var task = (Task)TreeCall(plugin, "ReadRequiredOperationFileIdsAsync", manifest, cache); await task;
            var selected = new HashSet<Guid>(((IEnumerable)Get(task, "Result")).Cast<Guid>());
            TreeCheck(selected.SetEquals(new[] { id }) && reads == 1 && files.Count == 522,
                "真实环境快照只校验目标零件，保留522文件下载清单，不操作CAD或PLM");
            Console.WriteLine("PASS: P700017-14真实存档引用快照与522项环境下载清单重放");
        }
    }

    static async Task ContextScopedCheckoutScenario(string output)
    {
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var bindings = identity.GetField("bindingDirectory", Flags);
        var oldResolver = resolver.GetValue(null); var oldBindings = bindings.GetValue(null);
        resolver.SetValue(null, new Func<string>(() => output)); bindings.SetValue(null, Path.Combine(output, "bindings"));
        try
        {
            foreach (var mode in new[] { "parent-advanced", "context-parent-advanced", "context-522-files", "context-parent-race",
                "context-sibling-advanced", "context-loaded-part", "context-real-driver", "context-double-role",
                "context-unverified", "context-malformed", "context-target-advanced", "context-target-race",
                "context-scope-race", "context-target-local-edit", "context-foreign-source", "context-denied", "context-other-project",
                "context-subassembly", "context-incomplete", "context-real-driver-clean", "context-open-and-acquire",
                "context-acquire-open-subassembly-blank-environment", "context-acquire-open-part",
                "context-acquire-open-subassembly", "context-acquire-open-latest-subassembly", "context-acquire-open-dirty-parent",
                "context-standalone-part-after-checkin", "context-missing-sibling", "context-new-uncached-sibling", "context-acquire-open-missing-sibling", "context-subassembly-toolbox-dirty-parent",
                "context-subassembly-toolbox-stamped", "context-subassembly-toolbox-stamp-changed", "context-scope-race-stamped" })
            {
                var toolboxInParentOnly = mode.StartsWith("context-subassembly-toolbox-", StringComparison.Ordinal);
                var scopeRaces = mode is "context-scope-race" or "context-scope-race-stamped";
                var standaloneAfterCheckin = mode is "context-standalone-part-after-checkin" or "context-missing-sibling" or "context-new-uncached-sibling";
                var missingSibling = mode is "context-missing-sibling" or "context-new-uncached-sibling" or "context-acquire-open-missing-sibling";
                var allowed = mode is "parent-advanced" or "context-parent-advanced" or "context-522-files"
                    or "context-parent-race" or "context-sibling-advanced" or "context-loaded-part"
                    or "context-subassembly" or "context-real-driver-clean" or "context-open-and-acquire"
                    or "context-acquire-open-subassembly-blank-environment" or "context-acquire-open-part"
                    or "context-acquire-open-subassembly" or "context-acquire-open-latest-subassembly" or "context-acquire-open-dirty-parent"
                    || standaloneAfterCheckin || missingSibling || toolboxInParentOnly;
                var acquireThenOpen = mode.StartsWith("context-acquire-open-", StringComparison.Ordinal);
                var subassembly = mode.Contains("subassembly");
                var openAndAcquire = mode == "context-open-and-acquire";
                var isContext = mode.StartsWith("context-", StringComparison.Ordinal);
                var folder = Path.Combine(output, "View", mode); Directory.CreateDirectory(folder);
                var bytes = Encoding.UTF8.GetBytes("unchanged controlled design bytes");
                var manifest = SpeedManifest("已存档上级.SLDASM", bytes);
                Set(manifest, "ForEdit", true);
                var files = (IList)Get(manifest, "Files");
                while (files.Count > 3) files.RemoveAt(files.Count - 1);
                if (mode == "context-522-files")
                    while (files.Count < 522)
                    {
                        var extra = New("ControlledOpenFileDto");
                        foreach (var property in files[2].GetType().GetProperties()) property.SetValue(extra, property.GetValue(files[2]));
                        Set(extra, "DocumentId", Guid.NewGuid()); Set(extra, "FileName", "环境零件" + files.Count + ".SLDPRT");
                        Set(extra, "RelativePath", Get(extra, "FileName")); files.Add(extra);
                    }
                var project = (Guid)Get(manifest, "ProjectId"); var root = files[0]; var target = files[1]; var sibling = files[2];
                if (subassembly) { Set(target, "FileName", "子装配.SLDASM"); Set(target, "RelativePath", Get(target, "FileName")); }
                var rootId = (Guid)Get(root, "DocumentId"); var targetId = (Guid)Get(target, "DocumentId");
                const string toolboxName = "hexagon socket head cap screws gb.sldprt";
                const string toolboxInstance = "hexagon socket head cap screws gb-2";
                const string toolboxConfig = "GB_FASTENER_SCREWS_HSHCS M8X12-N";
                const string toolboxOriginal = @"Z:\cad-cache\GB_FASTENER_SCREWS_HSHCS M8X12-N.SLDPRT";
                object ordinaryPart = null;
                if (toolboxInParentOnly)
                {
                    ordinaryPart = CloneFixtureFile(sibling, "受控零件.SLDPRT");
                    files.Add(ordinaryPart);
                    Set(sibling, "FileName", toolboxName);
                    Set(sibling, "RelativePath", toolboxName);
                    var warning = New("OpenReferenceIssueDto");
                    Set(warning, "IsToolbox", true);
                    Set(warning, "OwnerDocumentId", targetId);
                    Set(warning, "OriginalPath", toolboxOriginal);
                    Set(warning, "FileName", Path.GetFileName(toolboxOriginal));
                    Set(warning, "InstancePath", "总装-1/子装配-1/" + toolboxInstance);
                    Set(warning, "Reason", "Toolbox引用提醒：" + toolboxOriginal
                        + "；Toolbox引用待核对，本次不上传；实例 总装-1/子装配-1/" + toolboxInstance
                        + @"；实际来源 C:\SOLIDWORKS Data\browser\gb\" + toolboxName + "；原有关联保留，结构待补全");
                    ((IList)Get(manifest, "ReferenceIssues")).Add(warning);
                }
                object CloneFixtureFile(object original, string name)
                {
                    var file = New("ControlledOpenFileDto");
                    foreach (var property in original.GetType().GetProperties()) property.SetValue(file, property.GetValue(original));
                    Set(file, "DocumentId", Guid.NewGuid());
                    Set(file, "FileName", name);
                    Set(file, "RelativePath", name);
                    return file;
                }
                Guid? drawingId = null;
                if (standaloneAfterCheckin)
                {
                    var drawing = New("ControlledOpenFileDto");
                    drawingId = Guid.NewGuid();
                    Set(drawing, "DocumentId", drawingId.Value);
                    Set(drawing, "VersionId", Guid.NewGuid());
                    Set(drawing, "FileName", "配套工程图.SLDDRW");
                    Set(drawing, "RelativePath", Get(drawing, "FileName"));
                    Set(drawing, "Sha256", Get(target, "Sha256"));
                    Set(drawing, "FileLength", Get(target, "FileLength"));
                    Set(drawing, "Revision", "W2");
                    Set(target, "Revision", "W2");
                    files.Add(drawing);
                    Set(root, "IsRoot", false);
                    Set(target, "IsRoot", true);
                    Set(manifest, "RootDocumentId", targetId);
                    Set(manifest, "RootVersionId", Get(target, "VersionId"));
                    Set(manifest, "RootRelativePath", Get(target, "RelativePath"));
                }
                var parentPath = Path.Combine(folder, (string)Get(root, "FileName"));
                var path = Path.Combine(folder, (string)Get(target, "FileName"));
                foreach (var file in files)
                {
                    Set(file, "Configuration", "Default");
                    if (missingSibling && file == sibling) continue;
                    var local = Path.Combine(folder, (string)Get(file, "FileName")); File.WriteAllBytes(local, bytes);
                    File.SetAttributes(local, FileAttributes.ReadOnly);
                    TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", local,
                        Get(file, "DocumentId"), project, Get(file, "VersionId"), Get(file, "Revision"), Get(file, "Sha256"), (long)bytes.Length), "scope fixture");
                }
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                object Clone(object value) => json.Deserialize(json.Serialize(value), value.GetType());
                var originalRoot = root.GetType().GetProperties().ToDictionary(property => property.Name, property => property.GetValue(root));
                var originalRootVersion = Get(manifest, "RootVersionId");
                var latest = Clone(manifest); var latestFiles = (IList)Get(latest, "Files");
                Set(latestFiles[0], "VersionId", Guid.NewGuid());
                if (!standaloneAfterCheckin) Set(latest, "RootVersionId", Get(latestFiles[0], "VersionId"));
                if (mode.Contains("latest-subassembly")) Set(latestFiles[0], "Sha256", new string('B', 64));
                if (mode == "context-sibling-advanced" || mode == "context-real-driver") Set(latestFiles[2], "VersionId", Guid.NewGuid());
                if (mode == "context-target-advanced") Set(latestFiles[1], "VersionId", Guid.NewGuid());
                if (missingSibling) Set(latestFiles[2], "VersionId", Guid.NewGuid());
                if (mode == "context-new-uncached-sibling") files.Remove(sibling);
                var modelManifest = Clone(latest); var modelFiles = (IList)Get(modelManifest, "Files");
                if (toolboxInParentOnly) modelFiles.RemoveAt(2);
                if (!isContext) { while (modelFiles.Count > 2) modelFiles.RemoveAt(modelFiles.Count - 1); modelFiles.RemoveAt(0); }
                foreach (var file in modelFiles) { Set(file, "IsRoot", Get(file, "DocumentId").Equals(targetId)); if (Get(file, "DocumentId").Equals(rootId)) Set(file, "Configuration", acquireThenOpen && !mode.Contains("blank-environment") ? "Default" : ""); }
                Set(modelManifest, "RootDocumentId", targetId); Set(modelManifest, "RootVersionId", Get(latestFiles[1], "VersionId")); Set(modelManifest, "RootRelativePath", Get(target, "FileName"));
                if (mode == "context-other-project") Set(modelManifest, "ProjectId", Guid.NewGuid());
                var plugin = New("PdmAddin"); var session = (Guid)Field(plugin, "checkoutSessionId");
                using var pane = standaloneAfterCheckin ? (Control)New("PdmTaskPaneControl", "存档后原位获取") : null;
                if (pane != null) SetField(plugin, "taskPaneControl", pane);
                var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
                var parent = new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                    call.MethodName == "GetPathName" ? parentPath : call.MethodName == "IsOpenedReadOnly" ? mode != "context-acquire-open-dirty-parent" && !toolboxInParentOnly
                        : call.MethodName == "GetSaveFlag" ? (object)true
                        : throw new Exception("不得读取上级结构或修改上级窗口：" + call.MethodName)).GetTransparentProxy();
                var events = new List<string>();
                var loadedTarget = new Model(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true),
                    mode == "context-foreign-source" ? Path.Combine(output, "外部", Path.GetFileName(path)) : path, false, events);
                var openTargetReadOnly = true;
                var config = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration", true), call => call.MethodName == "get_Name" ? "Default" : throw new Exception(call.MethodName)).GetTransparentProxy();
                var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true), call => call.MethodName == "get_ActiveConfiguration" ? config : throw new Exception(call.MethodName)).GetTransparentProxy();
                var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => call.MethodName == "ListExternalFileReferences2" ? null : throw new Exception(call.MethodName)).GetTransparentProxy();
                object toolboxComponent = null;
                object ordinaryComponent = null;
                var toolboxNativeType = true;
                if (toolboxInParentOnly)
                {
                    var toolboxPath = Path.Combine(folder, toolboxName);
                    var toolboxExtension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true),
                        call => call.MethodName == "get_ToolboxPartType"
                            ? (object)(toolboxNativeType ? 1 : 0) : throw new Exception(call.MethodName)).GetTransparentProxy();
                    var toolboxModel = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                    {
                        if (call.MethodName == "GetPathName") return toolboxPath;
                        if (call.MethodName == "get_Extension") return toolboxExtension;
                        throw new Exception("不得保存或修改Toolbox模型：" + call.MethodName);
                    }).GetTransparentProxy();
                    var ordinaryPath = Path.Combine(folder, (string)Get(ordinaryPart, "FileName"));
                    ordinaryComponent = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
                    {
                        switch (call.MethodName)
                        {
                            case "GetPathName": return ordinaryPath;
                            case "get_Name2": return "子装配-1/受控零件-1";
                            case "GetSuppression": return 2;
                            case "get_ReferencedConfiguration": return "Default";
                            case "get_IsVirtual": return false;
                            case "GetModelDoc2": return null;
                            default: throw new Exception("不得修改普通组件：" + call.MethodName);
                        }
                    }).GetTransparentProxy();
                    toolboxComponent = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
                    {
                        switch (call.MethodName)
                        {
                            case "GetPathName": return toolboxPath;
                            case "get_Name2": return "子装配-1/" + toolboxInstance;
                            case "GetSuppression": return 2;
                            case "get_ReferencedConfiguration": return toolboxConfig;
                            case "get_IsVirtual": return false;
                            case "GetModelDoc2": return toolboxModel;
                            default: throw new Exception("不得修改Toolbox实例：" + call.MethodName);
                        }
                    }).GetTransparentProxy();
                }
                var openTarget = new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                {
                    switch (call.MethodName)
                    {
                        case "GetPathName": return path;
                        case "GetTitle": return Path.GetFileName(path);
                        case "GetType": return subassembly ? 2 : 1;
                        case "GetComponents": return toolboxInParentOnly ? new[] { ordinaryComponent, toolboxComponent } : Array.Empty<object>();
                        case "GetSaveFlag": return false;
                        case "IsOpenedReadOnly": return openTargetReadOnly;
                        case "SetReadOnlyState": openTargetReadOnly = (bool)call.Args[0]; return true;
                        case "get_ConfigurationManager": return manager;
                        case "get_Extension": return extension;
                        case "GetEquationMgr": case "GetDesignTable": return null;
                        default: throw new Exception("打开并获取不得保存或修改模型：" + call.MethodName);
                    }
                }).GetTransparentProxy();
                var nativeModels = openAndAcquire || acquireThenOpen || standaloneAfterCheckin || toolboxInParentOnly ? new[] { parent, openTarget }
                    : mode is "context-loaded-part" or "context-foreign-source" ? new[] { parent, loadedTarget.GetTransparentProxy() } : new[] { parent };
                var searchOptions = new Dictionary<int, bool>();
                SetField(plugin, "application", new Proxy(appType, call =>
                {
                    if (call.MethodName == "GetDocuments") return nativeModels;
                    if (openAndAcquire || acquireThenOpen || standaloneAfterCheckin || toolboxInParentOnly) switch (call.MethodName)
                    {
                        case "get_ActiveDoc": case "ActivateDoc3": return openTarget;
                        case "GetCurrentWorkingDirectory": case "GetSearchFolders": return "";
                        case "SetCurrentWorkingDirectory": case "SetSearchFolders": return true;
                        case "GetUserPreferenceToggle": return searchOptions.TryGetValue((int)call.Args[0], out var value) && value;
                        case "SetUserPreferenceToggle": searchOptions[(int)call.Args[0]] = (bool)call.Args[1]; return true;
                    }
                    throw new Exception("不得重开、重建或保存图档：" + call.MethodName);
                }).GetTransparentProxy());
                ((IDictionary)Field(plugin, "controlledOpenManifests")).Add(folder, manifest);
                var selected = New("CadTreeNode"); Set(selected, "DocumentId", targetId); Set(selected, "FullPath", path);
                Set(selected, "FileName", Get(target, "FileName")); Set(selected, "Kind", Enum.Parse(Type("CadDocumentKind"), subassembly ? "Assembly" : "Part"));
                SetField(plugin, "currentTree", selected); SetField(plugin, "authenticatedUsername", "tester");
                var checkouts = 0; var drawingCheckouts = 0; var reads = new Dictionary<Guid, int>();
                object Reference(object file, bool environment)
                {
                    var node = New("DocumentReferenceNodeDto"); Set(node, "DocumentId", Get(file, "DocumentId")); Set(node, "FileName", Get(file, "FileName"));
                    Set(node, "Children", Activator.CreateInstance(Property(node, "Children").PropertyType));
                    Set(node, "Kind", Get(file, "DocumentId").Equals(rootId) ? 0 : 1); Set(node, "Role", 1); Set(node, "IsContextReference", environment);
                    Set(node, "ReferenceTypeUnverified", mode == "context-unverified");
                    if (mode == "context-malformed") Set(node, "Role", 0);
                    return node;
                }
                using (var lifetime = new CancellationTokenSource())
                using (var client = (IDisposable)New("PdmApiClient", "http://context-scope.invalid/"))
                using (var handler = new ReferenceRecoveryHandler(request =>
                {
                    var url = request.RequestUri.AbsolutePath; object result;
                    if (url.EndsWith("/open-manifest"))
                    {
                        var selectedScope = url.Contains(targetId.ToString());
                        result = selectedScope ? modelManifest : latest;
                        if (selectedScope && mode.Contains("stamp"))
                            Set(modelManifest, "ReferenceScopeStamp",
                                checkouts > 0 && (scopeRaces || mode.EndsWith("stamp-changed", StringComparison.Ordinal))
                                    ? "changed scope signature" : "stable scope signature");
                    }
                    else if (url.EndsWith("/document-operation-rules")) result = new { disabledRuleIds = Array.Empty<string>() };
                    else if (url.EndsWith("/document-relations")) result = drawingId.HasValue
                        ? new object[] { new { modelDocumentId = targetId, drawingDocumentId = drawingId.Value } }
                        : Array.Empty<object>();
                    else if (url.EndsWith("/documents")) result = files.Cast<object>().Select(file => new { id = Get(file, "DocumentId"), projectId = project, fileName = Get(file, "FileName") }).ToArray();
                    else if (url.EndsWith("/versions"))
                    {
                        var id = Guid.Parse(url.Split('/').Reverse().Skip(1).First()); reads[id] = reads.TryGetValue(id, out var count) ? count + 1 : 1;
                        var file = latestFiles.Cast<object>().Single(value => Get(value, "DocumentId").Equals(id));
                        var snapshot = New("DocumentReferenceNodeDto"); Set(snapshot, "DocumentId", id); Set(snapshot, "FileName", Get(file, "FileName"));
                        Set(snapshot, "Children", Activator.CreateInstance(Property(snapshot, "Children").PropertyType));
                        Set(snapshot, "Kind", id == rootId || id == targetId && subassembly ? 0 : id == drawingId ? 2 : 1); Set(snapshot, "Configuration", "Default"); Set(snapshot, "UsesNativeFileInventory", true);
                        var children = (IList)Get(snapshot, "Children");
                        if (id == targetId && isContext && mode != "context-incomplete")
                        {
                            children.Add(Reference(root, !(scopeRaces && checkouts > 0)));
                            if (mode is "context-real-driver" or "context-real-driver-clean") children.Add(Reference(sibling, false));
                            if (mode == "context-double-role") children.Add(Reference(root, false));
                            if (toolboxInParentOnly)
                            {
                                var child = Reference(ordinaryPart, false);
                                Set(child, "Role", 0);
                                Set(child, "Configuration", "Default");
                                children.Add(child);
                            }
                        }
                        var version = New("DocumentVersionDto"); Set(version, "Id", Get(file, "VersionId")); Set(version, "DocumentId", id);
                        Set(version, "Sha256", Get(file, "Sha256")); Set(version, "FileLength", (long)bytes.Length); Set(version, "ReferenceSnapshot", snapshot);
                        result = new[] { version };
                    }
                    else if (url.EndsWith("/checkout") && url.Contains(targetId.ToString()))
                    {
                        checkouts++;
                        if (mode == "context-denied") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"无编辑权限\"}") };
                        if (mode == "context-parent-race") { Set(latestFiles[0], "VersionId", Guid.NewGuid()); Set(latest, "RootVersionId", Get(latestFiles[0], "VersionId")); }
                        if (mode == "context-target-race") { Set(latestFiles[1], "VersionId", Guid.NewGuid()); }
                        if (mode == "context-target-local-edit") { File.SetAttributes(path, FileAttributes.Normal); File.AppendAllText(path, "unexpected change"); File.SetAttributes(path, FileAttributes.ReadOnly); }
                        result = new { id = targetId, projectId = project, checkedOutBy = "tester", checkoutSessionId = session, checkedOutAt = DateTime.UtcNow };
                    }
                    else if (drawingId.HasValue && url.EndsWith("/" + drawingId.Value + "/checkout"))
                    {
                        drawingCheckouts++;
                        result = new { id = drawingId.Value, projectId = project, checkedOutBy = "tester", checkoutSessionId = session, checkedOutAt = DateTime.UtcNow };
                    }
                    else throw new Exception("不应扩大读写范围：" + url);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(result)) };
                }))
                using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://context-scope.invalid/") })
                {
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
                    SetField(client, "httpClient", http); SetField(client, "controlledOpenHttpClient", http); SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
                    var scope = (string)Get(client, "EditJournalScope") + "|tester";
                    SetField(plugin, "editJournal", New("EditDraftJournal", Path.Combine(folder, "journal"), scope)); SetField(plugin, "editJournalScope", scope);
                    var task = openAndAcquire ? (Task)TreeCall(plugin, "TryOpenModelInLoadedContextAsync", modelManifest, true)
                        : (Task)TreeCall(plugin, "TryAcquireReusableWorkingCopyAsync", LinkedItems(selected), project, targetId);
                    Exception failure = null; try { await task; } catch (InvalidOperationException error) { failure = error; }
                    if (allowed && failure != null) throw failure;
                    TreeCheck((failure == null && (bool)Get(task, "Result")) == allowed, "按实际引用范围获取：" + mode);
                    if (acquireThenOpen)
                    {
                        var open = (Task)TreeCall(plugin, "TryOpenModelInLoadedContextCoreAsync", modelManifest, false,
                            mode.Contains("latest") ? null : path);
                        await open;
                        var outdated = (Task)TreeCall(plugin, "HasOutdatedLoadedAssemblyContextAsync", modelManifest);
                        await outdated;
                        TreeCheck(!(bool)Get(outdated, "Result"), "环境总装升版不触发当前子装配的最新版替换：" + mode);
                        TreeCheck((bool)Get(open, "Result") && !openTargetReadOnly && checkouts == 1,
                            "获取后普通打开复用可写窗口，不重复获取或切换环境总装：" + mode);
                    }
                    TreeCheck(events.Count == 0 && File.ReadAllBytes(parentPath).SequenceEqual(bytes)
                        && (File.GetAttributes(parentPath) & FileAttributes.ReadOnly) != 0, "不保存、重建或解锁上级：" + mode);
                    TreeCheck(((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) == allowed
                        && loadedTarget.ReadOnly == (mode != "context-loaded-part") && (!openAndAcquire || !openTargetReadOnly), "只解锁通过核对的零件：" + mode);
                    if (missingSibling)
                        TreeCheck(!File.Exists(Path.Combine(folder, (string)Get(sibling, "FileName"))) && checkouts == 1,
                            "未缓存的环境零件无需下载，也不取得编辑权限：" + mode);
                    if (toolboxInParentOnly)
                    {
                        if (mode.Contains("stamp"))
                        {
                            var readsForPart = reads[(Guid)Get(ordinaryPart, "DocumentId")];
                            TreeCheck(readsForPart == (mode.EndsWith("stamped", StringComparison.Ordinal) ? 1 : 2),
                                "同一引用范围复用首次证据，范围戳变化时重新读版本：" + mode + "；读取次数=" + readsForPart);
                        }
                        var open = (Task)TreeCall(plugin, "TryOpenModelInLoadedContextCoreAsync", modelManifest, false, path);
                        await open;
                        TreeCheck((bool)Get(open, "Result"), "单独打开子装配时识别已核实的装配工作区Toolbox");
                        toolboxNativeType = false;
                        try
                        {
                            await (Task)TreeCall(plugin, "TryOpenModelInLoadedContextCoreAsync", modelManifest, false, path);
                            throw new Exception("普通零件从父工作区越过了所选子装配的引用来源检查");
                        }
                        catch (InvalidOperationException error)
                        { TreeCheck(error.Message.Contains(toolboxInstance), "正常文件越界诊断须指出实际实例"); }
                        TreeCheck(!openTargetReadOnly && !reads.ContainsKey((Guid)Get(sibling, "DocumentId"))
                            && (File.GetAttributes(Path.Combine(folder, toolboxName)) & FileAttributes.ReadOnly) != 0,
                            "总装有未保存修改时仅原位获取子装配，父清单中的Toolbox继续只读且不查询其版本");

                    }
                    if (standaloneAfterCheckin)
                        TreeCheck(!openTargetReadOnly && drawingCheckouts == 1 && !reads.ContainsKey(rootId),
                            "单独打开的 W2 零件原位再次获取，配套 W2 工程图一并授权，不校验旧环境总装");
                    if (allowed)
                    {
                        TreeCheck(checkouts == 1 && !reads.ContainsKey(rootId)
                            && (mode == "context-real-driver-clean" || !reads.ContainsKey((Guid)Get(sibling, "DocumentId"))), "环境图档无版本请求或获取：" + mode);
                        TreeCheck(originalRoot.All(pair => Equals(Get(root, pair.Key), pair.Value)), "上级文件记录保持：" + mode);
                        TreeCheck(Equals(Get(manifest, "RootVersionId"), originalRootVersion), "上级清单基准保持：" + mode);
                    }
                    Console.WriteLine("PASS: 存档上级后按实际范围获取零件：" + mode);
                }
            }
        }
        finally { resolver.SetValue(null, oldResolver); bindings.SetValue(null, oldBindings); }
    }
}
