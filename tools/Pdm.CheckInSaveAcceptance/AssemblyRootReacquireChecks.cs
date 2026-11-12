using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static async Task AssemblyRootReacquireScenario(string output)
    {
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var bindings = identity.GetField("bindingDirectory", Flags);
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var oldBindings = bindings.GetValue(null);
        var oldResolver = resolver.GetValue(null);
        bindings.SetValue(null, Path.Combine(output, "bindings"));
        resolver.SetValue(null, new Func<string>(() => output));
        var json = new JavaScriptSerializer();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        try
        {
            foreach (var mode in new[] { "committed", "later-memory-edit", "later-disk-edit", "new-server-head", "server-denies", "changes-during-checkout", "locked-file" })
            {
                var directory = Path.Combine(output, "View", "QA", mode);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "ROOT.SLDASM");
                var removed = Path.Combine(directory, "REMOVED.SLDPRT");
                var copyPath = Path.Combine(output, mode + "-submitted.SLDASM");
                File.WriteAllText(path, "prior controlled assembly");
                File.WriteAllText(removed, "prior referenced part kept locally");
                File.WriteAllText(copyPath, "confirmed stored assembly after removing a component");
                var oldHash = WorkingReuseHash(path);
                var copyHash = WorkingReuseHash(copyPath);
                var project = Guid.NewGuid();
                var id = Guid.NewGuid();
                var oldVersion = Guid.NewGuid();
                var newVersion = Guid.NewGuid();
                var latestVersion = mode == "new-server-head" ? Guid.NewGuid() : newVersion;
                Check((bool)identity.GetMethod("TryWriteControlledVersion", Flags).Invoke(null,
                    new object[] { path, id, project, oldVersion, "V1", oldHash, new FileInfo(path).Length }), mode + "：建立旧版工作区基准");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                object FileEntry(Guid document, Guid version, string local, string name, bool root) => new {
                    documentId = document, versionId = version, fileName = name, relativePath = name, configuration = "Default",
                    isRoot = root, revision = root && version != oldVersion ? "V2" : "V1", fileLength = new FileInfo(local).Length, sha256 = WorkingReuseHash(local) };
                var before = FileEntry(id, oldVersion, path, "ROOT.SLDASM", true);
                var after = FileEntry(id, latestVersion, copyPath, "ROOT.SLDASM", true);
                var omitted = FileEntry(Guid.NewGuid(), Guid.NewGuid(), removed, "REMOVED.SLDPRT", false);
                var owner = new { projectId = project, projectCode = "QA", rootDocumentId = id, rootVersionId = oldVersion,
                    rootRelativePath = "ROOT.SLDASM", rootRevision = "V1", files = new[] { before, omitted } };
                var latest = new { projectId = project, projectCode = "QA", rootDocumentId = id, rootVersionId = latestVersion,
                    rootRelativePath = "ROOT.SLDASM", rootRevision = "V2", forEdit = true, files = new[] { after } };
                var version = new { id = newVersion, documentId = id, sha256 = copyHash, fileLength = new FileInfo(copyPath).Length,
                    referenceSnapshot = new { documentId = id, fileName = "ROOT.SLDASM", configuration = "Default", kind = 0, children = new object[0] } };
                var plugin = New("PdmAddin");
                SetField(plugin, "authenticatedUsername", "tester");
                var session = (Guid)Field(plugin, "checkoutSessionId");
                var events = new List<string>();
                var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
                var native = new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), path, true, events);
                var model = native.GetTransparentProxy();
                SetField(plugin, "application", new Proxy(appType, call => call.MethodName == "GetDocuments" ? new[] { model }
                    : call.MethodName == "get_ActiveDoc" ? model : throw new Exception("不得重新打开图档：" + call.MethodName)).GetTransparentProxy());
                ((IDictionary)Field(plugin, "controlledOpenManifests")).Add(directory, json.Deserialize(json.Serialize(owner), Type("ControlledOpenManifestDto")));
                var root = New("CadTreeNode");
                Set(root, "DocumentId", id);
                Set(root, "FullPath", path);
                Set(root, "FileName", "ROOT.SLDASM");
                Set(root, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
                SetField(plugin, "currentTree", root);
                var selected = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));
                selected.Add(New("BatchOperationItem", root, 0));
                var checkouts = 0;
                using (var memory = (IDisposable)New("PdmAddin+ArchiveMemoryCopy", model, path))
                using (var lifetime = new CancellationTokenSource())
                using (var client = (IDisposable)New("PdmApiClient", "http://assembly-reacquire.invalid/"))
                using (var handler = new ReferenceRecoveryHandler(request => {
                    var route = request.RequestUri.AbsolutePath;
                    object response;
                    if (route.EndsWith("/open-manifest")) response = latest;
                    else if (route.EndsWith("/versions")) response = new[] { version };
                    else if (route.EndsWith("/document-operation-rules")) response = new { disabledRuleIds = new string[0] };
                    else if (route.EndsWith("/document-relations")) response = new object[0];
                    else if (route.EndsWith("/documents")) response = new[] { new { id, projectId = project, fileName = "ROOT.SLDASM" } };
                    else if (route.EndsWith("/checkout"))
                    {
                        checkouts++;
                        if (WorkingReuseHash(path) != copyHash) throw new Exception("获取前必须真正同步磁盘基准");
                        if (mode == "server-denies") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"无权限\"}") };
                        if (mode == "changes-during-checkout") native.Modify();
                        response = new { id, projectId = project, fileName = "ROOT.SLDASM", checkedOutBy = "tester", checkoutSessionId = session, checkedOutAt = DateTime.UtcNow };
                    }
                    else throw new Exception("不应请求：" + route);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(response)) };
                }))
                using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://assembly-reacquire.invalid/") })
                {
                    TreeCall(memory, "ReplaceCopy", copyPath, oldHash, new FileInfo(path).Length, copyHash, 1, native.UpdateStamp, 0);
                    SetField(memory, "CommittedVersion", json.Deserialize(json.Serialize(version), Type("DocumentVersionDto")));
                    SetField(memory, "CommittedDocumentId", id);
                    SetField(memory, "CommittedUser", "tester");
                    SetField(memory, "CommittedScope", Get(client, "EditJournalScope"));
                    ((IDictionary)Field(plugin, "archiveMemoryCopies")).Add(path, memory);
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
                    SetField(client, "httpClient", http);
                    SetField(client, "controlledOpenHttpClient", http);
                    SetField(plugin, "apiClient", client);
                    SetField(plugin, "lifetime", lifetime);
                    var scope = (string)Get(client, "EditJournalScope") + "|tester";
                    SetField(plugin, "editJournal", New("EditDraftJournal", Path.Combine(output, mode, "journal"), scope));
                    SetField(plugin, "editJournalScope", scope);
                    if (mode == "later-memory-edit") native.Modify(false);
                    if (mode == "later-disk-edit")
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.AppendAllText(path, "new local change");
                        File.SetAttributes(path, FileAttributes.ReadOnly);
                    }
                    var beforeAcquire = WorkingReuseHash(path);
                    bool success = false;
                    using (var fileLock = mode == "locked-file" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                        try
                        {
                            var task = (Task)TreeCall(plugin, "TryAcquireReusableWorkingCopyAsync", selected, project, id);
                            await task;
                            success = (bool)Get(task, "Result");
                        }
                        catch (InvalidOperationException error) when (mode != "committed") { Console.WriteLine("DIAGNOSTIC: " + error.Message); }
                    Check(success == (mode == "committed"), mode + "：仅确切的服务器已存档状态允许原窗口获取");
                    var syncExpected = mode == "committed" || mode == "server-denies" || mode == "changes-during-checkout";
                    Check(checkouts == (syncExpected ? 1 : 0) && WorkingReuseHash(path) == (syncExpected ? copyHash : beforeAcquire),
                        mode + "：申请前同步实际文件，状态不符时保留原文件");
                    Check(native.ReadOnly == !success && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly) == !success,
                        mode + "：只有成功获取后才解除只读");
                    Check(events.Count == 0 && File.ReadAllText(removed) == "prior referenced part kept locally", mode + "：保留原窗口及旧结构中移除的本机文件");
                    if (success) Check(Equals(Get(((IDictionary)Field(plugin, "localEdits"))[id], "BaseVersionId"), newVersion),
                        "原窗口再次获取后的恢复记录采用服务器最新版本");
                }
            }
        }
        finally { bindings.SetValue(null, oldBindings); resolver.SetValue(null, oldResolver); }
    }
}
