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

internal static partial class Program
{
    private static Assembly addin;
    private static string directory;
    private static int passed;
    private static int failed;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 && !(args.Length == 3 && (args[2] == "--archive-state-only" || args[2] == "--reference-cache-only" || args[2] == "--snapshot-only"))) return 2;
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
        if (args.Length == 3 && args[2] == "--snapshot-only")
        {
            Test("metadata refresh during base lookup cannot clear repeated reference versions", TestFrozenBatchReferences);
            Console.WriteLine($"Passed={passed}; Failed={failed}; no SolidWorks COM or production API calls.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Length == 3 && args[2] == "--reference-cache-only")
        {
            RunReferenceCacheTests();
            Console.WriteLine($"Passed={passed}; Failed={failed}; no SolidWorks COM or production API calls.");
            return failed == 0 ? 0 : 1;
        }
        if (args.Length == 3)
        {
            Test("desktop workspace identity and local changes", TestWorkspaceLocalStates);
            Test("committed version binding", TestBatchControlledVersionBinding);
            Test("manifest refresh retains committed metadata", TestCheckedInManifestRefresh);
            Test("external file archive cache", TestBatchArchiveSeedsProjectCache);
            Test("downloaded normalized copy binding", TestDownloadedCopyBinding);
            Test("post-commit edits remain unarchived", TestPostCommitLocalChange);
            Test("binding write failure remains visible", TestArchiveBindingFailure);
            Test("incomplete metadata remains unverified", TestIncompleteLocalMetadata);
            Test("damaged duplicate does not hide valid cache", TestReadOnlyDuplicateSelection);
            Test("different real edits retain conflict", TestChangedDuplicateIdentity);
            Test("managed archive cache", TestManagedArchiveCache);
            Test("server-confirmed metadata recovery", TestConfirmedArchiveRecovery);
            Test("matching timestamps never verify different file contents", TestLegacyTimestampDoesNotVerify);
            Test("archive completion refreshes metadata captured by an earlier scan", TestArchiveScanMetadataRefresh);
            Console.WriteLine($"Passed={passed}; Failed={failed}; no SolidWorks COM or production API calls.");
            return failed == 0 ? 0 : 1;
        }
        Test("stale source is updated alongside the refreshed UI tree", TestStaleSource);
        Test("300 repeated references resolve once to the matching historical version", TestResolveDuplicates);
        Test("changed reference is blocked, never assigned the latest version", TestChangedReference);
        Test("cancelled preflight makes no requests", TestCancellation);
        Test("embedded components skip independent registration and history checks but retain external dependencies", TestVirtualReferences);
        Test("archive dialog displays embedded hierarchy without separate upload selections", TestVirtualDialog);
        RunReferenceCacheTests();
        Test("successful child updates all operation instances but not a different local file", TestOperationInstances);
        Test("reference validation rejects unknown versions before upload", TestValidation);
        Test("copied legacy IDs are hints only, never automatic associations", TestLegacyIdentity);
        Test("10 controlled plus 10 new files retain independent identities on retry", TestIncrementalIdentity);
        Test("copied bindings cannot be replayed at another path or machine", TestBindingValidation);
        Test("same filename does not attach an external file during metadata refresh", TestNoFilenameBinding);
        Test("packed nested assemblies may reference sibling folders", TestNestedPackedAssembly);
        Test("new assemblies allow unregistered external files but block foreign controlled references", TestExternalReferences);
        Test("registration persists identity on the source even after UI tree refresh", TestRegistrationBinding);
        Test("structure actions lock during workspace work and recover afterward", TestSelectionIndependentStructureActions);
        Test("merged archive entry preserves whole-tree, checked, and context-menu scopes", TestMergedArchiveEntry);
        Test("binding failure stops before assigning an in-memory identity", TestBindingFailure);
        Test("incremental plan submits ten new files and parent, not ten unchanged originals", TestIncrementalPlan);
        Test("metadata refresh during base lookup cannot clear repeated reference versions", TestFrozenBatchReferences);
        Test("independent roots use separate visible working directories", TestProjectWorkspaceDirectory);
        Test("project workspace refresh preserves unrelated files", TestWorkspaceMerge);
        Test("writable changed files block refresh while stale read-only files may refresh", TestWorkspaceChangeGuard);
        Test("workspace manifests cannot escape the managed directory", TestWorkspaceTraversalGuard);
        Test("cache cleanup preserves working, recovery, and locked files", TestWorkspaceCacheCleanup);
        Test("workspace page explains location, local state, PLM state, and next action", TestWorkspaceGuidanceUi);
        Test("project tree resolves downloaded files by controlled workspace identity", TestProjectTreeLocalPathMapping);
        Test("desktop workspace states require identity and detect local version changes", TestWorkspaceLocalStates);
        Test("batch completion refreshes local controlled version metadata", TestBatchControlledVersionBinding);
        Test("post-check-in refresh cannot restore stale controlled-open metadata", TestCheckedInManifestRefresh);
        Test("batch archive seeds a safe read-only project workspace cache", TestBatchArchiveSeedsProjectCache);
        Test("downloaded upload copies retain their actual hash after unchanged completion", TestDownloadedCopyBinding);
        Test("post-commit local edits cannot be acknowledged as archived", TestPostCommitLocalChange);
        Test("binding write failure is reported without claiming server archive failed", TestArchiveBindingFailure);
        Test("incomplete local metadata never claims verified cache", TestIncompleteLocalMetadata);
        Test("valid copy wins over damaged readonly duplicate without deleting either", TestReadOnlyDuplicateSelection);
        Test("identical changed copies do not conflict but distinct writable edits do", TestChangedDuplicateIdentity);
        Test("managed archived sources do not create duplicate caches", TestManagedArchiveCache);
        Test("server-confirmed archive repairs stale metadata without hiding corrupt files or edits", TestConfirmedArchiveRecovery);
        Test("matching timestamps never verify different file contents", TestLegacyTimestampDoesNotVerify);
        Test("archive completion refreshes metadata captured by an earlier scan", TestArchiveScanMetadataRefresh);
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

    private static void RunReferenceCacheTests()
    {
        Test("three checkpoints share committed history while preserving 300 configured references", TestBatchReferenceHistoryReuse);
        Test("same-length same-mtime edits invalidate reference content at the next checkpoint", TestBatchReferenceContentChanges);
        Test("changed file identity queries its own history instead of reusing the old document", TestBatchReferenceIdentityChange);
        Test("history cache rejects other projects, servers, users and edit sessions", TestBatchReferenceCacheScope);
        Test("failed history lookup is retried but cached lookup still respects cancellation", TestBatchReferenceCacheFailure);
    }

    private static void TestBatchReferenceHistoryReuse()
    {
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Node("cache-root.SLDASM", null, 0);
        var bytes = new byte[] { 5, 4, 3, 2 };
        File.WriteAllBytes(Path.Combine(directory, "cache-part.SLDPRT"), bytes);
        for (var i = 0; i < 300; i++)
        {
            var child = Node("cache-part.SLDPRT", id);
            Set(child, "Configuration", "configuration-" + i);
            Children(root).Add(child);
        }
        using (var handler = new VersionsHandler(id, Hash(bytes)))
        {
            var plugin = Plugin(handler);
            var cache = ReferenceCache(plugin, project);
            for (var i = 0; i < 3; i++) ResolveCached(plugin, Items(root), project, cache);
            Assert(handler.Requests == 1, "three checkpoints repeated immutable history requests");
            Assert(Children(root).Cast<object>().All(node => (string)Get(node, "CurrentRevision") == "W1"), "content-matched historical version changed");
            var first = Children(root)[0];
            Set(first, "IsModifiedInSolidWorks", true);
            ResolveCached(plugin, Items(root), project, cache);
            Assert((string)Get(first, "CurrentRevision") == "W1*", "cached lookup masked an unsaved state change");
            Set(first, "IsModifiedInSolidWorks", false);
            ResolveCached(plugin, Items(root), project, cache);
            Assert((string)Get(first, "CurrentRevision") == "W1", "saved state did not refresh");
            ResolveCached(plugin, Items(root), project, ReferenceCache(plugin, project));
            Assert(handler.Requests == 2 && handler.Writes == 0, "a new batch reused the previous batch history or wrote data");
        }
    }

    private static void TestBatchReferenceContentChanges()
    {
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Node("cache-change-root.SLDASM", null, 0);
        var child = Node("cache-change.SLDPRT", id);
        var path = (string)Get(child, "FullPath");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(path, bytes);
        Children(root).Add(child);
        using (var handler = new VersionsHandler(id, Hash(bytes)))
        {
            var plugin = Plugin(handler);
            var cache = ReferenceCache(plugin, project);
            ResolveCached(plugin, Items(root), project, cache);
            var timestamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, new byte[] { 4, 3, 2, 1 });
            File.SetLastWriteTimeUtc(path, timestamp);
            Expect<InvalidOperationException>(() => ResolveCached(plugin, Items(root), project, cache));
            Assert(handler.Requests == 1 && handler.Writes == 0, "content recheck required stale history refresh or wrote data");
        }
    }

    private static void TestBatchReferenceIdentityChange()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Node("cache-identity-root.SLDASM", null, 0);
        var child = Node("cache-identity.SLDPRT", firstId);
        var bytes = new byte[] { 1, 9, 8, 4 };
        File.WriteAllBytes((string)Get(child, "FullPath"), bytes);
        Children(root).Add(child);
        using (var handler = new VersionsHandler(firstId, Hash(bytes)))
        {
            var plugin = Plugin(handler);
            var cache = ReferenceCache(plugin, project);
            ResolveCached(plugin, Items(root), project, cache);
            handler.DocumentId = secondId;
            Set(child, "DocumentId", secondId);
            ResolveCached(plugin, Items(root), project, cache);
            Assert(handler.Requests == 2 && (string)Get(child, "CurrentRevision") == "W1", "new document identity reused the old identity's history");
        }
    }

    private static void TestBatchReferenceCacheScope()
    {
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Node("cache-scope-root.SLDASM", null, 0);
        using (var handler = new VersionsHandler(id, "hash"))
        {
            var plugin = Plugin(handler);
            var cache = ReferenceCache(plugin, project);
            Expect<InvalidOperationException>(() => ResolveCached(plugin, Items(root), Guid.NewGuid(), cache));
            plugin.GetType().GetField("authenticatedUsername", Members).SetValue(plugin, "other-user");
            Expect<InvalidOperationException>(() => ResolveCached(plugin, Items(root), project, cache));
            plugin.GetType().GetField("authenticatedUsername", Members).SetValue(plugin, string.Empty);
            var client = Field(plugin, "apiClient");
            var originalScope = Get(client, "EditJournalScope");
            Set(client, "EditJournalScope", "http://other-server/|other-company");
            Expect<InvalidOperationException>(() => ResolveCached(plugin, Items(root), project, cache));
            Set(client, "EditJournalScope", originalScope);
            using (var otherHandler = new VersionsHandler(id, "hash"))
                Expect<InvalidOperationException>(() => ResolveCached(Plugin(otherHandler), Items(root), project, cache));
            Assert(handler.Requests == 0 && handler.Writes == 0, "scope rejection performed network requests");
        }
    }

    private static void TestBatchReferenceCacheFailure()
    {
        var id = Guid.NewGuid();
        var project = Guid.NewGuid();
        var root = Node("cache-failure-root.SLDASM", null, 0);
        var child = Node("cache-failure.SLDPRT", id);
        var bytes = new byte[] { 7, 5, 3, 1 };
        File.WriteAllBytes((string)Get(child, "FullPath"), bytes);
        Children(root).Add(child);
        using (var handler = new VersionsHandler(id, Hash(bytes)) { FailNext = true })
        {
            var plugin = Plugin(handler);
            var cache = ReferenceCache(plugin, project);
            Expect<InvalidOperationException>(() => ResolveCached(plugin, Items(root), project, cache));
            ResolveCached(plugin, Items(root), project, cache);
            Assert(handler.Requests == 2, "failed lookup was cached as a success");
            Expect<OperationCanceledException>(() => ((Task)Call(plugin, "ResolveBatchReferenceVersionsCachedAsync", Items(root), project,
                null, new CancellationToken(true), cache)).GetAwaiter().GetResult());
            Assert(handler.Requests == 2 && handler.Writes == 0, "cancellation made another request or write");
        }
    }

    private static object ReferenceCache(object plugin, Guid project) => New("PdmAddin+BatchReferenceVersionCache", project,
        Field(plugin, "checkoutSessionId"), Get(Field(plugin, "apiClient"), "EditJournalScope"), Field(plugin, "authenticatedUsername"));

    private static void TestVirtualReferences()
    {
        var root = Node("virtual-reference-root.SLDASM", null, 0);
        var nested = Node("STEP-child^ROOT.SLDASM", null, 0);
        var part = Node("STEP-part^STEP-child.SLDPRT", null);
        foreach (var node in new[] { nested, part })
        { Set(node, "Status", Enum.Parse(Type("CadReferenceStatus"), "Virtual")); File.WriteAllText((string)Get(node, "FullPath"), "embedded data"); }
        Children(root).Add(nested); Children(nested).Add(part);
        using var handler = new VersionsHandler(Guid.NewGuid(), "unused");
        var plugin = Plugin(handler);
        Resolve(plugin, Items(root), CancellationToken.None);
        Set(nested, "DocumentId", Guid.NewGuid()); Set(part, "DocumentId", Guid.NewGuid());
        Resolve(plugin, Items(root), CancellationToken.None);
        Assert(handler.Requests == 0 && handler.Writes == 0, "embedded components triggered independent registration/history lookup");
        var external = Node("real-external-under-virtual.SLDPRT", handler.DocumentId);
        var bytes = new byte[] { 1, 3, 5 }; File.WriteAllBytes((string)Get(external, "FullPath"), bytes);
        Children(nested).Add(external);
        // A real external file remains a normal reference even inside an embedded assembly.
        Expect<InvalidOperationException>(() => Resolve(plugin, Items(root), CancellationToken.None));
        Assert(handler.Requests == 1, "real external dependency beneath a virtual assembly was silently skipped");
    }

    private static void TestVirtualDialog()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
        var root = Node("embedded-ui-root.SLDASM", null, 0); File.WriteAllText((string)Get(root, "FullPath"), "parent file");
        var nested = Node("嵌入式子装配^ROOT.SLDASM", null, 0);
        var first = Node("嵌入式缸体^ROOT.SLDPRT", null); var second = Node("嵌入式活塞杆^ROOT.SLDPRT", null);
        foreach (var node in new[] { nested, first, second })
        { Set(node, "Status", Enum.Parse(Type("CadReferenceStatus"), "Virtual")); Set(node, "DisplayName", "嵌入式组件"); }
        Children(root).Add(nested); Children(nested).Add(first); Children(nested).Add(second);
        using var dialog = (Form)New("BatchOperationDialog", root, Items(root), Array.CreateInstance(Type("ProjectDto"), 0),
            null, "tester", Enum.Parse(Type("BatchOperationKind"), "CheckIn"), null, null, null, false, false, 0);
        dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show(); Application.DoEvents();
        var tree = (TreeView)Field(dialog, "files");
        Assert(tree.Nodes.Count == 1 && tree.Nodes[0].Nodes.Count == 1 && tree.Nodes[0].Nodes[0].Nodes.Count == 2
            && tree.Nodes[0].Nodes[0].IsExpanded, "embedded component rows were hidden or collapsed");
        Assert(((IEnumerable)Get(dialog, "SelectedItems")).Cast<object>().Count() == 1, "embedded components became separate upload items");
        Assert(((string)Call(dialog, "StateText", nested, "tester")).Contains("随父装配"), "embedded status still says unregistered");
        Assert(((Label)Field(dialog, "selectionSummary")).Text.Contains("3个嵌入式组件"), "selection summary omits embedded contents");
        Call(dialog, "ToggleShowCheckedOnly");
        Assert(tree.Nodes[0].Nodes[0].Nodes.Count == 2, "selected-parent filtering hides its embedded contents");
        using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, "embedded-components-ui.png"));
        dialog.Close();
        using var pane = (Control)New("PdmTaskPaneControl", "");
        Call(pane, "SetTree", root);
        Set(nested, "CurrentRevision", "W99"); Set(nested, "CheckedOutBy", "stale-user");
        Call(pane, "UpdateSelected", nested);
        var detail = ((Label)Field(pane, "selectedMeta")).Text;
        Assert(detail.Contains("随父装配") && !detail.Contains("W99") && !detail.Contains("未归档") && !detail.Contains("未检出"),
            "embedded details show an independent version or checkout requirement");
        foreach (var node in new[] { nested, first, second })
        {
            Set(node, "ImportedSourcePath", Path.Combine(directory, "YIHEDA_CDL21D150P", "CDL21-D150-P.stp"));
            Set(node, "DisplayName", "CDL21导入组件");
        }
        Call(pane, "SetTree", root); Call(pane, "UpdateSelected", nested);
        var importedDetail = ((Label)Field(pane, "selectedMeta")).Text;
        Assert(importedDetail.Contains("不随存档") && importedDetail.Contains("CDL21-D150-P.stp")
            && importedDetail.Contains("待确认保存归属") && !importedDetail.Contains("保存在所属装配体内")
            && !importedDetail.Contains("存储：所属父装配体内") && !importedDetail.Contains("W99"),
            "IC import was presented as verified self-contained embedded data");
        Assert((string)Static("PdmTaskPaneControl", "VersionText", nested) == "随总装核对",
            "imported cache shows an independent or embedded archive version");
        pane.GetType().GetField("authenticatedUsername", Members).SetValue(pane, "tester");
        Set(nested, "DocumentId", Guid.NewGuid()); Set(nested, "CheckedOutBy", "tester");
        Call(pane, "UpdateSelected", nested);
        Assert(!((Button)Field(pane, "checkoutButton")).Enabled,
            "stale cache identity enabled independent editing of imported content");
        Set(nested, "DocumentId", null); Set(nested, "CheckedOutBy", null);
        using var importDialog = (Form)New("BatchOperationDialog", root, Items(root), Array.CreateInstance(Type("ProjectDto"), 0),
            null, "tester", Enum.Parse(Type("BatchOperationKind"), "CheckIn"), null, null, null, false, false, 0);
        importDialog.ShowInTaskbar = false; importDialog.Opacity = 0; importDialog.Show(); Application.DoEvents();
        Assert(((IEnumerable)Get(importDialog, "SelectedItems")).Cast<object>().Count() == 1,
            "import UI enabled independent registration of cache files");
        var importSummary = ((Label)Field(importDialog, "selectionSummary")).Text;
        Assert(importSummary.Contains("3个导入组件") && !importSummary.Contains("3个嵌入式"),
            "import cache counts promise parent-only persistence");
        Assert(((string)Call(importDialog, "StateText", nested, "tester")).Contains("待确认保存归属"),
            "batch import status hides source dependency");
        using var importedBitmap = new Bitmap(importDialog.Width, importDialog.Height);
        importDialog.DrawToBitmap(importedBitmap, new Rectangle(Point.Empty, importedBitmap.Size));
        importedBitmap.Save(Path.Combine(directory, "imported-components-ui.png"));
        importDialog.Close();
        foreach (var node in new[] { nested, first, second }) Set(node,"ImportedStorageOwnerPath",Path.GetFullPath(Path.Combine(directory,"ROOT.SLDASM")));
        Call(pane,"UpdateSelected",nested);
        Assert((string)Static("PdmTaskPaneControl","VersionText",nested)=="随父装配"
            && ((Label)Field(pane,"selectedMeta")).Text.Contains("不单独登记或升版"),"persisted imports were not presented as parent-owned content");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void ResolveCached(object plugin, Array items, Guid project, object cache) =>
        ((Task)Call(plugin, "ResolveBatchReferenceVersionsCachedAsync", items, project, null, CancellationToken.None, cache)).GetAwaiter().GetResult();

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
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project);
        Bind((string)Get(child, "FullPath"), Guid.NewGuid(), Guid.NewGuid());
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project);
        var controlledPath = WorkspaceFile("target-project/shared.SLDPRT");
        Set(child, "FullPath", controlledPath);
        Bind(controlledPath, Guid.NewGuid(), project);
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", Items(root, child), project);
    }

    private static void TestRegistrationBinding()
    {
        var plugin = New("PdmAddin");
        using var pane = (IDisposable)New("PdmTaskPaneControl", "");
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

    private static void TestSelectionIndependentStructureActions()
    {
        using var pane = (Control)New("PdmTaskPaneControl", "");
        Call(pane, "SetAuthenticatedUser", "设计员", "designer");
        var root = Node("selection-independent/main.SLDASM", Guid.NewGuid(), 0);
        Set(root, "CurrentRevision", "W1");
        Set(root, "LatestRevision", "W2");
        Call(pane, "SetTree", root);

        var tree = (TreeView)Field(pane, "structureTree");
        var overall = (Button)Field(pane, "checkinButton");
        var property = (Button)Field(pane, "propertyEditButton");
        Assert(tree.SelectedNode == null, "fixture unexpectedly selected a design-tree node");
        Assert(overall.Enabled, "overall action still requires a selected design-tree node");
        Assert(property.Enabled, "property action still requires a selected design-tree node");

        Call(pane, "SetWorkspaceOperationState", true, "正在批量编辑本地属性");
        Assert(((Control)Field(pane, "tabsHost")).Enabled && !((Control)Field(pane, "projectPanel")).Enabled
            && !overall.Enabled && !property.Enabled,
            "archive actions must lock while diagnostics tabs remain accessible");
        var healthFilterType = Type("PdmTaskPaneControl+StructureHealthFilter");
        Call(pane, "ToggleHealthFilter", Enum.Parse(healthFilterType, "Outdated"));
        Assert(Field(pane, "activeHealthFilter").Equals(Enum.Parse(healthFilterType, "None")),
            "outdated-version filter ran during a workspace operation");
        Call(pane, "SetWorkspaceOperationState", false, "");
        Assert(((Control)Field(pane, "tabsHost")).Enabled && ((Control)Field(pane, "projectPanel")).Enabled,
            "plugin actions did not recover after the workspace operation");
        Assert(overall.Enabled && property.Enabled, "selection-independent actions did not recover after the workspace operation");
    }

    private static void TestMergedArchiveEntry()
    {
        using var pane = (Control)New("PdmTaskPaneControl", "");
        using var host = new Form
        {
            ClientSize = new Size(460, 850), ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000)
        };
        host.Controls.Add(pane);
        host.Show();
        Call(pane, "SetAuthenticatedUser", "设计员", "designer");
        var root = Node("merged-entry/main.SLDASM", Guid.NewGuid(), 0);
        var unowned = Node("merged-entry/unowned.SLDPRT", Guid.NewGuid());
        FixtureFile("merged-entry/main.SLDASM");
        FixtureFile("merged-entry/unowned.SLDPRT");
        Set(root, "CheckedOutBy", "designer");
        Children(root).Add(unowned);
        Call(pane, "SetTree", root);
        Application.DoEvents();

        var wholeRequests = 0;
        var checkInRequests = new List<EventArgs>();
        EventHandler wholeHandler = (_, _) => wholeRequests++;
        EventHandler checkInHandler = (_, args) => checkInRequests.Add(args);
        pane.GetType().GetEvent("BatchOperationRequested").AddEventHandler(pane, wholeHandler);
        var checkInEvent = pane.GetType().GetEvent("CheckInRequested");
        checkInEvent.AddEventHandler(pane, Delegate.CreateDelegate(checkInEvent.EventHandlerType, checkInHandler.Target, checkInHandler.Method));
        var archive = (Button)Field(pane, "checkinButton");
        var tree = (TreeView)Field(pane, "structureTree");
        tree.SelectedNode = null;
        Call(pane, "UpdateSelected", (object)null);
        Assert(archive.Enabled, "merged archive entry requires a selected tree row");
        archive.PerformClick();
        Assert(wholeRequests == 1 && checkInRequests.Count == 0, "unselected archive entry did not use whole-tree defaults");

        tree.SelectedNode = tree.Nodes[0];
        archive.PerformClick();
        Assert(wholeRequests == 2 && checkInRequests.Count == 0, "an unmarked selected row unexpectedly narrowed the archive scope");
        Call(pane, "SynchronizeCheckedDocument", root, true);
        Call(pane, "SynchronizeCheckedDocument", unowned, true);
        Call(pane, "UpdateSelected", root);
        archive.PerformClick();
        Assert(wholeRequests == 2 && checkInRequests.Count == 1, "checked archive scope was not dispatched once");
        var checkedArgs = checkInRequests[0];
        var checkedNodes = ((IEnumerable)Get(checkedArgs, "Nodes")).Cast<object>().ToArray();
        Assert((bool)Get(checkedArgs, "UsesCheckedSelection") && checkedNodes.Length == 2
            && checkedNodes.Contains(root) && checkedNodes.Contains(unowned),
            "merged archive silently discarded a checked document without an owned checkout");

        Call(pane, "UpdateStructureContextMenu", root);
        var contextArchive = (ToolStripMenuItem)Field(pane, "contextCheckIn");
        Assert(contextArchive.Enabled, "owned selected document lost context-menu archive access");
        contextArchive.PerformClick();
        var contextArgs = checkInRequests.Last();
        Assert(checkInRequests.Count == 2 && !(bool)Get(contextArgs, "UsesCheckedSelection")
            && ReferenceEquals(Get(contextArgs, "Node"), root)
            && ((IEnumerable)Get(contextArgs, "Nodes")).Cast<object>().Count() == 1,
            "context-menu archive inherited the checked batch instead of its selected document");

        Call(pane, "SetWorkspaceOperationState", true, "正在存档");
        Assert(!archive.Enabled, "merged archive remained enabled during an active operation");
        archive.PerformClick();
        Assert(wholeRequests == 2 && checkInRequests.Count == 2, "busy archive entry dispatched another operation");
        Call(pane, "SetWorkspaceOperationState", false, "");
        foreach (var previewFlag in new[] { "IsLatestReadOnlyPreview", "IsHistoricalPreview" })
        {
            Set(root, previewFlag, true);
            Call(pane, "UpdateSelected", root);
            Assert(!archive.Enabled, "merged archive remained enabled in " + previewFlag);
            archive.PerformClick();
            Assert(wholeRequests == 2 && checkInRequests.Count == 2, "read-only archive entry dispatched an operation");
            Set(root, previewFlag, false);
        }
        host.Hide();
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
                if (i == 0) Set(node, "CheckedOutBy", "another-designer");
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
        var scopeRows = ((IEnumerable)Type("PdmAddin").GetMethod("BuildArchiveScopeRows", Members).Invoke(null, new[] { Items(nodes.ToArray()), plan })).Cast<string[]>().ToArray();
        Assert(scopeRows.Length == 21 && scopeRows.Count(row => row[2] == "复用已有版本") == 10, "scope preview expanded the edit set or hid reused versions");
        using (var review = (Form)New("ArchiveScopeReviewDialog", scopeRows, 11, 10))
        {
            var grid = review.Controls.OfType<DataGridView>().Single();
            Assert(grid.ReadOnly && grid.Rows.Count == 21, "scope review is editable or lost files");
            Assert(review.CancelButton != null && review.AcceptButton != null, "scope review cannot be cancelled before submission");
        }
        SynchronizationContext.SetSynchronizationContext(null);
        Set(nodes[1], "IsModifiedInSolidWorks", true);
        Expect<InvalidOperationException>(() => ((Task)Call(plugin, "BuildBatchCheckInPlanAsync", Items(nodes.ToArray()), null, CancellationToken.None)).GetAwaiter().GetResult());
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
        Assert(firstDirectory != secondDirectory, "independent roots share a working directory");
        Assert(firstDirectory == Path.Combine(root, "View", "P700002", "装配工作区", ((Guid)Get(first, "RootDocumentId")).ToString("N")), "visible root view path differs");
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
        Assert((bool)Static("ControlledWorkspaceManager", "HasPotentialLocalChanges", target, files, Array.Empty<Guid>()), "unknown read-only content was not protected");
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, Get(file, "DocumentId"), Guid.NewGuid(), Guid.NewGuid(), "W1", Hash(File.ReadAllBytes(path)), new FileInfo(path).Length), "baseline binding failed");
        Assert(!(bool)Static("ControlledWorkspaceManager", "HasPotentialLocalChanges", target, files, Array.Empty<Guid>()), "known unchanged read-only baseline blocked safe refresh");
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
        var buttonHints = (ToolTip)Field(control, "toolTip");
        Assert(viewButton.Text == "打开最新" && buttonHints.GetToolTip(viewButton).Contains("只读"), "read-only action is ambiguous");
        Assert(editButton.Text == "获取编辑" && editButton.Enabled && buttonHints.GetToolTip(editButton).Contains("编辑权限"), "edit action does not explain checkout");
        Assert(selectedState.Text.Contains("本地 W1 = PLM最新 W1"), "selected state does not compare local and PLM versions");
        Assert(selectedHint.Text.Contains("获取编辑"), "selected hint does not explain the next action");
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
            Assert(buttonTop - selectorBottom == 6, "workspace action row gap is not 6 pixels");
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

    private static void TestLegacyTimestampDoesNotVerify()
    {
        var path = WriteFixture(Path.Combine(directory, "legacy-timestamp", "drawing.SLDDRW"), "locally changed content");
        var version = Version("W1", "different-server-content");
        Set(version, "PropertySnapshot", new Dictionary<string, string>
        {
            ["FileName"] = Path.GetFileName(path),
            ["LastWriteTimeUtc"] = File.GetLastWriteTimeUtc(path).ToString("O")
        });
        var hash = Hash(File.ReadAllBytes(path));
        Assert(!(bool)Static("PdmAddin", "VersionMatchesLocalFile", version, path, hash), "timestamp-only fallback hid a real local modification");
        ((Dictionary<string, string>)Get(version, "PropertySnapshot"))["SourceFileSha256"] = hash;
        Assert((bool)Static("PdmAddin", "VersionMatchesLocalFile", version, path, hash), "verified SaveAs source no longer matches its archived version");
    }

    private static void TestArchiveScanMetadataRefresh()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var path = WorkspaceFile("ARCHIVE-SCAN/part.SLDPRT");
        File.WriteAllText(path, "previous local record");
        Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, project,
            Guid.NewGuid(), "W1", Hash(File.ReadAllBytes(path)), new FileInfo(path).Length);
        var metadataArgs = new object[] { path, null };
        Assert((bool)Static("PdmDocumentIdentityStore", "TryReadControlledMetadata", metadataArgs), "cannot capture scan metadata");
        File.WriteAllText(path, "first archive final content");
        var committedVersion = Guid.NewGuid();
        Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, project,
            committedVersion, "W1", Hash(File.ReadAllBytes(path)), new FileInfo(path).Length);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", id); Set(request, "LatestRevision", "W1");
        var state = Static("WorkspaceLocalStateReader", "Evaluate", request, path, metadataArgs[1], "engineer");
        Assert((string)Get(state, "LocalState") == "ReadOnlyCache" && (Guid?)Get(state, "BoundVersionId") == committedVersion,
            "archive completion still uses the scan's old binding and reports an integrity mismatch");
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

    private static void TestCheckedInManifestRefresh()
    {
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var oldBytes = System.Text.Encoding.UTF8.GetBytes("controlled workspace version W1");
        var newBytes = System.Text.Encoding.UTF8.GetBytes("controlled workspace version W2 after check-in");
        var projectDirectory = Path.Combine(directory, "workspace", "View", "P700007-1");
        var path = Path.Combine(projectDirectory, "root.SLDASM");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllBytes(path, newBytes);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var file = ControlledFile("root.SLDASM", oldBytes);
        Set(file, "DocumentId", documentId);
        Set(file, "Revision", "W1");
        var oldVersionId = (Guid)Get(file, "VersionId");
        var files = Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
        ((IList)files).Add(file);
        var manifest = Manifest("P700007-1", "root.SLDASM");
        Set(manifest, "ProjectId", projectId);
        Set(manifest, "RootDocumentId", documentId);
        Set(manifest, "RootVersionId", oldVersionId);
        Set(manifest, "RootRevision", "W1");
        Set(manifest, "Files", files);

        var plugin = New("PdmAddin");
        ((IDictionary)Field(plugin, "controlledOpenManifests"))[projectDirectory + Path.DirectorySeparatorChar] = manifest;
        var version = Version("W2", "stored-copy-hash");
        var newVersionId = Guid.NewGuid();
        Set(version, "Id", newVersionId);
        Set(version, "PropertySnapshot", new Dictionary<string, string> { ["SourceFileSha256"] = Hash(newBytes) });
        Call(plugin, "RememberControlledVersionIdentityAndManifest", path, documentId, projectId, version);

        Assert((string)Get(file, "Revision") == "W2", "cached manifest retained the stale revision");
        Assert((Guid)Get(file, "VersionId") == newVersionId, "cached manifest retained the stale version id");
        Assert((string)Get(file, "Sha256") == Hash(newBytes), "cached manifest retained the archive-copy fingerprint");
        Assert((string)Get(manifest, "RootRevision") == "W2", "root manifest revision was not refreshed");

        var node = Node("root.SLDASM", documentId, 0);
        Set(node, "FullPath", path);
        plugin.GetType().GetMethods(Members)
            .Single(method => method.Name == "ApplyControlledOpenMetadata" && method.GetParameters().Length == 1)
            .Invoke(plugin, new[] { node });

        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", documentId);
        Set(request, "FileName", "root.SLDASM");
        Set(request, "LatestRevision", "W2");
        var snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), projectId, "P700007-1", "engineer", TypedArray("WorkspaceDocumentStateRequest", request));
        var item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "ReadOnlyCache", "refresh restored stale metadata and marked the checked-in file abnormal");
        Assert((string)Get(item, "LocalRevision") == "W2", "refresh restored the stale local revision");
    }

    private static void TestBatchArchiveSeedsProjectCache()
    {
        var workspaceRoot = Path.Combine(directory, "workspace");
        var projectCode = "P700007-1";
        var projectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes("standalone drawing archived in batch");
        var source = WriteFixture(Path.Combine(directory, "external", "drawing.SLDDRW"), "standalone drawing archived in batch");
        var version = Version("W1", "stored-copy-hash");
        Set(version, "Id", Guid.NewGuid());
        Set(version, "PropertySnapshot", new Dictionary<string, string> { ["SourceFileSha256"] = Hash(bytes) });

        var cached = (string)Static(
            "PdmAddin",
            "CacheCheckedInVersionInProjectView",
            workspaceRoot,
            projectCode,
            source,
            documentId,
            projectId,
            version);
        var expected = Path.Combine(workspaceRoot, "View", projectCode, "图档缓存", documentId.ToString("N"), "drawing.SLDDRW");
        Assert(string.Equals(cached, expected, StringComparison.OrdinalIgnoreCase), "batch archive did not create the project cache path");
        Assert(File.Exists(expected), "batch archive project cache file is missing");
        Assert((File.GetAttributes(expected) & FileAttributes.ReadOnly) != 0, "batch archive project cache is not read-only");

        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", documentId);
        Set(request, "FileName", "drawing.SLDDRW");
        Set(request, "LatestRevision", "W1");
        var snapshot = Static("WorkspaceLocalStateReader", "Read", workspaceRoot, projectId, projectCode, "engineer", TypedArray("WorkspaceDocumentStateRequest", request));
        var item = ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
        Assert((string)Get(item, "LocalState") == "ReadOnlyCache", "batch archived drawing still appears undownloaded");

        File.SetAttributes(expected, FileAttributes.Normal);
        File.WriteAllText(expected, "protected local content");
        File.SetAttributes(expected, FileAttributes.ReadOnly);
        var before = File.ReadAllText(expected);
        var refused = (string)Static(
            "PdmAddin",
            "CacheCheckedInVersionInProjectView",
            workspaceRoot,
            projectCode,
            source,
            documentId,
            projectId,
            version);
        Assert(string.IsNullOrWhiteSpace(refused), "different existing project file was treated as replaceable cache");
        Assert(File.ReadAllText(expected) == before, "different existing project file was overwritten");
    }

    private static void TestDownloadedCopyBinding()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var path = WorkspaceFile("HASH-BASE/drawing.SLDDRW");
        File.WriteAllText(path, "stored upload copy");
        var version = Version("W1", Hash(File.ReadAllBytes(path)));
        Set(version, "Id", Guid.NewGuid());
        Set(version, "PropertySnapshot", new Dictionary<string, string> { ["SourceFileSha256"] = Hash(System.Text.Encoding.UTF8.GetBytes("original source")) });
        Assert(string.IsNullOrEmpty((string)Call(New("PdmAddin"), "RememberControlledVersionIdentityAndManifest", path, id, project, version)), "valid stored copy rejected");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert((string)Get(ArchiveState("HASH-BASE", project, id), "LocalState") == "ReadOnlyCache", "stored copy was falsely marked abnormal");
    }

    private static void TestPostCommitLocalChange()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var path = WorkspaceFile("POST-COMMIT/drawing.SLDDRW");
        File.WriteAllText(path, "archived bytes");
        var version = Version("W1", Hash(File.ReadAllBytes(path))); Set(version, "Id", Guid.NewGuid());
        Static("PdmAddin", "RememberControlledVersionIdentity", path, id, project, version);
        File.WriteAllText(path, "unarchived later edit");
        var warning = (string)Call(New("PdmAddin"), "RememberControlledVersionIdentityAndManifest", path, id, project, version);
        Assert(warning.Contains("服务器存档已完成") && warning.Contains("未能同步"), "post-commit change silently acknowledged");
        Assert(File.ReadAllText(path) == "unarchived later edit", "local edit overwritten");
        Assert((string)Get(ArchiveState("POST-COMMIT", project, id), "LocalState") == "Modified", "local edit hidden as valid cache");
    }

    private static void TestArchiveBindingFailure()
    {
        var path = WorkspaceFile("BINDING-FAIL/drawing.SLDDRW");
        var version = Version("W1", Hash(File.ReadAllBytes(path))); Set(version, "Id", Guid.NewGuid());
        var field = Type("PdmDocumentIdentityStore").GetField("bindingDirectory", Members);
        var previous = field.GetValue(null);
        var blocker = WriteFixture(Path.Combine(directory, "binding-blocker"), "file, not directory");
        field.SetValue(null, blocker);
        try
        {
            var warning = (string)Call(New("PdmAddin"), "RememberControlledVersionIdentityAndManifest", path, Guid.NewGuid(), Guid.NewGuid(), version);
            Assert(warning.Contains("服务器存档已完成") && warning.Contains("磁盘空间"), "binding failure not surfaced in Chinese");
        }
        finally { field.SetValue(null, previous); }
    }

    private static void TestIncompleteLocalMetadata()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var path = WorkspaceFile("INCOMPLETE/drawing.SLDDRW"); Bind(path, id, project); File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert((string)Get(ArchiveState("INCOMPLETE", project, id), "LocalState") == "NeedsUpdate", "missing baseline considered verified");
    }

    private static void TestReadOnlyDuplicateSelection()
    {
        foreach (var damagedFirst in new[] { true, false })
        {
            var code = "COPY-SELECT-" + damagedFirst; var project = Guid.NewGuid(); var id = Guid.NewGuid();
            var first = WorkspaceFile(code + "/a.SLDDRW"); var second = WorkspaceFile(code + "/b.SLDDRW");
            File.WriteAllText(first, "valid bytes"); File.WriteAllText(second, "valid bytes");
            var version = Version("W1", Hash(File.ReadAllBytes(first))); Set(version, "Id", Guid.NewGuid());
            Static("PdmAddin", "RememberControlledVersionIdentity", first, id, project, version);
            Static("PdmAddin", "RememberControlledVersionIdentity", second, id, project, version);
            var bad = damagedFirst ? first : second; var good = damagedFirst ? second : first;
            File.WriteAllText(bad, "damaged cache"); File.SetAttributes(first, FileAttributes.ReadOnly); File.SetAttributes(second, FileAttributes.ReadOnly);
            var state = ArchiveState(code, project, id);
            Assert((string)Get(state, "LocalState") == "ReadOnlyCache" && (string)Get(state, "FullPath") == Path.GetFullPath(good), "damaged readonly cache displaced valid copy");
            Assert(((string)Get(state, "Message")).Contains(Path.GetFullPath(bad)) && File.Exists(bad), "damaged copy hidden or deleted");
        }
    }

    private static void TestChangedDuplicateIdentity()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid();
        var first = WorkspaceFile("COPY-EDIT/a.SLDDRW"); var second = WorkspaceFile("COPY-EDIT/b.SLDDRW");
        File.WriteAllText(first, "base"); File.WriteAllText(second, "base");
        var version = Version("W1", Hash(File.ReadAllBytes(first))); Set(version, "Id", Guid.NewGuid());
        Static("PdmAddin", "RememberControlledVersionIdentity", first, id, project, version);
        Static("PdmAddin", "RememberControlledVersionIdentity", second, id, project, version);
        File.WriteAllText(first, "same edit"); File.WriteAllText(second, "same edit");
        Assert((string)Get(ArchiveState("COPY-EDIT", project, id), "LocalState") == "Modified", "identical edits falsely conflict");
        File.SetAttributes(first, FileAttributes.ReadOnly); File.SetAttributes(second, FileAttributes.ReadOnly);
        Assert((string)Get(ArchiveState("COPY-EDIT", project, id), "LocalState") == "IntegrityMismatch", "identical readonly copies falsely conflict or hide stale base");
        File.SetAttributes(first, FileAttributes.Normal); File.SetAttributes(second, FileAttributes.Normal);
        File.WriteAllText(second, "distinct edit");
        Assert((string)Get(ArchiveState("COPY-EDIT", project, id), "LocalState") == "IdentityConflict", "distinct real edits were auto-selected");
        Assert(File.ReadAllText(first) == "same edit" && File.ReadAllText(second) == "distinct edit", "conflicting files altered");
    }

    private static void TestManagedArchiveCache()
    {
        var path = WorkspaceFile("MANAGED/drawing.SLDDRW"); var id = Guid.NewGuid();
        var version = Version("W1", Hash(File.ReadAllBytes(path))); Set(version, "Id", Guid.NewGuid());
        var cached = (string)Static("PdmAddin", "CacheCheckedInVersionInProjectView", Path.Combine(directory, "workspace"), "MANAGED", path, id, Guid.NewGuid(), version);
        Assert(cached == Path.GetFullPath(path) && Directory.GetFiles(Path.GetDirectoryName(path), "*.SLDDRW", SearchOption.AllDirectories).Length == 1, "managed archive created redundant bound copy");
    }

    private static object ArchiveState(string projectCode, Guid project, Guid id, string latestRevision = "W1")
    {
        var request = New("WorkspaceDocumentStateRequest"); Set(request, "DocumentId", id); Set(request, "LatestRevision", latestRevision);
        var snapshot = Static("WorkspaceLocalStateReader", "Read", Path.Combine(directory, "workspace"), project, projectCode, "engineer", TypedArray("WorkspaceDocumentStateRequest", request));
        return ((IEnumerable)Get(snapshot, "Items")).Cast<object>().Single();
    }

    private static void TestConfirmedArchiveRecovery()
    {
        foreach (var difference in new[] { "stored", "source", "changed", "project", "revision", "writable" })
        {
            var project = Guid.NewGuid(); var id = Guid.NewGuid(); var versionId = Guid.NewGuid();
            var code = "ARCHIVE-RECOVERY-" + difference; var path = WorkspaceFile(code + "/root.SLDASM");
            File.WriteAllText(path, "old archive"); var oldHash = Hash(File.ReadAllBytes(path));
            var oldVersion = Guid.NewGuid();
            Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, project, oldVersion,"W1",oldHash,new FileInfo(path).Length),"fixture binding failed");
            File.WriteAllText(path,"committed new archive"); var committed = Hash(File.ReadAllBytes(path));
            if (difference == "changed") File.WriteAllText(path,"unarchived change");
            File.SetAttributes(path,FileAttributes.ReadOnly);
            var state = ArchiveState(code,project,id); Set(state,"LatestRevision","W2");
            Assert((string)Get(state,"LocalState")=="IntegrityMismatch","fixture did not reproduce stale readonly checksum");
            if (difference == "writable") File.SetAttributes(path,FileAttributes.Normal);
            var before=File.ReadAllBytes(path); var attributes=File.GetAttributes(path);
            var repaired=(bool)Static("WorkspaceLocalStateReader","RecoverArchivedBinding",state,
                difference=="project"?Guid.NewGuid():project,versionId,difference=="revision"?"W3":"W2",
                difference=="source"?"different normalized upload hash":committed,difference=="source"?committed:null);
            Assert(repaired == (difference=="stored" || difference=="source"),"incorrect archive recovery: "+difference);
            Assert(File.ReadAllBytes(path).SequenceEqual(before) && File.GetAttributes(path)==attributes,"recovery changed file content or permissions");
            var metadata=Static("PdmDocumentIdentityStore","ReadBinding",path);
            Assert((Guid?)Get(metadata,"VersionId")== (repaired?versionId:oldVersion),"recovery overwrote an unverified base");
            if(repaired) Assert((string)Get(ArchiveState(code,project,id,"W2"),"LocalState")=="ReadOnlyCache","recovery did not clear stale integrity state");
        }
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
        ((Task)Call(plugin, "ResolveBatchReferenceVersionsAsync", items, Guid.NewGuid(), null, token)).GetAwaiter().GetResult();

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
    private static object Static(string type, string name, params object[] args)
    {
        var method = Type(type).GetMethod(name, Members);
        var parameters = method.GetParameters();
        if (args.Length < parameters.Length) args = args.Concat(parameters.Skip(args.Length).Select(parameter => parameter.DefaultValue)).ToArray();
        return method.Invoke(null, args);
    }
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
        public Guid DocumentId;
        private readonly string hash;
        private readonly bool includeNewerVersion;
        public int Requests;
        public int Writes;
        public bool FailNext;
        public VersionsHandler(Guid documentId, string hash, bool includeNewerVersion = true)
        {
            DocumentId = documentId;
            this.hash = hash;
            this.includeNewerVersion = includeNewerVersion;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("Writes are forbidden"); }
            Assert(request.RequestUri.AbsolutePath == "/api/documents/" + DocumentId + "/versions", "unexpected document lookup");
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"测试历史查询失败\"}") });
            }
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
