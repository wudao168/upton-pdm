using System.Collections.Concurrent;
using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class ReleaseRecoveryTests
{
    private static async Task<Fixture> ManagerFixtureAsync(string submitter = "publisher")
    {
        var f = await Fixture.CreateAsync();
        var company = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        foreach (var name in new[] { "pm", "collab", "publisher", "reviewer", "unrelated-pm" })
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, true,
                RoleCode: name is "pm" or "collab" or "unrelated-pm" ? "ProjectManager" : "Engineer", CompanyId: company), default);
        f.Project = f.Project with { OrganizationId = company, PrimaryProjectManager = "pm", CollaborativeProjectManagers = ["collab"] };
        ((ConcurrentDictionary<Guid, Project>)typeof(Upton.Pdm.Infrastructure.InMemoryPdmRepository).GetField("projects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Repository)!)[f.Project.Id] = f.Project;
        ApprovalTask Task(string name, int order) => new(Guid.NewGuid(), f.Package.Id, ApprovalStage.MechanicalEngineer, name, name, ApprovalDecision.Approved, null, DateTimeOffset.UtcNow) { StepOrder = order };
        f.Package = f.Package with { ApprovalTasks = [Task(submitter, 1), Task("reviewer", 2), Task("pm", 3)] };
        ((ConcurrentDictionary<Guid, ReleasePackage>)typeof(Upton.Pdm.Infrastructure.InMemoryPdmRepository).GetField("packages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Repository)!)[f.Package.Id] = f.Package;
        return f;
    }

    [Fact]
    public async Task ManagersReceiveOnePublishedSummaryButNoConversionProgressOrHistoricalReviewerCopies()
    {
        using var f = await ManagerFixtureAsync();
        await f.Service.ReconcileDeliveryAsync(f.Package.Id, default);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("publisher", 200, default));
        foreach (var name in new[] { "pm", "collab", "reviewer", "unrelated-pm" })
            Assert.Empty(await f.Repository.ListUserNotificationsAsync(name, 200, default));
        await ReleaseManagementNotifications.NotifyPublishedAsync(f.Repository, f.Package, f.Project, DateTimeOffset.UtcNow, default);
        await ReleaseManagementNotifications.NotifyPublishedAsync(f.Repository, f.Package, f.Project, DateTimeOffset.UtcNow, default);
        foreach (var name in new[] { "pm", "collab" })
        {
            var result = Assert.Single(await f.Repository.ListUserNotificationsAsync(name, 200, default));
            Assert.Equal("ReleaseDepartment", result.Category);
            Assert.Contains(f.Package.Number, result.Content);
        }
        Assert.Empty(await f.Repository.ListUserNotificationsAsync("unrelated-pm", 200, default));
    }

    [Fact]
    public async Task ManagerRiskIsMergedPreservesAcknowledgementClosesAfterRecoveryAndReopensForANewFailure()
    {
        using var f = await ManagerFixtureAsync();
        var now = DateTimeOffset.UtcNow;
        await f.Repository.MarkReleasePreviewStateAsync(f.Package.Id, ReleasePreviewState.Failed, "内部技术错误", 5, now, default);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now, default);
        var risk = Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default));
        Assert.Equal("ReleaseManagementRisk", risk.Category);
        Assert.DoesNotContain("内部技术错误", risk.Content);
        await f.Repository.MarkUserNotificationReadAsync(risk.Id, "pm", now.AddSeconds(1), default);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now.AddMinutes(1), default);
        var acknowledged = Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default));
        Assert.Equal(risk.Id, acknowledged.Id);
        Assert.Equal(now.AddSeconds(1), acknowledged.ReadAt);
        Assert.Null(acknowledged.ResolvedAt);
        await f.Repository.MarkReleasePreviewStateAsync(f.Package.Id, ReleasePreviewState.Succeeded, null, 5, now.AddMinutes(2), default);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now.AddMinutes(2), default, archiveVerified: true);
        var resolved = Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default));
        Assert.NotNull(resolved.ResolvedAt);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now.AddMinutes(3), default, archiveFailed: true);
        var reopened = Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default));
        Assert.Equal(risk.Id, reopened.Id);
        Assert.Null(reopened.ReadAt); Assert.Null(reopened.ResolvedAt);
        Assert.Equal(now.AddMinutes(3), reopened.CreatedAt);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now.AddMinutes(4), default);
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default)).ResolvedAt);
        await ReleaseManagementNotifications.ReconcileRiskAsync(f.Repository, f.Package.Id, now.AddMinutes(5), default, archiveVerified: true);
        Assert.NotNull(Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default)).ResolvedAt);
    }

    [Fact]
    public async Task ManagerWhoSubmittedTheEngineeringWorkRetainsOwnTechnicalMessagesWithoutDuplicateRisk()
    {
        using var f = await ManagerFixtureAsync("pm");
        await f.Repository.MarkReleasePreviewStateAsync(f.Package.Id, ReleasePreviewState.Failed, "转换失败", 5, DateTimeOffset.UtcNow, default);
        await f.Service.ReconcileDeliveryAsync(f.Package.Id, default);
        var owned = Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default));
        Assert.Equal("ReleasePreviewResult", owned.Category);
        Assert.Equal(0, await ReleaseManagementNotifications.ArchiveLegacyAsync(f.Repository, "pm", DateTimeOffset.UtcNow, default));
        Assert.Null(Assert.Single(await f.Repository.ListUserNotificationsAsync("pm", 200, default)).ReadAt);
        Assert.Equal("ReleaseManagementRisk", Assert.Single(await f.Repository.ListUserNotificationsAsync("collab", 200, default)).Category);
    }

    [Fact]
    public async Task HistoryCleanupPreservesRecordsAndDoesNotHideAnOlderActionableTodoBehindTheReadHistoryLimit()
    {
        using var f = await ManagerFixtureAsync();
        await f.Repository.MarkReleasePreviewStateAsync(f.Package.Id, ReleasePreviewState.Succeeded, null, 1, DateTimeOffset.UtcNow, default);
        var now = DateTimeOffset.UtcNow;
        var old = Enumerable.Range(0, 205).Select(index => new UserNotification(Guid.NewGuid(), "pm", "ReleasePreviewResult", "图纸转换已完成", "原始技术记录",
            f.Project.Id, f.Package.Id, $"old-preview-{index}", now.AddMinutes(index), null)).ToArray();
        var todo = new UserNotification(Guid.NewGuid(), "pm", "ProjectTodo", "需要经理办理", "协调交付", f.Project.Id, null, "real-todo", now.AddDays(-1), null);
        var own = old[0] with { Id = Guid.NewGuid(), Recipient = "publisher", SourceKey = "owned-technical" };
        await f.Repository.CreateUserNotificationsAsync([.. old, todo, own], default);
        Assert.Equal(205, await ReleaseManagementNotifications.ArchiveLegacyAsync(f.Repository, null, now.AddDays(1), default));
        var rows = await f.Repository.ListUserNotificationsAsync("pm", 200, default);
        Assert.Equal(todo.Id, Assert.Single(rows, item => item.ReadAt is null).Id);
        var stored = (Dictionary<Guid, UserNotification>)typeof(Upton.Pdm.Infrastructure.InMemoryPdmRepository).GetField("userNotifications", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Repository)!;
        Assert.Equal(207, stored.Count);
        foreach (var source in old)
        {
            Assert.NotNull(stored[source.Id].ReadAt);
            Assert.Equal(source.Content, stored[source.Id].Content);
            Assert.Equal(source.CreatedAt, stored[source.Id].CreatedAt);
        }
        Assert.Null(stored[own.Id].ReadAt);
    }
}
