using System.Collections.Concurrent;
using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionDrawingValidityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrawingQrUsesPdfRuleAndFrozenModelWithoutRequiringPdf(bool pdfReady)
    {
        var fixture = await CreateFixtureAsync();
        Store<ReleasePackage>(fixture.Repository, "packages")[fixture.Package.Id] = fixture.Package with
        { NonStandardBomSnapshot = fixture.Package.NonStandardBomSnapshot!.Select(item => item with { Specification = "PLM-FROZEN-MODEL" }).ToArray() };
        Store<DocumentVersion>(fixture.Repository, "versions")[fixture.DrawingVersion.Id] = fixture.DrawingVersion with
        { Preview = pdfReady ? fixture.DrawingVersion.Preview : null, PropertySnapshot = new Dictionary<string, string?> { ["型号"] = "OLD-CAD-MODEL" } };

        var drawing = Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, true, fixture.Drawing.ProjectId, default));

        Assert.Equal(PdfReleaseIdentity.Display("PLM-FROZEN-MODEL", "B"), drawing.QrContent);
        Assert.Equal(pdfReady, drawing.PdfReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteDrawingDisappearsFromProductionWithoutFallingBackToOlderRevision(bool includeHistory)
    {
        var fixture = await CreateFixtureAsync();
        var oldVersion = fixture.DrawingVersion with { Id = Guid.NewGuid(), Revision = RevisionLabel.Released('A') };
        Store<DocumentVersion>(fixture.Repository, "versions")[oldVersion.Id] = oldVersion;
        Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, false, fixture.Drawing.ProjectId, default));

        await fixture.Repository.ObsoleteDocumentAsync(fixture.Drawing.Id, "admin", default);

        Assert.Empty(await fixture.Service.ListAsync("admin", UserRole.Administrator, includeHistory, fixture.Drawing.ProjectId, default));
        Assert.Empty(await fixture.Service.ListIndexAsync("admin", UserRole.Administrator, includeHistory, default));
        Assert.NotNull(await fixture.Repository.FindDocumentVersionAsync(fixture.Drawing.Id, oldVersion.Id, default));
        var error = await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Drawing.Id, oldVersion.Id, default));
        Assert.Contains("已作废", error.Message);
    }

    [Fact]
    public async Task ProductionUseRechecksObsoleteStateAfterTheListWasLoaded()
    {
        var fixture = await CreateFixtureAsync();
        var cached = Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, false, fixture.Drawing.ProjectId, default));
        await fixture.Service.EnsureProductionUseAllowedAsync(cached.DocumentId, cached.VersionId, default);

        await fixture.Repository.ObsoleteDocumentAsync(fixture.Drawing.Id, "admin", default);

        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            cached.DocumentId, cached.VersionId, default));
    }

    [Fact]
    public async Task DeletedDrawingCannotBeDownloadedUsingCachedVersionIds()
    {
        var fixture = await CreateFixtureAsync();
        Store<PdmDocument>(fixture.Repository, "documents")[fixture.Drawing.Id] = fixture.Drawing with { DeletedAt = DateTimeOffset.UtcNow };

        await Assert.ThrowsAsync<PdmNotFoundException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Drawing.Id, fixture.DrawingVersion.Id, default));
    }

    [Fact]
    public async Task ObsoleteModelDisablesItsProductionStepEvenWhenThePdfRemainsValid()
    {
        var fixture = await CreateFixtureAsync();
        var before = Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, false, fixture.Drawing.ProjectId, default));
        Assert.True(Assert.Single(before.ModelFiles).StepReady);

        await fixture.Repository.ObsoleteDocumentAsync(fixture.Model.Id, "admin", default);

        var after = Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, false, fixture.Drawing.ProjectId, default));
        Assert.True(after.PdfReady);
        Assert.False(Assert.Single(after.ModelFiles).StepReady);
        await fixture.Service.EnsureProductionUseAllowedAsync(fixture.Drawing.Id, fixture.DrawingVersion.Id, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Model.Id, fixture.ModelVersion.Id, default));
    }

    [Theory]
    [InlineData(DocumentLifecycleState.Work)]
    [InlineData(DocumentLifecycleState.InReview)]
    public async Task NewWorkingRevisionDoesNotInvalidateTheExistingFormalVersion(DocumentLifecycleState state)
    {
        var fixture = await CreateFixtureAsync();
        Store<PdmDocument>(fixture.Repository, "documents")[fixture.Drawing.Id] = fixture.Drawing with
        { State = state, Revision = RevisionLabel.Parse("B-W1") };

        var version = await fixture.Service.EnsureProductionUseAllowedAsync(fixture.Drawing.Id, fixture.DrawingVersion.Id, default);

        Assert.Equal(fixture.DrawingVersion.Id, version.Id);
        Assert.True(Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, false,
            fixture.Drawing.ProjectId, default)).IsCurrent);
    }

    [Theory]
    [InlineData("unpublished")]
    [InlineData("wrong-project")]
    [InlineData("wrong-scope")]
    [InlineData("missing")]
    public async Task InvalidReleasePackagePreventsProductionUse(string invalidity)
    {
        var fixture = await CreateFixtureAsync();
        var packages = Store<ReleasePackage>(fixture.Repository, "packages");
        packages[fixture.Package.Id] = invalidity switch
        {
            "unpublished" => fixture.Package with { State = ReleasePackageState.PublishFailed },
            "wrong-project" => fixture.Package with { ProjectId = Guid.NewGuid() },
            "wrong-scope" => fixture.Package with { Scope = ReleaseScope.StandardFormal },
            _ => fixture.Package
        };
        if (invalidity == "missing") packages.TryRemove(fixture.Package.Id, out _);

        var error = await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Drawing.Id, fixture.DrawingVersion.Id, default));

        Assert.Contains("发布包", error.Message);
    }

    [Fact]
    public async Task WorkingOrForeignVersionCannotBeUsedThroughProductionEntryPoint()
    {
        var fixture = await CreateFixtureAsync();
        Store<DocumentVersion>(fixture.Repository, "versions")[fixture.DrawingVersion.Id] = fixture.DrawingVersion with
        { Status = DocumentVersionStatus.Work, ReleasePackageId = null };

        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Drawing.Id, fixture.DrawingVersion.Id, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => fixture.Service.EnsureProductionUseAllowedAsync(
            fixture.Drawing.Id, fixture.ModelVersion.Id, default));
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("VALIDITY-" + Guid.NewGuid().ToString("N"),
            "生产图纸有效性", "admin", "vault", "release"), "admin", default);
        var model = await repository.RegisterDocumentAsync(new(project.Id, "PART", "模型", "PART.SLDPRT", DocumentKind.Part), "admin", default);
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAWING", "图纸", "DRAWING.SLDDRW",
            DocumentKind.Drawing, RelatedModelDocumentId: model.Id), "admin", default);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-VALIDITY", ReleasePackageState.Published,
            null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
        {
            Scope = ReleaseScope.NonStandardWithDrawing,
            NonStandardBomSnapshot = [new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1,
                model.DrawingNumber, model.Name, 2, "件", null, null, "B", true) { SourceDocumentId = model.Id }]
        };
        await repository.CreateReleasePackageAsync(package, default);
        var drawingVersion = ReleasedVersion(drawing, package.Id, DocumentPreviewFormat.Pdf);
        var modelVersion = ReleasedVersion(model, package.Id, DocumentPreviewFormat.Step);
        var versions = Store<DocumentVersion>(repository, "versions");
        versions[drawingVersion.Id] = drawingVersion;
        versions[modelVersion.Id] = modelVersion;
        return new(repository, new ProductionDrawingService(repository), drawing, model, drawingVersion, modelVersion, package);
    }

    private static DocumentVersion ReleasedVersion(PdmDocument document, Guid packageId, DocumentPreviewFormat format)
    {
        var hash = new string('A', 64);
        return new(Guid.NewGuid(), document.Id, RevisionLabel.Released('B'), DocumentVersionStatus.Released,
            document.FileName, 12, hash, "admin", DateTimeOffset.UtcNow, "正式发布", new Dictionary<string, string?>(),
            new(Guid.NewGuid(), document.Id, document.FileName, document.FileName, document.Name, document.Kind,
                "默认", 1, ReferenceNodeStatus.Normal, RevisionLabel.Released('B'), null, []),
            [], [], null, null, null, packageId, new(format, document.Id.ToString("N"), 12, hash, hash));
    }

    private static ConcurrentDictionary<Guid, T> Store<T>(InMemoryPdmRepository repository, string field)
        => (ConcurrentDictionary<Guid, T>)typeof(InMemoryPdmRepository)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    private sealed record Fixture(InMemoryPdmRepository Repository, ProductionDrawingService Service,
        PdmDocument Drawing, PdmDocument Model, DocumentVersion DrawingVersion, DocumentVersion ModelVersion, ReleasePackage Package);
}
