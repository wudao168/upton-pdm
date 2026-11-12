using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Tests;
namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectBonusTests
{
    [Fact]
    public void PresetsAreValidAndHaveDistinctEditablePhaseIndicators()
    {
        var settings = ProjectBonusDefaults.Create();
        ProjectBonusRules.ValidateSettings(settings);
        Assert.Equal(16, settings.Roles.Count); Assert.Equal(80, settings.Roles.Sum(r => r.Indicators.Count));
        Assert.Equal(60, settings.ProjectIndicators[0].Weight); Assert.Equal(40, settings.ProjectIndicators[1].Weight);
        Assert.False(settings.Enabled);
        Assert.Throws<PdmRuleException>(() => ProjectBonusRules.ValidateSettings(settings with { Enabled = true }));
        var invalid = settings with { Roles = [settings.Roles[0] with { Indicators = [settings.Roles[0].Indicators[0]] }] };
        Assert.Throws<PdmRuleException>(() => ProjectBonusRules.ValidateSettings(invalid));
    }

    [Fact]
    public void ParallelDepartmentsJoinOnlyWhenAllPassAndRepeatedActorSignsEachBranch()
    {
        var first = new ApprovalDepartmentBranch(Guid.NewGuid(), "技术", ["same", "backup"], false, []);
        var second = new ApprovalDepartmentBranch(Guid.NewGuid(), "质量", ["same"], false, []);
        var group = new ApprovalGroup(["same", "backup"], true, []) { Departments = [first, second] };
        group = ApprovalNodeRules.Sign(group, "same", ApprovalDecision.Approved, "技术同意", DateTimeOffset.UtcNow, false, out var waiting)!;
        Assert.True(waiting); Assert.True(group.CanSign("same")); Assert.False(group.CanSign("backup")); Assert.True(group.Departments[0].Complete);
        group = ApprovalNodeRules.Sign(group, "same", ApprovalDecision.Approved, "质量同意", DateTimeOffset.UtcNow, false, out waiting)!;
        Assert.False(waiting); Assert.False(group.CanSign("same")); Assert.Equal(2, group.Signatures.Count);
        Assert.Equal("质量同意", group.Departments[1].Signatures.Single().Comment);
    }

    [Fact]
    public async Task FullLifecycleFreezesRulesRetainsDeductionsAndProtectsAmounts()
    {
        var (service, repo, store, budget, project, settings) = await Setup();
        var input = Input(settings, true);
        var view = await service.SaveAsync(project.Id, "Shipment", input, "manager", UserRole.Administrator, default);
        Assert.Equal(10000m, view.Pool); Assert.True(view.Frozen);
        var app = view.Applications.Single().Application;
        Assert.Equal("Scoring", app.Status);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(project.Id, "Shipment", input with { ExpectedRowVersion = view.RowVersion }, "manager", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ScoreAsync(project.Id, "Shipment", new(view.RowVersion, [new(app.ScoringTasks[0].IndicatorId, "", "division", 100, "越权")]), "worker", UserRole.Approver, default));
        await service.SaveSettingsAsync(settings with { Version = 1, ProjectIndicators = settings.ProjectIndicators.Select(i => i with { Name = i.Name + "新规则" }).ToArray() }, "manager", UserRole.Administrator, default);
        var savedBudget = (await budget.FindAsync(project.Id, default))!;
        await budget.SaveAsync(savedBudget with { BonusRate = .01m }, savedBudget.RowVersion, default);
        view = await ScoreAll(service, project, view);
        app = view.Applications.Single().Application;
        Assert.Equal("Scored", app.Status); Assert.Equal(90m, app.ProjectScore);
        Assert.Equal(3600m, app.Awards.Sum(a => a.Amount)); Assert.Equal(1, app.Template!.Version);
        Assert.DoesNotContain("新规则", app.Template.ProjectIndicators[0].Name); Assert.Equal(10000m, view.Pool);
        var own = await service.GetAsync(project.Id, "worker", UserRole.Approver, default);
        Assert.Null(own.Pool); Assert.Null(own.Settings); Assert.Empty(own.Users);
        Assert.Empty(own.Applications[0].Application.Steps); Assert.Null(own.Applications[0].Application.ProjectScore);
        Assert.Equal(3600m, own.Applications[0].Application.Awards.Sum(a => a.Amount));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.HistoryAsync(project.Id, "worker", UserRole.Approver, default));
        view = await service.ActAsync(project.Id, "Shipment", "submit", new(view.RowVersion), "manager", UserRole.Administrator, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ActAsync(project.Id, "Shipment", "approve", new(view.RowVersion, "跳过负责人"), "review-finance", UserRole.Approver, default));
        view = await service.ActAsync(project.Id, "Shipment", "approve", new(view.RowVersion, "事业部同意"), "review-division", UserRole.Approver, default);
        foreach (var key in new[] { "finance", "quality", "technical", "manufacturing" })
        {
            var previous = view.RowVersion;
            view = await service.ActAsync(project.Id, "Shipment", "approve", new(previous, key + "审核完成"), "review-" + key, UserRole.Approver, default);
            if (key == "finance")
            {
                Assert.Null(view.Applications[0].Application.Steps[1].Decision);
                await Assert.ThrowsAsync<PdmConflictException>(() => service.ActAsync(project.Id, "Shipment", "approve", new(previous, "旧版本"), "review-quality", UserRole.Approver, default));
            }
        }
        foreach (var key in new[] { "assistant", "general" }) view = await service.ActAsync(project.Id, "Shipment", "approve", new(view.RowVersion, "同意"), "review-" + key, UserRole.Approver, default);
        Assert.Equal("Approved", view.Applications[0].Application.Status);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.PayAsync(project.Id, "Shipment", new(view.RowVersion, 5000, DateOnly.FromDateTime(DateTime.Today), "错误金额"), "review-finance", UserRole.Approver, default));
        view = await service.PayAsync(project.Id, "Shipment", new(view.RowVersion, 3600, DateOnly.FromDateTime(DateTime.Today), "凭证001"), "review-finance", UserRole.Approver, default);
        Assert.Equal("Paid", view.Applications[0].Application.Status);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.PayAsync(project.Id, "Shipment", new(view.RowVersion, 3600, DateOnly.FromDateTime(DateTime.Today), "重复"), "review-finance", UserRole.Approver, default));
        Assert.DoesNotContain(await repo.ListUnreadUserNotificationsAsync(null, default), n => n.Category == "ProjectBonusPending" && n.ProjectId == project.Id && n.ResolvedAt is null);
        var histories = await store.HistoryAsync(project.Id, default);
        Assert.Contains(histories, h => h.Applications[0].Status == "Scored");
        Assert.All(histories, h => Assert.Equal(10000m, h.FrozenPool));
    }

    [Fact]
    public async Task SharedExpenseEngineResolvesParallelDepartmentsAndWaitsForBoth()
    {
        var (_, repo, _, _, project, settings) = await Setup();
        var flow = ExpenseWorkflowDefaults.Create(false);
        var parallel = flow.Steps[1] with { AssigneeSource = ApprovalAssigneeSource.ParallelDepartments,
            ParallelDepartments = settings.Departments.Where(d => d.Key is "finance" or "quality").Select(d => new ApprovalDepartmentTemplate(d.UnitId!.Value)).ToArray(), FixedUnitId = null };
        var system = await repo.GetSystemSettingsAsync(default);
        await repo.UpdateSystemSettingsAsync(system with { ApprovalWorkflows = system.ApprovalWorkflows with { ExpenseProcurement = flow with { Steps = [flow.Steps[0], parallel] } } }, default);
        var service = new ExpenseRequestService(repo, new InMemoryExpenseRequestRepository(repo), TimeProvider.System);
        var view = await service.SaveAsync(project.Id, Guid.NewGuid(), new("SitePurchase", "Procurement", DateOnly.FromDateTime(DateTime.Today), "并行审核验收", [new("Service", null, "检验服务", null, null, "项", 1, 100, null)], 0, true), "manager", UserRole.Administrator, default);
        Assert.Equal(2, view.Request.CurrentStep!.Participants.Departments.Count);
        view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "财务同意"), "review-finance", UserRole.Approver, default);
        Assert.Equal("Pending", view.Request.Status);
        view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "质量同意"), "review-quality", UserRole.Approver, default);
        Assert.Equal("Approved", view.Request.Status);
        Assert.All(view.Request.Steps[0].Participants.Departments, d => Assert.True(d.Complete));
    }

    [Fact]
    public async Task RejectionClosesParallelTasksAndResubmissionDiscardsOldScores()
    {
        var (s, repo, store, _, p, settings) = await Setup();
        var view = await s.SaveAsync(p.Id, "Shipment", Input(settings, true), "manager", UserRole.Administrator, default);
        view = await ScoreAll(s, p, view);
        view = await s.ActAsync(p.Id, "Shipment", "submit", new(view.RowVersion), "manager", UserRole.Administrator, default);
        view = await s.ActAsync(p.Id, "Shipment", "approve", new(view.RowVersion, "同意"), "review-division", UserRole.Approver, default);
        view = await s.ActAsync(p.Id, "Shipment", "reject", new(view.RowVersion, "预算依据须补充"), "review-finance", UserRole.Approver, default);
        Assert.Equal("Rejected", view.Applications[0].Application.Status);
        Assert.Equal("review-finance", view.Applications[0].Application.Steps[1].Participants.Departments[0].RejectedBy);
        Assert.DoesNotContain(await repo.ListUnreadUserNotificationsAsync(null, default), n => n.Category == "ProjectBonusPending" && n.ResolvedAt is null && n.ProjectId == p.Id);
        view = await s.SaveAsync(p.Id, "Shipment", Input(settings, true) with { ExpectedRowVersion = view.RowVersion }, "manager", UserRole.Administrator, default);
        Assert.Empty(view.Applications[0].Application.Scores); Assert.Empty(view.Applications[0].Application.Awards);
        Assert.All(view.Applications[0].Application.Steps, step => Assert.Empty(step.Participants.Signatures));
        Assert.Contains(await store.HistoryAsync(p.Id, default), h => h.Applications[0].Status == "Rejected");
    }

    [Fact]
    public async Task ConcurrentFirstSavesDoNotCreateDuplicateStageAndMissingEvidenceIsBlocked()
    {
        var (s, _, _, _, p, settings) = await Setup();
        await Assert.ThrowsAsync<PdmRuleException>(() => s.SaveAsync(p.Id, "Acceptance", Input(settings, true), "manager", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => s.SaveAsync(p.Id, "Shipment", Input(settings, true) with { Evidence = [] }, "manager", UserRole.Administrator, default));
        var view = await s.SaveAsync(p.Id, "Shipment", Input(settings, false), "manager", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => s.SaveAsync(p.Id, "Shipment", Input(settings, false), "manager", UserRole.Administrator, default));
        Assert.Single(view.Applications); Assert.False(view.Frozen);
    }

    private static async Task<ProjectBonusView> ScoreAll(ProjectBonusService service, Project project, ProjectBonusView view)
    {
        foreach (var group in view.Applications[0].Application.ScoringTasks.GroupBy(t => t.Members[0]))
        {
            await service.ScoreAsync(project.Id, "Shipment", new(view.RowVersion, group.Select(t => new BonusScore(t.IndicatorId, t.PersonId, t.DepartmentKey, t.PersonId == "" ? 90 : 80, "核实证据后评分")).ToArray()), group.Key, UserRole.Approver, default);
            view = await service.GetAsync(project.Id, "manager", UserRole.Administrator, default);
        }
        return view;
    }
    private static SaveBonusDraft Input(ProjectBonusSettings settings, bool submit)
    {
        var role = settings.Roles.Single(r => r.Id == "mechanical-Shipment");
        return new(0, [new("person", "worker", role.Id, role.SuggestedWeight, "负责机械设计与问题闭环")],
            settings.ProjectIndicators.Select(i => new BonusEvidence(i.Id, "", "经批准的基线和成本对账记录"))
                .Concat(role.Indicators.Select(i => new BonusEvidence(i.Id, "person", "评审与交付证据001"))).ToArray(), "发货凭证001，现场验收记录", submit);
    }
    private static async Task<(ProjectBonusService, InMemoryPdmRepository, InMemoryProjectBonusRepository, InMemoryProjectBudgetRepository, Project, ProjectBonusSettings)> Setup()
    {
        var repo = new InMemoryPdmRepository(TimeProvider.System);
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First(o => o.IsActive).Id;
        foreach (var name in new[] { "manager", "worker" }.Concat(ProjectBonusDefaults.Create().Departments.Select(d => "review-" + d.Key)))
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "manager" ? UserRole.Administrator : UserRole.Approver, true, CompanyId: company), default);
        await FinancialTestActors.GrantAsync(repo, "Administrator", new(PermissionCodes.ProjectBonusView, ["company"]),
            new(PermissionCodes.ProjectBonusManage, ["project-manager"]), new(PermissionCodes.ProjectBonusHistoryView, ["project-manager"]),
            new(PermissionCodes.ProjectExpenseView, ["company"]), new(PermissionCodes.ProjectExpenseSubmit, ["project-manager"]));
        await FinancialTestActors.GrantAsync(repo, "Approver", new(PermissionCodes.ProjectBonusView, ["company"]),
            new(PermissionCodes.ProjectBonusScore, ["company"]), new(PermissionCodes.ProjectBonusApprove, ["company"]), new(PermissionCodes.ProjectBonusPay, ["company"]),
            new(PermissionCodes.ProjectExpenseView, ["company"]), new(PermissionCodes.ProjectExpenseApprove, ["company"]));
        var customer = (await repo.ListCustomersAsync(true, default)).First();
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "奖金验收", null, null, 1, "manager", @"C:\qa-vault", @"C:\qa-release"), default);
        project = await repo.SetMainProjectStaffingAsync(project.Id, new("manager", [], []), "manager", default);
        var defaults = ProjectBonusDefaults.Create(); var departments = new List<BonusDepartment>();
        foreach (var d in defaults.Departments)
        {
            var unit = await repo.SaveOrganizationUnitAsync(new(null, company, null, "BONUS-" + d.Key, d.Name, OrganizationUnitKind.Department, true, 0), default);
            await repo.SetOrganizationMembershipsAsync("review-" + d.Key, [unit.Id], unit.Id, default);
            departments.Add(d with { UnitId = unit.Id, Reviewers = ["review-" + d.Key] });
        }
        var store = new InMemoryProjectBonusRepository(repo); var budget = new InMemoryProjectBudgetRepository();
        var service = new ProjectBonusService(repo, store, budget, TimeProvider.System);
        var settings = defaults with { Departments = departments, Enabled = true };
        await service.SaveSettingsAsync(settings, "manager", UserRole.Administrator, default);
        await budget.SaveAsync(new(project.Id, [], SettlementLines: [new("整线", "套", 1, 2000000)]), 0, default);
        return (service, repo, store, budget, project, settings);
    }
}
