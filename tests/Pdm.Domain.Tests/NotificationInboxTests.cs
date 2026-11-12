using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class NotificationInboxTests
{
    private sealed class Fixture : IDisposable
    {
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public InMemoryProjectPlanningRepository Plans { get; } = new();
        public InMemoryValidationPlanRepository Validations { get; } = new();
        public NotificationInboxService Inbox => new(Repository, Plans, Repository, Validations, TimeProvider.System);
        public Project Project = null!;
        public Guid Company;
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public static async Task<Fixture> Create()
        {
            TenantContext.Clear();
            var f = new Fixture();
            f.Project = (await f.Repository.ListProjectsAsync(default)).First();
            f.Company = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
            foreach (var (name, role) in new[] { ("worker", "Engineer"), ("review-a", "DrawingReviewer"), ("review-b", "DrawingReviewer"), ("supervisor", "MechanicalManager"), ("planner", "PlanningManager") })
                await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: f.Company), default);
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "mixed-admin", "混合管理账号", "unused", UserRole.Administrator, true, RoleCode: "Administrator", CompanyId: f.Company, RoleCodes: ["Administrator", "Engineer"]), default);
            return f;
        }
        public UserNotification Notice(string recipient, string category, string source) => new(Guid.NewGuid(), recipient, category, "业务提醒", "原始业务内容", Project.Id, null, source, Now, null);
        public void Dispose() => TenantContext.Clear();
    }

    [Fact]
    public async Task ParallelReviewersRemainAssignedAndTheirNotificationsEndWhenTheNextNodeBegins()
    {
        using var f = await Fixture.Create();
        var package = new DrawingReviewPackage { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Number = "DR-ROLE", State = DrawingReviewPackageState.InReview,
            CreatedBy = "worker", CreatedAt = f.Now, AssignedReviewers = ["review-a", "review-b"], Supervisor = "supervisor" };
        await f.Repository.CreateDrawingReviewPackageAsync(package, default);
        var a = f.Notice("review-a", "DrawingReviewAssigned", $"drawing-review:{package.Id:N}:reviewer");
        var b = a with { Id = Guid.NewGuid(), Recipient = "review-b" };
        var renewed = a with { Id = Guid.NewGuid(), Category = "DrawingReviewResubmitted", SourceKey = $"drawing-review:{package.Id:N}:resubmit:item:version", CreatedAt = f.Now.AddSeconds(1) };
        var supervisor = f.Notice("supervisor", "DrawingReviewSupervisorApproval", $"drawing-review:{package.Id:N}:supervisor");
        await f.Repository.CreateUserNotificationsAsync([a, b, renewed], default);
        Assert.Equal(1, await f.Inbox.ReconcileAsync(null, default));
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-b", 200, default)).ReadAt);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-a", 200, default), item => item.Id == renewed.Id).ReadAt);
        await f.Repository.AdvanceDrawingReviewToSupervisorAsync(package.Id, default);
        await f.Repository.CreateUserNotificationsAsync([supervisor], default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.All(await f.Repository.ListUserNotificationsAsync("review-a", 200, default), item => Assert.NotNull(item.ResolvedAt));
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-b", 200, default)).ResolvedAt);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("supervisor", 200, default)).ReadAt);
    }

    [Fact]
    public async Task LegacyPublicReviewPoolUsesConfiguredGrantsFromAllTemplates()
    {
        using var f = await Fixture.Create();
        var package = new DrawingReviewPackage { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Number = "DR-POOL", State = DrawingReviewPackageState.InReview,
            CreatedBy = "worker", CreatedAt = f.Now };
        await f.Repository.CreateDrawingReviewPackageAsync(package, default);
        var assigned = f.Notice("review-a", "DrawingReviewAssigned", $"drawing-review:{package.Id:N}:reviewer");
        var copied = assigned with { Id = Guid.NewGuid(), Recipient = "mixed-admin" };
        await f.Repository.CreateUserNotificationsAsync([assigned, copied], default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-a", 200, default)).ReadAt);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("mixed-admin", 200, default)).ReadAt);
    }

    [Fact]
    public async Task CompletedOrReassignedPlanTasksEndOldRemindersButKeepTheCurrentResponsiblePerson()
    {
        using var f = await Fixture.Create();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var task = new ProjectPlanTask(Guid.NewGuid(), "机械设计", "Design", "worker", 7, today, today.AddDays(7), null, null, null, null,
            0, ProjectPlanTaskStatus.NotStarted, [], 1, false, true, 1);
        var plan = new ProjectPlan(Guid.NewGuid(), f.Project.Id, Guid.NewGuid(), "测试计划", "Design", null, null, today, today.AddDays(7), today.AddDays(7), 1,
            [task], "planner", f.Now, "planner", f.Now, 1) { ApprovalStatus = ProjectPlanApprovalStatus.Approved };
        plan = await f.Plans.SavePlanAsync(plan, null, null, default);
        var own = f.Notice("worker", "project-plan", $"project-plan:{task.Id:N}:d7:{today:yyyyMMdd}");
        var unrelated = own with { Id = Guid.NewGuid(), Recipient = "review-b" };
        await f.Repository.CreateUserNotificationsAsync([own, unrelated], default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("worker", 200, default)).ReadAt);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-b", 200, default)).ReadAt);
        await f.Plans.SavePlanAsync(plan with { Tasks = [task with { Status = ProjectPlanTaskStatus.Completed, CompletionPercent = 100 }] }, plan.RowVersion, null, default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("worker", 200, default)).ResolvedAt);
    }

    [Fact]
    public async Task ProjectApplicationPoolKeepsPlanningApproversAndClosesThePriorApplicationRound()
    {
        using var f = await Fixture.Create();
        await PermissionTestUsers.Revoke(f.Repository, "Administrator", PermissionCodes.ProjectApplicationApprove);
        var data = new ProjectApplicationData(new(null, "客户", "项目", null, null, "worker", [], "", false), "人员", []);
        var application = new ProjectApplication(Guid.NewGuid(), "AP-ROLE", f.Company, "worker", "人员", ProjectApplicationService.Pending, 1, data, 1, f.Now, f.Now);
        application = await f.Repository.SaveApplicationAsync(application, new(Guid.NewGuid(), 1, "提交", "worker", "人员", f.Now, data), 0, default);
        var current = f.Notice("planner", "project-application", $"project-application:{application.Id:N}:{application.RowVersion}:planner");
        var wrong = current with { Id = Guid.NewGuid(), Recipient = "mixed-admin" };
        await f.Repository.CreateUserNotificationsAsync([current, wrong], default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("planner", 200, default)).ReadAt);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("mixed-admin", 200, default)).ReadAt);
        await f.Repository.SaveApplicationAsync(application with { Status = ProjectApplicationService.Approved, RowVersion = application.RowVersion + 1 }, new(Guid.NewGuid(), 1, "批准", "planner", "计划", f.Now, data), application.RowVersion, default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("planner", 200, default)).ResolvedAt);
    }

    [Fact]
    public async Task EditingRequestsEndWithTheLeaseWhileAmbiguousHistoricalOwnTodosRemainUnread()
    {
        using var f = await Fixture.Create();
        var document = await f.Repository.RegisterDocumentAsync(new(f.Project.Id, "ROLE-PART", "零件", "ROLE-PART.SLDPRT", DocumentKind.Part), "worker", default);
        var session = Guid.NewGuid();
        document = await f.Repository.CheckoutAsync(document.Id, "worker", session, "设计电脑", f.Now.AddMinutes(30), default);
        var request = f.Notice("worker", "EditLockRequested", $"edit-request:edit-wait:{document.Id:N}:{session:N}:0:review-a");
        var tracking = f.Notice("review-a", "EditLockWaiting", $"edit-wait:{document.Id:N}:{session:N}:0");
        var legacyTodo = f.Notice("review-a", "ProjectTodo", "calendar-todo:legacy") with { Title = "日历待办 · review-a" };
        await f.Repository.CreateUserNotificationsAsync([request, tracking, legacyTodo], default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("worker", 200, default)).ReadAt);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("review-a", 200, default), item => item.Id == tracking.Id).ReadAt);
        await f.Repository.ReclaimCheckoutAsync(document.Id, session, document.CheckedOutAt!.Value, null, f.Now.AddHours(1), default);
        await f.Inbox.ReconcileAsync(null, default);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("worker", 200, default)).ResolvedAt);
        var stored = await f.Repository.ListUnreadUserNotificationsAsync("review-a", default);
        Assert.Equal(legacyTodo.Id, Assert.Single(stored).Id);
    }
}
