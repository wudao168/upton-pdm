using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed partial class NotificationRecipientApiTests
{
    [Fact]
    public async Task ManagerInboxReconcilesHistoricalCopiesPreservesCurrentApprovalAndSerializesResolvedRisk()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First() with { PrimaryProjectManager = "manager-inbox" };
        ((ConcurrentDictionary<Guid, Project>)typeof(InMemoryPdmRepository).GetField("projects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(repository)!)[project.Id] = project;
        foreach (var name in new[] { "manager-inbox", "inbox-publisher" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, true,
                RoleCode: name == "manager-inbox" ? "ProjectManager" : "Engineer", CompanyId: project.OrganizationId), default);
        var now = DateTimeOffset.UtcNow;
        var packageId = Guid.NewGuid();
        var package = new ReleasePackage(packageId, project.Id, "RP-INBOX", ReleasePackageState.Published, null, "", "",
            [new(Guid.NewGuid(), packageId, ApprovalStage.MechanicalEngineer, "inbox-publisher", "inbox-publisher", ApprovalDecision.Approved, null, now) { StepOrder = 1 }], now, now, null)
            { Scope = ReleaseScope.NonStandardWithDrawing, PreviewState = ReleasePreviewState.Succeeded };
        await repository.CreateReleasePackageAsync(package, default);
        var history = new[] { "ReleasePreviewPending", "ReleasePreviewResult", "ReleaseApprovalRejected" }.Select(category => new UserNotification(
            Guid.NewGuid(), "manager-inbox", category, "旧技术消息", "原始记录", project.Id, package.Id, category, now, null)).ToArray();
        var approvalId = Guid.NewGuid();
        var approval = package with { Id = approvalId, State = ReleasePackageState.Approval, ApprovalTasks = [
            new(Guid.NewGuid(), approvalId, ApprovalStage.MechanicalEngineer, "inbox-publisher", "inbox-publisher", ApprovalDecision.Approved, null, now) { StepOrder = 1 },
            new(Guid.NewGuid(), approvalId, ApprovalStage.MainDesigner, "manager-inbox", null, null, null, null) { StepOrder = 2 }] };
        await repository.CreateReleasePackageAsync(approval, default);
        var current = new UserNotification(Guid.NewGuid(), "manager-inbox", "ReleaseApprovalPending", "当前需要审批", "办理本节点", project.Id, approvalId, "current-approval", now, null);
        var superseded = current with { Id = Guid.NewGuid(), SourceKey = "old-approval-round", CreatedAt = now.AddMinutes(-1) };
        await repository.CreateUserNotificationsAsync([.. history, current, superseded], default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("manager-inbox"));
        var inbox = (await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!;
        Assert.Equal(5, inbox.Length);
        foreach (var old in history) Assert.NotNull(Assert.Single(inbox, item => item.Id == old.Id).ReadAt);
        Assert.Null(Assert.Single(inbox, item => item.Id == current.Id).ReadAt);
        Assert.NotNull(Assert.Single(inbox, item => item.Id == superseded.Id).ReadAt);
        Assert.DoesNotContain(inbox, item => item.Category == "ReleaseDepartment");
        await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Failed, "技术详情", 5, now, default);
        await ReleaseManagementNotifications.ReconcileRiskAsync(repository, package.Id, now, default);
        var risk = Assert.Single((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!, item => item.Category == "ReleaseManagementRisk");
        Assert.Null(risk.ReadAt);
        await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, 5, now.AddMinutes(1), default);
        await ReleaseManagementNotifications.ReconcileRiskAsync(repository, package.Id, now.AddMinutes(1), default, archiveVerified: true);
        var resolved = Assert.Single((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!, item => item.Id == risk.Id);
        Assert.NotNull(resolved.ReadAt); Assert.NotNull(resolved.ResolvedAt);
    }
}
