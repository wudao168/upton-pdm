using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveContextReferenceTests
{
    [Fact]
    public async Task BatchReportsEveryIndependentOrdinaryCycleBeforeCommit()
    {
        var f = await Fixture.Create();
        var docs = new[] { f.Layout, f.Sub, f.Part, f.Driver };
        var files = docs.Select((doc, index) => new ArchiveBatchFile(doc.Id,
            Commit(f.Project, doc, Node(doc) with
            {
                Children = [Node(docs[index ^ 1]) with { Role = ReferenceRole.ExternalFileDependency }]
            }, 'E', false))).ToArray();
        var error = Assert.Throws<PdmRuleException>(() => ArchiveBatchRules.Order(files));
        Assert.Contains("2组", error.Message);
        Assert.All(docs, doc => Assert.Contains(doc.FileName, error.Message));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContextBacklinksCommitAtomicallyAndOpenWithPinnedFiles(bool update, bool unverified)
    {
        var f = await Fixture.Create();
        var first = await f.Commit('A', false, unverified);
        var originalPart = first.Results.Single(item => item.Document.Id == f.Part.Id).Version!;
        var receipt = update ? await f.Commit('B', true, unverified) : first;
        var part = receipt.Results.Single(item => item.Document.Id == f.Part.Id).Version!;
        Assert.Equal(2, ArchiveBatchRules.References(part.ReferenceSnapshot).Count(node => node.IsContextReference || node.ReferenceTypeUnverified));
        Assert.All(ArchiveBatchRules.References(part.ReferenceSnapshot).Where(node => node.IsContextReference || node.ReferenceTypeUnverified), node =>
            Assert.Equal(receipt.Results.Single(item => item.Document.Id == node.DocumentId).Document.Revision, node.Revision));
        Assert.All(ArchiveBatchRules.References(originalPart.ReferenceSnapshot).Where(node => node.IsContextReference || node.ReferenceTypeUnverified), node =>
            Assert.Equal(RevisionLabel.InitialWork(), node.Revision));
        if (unverified)
        {
            Assert.True(part.ReferenceSnapshot.HasPendingStructureIndex);
            Assert.DoesNotContain(ArchiveBatchRules.References(part.ReferenceSnapshot), node => node.IsContextReference);
            Assert.Contains("ReferenceTypeUnverified", System.Text.Json.JsonSerializer.Serialize(part.ReferenceSnapshot));
        }
        var manifest = await f.Workflow.CreateControlledOpenManifestAsync(f.Part.Id, part.Id, false, false,
            "admin", UserRole.Administrator, default, treeScopeOnly: true);
        Assert.Equal(4, manifest.Files.Count);
        Assert.Equal(4, manifest.Files.Select(file => file.DocumentId).Distinct().Count());
        Assert.All(receipt.Results, result => Assert.Contains(manifest.Files,
            file => file.DocumentId == result.Document.Id && file.VersionId == result.Version!.Id));
        var layout = receipt.Results.Single(item => item.Document.Id == f.Layout.Id).Version!;
        var rootManifest = await f.Workflow.CreateControlledOpenManifestAsync(f.Layout.Id, layout.Id, false, false,
            "admin", UserRole.Administrator, default);
        Assert.Equal(4, rootManifest.Files.Count);
        Assert.Equal("设备布局.SLDASM", rootManifest.RootRelativePath);
        Assert.DoesNotContain(manifest.Files, file => file.FileName.Contains("副本"));
    }

    [Fact]
    public async Task AdministratorCanBlockUnverifiedLinksEvenWithoutClientWarningText()
    {
        var f = await Fixture.Create();
        await f.Repository.SaveArchiveWarningPolicyAsync(new() { BlockingCodes = ["ReferenceTypeUnknown"] }, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => f.Commit('A', false, true));
        Assert.Contains("管理员存档警告策略", error.Message);
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Layout.Id, default));
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Part.Id, default));
    }

    [Fact]
    public void UnverifiedMarkerCannotReplaceAssemblyMembersOrVerifiedContexts()
    {
        var node = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "root", "a.SLDASM", "a", DocumentKind.Assembly,
            "默认", 1, ReferenceNodeStatus.Normal, null, null, []) { ReferenceTypeUnverified = true };
        Assert.Throws<InvalidOperationException>(() => ArchiveDeferredReferences.IsUnverified(node));
        Assert.Throws<InvalidOperationException>(() => ArchiveDeferredReferences.IsUnverified(node with { Role = ReferenceRole.ExternalFileDependency, IsContextReference = true }));
    }

    [Fact]
    public async Task StandalonePartUpdateKeepsParentReadOnlyAndCanOpenItsEnvironment()
    {
        var f = await Fixture.Create();
        var first = await f.Commit('A', false);
        var previous = first.Results.Single(item => item.Document.Id == f.Part.Id).Version!;
        var part = await f.Repository.CheckoutAsync(f.Part.Id, "admin", f.Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var tree = previous.ReferenceSnapshot with { CheckedOutBy = "admin" };
        var commit = Commit(f.Project, part, tree, 'D', false) with { ExpectedVersionId = previous.Id };
        var receipt = await f.Repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "part-only", "admin", f.Session,
            [new(part.Id, commit)], default);
        var changed = Assert.Single(receipt.Results).Version!;
        var manifest = await f.Workflow.CreateControlledOpenManifestAsync(part.Id, changed.Id, false, false,
            "admin", UserRole.Administrator, default);
        Assert.Equal(4, manifest.Files.Count);
        Assert.Equal(changed.Id, manifest.Files.Single(file => file.IsRoot).VersionId);
        Assert.Single(await f.Repository.ListDocumentVersionsAsync(f.Layout.Id, default));
        Assert.Single(await f.Repository.ListDocumentVersionsAsync(f.Sub.Id, default));
        Assert.Null((await f.Repository.FindDocumentAsync(f.Layout.Id, default))!.CheckedOutBy);
    }

    [Fact]
    public async Task UnchangedPartDoesNotGainVersionWhenContextAssemblyChanges()
    {
        var f = await Fixture.Create();
        var first = await f.Commit('A', false);
        var partVersion = first.Results.Single(item => item.Document.Id == f.Part.Id).Version!;
        var layoutVersion = first.Results.Single(item => item.Document.Id == f.Layout.Id).Version!;
        var part = await f.Repository.CheckoutAsync(f.Part.Id, "admin", f.Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var layout = await f.Repository.CheckoutAsync(f.Layout.Id, "admin", f.Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var receipt = await f.Repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "unchanged-part", "admin", f.Session,
            [new(part.Id, Commit(f.Project, part, partVersion.ReferenceSnapshot, 'A', false) with { ExpectedVersionId = partVersion.Id }),
             new(layout.Id, Commit(f.Project, layout, layoutVersion.ReferenceSnapshot, 'E', true) with { ExpectedVersionId = layoutVersion.Id })], default);
        Assert.False(receipt.Results.Single(item => item.Document.Id == part.Id).VersionCreated);
        Assert.Single(await f.Repository.ListDocumentVersionsAsync(part.Id, default));
        var stillOriginal = (await f.Repository.ListDocumentVersionsAsync(part.Id, default)).Single();
        Assert.Equal(partVersion.ReferenceSnapshot, stillOriginal.ReferenceSnapshot);
        Assert.Equal(2, (await f.Repository.ListDocumentVersionsAsync(layout.Id, default)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryReferenceCycleRemainsBlockedWithCompleteChainAndNoWrites(bool nested)
    {
        var f = await Fixture.Create();
        var leaf = Node(f.Sub) with { Role = ReferenceRole.ExternalFileDependency };
        var part = Node(f.Part) with { Children = [leaf] };
        var root = Node(f.Sub) with { Children = [part] };
        var selected = nested ? f.Layout : f.Sub;
        var snapshot = Commit(f.Project, selected, nested ? Node(f.Layout) with { Children = [root] } : root, 'A', true).ReferenceSnapshot;
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => f.Workflow.PreflightCheckInAsync(selected.Id,
            "admin", UserRole.Administrator, f.Session, new Dictionary<string, string?>(), snapshot, true, default,
            requireBaseVersion: true, plannedDocumentIds: [f.Layout.Id, f.Sub.Id, f.Part.Id]));
        Assert.Contains("70004220.03.SLDASM → 70004220.M2-01.SLDPRT → 70004220.03.SLDASM", error.Message);
        var files = new[] { new ArchiveBatchFile(f.Sub.Id, Commit(f.Project, f.Sub, root, 'A', true)),
            new ArchiveBatchFile(f.Part.Id, Commit(f.Project, f.Part, part, 'B', false)) };
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "cycle", "admin", f.Session, files, default));
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Sub.Id, default));
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Part.Id, default));
    }

    [Fact]
    public async Task FailedParentRollsBackDeferredContextAndEarlierChildVersion()
    {
        var f = await Fixture.Create();
        var context = Node(f.Layout) with { Role = ReferenceRole.ExternalFileDependency, IsContextReference = true, Revision = null };
        var part = Node(f.Part) with { Children = [context] };
        var root = Node(f.Layout) with { Children = [part] };
        var batch = Guid.NewGuid();
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Repository.CheckInBatchVersionsAsync(batch, "stale-parent", "admin", f.Session,
            [new(f.Part.Id, Commit(f.Project, f.Part, part, 'A', false)),
             new(f.Layout.Id, Commit(f.Project, f.Layout, root, 'B', true) with { ExpectedVersionId = Guid.NewGuid() })], default));
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Part.Id, default));
        Assert.Empty(await f.Repository.ListDocumentVersionsAsync(f.Layout.Id, default));
        Assert.Null(await f.Repository.FindArchiveBatchAsync(batch, default));
        Assert.Equal("admin", (await f.Repository.FindDocumentAsync(f.Part.Id, default))!.CheckedOutBy);
        Assert.Equal(f.Session, (await f.Repository.FindDocumentAsync(f.Part.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public void ContextFlagCannotHideComponentsOrNestedDependencies()
    {
        var node = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "ROOT/PART", "PART.SLDPRT", "PART",
            DocumentKind.Part, "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []) { IsContextReference = true };
        Assert.Throws<InvalidOperationException>(() => ArchiveContextReferences.IsContext(node));
        Assert.Throws<InvalidOperationException>(() => ArchiveContextReferences.IsContext(node with { Kind = DocumentKind.Assembly }));
        var context = node with { Kind = DocumentKind.Assembly, Role = ReferenceRole.ExternalFileDependency };
        Assert.True(ArchiveContextReferences.IsContext(context));
        Assert.Throws<InvalidOperationException>(() => ArchiveContextReferences.IsContext(context with { Children = [node] }));
        Assert.Throws<InvalidOperationException>(() => ArchiveContextReferences.IsContext(context with { Status = ReferenceNodeStatus.Virtual }));
    }

    private sealed record Fixture(InMemoryPdmRepository Repository, PdmWorkflowService Workflow, Project Project,
        Guid Session, PdmDocument Layout, PdmDocument Sub, PdmDocument Part, PdmDocument Driver)
    {
        internal static async Task<Fixture> Create()
        {
            var repository = new InMemoryPdmRepository(TimeProvider.System);
            var project = await repository.CreateProjectAsync(new("CTX-" + Guid.NewGuid().ToString("N"), "上下文回归", "admin", "vault", "release"), "admin", default);
            var session = Guid.NewGuid();
            async Task<PdmDocument> Register(string name, DocumentKind kind)
            {
                var document = await repository.RegisterDocumentAsync(new(project.Id, name, name,
                    name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "admin", default);
                return await repository.CheckoutAsync(document.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
            }
            var layout = await Register("设备布局", DocumentKind.Assembly);
            var sub = await Register("70004220.03", DocumentKind.Assembly);
            var part = await Register("70004220.M2-01", DocumentKind.Part);
            var driver = await Register("70004220.M2-02", DocumentKind.Part);
            return new(repository, new(repository, DispatchProxy.Create<IFileStorage, Files>(), null!, TimeProvider.System),
                project, session, layout, sub, part, driver);
        }

        internal async Task<ArchiveBatchReceipt> Commit(char hash, bool update, bool unverified = false)
        {
            var docs = new[] { Layout, Sub, Part, Driver };
            if (update) docs = await Task.WhenAll(docs.Select(doc => Repository.CheckoutAsync(doc.Id, "admin", Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default)));
            DocumentReferenceNode Context(PdmDocument doc) => Node(doc) with { Role = ReferenceRole.ExternalFileDependency, IsContextReference = !unverified, ReferenceTypeUnverified = unverified, InstancePath = "上下文/" + doc.FileName };
            var driver = Node(docs[3]);
            var part = Node(docs[2]) with { Children = [driver with { Role = ReferenceRole.ExternalFileDependency }, Context(docs[1]), Context(docs[0])] };
            var sub = Node(docs[1]) with { Children = [part, driver] };
            var layout = Node(docs[0]) with { Children = [sub] };
            var trees = new[] { layout, sub, part, driver };
            var files = new List<ArchiveBatchFile>();
            for (var i = 0; i < docs.Length; i++)
            {
                var latest = (await Repository.ListDocumentVersionsAsync(docs[i].Id, default)).FirstOrDefault();
                var commit = ArchiveContextReferenceTests.Commit(Project, docs[i], trees[i], hash, i == 0) with { ExpectedVersionId = latest?.Id };
                await Workflow.PreflightCheckInAsync(docs[i].Id, "admin", UserRole.Administrator, Session, commit.Properties,
                    commit.ReferenceSnapshot, i == 0, default, expectedVersionId: latest?.Id, requireBaseVersion: true,
                    plannedDocumentIds: docs.Select(doc => doc.Id).ToArray());
                files.Add(new(docs[i].Id, commit));
            }
            return await Repository.CheckInBatchVersionsAsync(Guid.NewGuid(), Guid.NewGuid().ToString(), "admin", Session, files, default);
        }
    }

    private static DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber,
        doc.FileName, doc.Name, doc.Kind, "默认", 1, ReferenceNodeStatus.Normal, doc.Revision, "admin", []) { UsesNativeFileInventory = true };
    private static DocumentVersionCommit Commit(Project project, PdmDocument doc, DocumentReferenceNode tree, char hash, bool root) => new(
        new(".versions/" + Guid.NewGuid().ToString("N"), 128, new string(hash, 64), DateTimeOffset.UtcNow), "上下文回归",
        new Dictionary<string, string?>(), new(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "admin", tree, new string('F', 64)), [], [], IsProjectRoot: root)
        { RequireBaseVersion = true };
    public class Files : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType == typeof(Task)
            ? Task.CompletedTask : throw new InvalidOperationException("此回归不允许实际文件读写：" + method.Name);
    }
}
