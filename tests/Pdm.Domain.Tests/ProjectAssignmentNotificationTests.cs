using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
namespace Upton.Pdm.Tests;
public sealed class ProjectAssignmentNotificationTests
{
    [Fact]
    public async Task AssignmentNotifiesOnlyDivisionAndThreeTeamLeadersAndResolvesManagerTask()
    {
        var repo = new InMemoryPdmRepository(TimeProvider.System);
        var company = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var division = await repo.SaveOrganizationUnitAsync(new(null, company, null, "NTEST", "通知测试事业部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        var other = await repo.SaveOrganizationUnitAsync(new(null, company, null, "NOTHER", "另一事业部", OrganizationUnitKind.BusinessDivision, true, 2, true), default);
        foreach (var name in new[] { "head", "project-head", "mechanical-head", "electrical-head", "other-head", "planner", "pm", "lead" })
        {
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "planner" ? UserRole.PlanningManager : UserRole.BusinessUnitManager, true), default);
            await repo.SetUserCompanyScopeAsync(name, company, false, [], "admin", default);
            await repo.SetOrganizationMembershipsAsync(name, [division.Id], division.Id, default);
        }
        await repo.SetOrganizationUnitManagersAsync(division.Id, "head", ["project-head"], default);
        foreach (var (label, actor) in new[] { ("项目组", "project-head"), ("机械组", "mechanical-head"), ("电气组", "electrical-head") })
        {
            var team = await repo.SaveOrganizationUnitAsync(new(null, company, division.Id, actor, label, OrganizationUnitKind.Team, true, 1), default);
            await repo.SetOrganizationUnitManagersAsync(team.Id, actor, [], default);
        }
        await repo.SetOrganizationUnitManagersAsync(other.Id, "other-head", [], default);
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "通知测试", null, new(2026,11,7), 1, "admin", "D:/test", "D:/release"), default);
        var service = new PdmWorkflowService(repo, null!, null!, TimeProvider.System);
        await service.SetProjectExecutionUnitAsync(project.Id, division.Id, "admin", UserRole.Administrator, default);
        foreach (var actor in new[] { "head", "project-head", "mechanical-head", "electrical-head" })
            Assert.Single(await repo.ListUserNotificationsAsync(actor, 200, default), n => n.ProjectId == project.Id);
        Assert.Empty(await repo.ListUserNotificationsAsync("other-head", 200, default));
        var task = Assert.Single(await repo.ListUserNotificationsAsync("head", 200, default));
        Assert.Equal("ProjectStaffingPending", task.Category);
        await repo.MarkAllUserNotificationsReadAsync("head", DateTimeOffset.UtcNow, default);
        await repo.MarkUserNotificationReadAsync(task.Id, "head", DateTimeOffset.UtcNow, default);
        Assert.Null((await repo.ListUserNotificationsAsync("head", 200, default)).Single().ReadAt);
        await service.SetProjectExecutionUnitAsync(project.Id, division.Id, "admin", UserRole.Administrator, default);
        Assert.Single(await repo.ListUserNotificationsAsync("head", 200, default));
        var plannerView = (await repo.ListProjectsForUserAsync("planner", UserRole.PlanningManager, default)).Single(p => p.Id == project.Id);
        Assert.True(plannerView.CanManageMainStaffing);
        var assigned = await service.SetMainProjectStaffingAsync(project.Id, new("pm", [], []), "planner", UserRole.PlanningManager, default);
        Assert.Equal("pm", assigned.PrimaryProjectManager);
        Assert.NotNull((await repo.ListUserNotificationsAsync("head", 200, default)).Single().ResolvedAt);
        await service.SetProjectExecutionUnitAsync(project.Id, division.Id, "admin", UserRole.Administrator, default);
        Assert.Equal("pm", (await repo.FindProjectAsync(project.Id, default))!.PrimaryProjectManager);
        await service.SetProjectExecutionUnitAsync(project.Id, other.Id, "admin", UserRole.Administrator, default);
        Assert.All(await repo.ListUserNotificationsAsync("mechanical-head", 200, default), n => Assert.NotNull(n.ResolvedAt));
        Assert.Equal("ProjectStaffingPending", (await repo.ListUserNotificationsAsync("other-head", 200, default)).Single().Category);
        await repo.SetUserCompanyScopeAsync("planner", Guid.Parse("30000000-0000-0000-0000-000000000001"), false, [], "admin", default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetMainProjectStaffingAsync(project.Id, new("pm", [], []), "planner", UserRole.PlanningManager, default));
    }
}
