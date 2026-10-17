using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ImportedSourceArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StepSourceAndParentCommitAtomicallyAndReplayWithoutAnotherVersion(bool rejectSource)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var storage = DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>();
        ((ArchiveBatchReadReuseTests.Files)(object)storage).RejectAt = rejectSource ? 2 : 0;
        var workflow = new PdmWorkflowService(repository, storage, null!, TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("STEP-" + Guid.NewGuid().ToString("N"), "导入源整批存档", "admin", "vault", "release"), "admin", default);
        var batch = Guid.NewGuid(); var session = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch, session, "QA");
        var root = await workflow.RegisterDocumentAsync(new(project.Id, "ROOT", "总装", "ROOT.SLDASM", DocumentKind.Assembly, SourceSha256: new string('A', 64)), "admin", UserRole.Administrator, default, context);
        var source = await workflow.RegisterDocumentAsync(new(project.Id, "SOURCE", "STEP导入源", "SOURCE.stp", DocumentKind.Other, SourceSha256: new string('B', 64)), "admin", UserRole.Administrator, default, context);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
        var sourceNode = ArchiveBatchTests.Node(source) with { Role = ReferenceRole.ExternalFileDependency, Revision = null };
        var generated = ArchiveBatchTests.Node(root) with { DocumentId = null, FileName = "SOURCE.stp.SLDPRT", Kind = DocumentKind.Part, Status = ReferenceNodeStatus.Virtual, Revision = null };
        ArchiveBatchInput Input(PdmDocument doc, DocumentReferenceNode node, char hash, bool isRoot) => new(doc.Id,
            new("vault/" + doc.FileName, 128, new string(hash, 64), DateTimeOffset.UtcNow), new Dictionary<string, string?>(),
            new(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "admin", node, new string('F', 64)), "导入源测试", isRoot, null);
        var files = new[] { Input(root, ArchiveBatchTests.Node(root) with { Children = [generated, sourceNode] }, 'C', true), Input(source, sourceNode, 'D', false) };
        if (rejectSource)
        {
            await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CheckInBatchAsync(batch, "step", "admin", UserRole.Administrator, session, files, default));
            Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
            Assert.Empty(await repository.ListDocumentVersionsAsync(root.Id, default));
            Assert.Empty(await repository.ListDocumentVersionsAsync(source.Id, default));
            Assert.Null(await repository.FindArchiveBatchAsync(batch, default));
            return;
        }
        var receipt = await workflow.CheckInBatchAsync(batch, "step", "admin", UserRole.Administrator, session, files, default);
        var replay = await workflow.CheckInBatchAsync(batch, "step", "admin", UserRole.Administrator, session, files, default);
        Assert.Equal(2, receipt.Results.Count);
        Assert.Equal(receipt.Results.Select(item => item.Version!.Id), replay.Results.Select(item => item.Version!.Id));
        Assert.Single(await repository.ListDocumentVersionsAsync(source.Id, default));
        var rootVersion = Assert.Single(await repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.Contains(rootVersion.ReferenceSnapshot.Children, node => node.Status == ReferenceNodeStatus.Virtual && node.DocumentId == null);
        Assert.Contains(rootVersion.ReferenceSnapshot.Children, node => node.DocumentId == source.Id && node.Revision!.Value.Display == "W1" && node.Role == ReferenceRole.ExternalFileDependency);
        Assert.Equal(2, (await repository.ListDocumentsAsync(project.Id, default)).Count);
    }

    [Theory]
    [InlineData("SOURCE.stp", false)]
    [InlineData("SOURCE.exe", true)]
    [InlineData("SOURCE.zip", true)]
    public async Task OtherFilesCannotBypassRegistrationRules(string name, bool batch)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("STEP-" + Guid.NewGuid().ToString("N"), "导入源登记限制", "admin", "vault", "release"), "admin", default);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterDocumentAsync(new(project.Id, "SOURCE", "导入源", name, DocumentKind.Other, SourceSha256: new string('A', 64)),
            "admin", UserRole.Administrator, default, batch ? new(Guid.NewGuid(), Guid.NewGuid(), "QA") : null));
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }
}
