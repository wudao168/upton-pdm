using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomDrawingRevisionTests
{
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
    public void ConflictingTitleRevisionStopsPublicationWithoutChangingTheApprovedSource(string property)
    {
        var version = Version(Guid.NewGuid(), "A-W1") with { PropertySnapshot = new Dictionary<string, string?> { [property] = "A" } };
        var error = Assert.Throws<PdmConflictException>(() => BomDrawingRevision.ValidateDrawingProperties(version, "B", "PART.SLDDRW"));
        Assert.Contains("重新存档并审核", error.Message);
        Assert.Equal("A", version.PropertySnapshot[property]);
        BomDrawingRevision.ValidateDrawingProperties(version, "A", "PART.SLDDRW");
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
