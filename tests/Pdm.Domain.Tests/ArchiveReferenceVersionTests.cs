using Upton.Pdm.Domain;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;
using System.Reflection;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveReferenceVersionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightAndBatchCommitUseArchivedParentBaselineWithoutRequiringChildCheckout(bool unknownReference)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var storage = DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>();
        var workflow = new PdmWorkflowService(repository, storage, null!, TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("REF-" + Guid.NewGuid().ToString("N"), "版本回填", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var child = await ArchiveBatchTests.Register(repository, project, "CHILD", DocumentKind.Part, session);
        await repository.CheckInVersionAsync(child.Id, "admin", session, ArchiveBatchTests.Commit(project, child, ArchiveBatchTests.Node(child), 'A'), default);
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var original = ArchiveBatchTests.Node(root) with { Children = [ArchiveBatchTests.Node(child) with { CheckedOutBy = null }] };
        var archived = await repository.CheckInVersionAsync(root.Id, "admin", session, ArchiveBatchTests.Commit(project, root, original, 'B', true), default);
        await repository.CheckoutAsync(root.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var incoming = original with { Children = [original.Children[0] with { Revision = null, DocumentId = unknownReference ? Guid.NewGuid() : child.Id }] };
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, root.Id, DateTimeOffset.UtcNow, "admin", incoming, "input");
        Task Preflight() => workflow.PreflightCheckInAsync(root.Id, "admin", UserRole.Administrator, session,
            new Dictionary<string, string?>(), snapshot, true, default, expectedVersionId: archived.Version!.Id, requireBaseVersion: true);
        var input = new ArchiveBatchInput(root.Id, new("vault/ROOT-new.SLDASM", 128, new string('C', 64), DateTimeOffset.UtcNow),
            new Dictionary<string, string?>(), snapshot, "修改总装", true, archived.Version!.Id);
        Task<ArchiveBatchReceipt> Commit() => workflow.CheckInBatchAsync(Guid.NewGuid(), "repair", "admin", UserRole.Administrator, session, [input], default);
        if (unknownReference)
        {
            await Assert.ThrowsAsync<PdmRuleException>(Preflight);
            await Assert.ThrowsAsync<PdmRuleException>(Commit);
            Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        }
        else
        {
            await Preflight();
            var receipt = await Commit();
            Assert.Equal("W1", receipt.Results.Single().Version!.ReferenceSnapshot.Children.Single().Revision!.Value.Display);
            Assert.Equal(2, (await repository.ListDocumentVersionsAsync(root.Id, default)).Count);
            Assert.Single(await repository.ListDocumentVersionsAsync(child.Id, default));
            Assert.Null((await repository.FindDocumentAsync(child.Id, default))!.CheckedOutBy);
        }
    }

    private static DocumentReferenceNode Part(string name) => new(Guid.NewGuid(), Guid.NewGuid(), "ROOT/" + name,
        name + ".SLDPRT", name, DocumentKind.Part, "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
    private static CadReferenceSnapshot Snapshot(DocumentReferenceNode root) => new(Guid.NewGuid(), Guid.NewGuid(), root.DocumentId!.Value,
        DateTimeOffset.UtcNow, "qa", root, "before");

    [Theory]
    [InlineData("A-XYPC834-SUS-M5", true)]
    [InlineData("BP1-六角头螺栓全螺纹C级", false)]
    public void RepeatedAndNestedInstancesRecoverTheirArchivedVersion(string name, bool nested)
    {
        var child = Part(name);
        var repeated = child with { NodeId = Guid.NewGuid(), InstancePath = child.InstancePath + "-2", Quantity = 3 };
        var owner = Part("SUB") with { Kind = DocumentKind.Assembly, Children = [child, repeated] };
        var root = Part("ROOT") with { Kind = DocumentKind.Assembly, Children = nested ? [owner] : [child, repeated] };
        var changed = nested ? root with { Children = [owner with { Children = [child, repeated with { Revision = null }] }] }
            : root with { Children = [child, repeated with { Revision = null }] };
        var result = ArchiveReferenceVersions.RestoreMissing(Snapshot(changed), root);
        Assert.True(ArchiveSafety.SameReferences(root, result.Root));
        Assert.NotEqual("before", result.Sha256);
        Assert.Equal("W1", repeated.Revision!.Value.Display);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("configuration")]
    [InlineData("ambiguous")]
    [InlineData("planned")]
    [InlineData("other-parent")]
    public void DoesNotGuessMissingVersions(string reason)
    {
        var child = Part("CHILD");
        var root = Part("ROOT") with { Kind = DocumentKind.Assembly, Children = [child] };
        var missing = child with { Revision = null, InstancePath = "ROOT/CHILD-moved" };
        if (reason == "new") missing = missing with { DocumentId = Guid.NewGuid() };
        if (reason == "configuration") missing = missing with { Configuration = "未存档配置" };
        if (reason == "ambiguous") root = root with { Children = [child, child with { InstancePath = "ROOT/CHILD-other", Revision = new(null, 2, false) }] };
        var request = Snapshot(root with { Children = [missing] });
        if (reason == "other-parent") root = root with { DocumentId = Guid.NewGuid() };
        var result = ArchiveReferenceVersions.RestoreMissing(request, root, reason == "planned" ? [child.DocumentId!.Value] : []);
        Assert.Null(result.Root.Children.Single().Revision);
        Assert.Same(request, result);
    }

    [Fact]
    public void KeepsExplicitVersionAndRestoresOnlyUnambiguousMovedOccurrence()
    {
        var child = Part("CHILD"); var root = Part("ROOT") with { Kind = DocumentKind.Assembly, Children = [child] };
        var current = root with { Children = [child with { Revision = new(null, 3, false) }, child with { InstancePath = "ROOT/NEW-INSTANCE", Revision = null }] };
        var result = ArchiveReferenceVersions.RestoreMissing(Snapshot(current), root);
        Assert.Equal("W3", result.Root.Children[0].Revision!.Value.Display);
        Assert.Equal("W1", result.Root.Children[1].Revision!.Value.Display);
    }
}
