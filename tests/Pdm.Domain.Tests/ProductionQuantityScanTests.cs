using System.Collections.Concurrent;
using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionQuantityScanTests
{
    private sealed record Fixture(InMemoryPdmRepository Repository, ProductionTrackingService Service,
        InMemoryProductionTrackingStore Store, Project Project, BomItem Item, PdmDocument Drawing,
        DocumentVersion Version, ReleasePackage Original, ReleasePackage Supplement, Guid LotId);

    private static async Task<Fixture> Create(string route)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("SCAN-QTY-" + Guid.NewGuid().ToString("N"), "同版本增量扫码", "admin", "vault", "release"), "admin", default);
        var model = await repository.RegisterDocumentAsync(new(project.Id, "MODEL-QTY", "模型", "MODEL.SLDPRT", DocumentKind.Part), "admin", default);
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAWING-QTY", "图纸", "DRAWING.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: model.Id), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "NS-QTY", "夹持座", 10, "件", "钢", "MODEL-QTY", "B", true)
            { SourceDocumentId = model.Id };
        var original = new ReleasePackage(Guid.NewGuid(), project.Id, "SCAN-ORIGINAL", ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
        var child = new DocumentReferenceNode(Guid.NewGuid(), model.Id, "D/M", model.FileName, model.Name, DocumentKind.Part, "", 1, ReferenceNodeStatus.Normal, RevisionLabel.Released('B'), null, []);
        var root = child with { DocumentId = drawing.Id, Kind = DocumentKind.Drawing, Children = [child] };
        var version = new DocumentVersion(Guid.NewGuid(), drawing.Id, RevisionLabel.Released('B'), DocumentVersionStatus.Released,
            drawing.FileName, 12, new string('A', 64), "admin", DateTimeOffset.UtcNow, "正式图纸", new Dictionary<string, string?> { ["型号"] = "MODEL-QTY" }, root,
            [], [], null, null, Guid.NewGuid(), original.Id);
        var versions = (ConcurrentDictionary<Guid, DocumentVersion>)typeof(InMemoryPdmRepository).GetField("versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
        versions[version.Id] = version;
        original = original with { VersionBindings = [new(drawing.Id, version.Id, "B", version.Sha256, true)] };
        await repository.CreateReleasePackageAsync(original, default);
        var store = new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddMinutes(-1));
        var service = new ProductionTrackingService(repository, store, TimeProvider.System, new ProductionDrawingService(repository));
        var record = await service.ReceiveAsync(original.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        record = await service.AssignAsync(original.Id, new(record.State.Lines[0].Id, route, 10, route == "External" ? "外协厂" : null, null, DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        var supplement = original with { Id = Guid.NewGuid(), Number = "SCAN-ADD-2", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = original.PublishedAt!.Value.AddMinutes(1), NonStandardBomSnapshot = [item with { Quantity = 12 }] };
        await repository.CreateReleasePackageAsync(supplement, default);
        return new(repository, service, store, project, item, drawing, version, original, supplement, record.State.Lots[0].Id);
    }

    [Theory]
    [InlineData("Self", "display")]
    [InlineData("Self", "old-pdf")]
    [InlineData("Self", "new-pdf")]
    [InlineData("Self", "legacy")]
    [InlineData("External", "display")]
    [InlineData("External", "old-pdf")]
    [InlineData("External", "new-pdf")]
    [InlineData("External", "legacy")]
    public async Task SameRevisionDrawingScanIncludesOriginalAndEveryPublishedIncrement(string route, string codeKind)
    {
        var f = await Create(route);
        var next = f.Supplement with { Id = Guid.NewGuid(), Number = "SCAN-ADD-3", PublishedAt = f.Supplement.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [f.Item with { Quantity = 15 }] };
        await f.Repository.CreateReleasePackageAsync(next, default);
        var code = codeKind switch
        {
            "display" => PdfReleaseIdentity.Display("MODEL-QTY", "B"),
            "old-pdf" => PdfReleaseIdentity.Encode(f.Original.Id, f.Version.Id),
            "new-pdf" => PdfReleaseIdentity.Encode(f.Supplement.Id, f.Version.Id),
            _ => $"UPLM-DRAWING|MODEL-QTY|B|{f.Drawing.Id}"
        };
        var found = await f.Service.ResolveScanAsync(code, "admin", UserRole.Administrator, default);
        Assert.Equal(3, found.Count);
        Assert.Equal(new decimal?[] { 2, 3, 10 }, found.Select(item => item.RequiredQuantity).Order());
        Assert.Equal(2, found.Count(item => item.IsSupplement && item.UnassignedQuantity > 0));
        Assert.Equal(new[] { f.Original.Id, f.Supplement.Id, next.Id }.Order(), found.Select(item => item.ReleasePackageId).Order());
        var states = new List<ProductionTrackingState>();
        foreach (var hit in found) states.Add((await f.Service.FindAsync(hit.ReleasePackageId, "admin", UserRole.Administrator, default)).State);
        Assert.Equal(new decimal[] { 2, 3, 10 }, states.SelectMany(state => state.Lines).Select(line => line.RequiredQuantity).Order());
        var repeated = await f.Service.ResolveScanAsync(code, "admin", UserRole.Administrator, default);
        Assert.Equal(3, repeated.Count);
        var original = await f.Service.FindAsync(f.Original.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(10, original.State.Lots[0].Quantity);
        Assert.False(original.State.Lots[0].Frozen);
    }

    [Fact]
    public async Task DraftOtherRevisionAndOtherMaterialAreNotMergedIntoTheScannedDemand()
    {
        var f = await Create("Self");
        foreach (var (state, item, number) in new[] {
            (ReleasePackageState.Draft, f.Item with { Quantity = 99 }, "DRAFT"),
            (ReleasePackageState.Published, f.Item with { Revision = "C", Quantity = 12 }, "OTHER-REVISION"),
            (ReleasePackageState.Published, f.Item with { Id = Guid.NewGuid(), ReleaseTrackingId = Guid.NewGuid(), DrawingNumber = "OTHER", Quantity = 3 }, "OTHER-MATERIAL") })
            await f.Repository.CreateReleasePackageAsync(f.Supplement with { Id = Guid.NewGuid(), Number = number, State = state,
                PublishedAt = f.Supplement.PublishedAt!.Value.AddMinutes(1), NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] }, default);
        var found = await f.Service.ResolveScanAsync(PdfReleaseIdentity.Encode(f.Original.Id, f.Version.Id), "admin", UserRole.Administrator, default);
        Assert.Equal(2, found.Count);
        Assert.All(found, item => Assert.Contains(item.ReleasePackageId, new[] { f.Original.Id, f.Supplement.Id }));
    }

    [Theory]
    [InlineData("Self")]
    [InlineData("External")]
    public async Task BatchCardKeepsOriginalBatchAndRelatedIncrementCanBeAllocatedAndIssuedExactlyOnce(string route)
    {
        var f = await Create(route);
        var station = new ProductionWorkstation(Guid.NewGuid(), "数量扫码回归工位", "CNC", true);
        Assert.True(await f.Store.SaveScanCatalogAsync(new(f.Project.OrganizationId ?? Guid.Empty, [station], ProductionFlowRules.Posts.Keys.Select(post =>
            new ProductionPostBinding(Guid.NewGuid(), "Role", "Administrator", post, [station.Id], true)).ToArray()) { FlowMode = "DepartmentHandover" }, 0, default));
        var code = $"UPLM-PRODUCTION|{f.Original.Id}|{f.LotId}";
        var hit = Assert.Single(await f.Service.ResolveScanAsync(code, "admin", UserRole.Administrator, default));
        Assert.Equal(f.LotId, hit.LotId);
        Assert.Equal(10, hit.LotQuantity);
        var context = await f.Service.ActionsAsync(f.Original.Id, "admin", UserRole.Administrator, default);
        var related = Assert.Single(context.RelatedDemands);
        Assert.Equal(f.Supplement.Id, related.ReleasePackageId);
        Assert.Equal(hit.LineId, related.RelatedToLineId);
        Assert.Equal(2, related.RequiredQuantity);
        Assert.Equal(2, related.UnassignedQuantity);
        var extra = await f.Service.ReceiveAsync(f.Supplement.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        extra = await f.Service.AssignAsync(f.Supplement.Id, new(extra.State.Lines[0].Id, route, 2, route == "External" ? "增补外协厂" : null, null,
            DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid()), "admin", UserRole.Administrator, default);
        foreach (var (id, lotId, quantity) in new[] { (f.Original.Id, f.LotId, 10m), (f.Supplement.Id, extra.State.Lots[0].Id, 2m) })
        {
            var steps = route == "Self" ? new[] { "ProductionReceived", "MachiningReceived", "PackingStarted", "QualityReceived", "Pass", "Issue" }
                : ["ExternalDelivered", "QualityReceived", "Pass", "Issue"];
            foreach (var step in steps)
                await f.Service.RecordAsync(id, new(lotId, step, quantity, Guid.NewGuid(), Recipient: step == "Issue" ? "领料人" : null, WorkstationId: station.Id), "admin", UserRole.Administrator, default);
        }
        Assert.Single(await f.Service.ResolveScanAsync(code, "admin", UserRole.Administrator, default));
        var original = await f.Service.FindAsync(f.Original.Id, "admin", UserRole.Administrator, default);
        extra = await f.Service.FindAsync(f.Supplement.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(10, ProductionTrackingRules.Issued(original.State, original.State.Lines[0].Id));
        Assert.Equal(2, ProductionTrackingRules.Issued(extra.State, extra.State.Lines[0].Id));
        Assert.Equal(0, Assert.Single((await f.Service.ActionsAsync(f.Original.Id, "admin", UserRole.Administrator, default)).RelatedDemands).UnassignedQuantity);
    }

    [Fact]
    public async Task OldPdfCanFindNewIncrementWithoutInventingPreActivationProductionHistory()
    {
        var f = await Create("Self");
        var store = new InMemoryProductionTrackingStore(f.Supplement.PublishedAt!.Value.AddSeconds(-30));
        var service = new ProductionTrackingService(f.Repository, store, TimeProvider.System, new ProductionDrawingService(f.Repository));
        var found = Assert.Single(await service.ResolveScanAsync(PdfReleaseIdentity.Encode(f.Original.Id, f.Version.Id), "admin", UserRole.Administrator, default));
        Assert.Equal(f.Supplement.Id, found.ReleasePackageId);
        Assert.Equal(2, found.RequiredQuantity);
        Assert.Null(await store.FindAsync(f.Original.Id, default));
    }
}
