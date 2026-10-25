using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    private static void TestRootRevisionDisplay()
    {
        var capture = Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot", Members);
        foreach (var mode in new[] { "A*", "W2*", "A-W1*", "bound-modified", "opened-modified", "wrong-binding", "unknown" })
        {
            var root = Node("REVISION-" + mode + ".SLDPRT", Guid.NewGuid());
            var path = (string)Get(root, "FullPath");
            // Windows filenames cannot contain the UI's modification star.
            path = path.Replace("*", "-star"); Set(root, "FullPath", path); Set(root, "FileName", System.IO.Path.GetFileName(path));
            System.IO.File.WriteAllText(path, "saved model bytes");
            Set(root, "Revision", mode.EndsWith("*") ? mode : "本地修改");
            var id = (Guid)Get(root, "DocumentId"); var version = Guid.NewGuid();
            if (mode == "bound-modified" || mode == "wrong-binding")
                Assert((bool)Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion", Members).Invoke(null,
                    new object[] { path, mode == "wrong-binding" ? Guid.NewGuid() : id, Guid.NewGuid(), version, "A", new string('A', 64), 17L }), "fixture binding missing");
            if (mode == "opened-modified") { Set(root, "OpenedVersionId", version); Set(root, "OpenedRevision", "A"); }
            var failed = false;
            try
            {
                var snapshot = capture.Invoke(null, new object[] { root, new[] { path } });
                var frozen = (Dictionary<string, object>)Call(snapshot, "CreateRoot");
                Assert(frozen["revision"] != null, "recorded revision disappeared");
                var revision = (Dictionary<string, object>)frozen["revision"];
                Assert(Convert.ToInt32(revision["workIteration"]) == (mode == "W2*" ? 2 : mode == "A-W1*" ? 1 : 0)
                    && Convert.ToString(revision["baseRevision"]) == (mode == "W2*" ? "" : "A"), "recorded root revision was substituted");
                Assert((string)Get(root, "Revision") == (mode.EndsWith("*") ? mode : "本地修改"), "snapshot capture rewrote UI state");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { failed = true; }
            Assert(failed == (mode == "wrong-binding" || mode == "unknown"), "incorrect root revision fallback: " + mode);
        }
    }

    private static void TestPendingNativeInventory()
    {
        var root = Node("PENDING-ROOT.SLDASM", Guid.NewGuid(), 0);
        var unknown = Node("UNREAD-OCCURRENCE.SLDASM", null, 0);
        var part = Node("KNOWN-PART.SLDPRT", Guid.NewGuid());
        foreach (var node in new[] { root, unknown, part })
        {
            Set(node, "NativeFileScopeVerified", true);
            Set(node, "Revision", "W1"); Set(node, "CurrentRevision", "W1");
        }
        Children(root).Add(unknown); Children(unknown).Add(part);
        var paths = new HashSet<string>(new[] { (string)Get(root, "FullPath"), (string)Get(part, "FullPath") }, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) System.IO.File.WriteAllText(path, "native bytes");
        Set(root, "NativeFileScopePaths", paths);
        var capture = Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot", Members);
        var snapshot = capture.Invoke(null, new object[] { root, paths.ToArray() });
        Assert((bool)Get(snapshot, "HasPendingStructureIndex"), "incomplete occurrence was reported complete");
        var frozen = (Dictionary<string, object>)Call(snapshot, "CreateRoot");
        var children = ((object[])frozen["children"]).Cast<Dictionary<string, object>>().ToArray();
        Assert(children.Any(child => (string)child["documentId"] == Get(part, "DocumentId").ToString()), "known native child was dropped with unread occurrence");
        Assert(!children.Any(child => (string)child["fileName"] == "UNREAD-OCCURRENCE.SLDASM"), "unknown index was guessed");
        var unregistered = Node("UNREGISTERED.SLDPRT", null);
        Set(unregistered, "NativeFileScopeVerified", true);
        var unregisteredPath = (string)Get(unregistered, "FullPath");
        System.IO.File.WriteAllText(unregisteredPath, "real unregistered bytes"); paths.Add(unregisteredPath);
        Children(root).Add(unregistered);
        var rejected = false;
        try { capture.Invoke(null, new object[] { root, paths.ToArray() }); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
        Assert(rejected, "actual native bytes without vault identity silently disappeared");
    }

    private static void TestFrozenBatchReferences()
    {
        foreach (var changedFiles in new[] { 4, 7 })
        {
            var root = Node("ROOT-" + changedFiles + ".SLDASM", Guid.NewGuid(), 0);
            var sub = Node("SUB-" + changedFiles + ".SLDASM", Guid.NewGuid(), 0);
            Children(root).Add(sub);
            var references = new List<object>();
            var partId = Guid.NewGuid();
            for (var i = 1; i <= 9; i++)
            {
                var part = Node("FGB280X40X3.3-UPTON01.SLDPRT", partId);
                Set(part, "CurrentRevision", "W1"); Set(part, "Revision", "W1");
                Set(part, "Configuration", i == 9 ? "Alternate" : "默认");
                Set(part, "InstancePath", "ROOT/SUB/SUB/FGB-" + i);
                Children(sub).Add(part); references.Add(part);
            }
            var roots = new List<object> { root, sub };
            while (roots.Count < changedFiles) roots.Add(Node("CHANGED-" + roots.Count + ".SLDPRT", Guid.NewGuid()));
            var bases = roots.ToDictionary(node => (Guid)Get(node, "DocumentId"), _ => Guid.NewGuid());
            foreach (var node in roots) { Set(node, "Revision", "W1"); Set(node, "CurrentRevision", "W1"); Set(node, "OpenedVersionId", bases[(Guid)Get(node, "DocumentId")]); }
            var plugin = New("PdmAddin");
            using (var handler = new SnapshotHistoryHandler(bases, () => references.ForEach(node => Set(node, "CurrentRevision", ""))))
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                ((HttpClient)Field(client, "httpClient")).Dispose();
                client.GetType().GetField("httpClient", Members).SetValue(client, new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
                plugin.GetType().GetField("apiClient", Members).SetValue(plugin, client);
                var sequential = Type("PdmApiClient").GetMethod("PrepareArchiveSnapshotAsync", Members);
                var scope = roots.Select(node => (string)Get(node, "FullPath")).ToArray();
                var missingVersionDeferred = false;
                {
                    foreach (var selected in roots)
                    {
                        var args = new object[] { selected, scope, null, CancellationToken.None };
                        if (sequential.GetParameters().Length == 5) args = args.Concat(new object[] { null }).ToArray();
                        var task = (Task)sequential.Invoke(client, args);
                        task.GetAwaiter().GetResult();
                        var snapshot = task.GetType().GetProperty("Result").GetValue(task);
                        var payload = new JavaScriptSerializer().Serialize(Call(snapshot, "CreateRoot"));
                        if (payload.Contains("\"revision\":null")) missingVersionDeferred = true;
                    }
                }
                Assert(missingVersionDeferred && handler.Reads == roots.Count && handler.Writes == 0,
                    "missing local reference metadata was not deferred to authoritative server preflight");
                foreach (var reference in references) Set(reference, "CurrentRevision", "W1");
                handler.Reads = 0;
                var preparedType = Type("PdmAddin").GetNestedType("PreparedArchiveFile", Members);
                var prepared = Array.CreateInstance(preparedType, roots.Count);
                for (var i = 0; i < roots.Count; i++)
                    prepared.SetValue(Activator.CreateInstance(preparedType, Members, null,
                        new object[] { roots[i], new Dictionary<string, string>(), "", "", null, null }, null), i);
                var method = Type("PdmAddin").GetMethod("PrepareArchiveSnapshotsAsync", Members);
                if (method == null) throw new Exception("Legacy plugin freezes one file at a time; it cannot protect the confirmed batch");
                ((Task)method.Invoke(plugin, new object[] { prepared, scope, CancellationToken.None })).GetAwaiter().GetResult();
                Assert(handler.Reads == roots.Count, "base versions queried repeatedly");
                Assert(handler.Writes == 0, "freezing snapshots performed a write");
                foreach (var file in prepared)
                {
                    var snapshot = Get(file, "ReferenceSnapshot");
                    var node = Get(file, "Node");
                    Assert((Guid)Get(snapshot, "ExpectedVersionId") == bases[(Guid)Get(node, "DocumentId")], "base revision changed");
                    var frozen = (Dictionary<string, object>)Call(snapshot, "CreateRoot");
                    var pending = new Stack<Dictionary<string, object>>(); pending.Push(frozen); var count = 0;
                    while (pending.Count > 0)
                    {
                        var current = pending.Pop();
                        if ((string)current["fileName"] == "FGB280X40X3.3-UPTON01.SLDPRT")
                        {
                            count++;
                            Assert(current["revision"] is Dictionary<string, object> revision && Convert.ToInt32(revision["workIteration"]) == 1,
                                "an unchanged repeated instance lost W1 after metadata refresh");
                        }
                        foreach (var child in (object[])current["children"]) pending.Push((Dictionary<string, object>)child);
                    }
                    if (ReferenceEquals(node, root) || ReferenceEquals(node, sub)) Assert(count == 9, "repeated instances were dropped");
                    Assert(!((IEnumerable)Get(snapshot, "PlannedDocumentIds")).Cast<object>().Any(id => (Guid)id == partId), "unchanged component was added to upload/version scope");
                }
            }
        }
    }

    private sealed class SnapshotHistoryHandler : HttpMessageHandler
    {
        private readonly Dictionary<Guid, Guid> bases;
        private readonly Action refresh;
        public int Reads; public int Writes;
        public SnapshotHistoryHandler(Dictionary<Guid, Guid> bases, Action refresh) { this.bases = bases; this.refresh = refresh; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("unexpected write"); }
            Reads++; if (Reads == 1) refresh();
            var id = Guid.Parse(request.RequestUri.Segments[3].Trim('/'));
            var json = new JavaScriptSerializer().Serialize(new[] { new { id = bases[id], revision = new { display = "W1" }, sha256 = "baseline" } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
