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
                var reproduced = false;
                try
                {
                    foreach (var selected in roots)
                    {
                        var args = new object[] { selected, scope, null, CancellationToken.None };
                        if (sequential.GetParameters().Length == 5) args = args.Concat(new object[] { null }).ToArray();
                        ((Task)sequential.Invoke(client, args)).GetAwaiter().GetResult();
                    }
                }
                catch (InvalidOperationException) { reproduced = true; }
                Assert(reproduced && handler.Reads == 1, "legacy per-file capture did not reproduce reference-version loss");
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
