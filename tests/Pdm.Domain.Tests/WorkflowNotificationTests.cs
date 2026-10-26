using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class WorkflowNotificationTests
{
    [Fact]
    public async Task ReleaseAssistantInSiblingGroupReceivesQueuedNoticeWithinTheSubmitterDivisionOnly()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var mechanical = await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, f.First, "NOTICE-MECH", "机械组", OrganizationUnitKind.Department, true, 0), default);
        var assistants = await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, f.First, "NOTICE-ASSIST", "技术助理组", OrganizationUnitKind.Department, true, 1), default);
        await f.Repository.SetOrganizationMembershipsAsync("submitter", [mechanical.Id], mechanical.Id, default);
        await f.Repository.SetOrganizationMembershipsAsync("assistant-a", [f.Supply], f.Supply, default);
        await f.Rule("standard-release", true, [new("SubmitterDepartment", null, RoleCode: "TechnicalAssistant")]);
        var package = await f.Release(ReleaseScope.StandardSupplement, "standard-release");
        await f.Service.CaptureReleaseAsync(package, default);
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        var job = await f.Job("standard-release");
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal("Attention", (await f.Store.FindJobAsync(job.Id, default))!.State);
        await f.Repository.SetOrganizationMembershipsAsync("assistant-a", [assistants.Id], assistants.Id, default);
        await f.Service.ProcessAsync(job.Id, default);
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("assistant-a"));
        Assert.Equal(0, await f.Notices("assistant-b"));
        Assert.Equal("Sent", Assert.Single(await f.Service.ReleaseRecordsAsync(package, default)).State);
    }

    private sealed class Fixture : IDisposable
    {
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public InMemoryWorkflowNotificationStore Store { get; } = new();
        public InMemoryMaterialRepository Materials { get; }
        public InMemoryValidationPlanRepository Plans { get; } = new();
        public WorkflowNotificationService Service { get; }
        public Guid Company, First, Second, Supply, Position = Guid.NewGuid(), OtherPosition = Guid.NewGuid();
        public Project Project = null!;
        public Fixture() { Materials = new(TimeProvider.System, Repository); Service = new(Repository, Store, Materials, Plans, TimeProvider.System); }
        public static async Task<Fixture> Create()
        {
            TenantContext.Clear(); var f = new Fixture();
            f.Company = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
            foreach (var name in new[] { "submitter", "assistant-a", "assistant-b", "supply", "engineer-a", "engineer-b" })
                await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, "示例" + name, "unused", UserRole.Administrator, true, RoleCode: "developer", CompanyId: f.Company, RoleCodes: name.StartsWith("assistant-") ? ["developer", "TechnicalAssistant"] : name == "supply" ? ["developer", "SupplyChain"] : ["developer"]), default);
            var customer = (await f.Repository.ListCustomersAsync(false, default)).First();
            var equipment = (await f.Repository.ListEquipmentTypesAsync(false, default)).First();
            f.Project = await f.Repository.CreateNumberedProjectAsync(new(f.Company, "P", equipment.Code, customer.Id, "通知隔离项目", null, DateOnly.FromDateTime(DateTime.UtcNow), 1, "submitter", Path.Combine(Path.GetTempPath(), "workflow-notice-vault"), Path.Combine(Path.GetTempPath(), "workflow-notice-release")), default);
            f.First = (await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, null, "NOTICE-T1", "T1事业部", OrganizationUnitKind.BusinessDivision, true, 0), default)).Id;
            f.Second = (await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, null, "NOTICE-T2", "T2事业部", OrganizationUnitKind.BusinessDivision, true, 1), default)).Id;
            f.Supply = (await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, null, "NOTICE-SC", "供应链", OrganizationUnitKind.BusinessDivision, true, 2), default)).Id;
            await f.Repository.SetOrganizationMembershipsAsync("submitter", [f.First], f.First, default);
            await f.Repository.SetOrganizationMembershipsAsync("assistant-a", [f.First, f.Supply], f.First, default);
            await f.Repository.SetOrganizationMembershipsAsync("assistant-b", [f.Second], f.Second, default);
            await f.Repository.SetOrganizationMembershipsAsync("supply", [f.Supply], f.Supply, default);
            f.SetCompany(f.Company);
            await f.Store.SaveAsync(new(f.Company, 0, [new(f.Position, "技术助理"), new(f.OtherPosition, "供应链主管")],
                [new(f.First, f.Position, ["assistant-a"]), new(f.Second, f.Position, ["assistant-b"]), new(f.Supply, f.OtherPosition, ["supply", "assistant-a"])], []), 0, default);
            return f;
        }
        public void SetCompany(Guid company) => TenantContext.Set(new(Guid.NewGuid(), company, Company, "submitter", "Administrator", true, new HashSet<string>()));
        public async Task Rule(string code, bool enabled = true, IReadOnlyList<WorkflowNotificationRecipient>? recipients = null)
        {
            var current = await Store.ReadAsync(Company, default);
            await Service.SaveFlowAsync(code, new(enabled, recipients ?? [new("SubmitterDepartment", null, RoleCode: "TechnicalAssistant"), new("Fixed", Supply, RoleCode: "SupplyChain")], current.RowVersion, Company), "submitter", UserRole.Administrator, default);
        }
        public async Task<ReleasePackage> Release(ReleaseScope scope, string code)
        {
            var task = new ApprovalTask(Guid.NewGuid(), Guid.NewGuid(), ApprovalStage.MechanicalEngineer, "submitter", null, null, null, null) { StepOrder = 1, StepName = "现有自检" };
            var package = new ReleasePackage(task.ReleasePackageId, Project.Id, "RP-" + Guid.NewGuid().ToString("N"), ReleasePackageState.Approval, null, "M1", "E1", [task], DateTimeOffset.UtcNow, null, null) { Scope = scope, WorkflowCode = code, LocksDocuments = false };
            await Repository.CreateReleasePackageAsync(package, default);
            return package;
        }
        public async Task<WorkflowNoticeJob> Job(string code) => Assert.Single(await Store.ListJobsAsync(Company, code, default));
        public async Task<int> Notices(string user) => (await Repository.ListUserNotificationsAsync(user, 100, default)).Count;
        public void Dispose() => TenantContext.Clear();
    }

    [Fact]
    public async Task NotificationConfigurationIsSeparateFromApprovalAndCompanyScopedWithConcurrencyChecks()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var before = JsonSerializer.Serialize((await f.Repository.GetSystemSettingsAsync(default)).ApprovalWorkflows);
        await f.Rule("standard-release");
        Assert.Equal(before, JsonSerializer.Serialize((await f.Repository.GetSystemSettingsAsync(default)).ApprovalWorkflows));
        var catalog = await f.Store.ReadAsync(f.Company, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveFlowAsync("standard-release", new(false, [], 0, f.Company), "submitter", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveFlowAsync("standard-release", new(false, [], catalog.RowVersion, Guid.NewGuid()), "submitter", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveFlowAsync("standard-release", new(true, [new("Fixed", Guid.NewGuid(), f.Position)], catalog.RowVersion, f.Company), "submitter", UserRole.Administrator, default));
        f.SetCompany(Guid.NewGuid());
        var other = await f.Service.ReadAsync("submitter", UserRole.Administrator, default);
        Assert.Empty(other.Positions); Assert.Empty(other.Assignments); Assert.Empty(other.Flows);
    }

    [Theory]
    [InlineData(ReleaseScope.StandardFormal, "standard-release")]
    [InlineData(ReleaseScope.NonStandardWithDrawing, "nonstandard-release")]
    [InlineData(ReleaseScope.ElectricalFormal, "electrical-release")]
    [InlineData(ReleaseScope.StandardSupplement, "standard-release")]
    public async Task BomNotificationsWaitForPublicationInheritOriginalSubmitterDepartmentAndDeduplicate(ReleaseScope scope, string code)
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company); await f.Rule(code);
        var package = await f.Release(scope, code);
        await f.Service.CaptureReleaseAsync(package, default); await f.Service.CaptureReleaseAsync(package, default);
        var job = await f.Job(code); Assert.Equal(f.First, job.SubmitterDepartmentId);
        await f.Service.ProcessAsync(job.Id, default); Assert.Equal(0, await f.Notices("assistant-a"));
        await f.Repository.SetOrganizationMembershipsAsync("submitter", [f.Second], f.Second, default);
        await f.Rule(code, false, []); // 已发起流程保留原配置。
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(1, await f.Notices("supply")); Assert.Equal(0, await f.Notices("assistant-b"));
        var sent = (await f.Store.FindJobAsync(job.Id, default))!;
        Assert.Equal("Sent", sent.State);
        // 模拟通知已落库、任务完成标记尚未写入时进程中断。
        await f.Store.SaveJobAsync(sent with { State = "Ready" }, sent.RowVersion, default);
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(1, await f.Notices("supply"));
        await f.Rule(code);
        var second = await f.Release(scope, code); await f.Service.CaptureReleaseAsync(second, default);
        var next = (await f.Store.ListJobsAsync(f.Company, code, default)).Single(item => item.BusinessId == second.Id);
        Assert.Equal(f.Second, next.SubmitterDepartmentId);
        await f.Repository.MarkPublishedAsync(second.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync(next.Id, default); Assert.Equal(1, await f.Notices("assistant-b"));
    }

    [Fact]
    public async Task FormalMaterialCodeReturnsToApplicantEvenWhenAnExtraDepartmentRoleIsVacant()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        await f.Rule("material-code-application", true, [new("Fixed", f.First, RoleCode: "SupplyChain")]);
        var app = new MaterialCodeApplication(Guid.NewGuid(), f.Project.Id, Guid.NewGuid(), MaterialCodeApplicationStatus.Pending, "submitter", DateTimeOffset.UtcNow, null, null, null, null, null, 1) { BomItemName = "申请物料" };
        await f.Materials.CreateMaterialCodeApplicationAsync(app, default); await f.Service.CaptureMaterialAsync(app, default);
        var job = await f.Job("material-code-application");
        await f.Service.ProcessAsync(job.Id, default); Assert.Equal(0, await f.Notices("submitter"));
        await f.Materials.DecideMaterialCodeApplicationAsync(app.Id, 1, MaterialCodeApplicationStatus.Approved, "supply", null, Guid.NewGuid(), "010200001", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync(job.Id, default);
        var notice = Assert.Single(await f.Repository.ListUserNotificationsAsync("submitter", 100, default));
        Assert.Contains("010200001", notice.Content); Assert.Equal("WorkflowMaterialCode", notice.Category);
        Assert.Equal("Attention", (await f.Store.FindJobAsync(job.Id, default))!.State);
        await f.Repository.SetOrganizationMembershipsAsync("supply", [f.First, f.Supply], f.Supply, default);
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("submitter")); Assert.Equal(1, await f.Notices("supply"));
        Assert.Equal("Sent", (await f.Store.FindJobAsync(job.Id, default))!.State);
    }

    [Fact]
    public async Task ValidationEngineersReceiveOnlyTheirAssignedItemsAfterThePlanBecomesEffective()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company); var now = DateTimeOffset.UtcNow;
        var planId = Guid.NewGuid();
        ProjectValidationPlanItem Item(string content, string person) => new(Guid.NewGuid(), null, null, "验证", content, null, null, null, null, person, null, 1);
        var task = new ValidationPlanApprovalTask(Guid.NewGuid(), planId, 1, ApprovalStage.MechanicalEngineer, "现有自检", "submitter", null, null, null, now, null);
        var plan = new ProjectValidationPlan(planId, f.Project.Id, 1, ProjectValidationPlanState.PendingApproval, "submitter", DateOnly.FromDateTime(DateTime.UtcNow), [Item("机械精度", "engineer-a"), Item(new string('长', 1200), "engineer-a"), Item("电气安全", "engineer-b")], [task], [], "submitter", now, "submitter", now, 1) { SubmittedBy = "submitter", SubmittedAt = now };
        plan = await f.Plans.SavePlanAsync(plan, null, default); await f.Service.CaptureValidationAsync(plan, default);
        var job = await f.Job("validation-plan"); await f.Service.ProcessAsync(job.Id, default); Assert.Equal(0, await f.Notices("engineer-a"));
        await f.Plans.SavePlanAsync(plan with { State = ProjectValidationPlanState.Effective }, plan.RowVersion, default);
        await f.Service.ProcessAsync(job.Id, default);
        var first = Assert.Single(await f.Repository.ListUserNotificationsAsync("engineer-a", 100, default));
        var second = Assert.Single(await f.Repository.ListUserNotificationsAsync("engineer-b", 100, default));
        Assert.Contains("机械精度", first.Content); Assert.DoesNotContain("电气安全", first.Content);
        Assert.True(first.Content.Length <= 1000);
        Assert.Contains("电气安全", second.Content); Assert.DoesNotContain("机械精度", second.Content);
        Assert.Equal(0, await f.Notices("supply"));
    }

    [Fact]
    public async Task RoleRulesValidateCompanyDepartmentsAndRealRolesWithoutChangingApprovalSettings()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var catalog = await f.Store.ReadAsync(f.Company, default);
        foreach (var recipient in new WorkflowNotificationRecipient[] {
            new("Fixed", Guid.NewGuid(), RoleCode: "TechnicalAssistant"),
            new("CompanyRole", f.First, RoleCode: "TechnicalAssistant"),
            new("SubmitterDepartment", f.First, RoleCode: "TechnicalAssistant"),
            new("CompanyRole", null, RoleCode: "missing-role"), new("CompanyRole", null, f.Position, "TechnicalAssistant"),
            new("Fixed", f.First, f.Position) })
            await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveFlowAsync("standard-release", new(true, [recipient], catalog.RowVersion, f.Company), "submitter", UserRole.Administrator, default));
        var custom = (await f.Repository.CreateRoleAsync("供应链主管", "通知测试", "SupplyChain", default)).Roles.Single(item => item.Name == "供应链主管");
        await f.Rule("standard-release", true, [new("Fixed", f.Supply, RoleCode: custom.Role.ToUpperInvariant())]);
        var rule = Assert.Single((await f.Store.ReadAsync(f.Company, default)).Flows);
        Assert.Equal(custom.Role, Assert.Single(rule.Recipients).RoleCode);
    }

    [Fact]
    public async Task ResubmittingARejectedPublicationReactivatesItsNotificationWithoutChangingApprovalTasks()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company); await f.Rule("standard-release");
        var package = await f.Release(ReleaseScope.StandardFormal, "standard-release");
        await f.Service.CaptureReleaseAsync(package, default);
        var job = await f.Job("standard-release");
        await f.Repository.DecideApprovalAsync(package.ApprovalTasks[0].Id, "submitter", ApprovalDecision.Rejected, "补充资料", false, null, default);
        await f.Service.ProcessAsync(job.Id, default); Assert.Equal("Cancelled", (await f.Store.FindJobAsync(job.Id, default))!.State);
        var submitted = await f.Repository.SubmitReleasePackageAsync(package.Id, "submitter", default);
        await f.Service.CaptureReleaseAsync(submitted, default, restart: true);
        Assert.Equal("Waiting", (await f.Store.FindJobAsync(job.Id, default))!.State);
        Assert.Equal(package.ApprovalTasks[0].Id, submitted.ApprovalTasks[0].Id);
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync(job.Id, default); Assert.Equal(1, await f.Notices("assistant-a"));
    }

    [Fact]
    public async Task EnablingNotificationsBeforeSubmissionRetainsTheDepartmentUsedWhenApprovalWasCreated()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var package = await f.Release(ReleaseScope.StandardFormal, "standard-release");
        await f.Service.CaptureReleaseAsync(package, default);
        await f.Repository.SetOrganizationMembershipsAsync("submitter", [f.Second], f.Second, default);
        await f.Rule("standard-release");
        await f.Service.CaptureReleaseAsync(package, default, restart: true);
        var job = await f.Job("standard-release"); Assert.Equal(f.First, job.SubmitterDepartmentId);
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(0, await f.Notices("assistant-b"));
    }

    [Fact]
    public async Task DepartmentRolesDoNotRequirePositionAssignmentsOrUseDepartmentManagerAsARecipient()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var before = await f.Store.ReadAsync(f.Company, default);
        await f.Store.SaveAsync(before with { Positions = [], Assignments = [] }, before.RowVersion, default);
        await f.Repository.SetOrganizationUnitManagersAsync(f.First, "engineer-a", [], default);
        await f.Rule("standard-release", true, [new("SubmitterDepartment", null, RoleCode: "TechnicalAssistant"), new("Fixed", f.Supply, RoleCode: "TechnicalAssistant")]);
        var package = await f.Release(ReleaseScope.StandardFormal, "standard-release");
        await f.Service.CaptureReleaseAsync(package, default);
        await f.Repository.SetOrganizationMembershipsAsync("submitter", [f.Second], f.Second, default);
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync((await f.Job("standard-release")).Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(0, await f.Notices("assistant-b")); Assert.Equal(0, await f.Notices("engineer-a"));
    }

    [Fact]
    public async Task NoDepartmentNotifiesAllActiveCompanyRoleMembersAndMixedRowsDeduplicateWithoutGrantingProjectAccess()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        foreach (var (name, company, active) in new[] { ("role-no-project", f.Company, true), ("role-foreign", Guid.NewGuid(), true), ("role-disabled", f.Company, false) })
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, active, RoleCode: "TechnicalAssistant", CompanyId: company), default);
        await f.Repository.SetRolePermissionsAsync("TechnicalAssistant", [], default);
        Assert.False(await f.Repository.HasProjectContentReadAccessAsync(f.Project.Id, "role-no-project", UserRole.Engineer, default));
        await f.Rule("standard-release", true, [new("SubmitterDepartment", null, RoleCode: "TechnicalAssistant"), new("Fixed", f.First, RoleCode: "TechnicalAssistant"), new("CompanyRole", null, RoleCode: "TechnicalAssistant")]);
        var package = await f.Release(ReleaseScope.StandardFormal, "standard-release");
        await f.Service.CaptureReleaseAsync(package, default); await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        var job = await f.Job("standard-release"); await f.Service.ProcessAsync(job.Id, default); await f.Service.ProcessAsync(job.Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(1, await f.Notices("assistant-b")); Assert.Equal(1, await f.Notices("role-no-project"));
        Assert.Equal(0, await f.Notices("role-foreign")); Assert.Equal(0, await f.Notices("role-disabled"));
        var summary = Assert.Single(await f.Repository.ListUserNotificationsAsync("role-no-project", 100, default));
        Assert.Contains("详情按项目权限开放", summary.Content); Assert.DoesNotContain(f.Project.Code, summary.Content); Assert.DoesNotContain(package.Number, summary.Content);
        Assert.False(await f.Repository.HasProjectContentReadAccessAsync(f.Project.Id, "role-no-project", UserRole.Engineer, default));
        Assert.Equal("Sent", (await f.Store.FindJobAsync(job.Id, default))!.State);
    }

    [Fact]
    public async Task QueuedRoleNotificationRechecksCompanyAndRedactsDetailsWhenReadPermissionWasRemoved()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "queued-role", "角色接收人", "unused", UserRole.Engineer, true, RoleCode: "TechnicalAssistant", CompanyId: f.Company), default);
        await f.Repository.SetRolePermissionsAsync("TechnicalAssistant", [], default);
        var now = DateTimeOffset.UtcNow;
        var job = new WorkflowNoticeJob { Id = Guid.NewGuid(), CompanyId = f.Company, ProjectId = f.Project.Id, BusinessId = Guid.NewGuid(), BusinessKind = "Release",
            BusinessToken = "queued", Submitter = "submitter", SourceKey = "queued-role-notice", NotificationSourceKey = "queued-role-notice", Rule = new("standard-release", true, []),
            CreatedAt = now, NextCheckAt = now, State = "Ready", Messages = [new("queued-role", "BOM已正式发布", "不应泄露的业务详情", true)] };
        await f.Store.CaptureAsync(job, default); await f.Service.ProcessAsync(job.Id, default);
        var notice = Assert.Single(await f.Repository.ListUserNotificationsAsync("queued-role", 100, default));
        Assert.DoesNotContain("不应泄露", notice.Content); Assert.Contains("详情按项目权限开放", notice.Content);
        var foreignCompany = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive && item.Id != f.Company).Id;
        await f.Repository.SetUserCompanyScopeAsync("queued-role", foreignCompany, false, [], "submitter", default);
        var second = job with { Id = Guid.NewGuid(), SourceKey = "queued-role-notice-moved", NotificationSourceKey = "queued-role-notice-moved" };
        await f.Store.CaptureAsync(second, default); await f.Service.ProcessAsync(second.Id, default);
        Assert.Equal(1, await f.Notices("queued-role"));
        Assert.Equal("Attention", (await f.Store.FindJobAsync(second.Id, default))!.State);
    }

    [Fact]
    public async Task CapturedLegacyRecipientsKeepTheirOriginalSnapshotUntilANewApprovalUsesRoleConfiguration()
    {
        using var f = await Fixture.Create(); f.SetCompany(f.Company);
        var package = await f.Release(ReleaseScope.StandardFormal, "standard-release");
        var catalog = await f.Store.ReadAsync(f.Company, default);
        await f.Store.SaveAsync(catalog with { Flows = [new("standard-release", true, [new("SubmitterDepartment", null, f.Position)])] }, catalog.RowVersion, default);
        await f.Service.CaptureReleaseAsync(package, default);
        await f.Rule("standard-release", true, [new("CompanyRole", null, RoleCode: "SupplyChain")]);
        await f.Repository.MarkPublishedAsync(package.Id, "unused", DateTimeOffset.UtcNow, default);
        await f.Service.ProcessAsync((await f.Job("standard-release")).Id, default);
        Assert.Equal(1, await f.Notices("assistant-a")); Assert.Equal(0, await f.Notices("supply"));
    }
}
