using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomDrawingRevisionTests
{
    [Theory]
    [InlineData(BomKind.Standard, ReleaseScope.StandardFormal, "S")]
    [InlineData(BomKind.Electrical, ReleaseScope.ElectricalFormal, "E")]
    public async Task CurrentMaterialRowsUseLatestMatchingPublishedSnapshot(BomKind kind, ReleaseScope scope, string prefix)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var item = Item(Guid.NewGuid(), "W1") with { Kind = kind, Unit = "001" };
        ReleasePackage Release(string revision, DateTimeOffset time, ReleasePackageState state = ReleasePackageState.Published) => Package(item) with
        {
            Scope = scope, State = state, PublishedAt = time,
            StandardBomRevision = kind == BomKind.Standard ? revision : null,
            ElectricalBomRevision = kind == BomKind.Electrical ? revision : "",
            StandardBomSnapshot = kind == BomKind.Standard ? [item with { Id = Guid.NewGuid(), Unit = "个" }] : [],
            ElectricalBomSnapshot = kind == BomKind.Electrical ? [item with { Id = Guid.NewGuid(), Unit = "个" }] : [], NonStandardBomSnapshot = [], VersionBindings = []
        };
        await repository.CreateReleasePackageAsync(Release($"{prefix}-P700016-2-B03", DateTimeOffset.UtcNow.AddDays(-2)), default);
        var latest = Release($"{prefix}-P700016-2-B06", DateTimeOffset.UtcNow.AddDays(-1));
        await repository.CreateReleasePackageAsync(latest, default);
        await repository.CreateReleasePackageAsync(Release($"{prefix}-P700016-2-B09", DateTimeOffset.UtcNow, ReleasePackageState.Approval), default);
        await repository.CreateReleasePackageAsync(latest with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), StandardBomRevision = "S-OTHER-V99", ElectricalBomRevision = "E-OTHER-V99", PublishedAt = DateTimeOffset.UtcNow }, default);
        var rows = await BomDrawingRevision.CurrentAsync(repository, [item, item with { DrawingNumber = "NEW" }, item with { SourceDocumentId = null, Revision = "A" }], default);
        Assert.Equal($"{prefix}-P700016-2-V06", rows[0].DisplayRevision);
        Assert.Equal("未发布", rows[1].DisplayRevision);
        Assert.Equal("A", rows[2].DisplayRevision);
        Assert.Equal(rows[0].DisplayRevision, BomDrawingRevision.ForPackage(item, latest));
        Assert.Equal("W1", item.Revision);
    }

    [Theory]
    [InlineData(BomKind.Standard)]
    [InlineData(BomKind.Electrical)]
    public async Task HistoricalBomMetadataSuppliesVersionWhenPackageRevisionIsMissing(BomKind kind)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("VERSION-TEST", "版本验证", "admin", "vault", "release"), "admin", default);
        var item = Item(Guid.NewGuid(), "W1") with { ProjectId = project.Id, Kind = kind, SourceDocumentId = null };
        var version = await repository.SaveBomDraftAsync(project.Id, kind, [item], "admin", default);
        await repository.SetBomVersionStateAsync([version.Id], BomVersionState.Released, "admin", DateTimeOffset.UtcNow, default);
        var expected = version.Label.Replace("-B", "-V");
        Assert.Equal(expected, Assert.Single(await BomDrawingRevision.CurrentAsync(repository, [item], default)).DisplayRevision);
        var package = Package(item) with { Scope = kind == BomKind.Standard ? ReleaseScope.StandardFormal : ReleaseScope.ElectricalFormal,
            StandardBomRevision = null, ElectricalBomRevision = "", NonStandardBomSnapshot = [],
            StandardBomSnapshot = kind == BomKind.Standard ? [item] : [], ElectricalBomSnapshot = kind == BomKind.Electrical ? [item] : [],
            StandardBomVersionId = kind == BomKind.Standard ? version.Id : null, ElectricalBomVersionId = kind == BomKind.Electrical ? version.Id : null };
        await repository.CreateReleasePackageAsync(package, default);
        Assert.Equal(expected, Assert.Single(await BomDrawingRevision.CurrentAsync(repository, [item], default)).DisplayRevision);
    }

    [Fact]
    public void StandardItemsWithoutIndependentRevisionUseTheirFrozenBomReleaseVersion()
    {
        var item = Item(Guid.NewGuid(), "W1") with { Kind = BomKind.Standard };
        var package = Package(item) with { Scope = ReleaseScope.StandardSupplement, StandardBomRevision = "S-P700016-2-B03", NonStandardBomSnapshot = [], StandardBomSnapshot = [item], VersionBindings = [] };
        Assert.Equal("S-P700016-2-V03", BomDrawingRevision.ForPackage(item, package));
        Assert.Equal("A", BomDrawingRevision.ForPackage(item with { Revision = "A" }, package));
        using var stream = new MemoryStream(BomWorkbook.Write(BomDrawingRevision.ForPackage([item], package)));
        Assert.Equal("S-P700016-2-V03", Assert.Single(BomWorkbook.Read(stream)).Revision);
        Assert.Equal("W1", item.Revision);
        Assert.Null(item.DisplayRevision);
    }
    [Theory]
    [InlineData("W1", "A-D")]
    [InlineData("W25", "A-D")]
    [InlineData("A", "A")]
    [InlineData("A-W1", "B-D")]
    [InlineData("A-W12", "B-D")]
    [InlineData("B-W2", "C-D")]
    [InlineData("Z-W1", "版次已达 Z（待处理）")]
    [InlineData("Rev-12", "Rev-12")]
    public void BusinessLabelDoesNotTreatEveryArchiveAsANewFormalRevision(string raw, string expected) =>
        Assert.Equal(expected, DrawingRevision.Display(raw));

    [Fact]
    public void CurrentBomUsesOnlyTheFormalPromotionOfItsExactSource()
    {
        var documentId = Guid.NewGuid();
        var work = Version(documentId, "W1");
        var released = Version(documentId, "A") with { SourceVersionId = work.Id };
        var changed = Version(documentId, "A-W1");
        var next = Version(documentId, "B") with { SourceVersionId = changed.Id };
        var item = Item(documentId, "W1");
        Assert.Equal("B", BomDrawingRevision.ForHistory(item, [next, changed, released, work]));
        Assert.Equal("A", BomDrawingRevision.ForHistory(item with { Revision = "A-W1" }, [released, changed, work]));
        Assert.Equal("A", BomDrawingRevision.ForHistory(item, [released with { SourceVersionId = Guid.NewGuid() }, work]));
        Assert.Equal("W1", item.Revision);
    }

    [Theory]
    [InlineData("W3", "A")]
    [InlineData("A-W2", "B")]
    public void PublishedWorkbookUsesFrozenBindingAndPreservesArchiveId(string raw, string formal)
    {
        var item = Item(Guid.NewGuid(), raw);
        var package = Package(item) with { VersionBindings = [new(item.SourceDocumentId!.Value, Guid.NewGuid(), raw, "hash", true, TargetRevision: formal)] };
        var displayed = BomDrawingRevision.ForPackage([item], package);
        using var stream = new MemoryStream(BomWorkbook.Write(displayed));
        Assert.Equal(formal, Assert.Single(BomWorkbook.Read(stream)).Revision);
        Assert.Equal(raw, displayed[0].Revision);
        Assert.Null(item.DisplayRevision);
    }

    [Fact]
    public void LegacyPublishedBomWithoutBindingIsNotGuessedAsA()
    {
        var item = Item(Guid.NewGuid(), "W1");
        Assert.Equal("历史版次待核对", BomDrawingRevision.ForPackage(item, Package(item)));
        var longLead = Package(item) with { Scope = ReleaseScope.NonStandardLongLead };
        Assert.Equal("未发布", BomDrawingRevision.ForPackage(item, longLead));
    }

    [Theory]
    [InlineData("全局/版本")]
    [InlineData("配置:Default/版次")]
    [InlineData("版本")]
    [InlineData("版次")]
    public void ConflictingTitleRevisionStopsPublicationWithoutChangingTheApprovedSource(string property)
    {
        var version = Version(Guid.NewGuid(), "A-W1") with { PropertySnapshot = new Dictionary<string, string?> { [property] = "A" } };
        var error = Assert.Throws<PdmConflictException>(() => BomDrawingRevision.ValidateDrawingProperties(version, "B", "PART.SLDDRW"));
        Assert.Contains("重新存档并审核", error.Message);
        Assert.Equal("A", version.PropertySnapshot[property]);
        BomDrawingRevision.ValidateDrawingProperties(version, "A", "PART.SLDDRW");
    }

    [Theory]
    [InlineData("版本", "Global")]
    [InlineData("版本", "Configuration")]
    [InlineData("版本", "Both")]
    [InlineData("版次", "Global")]
    [InlineData("版次", "Configuration")]
    [InlineData("版次", "Both")]
    public void PropertyCardScopeIsNotATitleRevision(string name, string scope)
    {
        var properties = new Dictionary<string, string?>
        {
            [CadPropertyCardSnapshot.SchemaKey] = "1",
            [CadPropertyCardSnapshot.ScopePrefix + name] = scope,
            ["全局/" + name] = ""
        };
        var version = Version(Guid.NewGuid(), "W1") with { PropertySnapshot = properties };

        BomDrawingRevision.ValidateDrawingProperties(version, "A", "700001.00-17.SLDDRW");
        properties["全局/" + name] = "A";
        BomDrawingRevision.ValidateDrawingProperties(version, "A", "700001.00-17.SLDDRW");
        properties["全局/" + name] = "B";
        var error = Assert.Throws<PdmConflictException>(() =>
            BomDrawingRevision.ValidateDrawingProperties(version, "A", "700001.00-17.SLDDRW"));
        Assert.Contains("属性为B", error.Message);
        Assert.Equal(scope, properties[CadPropertyCardSnapshot.ScopePrefix + name]);
    }

    private static BomItem Item(Guid documentId, string raw) => new(Guid.NewGuid(), Guid.NewGuid(), BomKind.NonStandard,
        1, "PART", "底板", 1, "个", "钢", "PART", raw, true) { SourceDocumentId = documentId };

    private static ReleasePackage Package(BomItem item) => new(Guid.NewGuid(), item.ProjectId, "RP-TEST",
        ReleasePackageState.Published, Guid.NewGuid(), "N-V03", "E-V01", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
        { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item] };

    private static DocumentVersion Version(Guid documentId, string raw) => new(Guid.NewGuid(), documentId,
        RevisionLabel.Parse(raw), DrawingRevision.IsWork(raw) ? DocumentVersionStatus.Work : DocumentVersionStatus.Released,
        "source", 1, "hash", "engineer", DateTimeOffset.UtcNow, "test", new Dictionary<string, string?>(), null!, [], [], null, null, null, null);
}
