using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static async Task DrawingArchiveSaveScenario(string output)
    {
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var rootResolver = identity.GetField("workspaceRootResolver", Flags);
        var bindingDirectory = identity.GetField("bindingDirectory", Flags);
        var previousRoot = rootResolver.GetValue(null);
        var previousBinding = bindingDirectory.GetValue(null);
        rootResolver.SetValue(null, new Func<string>(() => output));
        bindingDirectory.SetValue(null, Path.Combine(output, "bindings"));
        try
        {
            foreach (var mode in new[] { "current", "read-only", "revoked", "expired", "wrong-project", "newer-server-version", "later-edit",
                "assembly", "read-only-assembly", "part", "read-only-part" })
            {
                var folder = Path.Combine(output, "View", mode);
                Directory.CreateDirectory(folder);
                var kind = mode.EndsWith("assembly", StringComparison.Ordinal) ? "Assembly"
                    : mode.EndsWith("part", StringComparison.Ordinal) ? "Part" : "Drawing";
                var fileName = kind == "Assembly" ? "总装.SLDASM" : kind == "Part" ? "零件.SLDPRT" : "零件工程图.SLDDRW";
                var node = LinkedNode(folder, fileName, kind);
                var path = (string)Get(node, "FullPath");
                Set(node, "RequiresModelUpdate", true);
                var id = (Guid)Get(node, "DocumentId");
                var version = LinkedVersion(node, LinkedSnapshot(node));
                var project = Guid.NewGuid();
                var plugin = New("PdmAddin");
                var session = (Guid)Field(plugin, "checkoutSessionId");
                SetField(plugin, "authenticatedUsername", "tester");
                Set(node, "CheckoutSessionId", session);
                Set(node, "CheckedOutBy", "tester");
                var oldVersionId = (Guid)Get(version, "Id");
                var sourceHash = WorkingReuseHash(path);
                TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion",
                    path, id, project, oldVersionId, "W1", sourceHash, new FileInfo(path).Length), "fixture binding");
                var modelType = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly
                    .GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
                var events = new List<string>();
                var native = new Model(modelType, path, true, events, mode.StartsWith("read-only", StringComparison.Ordinal) || mode == "revoked");
                if (native.ReadOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
                native.OnSave = () => File.WriteAllText(path, "selected drawing with rebuilt views");
                var model = native.GetTransparentProxy();
                var copy = New("PdmAddin+ArchiveMemoryCopy", model, path);
                var snapshotDirectory = Path.Combine(output, "snapshots", mode);
                Directory.CreateDirectory(snapshotDirectory);
                var copyPath = Path.Combine(snapshotDirectory, Path.GetFileName(path));
                File.WriteAllText(copyPath, "frozen drawing");
                TreeCall(copy, "ReplaceCopy", copyPath, sourceHash, new FileInfo(path).Length,
                    WorkingReuseHash(copyPath), 1, native.UpdateStamp, 0);
                ((IDictionary)Field(plugin, "archiveMemoryCopies")).Add(path, copy);
                var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
                SetField(plugin, "application", new Proxy(appType, call => call.MethodName == "GetDocuments"
                    ? new[] { model } : throw new Exception("不得关闭、重载、解锁或保存其他图档：" + call.MethodName)).GetTransparentProxy());
                if (mode == "newer-server-version") Set(version, "Id", Guid.NewGuid());
                var json = new JavaScriptSerializer();
                using (var client = (IDisposable)New("PdmApiClient", "http://drawing-archive.invalid/"))
                using (var handler = new ReferenceRecoveryHandler(request =>
                {
                    if (request.Method != HttpMethod.Get) throw new Exception("准备阶段不得发送写请求");
                    if (request.RequestUri.AbsolutePath.EndsWith("/versions"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { version })) };
                    if (request.RequestUri.AbsolutePath != "/api/documents/" + id) throw new Exception(request.RequestUri.AbsolutePath);
                    if (mode == "later-edit") native.Modify(false);
                    var document = new { id, projectId = mode == "wrong-project" ? Guid.NewGuid() : project,
                        checkedOutBy = "tester", checkoutSessionId = session, drawingReviewLocked = false,
                        checkoutLeaseExpiresAt = DateTime.UtcNow.AddMinutes(mode == "expired" ? -5 : 5),
                        operationAccess = new { canCheckout = true, canArchive = mode != "revoked" } };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(document)) };
                }))
                using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://drawing-archive.invalid/") })
                {
                    SetField(client, "httpClient", http);
                    SetField(plugin, "apiClient", client);
                    try
                    {
                        Exception rejected = null;
                        try { await (Task)TreeCall(plugin, "SaveConfirmedWorkingSourcesForArchiveAsync", LinkedItems(node), project, CancellationToken.None); }
                        catch (InvalidOperationException error) { rejected = error; }
                        var saved = mode == "current" || mode == "read-only" || mode == "assembly" || mode == "part";
                        TreeCheck(events.SequenceEqual(saved ? new[] { "save:" + fileName } : Array.Empty<string>())
                            && native.Dirty != saved && (WorkingReuseHash(path) != sourceHash) == saved,
                            "只保存具有有效授权和一致基准的当前工程图：" + mode);
                        TreeCheck((rejected != null) == (mode == "revoked" || mode == "expired" || mode == "wrong-project" || mode == "later-edit"),
                            "权限或新修改拦截：" + mode);
                        if (mode == "revoked")
                            TreeCheck(native.ReadOnly && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "权限被撤销时保持窗口与文件只读");
                        if (saved)
                        {
                            var hash = WorkingReuseHash(path);
                            TreeCheck(TreeCall(plugin, "MemoryCopy", node) == null
                                && (bool)TreeCall(plugin, "SavedArchiveSourceMatches", node, hash)
                                && (kind != "Drawing" || (bool)TreeCall(plugin, "CanArchiveSavedDrawingBytes", node, false, hash)),
                                "已保存的受控原文件按磁盘字节归档");
                            await (Task)TreeCall(plugin, "EnsureBatchCheckInSavedStateAsync", LinkedItems(node), project, CancellationToken.None);
                            TreeCheck(TreeCall(plugin, "MemoryCopy", node) == null, "后续准备不再将已保存的工程图退回内存副本");
                            var newVersion = LinkedVersion(node, LinkedSnapshot(node));
                            TreeCallStatic("PdmAddin", "RememberControlledVersionIdentity", path, id, project, newVersion);
                            var manifestFile = New("ControlledOpenFileDto");
                            Set(manifestFile, "DocumentId", id);
                            Set(manifestFile, "FileName", Path.GetFileName(path));
                            Set(manifestFile, "VersionId", Get(newVersion, "Id"));
                            Set(manifestFile, "Sha256", hash);
                            Set(manifestFile, "FileLength", new FileInfo(path).Length);
                            TreeCheck((bool)TreeCallStatic("PdmAddin", "WorkingReuseBindingMatches", path, project, manifestFile),
                                "存档后的本地绑定通过3D配套工程图再次获取核对");
                            TreeCheck((bool)TreeCallStatic("PdmAddin", "VersionMatchesLocalFile", newVersion, path, hash)
                                && !native.Dirty, "2D为已保存的同版本，可继续直接获取");
                        }
                        Console.WriteLine("PASS: 已确认图档的单件保存与基准：" + mode);
                    }
                    finally { TreeCall(plugin, "PruneArchiveMemoryCopies", true); }
                }
            }
        }
        finally { rootResolver.SetValue(null, previousRoot); bindingDirectory.SetValue(null, previousBinding); }
    }
}
