using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectFinancialAccessTests
{
    private static ProjectBudgetView SensitiveView(Guid first, Guid second)
    {
        var rows = new[] {
            ProjectBudgetService.Calculate(new("Standard", 100, 50, 10, Note: "敏感说明", DepartmentId: first), 60),
            ProjectBudgetService.Calculate(new("Equipment", 900, 500, 20, DepartmentId: second), 550),
            ProjectBudgetService.Calculate(new("Other", 700, 400, 0))
        };
        return new(Guid.NewGuid(), rows, 1700, 950, 990, 0, true, true, 1, "admin", null,
            [new("PO1", "PO1", 1, "M1", "本部门", 60, "Standard"), new("PO2", "PO2", 1, "M2", "其他部门", 550, "Equipment")],
            true, true, [new(Guid.NewGuid(), "Standard", "其他金额可能写入备注", "admin", null)]);
    }

    [Fact]
    public void DepartmentReaderOnlyReceivesExplicitlyOwnedRowsAndRecomputedTotals()
    {
        var own = Guid.NewGuid();
        var result = new ProjectFinancialAccess(false, false, new HashSet<Guid> { own }).Filter(SensitiveView(own, Guid.NewGuid()));
        Assert.Single(result.Rows);
        Assert.Equal(100, result.BudgetAmount);
        Assert.Equal(50, result.ActualAmount);
        Assert.Equal("PO1", Assert.Single(result.Orders).Key);
        Assert.Empty(result.Notes!);
        Assert.False(result.CanEdit);
        Assert.Null(result.OverrunAmount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SeparateBudgetAndActualRightsRedactInputsAndDerivedValues(bool budget, bool actual)
    {
        var result = new ProjectFinancialAccess(budget, actual, new HashSet<Guid>()).Filter(SensitiveView(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(budget, result.BudgetAmount.HasValue);
        Assert.Equal(actual, result.ActualAmount.HasValue);
        Assert.All(result.Rows, row => {
            Assert.Equal(budget, row.Input.BudgetAmount.HasValue);
            Assert.Equal(actual, row.Input.ActualAmount.HasValue);
            Assert.Null(row.Input.Note);
            Assert.Null(row.Variance);
            Assert.Equal("Incomplete", row.Alert);
        });
        if (!actual) Assert.Empty(result.Orders);
        Assert.Empty(result.Notes!);
        Assert.Null(result.OverrunAmount);
    }

    [Fact]
    public void NoRightsReturnsNoRowsAmountsOrOrderHistory()
    {
        var result = new ProjectFinancialAccess(false, false, new HashSet<Guid>()).Filter(SensitiveView(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Empty(result.Rows);
        Assert.Empty(result.Orders);
        Assert.Null(result.BudgetAmount);
        Assert.Null(result.ActualAmount);
        Assert.Null(result.EstimatedAmount);
    }

    [Fact]
    public async Task AdministratorDoesNotBypassProjectFinancialPositions()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "admin", UserRole.Administrator, default);
        Assert.False(access.Budget);
        Assert.False(access.Actual);
        project = await repository.SetMainProjectStaffingAsync(project.Id, new("admin", [], ["engineer"]), "admin", default);
        access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "admin", UserRole.Administrator, default);
        Assert.True(access.Budget);
        Assert.True(access.Actual);
        Assert.False(access.CanMaintainBudget);
        Assert.True(access.CanMaintainActual);
        project = await repository.SetMainProjectStaffingAsync(project.Id, new("engineer", [], ["admin"]), "admin", default);
        access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "admin", UserRole.Administrator, default);
        Assert.True(access.Budget);
        Assert.True(access.Actual);
        Assert.True(access.CanMaintainBudget);
        Assert.False(access.CanMaintainActual);
    }

    [Fact]
    public async Task DesignLeadCanReadActualCostsWithoutGainingActualMaintenance()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "lead", "主设", "unused", UserRole.Administrator, true), default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetMainProjectStaffingAsync(project.Id, new("manager", [], ["lead"]), "admin", default);
        var store = new InMemoryProjectBudgetRepository();
        await store.SaveAsync(new(project.Id, ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, 100, 50, 10)).ToArray(), ManualActualAmounts: true), 0, default);
        var service = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        var view = await service.GetAsync(project.Id, "lead", UserRole.Administrator, default);
        Assert.NotNull(view.ActualAmount);
        Assert.All(view.Rows, row => Assert.Equal(50, row.ActualAmount));
        Assert.True(view.CanEditBudget);
        Assert.False(view.CanEditActual);
        await service.SaveAsync(project.Id, new(view.Rows.Select(row => row.Input with { BudgetAmount = 200 }).ToArray(), 1), "lead", UserRole.Administrator, default);
        var saved = (await store.FindAsync(project.Id, default))!;
        Assert.All(saved.Lines, line => { Assert.Equal(200, line.BudgetAmount); Assert.Equal(50, line.ActualAmount); Assert.Equal(10, line.RemainingAmount); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id,
            new(view.Rows.Select(row => row.Input with { ActualAmount = 999 }).ToArray(), 2), "lead", UserRole.Administrator, default));
    }

    [Fact]
    public async Task RealDivisionPositionAndDepartmentMembershipAreScopedToTheProjectsCompany()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First();
        var project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "财务隔离项目", null, null, 1, "admin", @"C:\qa-vault", @"C:\qa-release"), default);
        var division = await repository.SaveOrganizationUnitAsync(new(null, company, null, "QA-DIV", "测试事业部", OrganizationUnitKind.BusinessDivision, true, 0), default);
        var department = await repository.SaveOrganizationUnitAsync(new(null, company, division.Id, "QA-BUY", "采购部", OrganizationUnitKind.Department, true, 0), default);
        var team = await repository.SaveOrganizationUnitAsync(new(null, company, department.Id, "QA-TEAM", "采购组", OrganizationUnitKind.Team, true, 0), default);
        var sibling = await repository.SaveOrganizationUnitAsync(new(null, company, division.Id, "QA-OTHER", "其他部门", OrganizationUnitKind.Department, true, 0), default);
        foreach (var name in new[] { "leader", "buyer", "outsider" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Administrator, true, RoleCode: name == "buyer" ? "ProcurementSpecialist" : "Administrator", CompanyId: company), default);
        await repository.SetOrganizationMembershipsAsync("buyer", [team.Id], team.Id, default);
        await repository.SetOrganizationUnitManagersAsync(division.Id, "leader", [], default);
        project = await repository.SetProjectExecutionUnitAsync(project.Id, department.Id, "admin", default);
        var leader = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "leader", UserRole.Administrator, default);
        Assert.True(leader.Budget); Assert.True(leader.Actual);
        var buyer = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "buyer", UserRole.Administrator, default);
        Assert.False(buyer.Budget); Assert.False(buyer.Actual);
        Assert.Equal([department.Id], buyer.Departments);
        Assert.False(buyer.BudgetFor(sibling.Id)); Assert.False(buyer.ActualFor(null));
        var outsider = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "outsider", UserRole.Administrator, default);
        Assert.False(outsider.Budget); Assert.False(outsider.Actual); Assert.Empty(outsider.Departments);
    }

    [Fact]
    public async Task RootManagerReadsChildBudgetAssessmentAndActualWithoutGainingMaintenance()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "manager", "项目经理", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var customer = (await repository.ListCustomersAsync(true, default)).First();
        var root = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "项目经理财务查看", null, null, 1, "admin", @"C:\qa-vault", @"C:\qa-release"), default);
        await repository.SetMainProjectStaffingAsync(root.Id, new("manager", [], ["lead"]), "admin", default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "所属子项目", null, 1), default);
        var access = await ProjectFinancialAccessPolicy.ReadAsync(repository, child, "manager", UserRole.Administrator, default);
        Assert.True(access.Budget); Assert.True(access.Actual);
        Assert.False(access.CanMaintainBudget);
        var visible = access.Filter(SensitiveView(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(1700, visible.BudgetAmount); Assert.Equal(950, visible.ActualAmount);
        Assert.Equal(3, visible.Rows.Count); Assert.NotNull(visible.OverrunAmount);
        var budgets = new InMemoryProjectBudgetRepository();
        await budgets.SaveAsync(new(child.Id, ProjectBudgetCategories.All.Select(category => new ProjectBudgetLine(category, 100, 50)).ToArray(),
            AssessmentGroups: [new(Guid.NewGuid(), "机械评估", "Standard", null, [new(Guid.NewGuid(), "标准件", Quantity: 2, UnitPrice: 300)])]), 0, default);
        var budgetService = new ProjectBudgetService(repository, budgets, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        var evaluation = await new BudgetAssessmentService(repository, budgets, budgetService, TimeProvider.System).GetAsync(child.Id, "manager", UserRole.Administrator, default);
        var sheet = Assert.Single(evaluation.Sheets);
        Assert.Equal(600, sheet.Total); Assert.False(sheet.CanEdit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutiveHasActualCostOnlyInsideOwnCompany(bool sameCompany)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First();
        var project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "高管隔离项目", null, null, 1, "admin", @"C:\qa-vault", @"C:\qa-release"), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "executive", "高管", "unused", UserRole.Administrator, true,
            RoleCode: "CompanyExecutive", CompanyId: sameCompany ? company : Guid.NewGuid()), default);
        var access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, "executive", UserRole.Administrator, default);
        Assert.False(access.Budget);
        Assert.Equal(sameCompany, access.Actual);
        Assert.Empty(access.Departments);
    }
}
