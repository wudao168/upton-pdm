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

internal static partial class Program
{
    static async Task SelectedPartCheckoutScenario(string output, bool compactWorkspace = false)
    {
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var bindings = identity.GetField("bindingDirectory", Flags);
        var originalResolver = resolver.GetValue(null);
        var originalBindings = bindings.GetValue(null);
        resolver.SetValue(null, new Func<string>(() => output));
        bindings.SetValue(null, Path.Combine(output, "bindings"));
        try
        {
            foreach (var mode in new[] { "unloaded", "foreign-loaded", "local-changed", "edit-during-checkout", "server-race",
                "subassembly-unloaded", "subassembly-loaded", "subassembly-wrong-configuration", "parent-only" })
            {
                var subassembly = mode.StartsWith("subassembly-", StringComparison.Ordinal);
                var allowed = mode is "unloaded" or "subassembly-unloaded" or "subassembly-loaded";
                var bytes = Encoding.UTF8.GetBytes("verified disk content");
                var manifest = SpeedManifest("上层总装.SLDASM", bytes);
                var project = (Guid)Get(manifest, "ProjectId");
                var folder = compactWorkspace ? (string)TreeCallStatic("ManagedCadPaths", "CompactDirectory", output, "w",
                    project + "|" + Get(manifest, "RootDocumentId"), Get(manifest, "ProjectCode")) : Path.Combine(output, "View", mode);
                Directory.CreateDirectory(folder);
                Set(manifest, "ForEdit", true);
                var files = (IList)Get(manifest, "Files");
                while (files.Count > 3) files.RemoveAt(files.Count - 1);
                var root = files[0];
                var target = files[1];
                var sibling = files[2];
                if (subassembly)
                {
                    Set(target, "FileName", "子装配.SLDASM");
                    Set(target, "RelativePath", "子装配.SLDASM");
                    Set(target, "Configuration", "默认");
                }
                Set(sibling, "FileName", "不相关工程图.SLDDRW");
                Set(sibling, "RelativePath", Get(sibling, "FileName"));
                var rootId = (Guid)Get(root, "DocumentId");
                var targetId = (Guid)Get(target, "DocumentId");
                var path = Path.Combine(folder, (string)Get(target, "FileName"));
                foreach (var file in files)
                {
                    var local = Path.Combine(folder, (string)Get(file, "FileName"));
                    File.WriteAllBytes(local, bytes);
                    File.SetAttributes(local, FileAttributes.ReadOnly);
                    TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion",
                        local, Get(file, "DocumentId"), project, Get(file, "VersionId"), "W1", Get(file, "Sha256"), (long)bytes.Length), "fixture binding");
                }
                var json = new JavaScriptSerializer();
                var latest = json.Deserialize(json.Serialize(manifest), manifest.GetType());
                var latestFiles = (IList)Get(latest, "Files");
                Set(latestFiles[2], "VersionId", Guid.NewGuid()); // Unrelated 2D is behind.
                var part = json.Deserialize(json.Serialize(latest), latest.GetType());
                var partFiles = (IList)Get(part, "Files");
                partFiles.RemoveAt(2);
                partFiles.RemoveAt(0);
                Set(partFiles[0], "IsRoot", true);
                Set(part, "RootDocumentId", targetId);
                Set(part, "RootVersionId", Get(target, "VersionId"));
                Set(part, "RootRelativePath", Get(target, "FileName"));
                var plugin = New("PdmAddin");
                var session = (Guid)Field(plugin, "checkoutSessionId");
                var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
                var interop = appType.Assembly;
                var modelType = interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
                var seenParent = 0;
                var parentPath = Path.Combine(folder, (string)Get(root, "FileName"));
                var parent = new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                {
                    switch (call.MethodName)
                    {
                        case "GetPathName": return parentPath;
                        case "GetSaveFlag": return true;
                        case "IsOpenedReadOnly": return true;
                        case "GetComponents" when mode == "parent-only": return Array.Empty<object>();
                        case "get_ConfigurationManager":
                            seenParent++;
                            return null;
                        default: throw new Exception("获取单件不应解析、打开或修改总装：" + call.MethodName);
                    }
                }).GetTransparentProxy();
                var foreign = mode == "foreign-loaded"
                    ? new Model(modelType, Path.Combine(output, "外部", Path.GetFileName(path)), false, new List<string>()).GetTransparentProxy()
                    : null;
                var targetReadOnly = true;
                if (subassembly && mode != "subassembly-unloaded")
                {
                    var configuration = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration", true),
                        call => call.MethodName == "get_Name" ? mode == "subassembly-loaded" ? "默认" : "其他配置"
                            : throw new Exception(call.MethodName)).GetTransparentProxy();
                    var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true),
                        call => call.MethodName == "get_ActiveConfiguration" ? configuration
                            : throw new Exception(call.MethodName)).GetTransparentProxy();
                    foreign = new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
                    {
                        switch (call.MethodName)
                        {
                            case "GetPathName": return path;
                            case "GetType": return 2;
                            case "GetSaveFlag": return false;
                            case "IsOpenedReadOnly": return targetReadOnly;
                            case "get_ConfigurationManager": return manager;
                            case "GetComponents": return Array.Empty<object>();
                            case "SetReadOnlyState": targetReadOnly = (bool)call.Args[0]; return true;
                            default: throw new Exception("子装配不应保存或重开：" + call.MethodName);
                        }
                    }).GetTransparentProxy();
                }
                SetField(plugin, "application", new Proxy(appType, call => call.MethodName == "GetDocuments"
                    ? foreign == null ? new[] { parent } : new[] { parent, foreign }
                    : throw new Exception("应复用现有工作文件：" + call.MethodName)).GetTransparentProxy());
                ((IDictionary)Field(plugin, "controlledOpenManifests")).Add(folder, manifest);
                var rootNode = New("CadTreeNode");
                Set(rootNode, "DocumentId", rootId);
                Set(rootNode, "FullPath", parentPath);
                Set(rootNode, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
                SetField(plugin, "currentTree", rootNode);
                SetField(plugin, "authenticatedUsername", "tester");
                var targetNode = New("CadTreeNode");
                Set(targetNode, "DocumentId", targetId);
                Set(targetNode, "FullPath", path);
                Set(targetNode, "FileName", Path.GetFileName(path));
                Set(targetNode, "Kind", Enum.Parse(Type("CadDocumentKind"), subassembly ? "Assembly" : "Part"));
                if (mode == "local-changed") { File.SetAttributes(path, FileAttributes.Normal); File.AppendAllText(path, " edited"); }
                var checkouts = 0;
                var rootVersionReads = 0;
                var checkedAt = DateTime.UtcNow;
                using (var lifetime = new CancellationTokenSource())
                using (var client = (IDisposable)New("PdmApiClient", "http://selected-part.invalid/"))
                using (var handler = new ReferenceRecoveryHandler(request =>
                {
                    var url = request.RequestUri.AbsolutePath;
                    var id = url.Contains(rootId.ToString()) ? rootId : targetId;
                    var file = id == rootId ? root : target;
                    object result;
                    if (url.EndsWith("/open-manifest")) result = id == targetId ? part : latest;
                    else if (url.EndsWith("/document-operation-rules")) result = new { disabledRuleIds = Array.Empty<string>() };
                    else if (url.EndsWith("/document-relations")) result = Array.Empty<object>();
                    else if (url.EndsWith("/documents")) result = new[] { new { id = targetId, projectId = project, fileName = Path.GetFileName(path) } };
                    else if (url.EndsWith("/versions"))
                    {
                        if (id == rootId) rootVersionReads++;
                        var versionId = (Guid)Get(file, "VersionId");
                        if (mode == "server-race" && checkouts > 0 && id == targetId) versionId = Guid.NewGuid();
                        result = new[] { new { id = versionId, documentId = id, sha256 = Get(file, "Sha256"), fileLength = bytes.Length,
                            referenceSnapshot = new { documentId = id, fileName = Get(file, "FileName"),
                                configuration = Get(file, "Configuration"), children = Array.Empty<object>() } } };
                    }
                    else if (url.EndsWith("/checkout") && id == targetId)
                    {
                        checkouts++;
                        if (mode == "edit-during-checkout")
                        {
                            File.SetAttributes(path, FileAttributes.Normal);
                            File.AppendAllText(path, " late edit");
                            File.SetAttributes(path, FileAttributes.ReadOnly);
                        }
                        result = new { id, projectId = project, fileName = Path.GetFileName(path),
                            checkedOutBy = "tester", checkoutSessionId = session, checkedOutAt = checkedAt };
                    }
                    else throw new Exception("不应扩大获取或下载范围：" + url);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(result)) };
                }))
                using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://selected-part.invalid/") })
                {
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
                    SetField(client, "httpClient", http);
                    SetField(client, "controlledOpenHttpClient", http);
                    SetField(plugin, "apiClient", client);
                    SetField(plugin, "lifetime", lifetime);
                    var scope = (string)Get(client, "EditJournalScope") + "|tester";
                    SetField(plugin, "editJournal", New("EditDraftJournal", Path.Combine(folder, "journal"), scope));
                    SetField(plugin, "editJournalScope", scope);
                    var rootSelected = mode == "parent-only";
                    var task = (Task)TreeCall(plugin, "TryAcquireReusableWorkingCopyAsync",
                        LinkedItems(rootSelected ? rootNode : targetNode), project, rootId);
                    Exception failure = null;
                    try { await task; }
                    catch (InvalidOperationException error) { failure = error; }
                    if (allowed && failure != null) throw failure;
                    TreeCheck((failure == null && (bool)Get(task, "Result")) == allowed, "获取结果应符合所选范围：" + mode);
                    TreeCheck(checkouts == (allowed || mode == "edit-during-checkout" || mode == "server-race" ? 1 : 0)
                        && seenParent == (rootSelected ? 1 : 0) && rootVersionReads == (rootSelected ? 1 : 0),
                        "仅核对所选零件，明确选择总装时保留其原生校验：" + mode);
                    TreeCheck((File.GetAttributes(parentPath) & FileAttributes.ReadOnly) != 0
                        && targetReadOnly == (mode != "subassembly-loaded")
                        && (File.GetAttributes(path) & FileAttributes.ReadOnly) == (allowed || mode == "local-changed" ? 0 : FileAttributes.ReadOnly),
                        "保护总装及尚未通过核对的零件：" + mode);
                    Console.WriteLine("PASS: 从总装按所选零件获取权限：" + mode);
                }
            }
        }
        finally { resolver.SetValue(null, originalResolver); bindings.SetValue(null, originalBindings); }
    }
}
