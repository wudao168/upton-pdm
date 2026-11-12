using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static void PlmBackupFailureScenario()
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var root = Path.Combine(Path.GetTempPath(), "pdm-backup-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bindings = Type("PdmDocumentIdentityStore").GetField("bindingDirectory", Flags);
        var previousBindings = bindings.GetValue(null);
        var resolver = Type("PdmDocumentIdentityStore").GetField("workspaceRootResolver", Flags);
        var previousRoot = resolver.GetValue(null);
        bindings.SetValue(null, Path.Combine(root, "bindings"));
        resolver.SetValue(null, new Func<string>(() => root));
        try
        {
            foreach (var dirty in new[] { false, true })
            foreach (var scope in new[] { "unbound", "target", "other-project", "other-document" })
            {
                var caseDirectory = Path.Combine(root, "View", scope + "-" + dirty); Directory.CreateDirectory(caseDirectory);
                var path = Path.Combine(caseDirectory, "PART.SLDPRT");
                File.WriteAllText(path, "saved original");
                var closes = 0;
                var calls = new System.Collections.Generic.List<string>();
                var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.IModelDocExtension"), call =>
                {
                    if (call.MethodName == "SaveAs") return false;
                    throw new InvalidOperationException("Unexpected extension call: " + call.MethodName);
                }).GetTransparentProxy();
                var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2"), call =>
                {
                    calls.Add("model." + call.MethodName);
                    switch (call.MethodName)
                    {
                        case "GetPathName": return path;
                        case "GetTitle": return "PART.SLDPRT";
                        case "GetType": return 1;
                        case "GetSaveFlag": return dirty;
                        case "SetSaveFlag": return null;
                        case "GetHashCode": return 1;
                        case "Equals": return true;
                        case "get_Extension": return extension;
                        default: throw new InvalidOperationException("Unexpected model call: " + call.MethodName);
                    }
                }).GetTransparentProxy();
                var app = new Proxy(appType, call =>
                {
                    calls.Add("app." + call.MethodName);
                    switch (call.MethodName)
                    {
                        case "GetDocuments": return new[] { model };
                        case "GetOpenDocumentByName": return model;
                        case "CloseDoc": closes++; return null;
                        default: throw new InvalidOperationException("Unexpected application call: " + call.MethodName);
                    }
                }).GetTransparentProxy();
                var addin = New("PdmAddin"); SetField(addin, "application", app);
                var manifest = New("ControlledOpenManifestDto"); var file = New("ControlledOpenFileDto");
                var project = Guid.NewGuid(); var document = Guid.NewGuid();
                Set(manifest, "ProjectId", project); Set(file, "DocumentId", document);
                if (scope != "unbound") Require((bool)Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion", Flags)
                    .Invoke(null, new object[] { path, scope == "other-document" ? Guid.NewGuid() : document,
                        scope == "other-project" ? Guid.NewGuid() : project, Guid.NewGuid(), "W1",
                        Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { path }), new FileInfo(path).Length }), "fixture binding failed");
                Set(file, "FileName", "PART.SLDPRT");
                var files = (IList)Activator.CreateInstance(manifest.GetType().GetProperty("Files").PropertyType);
                files.Add(file); Set(manifest, "Files", files);
                var blocked = false;
                try { Invoke(addin, "CloseLocalContextsForPlmOpen", manifest, null); }
                catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                { blocked = error.InnerException.Message.Contains("同名图档") && error.InnerException.Message.Contains("保留"); }
                var shouldBlock = scope != "target";
                Require(blocked == shouldBlock && closes == (shouldBlock ? 0 : 1),
                    "PLM retrieval must honor target overwrite authorization and retain unrelated unsaved windows. scope=" + scope + "; dirty=" + dirty + "; closes=" + closes + "; calls=" + string.Join(",", calls));
                Require(File.ReadAllText(path) == "saved original", "PLM retrieval changed original disk file");
            }
            Console.WriteLine("PASS: eight identity-policy cases; authorized target proceeds, unrelated/unbound windows stay, original disk bytes retained");
        }
        finally { bindings.SetValue(null, previousBindings); resolver.SetValue(null, previousRoot); Directory.Delete(root, true); }
    }

    static async Task PlmAuthorityScenario()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-plm-authority", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Type("PdmDocumentIdentityStore").GetMethod("ConfigureBrowserReadScope", Flags).Invoke(null, new object[] { root });
        var id = Guid.NewGuid(); var project = Guid.NewGuid(); var version = Guid.NewGuid();
        var manifest = New("ControlledOpenManifestDto"); var file = New("ControlledOpenFileDto");
        Set(manifest, "Id", Guid.NewGuid()); Set(manifest, "ProjectId", project); Set(manifest, "ProjectCode", "QA-AUTHORITY");
        Set(manifest, "RootDocumentId", id); Set(manifest, "RootVersionId", version); Set(manifest, "RootRelativePath", "PART.SLDPRT");
        Set(file, "DocumentId", id); Set(file, "VersionId", version); Set(file, "FileName", "PART.SLDPRT");
        Set(file, "RelativePath", "PART.SLDPRT"); Set(file, "Revision", "W1"); Set(file, "IsRoot", true);
        var baseline = System.Text.Encoding.UTF8.GetBytes("PLM authoritative archived bytes");
        var seed = Path.Combine(root, "server-content"); File.WriteAllBytes(seed, baseline);
        var hash = (string)Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { seed });
        Set(file, "Sha256", hash); Set(file, "FileLength", (long)baseline.Length);
        var files = (IList)Activator.CreateInstance(manifest.GetType().GetProperty("Files").PropertyType); files.Add(file); Set(manifest, "Files", files);
        using (var handler = new DownloadHandler(baseline))
        using (var client = (IDisposable)New("PdmApiClient", "http://authority-qa.invalid/"))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://authority-qa.invalid/") })
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "isolated");
            foreach (var field in new[] { "httpClient", "controlledOpenHttpClient" })
            { ((HttpClient)Field(client, field)).Dispose(); SetField(client, field, http); }
            var workspace = New("ControlledWorkspaceManager", client, root);
            async Task<string> Fresh(bool history = false)
            {
                var task = (Task)Invoke(workspace, history ? "PrepareHistoricalDerivedCopyAsync" : "PreparePlmWorkingCopyAsync",
                    history ? new object[] { manifest, CancellationToken.None } : new object[] { manifest, new[] { id }, CancellationToken.None });
                await task; return (string)Get(task, "Result");
            }
            var old = (string)Invoke(workspace, "GetWorkingFilePath", manifest, id);
            Directory.CreateDirectory(Path.GetDirectoryName(old)); File.WriteAllText(old, "unarchived local edit");
            var lockedRejected = false;
            using (var locked = new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { await Fresh(); }
                catch (IOException) { lockedRejected = true; }
            }
            Require(lockedRejected && File.ReadAllText(old) == "unarchived local edit", "Locked modified file was replaced or bypassed without confirmation");
            async Task<string> ConfirmBackupAndRetrieve(string expectedLocal)
            {
                var conflict = Invoke(workspace, "FindWorkingConflicts", manifest, new[] { id });
                Require(conflict != null, "Unarchived local changes were not identified");
                var rejected = false;
                try { await Fresh(); }
                catch (Exception error) when (error.GetType().Name == "WorkspaceCacheConflictException") { rejected = true; }
                Require(rejected && File.ReadAllText(old) == expectedLocal, "Acquisition overwrote local changes without confirmation");
                var backup = Invoke(workspace, "BackupWorkingConflicts", manifest, conflict, CancellationToken.None);
                var recovery = (string)Get(backup, "RecoveryDirectory");
                var task = (Task)Invoke(workspace, "PrepareBackedUpWorkingCopyAsync", manifest, new[] { id }, backup, CancellationToken.None);
                await task;
                var restored = (string)Get(task, "Result");
                Require(restored == old && File.ReadAllBytes(restored).SequenceEqual(baseline), "Confirmed retrieval did not reuse the managed workspace");
                Require(File.ReadAllText(Path.Combine(recovery, "PART.SLDPRT")) == expectedLocal, "Confirmed retrieval lost the verified recovery backup");
                return restored;
            }
            var first = await ConfirmBackupAndRetrieve("unarchived local edit");
            Require(await Fresh() == first, "Unchanged checkout created another workspace");
            Console.WriteLine("PASS: locked or modified working files remain protected; confirmed verified backup permits retrieval in the same workspace");

            File.WriteAllText(first, "local saved modification"); File.SetAttributes(first, FileAttributes.ReadOnly);
            var second = await ConfirmBackupAndRetrieve("local saved modification");
            File.Delete(second);
            var third = await Fresh();
            Require(third == second && File.ReadAllBytes(third).SequenceEqual(baseline), "Missing working file was not repaired in the managed workspace");
            Console.WriteLine("PASS: read-only changes require verified backup; missing working files are repaired without another workspace");

            var read = (Task)Invoke(workspace, "PrepareReadOnlyAsync", manifest, false, CancellationToken.None); await read;
            var cached = (string)Get(read, "Result"); File.SetAttributes(cached, FileAttributes.Normal); File.WriteAllText(cached, "corrupt cache");
            using (var locked = new FileStream(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var freshRead = (Task)Invoke(workspace, "PrepareReadOnlyAsync", manifest, false, CancellationToken.None); await freshRead;
                var fourth = (string)Get(freshRead, "Result");
                Require(File.ReadAllBytes(fourth).SequenceEqual(baseline), "Corrupt locked snapshot prevented a new server download");
                var request = New("WorkspaceDocumentStateRequest"); Set(request, "DocumentId", id); Set(request, "FileName", "PART.SLDPRT"); Set(request, "LatestRevision", "W1");
                var requests = Array.CreateInstance(request.GetType(), 1); requests.SetValue(request, 0);
                var state = Type("WorkspaceLocalStateReader").GetMethod("Read", Flags).Invoke(null,
                    new object[] { root, project, "QA-AUTHORITY", "qa", requests, null });
                var current = ((IEnumerable)Get(state, "Items")).Cast<object>().Single();
                Require((string)Get(current, "FullPath") != cached, "Corrupt locked copy displaced verified content in workspace UI");
            }
            Console.WriteLine("PASS: corrupt locked snapshot is bypassed; workspace selects latest server copy rather than old modifications");

            var historical = await Fresh(true); File.SetAttributes(historical, FileAttributes.Normal); File.WriteAllText(historical, "previous derived edit");
            var nextHistorical = await Fresh(true);
            Require(nextHistorical != historical && File.ReadAllBytes(nextHistorical).SequenceEqual(baseline), "Old historical edit prevented historical server retrieval");
            Console.WriteLine("PASS: historical derivation starts from selected PLM version without depending on prior local derived directory");

            var node = New("CadTreeNode"); Set(node, "DocumentId", id); Set(node, "Kind", Enum.ToObject(Get(node, "Kind").GetType(), 1));
            Set(node, "IsLatestReadOnlyPreview", true);
            Require((bool)Type("BatchOperationDialog").GetMethod("CanAcquire", Flags).Invoke(null, new[] { node }), "Registered preview without local path was excluded from batch acquire");
            Require(!(bool)Type("BatchOperationDialog").GetMethod("CanSelectForCheckIn", Flags).Invoke(null, new object[] { node, "qa" }), "Read-only preview incorrectly became eligible for check-in");
            var other = New("CadTreeNode"); Set(other, "DocumentId", Guid.NewGuid());
            Require((string)Type("BatchOperationDialog").GetMethod("SelectionKey", Flags).Invoke(null, new[] { node })
                != (string)Type("BatchOperationDialog").GetMethod("SelectionKey", Flags).Invoke(null, new[] { other }), "Missing-path document selection identities collide");
            Console.WriteLine("PASS: registered missing-path preview remains selectable for retrieval; check-in remains protected and selections retain document identity");
        }
        // Only generated test files under this unique temporary root are removed.
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
