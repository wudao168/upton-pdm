namespace Upton.Pdm.Domain;

public sealed record UserNotification(
    Guid Id,
    string Recipient,
    string Category,
    string Title,
    string Content,
    Guid? ProjectId,
    Guid? ReleasePackageId,
    string SourceKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
