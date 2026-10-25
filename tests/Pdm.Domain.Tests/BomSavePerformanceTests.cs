using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomSavePerformanceTests
{
    [Theory]
    [InlineData(BomKind.Standard)]
    [InlineData(BomKind.NonStandard)]
    public async Task ChangedInstancesShareLightweightVersionReadAndEnqueueOneBatch(BomKind kind)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var documents = Enumerable.Range(0, 17).Select(_ => Guid.NewGuid()).ToArray();
        var rows = Enumerable.Range(1, 1000).Select(index => new BomItem(Guid.NewGuid(), projectId, kind,
            index, "MAT-1", "物料", 1, "001", "钢", "M1", "W1", true)
        { Source = "Auto", SourceDocumentId = documents[index % documents.Length], SourceInstancePath = $"root/{index}" }).ToArray();
        await repository.ReplaceBomAsync(projectId, kind, rows, default);
        var proxy = DispatchProxy.Create<IPdmRepository, CountingRepository>();
        var counter = (CountingRepository)(object)proxy;
        counter.Target = repository;
        counter.LatestVersions = documents.ToDictionary(id => id,
            id => new DocumentVersionVerification(Guid.NewGuid(), id, "W3", 0, "hash", null));
        var workflow = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var inputs = rows.Select(row => new BomItemInput(row.Sequence, row.DrawingNumber, row.Name, row.Quantity,
            row.Unit, row.Material, row.Specification, row.Revision, true, SourceDocumentId: row.SourceDocumentId,
            Id: row.Id, SourceInstancePath: row.SourceInstancePath, Brand: "更新品牌")).ToArray();
        await workflow.ReplaceBomAsync(projectId, kind, inputs, "admin", UserRole.Administrator, default);
        Assert.Equal(1, counter.SettingsReads);
        Assert.Equal(0, counter.VersionReads);
        Assert.Equal(1, counter.VersionVerificationReads);
        Assert.Equal(documents.Order(), counter.RequestedDocuments.Order());
        Assert.Equal(1, counter.WritebackBatches);
        Assert.Equal(1000, counter.WritebackCount);
        var requests = await repository.ListCadPropertyWritebacksAsync(projectId, default);
        Assert.Equal(1000, requests.Count(request => rows.Any(row => row.Id == request.BomItemId)));
        Assert.All(requests.Where(request => rows.Any(row => row.Id == request.BomItemId)), request => {
            Assert.Equal(counter.LatestVersions[request.SourceDocumentId].Id, request.ExpectedVersionId);
            Assert.Equal("W3", request.ExpectedRevision);
            Assert.Equal("更新品牌", request.Properties["品牌"]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavePreservesInstancesAndOnlyReadsDesignTreeForManualDuplicates(bool includeManual)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var documentId = Guid.NewGuid();
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var rows = Enumerable.Range(1, 1000).Select(index => new BomItem(
            Guid.NewGuid(), projectId, BomKind.Standard, index, "MAT-1", "物料", index, "001", null, "M1", "W1", true)
        {
            Source = "Auto", SourceDocumentId = documentId, SourceInstancePath = $"root/{index}",
            IsManuallyOverridden = true
        }).ToList();
        if (includeManual)
            rows.Add(new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1001, "MAT-2", "人工项", 1, "001", null, "M2", "W1", true) { Source = "Manual" });
        await repository.ReplaceBomAsync(projectId, BomKind.Standard, rows, default);
        var proxy = DispatchProxy.Create<IPdmRepository,CountingRepository>();
        var counter = (CountingRepository)(object)proxy;
        counter.Target = repository;
        var workflow = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var inputs = rows.Select(row => new BomItemInput(row.Sequence, row.DrawingNumber, row.Name,
            row.Quantity, row.Unit, row.Material, row.Specification, row.Revision, row.IsComplete,
            SourceDocumentId: row.SourceDocumentId, Id: row.Id, SourceInstancePath: row.SourceInstancePath)).ToArray();

        var saved = await workflow.ReplaceBomAsync(projectId, BomKind.Standard, inputs, "admin", UserRole.Administrator, default);

        Assert.Equal(includeManual ? 1 : 0, counter.SnapshotReads);
        if (!includeManual) Assert.Equal(0, counter.VersionReads);
        Assert.Equal(0, counter.VersionVerificationReads);
        Assert.Equal(rows.Select(row => (row.Id, row.Quantity, row.SourceInstancePath)),
            saved.Select(row => (row.Id, row.Quantity, row.SourceInstancePath)));
    }

    [Theory]
    [InlineData(BomKind.Standard)]
    [InlineData(BomKind.Electrical)]
    public async Task MissingArchivedVersionDoesNotCreateWritebacksOrReadFullSnapshots(BomKind kind)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var row = new BomItem(Guid.NewGuid(), projectId, kind, 1, "", "未存档物料", 1, "001", null, "M1", "W1", false)
        { Source = "Auto", SourceDocumentId = Guid.NewGuid(), SourceInstancePath = "root/part" };
        await repository.ReplaceBomAsync(projectId, kind, [row], default);
        var proxy = DispatchProxy.Create<IPdmRepository, CountingRepository>();
        var counter = (CountingRepository)(object)proxy;
        counter.Target = repository;
        var workflow = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var saved = await workflow.ReplaceBomAsync(projectId, kind,
            [new(1, "", row.Name, 1, "001", null, "M1", "W1", false,
                SourceDocumentId: row.SourceDocumentId, Id: row.Id, SourceInstancePath: row.SourceInstancePath, Brand: "更新品牌")],
            "admin", UserRole.Administrator, default);
        Assert.Equal("更新品牌", Assert.Single(saved).Brand);
        Assert.Equal(0, counter.VersionReads);
        Assert.Equal(kind == BomKind.Electrical ? 0 : 1, counter.VersionVerificationReads);
        Assert.Equal(0, counter.WritebackBatches);
    }

    public class CountingRepository : DispatchProxy
    {
        public IPdmRepository Target = null!;
        public int SnapshotReads;
        public int SettingsReads;
        public int VersionReads;
        public int WritebackBatches;
        public int WritebackCount;
        public int VersionVerificationReads;
        public List<Guid> RequestedDocuments = [];
        public Dictionary<Guid, DocumentVersionVerification> LatestVersions = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.GetLatestReferenceSnapshotAsync)) SnapshotReads++;
            if (method.Name == nameof(IPdmRepository.GetSystemSettingsAsync)) SettingsReads++;
            if (method.Name == nameof(IPdmRepository.ListDocumentVersionsAsync))
            {
                VersionReads++;
            }
            if (method.Name == nameof(IPdmRepository.ListDocumentVersionVerificationsAsync))
            {
                VersionVerificationReads++;
                var requests = (IReadOnlyList<DocumentVersionVerificationRequest>)args![0]!;
                RequestedDocuments.AddRange(requests.Select(request => request.DocumentId));
                return Task.FromResult<IReadOnlyList<DocumentVersionVerificationResult>>(requests.Select(request =>
                    new DocumentVersionVerificationResult(request.DocumentId, LatestVersions.GetValueOrDefault(request.DocumentId), null)).ToArray());
            }
            if (method.Name == nameof(IPdmRepository.EnqueueCadPropertyWritebackBatchAsync))
            {
                WritebackBatches++;
                WritebackCount += ((IReadOnlyList<CadPropertyWriteback>)args![0]!).Count;
            }
            return method.Invoke(Target, args);
        }
    }
}
