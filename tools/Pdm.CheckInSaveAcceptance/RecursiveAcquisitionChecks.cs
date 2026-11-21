using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static async Task RecursiveAcquisitionScenario(string output)
    {
        var identity = Type("PdmDocumentIdentityStore");
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var bindings = identity.GetField("bindingDirectory", Flags);
        var isolated = identity.GetField("isolatedReadScope", Flags);
        var oldResolver = resolver.GetValue(null); var oldBindings = bindings.GetValue(null); var oldIsolated = isolated.GetValue(null);
        output = Path.GetFullPath(output);
        resolver.SetValue(null, new Func<string>(() => output));
        bindings.SetValue(null, Path.Combine(output, "bindings")); isolated.SetValue(null, true);
        try
        {
            foreach (var mode in new[] { "root", "selected-subassembly", "multiple-configurations", "repeated-instance", "multiple-owners",
                "managed-suppressed", "managed-lightweight", "other-path", "new-missing", "resolved-missing",
                "extra-managed-instance", "configuration-changed", "unselected-insertion", "source-race", "instance-race",
                "suppression-race", "server-race", "permission-denied", "cross-project" })
            {
                var folder = Path.Combine(output, "View", mode); Directory.CreateDirectory(folder);
                var plugin = New("PdmAddin"); var project = Guid.NewGuid(); var session = (Guid)Field(plugin, "checkoutSessionId");
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = LinkedNode(folder, "总装.SLDASM", "Assembly");
                var mid = LinkedNode(folder, "子装配.SLDASM", "Assembly");
                var inner = LinkedNode(folder, "下层装配.SLDASM", "Assembly");
                var part = LinkedNode(folder, "受控零件.SLDPRT", "Part");
                var peer = LinkedNode(folder, "另一零件.SLDPRT", "Part");
                var nodes = new[] { root, mid, inner, part, peer };
                foreach (var node in nodes) Set(node, "Configuration", "Default");
                ((IList)Get(root, "Children")).Add(mid); ((IList)Get(mid, "Children")).Add(inner);
                ((IList)Get(inner, "Children")).Add(part); ((IList)Get(inner, "Children")).Add(peer);
                var multiple = mode is "multiple-configurations" or "repeated-instance";
                if (multiple) ((IList)Get(mid, "Children")).Add(inner);
                var rootId = (Guid)Get(root, "DocumentId"); var innerId = (Guid)Get(inner, "DocumentId");
                var selected = mode == "selected-subassembly" ? mid : root;
                var selectedId = (Guid)Get(selected, "DocumentId");
                var versions = nodes.ToDictionary(node => (Guid)Get(node, "DocumentId"), node => LinkedVersion(node, LinkedSnapshot(node)));
                if (mode == "multiple-configurations")
                {
                    var midSnapshot = Get(versions[(Guid)Get(mid, "DocumentId")], "ReferenceSnapshot");
                    Set(((IList)Get(midSnapshot, "Children"))[1], "Configuration", "Alternate");
                    var rootMid = ((IList)Get(Get(versions[rootId], "ReferenceSnapshot"), "Children"))[0];
                    Set(((IList)Get(rootMid, "Children"))[1], "Configuration", "Alternate");
                }
                var manifest = New("ControlledOpenManifestDto");
                Set(manifest, "ProjectId", project); Set(manifest, "RootDocumentId", rootId);
                Set(manifest, "RootVersionId", Get(versions[rootId], "Id")); Set(manifest, "RootRelativePath", Get(root, "FileName"));
                Set(manifest, "ForEdit", true);
                var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                Set(manifest, "Files", files);
                foreach (var node in nodes)
                {
                    var id = (Guid)Get(node, "DocumentId"); var path = (string)Get(node, "FullPath"); var version = versions[id];
                    var file = New("ControlledOpenFileDto"); Set(file, "DocumentId", id); Set(file, "VersionId", Get(version, "Id"));
                    Set(file, "FileName", Get(node, "FileName")); Set(file, "RelativePath", Get(node, "FileName"));
                    Set(file, "Configuration", "Default"); Set(file, "Revision", "W1"); Set(file, "IsRoot", id == rootId);
                    Set(file, "Sha256", WorkingReuseHash(path)); Set(file, "FileLength", new FileInfo(path).Length); files.Add(file);
                    TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id,
                        mode == "cross-project" && id == innerId ? Guid.NewGuid() : project, Get(version, "Id"), "W1", Get(file, "Sha256"), Get(file, "FileLength")), "binding");
                    File.SetAttributes(path, FileAttributes.ReadOnly);
                }
                var original = nodes.ToDictionary(node => (string)Get(node, "FullPath"), node => File.ReadAllBytes((string)Get(node, "FullPath")));
                ((IDictionary)Field(plugin, "controlledOpenManifests")).Add(folder, manifest);
                SetField(plugin, "currentTree", root); SetField(plugin, "authenticatedUsername", "tester"); SetField(plugin, "currentProjectId", project);
                var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
                var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
                var models = new Dictionary<Guid, object>(); var readOnly = nodes.ToDictionary(node => (Guid)Get(node, "DocumentId"), _ => true);
                var nativeWrites = new List<Guid>(); var changedDuringCheckout = false;
                var managed = mode.StartsWith("managed-", StringComparison.Ordinal) || mode == "extra-managed-instance";
                var oldPath = Path.Combine(output, "原存档目录", managed ? (string)Get(part, "FileName") : "历史夹具.SLDASM");
                var midOldPath = Path.Combine(output, "原存档目录", "中层历史夹具.SLDPRT");
                object Component(string path, object model, string configuration, int state, Func<object[]> children = null) =>
                    new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
                    {
                        switch (call.MethodName)
                        {
                            case "GetPathName": return path;
                            case "GetModelDoc2": return model;
                            case "get_ReferencedConfiguration": return configuration;
                            case "get_IsVirtual": return false;
                            case "GetSuppression": return state;
                            case "IsSuppressed": return state == 0;
                            case "GetChildren": return children?.Invoke() ?? Array.Empty<object>();
                            default: throw new Exception("不能修改实例：" + call.MethodName);
                        }
                    }).GetTransparentProxy();
                object Known(object node, Func<object[]> children = null, string configuration = "Default") =>
                    Component((string)Get(node, "FullPath"), models[(Guid)Get(node, "DocumentId")], configuration, 2, children);
                object[] InnerChildren(string configuration = "Default")
                {
                    var result = new List<object> { Known(peer) };
                    var state = mode == "managed-lightweight" ? 1 : mode == "resolved-missing" || mode == "suppression-race" && changedDuringCheckout ? 2 : 0;
                    if (managed) result.Add(Component(oldPath, null, "Default", state));
                    else
                    {
                        result.Add(Known(part, configuration: mode == "configuration-changed" ? "Wrong" : "Default"));
                        result.Add(Component(oldPath, null, "Default", state));
                    }
                    if (mode == "extra-managed-instance" || mode == "instance-race" && changedDuringCheckout)
                        result.Add(Component(oldPath, null, "Default", state));
                    if (mode == "unselected-insertion") result.Add(Known(peer));
                    return result.ToArray();
                }
                object[] MidChildren()
                {
                    var rows = new List<object> { Known(inner, () => InnerChildren()) };
                    if (multiple) rows.Add(Known(inner, () => InnerChildren("Alternate"), mode == "multiple-configurations" ? "Alternate" : "Default"));
                    if (mode == "multiple-owners") rows.Add(Component(midOldPath, null, "Default", 0));
                    return rows.ToArray();
                }
                foreach (var node in nodes)
                {
                    var id = (Guid)Get(node, "DocumentId"); var path = (string)Get(node, "FullPath");
                    var isAssembly = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
                    var configuration = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration", true),
                        call => call.MethodName == "get_Name" ? "Default" : throw new Exception(call.MethodName)).GetTransparentProxy();
                    var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true),
                        call => call.MethodName == "get_ActiveConfiguration" ? configuration : throw new Exception(call.MethodName)).GetTransparentProxy();
                    Func<System.Runtime.Remoting.Messaging.IMethodCallMessage, object> invoke = call =>
                    {
                        if (call.MethodName == "GetType" && (call.MethodBase as MethodInfo)?.ReturnType == typeof(System.Type)) return modelType;
                        switch (call.MethodName)
                        {
                            case "GetPathName": return path;
                            case "GetType": return isAssembly ? 2 : 1;
                            case "GetSaveFlag": return isAssembly;
                            case "GetUpdateStamp": return 0;
                            case "add_ModifyNotify": case "add_DestroyNotify2": case "add_FileReloadNotify":
                            case "remove_ModifyNotify": case "remove_DestroyNotify2": case "remove_FileReloadNotify": return null;
                            case "IsOpenedReadOnly": return readOnly[id];
                            case "get_ConfigurationManager":
                                if (id != selectedId) throw new Exception("不能用子装配的活动配置代替实际实例配置");
                                return manager;
                            case "GetComponents":
                                if (id != selectedId || !(bool)call.Args[0]) throw new Exception("递归核对应使用实际 GetChildren 实例");
                                return id == rootId ? new[] { Known(mid, MidChildren) } : MidChildren();
                            case "SetReadOnlyState": nativeWrites.Add(id); readOnly[id] = (bool)call.Args[0]; return true;
                            default: throw new Exception("获取不得保存、关闭、重开或重建：" + call.MethodName);
                        }
                    };
                    models[id] = isAssembly ? new AssemblyProxy(modelType, invoke).GetTransparentProxy() : new Proxy(modelType, invoke).GetTransparentProxy();
                }
                var dependencyReads = 0;
                SetField(plugin, "application", new Proxy(appType, call =>
                {
                    if (call.MethodName == "GetDocuments") return models.Values.ToArray();
                    if (call.MethodName == "GetDocumentDependencies2")
                    {
                        dependencyReads++;
                        if (call.Args.Skip(1).Cast<bool>().Any(value => value) || original.ContainsKey((string)call.Args[0]))
                            throw new Exception("只允许读取独立存档副本的原始直接引用");
                        var saved = mode == "other-path" ? Path.Combine(output, "异路径", Path.GetFileName(oldPath))
                            : mode == "multiple-owners" && Path.GetFileName((string)call.Args[0]) == (string)Get(mid, "FileName") ? midOldPath : oldPath;
                        return mode == "new-missing" ? Array.Empty<string>() : new[] { Path.GetFileName(saved), saved };
                    }
                    throw new Exception("获取不得操作窗口：" + call.MethodName);
                }).GetTransparentProxy());
                var checkouts = new List<Guid>(); var checkedAt = DateTime.UtcNow;
                using (var lifetime = new CancellationTokenSource())
                using (var client = (IDisposable)New("PdmApiClient", "http://recursive-checkout.invalid/"))
                using (var handler = new ReferenceRecoveryHandler(request =>
                {
                    var url = request.RequestUri.AbsolutePath;
                    var id = url.Split('/').Select(segment => Guid.TryParse(segment, out var parsed) ? parsed : Guid.Empty).FirstOrDefault(value => value != Guid.Empty);
                    object value;
                    if (url.EndsWith("/file"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(original[(string)Get(nodes.Single(node => (Guid)Get(node, "DocumentId") == id), "FullPath")]) };
                    if (url.EndsWith("/open-manifest"))
                    {
                        value = json.Deserialize(json.Serialize(manifest), manifest.GetType());
                        if (id != rootId)
                        {
                            var scoped = (IList)Get(value, "Files"); scoped.RemoveAt(0);
                            Set(scoped[0], "IsRoot", true); Set(value, "RootDocumentId", id);
                            Set(value, "RootVersionId", Get(versions[id], "Id")); Set(value, "RootRelativePath", Get(mid, "FileName"));
                        }
                        if (mode == "server-race" && checkouts.Count > 0) Set(((IList)Get(value, "Files"))[2], "VersionId", Guid.NewGuid());
                    }
                    else if (url.EndsWith("/document-operation-rules")) value = new { disabledRuleIds = Array.Empty<string>() };
                    else if (url.EndsWith("/document-relations")) value = Array.Empty<object>();
                    else if (url.EndsWith("/documents")) value = nodes.Select(node => new { id = Get(node, "DocumentId"), projectId = project,
                        fileName = Get(node, "FileName"), kind = Get(node, "Kind") }).ToArray();
                    else if (url.EndsWith("/versions")) value = new[] { versions[id] };
                    else if (url.EndsWith("/checkout"))
                    {
                        checkouts.Add(id);
                        if (mode == "permission-denied") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"无此项目的图档获取权限\"}") };
                        changedDuringCheckout = true;
                        if (mode == "source-race")
                        {
                            var ownerPath = (string)Get(inner, "FullPath"); File.SetAttributes(ownerPath, FileAttributes.Normal);
                            File.AppendAllText(ownerPath, " 外部并发写入"); File.SetAttributes(ownerPath, FileAttributes.ReadOnly);
                        }
                        value = new { id, projectId = project, checkedOutBy = "tester", checkoutSessionId = session, checkedOutAt = checkedAt };
                    }
                    else throw new Exception("未授权操作：" + request.Method + " " + url);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(value)) };
                }))
                using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://recursive-checkout.invalid/") })
                {
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
                    SetField(client, "httpClient", http); SetField(client, "controlledOpenHttpClient", http);
                    SetField(plugin, "apiClient", client); SetField(plugin, "lifetime", lifetime);
                    var scope = (string)Get(client, "EditJournalScope") + "|tester";
                    SetField(plugin, "editJournal", New("EditDraftJournal", Path.Combine(folder, "journal"), scope)); SetField(plugin, "editJournalScope", scope);
                    Exception failure = null; var success = false;
                    try { var task = (Task)TreeCall(plugin, "TryAcquireReusableWorkingCopyAsync", LinkedItems(selected), project, rootId); await task; success = (bool)Get(task, "Result"); }
                    catch (Exception error) { failure = error; }
                    var allowed = mode is "root" or "selected-subassembly" or "multiple-configurations" or "repeated-instance" or "multiple-owners" or "managed-suppressed" or "managed-lightweight";
                    var afterCheckout = mode is "source-race" or "instance-race" or "suppression-race" or "server-race" or "permission-denied";
                    if (allowed && failure != null) throw new Exception("递归获取失败：" + mode, failure);
                    TreeCheck(success == allowed && (failure != null) != allowed, "正确拒绝真实冲突：" + mode);
                    TreeCheck(checkouts.Count == (allowed || afterCheckout ? 1 : 0) && checkouts.All(id => id == selectedId), "权限仅授予所选文档：" + mode);
                    TreeCheck(nativeWrites.Count == (allowed ? 1 : 0) && nativeWrites.All(id => id == selectedId), "原位解锁范围：" + mode);
                    TreeCheck(dependencyReads <= (mode == "multiple-owners" ? 2 : 1), "同一存档文件的多实例只读取一次原始引用：" + mode);
                    foreach (var node in nodes)
                    {
                        var path = (string)Get(node, "FullPath"); var id = (Guid)Get(node, "DocumentId");
                        if (mode != "source-race" || id != innerId) TreeCheck(original[path].SequenceEqual(File.ReadAllBytes(path)), "不能覆盖图档：" + mode);
                        if (id != selectedId || !allowed) TreeCheck(readOnly[id] && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "子件权限和文件只读状态保留：" + mode);
                    }
                    Console.WriteLine("PASS: recursive acquisition " + mode + "; actual occurrences, selected-only checkout, no save/reload/rebuild");
                }
            }
        }
        finally { resolver.SetValue(null, oldResolver); bindings.SetValue(null, oldBindings); isolated.SetValue(null, oldIsolated); }
    }
}
