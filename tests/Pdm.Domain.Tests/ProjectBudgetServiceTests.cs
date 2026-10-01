using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectBudgetServiceTests
{
    [Fact]
    public void OrdersAreNotAddedTwiceAndMissingCostsStayUnknown()
    {
        var row = ProjectBudgetService.Calculate(new("Standard", 1000, 400, 100), 600);
        Assert.Equal(700, row.EstimatedAmount);
        Assert.Equal(-300, row.Variance);
        Assert.Null(ProjectBudgetService.Calculate(new("Standard", 1000)).EstimatedAmount);
        Assert.Equal("Incomplete", ProjectBudgetService.Calculate(new("Standard", 1000, 400, 0), null, true).Alert);
        Assert.Equal("Overrun", ProjectBudgetService.Calculate(new("Standard", 1000, 1100, null)).Alert);
    }

    [Fact]
    public void LaborUsesManualActualAmountAndForecastsRemainingHours()
    {
        var row = ProjectBudgetService.Calculate(new("SiteElectrical", BudgetAmount: 1000, PlannedHours: 10, HourlyRate: 500,
            ActualAmount: 600, ActualHours: 99, ActualHourlyRate: 100, RemainingHours: 3));
        Assert.Equal(1000, row.BudgetAmount);
        Assert.Equal(600, row.ActualAmount);
        Assert.Equal(900, row.EstimatedAmount);
        Assert.Equal("Warning", row.Alert);
    }

    [Fact]
    public async Task LegacyLaborBudgetKeepsItsAmountAndCanBeClearedAfterConversion()
    {
        var (service, store, projectId) = await Setup();
        var lines = ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category)).ToArray();
        lines = lines.Select(line => line.Category == "MechanicalDesign"
            ? line with { PlannedHours = 10, HourlyRate = 100, ActualHours = 2, ActualHourlyRate = 200, RemainingHours = 1 }
            : line).ToArray();
        await store.SaveAsync(new(projectId, lines), 0, default);
        var view = await service.GetAsync(projectId, "planner", UserRole.PlanningManager, default);
        var labor = view.Rows.Single(row => row.Input.Category == "MechanicalDesign");
        Assert.Equal(1000, labor.Input.BudgetAmount);
        Assert.Null(labor.Input.PlannedHours);
        Assert.Equal(600, labor.EstimatedAmount);
        var input = view.Rows.Select(row => row.Input.Category == "MechanicalDesign"
            ? row.Input with { BudgetAmount = null } : row.Input).ToArray();
        var saved = await service.SaveAsync(projectId, new(input, 1), "planner", UserRole.PlanningManager, default);
        Assert.Null(saved.Rows.Single(row => row.Input.Category == "MechanicalDesign").BudgetAmount);
    }

    [Fact]
    public async Task ReserveIsRemovedFromResponseAndEveryTotalEvenForAdministrator()
    {
        var (service, store, projectId) = await Setup();
        var lines = ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, 100, 20, 0,
            PlannedHours: 1, HourlyRate: 100, ActualHours: 1, ActualHourlyRate: 20, RemainingHours: 0)).ToArray();
        await store.SaveAsync(new(projectId, lines), 0, default);
        var planning = await service.GetAsync(projectId, "planner", UserRole.PlanningManager, default);
        var other = await service.GetAsync(projectId, "admin", UserRole.Administrator, default);
        Assert.True(planning.CanViewReserve);
        Assert.False(other.CanViewReserve);
        Assert.DoesNotContain(other.Rows, row => row.Input.Category == ProjectBudgetCategories.Reserve);
        Assert.Equal(1100, planning.BudgetAmount);
        Assert.Equal(1000, other.BudgetAmount);
        Assert.Equal(220, planning.ActualAmount);
        Assert.Equal(200, other.ActualAmount);
        Assert.Equal(220, planning.EstimatedAmount);
        Assert.Equal(200, other.EstimatedAmount);
    }

    [Fact]
    public async Task HiddenReserveCannotBeSubmittedAndIsPreservedOnOtherEdits()
    {
        var (service, store, projectId) = await Setup();
        var lines = ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, 100, 0, 0)).ToArray();
        await store.SaveAsync(new(projectId, lines), 0, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(projectId, new(lines, 1), "admin", UserRole.Administrator, default));
        var visible = lines.Where(line => line.Category != ProjectBudgetCategories.Reserve).Select(line => line with { BudgetAmount = 200 }).ToArray();
        await service.SaveAsync(projectId, new(visible, 1), "admin", UserRole.Administrator, default);
        var stored = await store.FindAsync(projectId, default);
        Assert.Equal(100, stored!.Lines.Single(line => line.Category == ProjectBudgetCategories.Reserve).BudgetAmount);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(projectId, new(visible, 1), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EquipmentOrdersAreCountedOnlyOnceAndCanceledOrdersAreExcluded()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectBudgetRepository();
        var procurement = new InMemoryU9ProcurementRepository();
        var order = new U9ProcurementSourceRow("ORG", "PO", "line1", null, "PO001", 1, 2, false, null,
            "DEVICE", "外购设备", null, null, project.Code, project.Name, null, 0, 0, 1, 0, null, null, null)
            { OrderNetAmount = 600 };
        await procurement.ReplaceSnapshotAsync(Guid.NewGuid(), [order, order, order with { LineId = "line2", IsCanceled = true, OrderNetAmount = 999 }], TimeProvider.System.GetUtcNow(), default);
        await store.SaveAsync(new(project.Id, [new("Equipment", 1000, 100, 50), new("Standard", 2000, 0, 0)],
            OrderCategories: new Dictionary<string, string> { ["ORG:line1"] = "Equipment" }), 0, default);
        var service = new ProjectBudgetService(repository, store, procurement, TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        var view = await service.GetAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.Single(view.Orders);
        Assert.Equal(600, view.Rows.Single(row => row.Input.Category == "Equipment").OrderAmount);
        Assert.Equal(650, view.Rows.Single(row => row.Input.Category == "Equipment").EstimatedAmount);
        Assert.Null(view.Rows.Single(row => row.Input.Category == "Standard").OrderAmount);
    }

    [Fact]
    public async Task ViewerAndCrossCompanyRequestsCannotWriteOrRead()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectBudgetService(repository, new InMemoryProjectBudgetRepository(), new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(project.Id, "engineer", UserRole.Engineer, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectBudgetView], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, new([], 0), "unknown-engineer", UserRole.Engineer, default));
        var organization = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First();
        var customer = (await repository.ListCustomersAsync(false, default)).First();
        var scoped = await repository.CreateNumberedProjectAsync(new(organization.Id, "P", 1, customer.Id, "公司项目", null,
            new DateOnly(2026, 10, 1), 1, "planner", @"D:\Vault", @"D:\Release"), default);
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "planner", "PlanningManager", false, new HashSet<string>()));
        try { await Assert.ThrowsAsync<PdmNotFoundException>(() => service.GetAsync(scoped.Id, "planner", UserRole.PlanningManager, default)); }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task ActualCostsRequireSeparatePermissionAndRemainIndependentOfHours()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectBudgetRepository();
        var service = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectBudgetActualEdit], default);
        var view = await service.GetAsync(project.Id, "unknown-engineer", UserRole.Engineer, default);
        Assert.True(view.CanEditActual);
        Assert.False(view.CanEditBudget);
        var lines = view.Rows.Select(row => row.Input.Category == "MechanicalDesign" ? row.Input with { ActualAmount = 1234, ActualHours = 7 } : row.Input).ToArray();
        var saved = await service.SaveAsync(project.Id, new(lines, 0), "unknown-engineer", UserRole.Engineer, default);
        Assert.Equal(1234, saved.Rows.Single(row => row.Input.Category == "MechanicalDesign").ActualAmount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, new(lines.Select(line => line with { BudgetAmount = 500 }).ToArray(), 1), "unknown-engineer", UserRole.Engineer, default));
        var planning = await service.GetAsync(project.Id, "planner", UserRole.PlanningManager, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, new(planning.Rows.Select(row => row.Input with { ActualAmount = 999 }).ToArray(), 1), "planner", UserRole.PlanningManager, default));
        var cleared = await service.SaveAsync(project.Id, new(saved.Rows.Select(row => row.Input with { ActualAmount = null }).ToArray(), 1), "unknown-engineer", UserRole.Engineer, default);
        Assert.Null(cleared.Rows.Single(row => row.Input.Category == "MechanicalDesign").ActualAmount);
    }

    private static async Task<(ProjectBudgetService, InMemoryProjectBudgetRepository, Guid)> Setup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectBudgetRepository();
        return (new(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System)), store, project.Id);
    }
}
