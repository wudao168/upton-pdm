using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<UserNotification>> ListUserNotificationsAsync(string recipient, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<UserNotificationRow>(new CommandDefinition(
            """
            SELECT id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at
            FROM user_notification
            WHERE recipient_username=@Recipient
            ORDER BY created_at DESC,id DESC
            LIMIT @Take
            """,
            new { Recipient = recipient, Take = Math.Clamp(take, 1, 200) },
            cancellationToken: cancellationToken));
        return rows.Select(MapUserNotification).ToArray();
    }

    public async Task CreateUserNotificationsAsync(IReadOnlyList<UserNotification> notifications, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT IGNORE INTO user_notification(
                id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at)
            VALUES(
                @Id,@Recipient,@Category,@Title,@Content,@ProjectId,@ReleasePackageId,@SourceKey,@CreatedAt,NULL)
            """,
            notifications.Select(item => new
            {
                item.Id,
                item.Recipient,
                item.Category,
                item.Title,
                item.Content,
                item.ProjectId,
                item.ReleasePackageId,
                item.SourceKey,
                CreatedAt = item.CreatedAt.UtcDateTime
            }),
            cancellationToken: cancellationToken));
    }

    public async Task MarkUserNotificationReadAsync(Guid notificationId, string recipient, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_notification SET read_at=COALESCE(read_at,@ReadAt) WHERE id=@Id AND recipient_username=@Recipient",
            new { Id = notificationId, Recipient = recipient, ReadAt = readAt.UtcDateTime },
            cancellationToken: cancellationToken));
    }

    public async Task MarkAllUserNotificationsReadAsync(string recipient, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_notification SET read_at=@ReadAt WHERE recipient_username=@Recipient AND read_at IS NULL",
            new { Recipient = recipient, ReadAt = readAt.UtcDateTime },
            cancellationToken: cancellationToken));
    }

    private static UserNotification MapUserNotification(UserNotificationRow row) => new(
        row.Id,
        row.RecipientUsername,
        row.Category,
        row.Title,
        row.Content,
        row.ProjectId,
        row.ReleasePackageId,
        row.SourceKey,
        AsUtc(row.CreatedAt),
        row.ReadAt.HasValue ? AsUtc(row.ReadAt.Value) : null);

    private sealed class UserNotificationRow
    {
        public Guid Id { get; init; }
        public string RecipientUsername { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public Guid? ProjectId { get; init; }
        public Guid? ReleasePackageId { get; init; }
        public string SourceKey { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? ReadAt { get; init; }
    }
}
