using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly assembly;
    [STAThread]
    static int Main(string[] args)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => Assembly.LoadFrom(Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll"));
        assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        try
        {
            if (args.Length > 1) { SourceWriteLock(args[1] == "--expect-file-lock"); return 0; }
            Scenario(false, false).GetAwaiter().GetResult();
            Scenario(true, false).GetAwaiter().GetResult();
            Scenario(true, true).GetAwaiter().GetResult();
            MissingSourceRecovery();
            BatchCloseRecovery().GetAwaiter().GetResult();
            FirstArchiveFailureRemainsEditable().GetAwaiter().GetResult();
            EquivalentDraftBase();
            return 0;
        }
        catch (Exception exception) { Console.WriteLine(exception); return 1; }
    }

    static void EquivalentDraftBase()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-draft-base-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "part.SLDPRT");
            File.WriteAllText(source, "unchanged released model");
            var hash = (string)Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { source });
            var journal = New("EditDraftJournal", root, "promotion");
            var entry = New("LocalEditDraft");
            Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "SessionId", Guid.NewGuid());
            Set(entry, "SourcePath", source); Set(entry, "BaseVersionId", Guid.NewGuid()); Set(entry, "BaseSha256", hash);
            var snapshot = Path.Combine(root, "snapshot.SLDPRT");
            File.WriteAllText(snapshot, "local unsaved feature change");
            Invoke(journal, "Preserve", entry, snapshot, true);
            var formalVersion = Guid.NewGuid();
            Require((bool)Invoke(journal, "Restore", entry, source, formalVersion, hash), "Content-equivalent formal version blocked draft restore");
            Require(File.ReadAllText(source) == "local unsaved feature change", "Draft content lost");
            Require((bool)Invoke(journal, "HasVerifiedDraft", entry), "Recovery copy removed");
            var separateWorkspace = Path.Combine(root, "standalone-part.SLDPRT");
            File.WriteAllText(separateWorkspace, "unchanged released model");
            Set(entry, "DiskSha256", Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { source }));
            Require((bool)Invoke(journal, "Restore", entry, separateWorkspace, formalVersion, hash), "Same document could not recover from assembly workspace into standalone workspace");
            Require(File.ReadAllText(separateWorkspace) == "local unsaved feature change" && File.ReadAllText(source) == "local unsaved feature change", "Workspace switch lost either copy");
            File.WriteAllText(separateWorkspace, "different local edits");
            var localConflict = false;
            try { Invoke(journal, "Restore", entry, separateWorkspace, formalVersion, hash); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { localConflict = true; }
            Require(localConflict && File.ReadAllText(separateWorkspace) == "different local edits", "Workspace switch overwrote unrelated local modifications");
            var blocked = false;
            try { Invoke(journal, "Restore", entry, source, Guid.NewGuid(), new string('F', 64)); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { blocked = true; }
            Require(blocked && File.ReadAllText(source) == "local unsaved feature change", "Different server contents overwrote draft");
            Require(!(bool)Type("EditDraftJournal").GetMethod("MatchesBase", Flags).Invoke(null,
                new object[] { entry, Guid.NewGuid(), null }), "Unknown server hash allowed rebase");
            Console.WriteLine("PASS: equal-content W-to-formal baseline restores draft; content change and unknown baseline preserve both files");
        }
        finally { Directory.Delete(root, true); }
    }

    static void MissingSourceRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-session-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "missing.SLDPRT");
        File.WriteAllText(source, "base");
        var plugin = New("PdmAddin");
        var entry = New("LocalEditDraft");
        Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "ProjectId", Guid.NewGuid());
        Set(entry, "SessionId", Field(plugin, "checkoutSessionId")); Set(entry, "SourcePath", source);
        Set(entry, "BaseSha256", Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { source }));
        Set(entry, "ObservedOpen", true); Set(entry, "ProcessId", int.MaxValue);
        var journal = New("EditDraftJournal", root, "missing-source");
        Invoke(journal, "Save", entry);
        SetField(plugin, "editJournal", journal);
        ((IDictionary)Field(plugin, "localEdits")).Add(Get(entry, "DocumentId"), entry);
        EmptyApplication(plugin);
        File.Delete(source);
        for (var index = 0; index < 100; index++)
            Require(((ICollection)Invoke(plugin, "PrepareClosedEdits")).Count == 0, "Missing source was queued for release");
        Require((bool)Get(entry, "RecoveryRequired") && (int)Get(entry, "MaintenanceFailureCount") == 1,
            "Missing-source ticks repeatedly attempted maintenance");
        Require(!(bool)Get(entry, "PendingClose") && !(bool)Get(entry, "Closed"), "Missing source was treated as unchanged");
        var restarted = New("PdmAddin");
        SetField(restarted, "editJournal", New("EditDraftJournal", root, "missing-source"));
        EmptyApplication(restarted);
        for (var index = 0; index < 100; index++)
            Require(((ICollection)Invoke(restarted, "PrepareClosedEdits")).Count == 0, "Restart lost missing-source backoff");
        var recovered = ((IEnumerable)Invoke(journal, "ReadAll")).Cast<object>().Single();
        Require((int)Get(recovered, "MaintenanceFailureCount") == 1, "Restart repeated the same failure");
        File.WriteAllText(source, "base");
        Set(recovered, "NextMaintenanceAttemptUtc", DateTime.MinValue); Invoke(journal, "Save", recovered);
        var pending = (ICollection)Invoke(restarted, "PrepareClosedEdits");
        Require(pending.Count == 1 && (bool)Get(pending.Cast<object>().Single(), "PendingClose"), "Restored original path did not resume safely");
        File.SetAttributes(source, FileAttributes.Normal);
        Directory.Delete(root, true);
        Console.WriteLine("PASS: missing source held safely for 100 ticks and restart; original-path recovery resumed");
    }

    static void EmptyApplication(object plugin)
    {
        var appType = plugin.GetType().GetField("application", Flags).FieldType;
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            if (call.MethodName == "GetRunningCommandInfo")
                return new ReturnMessage(null, new object[] { 0, "", false }, 3, call.LogicalCallContext, call);
            if (call.MethodName == "GetDocuments") return Array.Empty<object>();
            throw new Exception("Unexpected application call: " + call.MethodName);
        }).GetTransparentProxy());
    }

    static async Task BatchCloseRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-session-acceptance", Guid.NewGuid().ToString("N"));
        var plugin = New("PdmAddin");
        var journal = New("EditDraftJournal", root, "batch-close");
        SetField(plugin, "editJournal", journal); SetField(plugin, "authenticatedUsername", "tester");
        var entries = Array.CreateInstance(Type("LocalEditDraft"), 100);
        var projectId = Guid.NewGuid();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = New("LocalEditDraft");
            Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "ProjectId", projectId);
            Set(entry, "SessionId", Field(plugin, "checkoutSessionId"));
            Set(entry, "CheckedOutAt", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
            Set(entry, "PendingClose", true); Set(entry, "BaseVersionId", Guid.NewGuid());
            Set(entry, "BaseSha256", "verified-base"); Set(entry, "DiskSha256", "verified-base");
            Invoke(journal, "Save", entry); entries.SetValue(entry, index);
        }
        using (var handler = new Handler(entries.Cast<object>().ToArray()))
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client);
            handler.Fail = true;
            for (var index = 0; index < 100; index++)
                await (Task)Invoke(plugin, "ClosePreservedEditsAsync", entries, CancellationToken.None);
            Require(handler.QueryCalls == 1 && handler.CloseCalls == 0, "Offline batch made more than one project query or released edits");
            entries = (Array)Invoke(New("EditDraftJournal", root, "batch-close"), "ReadAll");
            await (Task)Invoke(plugin, "ClosePreservedEditsAsync", entries, CancellationToken.None);
            Require(handler.QueryCalls == 1, "Reloaded journals lost network backoff");
            handler.Fail = false; handler.HideDocuments = true;
            foreach (var entry in entries) { Set(entry, "NextMaintenanceAttemptUtc", DateTime.MinValue); Invoke(journal, "Save", entry); }
            for (var index = 0; index < 100; index++)
                await (Task)Invoke(plugin, "ClosePreservedEditsAsync", entries, CancellationToken.None);
            Require(handler.QueryCalls == 2 && handler.CloseCalls == 0, "Unavailable documents repeatedly queried or released");
            handler.HideDocuments = false;
            foreach (var entry in entries) { Set(entry, "NextMaintenanceAttemptUtc", DateTime.MinValue); Invoke(journal, "Save", entry); }
            await (Task)Invoke(plugin, "ClosePreservedEditsAsync", entries, CancellationToken.None);
            Require(handler.QueryCalls == 3 && handler.CloseCalls == 100, "100 same-project closes did not share one query");
            Require(entries.Cast<object>().All(entry => (bool)Get(entry, "Closed") && !(bool)Get(entry, "PendingClose")
                && Get(entry, "MaintenanceErrorKey") == null), "Successful close did not clear durable pending state");
        }
        Directory.Delete(root, true);
        Console.WriteLine("PASS: 100 same-project closes use one query; offline/unavailable state backs off across journal reload; recovery closes once");
    }

    static async Task FirstArchiveFailureRemainsEditable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-session-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "new-assembly.SLDASM");
        File.WriteAllText(source, "unsaved first archive source");
        var plugin = New("PdmAddin");
        var journal = New("EditDraftJournal", root, "first-archive");
        var entry = New("LocalEditDraft");
        Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "ProjectId", Guid.NewGuid());
        Set(entry, "SessionId", Field(plugin, "checkoutSessionId")); Set(entry, "SourcePath", source);
        Set(entry, "ObservedOpen", true);
        Invoke(journal, "Save", entry); SetField(plugin, "editJournal", journal);
        ((IDictionary)Field(plugin, "localEdits")).Add(Get(entry, "DocumentId"), entry);
        EmptyApplication(plugin);
        var pending = (ICollection)Invoke(plugin, "PrepareClosedEdits");
        Require(pending.Count == 1 && (bool)Get(entry, "PendingClose"), "Failed first archive was not queued safely");
        Require((File.GetAttributes(source) & FileAttributes.ReadOnly) == 0, "Failed first archive became read-only before server confirmation");
        Require((bool)Invoke(journal, "HasVerifiedDraft", entry), "Failed first archive lost its local draft");
        using (var handler = new Handler(new[] { entry }) { HideDocuments = true })
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client);
            await (Task)Invoke(plugin, "ClosePreservedEditsAsync", pending, CancellationToken.None);
            Require(handler.QueryCalls == 1 && handler.CloseCalls == 0, "Absent provisional document was treated as archived");
        }
        Require((bool)Get(entry, "Closed") && !(bool)Get(entry, "PendingClose"), "Failed first archive kept a stale edit record");
        Require((File.GetAttributes(source) & FileAttributes.ReadOnly) == 0, "Failed first archive was locked after cleanup");
        Require(File.ReadAllText(source) == "unsaved first archive source" && (bool)Invoke(journal, "HasVerifiedDraft", entry),
            "Failed first archive changed source or discarded verified draft");
        var unsafeEntry = New("LocalEditDraft");
        Set(unsafeEntry, "DocumentId", Guid.NewGuid()); Set(unsafeEntry, "ProjectId", Get(entry, "ProjectId"));
        Set(unsafeEntry, "SessionId", Get(entry, "SessionId")); Set(unsafeEntry, "SourcePath", source);
        Set(unsafeEntry, "BaseSha256", "older content"); Set(unsafeEntry, "DiskSha256", "newer content");
        Set(unsafeEntry, "PendingClose", true); Invoke(journal, "Save", unsafeEntry);
        var unsafeEntries = Array.CreateInstance(Type("LocalEditDraft"), 1); unsafeEntries.SetValue(unsafeEntry, 0);
        using (var handler = new Handler(new[] { unsafeEntry }) { HideDocuments = true })
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client);
            await (Task)Invoke(plugin, "ClosePreservedEditsAsync", unsafeEntries, CancellationToken.None);
        }
        Require(!(bool)Get(unsafeEntry, "Closed") && (bool)Get(unsafeEntry, "PendingClose"), "Unverified draft was discarded");
        Require((File.GetAttributes(source) & FileAttributes.ReadOnly) == 0, "Unverified draft locked the source");
        Directory.Delete(root, true);
        Console.WriteLine("PASS: uncommitted first archive keeps source editable and verified draft; stale reservation closes once");
    }

    static void SourceWriteLock(bool expectFailure)
    {
        var root=Path.Combine(Path.GetTempPath(),"pdm-edit-session-acceptance",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"assembly.SLDASM");var copy=Path.Combine(root,"snapshot.SLDASM");
        File.WriteAllText(path,"saved assembly");File.WriteAllText(copy,"unsaved inserted assembly");
        var entry=New("LocalEditDraft");Set(entry,"DocumentId",Guid.NewGuid());Set(entry,"SessionId",Guid.NewGuid());Set(entry,"SourcePath",path);
        var journal=New("EditDraftJournal",root,"share-proof");
        var failed=false;
        using(var writer=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
        {
            try{Invoke(journal,"Preserve",entry,copy,true);}
            catch(TargetInvocationException error) when(error.InnerException is IOException){failed=true;}
        }
        Require(failed==expectFailure,"Source-lock regression result unexpected");
        if(!failed)Require((bool)Invoke(journal,"HasVerifiedDraft",entry),"Shared-writer draft was not verified");
        Console.WriteLine(expectFailure ? "PASS reproduced released-plugin draft sharing violation with a writable open source" : "PASS fixed-plugin preserves verified draft with the same writable source lock");
    }

    static async Task Scenario(bool changed, bool dirty)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-session-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "part.SLDPRT");
        File.WriteAllText(path, "base");
        var plugin = New("PdmAddin");
        var session = (Guid)Field(plugin, "checkoutSessionId");
        var entry = New("LocalEditDraft");
        Set(entry, "DocumentId", Guid.NewGuid()); Set(entry, "ProjectId", Guid.NewGuid()); Set(entry, "SessionId", session);
        Set(entry, "CheckedOutAt", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560));
        Set(entry, "SourcePath", path); Set(entry, "BaseVersionId", Guid.NewGuid());
        Set(entry, "BaseSha256", Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { path }));
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            Set(entry, "ProcessId", process.Id); Set(entry, "ProcessStartedUtcTicks", process.StartTime.ToUniversalTime().Ticks);
        }
        if (changed) File.WriteAllText(path, "saved change");
        var journal = New("EditDraftJournal", root, "fixture");
        SetField(plugin, "editJournal", journal);
        ((IDictionary)Field(plugin, "localEdits")).Add(Get(entry, "DocumentId"), entry);
        SetField(plugin, "authenticatedUsername", "tester");
        var appType = plugin.GetType().GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
        var extensionType = modelType.GetProperty("Extension").PropertyType;
        var dirtyFlag = dirty;
        var saves = 0;
        var allowDraft = false;
        SetField(plugin, "draftIdleCheck", new Func<bool>(() => allowDraft));
        var extension = new Proxy(extensionType, call =>
        {
            if (call.MethodName != "SaveAs") throw new Exception("Unexpected extension call: " + call.MethodName);
            Invoke(plugin, "OnAssemblyFileSaved", 0, path);
            Invoke(plugin, "OnAssemblyTreeChanged");
            Invoke(plugin, "OnDocumentStateChanged");
            Invoke(plugin, "OnActiveDocumentChanged");
            File.WriteAllText((string)call.Args[0], "unsaved memory");
            call.Args[4] = 0; call.Args[5] = 0; dirtyFlag = false; saves++; return true;
        }).GetTransparentProxy();
        var model = new Proxy(modelType, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return path;
                case "GetType": return 1;
                case "GetSaveFlag": return dirtyFlag;
                case "get_Extension": return extension;
                case "SetSaveFlag": dirtyFlag = true; return null;
                default: throw new Exception("Unexpected model call: " + call.MethodName);
            }
        }).GetTransparentProxy();
        var loaded = true;
        var commandActive = true;
        var commandQueryFails = false;
        var documentReads = 0;
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            if (call.MethodName == "GetRunningCommandInfo")
            {
                if (commandQueryFails) throw new InvalidOperationException("模拟命令状态暂不可读");
                return new ReturnMessage(null, new object[] { commandActive ? 42 : 0, commandActive ? "插入零部件" : "", commandActive },
                    3, call.LogicalCallContext, call);
            }
            if (call.MethodName == "GetDocuments") { documentReads++; return loaded ? new[] { model } : Array.Empty<object>(); }
            throw new Exception("Unexpected application call: " + call.MethodName);
        }).GetTransparentProxy());
        SetField(plugin, "pendingTreeRefresh", 1);
        SetField(plugin, "pendingDrawingQrGraphicsRefresh", 1);
        Invoke(plugin, "OnSolidWorksIdle");
        Invoke(plugin, "RefreshTree", false, false);
        Require(((ICollection)Invoke(plugin, "PrepareClosedEdits")).Count == 0, "Command-time maintenance ran");
        Require(documentReads == 0 && saves == 0 && !(bool)Get(entry, "ObservedOpen"), "Insertion triggered native reads/save/close");
        Require((int)Field(plugin, "pendingTreeRefresh") == 1 && (int)Field(plugin, "pendingDrawingQrGraphicsRefresh") == 1,
            "Deferred work was lost");
        commandActive = false; commandQueryFails = true;
        Invoke(plugin, "OnSolidWorksIdle");
        Require(documentReads == 0, "Unknown command state allowed background work");
        commandQueryFails = false;
        var pending = (ICollection)Invoke(plugin, "PrepareClosedEdits");
        Require(pending.Count == 0 && (bool)Get(entry, "ObservedOpen"), "An open/referenced document was released");
        Require(saves == 0, "Mouse/keyboard activity allowed an expensive draft snapshot");
        allowDraft = true;
        SetField(plugin,"pendingTreeRefresh",0);SetField(plugin,"pendingTreeRefreshForceScan",0);
        SetField(plugin,"currentTreeCacheKey","unchanged-edit-tree");
        using(var solidWorksFile = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read))
            pending = (ICollection)Invoke(plugin,"PrepareClosedEdits");
        if (dirty)
        {
            Require(saves == 1 && dirtyFlag, "Draft snapshot cleared the original unsaved flag");
            Require(File.ReadAllText((string)Get(entry, "DraftPath")) == "unsaved memory", "Unsaved memory was not backed up");
            Require(File.ReadAllText(path) == "saved change", "Snapshot modified original file");
            Require((int)Field(plugin,"pendingTreeRefresh")==0 && (int)Field(plugin,"pendingTreeRefreshForceScan")==0
                && (string)Field(plugin,"currentTreeCacheKey")=="unchanged-edit-tree", "Draft-only save events scheduled a full tree scan");
            for(var i=0;i<100;i++) Invoke(plugin,"PrepareClosedEdits");
            Require(saves==1,"Unchanged dirty flag caused repeated snapshot work");
            var preserved = (string)Get(entry,"DraftPath");
            Set(entry,"SavedAtUtc",DateTime.UtcNow.AddMinutes(-5));Set(entry,"NextSnapshotAttemptUtc",DateTime.MinValue);
            using(var exclusive = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                Invoke(plugin,"PrepareClosedEdits");
                var attempts=saves;
                for(var i=0;i<100;i++) Invoke(plugin,"PrepareClosedEdits");
                Require(attempts==2 && saves==attempts,"A failed snapshot repeatedly retried while the source remained locked");
                Require((string)Get(entry,"DraftPath")==preserved && (bool)Invoke(journal,"HasVerifiedDraft",entry),"Failed snapshot destroyed a verified draft");
            }
            Require(dirtyFlag && !(bool)Get(entry,"PendingClose"),"Failed draft cleared unsaved changes or released editing");
            Set(entry,"NextSnapshotAttemptUtc",DateTime.MinValue);
            Invoke(plugin,"PrepareClosedEdits");
            Require(saves==3 && (bool)Invoke(journal,"HasVerifiedDraft",entry),"Draft did not recover after the lock was released");
            Invoke(plugin,"OnAssemblyTreeChanged");
            Require((int)Field(plugin,"pendingTreeRefreshForceScan")==1,"Actual insertion/structural event was lost after snapshot");
            Console.WriteLine("PASS: writable SW lock compatible; input deferral; no snapshot-induced scan; 100 failed retry ticks coalesced; verified draft retained");
        }
        loaded = false;
        pending = (ICollection)Invoke(plugin, "PrepareClosedEdits");
        Require(pending.Count == 1 && (bool)Get(entry, "PendingClose"), "Closed document not queued durably");
        Require((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0, "Closed edit was locked before server confirmation");
        if (changed) Require((bool)Invoke(journal, "HasVerifiedDraft", entry), "Changed file lacks verified draft");
        using (var handler = new Handler(entry))
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        {
            ((HttpClient)Field(client, "httpClient")).Dispose();
            SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
            SetField(plugin, "apiClient", client);
            handler.Fail = true;
            try { await (Task)Invoke(plugin, "ClosePreservedEditAsync", entry, CancellationToken.None); throw new Exception("Network failure ignored"); }
            catch (HttpRequestException) { Require((bool)Get(entry, "PendingClose"), "Failed close lost retry record");
                Require((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0, "Disconnected close locked uncommitted changes"); }
            handler.Fail = false;
            await (Task)Invoke(plugin, "ClosePreservedEditAsync", entry, CancellationToken.None);
            Require((bool)Get(entry, "Closed") && !(bool)Get(entry, "PendingClose"), "Successful close not recorded");
            Require((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "Confirmed archived close did not protect the source");
            Require(handler.CloseCalls == 1 && handler.DraftPreserved == changed, "Wrong close semantics");
            Require(handler.CheckedOutAt == (DateTime)Get(entry, "CheckedOutAt"), "Checkout generation lost in HTTP serialization");
        }
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
        Console.WriteLine("PASS: open/reference protection, durable close/retry, generation serialization; changed=" + changed + ", unsaved=" + dirty);
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly object[] entries;
        public bool Fail, DraftPreserved, HideDocuments;
        public int CloseCalls, QueryCalls;
        public DateTime CheckedOutAt;
        public Handler(object entry) : this(new[] { entry }) { }
        public Handler(object[] entries) { this.entries = entries; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) QueryCalls++;
            if (Fail) throw new HttpRequestException("模拟断网");
            var serializer = new JavaScriptSerializer();
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(serializer.Serialize(
                    (HideDocuments ? Array.Empty<object>() : entries).Select(entry => new
                    { id = Get(entry, "DocumentId"), checkedOutBy = "tester", checkoutSessionId = Get(entry, "SessionId"), checkedOutAt = ((DateTime)Get(entry, "CheckedOutAt")).ToString("O") }).ToArray())) };
            Require(request.RequestUri.AbsolutePath.EndsWith("/close-edit"), "Unexpected write endpoint");
            var body = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
            DraftPreserved = (bool)body["draftPreserved"];
            CheckedOutAt = DateTime.Parse((string)body["checkedOutAt"], null, System.Globalization.DateTimeStyles.RoundtripKind);
            CloseCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    sealed class Proxy : RealProxy
    {
        readonly Func<IMethodCallMessage, object> callback;
        public Proxy(System.Type type, Func<IMethodCallMessage, object> callback) : base(type) { this.callback = callback; }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try { var result = callback(call); return result as IMessage ?? new ReturnMessage(result, call.Args, call.ArgCount, call.LogicalCallContext, call); }
            catch (Exception exception) { return new ReturnMessage(exception, call); }
        }
    }
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static System.Type Type(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Flags & ~BindingFlags.Static, null, args, null);
    static object Get(object value, string name) => value.GetType().GetProperty(name, Flags).GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags).SetValue(value, data);
    static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static void SetField(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
    static object Invoke(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
}
