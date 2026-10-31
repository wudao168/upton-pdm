using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class NameplateWorkflowTests
{
    private static readonly CancellationToken Ct = default;

    [Fact]
    public async Task EachPositionCanOnlySaveItsOwnFieldsAndHardwareCanConfirmItsOwnEntry()
    {
        var f = await FixtureAsync();
        var execution = await f.Service.GetAsync(f.Project.Id, "engineer", UserRole.Engineer, Ct);
        Assert.True(execution.CanEditExecution);
        Assert.False(execution.CanEditHardware);
        Assert.False(execution.CanConfirm);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Project.Id,
            new(true, "380V 50Hz / 5A", "", "", "", null, 0), "engineer", UserRole.Engineer, Ct));
        var saved = await f.Service.SaveAsync(f.Project.Id,
            new(true, "", "1500mm x 1800mm x 2200mm", "0.5~0.7 MPa", "12000 kg", new(2026, 10, 1), 0), "engineer", UserRole.Engineer, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ConfirmAsync(f.Project.Id,
            new(saved.Nameplate.RowVersion), "engineer", UserRole.Engineer, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Project.Id,
            Command(saved.Nameplate) with { Weight = "1 kg" }, "hardware", UserRole.Engineer, Ct));
        saved = await f.Service.SaveAsync(f.Project.Id, Command(saved.Nameplate) with { PowerSupply = "380V 50Hz / 5A" }, "hardware", UserRole.Engineer, Ct);
        Assert.Equal("engineer", saved.Nameplate.ExecutionSavedBy);
        Assert.Equal("hardware", saved.Nameplate.HardwareSavedBy);
        var released = await f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct);
        Assert.Equal("hardware", released.Nameplate.ConfirmedBy);
        Assert.False(released.CanEdit);
        var package = (await f.Repository.FindReleasePackageAsync(released.Nameplate.ReleasePackageId!.Value, Ct))!;
        Assert.Equal(ReleaseScope.Nameplate, package.Scope);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.False(ReleasePreviewPolicy.RequiresConversion(package));
        Assert.Null(package.ReferenceSnapshotId);
        Assert.Empty(package.VersionBindings!);
        Assert.Equal(ApprovalDecision.Approved, Assert.Single(package.ApprovalTasks).Decision);
        Assert.Equal(2, package.NonStandardBomSnapshot.Count);
        Assert.All(package.NonStandardBomSnapshot, item => { Assert.Equal(1, item.Quantity); Assert.Null(item.SourceDocumentId); Assert.Equal(1.9m, item.NameplateContent!.RatedPower); });
        Assert.Equal(f.Project.SerialNumbers.Order(), package.NonStandardBomSnapshot.Select(item => item.NameplateContent!.SerialNumber).Order());
        var record = await f.Tracking.FindAsync(package.Id, "admin", UserRole.Administrator, Ct);
        Assert.Equal(2, record.State.Lines.Count);
        Assert.All(record.State.Lines, line => Assert.NotNull(line.NameplateContent));
        var received = await f.Tracking.ReceiveAsync(package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, Ct);
        var assigned = await f.Tracking.AssignAsync(package.Id,
            new(received.State.Lines[0].Id, "Self", 1, null, "engineer", new(2026, 11, 1), Guid.NewGuid()), "admin", UserRole.Administrator, Ct);
        Assert.Single(assigned.State.Lots);
        Assert.True(ReleaseDeliveryService.Ready(new ReleaseDeliveryState { ReleasePackageId = package.Id, ProjectId = package.ProjectId,
            CompanyId = f.Project.OrganizationId!.Value, PackageNumber = package.Number, ProjectCode = f.Project.Code,
            PublishedAt = package.PublishedAt!.Value, RequiresU9 = false, PreviewRequired = false }));
    }

    [Fact]
    public async Task PartialOrStaleContentCannotPublishAndConfirmationIsIdempotent()
    {
        var f = await FixtureAsync();
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ConfirmAsync(f.Project.Id, new(0), "hardware", UserRole.Engineer, Ct));
        var saved = await SaveCompleteAsync(f);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion - 1), "hardware", UserRole.Engineer, Ct));
        Assert.Empty(await f.Repository.ListReleasePackagesAsync(f.Project.Id, Ct));
        var confirmed = await f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct);
        var repeat = await f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct);
        Assert.Equal(confirmed.Nameplate.ReleasePackageId, repeat.Nameplate.ReleasePackageId);
        Assert.Single(await f.Repository.ListReleasePackagesAsync(f.Project.Id, Ct));
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveAsync(f.Project.Id,
            Command(confirmed.Nameplate) with { Weight = "1 kg" }, "engineer", UserRole.Engineer, Ct));
    }

    [Fact]
    public async Task MissingElectricalFieldsDoNotCreateAnyProductionDemand()
    {
        var f = await FixtureAsync();
        var saved = await f.Service.SaveAsync(f.Project.Id,
            new(true, "", "1500mm x 1800mm x 2200mm", "0.5~0.7 MPa", "12000 kg", new(2026, 10, 1), 0), "engineer", UserRole.Engineer, Ct);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct));
        Assert.Empty(await f.Repository.ListReleasePackagesAsync(f.Project.Id, Ct));
    }

    [Fact]
    public async Task SerialScanFindsOnlyTheMatchingPublishedNameplateAndChecksAccess()
    {
        var f = await FixtureAsync();
        var serial = f.Project.SerialNumbers[0];
        Assert.Empty(await f.Tracking.ResolveScanAsync(serial, "admin", UserRole.Administrator, Ct));
        var saved = await SaveCompleteAsync(f);
        var confirmed = await f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct);
        var first = Assert.Single(await f.Tracking.ResolveScanAsync(" " + serial + "\r\n", "admin", UserRole.Administrator, Ct));
        Assert.Equal(confirmed.Nameplate.ReleasePackageId, first.ReleasePackageId);
        Assert.Equal(serial, first.DrawingNumber);
        Assert.Null(first.LotId);
        var other = Assert.Single(await f.Tracking.ResolveScanAsync(f.Project.SerialNumbers[1], "admin", UserRole.Administrator, Ct));
        Assert.NotEqual(first.LineId, other.LineId);
        Assert.Empty(await f.Tracking.ResolveScanAsync("NOT-A-NAMEPLATE-SERIAL", "admin", UserRole.Administrator, Ct));
        try {
            var company = Guid.NewGuid();
            TenantContext.Set(new(Guid.NewGuid(), company, company, "admin", "Administrator", false, new HashSet<string>()));
            Assert.Empty(await f.Tracking.ResolveScanAsync(serial, "admin", UserRole.Administrator, Ct));
        } finally { TenantContext.Clear(); }
        await f.Repository.SetRolePermissionsAsync("Engineer", [], Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Tracking.ResolveScanAsync(serial, "engineer", UserRole.Engineer, Ct));
    }

    [Fact]
    public async Task SerialScanKeepsMultiplePublishedDemandsForExplicitSelection()
    {
        var f = await FixtureAsync();
        var saved = await SaveCompleteAsync(f);
        var confirmed = await f.Service.ConfirmAsync(f.Project.Id, new(saved.Nameplate.RowVersion), "hardware", UserRole.Engineer, Ct);
        var original = (await f.Repository.FindReleasePackageAsync(confirmed.Nameplate.ReleasePackageId!.Value, Ct))!;
        var next = original with { Id = Guid.NewGuid(), Number = original.Number + "-NEXT", ApprovalTasks = [], PublishedAt = original.PublishedAt!.Value.AddMinutes(1) };
        await f.Repository.CreateReleasePackageAsync(next, Ct);
        await f.Tracking.PublishedAsync(next.Id, Ct);
        var results = await f.Tracking.ResolveScanAsync(f.Project.SerialNumbers[0], "admin", UserRole.Administrator, Ct);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.ReleasePackageId == original.Id);
        Assert.Contains(results, item => item.ReleasePackageId == next.Id);
    }

    private static SaveProjectNameplateCommand Command(ProjectNameplate plate) => new(plate.Enabled, plate.PowerSupply,
        plate.Dimensions, plate.AirPressure, plate.Weight, plate.FactoryDate, plate.RowVersion);

    private static async Task<ProjectNameplateView> SaveCompleteAsync(Fixture f)
    {
        var view = await f.Service.SaveAsync(f.Project.Id, new(true, "", "1500mm x 1800mm x 2200mm", "0.5~0.7 MPa", "12000 kg", new(2026, 10, 1), 0), "engineer", UserRole.Engineer, Ct);
        return await f.Service.SaveAsync(f.Project.Id, Command(view.Nameplate) with { PowerSupply = "380V 50Hz / 5A" }, "hardware", UserRole.Engineer, Ct);
    }

    private sealed record Fixture(InMemoryPdmRepository Repository, Project Project, ProjectNameplateService Service, ProductionTrackingService Tracking);
    private static async Task<Fixture> FixtureAsync()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "铭牌生产", null, new(2026, 10, 2), 2, "admin", "D:/test", "D:/release"), Ct);
        await repository.SetChildProjectDesignersAsync(project.Id, ["engineer"], "admin", Ct);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "hardware", "硬件工程师", "unused", UserRole.Engineer, true, RoleCode: "HardwareEngineer"), Ct);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectNameplateEdit], Ct);
        await repository.SetRolePermissionsAsync("HardwareEngineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectNameplateEdit], Ct);
        await repository.SetProjectPhaseOwnersAsync(project.Id, new Dictionary<string, string> { ["ElectricalHardware"] = "hardware" }, "admin", Ct);
        var store = new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddDays(-1));
        var tracking = new ProductionTrackingService(repository, store, TimeProvider.System, new(repository));
        return new(repository, project, new(repository, new InMemoryProjectNameplateRepository(repository), TimeProvider.System, tracking), tracking);
    }
}
