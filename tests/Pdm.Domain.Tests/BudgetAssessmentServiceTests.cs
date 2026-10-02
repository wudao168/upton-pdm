using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
namespace Upton.Pdm.Tests;
public sealed class BudgetAssessmentServiceTests
{
    [Fact]
    public async Task AssessmentsAggregateByCategoryAcrossProjectsAndDoNotOverwriteBudget()
    {
        var (service, repository, store, root) = await Setup();
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        await service.SaveLaborRatesAsync(root.Id, new(new Dictionary<string, decimal?> { ["MechanicalDesign"] = 100 }, 0), "planner", UserRole.PlanningManager, default);
        var rootGroups = new[] { Group("Standard", null, 2, 125), Group("Labor", "MechanicalDesign", 3, 100) };
        await service.SaveAsync(root.Id, new(rootGroups, 0), "engineer", UserRole.Engineer, default);
        await service.SaveAsync(child.Id, new([Group("Standard", null, 1, 50), Group("Equipment", null, 2, 400)], 0), "engineer", UserRole.Engineer, default);
        var main = await service.GetAsync(root.Id, "planner", UserRole.PlanningManager, default);
        Assert.Equal(2, main.Sheets.Count);
        Assert.Equal(300, main.Amounts["Standard"]);
        Assert.Equal(300, main.Amounts["MechanicalDesign"]);
        Assert.Equal(800, main.Amounts["Equipment"]);
        Assert.False(main.Sheets[0].CanEdit);
        var sub = await service.GetAsync(child.Id, "engineer", UserRole.Engineer, default);
        Assert.Single(sub.Sheets);
        Assert.Equal(50, sub.Amounts["Standard"]);
        var budget = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        var view = await budget.GetAsync(root.Id, "planner", UserRole.PlanningManager, default);
        Assert.All(view.Rows, row => Assert.Null(row.BudgetAmount));
        await budget.SaveAsync(root.Id, new(view.Rows.Select(row => row.Input with { BudgetAmount = 1000 }).ToArray(), 1), "planner", UserRole.PlanningManager, default);
        Assert.Equal(2, (await store.FindAsync(root.Id, default))!.AssessmentGroups!.Count);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(root.Id, new(rootGroups, 1), "engineer", UserRole.Engineer, default));
    }
    [Fact]
    public async Task AssessmentNotesAreSeparatePerProjectAndSurviveAssessmentEdits()
    {
        var (service, repository, store, root) = await Setup();
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        var budget = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        await budget.AddNoteAsync(root.Id, new("Assessment", "主项目评估备注", 0), "planner", UserRole.PlanningManager, default);
        await budget.AddNoteAsync(child.Id, new("Assessment", "子项目评估备注", 0), "planner", UserRole.PlanningManager, default);
        await budget.AddNoteAsync(root.Id, new("Standard", "标准件备注", 1), "planner", UserRole.PlanningManager, default);
        var view = await service.GetAsync(root.Id, "planner", UserRole.PlanningManager, default);
        Assert.Equal("主项目评估备注", Assert.Single(view.Sheets.Single(sheet => sheet.ProjectId == root.Id).Notes!).Content);
        Assert.Equal("子项目评估备注", Assert.Single(view.Sheets.Single(sheet => sheet.ProjectId == child.Id).Notes!).Content);
        var saved = await service.SaveAsync(root.Id, new([Group("Standard", null, 2, 100)], 2), "engineer", UserRole.Engineer, default);
        Assert.Equal("主项目评估备注", Assert.Single(saved.Sheets.Single(sheet => sheet.ProjectId == root.Id).Notes!).Content);
        Assert.Equal(200, saved.Amounts["Standard"]);
    }

    [Fact]
    public async Task OnlyDesignLeadCanWriteAndUnsupportedCategoriesAreRejected()
    {
        var (service, _, _, root) = await Setup();
        var groups = new[] { Group("Standard", null, 1, 100) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(root.Id, new(groups, 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new([groups[0] with { Category = "RiskReserve" }], 0), "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new([groups[0] with { Category = "Labor", LaborCategory = null }], 0), "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new([groups[0] with { Items = [new(Guid.NewGuid(), "错误", Quantity: -1, UnitPrice: 1)] }], 0), "engineer", UserRole.Engineer, default));
    }
    [Fact]
    public void TotalsRoundPerLineAndMissingInputsAreNotInvented()
    {
        Assert.Equal(3.34m, BudgetAssessmentService.ItemTotal(new(Guid.NewGuid(), "工时", Quantity: 1, UnitPrice: 3.335m)));
        Assert.Null(BudgetAssessmentService.ItemTotal(new(Guid.NewGuid(), "未填")));
        Assert.Equal(0, BudgetAssessmentService.ItemTotal(new(Guid.NewGuid(), "零", Quantity: 0, UnitPrice: 5)));
    }
    [Fact]
    public async Task LaborTypesArePerLineAndRatesAreProtectedAndSharedWithChildren()
    {
        var (service, repository, store, root) = await Setup();
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        var rates = new Dictionary<string, decimal?> { ["MechanicalDesign"] = 100, ["ElectricalDesign"] = 200 };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveLaborRatesAsync(root.Id, new(rates, 0), "engineer", UserRole.Engineer, default));
        await service.SaveLaborRatesAsync(child.Id, new(rates, 0), "admin", UserRole.Administrator, default);
        var group = new BudgetAssessmentGroup(Guid.NewGuid(), "人工", "Labor", null, [
            new(Guid.NewGuid(), "机械", Quantity: 3, UnitPrice: 9999, LaborCategory: "MechanicalDesign"),
            new(Guid.NewGuid(), "电气", Quantity: 2, UnitPrice: 1, LaborCategory: "ElectricalDesign")]);
        var saved = await service.SaveAsync(child.Id, new([group], 0), "engineer", UserRole.Engineer, default);
        Assert.Equal(300, saved.Amounts["MechanicalDesign"]);
        Assert.Equal(400, saved.Amounts["ElectricalDesign"]);
        Assert.Equal(100, saved.Sheets[0].Groups[0].Items[0].UnitPrice);
        var main = await service.GetAsync(root.Id, "planner", UserRole.PlanningManager, default);
        Assert.Equal(700, main.Sheets.Single(sheet => sheet.ProjectId == child.Id).Total);
        await service.SaveLaborRatesAsync(root.Id, new(new Dictionary<string, decimal?> { ["MechanicalDesign"] = 150, ["ElectricalDesign"] = 200 }, 1), "planner", UserRole.PlanningManager, default);
        Assert.Equal(450, (await service.GetAsync(child.Id, "engineer", UserRole.Engineer, default)).Amounts["MechanicalDesign"]);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveLaborRatesAsync(root.Id, new(rates, 1), "admin", UserRole.Administrator, default));
        Assert.NotNull(await store.FindLaborRatesAsync(default));
        var other = await repository.CreateProjectAsync(new("OTHER-RATES", "另一主项目", "admin", @"D:\Vault", @"D:\Release"), "admin", default);
        Assert.Equal(150, (await service.GetAsync(other.Id, "admin", UserRole.Administrator, default)).LaborRates["MechanicalDesign"]);
    }
    [Fact]
    public async Task LegacyGroupTypeAndAmountArePreservedUntilRatesAreConfigured()
    {
        var (service, _, store, root) = await Setup();
        await store.SaveAsync(new(root.Id, [], AssessmentGroups: [Group("Labor", "MechanicalDesign", 3, 500)]), 0, default);
        var directory = await service.GetAsync(root.Id, "engineer", UserRole.Engineer, default);
        Assert.Equal(1500, directory.Amounts["MechanicalDesign"]);
        Assert.Equal("MechanicalDesign", directory.Sheets[0].Groups[0].Items[0].LaborCategory);
        Assert.Null(directory.Sheets[0].Groups[0].LaborCategory);
        await service.SaveAsync(root.Id, new(directory.Sheets[0].Groups, 1), "engineer", UserRole.Engineer, default);
        Assert.Equal(1500, (await service.GetAsync(root.Id, "engineer", UserRole.Engineer, default)).Amounts["MechanicalDesign"]);
    }
    [Fact]
    public async Task FullyBlankRowsDoNotBlockSavingButPartlyEnteredRowsStillRequireAName()
    {
        var (service, _, _, root) = await Setup();
        var blank = new BudgetAssessmentItem(Guid.NewGuid(), "  ", Model: " ");
        var material = Group("Standard", null, 2, 100) with { Items = [new(Guid.NewGuid(), "物料", Quantity: 2, UnitPrice: 100), blank] };
        var labor = new BudgetAssessmentGroup(Guid.NewGuid(), "人工", "Labor", null, [blank]);
        var saved = await service.SaveAsync(root.Id, new([material, labor], 0), "engineer", UserRole.Engineer, default);
        Assert.Single(saved.Sheets[0].Groups[0].Items);
        Assert.Empty(saved.Sheets[0].Groups[1].Items);
        Assert.Equal(200, saved.Amounts["Standard"]);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new([material with { Items = [blank with { Quantity = 1 }] }], 1), "engineer", UserRole.Engineer, default));
    }
    [Fact]
    public async Task LaborNamesAreOptionalButMaterialNamesAreRequired()
    {
        var (service, _, _, root) = await Setup();
        await service.SaveLaborRatesAsync(root.Id, new(new Dictionary<string, decimal?> { ["MechanicalDesign"] = 1500 }, 0), "planner", UserRole.PlanningManager, default);
        var labor = new BudgetAssessmentGroup(Guid.NewGuid(), "人工", "Labor", null,
            [new(Guid.NewGuid(), "", Quantity: 11, LaborCategory: "MechanicalDesign")]);
        var result = await service.SaveAsync(root.Id, new([labor], 0), "engineer", UserRole.Engineer, default);
        Assert.Equal(16500, result.Amounts["MechanicalDesign"]);
        Assert.Equal("", result.Sheets[0].Groups[0].Items[0].Name);
        var material = labor with { Category = "Standard" };
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new([material], 1), "engineer", UserRole.Engineer, default));
    }
    private static BudgetAssessmentGroup Group(string category, string? labor, decimal quantity, decimal price) => new(Guid.NewGuid(), "分组", category, labor, [new(Guid.NewGuid(), "费用", Quantity: quantity, UnitPrice: price)]);
    private static async Task<(BudgetAssessmentService, InMemoryPdmRepository, InMemoryProjectBudgetRepository, Project)> Setup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var organization = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First();
        var customer = (await repository.ListCustomersAsync(false, default)).First();
        var root = await repository.CreateNumberedProjectAsync(new(organization.Id, "P", 1, customer.Id, "评估", null, new DateOnly(2026, 10, 1), 1, "engineer", @"D:\Vault", @"D:\Release"), default);
        root = await repository.SetMainProjectStaffingAsync(root.Id, new("admin", [], ["engineer"]), "admin", default);
        var store = new InMemoryProjectBudgetRepository();
        var budget = new ProjectBudgetService(repository, store, new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        return (new(repository, store, budget, TimeProvider.System), repository, store, root);
    }
}
