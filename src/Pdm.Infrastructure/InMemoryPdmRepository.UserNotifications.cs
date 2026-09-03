using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<Guid, UserNotification> userNotifications = [];

    public Task<IReadOnlyList<UserNotification>> ListUserNotificationsAsync(string recipient, int take, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<UserNotification>>(userNotifications.Values
                .Where(item => string.Equals(item.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(Math.Clamp(take, 1, 200))
                .ToArray());
        }
    }

    public Task CreateUserNotificationsAsync(IReadOnlyList<UserNotification> notifications, CancellationToken cancellationToken)
    {
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
        return Task.CompletedTask;
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
            foreach (var notification in userNotifications.Values
                .Where(item => item.ReadAt is null && string.Equals(item.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
                .ToArray())
                userNotifications[notification.Id] = notification with { ReadAt = readAt };
        }
        return Task.CompletedTask;
    }
}
