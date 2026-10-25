using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionTrackingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task PdfScanUsesFrozenPackageAndVersionWhenPlmModelDiffersFromCadText()
    {
        var fixture = await FixtureAsync("PLM-RENAMED-123");
        var model = await fixture.Repository.RegisterDocumentAsync(new(fixture.Project.Id, "OLD-CAD-MODEL", "模型", "model.SLDPRT", DocumentKind.Part), "admin", Ct);
        var drawing = await fixture.Repository.RegisterDocumentAsync(new(fixture.Project.Id, "OLD-CAD-DRAWING", "图纸", "drawing.SLDDRW", DocumentKind.Drawing,
            RelatedModelDocumentId: model.Id), "admin", Ct);
        var session = Guid.NewGuid();
        await fixture.Repository.CheckoutAsync(drawing.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddHours(1), Ct);
        var child = new DocumentReferenceNode(Guid.NewGuid(), model.Id, "D/M", model.FileName, model.Name, DocumentKind.Part,
            "", 1, ReferenceNodeStatus.Normal, model.Revision, null, []);
        var root = child with { DocumentId = drawing.Id, Kind = DocumentKind.Drawing, Children = [child] };
        var hash = new string('A', 64);
        var version = (await fixture.Repository.CheckInVersionAsync(drawing.Id, "admin", session,
            new(new StoredFile(".versions/approved-drawing.SLDDRW", 128, hash, DateTimeOffset.UtcNow), "已批准来源",
                new Dictionary<string, string?> { ["型号"] = "OLD-CAD-MODEL" },
                new(Guid.NewGuid(), fixture.Project.Id, drawing.Id, DateTimeOffset.UtcNow, "admin", root, hash), [], []), Ct)).Version!;
        var item = fixture.Item with { SourceDocumentId = model.Id, Revision = version.Revision.Release().Display };
        var package = fixture.Package with
        {
            Id = Guid.NewGuid(), Number = "FB-PDF-QR", PublishedAt = fixture.Package.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [item], VersionBindings = [new(drawing.Id, version.Id, version.Revision.Display, hash, true)]
        };
        var approvalId = Guid.NewGuid();
        package = package with { ApprovalTasks = [new(approvalId, package.Id, ApprovalStage.Approval, "admin", "admin",
            ApprovalDecision.Approved, "批准", DateTimeOffset.UtcNow)] };
        await fixture.Repository.CreateReleasePackageAsync(package, Ct);
        var code = PdfReleaseIdentity.Encode(package.Id, version.Id);
        var result = Assert.Single(await fixture.Service.ResolveScanAsync(code, "admin", UserRole.Administrator, Ct));
        Assert.Equal(package.Id, result.ReleasePackageId);
        Assert.Equal("PLM-RENAMED-123", result.DrawingNumber);
        var released = await fixture.Repository.PublishDocumentVersionAsync(drawing.Id, version.Id, package.Id, approvalId, "admin", Ct);
        var withoutPdf = Assert.Single(await fixture.Service.ResolveScanAsync(
            PdfReleaseIdentity.Display("PLM-RENAMED-123", released.Revision.Display), "admin", UserRole.Administrator, Ct));
        Assert.Equal(package.Id, withoutPdf.ReleasePackageId);
        await fixture.Repository.AttachReleasePreviewArtifactsAsync(package.Id,
            new Dictionary<Guid, DocumentPreviewArtifact> { [drawing.Id] = new(DocumentPreviewFormat.Pdf, "verified.pdf", 128, hash, hash) }, Ct);
        var readable = PdfReleaseIdentity.Display("PLM-RENAMED-123", released.Revision.Display);
        var readableResult = Assert.Single(await fixture.Service.ResolveScanAsync(readable, "admin", UserRole.Administrator, Ct));
        Assert.Equal(package.Id, readableResult.ReleasePackageId);
        Assert.Equal("PLM-RENAMED-123", readableResult.DrawingNumber);
        Assert.Empty(await fixture.Service.ResolveScanAsync(PdfReleaseIdentity.Display("WRONG-MODEL", released.Revision.Display), "admin", UserRole.Administrator, Ct));
        Assert.Empty(await fixture.Service.ResolveScanAsync(PdfReleaseIdentity.Display("PLM-RENAMED-123", "Z"), "admin", UserRole.Administrator, Ct));
        var versionStore = (System.Collections.Concurrent.ConcurrentDictionary<Guid, DocumentVersion>)typeof(InMemoryPdmRepository)
            .GetField("versions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Repository)!;
        versionStore[released.Id] = released with { SourceVersionId = null, Preview = null };
        var packageStore = (System.Collections.Concurrent.ConcurrentDictionary<Guid, ReleasePackage>)typeof(InMemoryPdmRepository)
            .GetField("packages", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Repository)!;
        packageStore[package.Id] = package with { VersionBindings = [] };
        var legacy = Assert.Single(await fixture.Service.ResolveScanAsync(readable, "admin", UserRole.Administrator, Ct));
        Assert.Equal(package.Id, legacy.ReleasePackageId);
        packageStore[package.Id] = package;
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ResolveScanAsync(
            PdfReleaseIdentity.Encode(package.Id, Guid.NewGuid()), "admin", UserRole.Administrator, Ct));
        await fixture.Repository.SetRolePermissionsAsync("Engineer", [], Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ResolveScanAsync(code, "engineer", UserRole.Engineer, Ct));
        var forged = package with { Id = Guid.NewGuid(), Number = "FB-FORGED", VersionBindings = [new(drawing.Id, version.Id, version.Revision.Display, new string('F', 64), true)] };
        await fixture.Repository.CreateReleasePackageAsync(forged, Ct);
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.ResolveScanAsync(
            PdfReleaseIdentity.Encode(forged.Id, version.Id), "admin", UserRole.Administrator, Ct));
    }

    [Theory]
    [InlineData("UPLM-PDF|bad|bad")]
    [InlineData("UPLM-PDF|00000000000000000000000000000000|00000000000000000000000000000000")]
    [InlineData("UPLM-PDF|ab|cd|extra")]
    public void PdfQrRejectsInvalidOrEmptyIdentity(string code) => Assert.False(PdfReleaseIdentity.TryDecode(code, out _, out _));

    [Theory]
    [InlineData("P7000XX.30.01-09\nB")]
    [InlineData("P7000XX.30.01-09\r\nB")]
    [InlineData("图号：P7000XX.30.01-09\n版本：B")]
    [InlineData("图号：P7000XX.30.01-09\r\n版本：B")]
    public void ReadablePdfQrContainsOnlyModelAndRevision(string code)
    {
        Assert.True(PdfReleaseIdentity.TryDecodeDisplay(code, out var model, out var revision));
        Assert.Equal("P7000XX.30.01-09", model);
        Assert.Equal("B", revision);
        Assert.Equal("P7000XX.30.01-09\nB", PdfReleaseIdentity.Display(model, revision));
    }

    [Theory]
    [InlineData("P7000XX.30.01-09\nB\n2026-10-20")]
    [InlineData("P7000XX.30.01-09\n2026-10-20")]
    [InlineData("P7000XX.30.01-09\n")]
    [InlineData("图号：P7000XX.30.01-09\n版本：B\n发布日期：2026-10-20")]
    [InlineData("图号：\n版本：B")]
    [InlineData("图号：P7000XX.30.01-09\n版本：")]
    public void ReadablePdfQrRejectsMissingFieldsAndExtraLines(string code) =>
        Assert.False(PdfReleaseIdentity.TryDecodeDisplay(code, out _, out _));

    [Fact]
    public async Task SelfProductionSummaryExcludesExternalAndUnassignedLinesWithoutChangingSourceDemand()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        var external = new ProductionLot(Guid.NewGuid(), line.Id, "External", 6, "外协供应商", null, new DateOnly(2030, 1, 1), null, null, 0);
        var self = external with { Id = Guid.NewGuid(), Route = "Self", Quantity = 4, Supplier = null, PromisedOn = line.RequiredOn!.Value.AddDays(-1) };
        var other = line with { Id = Guid.NewGuid(), RequiredQuantity = 3 };
        var unassigned = line with { Id = Guid.NewGuid(), RequiredQuantity = 2 };
        var state = received.State with
        {
            Lines = [line, other, unassigned], Lots = [external, self, external with { Id = Guid.NewGuid(), LineId = other.Id, Quantity = 3 }],
            Movements = [new(Guid.NewGuid(), Guid.NewGuid(), external.Id, "Pass", 6, "质检", DateTimeOffset.UtcNow)]
        };
        var view = ProductionTrackingRules.SelfProduction(state)!;
        Assert.Equal(line.Id, Assert.Single(view.Lines).Id);
        Assert.Equal(4, view.Lines[0].RequiredQuantity);
        Assert.Equal(self.Id, Assert.Single(view.Lots).Id);
        Assert.Empty(view.Movements);
        var summary = ProductionTrackingRules.Summary(view);
        Assert.Equal(1, summary.TotalLines);
        Assert.Equal(4, summary.TotalQuantity);
        Assert.Equal(0, summary.AtRiskLines);
        Assert.Equal(0, summary.QualifiedLines);
        Assert.Equal("待生产签收", summary.Status);
        Assert.Equal(self.PromisedOn, summary.ForecastOn);
        Assert.Equal(10, state.Lines[0].RequiredQuantity);
        Assert.Equal(3, state.Lots.Count);
        Assert.Single(state.Movements);
        Assert.Null(ProductionTrackingRules.SelfProduction(state with { Lots = [external] }));
        Assert.Null(ProductionTrackingRules.SelfProduction(state with { Lots = [] }));
    }

    [Fact]
    public async Task SelfOnlyProjectListDropsExternalOnlyPackagesAndRefreshesAfterRouteChange()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        await fixture.Service.AssignAsync(fixture.Package.Id, new(line.Id, "External", 6, "外协厂", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var assigned = await fixture.Service.AssignAsync(fixture.Package.Id, new(line.Id, "Self", 4, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var otherPackage = fixture.Package with { Id = Guid.NewGuid(), Number = "FB-EXTERNAL", PublishedAt = fixture.Package.PublishedAt!.Value.AddMinutes(1) };
        await fixture.Repository.CreateReleasePackageAsync(otherPackage, Ct);
        var other = await fixture.Service.ReceiveAsync(otherPackage.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        await fixture.Service.AssignAsync(otherPackage.Id, new(other.State.Lines[0].Id, "External", 10, "外协厂", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)), Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var summaries = await fixture.Service.ListAsync("admin", UserRole.Administrator, Ct, selfOnly: true);
        var summary = Assert.Single(Assert.Single(summaries).Packages);
        Assert.Equal(fixture.Package.Id, summary.ReleasePackageId);
        Assert.Equal(4, summary.TotalQuantity);
        Assert.Equal(0, summary.AtRiskLines);
        Assert.Equal(2, Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, Ct)).Packages.Count);
        var external = Assert.Single(await fixture.Service.ListAsync("admin", UserRole.Administrator, Ct, externalOnly: true));
        Assert.Equal(2, external.Packages.Count);
        Assert.Equal(6, external.Packages.Single(item => item.ReleasePackageId == fixture.Package.Id).TotalQuantity);
        Assert.Equal(10, external.Packages.Single(item => item.ReleasePackageId == otherPackage.Id).TotalQuantity);
        var externalState = ProductionTrackingRules.ExternalProduction(assigned.State)!;
        Assert.All(externalState.Lots, lot => Assert.Equal("External", lot.Route));
        Assert.Equal(6, Assert.Single(externalState.Lines).RequiredQuantity);
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ListAsync("admin", UserRole.Administrator, Ct, selfOnly: true, externalOnly: true));
        var self = assigned.State.Lots.Single(lot => lot.Route == "Self");
        await fixture.Service.UpdateAllocationAsync(fixture.Package.Id,
            new(self.Id, "External", 4, "外协厂", null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), null, Guid.NewGuid(), assigned.RowVersion),
            "admin", UserRole.Administrator, Ct);
        Assert.Empty(await fixture.Service.ListAsync("admin", UserRole.Administrator, Ct, selfOnly: true));
        Assert.Equal(10, (await fixture.Service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct)).State.Lines[0].RequiredQuantity);
    }

    [Fact]
    public async Task PackageSummaryCountsDemandOnceAndPreservesFractionalQuantities()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var first = Assert.Single(received.State.Lines);
        var state = received.State with { Lines = [first with { RequiredQuantity = 10.25m },
            first with { Id = Guid.NewGuid(), RequiredQuantity = 2.5m }] };
        var summary = ProductionTrackingRules.Summary(state);
        Assert.Equal(2, summary.TotalLines);
        Assert.Equal(12.75m, summary.TotalQuantity);
        var assigned = state with { Lots = [new(Guid.NewGuid(), first.Id, "Self", 5, null, "机加", null, null, null, 0),
            new(Guid.NewGuid(), first.Id, "External", 5.25m, "外协", null, null, null, null, 0)] };
        Assert.Equal(12.75m, ProductionTrackingRules.Summary(assigned).TotalQuantity);
        Assert.Equal(0, ProductionTrackingRules.Summary(state with { Lines = [], Lots = [] }).TotalQuantity);
    }

    private static async Task<(InMemoryPdmRepository Repository, InMemoryProductionTrackingStore Store,
        ProductionTrackingService Service, Project Project, ReleasePackage Package, BomItem Item)> FixtureAsync(string? model = "7002541.00-02")
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("PROD-" + Guid.NewGuid().ToString("N"),
            "非标件生产", "admin", "vault", "release"), "admin", Ct);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1,
            "NS-001", "夹持座", 10, "件", "45钢", model, "B", true);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "FB-001",
            ReleasePackageState.Published, null, "B", "B", [], DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "release")
        {
            Scope = ReleaseScope.NonStandardWithDrawing,
            NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id],
            DrawingRequiredOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))
        };
        await repository.CreateReleasePackageAsync(package, Ct);
        var store = new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddMinutes(-1));
        var station = new ProductionWorkstation(Guid.NewGuid(), "测试综合工位", "CNC组", true);
        await store.SaveScanCatalogAsync(new(project.OrganizationId ?? Guid.Empty, [station],
            ProductionFlowRules.Posts.Keys.Select(post => new ProductionPostBinding(Guid.NewGuid(), "Role", "Administrator", post, [station.Id], true)).ToArray()), 0, Ct);
        var service = new ProductionTrackingService(repository, store, TimeProvider.System,
            new ProductionDrawingService(repository));
        return (repository, store, service, project, package, item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingProductionLabelsUseTheirOwnFrozenBomWithoutChangingBusinessRecords(bool missingSourceIds)
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        Assert.Equal("7002541.00-02", line.DrawingNumber);
        Assert.Equal("NS-001", line.MaterialCode);
        Assert.Equal("夹持座", line.Name);
        var allocated = await fixture.Service.AssignAsync(fixture.Package.Id,
            new(line.Id, "External", 5, "原供应商", "原负责人", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct);
        var legacy = allocated with { State = allocated.State with { Lines = [line with
        {
            DrawingNumber = fixture.Item.DrawingNumber, Name = "旧错误名称", MaterialCode = string.Empty,
            SourceBomItemIds = missingSourceIds ? [] : line.SourceBomItemIds
        }] } };
        Assert.True(await fixture.Store.SaveAsync(legacy, allocated.RowVersion, Ct));
        var newerItem = fixture.Item with { Id = Guid.NewGuid(), Specification = "新版型号", Name = "新版名称" };
        await fixture.Repository.CreateReleasePackageAsync(fixture.Package with
        {
            Id = Guid.NewGuid(), Number = "FB-NEWER", PublishedAt = fixture.Package.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [newerItem], SelectedBomItemIds = [newerItem.Id]
        }, Ct);
        var repaired = await fixture.Service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        Assert.Equivalent(line with { SourceBomItemIds = legacy.State.Lines[0].SourceBomItemIds }, Assert.Single(repaired.State.Lines), strict: true);
        Assert.Equivalent(allocated.State.Lots, repaired.State.Lots, strict: true);
        Assert.Equivalent(allocated.State.Movements, repaired.State.Movements, strict: true);
        Assert.Equal(allocated.State.ReceivedAt, repaired.State.ReceivedAt);
        Assert.Equal(allocated.RowVersion + 2, repaired.RowVersion);
        Assert.Equal(repaired.RowVersion, (await fixture.Service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct)).RowVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingBomModelDoesNotDisplayMaterialCodeAsDrawingNumber(string? model)
    {
        var fixture = await FixtureAsync(model);
        var record = await fixture.Service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(record.State.Lines);
        Assert.Equal(string.Empty, line.DrawingNumber);
        Assert.Equal("NS-001", line.MaterialCode);
        Assert.Equal("夹持座", line.Name);
    }

    [Fact]
    public async Task UnstartedAllocationCanSwitchBothWaysWithoutCreatingLotsAndReleasesItsSchedule()
    {
        var fixture = await FixtureAsync();
        var service = fixture.Service;
        var received = await service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        var assigned = await service.AssignAsync(fixture.Package.Id, new(line.Id, "Self", 6, null, "机加", null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var selfId = Assert.Single(assigned.State.Lots).Id;
        assigned = await service.AssignAsync(fixture.Package.Id, new(line.Id, "External", 4, "原供应商", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var otherId = assigned.State.Lots.Single(lot => lot.Id != selfId).Id;
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        Assert.True(await fixture.Store.SaveCapacityAsync(new(fixture.Project.OrganizationId ?? Guid.Empty, [new("CNC组", date, 8)]), 0, Ct));
        await service.RecordAsync(fixture.Package.Id, new(selfId, "ProductionReceived", 6, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var scheduled = await service.ScheduleAsync(fixture.Package.Id, new(selfId, [new("CNC组", date, 8)], date, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var change = new UpdateProductionLotAllocation(selfId, "External", 6, "新供应商", "采购", date, "加工任务调整外协", Guid.NewGuid(), scheduled.RowVersion);
        var changed = await service.UpdateAllocationAsync(fixture.Package.Id, change, "admin", UserRole.Administrator, Ct);
        Assert.Equal(2, changed.State.Lots.Count);
        var external = changed.State.Lots.Single(lot => lot.Id == selfId);
        Assert.Equal("External", external.Route);
        Assert.Equal("新供应商", external.Supplier);
        Assert.Empty(external.Schedule);
        Assert.Null(external.ResourceGroupId);
        Assert.Null(external.PlannedOn);
        Assert.Equal(0, external.EstimatedHours);
        Assert.Contains("原排程已清空", changed.State.Movements.Last().Note);
        var replay = await service.UpdateAllocationAsync(fixture.Package.Id, change, "admin", UserRole.Administrator, Ct);
        Assert.Equal(changed.RowVersion, replay.RowVersion);
        Assert.Single(replay.State.Movements, item => item.Kind == "AllocationChanged");
        changed = await service.UpdateAllocationAsync(fixture.Package.Id, new(otherId, "Self", 4, "不应保留", "机加", date, "改回自制", Guid.NewGuid(), changed.RowVersion), "admin", UserRole.Administrator, Ct);
        var self = changed.State.Lots.Single(lot => lot.Id == otherId);
        Assert.Null(self.Supplier);
        Assert.Null(self.SupplierId);
        await service.RecordAsync(fixture.Package.Id, new(otherId, "ProductionReceived", 4, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var rescheduled = await service.ScheduleAsync(fixture.Package.Id, new(otherId, [new("CNC组", date, 8)], date, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        Assert.Equal(8, rescheduled.State.Lots.SelectMany(lot => lot.Schedule).Sum(slot => slot.Hours));
        Assert.Equal(new[] { selfId, otherId }.Order(), rescheduled.State.Lots.Select(lot => lot.Id).Order());
    }

    [Fact]
    public async Task AllocationEditHonorsOtherLotsQuantityAndRejectsStaleEdits()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        var first = await fixture.Service.AssignAsync(fixture.Package.Id, new(line.Id, "Self", 6, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var lotId = Assert.Single(first.State.Lots).Id;
        var all = await fixture.Service.AssignAsync(fixture.Package.Id, new(line.Id, "Self", 4, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var input = new UpdateProductionLotAllocation(lotId, "Self", 7, null, "机加", null, "调整数量", Guid.NewGuid(), all.RowVersion);
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.UpdateAllocationAsync(fixture.Package.Id, input, "admin", UserRole.Administrator, Ct));
        var updated = await fixture.Service.UpdateAllocationAsync(fixture.Package.Id, input with { Quantity = 5 }, "admin", UserRole.Administrator, Ct);
        Assert.Equal(9, updated.State.Lots.Sum(lot => lot.Quantity));
        Assert.Equal(4, updated.State.Lots.Single(lot => lot.Id != lotId).Quantity);
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.UpdateAllocationAsync(fixture.Package.Id, input with { Quantity = 6, RequestId = Guid.NewGuid() }, "admin", UserRole.Administrator, Ct));
        var optional = await fixture.Service.UpdateAllocationAsync(fixture.Package.Id, input with { Quantity = 5, Reason = null, Assignee = null, RequestId = Guid.NewGuid(), ExpectedRowVersion = updated.RowVersion }, "admin", UserRole.Administrator, Ct);
        Assert.Null(optional.State.Lots.Single(lot => lot.Id == lotId).Assignee);
        Assert.DoesNotContain("原因：", optional.State.Movements.Last().Note);
    }

    [Fact]
    public async Task BatchAllocationUsesFrozenBomQuantityPreservesSplitLotsAndFillsUnallocatedDemand()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var first = Assert.Single(received.State.Lines);
        var second = first with { Id = Guid.NewGuid(), MaterialKey = "second", SourceBomItemIds = [], DrawingNumber = "NS-002", RequiredQuantity = 3 };
        Assert.True(await fixture.Store.SaveAsync(received with { State = received.State with { Lines = [first, second] } }, received.RowVersion, Ct));
        var allocated = await fixture.Service.AssignAsync(fixture.Package.Id, new(first.Id, "Self", 6, null, "原负责人", null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var oldId = Assert.Single(allocated.State.Lots).Id;
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        Assert.True(await fixture.Store.SaveCapacityAsync(new(fixture.Project.OrganizationId ?? Guid.Empty, [new("CNC组", date, 8)]), 0, Ct));
        await fixture.Service.RecordAsync(fixture.Package.Id, new(oldId, "ProductionReceived", 6, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var scheduled = await fixture.Service.ScheduleAsync(fixture.Package.Id, new(oldId, [new("CNC组", date, 8)], date, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var input = new BatchProductionAllocation([first.Id, second.Id], "External", "外协厂", null, date, null, Guid.NewGuid(), scheduled.RowVersion);
        var changed = await fixture.Service.BatchAllocateAsync(fixture.Package.Id, input, "admin", UserRole.Administrator, Ct);
        Assert.Equal(3, changed.State.Lots.Count);
        Assert.Equal(10, changed.State.Lots.Where(lot => lot.LineId == first.Id).Sum(lot => lot.Quantity));
        Assert.Equal(3, changed.State.Lots.Where(lot => lot.LineId == second.Id).Sum(lot => lot.Quantity));
        Assert.Equal(6, changed.State.Lots.Single(lot => lot.Id == oldId).Quantity);
        Assert.Equal("原负责人", changed.State.Lots.Single(lot => lot.Id == oldId).Assignee);
        Assert.All(changed.State.Lots, lot => { Assert.Equal("External", lot.Route); Assert.Empty(lot.Schedule); });
        var replay = await fixture.Service.BatchAllocateAsync(fixture.Package.Id, input, "admin", UserRole.Administrator, Ct);
        Assert.Equal(changed.RowVersion, replay.RowVersion);
        var ids = changed.State.Lots.Select(lot => lot.Id).Order().ToArray();
        var self = await fixture.Service.BatchAllocateAsync(fixture.Package.Id, input with { Route = "Self", PromisedOn = null, RequestId = Guid.NewGuid(), ExpectedRowVersion = changed.RowVersion }, "admin", UserRole.Administrator, Ct);
        Assert.Equal(ids, self.State.Lots.Select(lot => lot.Id).Order().ToArray());
        Assert.All(self.State.Lots, lot => { Assert.Equal("Self", lot.Route); Assert.Null(lot.Supplier); });
        await fixture.Service.RecordAsync(fixture.Package.Id, new(oldId, "ProductionReceived", 6, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        await fixture.Service.ScheduleAsync(fixture.Package.Id, new(oldId, [new("CNC组", date, 8)], date, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
    }

    [Fact]
    public async Task BatchAllocationRejectsBlockedSelectionWithoutPartiallyChangingOtherLines()
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var first = Assert.Single(received.State.Lines);
        var allocated = await fixture.Service.AssignAsync(fixture.Package.Id, new(first.Id, "Self", 6, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var second = first with { Id = Guid.NewGuid(), MaterialKey = "second", SourceBomItemIds = [], DrawingNumber = "NS-002", RequiredQuantity = 3 };
        var delivered = new ProductionLot(Guid.NewGuid(), second.Id, "External", 3, "外协厂", null, null, null, null, 0);
        var state = allocated.State with { Lines = [first, second], Lots = allocated.State.Lots.Append(delivered).ToArray(),
            Movements = allocated.State.Movements.Append(new ProductionMovement(Guid.NewGuid(), Guid.NewGuid(), delivered.Id, "ExternalDelivered", 1, "admin", DateTimeOffset.UtcNow)).ToArray() };
        Assert.True(await fixture.Store.SaveAsync(allocated with { State = state }, allocated.RowVersion, Ct));
        var current = (await fixture.Store.FindAsync(fixture.Package.Id, Ct))!;
        var input = new BatchProductionAllocation([first.Id, second.Id], "External", "新外协厂", null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "", Guid.NewGuid(), current.RowVersion);
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.BatchAllocateAsync(fixture.Package.Id, input, "admin", UserRole.Administrator, Ct));
        var unchanged = (await fixture.Store.FindAsync(fixture.Package.Id, Ct))!;
        Assert.Equal(current.RowVersion, unchanged.RowVersion);
        Assert.Equal(2, unchanged.State.Lots.Count);
        Assert.Equal("Self", unchanged.State.Lots.Single(lot => lot.LineId == first.Id).Route);
        var changed = await fixture.Service.BatchAllocateAsync(fixture.Package.Id, input with { LineIds = [first.Id] }, "admin", UserRole.Administrator, Ct);
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.BatchAllocateAsync(fixture.Package.Id, input with { LineIds = [first.Id], RequestId = Guid.NewGuid() }, "admin", UserRole.Administrator, Ct));
        Assert.Equal(10, changed.State.Lots.Where(lot => lot.LineId == first.Id).Sum(lot => lot.Quantity));
    }

    [Theory]
    [InlineData("Start")]
    [InlineData("MachiningCompleted")]
    [InlineData("ExternalDelivered")]
    [InlineData("QualityReceived")]
    [InlineData("Pass")]
    [InlineData("Issue")]
    [InlineData("Cancelled")]
    [InlineData("Transferred")]
    [InlineData("Frozen")]
    [InlineData("Paused")]
    public async Task AllocationCannotBeEditedAfterBusinessExecutionOrWhileBlocked(string kind)
    {
        var fixture = await FixtureAsync();
        var received = await fixture.Service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        var allocated = await fixture.Service.AssignAsync(fixture.Package.Id, new(line.Id, "Self", 10, null, null, null, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        var lot = Assert.Single(allocated.State.Lots);
        var state = kind is "Frozen" or "Paused"
            ? allocated.State with { Lots = [lot with { Frozen = kind == "Frozen", Paused = kind == "Paused" }] }
            : allocated.State with { Movements = allocated.State.Movements.Append(new ProductionMovement(Guid.NewGuid(), Guid.NewGuid(), lot.Id, kind, 1, "admin", DateTimeOffset.UtcNow)).ToArray() };
        Assert.True(await fixture.Store.SaveAsync(allocated with { State = state }, allocated.RowVersion, Ct));
        var current = (await fixture.Store.FindAsync(fixture.Package.Id, Ct))!;
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Service.UpdateAllocationAsync(fixture.Package.Id,
            new(lot.Id, "External", 10, "供应商", null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "不能覆盖已加工批次", Guid.NewGuid(), current.RowVersion), "admin", UserRole.Administrator, Ct));
        Assert.Equal("Self", Assert.Single((await fixture.Store.FindAsync(fixture.Package.Id, Ct))!.State.Lots).Route);
    }

    [Fact]
    public async Task PublishingImmediatelyCreatesThePackageTrackingUnit()
    {
        var fixture = await FixtureAsync();
        Assert.Null(await fixture.Store.FindAsync(fixture.Package.Id, Ct));
        await fixture.Service.PublishedAsync(fixture.Package.Id, Ct);
        var created = await fixture.Store.FindAsync(fixture.Package.Id, Ct);
        Assert.NotNull(created);
        Assert.Equal("待接收", ProductionTrackingRules.Status(created.State));
        await fixture.Service.PublishedAsync(fixture.Package.Id, Ct);
        Assert.Equal(created.RowVersion, (await fixture.Store.FindAsync(fixture.Package.Id, Ct))!.RowVersion);
    }

    [Fact]
    public async Task SelfAndExternalMeetAtQualityAndOnlyQualifiedQuantityCanBeIssued()
    {
        var fixture = await FixtureAsync();
        var service = fixture.Service;
        var summary = Assert.Single(Assert.Single(await service.ListAsync("admin", UserRole.Administrator, Ct)).Packages);
        Assert.Equal("待接收", summary.Status);
        var received = await service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(received.State.Lines);
        var selfRequest = Guid.NewGuid();
        var self = await service.AssignAsync(fixture.Package.Id,
            new(line.Id, "Self", 6, null, "机加主管", null, selfRequest), "admin", UserRole.Administrator, Ct);
        var duplicate = await service.AssignAsync(fixture.Package.Id,
            new(line.Id, "Self", 6, null, "机加主管", null, selfRequest), "admin", UserRole.Administrator, Ct);
        Assert.Single(duplicate.State.Lots);
        var selfLot = Assert.Single(self.State.Lots);
        var external = await service.AssignAsync(fixture.Package.Id,
            new(line.Id, "External", 4, "外协厂A", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct);
        var externalLot = Assert.Single(external.State.Lots, item => item.Route == "External");
        await Assert.ThrowsAsync<PdmRuleException>(() => service.AssignAsync(fixture.Package.Id,
            new(line.Id, "External", 1, "外协厂A", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct));

        var companyId = fixture.Project.OrganizationId ?? Guid.Empty;
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        Assert.True(await fixture.Store.SaveCapacityAsync(new(companyId,
            [new("CNC组", date, 8)], 1), 0, Ct));
        await service.RecordAsync(fixture.Package.Id, new(selfLot.Id, "ProductionReceived", 6, Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        await service.ScheduleAsync(fixture.Package.Id,
            new(selfLot.Id, [new("CNC组", date, 6)], date, Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct);
        async Task Move(Guid lotId, string kind, decimal count) => _ = await service.RecordAsync(fixture.Package.Id,
            new(lotId, kind, count, Guid.NewGuid(), Recipient: kind == "Issue" ? "项目领料员" : null),
            "admin", UserRole.Administrator, Ct);
        await Move(selfLot.Id, "Start", 6);
        await Move(selfLot.Id, "MachiningCompleted", 6);
        await Move(selfLot.Id, "PackingStarted", 6);
        await Move(selfLot.Id, "Packed", 6);
        await Move(externalLot.Id, "ExternalDelivered", 4);
        await Move(selfLot.Id, "QualityReceived", 6);
        await Move(externalLot.Id, "QualityReceived", 4);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RecordAsync(fixture.Package.Id,
            new(selfLot.Id, "Issue", 1, Guid.NewGuid(), Recipient: "项目领料员"),
            "admin", UserRole.Administrator, Ct));
        await Move(selfLot.Id, "Pass", 6);
        await Move(externalLot.Id, "Pass", 4);
        var qualified = await service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        Assert.Equal(1, ProductionTrackingRules.QualifiedLines(qualified.State));
        await Move(selfLot.Id, "Issue", 6);
        await Move(externalLot.Id, "Issue", 4);
        var finished = await service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        Assert.Equal("已完结", ProductionTrackingRules.Status(finished.State));
        Assert.Equal(1, ProductionTrackingRules.IssuedLines(finished.State));
        Assert.All(finished.State.Movements.Where(item => item.Kind == "Issue"),
            item => Assert.Equal("项目领料员", item.Recipient));
    }

    [Fact]
    public async Task SupplementFreezesOnlyChangedRevisionAndTransfersRemainingOnce()
    {
        var fixture = await FixtureAsync();
        var service = fixture.Service;
        var line = Assert.Single((await service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(),
            "admin", UserRole.Administrator, Ct)).State.Lines);
        var prior = await service.AssignAsync(fixture.Package.Id, new(line.Id, "External", 10,
            "外协厂A", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct);
        var lot = Assert.Single(prior.State.Lots);
        var changed = fixture.Item with { Id = Guid.NewGuid(), Revision = "C" };
        var supplement = fixture.Package with
        {
            Id = Guid.NewGuid(), Number = "FB-002", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = fixture.Package.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [changed], SelectedBomItemIds = [changed.Id]
        };
        await fixture.Repository.CreateReleasePackageAsync(supplement, Ct);
        await service.ListAsync("admin", UserRole.Administrator, Ct);
        var frozen = await service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        Assert.True(Assert.Single(frozen.State.Lots).Frozen);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.RecordAsync(fixture.Package.Id,
            new(lot.Id, "ExternalDelivered", 1, Guid.NewGuid()), "admin", UserRole.Administrator, Ct));
        await service.ReceiveAsync(supplement.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var requestId = Guid.NewGuid();
        var decision = new ResolveProductionRevision(lot.Id, supplement.Id, "TransferNew", "新版图纸重做", requestId);
        await service.ResolveRevisionAsync(fixture.Package.Id, decision, "admin", UserRole.Administrator, Ct);
        await service.ResolveRevisionAsync(fixture.Package.Id, decision, "admin", UserRole.Administrator, Ct);
        var newer = await service.FindAsync(supplement.Id, "admin", UserRole.Administrator, Ct);
        var transferred = Assert.Single(newer.State.Lines);
        Assert.Equal(10, transferred.RequiredQuantity);
        Assert.Equal("C", transferred.Revision);
        Assert.Equal("变更已结案", ProductionTrackingRules.Status((await service.FindAsync(
            fixture.Package.Id, "admin", UserRole.Administrator, Ct)).State));
    }

    [Fact]
    public async Task CapacityReservesHoursAcrossDifferentProjectsAndRejectsAnOverlappingConfirmation()
    {
        var companyId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var store = new InMemoryProductionTrackingStore();
        Assert.True(await store.SaveCapacityAsync(new(companyId, [new("CNC组", date, 8)], 1), 0, Ct));
        ProductionTrackingState State(Guid releaseId, Guid projectId, Guid lotId) => new(releaseId, projectId,
            companyId, "P", "项目", "FB", ReleaseScope.NonStandardWithDrawing, DateTimeOffset.UtcNow,
            [], [new ProductionLot(lotId, Guid.NewGuid(), "Self", 1, null, "机加", date,
                "CNC组", date, 6) { Schedule = [new("CNC组", date, 6)] }], [], "供应链", DateTimeOffset.UtcNow);
        var first = await store.CreateAsync(State(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Ct);
        var second = await store.CreateAsync(State(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) with
        { Lots = [] }, Ct);
        Assert.True(await store.SaveScheduleAsync(first with { RowVersion = 2 }, 1, Ct));
        var secondWithSchedule = second with { State = second.State with
            { Lots = [new ProductionLot(Guid.NewGuid(), Guid.NewGuid(), "Self", 1, null, "机加", date,
                "CNC组", date, 6) { Schedule = [new("CNC组", date, 6)] }] }, RowVersion = 2 };
        await Assert.ThrowsAsync<PdmConflictException>(() => store.SaveScheduleAsync(secondWithSchedule, 1, Ct));
        Assert.Empty((await store.FindAsync(second.State.ReleasePackageId, Ct))!.State.Lots);
    }

    [Fact]
    public async Task PreActivationReleaseIsNotInventedAsUnfinishedHistory()
    {
        var fixture = await FixtureAsync();
        var later = new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddDays(1));
        var service = new ProductionTrackingService(fixture.Repository, later, TimeProvider.System,
            new ProductionDrawingService(fixture.Repository));
        Assert.Empty(await service.ListAsync("admin", UserRole.Administrator, Ct));
        var preview = await service.PreviewExistingAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        var line = Assert.Single(preview.Lines);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.IncludeExistingAsync(fixture.Package.Id,
            new(DateTimeOffset.UtcNow, true, [line.MaterialKey],
                [new(line.MaterialKey, "External", 3, "Issued", "外协厂A")], "现场核对"),
            "admin", UserRole.Administrator, Ct));
        var included = await service.IncludeExistingAsync(fixture.Package.Id,
            new(DateTimeOffset.UtcNow, true, [line.MaterialKey],
                [new(line.MaterialKey, "External", 3, "Issued", "外协厂A", Recipient: "项目领料员"),
                    new(line.MaterialKey, "Self", 4, "Packed", Assignee: "机加组")],
                "供应链与质量部门共同核对"), "admin", UserRole.Administrator, Ct);
        Assert.Single(included.State.Lines);
        Assert.Equal(7, included.State.Lots.Sum(item => item.Quantity));
        Assert.Equal(3, ProductionTrackingRules.Issued(included.State, included.State.Lines[0].Id));
        Assert.Equal("项目领料员", Assert.Single(included.State.Movements, item => item.Kind == "Issue").Recipient);
        Assert.NotNull(included.State.ReceivedAt);
    }

    [Fact]
    public async Task QuantityReductionCancelsOnlyTheReducedAmountAndReworkReturnsToQuality()
    {
        var fixture = await FixtureAsync();
        var service = fixture.Service;
        var line = Assert.Single((await service.ReceiveAsync(fixture.Package.Id, Guid.NewGuid(),
            "admin", UserRole.Administrator, Ct)).State.Lines);
        var assigned = await service.AssignAsync(fixture.Package.Id, new(line.Id, "External", 10,
            "外协厂A", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), Guid.NewGuid()),
            "admin", UserRole.Administrator, Ct);
        var lot = Assert.Single(assigned.State.Lots);
        var reducedItem = fixture.Item with { Id = Guid.NewGuid(), Quantity = 8 };
        var supplement = fixture.Package with
        {
            Id = Guid.NewGuid(), Number = "FB-REDUCED", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = fixture.Package.PublishedAt!.Value.AddMinutes(1),
            NonStandardBomSnapshot = [reducedItem], SelectedBomItemIds = [reducedItem.Id]
        };
        await fixture.Repository.CreateReleasePackageAsync(supplement, Ct);
        await service.ListAsync("admin", UserRole.Administrator, Ct);
        Assert.True(Assert.Single((await service.FindAsync(fixture.Package.Id, "admin",
            UserRole.Administrator, Ct)).State.Lots).Frozen);
        await service.ResolveRevisionAsync(fixture.Package.Id,
            new(lot.Id, supplement.Id, "CancelRemaining", "设计减量2件", Guid.NewGuid(), 2),
            "admin", UserRole.Administrator, Ct);
        async Task Move(string kind, decimal count, string? note = null) => _ = await service.RecordAsync(
            fixture.Package.Id, new(lot.Id, kind, count, Guid.NewGuid(), note,
                kind == "Issue" ? "专职领料员" : null), "admin", UserRole.Administrator, Ct);
        await Move("ExternalDelivered", 8);
        await Move("QualityReceived", 8);
        await Move("Pass", 6);
        await Move("Rework", 2, "孔径不合格");
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RecordAsync(fixture.Package.Id,
            new(lot.Id, "Issue", 7, Guid.NewGuid(), Recipient: "专职领料员"),
            "admin", UserRole.Administrator, Ct));
        await Move("ReworkCompleted", 2);
        await Move("Pass", 2);
        await Move("Issue", 8);
        var result = await service.FindAsync(fixture.Package.Id, "admin", UserRole.Administrator, Ct);
        Assert.Equal("变更已结案", ProductionTrackingRules.Status(result.State));
        Assert.Equal(8, ProductionTrackingRules.EffectiveDemand(result.State, line));
        Assert.Equal(1, ProductionTrackingRules.QualifiedLines(result.State));
        Assert.Equal(1, ProductionTrackingRules.IssuedLines(result.State));
    }
}
