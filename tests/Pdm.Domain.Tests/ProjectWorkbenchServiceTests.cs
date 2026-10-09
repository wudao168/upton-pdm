using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectWorkbenchServiceTests
{
    [Theory]
    [InlineData("BusinessUnitManager", "division")]
    [InlineData("ProjectManager", "manager")]
    [InlineData("Engineer", "engineer")]
    [InlineData("ProcurementSpecialist", "procurement")]
    [InlineData("ProductionManager", "production")]
    [InlineData("SalesManager", "sales")]
    [InlineData("QualityInspector", "quality")]
    public void RolesReceiveOnlyTheirViews(string role, string expected)
        => Assert.Equal(expected, Assert.Single(WorkbenchScope.Available([role], false, false, false, false)).Key);

    [Fact]
    public void MultipleRolesAndActualAssignmentsAreAdditive()
    {
        Assert.Equal(["manager", "engineer", "sales"], WorkbenchScope.Available(["Engineer", "SalesManager"], false, false, true, false).Select(view => view.Key));
        Assert.Equal(7, WorkbenchScope.Available([], true, false, false, false).Count);
        Assert.Empty(WorkbenchScope.Available(["Finance"], false, false, false, false));
    }

    [Fact]
    public void DivisionScopeIncludesManagedDescendantsButNotOtherDivisions()
    {
        var company = Guid.NewGuid(); var division = Guid.NewGuid(); var team = Guid.NewGuid(); var other = Guid.NewGuid();
        var directory = new OrganizationDirectory([], [new(division, company, null, "A", "A", OrganizationUnitKind.BusinessDivision, true, 0),
            new(team, company, division, "T", "T", OrganizationUnitKind.Team, true, 0)], [], [new(division, "boss", [])], []);
        var managed = WorkbenchScope.ManagedUnits(directory, "BOSS");
        var project = Project() with { ExecutionUnitId = team };
        Assert.True(WorkbenchScope.Matches("division", project, [project], "boss", false, managed, new Dictionary<Guid, ProjectPlan>(), null));
        Assert.False(WorkbenchScope.Matches("division", project with { ExecutionUnitId = other }, [project], "boss", false, managed, new Dictionary<Guid, ProjectPlan>(), null));
    }

    [Fact]
    public void ManagerAndSalesScopeUseAssignmentsRatherThanBroadReadPermission()
    {
        var project = Project() with { PrimaryProjectManager = "pm", CollaborativeProjectManagers = ["co"] };
        var plans = new Dictionary<Guid, ProjectPlan>(); var units = new HashSet<Guid>();
        Assert.True(WorkbenchScope.Matches("manager", project, [project], "CO", false, units, plans, null));
        Assert.False(WorkbenchScope.Matches("manager", project, [project], "reader", false, units, plans, null));
        Assert.True(WorkbenchScope.Matches("sales", project, [project], "seller", false, units, plans, new(project.Id, SalesPeople: "other、Seller")));
        Assert.False(WorkbenchScope.Matches("sales", project, [project], "reader", false, units, plans, new(project.Id, SalesPeople: "seller")));
    }

    [Fact]
    public async Task ServiceRejectsUnassignedRoleAndHidesUnauthorizedProjectAndRisk()
    {
        var (service, repository, _, risks) = Setup();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await risks.SaveAsync(new(Guid.NewGuid(), project.Id, "内部风险", "影响", "admin", new(2026, 10, 10), "warning", "admin", DateTimeOffset.UtcNow), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync("manager", "engineer", UserRole.Engineer, default));
        var view = await service.GetAsync("engineer", "unknown", UserRole.Engineer, default);
        Assert.Empty(view.Projects);
        Assert.Empty(view.Risks);
    }

    [Fact]
    public async Task WorkbenchReadsEffectiveSnapshotAndUsesCanonicalProgress()
    {
        var (service, repository, planning, _) = Setup();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var task = new ProjectPlanTask(Guid.NewGuid(), "设计", "Design", "engineer", 2, new(2026, 10, 1), new(2026, 10, 2), null, null, null, null, 35, ProjectPlanTaskStatus.InProgress, [], 1, false, true, 0);
        var original = new ProjectPlan(Guid.NewGuid(), project.Id, Guid.NewGuid(), "计划", "Design", null, null,
            task.PlannedStart, task.PlannedFinish, task.PlannedFinish, 1, [task], "admin", DateTimeOffset.UtcNow, "admin", DateTimeOffset.UtcNow, 0) { ApprovalStatus = ProjectPlanApprovalStatus.Approved };
        await planning.SavePlanAsync(original with { Tasks = [task with { CompletionPercent = 90 }], ChangeDraftSource = original }, null, null, default);
        var view = await service.GetAsync("engineer", "admin", UserRole.Administrator, default);
        var entry = Assert.Single(view.Projects);
        Assert.Equal(35, entry.CompletionPercent);
        Assert.Equal(35, entry.Plan!.Tasks[0].CompletionPercent);
        Assert.Null(entry.Budget);
        Assert.Null(entry.Procurement);
    }

    [Fact]
    public async Task RiskOwnerCanCloseWithoutBeingProjectManagerButOutsiderCannot()
    {
        var (service, repository, _, risks) = Setup();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var risk = new ProjectWorkbenchRisk(Guid.NewGuid(), project.Id, "风险", "影响", "engineer", new(2026, 10, 10), "warning", "admin", DateTimeOffset.UtcNow);
        await risks.SaveAsync(risk, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CloseRiskAsync(risk.Id, "unknown", UserRole.Engineer, default));
        await service.CloseRiskAsync(risk.Id, "engineer", UserRole.Engineer, default);
        Assert.True((await risks.FindAsync(risk.Id, default))!.Closed);
        Assert.Equal("engineer", (await risks.FindAsync(risk.Id, default))!.ClosedBy);
    }

    [Fact]
    public async Task RiskRegistrationRejectsMissingDeadlineAndInvalidOwner()
    {
        var (service, repository, _, _) = Setup();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RegisterRiskAsync(project.Id, new("风险", "影响", "admin", default, "warning"), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RegisterRiskAsync(project.Id, new("风险", "影响", "nonexistent", new(2026, 10, 10), "warning"), "admin", UserRole.Administrator, default));
    }

    private static Project Project() => new(Guid.NewGuid(), "P1", "项目", "pm", "vault", "release", true);
    private static (ProjectWorkbenchService, InMemoryPdmRepository, InMemoryProjectPlanningRepository, InMemoryProjectWorkbenchRiskRepository) Setup()
    {
        var time = TimeProvider.System; var repository = new InMemoryPdmRepository(time); var planning = new InMemoryProjectPlanningRepository();
        var procurement = new InMemoryU9ProcurementRepository(); var materials = new InMemoryMaterialRepository(time); var risks = new InMemoryProjectWorkbenchRiskRepository();
        var service = new ProjectWorkbenchService(repository, planning, new InMemoryProjectContactInformationRepository(),
            new ProjectBudgetService(repository, new InMemoryProjectBudgetRepository(), procurement, time, materials),
            new U9ProcurementService(procurement, materials, repository, planning, null!, null!, null!, time),
            new InMemoryValidationPlanRepository(), new InMemoryQualityInspectionRepository(), risks, time);
        return (service, repository, planning, risks);
    }
}
