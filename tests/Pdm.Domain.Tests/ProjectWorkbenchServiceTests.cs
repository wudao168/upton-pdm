using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectWorkbenchServiceTests
{
    private sealed class CountingOplRepository : IProjectOplRepository
    {
        public int Batches { get; private set; }
        public IReadOnlyCollection<Guid> Requested { get; private set; } = [];
        public Task<IReadOnlyDictionary<Guid, int>> CountOpenAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        { Batches++; Requested = ids; return Task.FromResult<IReadOnlyDictionary<Guid, int>>(ids.ToDictionary(id => id, _ => 2)); }
        public Task<IReadOnlyList<OplIssue>> ListAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("Overview must not load full OPL lists");
        public Task<OplIssue> SaveAsync(OplIssue issue, long version, CancellationToken ct) => throw new NotSupportedException();
    }
    [Fact]
    public async Task FirstScreenUsesOneOplCountBatchAndSkipsBusinessDetails()
    {
        var opl = new CountingOplRepository();
        var (service, repository, _, _) = Setup(opl);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var summary = await service.GetAsync(null, "admin", UserRole.Administrator, default, "company", true);
        var row = Assert.Single(summary.Projects);
        Assert.Null(row.Budget); Assert.Null(row.Procurement); Assert.Null(row.Quality);
        Assert.Equal(2, row.OplOpenCount);
        Assert.Equal(1, opl.Batches);
        Assert.Equal([project.Id], opl.Requested);
        Assert.Empty(row.Warnings);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(null, "admin", UserRole.Administrator, default, "company", false, Guid.NewGuid()));
    }

    private sealed class CalendarClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(7, false, true, true)]
    [InlineData(8, false, true, false)]
    [InlineData(-1, false, true, true)]
    [InlineData(-1, true, true, false)]
    [InlineData(0, true, true, true)]
    [InlineData(7, false, false, false)]
    public async Task CalendarSkipsUnneededDetailsButRetainsUpcomingAndOverdueDeliveryWarnings(int days, bool completed, bool deliveryStage, bool needsDetails)
    {
        var opl = new CountingOplRepository();
        var time = new CalendarClock();
        var (service, repository, planning, risks) = Setup(opl, time);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var finish = new DateOnly(2026, 10, 15).AddDays(days);
        var task = new ProjectPlanTask(Guid.NewGuid(), "交付", "Delivery", "engineer", 1, finish, finish, null, null, null, null,
            completed ? 100 : 20, completed ? ProjectPlanTaskStatus.Completed : ProjectPlanTaskStatus.InProgress, [], 1, true, true, 0);
        var plan = new ProjectPlan(Guid.NewGuid(), project.Id, Guid.NewGuid(), "日历计划", "Delivery", null, null,
            finish, finish, finish, 1, [task], "admin", time.GetUtcNow(), "admin", time.GetUtcNow(), 0)
            { ApprovalStatus = ProjectPlanApprovalStatus.Approved, Stages = [new("Delivery", "交付") { ParticipatesInDelivery = deliveryStage }] };
        await planning.SavePlanAsync(plan, null, null, default);
        var risk = new ProjectWorkbenchRisk(Guid.NewGuid(), project.Id, "日历风险", "交期", "admin", finish, "warning", "admin", time.GetUtcNow());
        await risks.SaveAsync(risk, default);
        var calendar = await service.GetAsync(null, "admin", UserRole.Administrator, default, calendarOnly: true);
        var entry = Assert.Single(calendar.Projects);
        Assert.Equal(plan.Tasks, entry.Plan!.Tasks);
        Assert.Contains(calendar.Risks, item => item.Id == risk.Id);
        Assert.Null(entry.Budget);
        Assert.Equal(needsDetails, entry.Procurement is not null);
        Assert.Equal(needsDetails, entry.Quality is not null);
        Assert.Equal(0, opl.Batches);
        if (needsDetails) Assert.DoesNotContain(entry.Warnings, warning => warning.Contains("采购跟踪") || warning.Contains("验收情况"));
    }

    [Fact]
    public async Task CalendarRetainsDeliveryChecksForPropagatedSourcesWhenClientFiltersCanShowThemAgain()
    {
        var time = new CalendarClock();
        var (service, repository, planning, _) = Setup(testClock: time);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var other = await repository.CreateProjectAsync(new("CAL-OTHER", "传播任务", "admin", "vault", "release"), "admin", default);
        var finish = new DateOnly(2026, 12, 1);
        var task = new ProjectPlanTask(Guid.NewGuid(), "交付", "Delivery", "engineer", 1, finish, finish, null, null, null, null, 20, ProjectPlanTaskStatus.InProgress, [], 1, true, true, 0);
        var plan = new ProjectPlan(Guid.NewGuid(), project.Id, Guid.NewGuid(), "计划", "Delivery", null, null, finish, finish, finish, 1, [task], "admin", time.GetUtcNow(), "admin", time.GetUtcNow(), 0)
            { ApprovalStatus = ProjectPlanApprovalStatus.Approved, Stages = [new("Delivery", "交付") { ParticipatesInDelivery = true }] };
        await planning.SavePlanAsync(plan, null, null, default);
        await planning.SavePlanAsync(plan with { Id = Guid.NewGuid(), ProjectId = other.Id, Tasks = [task with { Id = Guid.NewGuid(), SourceTaskId = task.Id }] }, null, null, default);
        var calendar = await service.GetAsync(null, "admin", UserRole.Administrator, default, calendarOnly: true);
        Assert.NotNull(calendar.Projects.Single(item => item.Project.Id == project.Id).Quality);
    }

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
    private static (ProjectWorkbenchService, InMemoryPdmRepository, InMemoryProjectPlanningRepository, InMemoryProjectWorkbenchRiskRepository) Setup(IProjectOplRepository? opl = null, TimeProvider? testClock = null)
    {
        var time = testClock ?? TimeProvider.System; var repository = new InMemoryPdmRepository(time); var planning = new InMemoryProjectPlanningRepository();
        var procurement = new InMemoryU9ProcurementRepository(); var materials = new InMemoryMaterialRepository(time); var risks = new InMemoryProjectWorkbenchRiskRepository();
        var service = new ProjectWorkbenchService(repository, planning, new InMemoryProjectContactInformationRepository(),
            new ProjectBudgetService(repository, new InMemoryProjectBudgetRepository(), procurement, time, materials),
            new U9ProcurementService(procurement, materials, repository, planning, null!, null!, null!, time),
            new InMemoryValidationPlanRepository(), new InMemoryQualityInspectionRepository(), risks, time, opl);
        return (service, repository, planning, risks);
    }
}
