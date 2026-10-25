using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static void EditCollaborationSafetyScenario()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-edit-collaboration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "PART.SLDPRT");
            File.WriteAllText(path, "unchanged archived bytes");
            var plugin = New("PdmAddin");
            var session = (Guid)Field(plugin, "checkoutSessionId");
            var entry = New("LocalEditDraft");
            var id = Guid.NewGuid();
            Set(entry, "DocumentId", id); Set(entry, "SessionId", session); Set(entry, "CheckedOutAt", DateTime.UtcNow.AddHours(-1));
            Set(entry, "SourcePath", path); Set(entry, "BaseVersionId", Guid.NewGuid());
            Set(entry, "BaseSha256", Type("EditDraftJournal").GetMethod("Hash", Flags).Invoke(null, new object[] { path }));
            ((IDictionary)Field(plugin, "localEdits")).Add(id, entry);
            Invoke(Field(plugin, "activeCheckoutDocumentIds"), "Add", id);
            var appType = plugin.GetType().GetField("application", Flags).FieldType;
            var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
            var dirty = false; var command = false; var inventoryKnown = true;
            var model = new Proxy(modelType, call =>
            {
                if (call.MethodName == "GetPathName") return path;
                if (call.MethodName == "GetSaveFlag") return dirty;
                throw new Exception("Unexpected CAD call: " + call.MethodName);
            }).GetTransparentProxy();
            SetField(plugin, "application", new Proxy(appType, call =>
            {
                if (call.MethodName == "GetDocuments") return inventoryKnown ? new[] { model } : null;
                if (call.MethodName == "get_StartupProcessCompleted") return true;
                if (call.MethodName == "GetRunningCommandInfo") return new ReturnMessage(null, new object[] { command ? 42 : 0, command ? "重建" : "", command }, 3, call.LogicalCallContext, call);
                throw new Exception("Unexpected application call: " + call.MethodName);
            }).GetTransparentProxy());
            var candidateType = Type("PdmAddin").GetNestedType("IdleReleaseCandidate", BindingFlags.NonPublic);
            var candidate = Activator.CreateInstance(candidateType, true);
            SetField(candidate, "Entry", entry); SetField(candidate, "Length", new FileInfo(path).Length); SetField(candidate, "LastWrite", File.GetLastWriteTimeUtc(path));
            Require((bool)Invoke(plugin, "StillIdle", candidate, true), "Unchanged owned document was not eligible");
            dirty = true;
            Require(!(bool)Invoke(plugin, "StillIdle", candidate, true), "Unsaved in-memory edits were eligible for automatic release");
            dirty = false;
            File.WriteAllText(path, "saved but not archived changes");
            Require(!(bool)Invoke(plugin, "StillIdle", candidate, true), "Saved unarchived edits were eligible for automatic release");
            File.WriteAllText(path, "unchanged archived bytes");
            SetField(candidate, "Length", new FileInfo(path).Length); SetField(candidate, "LastWrite", File.GetLastWriteTimeUtc(path));
            inventoryKnown = false;
            Require(!(bool)Invoke(plugin, "NoLoadedDocumentHasUnsavedChanges"), "Unknown CAD state was treated as safe");
            inventoryKnown = true;
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                var http = (HttpClient)Field(client, "httpClient");
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "isolated");
                SetField(plugin, "apiClient", client); SetField(plugin, "idleReleaseEnabled", true);
                SetField(plugin, "editReleaseIdleCheck", new Func<int, bool>(_ => false));
                Require(!(bool)Invoke(plugin, "CanAutomaticallyReleaseEdits"), "Recent user activity allowed automatic release");
                SetField(plugin, "editReleaseIdleCheck", new Func<int, bool>(_ => true));
                Require((bool)Invoke(plugin, "CanAutomaticallyReleaseEdits"), "Idle release policy did not enable");
                SetField(plugin, "idleReleaseEnabled", false);
                Require(!(bool)Invoke(plugin, "CanAutomaticallyReleaseEdits"), "Disabled switch still allowed automatic release");
                SetField(plugin, "idleReleaseEnabled", true); command = true;
                Require(!(bool)Invoke(plugin, "CanAutomaticallyReleaseEdits"), "Active CAD command allowed automatic release");
                command = false; SetField(plugin, "checkInOperationInProgress", 1);
                Require(!(bool)Invoke(plugin, "CanAutomaticallyReleaseEdits"), "Archive in progress allowed automatic release");
                http.DefaultRequestHeaders.Authorization = null;
            }
            Invoke(Field(plugin, "activeCheckoutDocumentIds"), "Remove", id);
            Require(!(bool)Invoke(plugin, "StillIdle", candidate, true), "Lost ownership allowed stale countdown to release a document");
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                var handler = new AutoReleaseHandler();
                ((HttpClient)Field(client, "httpClient")).Dispose();
                SetField(client, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
                ((Task)Invoke(client, "AutoReleaseEditAsync", entry, (Guid)Get(entry, "BaseVersionId"), (string)Get(entry, "BaseSha256"), CancellationToken.None)).GetAwaiter().GetResult();
                Require(handler.CheckedOutAt == ((DateTime)Get(entry, "CheckedOutAt")).ToUniversalTime(), "Automatic release lost checkout generation precision");
            }
            Console.WriteLine("PASS: native idle release rejects unsaved/saved changes, unknown CAD state, disabled policy, active commands, archive progress and lost ownership.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "pdm-edit-collaboration-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    static void EditCollaborationUiScenario()
    {
        var root = New("CadTreeNode");
        Set(root, "FileName", "协作零件.SLDPRT"); Set(root, "FullPath", "C:\\fixture\\协作零件.SLDPRT");
        var items = Array.CreateInstance(Type("BatchOperationItem"), 1); items.SetValue(New("BatchOperationItem", root, 0), 0);
        using (var dialog = (Form)New("BatchOperationDialog", root, items, Array.CreateInstance(Type("ProjectDto"), 0), null, "tester",
            Enum.ToObject(Type("BatchOperationKind"), 1), null, null, new Dictionary<string, string> { ["tester"] = "当前用户", ["helper"] = "协助设计员" }, true, false, 0))
        {
            var mode = (ComboBox)Field(dialog, "editDisposition");
            var recipient = (ComboBox)Field(dialog, "handoffRecipient");
            Require(mode.SelectedIndex == 0 && !(bool)Get(dialog, "KeepEditing") && Get(dialog, "HandoffTo") == null, "Archive default must release editing");
            mode.SelectedIndex = 1;
            Require((bool)Get(dialog, "KeepEditing") && !recipient.Enabled, "Continue editing selector is inconsistent");
            mode.SelectedIndex = 2; recipient.SelectedIndex = 0;
            Require(!(bool)Get(dialog, "KeepEditing") && (string)Get(dialog, "HandoffTo") == "helper" && recipient.Items.Count == 1, "Handoff must exclude self and select recipient");
            ((CheckBox)Field(dialog, "standardLibrary")).Checked = true;
            Require(!mode.Enabled && Get(dialog, "HandoffTo") == null && !(bool)Get(dialog, "KeepEditing"), "Standard library archive inherited a project handoff");
        }
        using (var countdown = (Form)New("EditAutoReleaseDialog", 2, 60, new Func<bool>(() => true), new Func<Task>(() => Task.CompletedTask), new Action(() => { })))
        {
            Invoke(countdown, "UpdateText");
            Require(((Label)Field(countdown, "countdown")).Text.Contains("60 秒"), "Idle countdown text is missing");
        }
        Console.WriteLine("PASS: native archive/release, continue, recipient handoff and standard-library isolation; automatic release ISO generation.");
    }

    sealed class AutoReleaseHandler : HttpMessageHandler
    {
        public DateTime CheckedOutAt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri.AbsolutePath.EndsWith("/auto-release"), "Unexpected automatic release endpoint");
            var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
            Require(body["checkedOutAt"] is string && ((string)body["checkedOutAt"]).Contains("T"), "Automatic release generation must use ISO date text");
            CheckedOutAt = DateTime.Parse((string)body["checkedOutAt"], null, System.Globalization.DateTimeStyles.RoundtripKind);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
