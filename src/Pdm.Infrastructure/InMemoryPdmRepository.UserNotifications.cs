using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, UserNotification> userNotifications = [];
    public Task ResolveWorkHoursNotificationsAsync(string recipient, Guid companyId, DateTimeOffset readAt, CancellationToken ct)
    {
        lock (gate)
            foreach (var notice in userNotifications.Values.Where(x => x.ReadAt is null && x.Category == "work-hours" && x.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase) && x.SourceKey.StartsWith($"work-hours:{companyId:N}:", StringComparison.Ordinal)).ToArray())
                userNotifications[notice.Id] = notice with { ReadAt = readAt };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNotificationRecipientsAsync(string sourceKey, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<string>>(userNotifications.Values
            .Where(item => item.SourceKey == sourceKey).Select(item => item.Recipient).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task<IReadOnlyList<UserNotification>> ListDatedUserTodosAsync(string recipient, Guid companyId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (NotificationRecipientPolicy.SystemOnly(users.Values.FirstOrDefault(user => user.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase))))
            return Task.FromResult<IReadOnlyList<UserNotification>>([]);
        lock (gate) return Task.FromResult<IReadOnlyList<UserNotification>>(userNotifications.Values.Where(item =>
            item.Recipient.Equals(recipient, StringComparison.OrdinalIgnoreCase) && item.Category == "ProjectTodo" && item.DueDate >= from && item.DueDate <= to
            && (item.SourceKey.StartsWith($"calendar-todo:{companyId:N}:", StringComparison.Ordinal)
                || item.ProjectId is Guid id && projects.TryGetValue(id, out var project) && project.OrganizationId == companyId)).OrderBy(item => item.DueDate).ThenBy(item => item.CreatedAt).ToArray());
    }

    public Task<IReadOnlyList<UserNotification>> ListUserNotificationsAsync(string recipient, int take, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var systemOnly = NotificationRecipientPolicy.SystemOnly(users.Values.FirstOrDefault(user => user.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult<IReadOnlyList<UserNotification>>(userNotifications.Values
                .Where(item => string.Equals(item.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                .Where(item => !systemOnly || NotificationRecipientPolicy.IsSystemMessage(item))
                .Where(item => item.Category is not ("ReleasePreviewPending" or "ReleasePreviewResult")
                    || item.ReleasePackageId is Guid packageId && packages.TryGetValue(packageId, out var package) && ReleasePreviewPolicy.RequiresConversion(package))
                .Where(item => !item.SourceKey.StartsWith("calendar-todo:", StringComparison.Ordinal) || TenantContext.CompanyId is Guid company && item.SourceKey.StartsWith($"calendar-todo:{company:N}:", StringComparison.Ordinal))
                .Where(item => item.Category is not ("work-hours" or "work-hours-correction") || TenantContext.CompanyId is not Guid hoursCompany || item.SourceKey.StartsWith($"{item.Category}:{hoursCompany:N}:", StringComparison.Ordinal))
                .Where(item => item.Category != "stocked-material" || TenantContext.CompanyId is not Guid stockedCompany || item.SourceKey.StartsWith($"stocked-low:{stockedCompany:N}:", StringComparison.Ordinal))
                .Where(item => !item.IsPlanOverdueReminder)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .GroupBy(item => item.Category == "project-plan" && item.SourceKey.StartsWith("project-plan:", StringComparison.Ordinal) ? $"project-plan:{item.SourceKey.Split(':').ElementAtOrDefault(1)}" : item.Id.ToString())
                .Select(group => group.First())
                .Take(Math.Clamp(take, 1, 200))
                .ToArray());
        }
    }

    public async Task CreateUserNotificationsAsync(IReadOnlyList<UserNotification> notifications, CancellationToken cancellationToken)
    {
        notifications = await NotificationRecipientPolicy.FilterAsync(this, notifications, cancellationToken);
        lock (gate)
        {
            foreach (var notification in notifications)
            {
                if (userNotifications.Values.Any(item =>
                    string.Equals(item.Recipient, notification.Recipient, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.SourceKey, notification.SourceKey, StringComparison.Ordinal))) continue;
                userNotifications[notification.Id] = notification;
            }
        }
    }

    public Task MarkUserNotificationReadAsync(Guid notificationId, string recipient, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (userNotifications.TryGetValue(notificationId, out var notification)
                && string.Equals(notification.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                userNotifications[notificationId] = notification with { ReadAt = readAt };
        }
        return Task.CompletedTask;
    }

    public Task MarkAllUserNotificationsReadAsync(string recipient, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var systemOnly = NotificationRecipientPolicy.SystemOnly(users.Values.FirstOrDefault(user => user.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase)));
            foreach (var notification in userNotifications.Values
                .Where(item => item.ReadAt is null && string.Equals(item.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                .Where(item => !systemOnly || NotificationRecipientPolicy.IsSystemMessage(item))
                .ToArray())
                userNotifications[notification.Id] = notification with { ReadAt = readAt };
        }
        return Task.CompletedTask;
    }
}
