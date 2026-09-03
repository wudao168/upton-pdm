using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    private static Assembly addin;
    private static string directory;
    private static int passed;
    private static int failed;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS", new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        addin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        // Never touch the user's actual document bindings or CAD files.
        Type("PdmDocumentIdentityStore").GetField("bindingDirectory", Members)
            .SetValue(null, Path.Combine(directory, "bindings-" + Guid.NewGuid().ToString("N")));
        Type("PdmDocumentIdentityStore").GetField("workspaceRootResolver", Members)
            .SetValue(null, new Func<string>(() => Path.Combine(directory, "workspace")));
        Test("stale source is updated alongside the refreshed UI tree", TestStaleSource);
        Test("300 repeated references resolve once to the matching historical version", TestResolveDuplicates);
        Test("changed reference is blocked, never assigned the latest version", TestChangedReference);
        Test("cancelled preflight makes no requests", TestCancellation);
        Test("successful child updates all operation instances but not a different local file", TestOperationInstances);
        Test("reference validation rejects unknown versions before upload", TestValidation);
        Test("copied legacy IDs are hints only, never automatic associations", TestLegacyIdentity);
        Test("10 controlled plus 10 new files retain independent identities on retry", TestIncrementalIdentity);
        Test("copied bindings cannot be replayed at another path or machine", TestBindingValidation);
        Test("same filename does not attach an external file during metadata refresh", TestNoFilenameBinding);
        Test("packed nested assemblies may reference sibling folders", TestNestedPackedAssembly);
        Test("new assemblies block original external files but allow target project controlled references", TestExternalReferences);
        Test("registration persists identity on the source even after UI tree refresh", TestRegistrationBinding);
        Test("binding failure stops before assigning an in-memory identity", TestBindingFailure);
        Test("incremental plan submits ten new files and parent, not ten unchanged originals", TestIncrementalPlan);
        Test("one project uses one visible working directory", TestProjectWorkspaceDirectory);
        Test("project workspace refresh preserves unrelated files", TestWorkspaceMerge);
        Test("writable changed files block refresh while stale read-only files may refresh", TestWorkspaceChangeGuard);
        Test("workspace manifests cannot escape the managed directory", TestWorkspaceTraversalGuard);
        Test("cache cleanup preserves working, recovery, and locked files", TestWorkspaceCacheCleanup);
        Test("workspace page explains location, local state, PLM state, and next action", TestWorkspaceGuidanceUi);
        Test("project tree resolves downloaded files by controlled workspace identity", TestProjectTreeLocalPathMapping);
        Test("desktop workspace states require identity and detect local version changes", TestWorkspaceLocalStates);
        Test("batch completion refreshes local controlled version metadata", TestBatchControlledVersionBinding);
        Console.WriteLine($"Passed={passed}; Failed={failed}; no SolidWorks COM or production API calls.");
        return failed == 0 ? 0 : 1;
    }

    private static void TestStaleSource()
    {
        var plugin = New("PdmAddin");
        var source = Node("source.SLDPRT", Guid.NewGuid());
        var uiNode = Node("source.SLDPRT", (Guid)Get(source, "DocumentId"));
        plugin.GetType().GetField("currentTree", Members).SetValue(plugin, uiNode);
        var matches = ((IEnumerable)Call(plugin, "FindMatchingDocumentInstances", source, Get(source, "DocumentId"))).Cast<object>().ToArray();
        Assert(matches.Contains(source) && matches.Contains(uiNode), "old operation source was excluded from matches");
        var version = Version("W2", "hash");
        Call(plugin, "ApplyResolvedWorkingVersionToMatchingInstances", source, version);
        Assert((string)Get(source, "CurrentRevision") == "W2", "source version not updated");
        Assert((string)Get(uiNode, "CurrentRevision") == "W2", "UI version not updated");
    }

    private static void TestResolveDuplicates()
    {
        var documentId = Guid.NewGuid();
        var root = Node("root.SLDASM", null, 0);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var path = Path.Combine(directory, "part.SLDPRT");
        File.WriteAllBytes(path, bytes);
        for (var i = 0; i < 300; i++)
        {
            var node = Node("part.SLDPRT", documentId);
            Set(node, "Configuration", "config-" + i);
            Children(root).Add(node);
        }
        var pending = Node("pending.SLDPRT", Guid.NewGuid());
        Children(root).Add(pending);
        // Drawings are excluded from assembly snapshots and need no version lookup here.
        Children(root).Add(Node("drawing.SLDDRW", Guid.NewGuid(), 2));
        using (var handler = new VersionsHandler(documentId, Hash(bytes)))
        {
            var plugin = Plugin(handler);
            Resolve(plugin, Items(root, pending), CancellationToken.None);
            Assert(handler.Requests == 1, "duplicate component instances caused repeated API requests");
            foreach (var node in Children(root).Cast<object>().Take(300))
                Assert((string)Get(node, "CurrentRevision") == "W1", "used latest W2 instead of content-matched W1");
            Assert(string.IsNullOrEmpty((string)Get(pending, "CurrentRevision")), "planned child was incorrectly resolved before its check-in");
            Set(pending, "CurrentRevision", "W3");
            Static("PdmApiClient", "ValidateCheckInReferences", root);
            Assert(handler.Writes == 0, "preflight performed a write");
        }
    }

    private static void TestChangedReference()
    {
        var documentId = Guid.NewGuid();
        var root = Node("changed-root.SLDASM", null, 0);
        var child = Node("changed.SLDPRT", documentId);
        File.WriteAllBytes((string)Get(child, "FullPath"), new byte[] { 9 });
        Children(root).Add(child);
        using (var handler = new VersionsHandler(documentId, "not-the-local-hash"))
        {
            var plugin = Plugin(handler);
            Expect<InvalidOperationException>(() => Resolve(plugin, Items(root), CancellationToken.None));
            Assert(string.IsNullOrEmpty((string)Get(child, "CurrentRevision")), "unmatched reference assigned a version");
            Assert(handler.Writes == 0, "failed preflight performed a write");
        }
    }

    private static void TestCancellation()
    {
        var id = Guid.NewGuid();
        var root = Node("cancel.SLDASM", null, 0);
        Children(root).Add(Node("part.SLDPRT", id));
        using (var handler = new VersionsHandler(id, "hash"))
        {
            var plugin = Plugin(handler);
            Expect<OperationCanceledException>(() => Resolve(plugin, Items(root), new CancellationToken(true)));
            Assert(handler.Requests == 0, "cancelled preflight made an API call");
        }
    }

    private static void TestOperationInstances()
    {
        var source = Node("submitted.SLDPRT", Guid.NewGuid());
        Set(source, "CurrentRevision", "W4");
        Set(source, "Revision", "W4");
        Set(source, "HasStoredVersion", true);
        var duplicate = Node("submitted.SLDPRT", null);
        var differentPath = Node("different.SLDPRT", (Guid)Get(source, "DocumentId"));
        Static("PdmAddin", "UpdateBatchDocumentInstances", source, TypedArray("CadTreeNode", source, duplicate, differentPath));
        Assert((string)Get(duplicate, "CurrentRevision") == "W4", "old operation graph duplicate not updated");
        Assert(Get(duplicate, "DocumentId").Equals(Get(source, "DocumentId")), "new document identity not propagated");
        Assert(string.IsNullOrEmpty((string)Get(differentPath, "CurrentRevision")), "different local content path was overwritten");
    }

    private static void TestValidation()
    {
        var root = Node("validate.SLDASM", null, 0);
        var child = Node("validate.SLDPRT", Guid.NewGuid());
        Set(child, "LatestRevision", "W8");
        Children(root).Add(child);
        Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateCheckInReferences", root));
        foreach (var revision in new[] { "W1", "W2*", "A", "A-W2" })
        {
            Set(child, "CurrentRevision", revision);
            Static("PdmApiClient", "ValidateCheckInReferences", root);
        }
    }

    private static void TestLegacyIdentity()
    {
        var source = FixtureFile("legacy/original.SLDPRT");
        var id = Guid.NewGuid();
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteIdentityStream", source, id), "cannot create legacy stream fixture");
        Assert(ReadIdentity("TryReadUnverifiedIdentity", source) == id, "legacy stream was not read");
        Assert(ReadIdentity("TryRead", source) == null, "legacy stream silently became an association");
        var node = Node("legacy/original.SLDPRT", null);
        Static("PdmAddin", "RestorePersistedDocumentIdentities", node);
        Assert(Get(node, "DocumentId") == null, "scanner adopted legacy ID");
        var project = Guid.NewGuid();
        Bind(source, id, project);
        Static("PdmAddin", "RestorePersistedDocumentIdentities", node);
        Assert(Get(node, "DocumentId") == null, "external admission became a trusted workspace identity");
        Assert((bool)Get(node, "IsExternalProvenance"), "external PLM provenance was not shown");
        Assert(Get(node, "ProvenanceDocumentId").Equals(id), "external provenance lost the document ID hint");
        var copy = FixtureFile("legacy/copy/original.SLDPRT");
        File.Copy(source, copy, true);
        Static("PdmDocumentIdentityStore", "TryWriteIdentityStream", copy, id);
        Assert(ReadIdentity("TryRead", copy) == null, "copied stream adopted source ID");
        Assert(Static("PdmDocumentIdentityStore", "ReadProjectId", copy) == null, "copy inherited source project");
        Assert(ReadIdentity("TryRead", source) == null, "external original was trusted outside the workspace");
    }

    private static void TestIncrementalIdentity()
    {
        var project = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        var paths = Enumerable.Range(0, 20).Select(i => WorkspaceFile("project/incremental/part-" + i + ".SLDPRT")).ToArray();
        for (var i = 0; i < 10; i++) Bind(paths[i], ids[i], project);
        Assert(paths.Count(path => ReadIdentity("TryRead", path).HasValue) == 10, "first admission count is wrong");
        // Additional files admitted in the same workspace remain controlled without downloading again.
        for (var i = 10; i < 20; i++) Bind(paths[i], ids[i], project);
        for (var i = 0; i < 20; i++)
        {
            Assert(ReadIdentity("TryRead", paths[i]) == ids[i], "retry lost document identity");
            Assert(Static("PdmDocumentIdentityStore", "ReadProjectId", paths[i]).Equals(project), "project not retained");
            File.AppendAllText(paths[i], " modified");
            Assert(ReadIdentity("TryRead", paths[i]) == ids[i], "normal edit was treated as a new document");
            var copy = WorkspaceFile("other-project/independent/part-" + i + ".SLDPRT");
            File.Copy(paths[i], copy, true);
            Static("PdmDocumentIdentityStore", "TryWriteIdentityStream", copy, ids[i]);
            Assert(ReadIdentity("TryRead", copy) == null, "packed copy retained old association");
            Bind(copy, Guid.NewGuid(), otherProject);
            Assert(ReadIdentity("TryRead", copy) != ids[i], "independent admission reused old document");
            Assert(ReadIdentity("TryRead", paths[i]) == ids[i], "independent admission changed original");
        }
    }

    private static void TestBindingValidation()
    {
        var path = WorkspaceFile("validation/original.SLDPRT");
        var copy = WorkspaceFile("validation/copy.SLDPRT");
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        Bind(path, id, project);
        var receipt = (string)Static("PdmDocumentIdentityStore", "BindingPath", path);
        var copyReceipt = (string)Static("PdmDocumentIdentityStore", "BindingPath", copy);
        File.Copy(receipt, copyReceipt, true);
        Assert(ReadIdentity("TryRead", copy) == null, "receipt accepted at another file location");
        var json = File.ReadAllText(receipt);
        File.WriteAllText(receipt, json.Replace(Environment.MachineName, "not-this-machine"));
        Assert(ReadIdentity("TryRead", path) == null, "receipt accepted for another machine");
        File.WriteAllText(receipt, "not-json");
        Assert(ReadIdentity("TryRead", path) == null, "corrupt receipt did not fail safely");
        Bind(path, id, project);
        Bind(path, id, null);
        Assert(Static("PdmDocumentIdentityStore", "ReadProjectId", path).Equals(project), "identity refresh lost project");
    }

    private static void TestNoFilenameBinding()
    {
        var node = Node("external/same-name.SLDPRT", null);
        FixtureFile("external/same-name.SLDPRT");
        var document = New("DocumentDto");
        Set(document, "Id", Guid.NewGuid());
        Set(document, "ProjectId", Guid.NewGuid());
        Set(document, "FileName", Get(node, "FileName"));
        var docs = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentDto")));
        docs.Add(Get(document, "Id"), document);
        Call(New("PdmAddin"), "ApplyMetadata", node, docs);
        Assert(Get(node, "DocumentId") == null, "filename matching silently linked external CAD");
        Assert(ReadIdentity("TryRead", (string)Get(node, "FullPath")) == null, "metadata refresh wrote an unwanted receipt");
    }

    private static void TestNestedPackedAssembly()
    {
        var root = Node("packed/main.SLDASM", null, 0);
        var nested = Node("packed/assemblies/sub.SLDASM", null, 0);
        Children(root).Add(nested);
        Children(nested).Add(Node("packed/parts/shared.SLDPRT", null));
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, nested), Guid.NewGuid());
    }

    private static void TestExternalReferences()
    {
        var project = Guid.NewGuid();
        var root = Node("packed/new-main.SLDASM", null, 0);
        var child = Node("original/shared.SLDPRT", null);
        FixtureFile("original/shared.SLDPRT");
        Children(root).Add(child);
        Expect<InvalidOperationException>(() => Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project));
        Bind((string)Get(child, "FullPath"), Guid.NewGuid(), Guid.NewGuid());
        Expect<InvalidOperationException>(() => Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project));
        var controlledPath = WorkspaceFile("target-project/shared.SLDPRT");
        Set(child, "FullPath", controlledPath);
        Bind(controlledPath, Guid.NewGuid(), project);
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project);
    }

    private static void TestRegistrationBinding()
    {
        var plugin = New("PdmAddin");
        using var pane = (IDisposable)New("PdmTaskPaneControl");
        plugin.GetType().GetField("taskPaneControl", Members).SetValue(plugin, pane);
        var source = Node("workspace/View/registered/new.SLDPRT", null);
        WorkspaceFile("registered/new.SLDPRT");
        var ui = Node("workspace/View/registered/new.SLDPRT", null);
        plugin.GetType().GetField("currentTree", Members).SetValue(plugin, ui);
        var document = New("DocumentDto");
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        Set(document, "Id", id);
        Set(document, "ProjectId", project);
        Call(plugin, "ApplyRegisteredDocumentToMatchingInstances", source, document);
        Assert(Get(source, "DocumentId").Equals(id) && Get(ui, "DocumentId").Equals(id), "registration missed operation source or UI");
        Assert(ReadIdentity("TryRead", (string)Get(source, "FullPath")) == id, "retry would register another new document");
        Assert(Static("PdmDocumentIdentityStore", "ReadProjectId", Get(source, "FullPath")).Equals(project), "registration lost project");
    }

    private static void TestBindingFailure()
    {
        var field = Type("PdmDocumentIdentityStore").GetField("bindingDirectory", Members);
        var previous = field.GetValue(null);
        try
        {
            field.SetValue(null, FixtureFile("blocked-receipt-directory"));
            var node = Node("workspace/View/blocked/new.SLDPRT", null);
            WorkspaceFile("blocked/new.SLDPRT");
            var document = New("DocumentDto");
            Set(document, "Id", Guid.NewGuid());
            Set(document, "ProjectId", Guid.NewGuid());
            Expect<IOException>(() => Call(New("PdmAddin"), "ApplyRegisteredDocumentToMatchingInstances", node, document));
            Assert(Get(node, "DocumentId") == null, "failed binding left a silently trusted in-memory ID");
        }
        finally { field.SetValue(null, previous); }
    }

    private static void TestIncrementalPlan()
    {
        // A prior hidden WinForms fixture may install a UI context; this runner has no message loop.
        SynchronizationContext.SetSynchronizationContext(null);
        var root = Node("plan/main.SLDASM", Guid.NewGuid(), 0);
        var rootPath = FixtureFile("plan/main.SLDASM");
        var nodes = new List<object> { root };
        for (var i = 0; i < 20; i++)
        {
            var relative = "plan/part-" + i + ".SLDPRT";
            var path = FixtureFile(relative);
            var node = Node(relative, i < 10 ? (Guid?)Guid.NewGuid() : null);
            if (i < 10)
            {
                Set(node, "CurrentRevision", "W1");
                Set(node, "LatestRevision", "W1");
                Set(node, "LatestVersionSha256", Hash(File.ReadAllBytes(path)));
            }
            Children(root).Add(node);
            nodes.Add(node);
        }
        using var handler = new VersionsHandler((Guid)Get(root, "DocumentId"), Hash(File.ReadAllBytes(rootPath)), false);
        var plugin = Plugin(handler);
        var task = (Task)Call(plugin, "BuildBatchCheckInPlanAsync", Items(nodes.ToArray()), null, CancellationToken.None);
        task.GetAwaiter().GetResult();
        var plan = Get(task, "Result");
        var planned = ((IEnumerable)Get(plan, "Items")).Cast<object>().Select(item => Get(item, "Node")).ToArray();
        Assert(planned.Length == 11 && planned.Contains(root), "parent and ten new files were not scheduled together");
        Assert(nodes.Skip(1).Take(10).All(node => !planned.Contains(node)), "unchanged controlled originals would be submitted again");
        Assert((int)Get(plan, "SkippedFiles") == 10, "unchanged file count differs");
        Assert(handler.Writes == 0 && handler.Requests == 1, "plan repeated per-instance queries or wrote business data");
    }

    private static void TestProjectWorkspaceDirectory()
    {
        var root = Path.Combine(directory, "manager-workspace");
        var client = New("PdmApiClient", "http://fixture.invalid/");
        var manager = New("ControlledWorkspaceManager", client, root);
        var first = Manifest("P700002", "first.SLDASM");
        var second = Manifest("P700002", "second.SLDASM");
        var firstDirectory = (string)Call(manager, "WorkingDirectory", first);
        var secondDirectory = (string)Call(manager, "WorkingDirectory", second);
        Assert(firstDirectory == secondDirectory, "different root documents created separate project views");
        Assert(firstDirectory == Path.Combine(root, "View", "P700002"), "visible project view path differs");
        ((IDisposable)client).Dispose();
    }

    private static void TestWorkspaceMerge()
    {
        var source = Path.Combine(directory, "merge-source");
        var target = Path.Combine(directory, "merge-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var content = new byte[] { 1, 3, 5, 7 };
        File.WriteAllBytes(Path.Combine(source, "part.SLDPRT"), content);
        File.WriteAllText(Path.Combine(target, "unrelated.SLDPRT"), "must remain");
        File.WriteAllText(Path.Combine(target, "part.SLDPRT"), "old");
        var file = ControlledFile("part.SLDPRT", content);
        var client = New("PdmApiClient", "http://fixture.invalid/");
        var manager = New("ControlledWorkspaceManager", client, Path.Combine(directory, "manager-workspace"));
        Call(manager, "MergeManifestFiles", source, target, TypedArray("ControlledOpenFileDto", file));
        Assert(File.ReadAllBytes(Path.Combine(target, "part.SLDPRT")).SequenceEqual(content), "manifest file was not refreshed");
        Assert(File.ReadAllText(Path.Combine(target, "unrelated.SLDPRT")) == "must remain", "unrelated project file was deleted");
        ((IDisposable)client).Dispose();
    }

    private static void TestWorkspaceChangeGuard()
    {
        var target = Path.Combine(directory, "change-guard");
        Directory.CreateDirectory(target);
        var expected = new byte[] { 2, 4, 6 };
        var file = ControlledFile("part.SLDPRT", expected);
        var path = Path.Combine(target, "part.SLDPRT");
        File.WriteAllText(path, "changed");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var files = TypedArray("ControlledOpenFileDto", file);
        Assert(!(bool)Static("ControlledWorkspaceManager", "HasPotentialLocalChanges", target, files, Array.Empty<Guid>()), "stale read-only cache blocked safe refresh");
        File.SetAttributes(path, FileAttributes.Normal);
        Assert((bool)Static("ControlledWorkspaceManager", "HasPotentialLocalChanges", target, files, Array.Empty<Guid>()), "writable changed file was not protected");
    }

    private static void TestWorkspaceTraversalGuard()
    {
        var file = ControlledFile("..\\escape.SLDPRT", new byte[] { 1 });
        Expect<InvalidDataException>(() => Static("ControlledWorkspaceManager", "ManifestPath", Path.Combine(directory, "safe-root"), file));
    }

    private static void TestWorkspaceCacheCleanup()
    {
        var root = Path.Combine(directory, "maintenance-workspace");
        var working = WriteFixture(Path.Combine(root, "View", "P700002", "working.SLDASM"), "working");
        var snapshot = WriteFixture(Path.Combine(root, ".uplm", "Snapshots", "P700002", "latest.SLDASM"), "snapshot");
        var locked = WriteFixture(Path.Combine(root, ".uplm", "Snapshots", "P700002", "locked.SLDPRT"), "locked");
        var staging = WriteFixture(Path.Combine(root, ".uplm", "Staging", "partial.tmp"), "partial");
        var recovery = WriteFixture(Path.Combine(root, ".uplm", "Recovery", "failed", "rescue.SLDASM"), "recovery");
        File.SetAttributes(snapshot, FileAttributes.ReadOnly);
        var before = Upton.Pdm.Desktop.WorkspaceMaintenance.ReadUsage(root);
        Assert(before.WorkingFiles == 1 && before.SnapshotFiles == 2 && before.RecoveryFiles == 1, "workspace usage categories differ");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Upton.Pdm.Desktop.WorkspaceMaintenance.ClearReusableCache(root);
        }
        Assert(File.Exists(working), "cache cleanup deleted a working file");
        Assert(File.Exists(recovery), "cache cleanup deleted a recovery file");
        Assert(!File.Exists(snapshot) && !File.Exists(staging), "reusable cache was not cleaned");
        Assert(File.Exists(locked), "cache cleanup deleted an in-use file");
        Upton.Pdm.Desktop.WorkspaceMaintenance.ClearReusableCache(root);
        Assert(!File.Exists(locked), "released cache file was not cleaned on retry");
    }

    private static void TestWorkspaceGuidanceUi()
    {
        var control = (Control)New("ProjectDocumentsControl");
        var selector = Field(control, "projectSelector");
        var project = New("ProjectDto");
        var projectId = Guid.NewGuid();
        Set(project, "Id", projectId);
        Set(project, "Code", "P700005-4-4");
        Set(project, "Name", "工作区验收项目");
        var projects = TypedArray("ProjectDto", project);
        Call(control, "SetProjects", projects);
        Call(selector, "SelectProject", projectId);
        Call(control, "UpdateWorkspaceLocation");
        Call(control, "SetAuthenticatedUser", "designer");

        var root = Node("workspace-guidance/main.SLDASM", Guid.NewGuid(), 0);
        Set(root, "DisplayName", "打料机架（总）");
        Set(root, "CurrentRevision", "W1");
        Set(root, "LatestRevision", "W1");
        FixtureFile("workspace-guidance/main.SLDASM");
        var child = Node("workspace-guidance/part.SLDPRT", Guid.NewGuid());
        Set(child, "DisplayName", "HG-10-16MM");
        Set(child, "CurrentRevision", "W1");
        Set(child, "LatestRevision", "W2");
        FixtureFile("workspace-guidance/part.SLDPRT");
        Children(root).Add(child);
        Call(control, "SetTree", root);

        var viewButton = (Button)Field(control, "openLatest");
        var editButton = (Button)Field(control, "openEdit");
        var selectedState = (Label)Field(control, "selectedState");
        var selectedHint = (Label)Field(control, "selectedHint");
        var workspaceLocation = (Label)Field(control, "workspaceLocation");
        Assert(viewButton.Text == "查看最新版（只读）", "read-only action is ambiguous");
        Assert(editButton.Text == "检出并编辑" && editButton.Enabled, "edit action does not explain checkout");
        Assert(selectedState.Text.Contains("本地 W1 = PLM最新 W1"), "selected state does not compare local and PLM versions");
        Assert(selectedHint.Text.Contains("检出并编辑"), "selected hint does not explain the next action");
        Assert(workspaceLocation.Text.Contains("View") && workspaceLocation.Text.Contains("P700005-4-4"), "workspace location is not visible");
        Assert((string)Call(control, "LocalStateText", root) == "只读缓存", "local read-only cache state differs");
        Assert((string)Call(control, "PlmStateText", child) == "需更新 W1→W2", "PLM update state differs");

        Set(root, "CheckedOutBy", "designer");
        Call(control, "SetTree", root);
        Assert(editButton.Text == "继续编辑" && editButton.Enabled, "owned checkout is not presented as continue editing");
        Assert(selectedState.Text.Contains("已由你检出"), "owned checkout guidance differs");

        Set(root, "CheckedOutBy", "other.user");
        Call(control, "SetTree", root);
        Assert(!editButton.Enabled && selectedState.Text.Contains("other.user"), "other-user checkout is not clearly blocked");

        Set(root, "CheckedOutBy", "designer");
        Call(control, "SetTree", root);
        using (var host = new Form
        {
            ClientSize = new Size(460, 850),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10000, -10000)
        })
        using (var bitmap = new Bitmap(460, 850))
        {
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
            host.Show();
            Application.DoEvents();
            host.PerformLayout();
            control.PerformLayout();
            control.Refresh();
            var selectorControl = (Control)selector;
            var selectorBottom = selectorControl.PointToScreen(Point.Empty).Y + selectorControl.Height;
            var buttonTop = viewButton.PointToScreen(Point.Empty).Y;
            Assert(buttonTop - selectorBottom == 3, "workspace action row gap is not 3 pixels");
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, "workspace-guidance.png"));
            host.Hide();
        }
    }

    private static void TestProjectTreeLocalPathMapping()
    {
        var control = (Control)New("ProjectDocumentsControl");
        var selector = Field(control, "projectSelector");
        var project = New("ProjectDto");
        var projectId = Guid.NewGuid();
        Set(project, "Id", projectId);
        Set(project, "Code", "P700005-MAPPING");
        Set(project, "Name", "路径映射验收项目");
        control.GetType().GetField("workspaceRootResolver", Members)
            .SetValue(control, new Func<string>(() => Path.Combine(directory, "workspace")));
        Call(control, "SetProjects", TypedArray("ProjectDto", project));
        Call(selector, "SelectProject", projectId);

        var documentId = Guid.NewGuid();
        var downloaded = WorkspaceFile(Path.Combine("P700005-MAPPING", "nested", "mapped.SLDPRT"));
        Bind(downloaded, documentId, projectId);
        var node = Node("unrelated-location/mapped.SLDPRT", documentId);
        Set(node, "FullPath", string.Empty);
        Set(node, "CurrentRevision", "W1");
        Set(node, "LatestRevision", "W1");
        Call(control, "SetTree", node);

        Assert(string.Equals((string)Get(node, "FullPath"), downloaded, StringComparison.OrdinalIgnoreCase), "downloaded workspace file was not mapped to the project tree");
        Assert((string)Call(control, "LocalStateText", node) == "只读缓存", "mapped workspace file still appears undownloaded");
        control.Dispose();
    }

    private static void TestWorkspaceLocalStates()
    {
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes("controlled workspace version W1");
        var path = Path.Combine(directory, "workspace", "View", "P700005-STATE", "state.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, documentId, projectId, versionId, "W1", Hash(bytes), (long)bytes.Length), "version binding write failed");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", documentId);
        Set(request, "FileName", "state.SLDPRT");
        Set(request, "LatestRevision", "W1");
        var requests = TypedArray("WorkspaceDocumentStateRequest", request);
        var snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700005-STATE", "engineer", requests);
        var item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "ReadOnlyCache", "matching controlled file was not read-only cache");
        Assert((string)Get(item, "LocalRevision") == "W1", "local revision metadata was lost");

        Set(request, "LatestRevision", "W2");
        snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700005-STATE", "engineer", requests);
        item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "NeedsUpdate", "stale controlled version was not detected");

        File.SetAttributes(path, FileAttributes.Normal);
        File.AppendAllText(path, " changed");
        snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700005-STATE", "engineer", requests);
        item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "Modified", "writable changed file was not protected as a local modification");
    }

    private static void TestBatchControlledVersionBinding()
    {
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var oldBytes = System.Text.Encoding.UTF8.GetBytes("controlled workspace version W1");
        var newBytes = System.Text.Encoding.UTF8.GetBytes("controlled workspace version W2 after batch check-in");
        var path = Path.Combine(directory, "workspace", "View", "P700005-BATCH", "batch.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, oldBytes);
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, documentId, projectId, Guid.NewGuid(), "W1", Hash(oldBytes), (long)oldBytes.Length), "old version binding write failed");
        File.WriteAllBytes(path, newBytes);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", documentId);
        Set(request, "FileName", "batch.SLDPRT");
        Set(request, "LatestRevision", "W2");
        var requests = TypedArray("WorkspaceDocumentStateRequest", request);
        var snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700005-BATCH", "engineer", requests);
        var item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "IntegrityMismatch", "stale batch metadata did not reproduce the client warning");

        var version = Version("W2", Hash(newBytes));
        Set(version, "Id", Guid.NewGuid());
        Static("PdmAddin", "RememberControlledVersionIdentity", path, documentId, projectId, version);
        snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700005-BATCH", "engineer", requests);
        item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "ReadOnlyCache", "batch completion left the local file marked abnormal");
        Assert((string)Get(item, "LocalRevision") == "W2", "batch completion did not store the returned revision");
    }

    private static string WriteFixture(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        return path;
    }

    private static object Manifest(string projectCode, string rootRelativePath)
    {
        var manifest = New("ControlledOpenManifestDto");
        Set(manifest, "ProjectCode", projectCode);
        Set(manifest, "RootDocumentId", Guid.NewGuid());
        Set(manifest, "RootVersionId", Guid.NewGuid());
        Set(manifest, "RootRelativePath", rootRelativePath);
        return manifest;
    }

    private static object ControlledFile(string relativePath, byte[] content)
    {
        var file = New("ControlledOpenFileDto");
        Set(file, "DocumentId", Guid.NewGuid());
        Set(file, "VersionId", Guid.NewGuid());
        Set(file, "FileName", Path.GetFileName(relativePath));
        Set(file, "RelativePath", relativePath);
        Set(file, "FileLength", (long)content.Length);
        Set(file, "Sha256", Hash(content));
        return file;
    }

    private static string FixtureFile(string relative)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "isolated fake CAD fixture " + relative);
        return path;
    }

    private static string WorkspaceFile(string relative) => FixtureFile(Path.Combine("workspace", "View", relative));

    private static void Bind(string path, Guid id, Guid? project) =>
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWrite", path, id, project), "binding write failed");

    private static Guid? ReadIdentity(string method, string path)
    {
        var args = new object[] { path, Guid.Empty };
        return (bool)Type("PdmDocumentIdentityStore").GetMethod(method, Members).Invoke(null, args) ? (Guid?)args[1] : null;
    }

    private static object Plugin(VersionsHandler handler)
    {
        var plugin = New("PdmAddin");
        var client = New("PdmApiClient", "http://fixture.invalid/");
        var field = client.GetType().GetField("httpClient", Members);
        ((HttpClient)field.GetValue(client)).Dispose();
        field.SetValue(client, new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
        plugin.GetType().GetField("apiClient", Members).SetValue(plugin, client);
        return plugin;
    }

    private static void Resolve(object plugin, Array items, CancellationToken token) =>
        ((Task)Call(plugin, "ResolveBatchReferenceVersionsAsync", items, null, token)).GetAwaiter().GetResult();

    private static Array Items(params object[] nodes) =>
        TypedArray("BatchOperationItem", nodes.Select(node => New("BatchOperationItem", node, 0)).ToArray());

    private static object Node(string file, Guid? id, int kind = 1)
    {
        var node = New("CadTreeNode");
        Set(node, "FileName", file);
        Set(node, "FullPath", Path.Combine(directory, file));
        Set(node, "DocumentId", id);
        Set(node, "Kind", Enum.ToObject(Type("CadDocumentKind"), kind));
        return node;
    }

    private static object Version(string display, string hash)
    {
        var version = New("DocumentVersionDto");
        var revision = New("RevisionDto");
        Set(revision, "Display", display);
        Set(version, "Revision", revision);
        Set(version, "Sha256", hash);
        return version;
    }

    private static Type Type(string name) => addin.GetType("Upton.Pdm.SolidWorks." + name, true);
    private static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Members & ~BindingFlags.Static, null, args, null);
    private static object Get(object obj, string name) => obj.GetType().GetProperty(name, Members).GetValue(obj);
    private static object Field(object obj, string name) => obj.GetType().GetField(name, Members).GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetProperty(name, Members).SetValue(obj, value);
    private static IList Children(object node) => (IList)Get(node, "Children");
    private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Members).Invoke(obj, args);
    private static object Static(string type, string name, params object[] args) => Type(type).GetMethod(name, Members).Invoke(null, args);
    private static Array TypedArray(string type, params object[] values)
    {
        var array = Array.CreateInstance(Type(type), values.Length);
        for (var i = 0; i < values.Length; i++) array.SetValue(values[i], i);
        return array;
    }
    private static string Hash(byte[] bytes)
    {
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }
    private static Exception Unwrap(Exception e) => e is TargetInvocationException && e.InnerException != null ? Unwrap(e.InnerException) : e;
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (Exception e) { if (Unwrap(e) is T) return; throw; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + Unwrap(e)); }
    }

    private sealed class VersionsHandler : HttpMessageHandler
    {
        private readonly Guid documentId;
        private readonly string hash;
        private readonly bool includeNewerVersion;
        public int Requests;
        public int Writes;
        public VersionsHandler(Guid documentId, string hash, bool includeNewerVersion = true)
        {
            this.documentId = documentId;
            this.hash = hash;
            this.includeNewerVersion = includeNewerVersion;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("Writes are forbidden"); }
            Assert(request.RequestUri.AbsolutePath == "/api/documents/" + documentId + "/versions", "unexpected document lookup");
            var versions = new[]
            {
                new { revision = new { display = "W2" }, sha256 = "newer-different-content", propertySnapshot = new { SourceFileSha256 = "newer-different-source" } },
                new { revision = new { display = "W1" }, sha256 = "archive-copy-hash", propertySnapshot = new { SourceFileSha256 = hash } }
            };
            var body = new JavaScriptSerializer().Serialize(includeNewerVersion ? versions : versions.Skip(1).ToArray());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
