using Dapper;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<string>> ListNotificationRecipientsAsync(string sourceKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT recipient_username FROM user_notification WHERE source_key=@SourceKey",
            new { SourceKey = sourceKey }, cancellationToken: cancellationToken))).ToArray();
    }

    public async Task<IReadOnlyList<UserNotification>> ListDatedUserTodosAsync(string recipient, Guid companyId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<UserNotificationRow>(new CommandDefinition(
            """
            SELECT n.id,n.recipient_username,n.category,n.title,n.content,n.project_id,n.release_package_id,n.source_key,n.created_at,n.read_at,n.due_date
            FROM user_notification n LEFT JOIN project p ON p.id=n.project_id
            WHERE n.recipient_username=@Recipient AND n.category='ProjectTodo' AND n.due_date BETWEEN @From AND @To
              AND (n.source_key LIKE @Prefix OR p.organization_id=@CompanyId)
            ORDER BY n.due_date,n.created_at,n.id
            """, new { Recipient = recipient, CompanyId = companyId, Prefix = $"calendar-todo:{companyId:N}:%", From = from.ToDateTime(TimeOnly.MinValue), To = to.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
        return rows.Select(MapUserNotification).ToArray();
    }

    public async Task<IReadOnlyList<UserNotification>> ListUserNotificationsAsync(string recipient, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<UserNotificationRow>(new CommandDefinition(
            """
            SELECT id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at,due_date
            FROM user_notification n
            WHERE recipient_username=@Recipient
              AND (n.source_key NOT LIKE 'calendar-todo:%' OR n.source_key LIKE @CalendarPrefix)
              AND NOT (category='project-plan' AND source_key LIKE '%:overdue:%')
              AND (n.category<>'project-plan' OR n.source_key NOT LIKE 'project-plan:%' OR NOT EXISTS (
                  SELECT 1 FROM user_notification newer
                  WHERE newer.recipient_username=n.recipient_username AND newer.category='project-plan'
                    AND newer.source_key NOT LIKE '%:overdue:%'
                    AND SUBSTRING_INDEX(newer.source_key,':',2)=SUBSTRING_INDEX(n.source_key,':',2)
                    AND (newer.created_at>n.created_at OR (newer.created_at=n.created_at AND newer.id>n.id))
              ))
            ORDER BY created_at DESC,id DESC
            LIMIT @Take
            """,
            new { Recipient = recipient, Take = Math.Clamp(take, 1, 200), CalendarPrefix = $"calendar-todo:{TenantContext.CompanyId:N}:%" },
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
                id,recipient_username,category,title,content,project_id,release_package_id,source_key,created_at,read_at,due_date)
            VALUES(
                @Id,@Recipient,@Category,@Title,@Content,@ProjectId,@ReleasePackageId,@SourceKey,@CreatedAt,NULL,@DueDate)
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
                CreatedAt = item.CreatedAt.UtcDateTime,
                DueDate = item.DueDate?.ToDateTime(TimeOnly.MinValue)
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
        row.ReadAt.HasValue ? AsUtc(row.ReadAt.Value) : null,
        row.DueDate.HasValue ? DateOnly.FromDateTime(row.DueDate.Value) : null);

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
        public DateTime? DueDate { get; init; }
    }
}
