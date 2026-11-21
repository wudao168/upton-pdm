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
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
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
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
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
    [InlineData(false, true, "ARCHIVED")]
    [InlineData(true, true, "ARCHIVED")]
    [InlineData(false, false, "LATEST")]
    [InlineData(true, false, null)]
    public async Task ManualDuplicateCheckUsesOneBatchAndRespectsFrozenSourceRevision(
        bool partialStructure, bool archivedRevisionExists, string? duplicateCode)
    {
        var repository = BusinessTestRepository.Create(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("SAVE-" + Guid.NewGuid().ToString("N"),
            "BOM保存", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var root = await ArchiveBatchTests.Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var boundRevision = RevisionLabel.Parse("W1");
        var occurrence = ArchiveBatchTests.Node(part) with { Revision = boundRevision };
        var tree = ArchiveBatchTests.Node(root) with
        {
            StructureIndexPending = partialStructure,
            Children = [occurrence with { InstancePath = "ROOT/PART-1" }, occurrence with { InstancePath = "ROOT/PART-2" }]
        };
        var rows = new[] { "ARCHIVED", "LATEST" }.Select((code, index) => new BomItem(Guid.NewGuid(), project.Id,
            BomKind.Standard, index + 1, code, code, 1, "个", null, null, "W1", true) { Source = "Manual" }).ToArray();
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, rows, default);

        var proxy = DispatchProxy.Create<IPdmRepository, CountingRepository>();
        var counter = (CountingRepository)(object)proxy;
        counter.Target = repository;
        counter.ReferenceSnapshot = ArchiveBatchTests.Commit(project, root, tree, 'A', true).ReferenceSnapshot;
        var latest = SourceProperties("W2", "LATEST");
        counter.SourceProperties = archivedRevisionExists ? [latest, SourceProperties("W1", "ARCHIVED")] : [latest];
        var workflow = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var inputs = rows.Select(row => new BomItemInput(row.Sequence, row.DrawingNumber, row.Name, row.Quantity,
            row.Unit, row.Material, row.Specification, row.Revision, row.IsComplete, Id: row.Id)).ToArray();

        var saved = await workflow.ReplaceBomAsync(project.Id, BomKind.Standard, inputs, "admin", UserRole.Administrator, default);

        Assert.Equal(1, counter.SourcePropertyBatches);
        Assert.Equal(new[] { root.Id, part.Id }.Order(), counter.RequestedSourceDocuments.Order());
        Assert.Equal(0, counter.VersionReads);
        Assert.All(saved, row => Assert.Equal(row.DrawingNumber == duplicateCode, row.ReconciliationStatus == "DuplicateSourcePending"));

        BomSourceVersionProperties SourceProperties(string revision, string code) => new(part.Id, RevisionLabel.Parse(revision),
            new Dictionary<string, string?> { ["全局/物料分类"] = "标准件", ["全局/物料编码"] = code, ["全局/物料名称"] = code });
    }

    [Theory]
    [InlineData(BomKind.Standard)]
    [InlineData(BomKind.Electrical)]
    public async Task MissingArchivedVersionDoesNotCreateWritebacksOrReadFullSnapshots(BomKind kind)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
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
        public int SourcePropertyBatches;
        public CadReferenceSnapshot? ReferenceSnapshot;
        public List<Guid> RequestedSourceDocuments = [];
        public IReadOnlyList<BomSourceVersionProperties> SourceProperties = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.GetLatestReferenceSnapshotAsync))
            {
                SnapshotReads++;
                if (ReferenceSnapshot is not null) return Task.FromResult<CadReferenceSnapshot?>(ReferenceSnapshot);
            }
            if (method.Name == nameof(IPdmRepository.ListBomSourceVersionPropertiesAsync))
            {
                SourcePropertyBatches++;
                RequestedSourceDocuments.AddRange((IReadOnlyCollection<Guid>)args![0]!);
                return Task.FromResult(SourceProperties);
            }
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
