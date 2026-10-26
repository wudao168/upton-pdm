using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class NotificationRecipientPolicyTests
{
    [Theory]
    [InlineData("Administrator", true)]
    [InlineData("administrator", true)]
    [InlineData("Engineer", false)]
    [InlineData("developer", false)]
    public void UsesAssignedRoleInsteadOfAdministrativeBusinessPermissions(string role, bool expected) =>
        Assert.Equal(expected, NotificationRecipientPolicy.SystemOnly(new UserAccount(Guid.NewGuid(), "test", "测试", "unused", UserRole.Administrator, true, RoleCode: role)));

    [Theory]
    [InlineData("Engineer", false)]
    [InlineData("Approver", false)]
    [InlineData("platform_admin", true)]
    [InlineData("PlatformAdministrator", true)]
    [InlineData("developer", true)]
    public void MixedAdministratorReceivesBusinessMessagesOnlyWhenABusinessRoleIsAssigned(string additionalRole, bool expected) =>
        Assert.Equal(expected, NotificationRecipientPolicy.SystemOnly(["Administrator", additionalRole]));

    [Fact]
    public async Task MixedAdministratorReceivesTargetedBusinessMessagesWithoutDuplicatingSystemMessages()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mixed", "兼任工程师", "unused", UserRole.Administrator, true,
            RoleCodes: ["Administrator", "Engineer"]), default);
        UserNotification Notice(string category) => new(Guid.NewGuid(), "mixed", category, "通知", "测试", null, null, category, DateTimeOffset.UtcNow, null);
        var messages = new[] { Notice("System"), Notice("ProjectTodo"), Notice("work-hours") };
        await repository.CreateUserNotificationsAsync(messages, default);
        await repository.CreateUserNotificationsAsync(messages, default);
        Assert.Equal(3, (await repository.ListUserNotificationsAsync("mixed", 100, default)).Count);
    }

    [Theory]
    [InlineData("Administrator", false)]
    [InlineData("Approver", false)]
    [InlineData("Engineer", true)]
    public async Task MixedAdministratorInboxDoesNotUseTheAdministratorRoleForEmergencyApproval(string emergencyRole, bool expected)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mixed", "兼任工程师", "unused", UserRole.Administrator, true,
            RoleCodes: ["Administrator", "Engineer"]), default);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        Assert.Equal(expected, await ApprovalEmergencyPolicy.CanAsync(repository, project.Id, [emergencyRole], "mixed", UserRole.Administrator, default, businessInbox: true));
        if (emergencyRole == "Administrator")
            Assert.True(await ApprovalEmergencyPolicy.CanAsync(repository, project.Id, [emergencyRole], "mixed", UserRole.Administrator, default));
    }

    [Fact]
    public async Task AdministratorReceivesOnlySystemMessages_AndHistoricalBusinessRecordsRemainUnchanged()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "recipient", "接收人", "unused", UserRole.Engineer, true), default);
        var now = DateTimeOffset.UtcNow;
        UserNotification Notice(string category, string source, int minute, Guid? project = null) =>
            new(Guid.NewGuid(), "recipient", category, "通知", "测试内容", project, null, source, now.AddMinutes(minute), null, DateOnly.FromDateTime(now.Date));
        var system = Notice("System", "system-maintenance", 0);
        var business = Notice("ProjectTodo", "calendar-todo:old", 1);
        var project = Notice("ReleasePreviewResult", "release:old", 2, Guid.NewGuid());
        await repository.CreateUserNotificationsAsync([system, business, project], default);
        await repository.UpdateUserAsync("recipient", "接收人", UserRole.Administrator, "Administrator", ["Administrator"], true, default);

        Assert.Equal(system.Id, Assert.Single(await repository.ListUserNotificationsAsync("recipient", 1, default)).Id);
        Assert.Empty(await repository.ListDatedUserTodosAsync("recipient", Guid.NewGuid(), DateOnly.FromDateTime(now.Date).AddDays(-1), DateOnly.FromDateTime(now.Date).AddDays(1), default));
        var rejected = Notice("work-hours", "work-hours:new", 3);
        var disguised = Notice("System", "project:new", 4, Guid.NewGuid());
        await repository.CreateUserNotificationsAsync([rejected, disguised], default);
        Assert.Empty(await repository.ListNotificationRecipientsAsync(rejected.SourceKey, default));
        Assert.Empty(await repository.ListNotificationRecipientsAsync(disguised.SourceKey, default));
        await repository.MarkAllUserNotificationsReadAsync("recipient", now.AddMinutes(5), default);

        await repository.UpdateUserAsync("recipient", "接收人", UserRole.Engineer, "Engineer", ["Engineer"], true, default);
        var remaining = await repository.ListUserNotificationsAsync("recipient", 200, default);
        Assert.NotNull(remaining.Single(item => item.Id == system.Id).ReadAt);
        Assert.Null(remaining.Single(item => item.Id == project.Id).ReadAt);
        Assert.Single(await repository.ListNotificationRecipientsAsync(business.SourceKey, default));
    }
}
