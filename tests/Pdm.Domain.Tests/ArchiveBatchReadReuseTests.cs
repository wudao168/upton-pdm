using System.Reflection;
using System.Runtime.ExceptionServices;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveBatchReadReuseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedInstancesShareReadsButEveryPhysicalFileIsVerified(bool rejectLastFile)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("READS-" + Guid.NewGuid().ToString("N"), "整批读取复用", "admin", "vault", "release"), "admin", default);
        var batch = Guid.NewGuid(); var session = Guid.NewGuid();
        var context = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind, Guid? model = null) => await repository.ReserveArchiveDocumentAsync(
            new(project.Id, name, name, name + (kind == DocumentKind.Part ? ".SLDPRT" : kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDDRW"), kind,
                RelatedModelDocumentId: model), "admin", context, default);
        var parts = new List<PdmDocument>();
        for (var i = 0; i < 32; i++) parts.Add(await Reserve("PART-" + i, DocumentKind.Part));
        var models = new List<PdmDocument>();
        for (var i = 0; i < 8; i++) models.Add(await Reserve("MODEL-" + i, DocumentKind.Assembly));
        var drawings = new List<PdmDocument>();
        for (var i = 0; i < 6; i++) drawings.Add(await Reserve("DRAWING-" + i, DocumentKind.Drawing, models[0].Id));
        DocumentReferenceNode Model(PdmDocument document) => ArchiveBatchTests.Node(document) with {
            Children = parts.SelectMany(part => new[] { "A", "B", "C" }.Select(configuration => ArchiveBatchTests.Node(part) with {
                Configuration = configuration, InstancePath = document.FileName + "/" + part.FileName + "/" + configuration
            })).ToArray()
        };
        var files = parts.Select(document => (Document: document, Tree: ArchiveBatchTests.Node(document)))
            .Concat(models.Select(document => (Document: document, Tree: Model(document))))
            .Concat(drawings.Select(document => (Document: document, Tree: ArchiveBatchTests.Node(document) with { Children = [Model(models[0])] }))).ToArray();
        var proxy = DispatchProxy.Create<IPdmRepository, Reads>();
        var reads = (Reads)(object)proxy; reads.Target = repository;
        var storage = DispatchProxy.Create<IFileStorage, Files>();
        var verification = (Files)(object)storage; verification.RejectAt = rejectLastFile ? files.Length : 0;
        var workflow = new PdmWorkflowService(proxy, storage, null!, TimeProvider.System);
        var inputs = files.Select(file => new ArchiveBatchInput(file.Document.Id,
            new StoredFile("vault/" + file.Document.Id, 4, new string('A', 64), DateTimeOffset.UtcNow), new Dictionary<string, string?>(),
            new(Guid.NewGuid(), project.Id, file.Document.Id, DateTimeOffset.UtcNow, "admin", file.Tree, new string('B', 64)), "整批读取测试", file.Document.Id == models[0].Id, null)).ToArray();
        if (rejectLastFile)
        {
            await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CheckInBatchAsync(batch, "reads", "admin", UserRole.Administrator, session, inputs, default));
            Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
            Assert.Null(await repository.FindArchiveBatchAsync(batch, default));
        }
        else
        {
            var receipt = await workflow.CheckInBatchAsync(batch, "reads", "admin", UserRole.Administrator, session, inputs, default);
            Assert.Equal(files.Length, receipt.Results.Count);
            Assert.Equal(6, (await repository.ListDocumentRelationsAsync(project.Id, default)).Count);
            var version = Assert.Single(await repository.ListDocumentVersionsAsync(models[0].Id, default));
            Assert.Equal(96, version.ReferenceSnapshot.Children.Count);
            Assert.Equal(3, version.ReferenceSnapshot.Children.Select(child => child.Configuration).Distinct().Count());
            var priorReads = reads.BomReads; var priorVerifications = verification.Verified;
            var replay = await workflow.CheckInBatchAsync(batch, "reads", "admin", UserRole.Administrator, session, inputs, default);
            Assert.Equal(receipt.BatchId, replay.BatchId);
            Assert.Equal(priorReads, reads.BomReads); Assert.Equal(priorVerifications, verification.Verified);
            Assert.All(receipt.Results, result => Assert.True(result.VersionCreated));
        }
        Assert.Equal(files.Length, verification.Verified);
        Assert.Equal(4, reads.BomReads);
        Assert.All(reads.HistoryReads.Values, count => Assert.Equal(1, count));
        Assert.InRange(reads.DocumentReads, files.Length, files.Length * 2);
    }

    public class Reads : DispatchProxy
    {
        public IPdmRepository Target = null!;
        public int BomReads;
        public int DocumentReads;
        public Dictionary<Guid, int> HistoryReads { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.GetBomAsync)) BomReads++;
            if (method.Name == nameof(IPdmRepository.FindDocumentAsync)) DocumentReads++;
            if (method.Name == nameof(IPdmRepository.ListDocumentVersionsAsync))
            {
                var id = (Guid)args![0]!;
                HistoryReads[id] = HistoryReads.GetValueOrDefault(id) + 1;
            }
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
        }
    }
    public class Files : DispatchProxy
    {
        public int Verified;
        public int RejectAt;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IFileStorage.VerifyStoredFileAsync)) throw new InvalidOperationException("Unexpected storage write");
            Verified++;
            return Verified == RejectAt ? Task.FromException(new PdmConflictException("最后一个文件校验失败")) : Task.CompletedTask;
        }
    }
}
