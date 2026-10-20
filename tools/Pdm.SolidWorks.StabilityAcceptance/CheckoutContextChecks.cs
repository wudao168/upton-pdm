using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private static void CheckoutContext(Assembly plugin, SldWorks app, IModelDoc2 parent, string copies, string qa)
    {
        object New(string name, params object[] args) => Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks." + name, true), Members & ~BindingFlags.Static, null, args, null);
        void Set(object value, string name, object data) => value.GetType().GetProperty(name).SetValue(value, data);
        void Field(object value, string name, object data) => value.GetType().GetField(name, Members).SetValue(value, data);
        var addin = New("PdmAddin"); var project = Guid.NewGuid(); var rootId = Guid.NewGuid();
        var rootName = Path.GetFileName(parent.GetPathName());
        var selected = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>()
            .First(model => model.GetType() == 1 && model.GetPathName().StartsWith(copies + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var targetName = Path.GetFileName(selected.GetPathName()); var targetId = Guid.NewGuid();
        var sourceHashes = HashFiles(copies); var owner = New("ControlledOpenManifestDto");
        Set(owner, "Id", Guid.NewGuid()); Set(owner, "ProjectId", project); Set(owner, "ProjectCode", "QA-CONTEXT");
        Set(owner, "RootDocumentId", rootId); Set(owner, "RootVersionId", Guid.NewGuid()); Set(owner, "RootRevision", "W1");
        Set(owner, "RootRelativePath", rootName);
        var files = (IList)Activator.CreateInstance(owner.GetType().GetProperty("Files").PropertyType);
        foreach (var path in Directory.GetFiles(copies).Where(path => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            var file = New("ControlledOpenFileDto"); var name = Path.GetFileName(path);
            Set(file, "DocumentId", name == rootName ? rootId : name == targetName ? targetId : Guid.NewGuid());
            Set(file, "VersionId", Guid.NewGuid()); Set(file, "FileName", name); Set(file, "RelativePath", name);
            Set(file, "Sha256", Hash(path)); Set(file, "FileLength", new FileInfo(path).Length); Set(file, "Revision", "W1");
            Set(file, "IsRoot", name == rootName); files.Add(file);
        }
        Set(owner, "Files", files);
        var requested = New("ControlledOpenManifestDto");
        foreach (var property in owner.GetType().GetProperties().Where(property => property.CanWrite)) property.SetValue(requested, property.GetValue(owner));
        Set(requested, "RootDocumentId", targetId); Set(requested, "RootRelativePath", targetName);
        var selectedFile = files.Cast<object>().Single(file => (Guid)Get(file, "DocumentId") == targetId);
        var requestedFiles = (IList)Activator.CreateInstance(files.GetType()); requestedFiles.Add(selectedFile); Set(requested, "Files", requestedFiles);
        Set(requested, "RootVersionId", Get(selectedFile, "VersionId"));
        var client = New("PdmApiClient", "http://isolated-edit.invalid/");
        var session = (Guid)addin.GetType().GetField("checkoutSessionId", Members).GetValue(addin);
        using (var handler = new CheckoutFixtureHandler(requested, project, targetId, targetName, session))
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://isolated-edit.invalid/") })
        using (var host = new Form { Opacity = 0, ShowInTaskbar = false })
        using (var pane = (Control)New("PdmTaskPaneControl", "原生编辑切换验收"))
        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            foreach (var name in new[] { "httpClient", "controlledOpenHttpClient" })
            { ((HttpClient)client.GetType().GetField(name, Members).GetValue(client)).Dispose(); Field(client, name, http); }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "isolated-fixture");
            var workspace = New("ControlledWorkspaceManager", client, Path.Combine(qa, "workspace"));
            Field(addin, "application", app); Field(addin, "apiClient", client); Field(addin, "controlledWorkspace", workspace);
            Field(addin, "authenticatedUsername", "qa"); Field(addin, "currentProjectId", project); Field(addin, "taskPaneControl", pane);
            Field(addin, "lifetime", new CancellationTokenSource());
            var identity = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true).GetField("bindingDirectory", Members);
            var previousBindings = identity.GetValue(null); identity.SetValue(null, Path.Combine(qa, "bindings"));
            var settings = plugin.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true).GetField("SettingsPath", Members);
            var previousSettings = settings.GetValue(null); var settingsPath = Path.Combine(qa, "workspace-settings.json");
            File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new { WorkspaceRoot = Path.Combine(qa, "workspace") })); settings.SetValue(null, settingsPath);
            var previousContext = SynchronizationContext.Current;
            try
            {
                host.Controls.Add(pane); var handle = host.Handle; pane.CreateControl();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var scope = (string)client.GetType().GetProperty("EditJournalScope").GetValue(client) + "|qa";
                Field(addin, "editJournalScope", scope); Field(addin, "editJournal", New("EditDraftJournal", Path.Combine(qa, "drafts"), scope));
                addin.GetType().GetMethod("RememberControlledOpenManifest", Members).Invoke(addin, new[] { owner, (object)copies });
                var errorShown = false;
                timer.Tick += (_, __) => { var window = FindWindow("#32770", "UPLM"); if (window == IntPtr.Zero) return;
                    GetWindowThreadProcessId(window, out var pid); if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
                    errorShown = true; PostMessage(window, 0x111, new IntPtr(1), IntPtr.Zero); };
                timer.Start();
                addin.GetType().GetMethods(Members).Single(method => method.Name == "BeginControlledOpen" && method.GetParameters().Length == 5).Invoke(addin, new object[] { project, targetId,
                    Enum.Parse(plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenMode", true), "LatestEdit"), null, null });
                var until = DateTime.UtcNow.AddMinutes(4);
                while ((int)addin.GetType().GetField("controlledOpenInProgress", Members).GetValue(addin) != 0 && DateTime.UtcNow < until)
                { Application.DoEvents(); Thread.Sleep(10); }
                timer.Stop();
                var parentPath = (string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace, new[] { owner, (object)rootId });
                var targetPath = (string)workspace.GetType().GetMethod("GetWorkingFilePath").Invoke(workspace, new[] { owner, (object)targetId });
                var nativeParent = app.GetOpenDocumentByName(parentPath) as IModelDoc2;
                var nativeTarget = app.GetOpenDocumentByName(targetPath) as IModelDoc2;
                if (errorShown || handler.Checkouts != 1 || handler.Releases != 0 || nativeParent == null || !nativeParent.IsOpenedReadOnly()
                    || nativeTarget == null || nativeTarget.IsOpenedReadOnly()) throw new Exception("原生上下文切换未完成或扩大了权限范围。");
                var tree = New("CadTreeNode"); Set(tree, "FullPath", parentPath); Set(tree, "FileName", rootName);
                Set(tree, "Kind", Enum.ToObject(tree.GetType().GetProperty("Kind").PropertyType, 0));
                foreach (var path in Directory.GetFiles(copies, "*.SLDDRW"))
                {
                    var child = New("CadTreeNode"); Set(child, "FullPath", path); Set(child, "FileName", Path.GetFileName(path));
                    Set(child, "Kind", Enum.ToObject(child.GetType().GetProperty("Kind").PropertyType, 2));
                    ((IList)Get(tree, "Children")).Add(child);
                }
                Field(addin, "currentTree", tree);
                var items = addin.GetType().GetMethod("BuildBatchOperationItems", Members).Invoke(null, new[] { tree });
                var beforeDrawings = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>().Count(model => model.GetType() == 3);
                Pump((Task)addin.GetType().GetMethod("PreloadBatchCheckInDocumentsAsync", Members).Invoke(addin, new object[] { items, null, CancellationToken.None }));
                var afterDrawings = (app.GetDocuments() as Array).Cast<object>().OfType<IModelDoc2>().Count(model => model.GetType() == 3);
                if (beforeDrawings != afterDrawings || !nativeParent.IsOpenedReadOnly() || nativeTarget.IsOpenedReadOnly())
                    throw new Exception("最终插件的准备阶段打开了未选择的工程图或改变了原窗口权限。");
                Report["finalPreloadInventoryPassed"] = true;
                if (!SameHashes(sourceHashes, HashFiles(copies))) throw new Exception("原预览图档在编辑切换中被改写。");
                Report["checkoutContextPassed"] = true; Report["checkoutTarget"] = targetName;
                Report["checkoutCalls"] = handler.Checkouts; Report["parentReadOnlyAfterChildCheckout"] = true;
                Console.WriteLine("NATIVE CHECKOUT PASS: parent remains read-only; only " + targetName + " acquires editing; source hashes unchanged");
            }
            finally { identity.SetValue(null, previousBindings); settings.SetValue(null, previousSettings); SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }

    private sealed class CheckoutFixtureHandler : HttpMessageHandler
    {
        private readonly object manifest; private readonly Guid project, document, session; private readonly string fileName;
        internal int Checkouts, Releases;
        internal CheckoutFixtureHandler(object manifest, Guid project, Guid document, string fileName, Guid session)
        { this.manifest = manifest; this.project = project; this.document = document; this.fileName = fileName; this.session = session; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath; object response;
            if (path.EndsWith("/open-manifest")) response = manifest;
            else if (path.EndsWith("/documents") && request.Method == HttpMethod.Get) response = new[] { Doc(false) };
            else if (path == "/api/documents/" + document + "/checkout") { Checkouts++; response = Doc(true); }
            else if (path.EndsWith("/discard-checkout")) { Releases++; response = Doc(false); }
            else throw new Exception("Unexpected isolated API call: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(response)) });
        }
        private object Doc(bool editing) => new { id = document, projectId = project, fileName, name = fileName, kind = 1,
            revision = new { display = "W1" }, checkedOutBy = editing ? "qa" : null, checkoutSessionId = editing ? (Guid?)session : null };
    }
}
