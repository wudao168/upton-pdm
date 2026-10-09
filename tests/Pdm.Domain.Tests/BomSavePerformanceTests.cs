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
    public async Task ChangedInstancesShareVersionReadAndEnqueueOneBatch(BomKind kind)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var document = (await repository.ListDocumentsAsync(projectId, default)).First();
        var rows = Enumerable.Range(1, 1000).Select(index => new BomItem(Guid.NewGuid(), projectId, kind,
            index, "MAT-1", "物料", 1, "001", "钢", "M1", "W1", true)
        { Source = "Auto", SourceDocumentId = document.Id, SourceInstancePath = $"root/{index}" }).ToArray();
        await repository.ReplaceBomAsync(projectId, kind, rows, default);
        var proxy = DispatchProxy.Create<IPdmRepository, CountingRepository>();
        var counter = (CountingRepository)(object)proxy;
        counter.Target = repository;
        counter.Version = new DocumentVersion(Guid.NewGuid(), document.Id, document.Revision,
            DocumentVersionStatus.Work, "test", 0, "hash", "admin", DateTimeOffset.UtcNow, "test",
            new Dictionary<string, string?>(), null!, [], [], null, null, null, null);
        var workflow = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var inputs = rows.Select(row => new BomItemInput(row.Sequence, row.DrawingNumber, row.Name, row.Quantity,
            row.Unit, row.Material, row.Specification, row.Revision, true, SourceDocumentId: row.SourceDocumentId,
            Id: row.Id, SourceInstancePath: row.SourceInstancePath, Brand: "更新品牌")).ToArray();
        await workflow.ReplaceBomAsync(projectId, kind, inputs, "admin", UserRole.Administrator, default);
        Assert.Equal(1, counter.SettingsReads);
        Assert.Equal(1, counter.VersionReads);
        Assert.Equal(1, counter.WritebackBatches);
        Assert.Equal(1000, counter.WritebackCount);
        var requests = await repository.ListCadPropertyWritebacksAsync(projectId, default);
        Assert.Equal(1000, requests.Count(request => rows.Any(row => row.Id == request.BomItemId)));
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
        Assert.Equal(rows.Select(row => (row.Id, row.Quantity, row.SourceInstancePath)),
            saved.Select(row => (row.Id, row.Quantity, row.SourceInstancePath)));
    }

    public class CountingRepository : DispatchProxy
    {
        public IPdmRepository Target = null!;
        public int SnapshotReads;
        public int SettingsReads;
        public int VersionReads;
        public int WritebackBatches;
        public int WritebackCount;
        public DocumentVersion? Version;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.GetLatestReferenceSnapshotAsync)) SnapshotReads++;
            if (method.Name == nameof(IPdmRepository.GetSystemSettingsAsync)) SettingsReads++;
            if (method.Name == nameof(IPdmRepository.ListDocumentVersionsAsync))
            {
                VersionReads++;
                if (Version is not null) return Task.FromResult<IReadOnlyList<DocumentVersion>>([Version]);
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
