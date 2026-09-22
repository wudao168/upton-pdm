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
    DateTimeOffset? ReadAt)
{
    /// <summary>计划逾期提醒：不再产生，也不在消息中心展示；逾期只在项目计划与工作台体现。</summary>
    public bool IsPlanOverdueReminder => Category == "project-plan" && SourceKey.Contains(":overdue:", StringComparison.Ordinal);
}
