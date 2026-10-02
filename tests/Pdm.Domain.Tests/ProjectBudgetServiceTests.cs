using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectBudgetServiceTests
{
    [Fact]
    public async Task SalesAssistantCanEnterSettlementButCannotChangeBonusOrBudget()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "sales", "销售助理", "unused", UserRole.ProductionViewer, true, RoleCode: "SalesAssistant"), default);
        var store = new InMemoryProjectBudgetRepository();
        var service = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sales", "SalesAssistant", false, new HashSet<string>()));
        try
        {
            var saved = await service.SaveSettlementAsync(project.Id, new([new("设备款", "台", 2, 1000)], 0), "sales", UserRole.ProductionViewer, default);
            Assert.Equal(2000m, saved.SettlementAmount);
            Assert.True(saved.CanEditSettlement);
            Assert.False(saved.CanEditBudget);
            Assert.False(saved.CanEditActual);
            Assert.False(saved.CanEditBonus);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveBonusRateAsync(project.Id, new(.01m, saved.RowVersion), "sales", UserRole.ProductionViewer, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, new(saved.Rows.Select(x => x.Input).ToArray(), saved.RowVersion), "sales", UserRole.ProductionViewer, default));
        }
        finally { TenantContext.Clear(); }
    }
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

    [Fact]
    public async Task BudgetViewersCanAppendNotesWithoutEditingAmountsAndHistorySurvivesBudgetSave()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectBudgetView], default);
        var store = new InMemoryProjectBudgetRepository();
        var service = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        var lines = ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, BudgetAmount: 100, Note: category == "Standard" ? "历史内容" : null)).ToArray();
        await store.SaveAsync(new(project.Id, lines, UpdatedBy: "planner", UpdatedAt: TimeProvider.System.GetUtcNow()), 0, default);
        var first = await service.AddNoteAsync(project.Id, new("Standard", " 第一条 ", 1), "viewer", UserRole.Engineer, default);
        Assert.False(first.CanEdit);
        Assert.Equal("第一条", first.Notes!.Last().Content);
        Assert.Equal("viewer", first.Notes!.Last().CreatedBy);
        Assert.NotNull(first.Notes!.Last().CreatedAt);
        Assert.Equal(100, first.Rows.Single(row => row.Input.Category == "Standard").BudgetAmount);
        var second = await service.AddNoteAsync(project.Id, new("Standard", "第二条", 2), "planner", UserRole.PlanningManager, default);
        Assert.Equal(new[] { "历史内容", "第一条", "第二条" }, second.Notes!.Select(note => note.Content));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.AddNoteAsync(project.Id, new("Standard", "过期请求", 2), "viewer", UserRole.Engineer, default));
        var planning = await service.GetAsync(project.Id, "planner", UserRole.PlanningManager, default);
        var saved = await service.SaveAsync(project.Id, new(planning.Rows.Select(row => row.Input with { BudgetAmount = 200 }).ToArray(), 3), "planner", UserRole.PlanningManager, default);
        Assert.Equal(3, saved.Notes!.Count);
        Assert.Equal("历史内容", saved.Notes[0].Content);
        Assert.Equal(200, saved.Rows.Single(row => row.Input.Category == "Standard").BudgetAmount);
    }

    [Fact]
    public async Task NotesRequireBudgetReadAccessAndRespectReserveVisibility()
    {
        var (service, _, projectId) = await Setup();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddNoteAsync(projectId, new("Standard", "备注", 0), "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddNoteAsync(projectId, new("RiskReserve", "备注", 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.AddNoteAsync(projectId, new("Standard", "  ", 0), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.AddNoteAsync(projectId, new("Invalid", "备注", 0), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.AddNoteAsync(projectId, new("Standard", new string('字', 501), 0), "planner", UserRole.PlanningManager, default));
        await service.AddNoteAsync(projectId, new("RiskReserve", "保密备注", 0), "planner", UserRole.PlanningManager, default);
        var admin = await service.GetAsync(projectId, "admin", UserRole.Administrator, default);
        Assert.Empty(admin.Notes!);
    }

    [Theory]
    [InlineData(10000, 0)]
    [InlineData(199999.99, 0)]
    [InlineData(200000, 1000)]
    [InlineData(200001, 1000.01)]
    public async Task BonusRequiresSettlementOfAtLeastTwoHundredThousand(decimal amount, decimal expected)
    {
        var (service, _, projectId) = await Setup();
        var saved = await service.SaveSettlementAsync(projectId, new([new("结算", "项", 1, amount)], 0), "planner", UserRole.PlanningManager, default);
        Assert.Equal(expected, saved.BonusAmount);
    }

    [Theory]
    [InlineData("PlanningManager", true)]
    [InlineData("SalesAssistant", true)]
    [InlineData("BusinessUnitManager", true)]
    [InlineData("Finance", true)]
    [InlineData("Administrator", false)]
    [InlineData("Engineer", false)]
    public async Task SettlementIsVisibleOnlyToTheFourSpecifiedRoles(string roleCode, bool allowed)
    {
        var (service, _, projectId) = await Setup();
        await service.SaveSettlementAsync(projectId, new([new("结算", "项", 1, 10000)], 0), "planner", UserRole.PlanningManager, default);
        TenantContext.Set(new(Guid.NewGuid(), Guid.Empty, Guid.Empty, "admin", roleCode, false, new HashSet<string>()));
        try
        {
            var view = await service.GetAsync(projectId, "admin", UserRole.Administrator, default);
            Assert.Equal(allowed, view.CanViewSettlement);
            Assert.Equal(allowed ? 10000m : (decimal?)null, view.SettlementAmount);
            Assert.Equal(allowed ? 1 : 0, view.SettlementLines!.Count);
            Assert.Equal(0m, view.BonusAmount);
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task SettlementTotalsAndBonusRatesPersistIndependentlyOfBudgetEdits()
    {
        var (service, store, projectId) = await Setup();
        var initial = await service.GetAsync(projectId, "planner", UserRole.PlanningManager, default);
        Assert.Equal(.005m, initial.BonusRate);
        Assert.Null(initial.SettlementAmount);
        Assert.True(initial.CanEditSettlement);
        Assert.True(initial.CanEditBonus);
        var saved = await service.SaveSettlementAsync(projectId, new([new("装配线", "套", 1, 1620000), new("追加工作", "项", 2, 1000), new("")], 0), "planner", UserRole.PlanningManager, default);
        Assert.Equal(1622000, saved.SettlementAmount);
        Assert.Equal(8110, saved.BonusAmount);
        Assert.Equal(2, saved.SettlementLines!.Count);
        var manager = await service.GetAsync(projectId, "manager", UserRole.BusinessUnitManager, default);
        Assert.Equal(saved.SettlementAmount, manager.SettlementAmount);
        Assert.Equal(saved.SettlementLines, manager.SettlementLines);
        Assert.Equal(saved.BonusAmount, manager.BonusAmount);
        Assert.Null(manager.BonusRate);
        Assert.False(manager.CanEditSettlement);
        Assert.False(manager.CanEditBonus);
        var admin = await service.GetAsync(projectId, "admin", UserRole.Administrator, default);
        Assert.Null(admin.BonusRate);
        Assert.Equal(saved.BonusAmount, admin.BonusAmount);
        Assert.False(admin.CanViewSettlement);
        Assert.Null(admin.SettlementAmount);
        Assert.Empty(admin.SettlementLines!);
        var bonus = await service.SaveBonusRateAsync(projectId, new(.01m, 1), "planner", UserRole.PlanningManager, default);
        Assert.Equal(16220, bonus.BonusAmount);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveSettlementAsync(projectId, new([], 1), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveBonusRateAsync(projectId, new(.010001m, 2), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveBonusRateAsync(projectId, new(-.001m, 2), "planner", UserRole.PlanningManager, default));
        await service.SaveAsync(projectId, new(bonus.Rows.Select(row => row.Input with { BudgetAmount = 1000 }).ToArray(), 2), "planner", UserRole.PlanningManager, default);
        var loaded = await store.FindAsync(projectId, default);
        Assert.Equal(2, loaded!.SettlementLines!.Count);
        Assert.Equal(.01m, loaded.BonusRate);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveSettlementAsync(projectId, new([new("无数量", UnitPrice: 10)], 3), "planner", UserRole.PlanningManager, default));
        var cleared = await service.SaveSettlementAsync(projectId, new([], 3), "planner", UserRole.PlanningManager, default);
        Assert.Null(cleared.SettlementAmount);
        Assert.Null(cleared.BonusAmount);
    }

    [Fact]
    public async Task SalesAssistantCanSettleButCannotChangeBonusAndAdministratorsCannotBypassRoles()
    {
        var (service, _, projectId) = await Setup();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveSettlementAsync(projectId, new([], 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveBonusRateAsync(projectId, new(.01m, 0), "admin", UserRole.Administrator, default));
        TenantContext.Set(new(Guid.NewGuid(), Guid.Empty, Guid.Empty, "sales", "SalesAssistant", false,
            new HashSet<string>([PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectBudgetView])));
        try {
            var saved = await service.SaveSettlementAsync(projectId, new([new("销售结算", "套", 2, 1000)], 0), "admin", UserRole.Administrator, default);
            Assert.True(saved.CanEditSettlement);
            Assert.False(saved.CanEditBonus);
            Assert.Equal(2000, saved.SettlementAmount);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveBonusRateAsync(projectId, new(.01m, 1), "admin", UserRole.Administrator, default));
        } finally { TenantContext.Clear(); }
    }

    private static async Task<(ProjectBudgetService, InMemoryProjectBudgetRepository, Guid)> Setup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectBudgetRepository();
        return (new(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System)), store, project.Id);
    }
}
