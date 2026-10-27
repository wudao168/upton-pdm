using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PdfTitleBlockSnapshotTests
{
    [Fact]
    public async Task CapturesAll19SelectedFieldsFromFrozenVersionsAndActualReview()
    {
        var f = new Fixture();
        var snapshot = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project, f.Settings, default);
        var values = Assert.Single(snapshot.Drawings).Value.Values;
        Assert.Equal("原设计者", values["designer"]);
        Assert.Equal(values["designer"], values["drafter"]);
        Assert.Equal("2026-10-01", values["designedOn"]);
        Assert.Equal("实际审核员", values["reviewer"]);
        Assert.Equal("2026-10-20", values["reviewedOn"]);
        Assert.Equal("实际批准主管", values["approver"]);
        Assert.Equal("2026-10-21", values["approvedOn"]);
        Assert.Equal("是", values["wearPart"]);
        Assert.Equal("0204-001", values["materialCode"]);
        Assert.Equal("2.5 kg", values["weight"]);
        Assert.Equal("B", values["revision"]);
        Assert.Equal(18, values.Count);
        var prior = f.Package with { Id = Guid.NewGuid(), State = ReleasePackageState.Published,
            Scope = ReleaseScope.NonStandardLongLead, WholeSetMultiplier = 1,
            NonStandardBomSnapshot = [f.Item with { Quantity = 2 }], PublishedAt = f.Package.PublishedAt!.Value.AddDays(-1) };
        var frozen = PdfTitleBlockSnapshotBuilder.FreezeQuantities(f.Package with { PdfTitleBlockSnapshot = snapshot }, [prior]);
        Assert.Equal("4", frozen.Drawings[f.DrawingId].Values["quantity"]);
        Assert.True(frozen.QuantitiesFrozen);
        Assert.Same(frozen, PdfTitleBlockSnapshotBuilder.FreezeQuantities(f.Package with { PdfTitleBlockSnapshot = frozen }, []));
        f.Project = f.Project with { Name = "后来改名" };
        Assert.Equal("冻结项目", frozen.Drawings[f.DrawingId].Values["projectName"]);
    }

    [Fact]
    public async Task DisabledFieldsDoNotRequireDesignMetadataOrReadCurrentData()
    {
        var f = new Fixture();
        f.State.Versions[f.DrawingVersionId] = f.State.Versions[f.DrawingVersionId] with { PropertySnapshot = new Dictionary<string, string?>() };
        var settings = f.Settings with { PdfTitleBlockFill = new() { EnabledFields = ["projectNumber", "projectName"] } };
        var snapshot = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project, settings, default);
        Assert.Equal(2, snapshot.Drawings[f.DrawingId].Values.Count);
        var empty = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project,
            settings with { PdfTitleBlockFill = new() }, default);
        Assert.False(empty.Enabled);
        Assert.Empty(empty.Drawings);
    }

    [Theory]
    [InlineData("designer", "设计人")]
    [InlineData("designedOn", "设计日期")]
    public async Task DoesNotInferDesignerOrDateFromReviewWritebackSubmitter(string key, string label)
    {
        var f = new Fixture();
        f.State.Versions[f.DrawingVersionId] = f.State.Versions[f.DrawingVersionId] with { PropertySnapshot = new Dictionary<string, string?>() };
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package,
            f.Project, f.Settings with { PdfTitleBlockFill = new() { EnabledFields = [key] } }, default));
        Assert.Contains(label, error.Message);
    }

    [Fact]
    public async Task MaterialConflictOnlyBlocksTheSelectedField()
    {
        var f = new Fixture();
        f.State.Versions[f.ModelVersionId] = f.State.Versions[f.ModelVersionId] with
        { PropertySnapshot = new Dictionary<string, string?> { ["材质"] = "铝" } };
        var settings = f.Settings with { PdfTitleBlockFill = new() { EnabledFields = ["material"] } };
        await Assert.ThrowsAsync<PdmConflictException>(() => PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project, settings, default));
        var result = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project,
            settings with { PdfTitleBlockFill = new() { EnabledFields = ["name"] } }, default);
        Assert.Equal("零件名称", result.Drawings[f.DrawingId].Values["name"]);
    }

    [Fact]
    public void MissingPackageArtifactNeverFallsBackToAnotherBatchPdf()
    {
        var f = new Fixture();
        var old = new DocumentPreviewArtifact(DocumentPreviewFormat.Pdf, "old.pdf", 100, "old", f.Sha);
        var version = f.State.Versions[f.DrawingVersionId] with { Preview = old };
        var snapshot = new PdfTitleBlockSnapshot(["quantity"], new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>
        { [f.DrawingId] = new(f.DrawingVersionId, f.Sha, new Dictionary<string, string> { ["quantity"] = "4" }, [f.Item.Id]) }, true);
        Assert.Null(PdfTitleBlockSnapshotBuilder.DeliveryVersion(f.Package with { PdfTitleBlockSnapshot = snapshot }, version).Preview);
        var current = old with { StorageRelativePath = "batch2.pdf" };
        var package = f.Package with { PdfTitleBlockSnapshot = snapshot, PdfDeliveryArtifacts = new Dictionary<Guid, DocumentPreviewArtifact> { [f.DrawingId] = current } };
        Assert.Same(current, PdfTitleBlockSnapshotBuilder.DeliveryVersion(package, version).Preview);
        Assert.Same(old, version.Preview);
    }

    [Fact]
    public void RejectsUnknownSettingsAndNormalizesOrder()
    {
        Assert.Throws<PdmRuleException>(() => PdfTitleBlockSnapshotBuilder.Normalize(["unknown"]));
        Assert.Equal(new[] { "designer", "weight" }, PdfTitleBlockSnapshotBuilder.Normalize(["weight", "designer", "weight"]));
    }

    [Fact]
    public async Task PublicationValuesArePersistedOnceAndCannotBeReplacedAfterFreezing()
    {
        var f = new Fixture();
        var snapshot = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repository, f.Package, f.Project, f.Settings, default);
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateReleasePackageAsync(f.Package with { State = ReleasePackageState.Publishing, PdfTitleBlockSnapshot = snapshot }, default);
        var frozen = PdfTitleBlockSnapshotBuilder.FreezeQuantities(f.Package with { PdfTitleBlockSnapshot = snapshot,
            NonStandardBomSnapshot = [f.Item with { DrawingNumber = "正式料号" }] }, []);
        var saved = await repository.SaveReleasePdfTitleBlockAsync(f.Package.Id, frozen, default);
        Assert.Equal("正式料号", saved.PdfTitleBlockSnapshot!.Drawings[f.DrawingId].Values["materialCode"]);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.SaveReleasePdfTitleBlockAsync(f.Package.Id, snapshot, default));
        Assert.Equal("正式料号", (await repository.FindReleasePackageAsync(f.Package.Id, default))!.PdfTitleBlockSnapshot!.Drawings[f.DrawingId].Values["materialCode"]);
    }

    [Fact]
    public async Task PendingEarlyReleaseIsCheckedBeforeFreezingFinalIssueQuantity()
    {
        var f = new Fixture();
        var root = Path.Combine(Path.GetTempPath(), "pdm-pdf-freeze-order", Guid.NewGuid().ToString("N"));
        try
        {
            var project = f.Project with { VaultLocation = Path.Combine(root, "vault"), ReleaseLocation = Path.Combine(root, "release") };
            var package = f.Package with { State = ReleasePackageState.Publishing, PdfTitleBlockSnapshot = new(["quantity"], new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>()) };
            f.State.History = [package with { Id = Guid.NewGuid(), State = ReleasePackageState.Draft, Scope = ReleaseScope.NonStandardLongLead }];
            Directory.CreateDirectory(Path.Combine(project.VaultLocation, ".release-staging", package.Number));
            var publisher = new AtomicReleasePackagePublisher(TimeProvider.System, new UnusedConverter(), f.Repository);
            var error = await Assert.ThrowsAsync<PdmRuleException>(() => publisher.PublishAsync(package, project, [], default));
            Assert.Contains("长交期", error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class UnusedConverter : IServerPreviewConverter
    {
        public Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(ReleasePackage package, Project project,
            IReadOnlyList<ReleasePreviewSource> sources, string stagingDirectory, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public Guid DrawingId = Guid.NewGuid(), DrawingVersionId = Guid.NewGuid(), ModelVersionId = Guid.NewGuid();
        public string Sha = new('A', 64);
        public Project Project;
        public BomItem Item;
        public ReleasePackage Package;
        public PdmSystemSettings Settings;
        public IPdmRepository Repository = DispatchProxy.Create<IPdmRepository, FrozenRepository>();
        public FrozenRepository State => (FrozenRepository)(object)Repository;
        public Fixture()
        {
            Project = new(Guid.NewGuid(), "P700001-1", "冻结项目", "owner", "D:/Vault", "D:/Release", true);
            var modelId = Guid.NewGuid();
            var model = new DocumentReferenceNode(Guid.NewGuid(), modelId, "M", "M.SLDPRT", "零件", DocumentKind.Part, "默认", 1,
                ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
            var drawing = model with { DocumentId = DrawingId, Kind = DocumentKind.Drawing, Children = [model] };
            var now = DateTimeOffset.Parse("2026-10-22T10:00:00+08:00");
            State.Versions[DrawingVersionId] = new(DrawingVersionId, DrawingId, new('A', 1, false), DocumentVersionStatus.Work,
                "D.SLDDRW", 10, Sha, "writeback-user", now, "", new Dictionary<string, string?> { ["设计"] = "原设计者", ["设计日期"] = "2026/10/01" }, drawing, [], [], null, null, null, null);
            State.Versions[ModelVersionId] = State.Versions[DrawingVersionId] with { Id = ModelVersionId, DocumentId = modelId,
                PropertySnapshot = new Dictionary<string, string?>(), ReferenceSnapshot = model };
            Item = new(Guid.NewGuid(), Project.Id, BomKind.NonStandard, 1, "0204-001", "零件名称", 2, "001", "钢", "X-123", "B", true)
            { SourceDocumentId = modelId, SourceConfiguration = "默认", SurfaceTreatment = "喷漆", HeatTreatment = "无", Weight = "2.5 kg", IsWearPart = true };
            Package = new(Guid.NewGuid(), Project.Id, "RP-1", ReleasePackageState.Draft, null, "", "", [], now, now, null)
            { Scope = ReleaseScope.NonStandardWithDrawing, WholeSetMultiplier = 3, NonStandardBomSnapshot = [Item],
                VersionBindings = [new(DrawingId, DrawingVersionId, "A-W1", Sha, true, "D.SLDDRW"), new(modelId, ModelVersionId, "A-W1", Sha, true, "M.SLDPRT")] };
            var reviewId = Guid.NewGuid();
            State.Reviews = [new() { Id = reviewId, ProjectId = Project.Id, Number = "DR-1", CreatedBy = "submitter", CreatedAt = now,
                State = DrawingReviewPackageState.Approved, SupervisorName = "指定主管", SupervisorReviewedByName = "实际批准主管",
                SupervisorReviewedAt = DateTimeOffset.Parse("2026-10-20T20:00:00Z"), ApprovedAt = now,
                Items = [new() { Id = Guid.NewGuid(), PackageId = reviewId, BomItemId = Item.Id, DrawingNumber = Item.DrawingNumber, Name = Item.Name,
                    ModelDocumentId = modelId, ModelVersionId = ModelVersionId, ModelRevision = "A-W1", ModelSha256 = Sha, ModelCreatedBy = "original",
                    DrawingDocumentId = DrawingId, DrawingVersionId = DrawingVersionId, DrawingRevision = "A-W1", DrawingSha256 = Sha, DrawingCreatedBy = "original",
                    DrawingReviewerName = "实际审核员", DrawingReviewedAt = DateTimeOffset.Parse("2026-10-20T10:00:00+08:00") }] }];
            Settings = new(Project.VaultLocation, Project.ReleaseLocation) { PdfTitleBlockFill = new() { EnabledFields = PdfTitleBlockFields.All.Select(f => f.Key).ToArray() } };
        }
    }

    public class FrozenRepository : DispatchProxy
    {
        public Dictionary<Guid, DocumentVersion> Versions = new();
        public IReadOnlyList<DrawingReviewPackage> Reviews = [];
        public IReadOnlyList<ReleasePackage> History = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IPdmRepository.FindDocumentAsync) => Task.FromResult<PdmDocument?>(new((Guid)args![0]!, Guid.Empty, "D", "图纸", "D.SLDDRW",
                Versions.Values.First(v => v.DocumentId == (Guid)args[0]!).ReferenceSnapshot.Kind, DocumentLifecycleState.Work, RevisionLabel.InitialWork(), null, DateTimeOffset.UtcNow)),
            nameof(IPdmRepository.ListDocumentVersionsAsync) => Task.FromResult<IReadOnlyList<DocumentVersion>>(Versions.Values.Where(v => v.DocumentId == (Guid)args![0]!).ToArray()),
            nameof(IPdmRepository.FindDocumentVersionAsync) => Task.FromResult(Versions.GetValueOrDefault((Guid)args![1]!)),
            nameof(IPdmRepository.ListNativeReferenceIndexesAsync) => Task.FromResult<IReadOnlyList<DocumentReferenceNode>>([]),
            nameof(IPdmRepository.ListDrawingReviewPackagesAsync) => Task.FromResult(Reviews),
            nameof(IPdmRepository.ListReleasePackagesAsync) => Task.FromResult(History),
            _ => throw new InvalidOperationException("不应读取非冻结数据：" + method.Name)
        };
    }
}
