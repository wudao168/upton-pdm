using System.Collections.Concurrent;
using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectActivityFeedTests
{
    private static T Store<T>(object repo, string field) => (T)repo.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repo)!;
    private static async Task<(InMemoryPdmRepository Repo, Project Project)> Fixture()
    {
        TenantContext.Clear();
        var repo = new InMemoryPdmRepository(TimeProvider.System);
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        foreach (var name in new[] { "pm", "engineer", "other", "disabled" })
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, name != "disabled", CompanyId: company), default);
        var project = (await repo.ListProjectsAsync(default)).First() with {
            OrganizationId = company, Owner = "pm", PrimaryProjectManager = "pm", Designers = ["engineer"], ResponsibleUsers = ["pm", "engineer", "disabled"] };
        Store<ConcurrentDictionary<Guid, Project>>(repo, "projects")[project.Id] = project;

        return (repo, project);
    }

    [Fact]
    public async Task EmitsOnlyToRelatedActivePeopleAndDoesNotAddInboxTasks()
    {
        var (repo, project) = await Fixture();
        TenantContext.Set(new(Guid.NewGuid(), project.OrganizationId!.Value, project.OrganizationId.Value, "pm", "Engineer", false, new HashSet<string>()));
        try
        {
            var now = DateTimeOffset.UtcNow;
            await ProjectActivityFeed.EmitAsync(repo, project, "Plan", "项目计划变更已生效", "11/20 → 11/25", "pm", "event", now, default);
            await ProjectActivityFeed.EmitAsync(repo, project, "Plan", "项目计划变更已生效", "11/20 → 11/25", "pm", "event", now, default);
            var item = Assert.Single(await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now, default));
            Assert.True(item.Important);
            Assert.Equal(now.AddDays(3), item.ExpiresAt);
            Assert.Empty(await repo.ListUserNotificationsAsync("pm", 100, default));
            Assert.Single(await ProjectActivityFeed.ReadAsync(repo, "engineer", UserRole.Administrator, now, default));
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "other", UserRole.Administrator, now, default));
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "disabled", UserRole.Administrator, now, default));
            TenantContext.Set(TenantContext.Current! with { CompanyId = Guid.NewGuid() });
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now, default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task CoalescesLatestStatusKeepsReadItemsAndExpiresByImportance()
    {
        var (repo, project) = await Fixture();
        TenantContext.Set(new(Guid.NewGuid(), project.OrganizationId!.Value, project.OrganizationId.Value, "pm", "Engineer", false, new HashSet<string>()));
        try
        {
            var now = DateTimeOffset.UtcNow;
            UserNotification Notice(string category, string title, int hours) => new(Guid.NewGuid(), "pm", "ProjectActivity." + category, title, "内容", project.Id, null, Guid.NewGuid().ToString(), now.AddHours(hours), now.AddHours(hours));
            await repo.CreateUserNotificationsAsync([
                Notice("Status", "项目已暂停", -2), Notice("Status", "项目已恢复", -1),
                Notice("Plan", "计划变更", -48), Notice("Staffing", "人员变更过期", -73), Notice("Release", "普通过期", -25)], default);
            var items = await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now, default);
            Assert.Equal(2, items.Count);
            Assert.Equal("项目已恢复", items[0].Title);
            Assert.Equal("计划变更", items[1].Title);
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now.AddDays(4), default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task NeverShowsDraftOrReviewReleaseAndMergesDepartmentCopies()
    {
        var (repo, project) = await Fixture();
        TenantContext.Set(new(Guid.NewGuid(), project.OrganizationId!.Value, project.OrganizationId.Value, "pm", "Engineer", false, new HashSet<string>()));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var package = new ReleasePackage(id, project.Id, "RP-TEST", ReleasePackageState.ProcessReview, null, "", "", [], now, null, null);
            var packages = Store<ConcurrentDictionary<Guid, ReleasePackage>>(repo, "packages");
            packages[id] = package;
            await ReleaseManagementNotifications.NotifyPublishedAsync(repo, package, project, now, default);
            Assert.Empty(await repo.ListProjectActivityNotificationsAsync("pm", now.AddDays(-1), default));
            packages[id] = package with { State = ReleasePackageState.Published, PublishedAt = now };
            await ReleaseManagementNotifications.NotifyPublishedAsync(repo, package, project, now, default);
            Assert.Single(await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now, default));
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "pm", UserRole.Administrator, now.AddHours(25), default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task RechecksReadPermissionAndIgnoresUnchangedStaffing()
    {
        var (repo, project) = await Fixture();
        TenantContext.Set(new(Guid.NewGuid(), project.OrganizationId!.Value, project.OrganizationId.Value, "pm", "Engineer", false, new HashSet<string>()));
        try
        {
            var now = DateTimeOffset.UtcNow;
            await ProjectActivityFeed.StaffingAsync(repo, project, project, "pm", now, default);
            Assert.Empty(await repo.ListProjectActivityNotificationsAsync("pm", now.AddDays(-1), default));
            await repo.CreateUserNotificationsAsync([new(Guid.NewGuid(), "other", "ProjectActivity.Plan", "计划", "内容", project.Id, null, "stale-membership", now, now)], default);
            await repo.SetRolePermissionsAsync("Engineer", [], default);
            Assert.Empty(await ProjectActivityFeed.ReadAsync(repo, "other", UserRole.Engineer, now, default));
        }
        finally { TenantContext.Clear(); }
    }
}
