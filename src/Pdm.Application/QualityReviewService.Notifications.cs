using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class QualityReviewService
{
    public const string NotificationCategory = "QualityReviewPending";
    private static string NotificationPrefix(Guid company) => $"quality-review:{company:N}:";
    public async Task NotifyPendingAsync(Guid companyId, CancellationToken ct)
    {
        if (TenantContext.CompanyId != companyId) throw new PdmRuleException("质量提醒的公司上下文不匹配。");
        var catalog = await store.ReadAsync(companyId, ct);
        var settings = await repository.GetAuthorizationSettingsAsync(ct);
        var users = new List<UserAccount>();
        foreach (var user in (await repository.ListUsersAsync(ct)).Where(u => u.IsActive))
            if (await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, companyId, ct)) users.Add(user);
        var notifications = new List<UserNotification>();
        var prefix = NotificationPrefix(companyId);
        bool Can(UserAccount user, string permission) => AuthorizationPolicy.GlobalAllows(settings, user, companyId, permission)
            && AuthorizationPolicy.GlobalAllows(settings, user, companyId, AuthorizationActions.BusinessNotifications);
        void Add(string kind, Guid id, UserAccount user, string title, DateOnly? due, Guid? project = null) =>
            notifications.Add(new(Guid.NewGuid(), user.Username, NotificationCategory, title,
                "请进入质量复盘工作台查看当前记录并处理。", project, null, prefix + kind + ":" + id.ToString("N"), clock.GetUtcNow(), null, due));
        foreach (var item in catalog.Improvements)
        {
            if (item.State is "已关闭" or "已撤销") continue;
            if (users.FirstOrDefault(u => Same(u.Username, item.Owner)) is { } owner && Can(owner, QualityReviewPermissions.View))
                Add("improvement", item.Id, owner, item.DueOn < Today ? "公司改善已逾期" : "公司改善待跟踪", item.DueOn);
            foreach (var action in item.Actions.Where(a => !a.Completed && item.State == "实施中"))
                if (users.FirstOrDefault(u => Same(u.Username, action.Owner)) is { } executor && Can(executor, QualityReviewPermissions.ImprovementExecute))
                    Add("action", action.Id, executor, action.DueOn < Today ? "改善措施已逾期" : "改善措施待执行", action.DueOn);
            if (item.State == "草稿")
                foreach (var user in users.Where(u => Can(u, QualityReviewPermissions.ImprovementApprove))) Add("approval", item.Id, user, "公司改善计划待批准", item.DueOn);
            if (item.State == "待验证")
                foreach (var user in users.Where(u => Can(u, QualityReviewPermissions.Verify)))
                {
                    try { QualityReviewRules.RequireIndependentVerifier(item, user.Username); }
                    catch (PdmRuleException) { continue; }
                    Add("verify", item.Id, user, "公司改善待独立验证", item.DueOn);
                }
        }
        foreach (var adoption in catalog.Adoptions.Where(a => a.State == "待检查"))
            if (users.FirstOrDefault(u => Same(u.Username, adoption.Owner)) is { } owner
                && Can(owner, AuthorizationActions.BusinessNotifications)
                && await ProjectCanAsync(adoption.ProjectId, owner.Username, QualityReviewPermissions.AdoptionConfirm, ct))
                Add("adoption", adoption.Id, owner, adoption.DueOn < Today ? "项目经验检查已逾期" : "项目经验待检查采用", adoption.DueOn, adoption.ProjectId);
        var projects = await ProjectsAsync(companyId, ct);
        var rows = await RowsAsync(catalog, projects, "", ct);
        foreach (var row in rows.Where(r => r.Profile?.RecurrenceOfIssueId is not null && r.Issue.Status != "已作废"))
        {
            var profile = row.Profile!;
            if (catalog.Improvements.Any(i => i.ThemeId == profile.ThemeId && i.State is not ("已关闭" or "已撤销"))) continue;
            var closed = catalog.Improvements.Where(i => i.ThemeId == profile.ThemeId && i.ClosedAt is not null
                && profile.OccurredOn >= DateOnly.FromDateTime(i.ClosedAt.Value.ToOffset(TimeSpan.FromHours(8)).Date)).OrderByDescending(i => i.ClosedAt).FirstOrDefault();
            if (closed is not null && users.FirstOrDefault(u => Same(u.Username, closed.Owner)) is { } owner && Can(owner, QualityReviewPermissions.View))
                Add("recurrence", row.Issue.Id, owner, "已关闭改善出现后续复发，请评估重开", null);
        }
        var metrics = QualityReviewRules.Metrics(catalog, rows, Today);
        foreach (var metric in metrics.Where(m => m.IsCandidate && m.ActiveImprovementId is null))
            foreach (var user in users.Where(u => Can(u, QualityReviewPermissions.Improve))) Add("candidate", metric.ThemeId, user, "共性问题达到改善候选条件", null);
        var finished = new List<string>();
        foreach (var item in catalog.Improvements)
        {
            if (item.State is "已关闭" or "已撤销") finished.Add(prefix + "improvement:" + item.Id.ToString("N"));
            if (item.State != "草稿") finished.Add(prefix + "approval:" + item.Id.ToString("N"));
            if (item.State != "待验证") finished.Add(prefix + "verify:" + item.Id.ToString("N"));
            foreach (var action in item.Actions.Where(a => a.Completed || item.State != "实施中")) finished.Add(prefix + "action:" + action.Id.ToString("N"));
        }
        finished.AddRange(catalog.Adoptions.Where(a => a.State != "待检查").Select(a => prefix + "adoption:" + a.Id.ToString("N")));
        finished.AddRange(metrics.Where(m => !m.IsCandidate || m.ActiveImprovementId is not null).Select(m => prefix + "candidate:" + m.ThemeId.ToString("N")));
        await repository.ResolveUserNotificationsAsync(finished, clock.GetUtcNow(), ct);
        var desired = notifications.Select(n => (n.SourceKey, n.Recipient.ToLowerInvariant())).ToHashSet();
        foreach (var stale in (await repository.ListUnreadUserNotificationsAsync(null, ct)).Where(n => n.Category == NotificationCategory
            && n.SourceKey.StartsWith(prefix, StringComparison.Ordinal) && !desired.Contains((n.SourceKey, n.Recipient.ToLowerInvariant()))))
            await repository.ResolveUserNotificationAsync(stale.Id, stale.Recipient, clock.GetUtcNow(), ct);
        await repository.UpsertUserNotificationsAsync(notifications, ct);
    }
    public async Task<IReadOnlyList<UserNotification>> FilterNotificationsAsync(IReadOnlyList<UserNotification> list, string actor, CancellationToken ct)
    {
        var result = new List<UserNotification>();
        foreach (var notification in list)
        {
            if (notification.Category != NotificationCategory) { result.Add(notification); continue; }
            if (TenantContext.CompanyId is not { } company || !notification.SourceKey.StartsWith(NotificationPrefix(company), StringComparison.Ordinal)) continue;
            if (!await CompanyCanAsync(actor, AuthorizationActions.BusinessNotifications, ct)) continue;
            var kind = notification.SourceKey.Split(':').ElementAtOrDefault(2);
            var permission = kind switch
            {
                "action" => QualityReviewPermissions.ImprovementExecute, "approval" => QualityReviewPermissions.ImprovementApprove,
                "verify" => QualityReviewPermissions.Verify, "candidate" => QualityReviewPermissions.Improve,
                _ => QualityReviewPermissions.View
            };
            if (kind == "adoption")
            {
                if (notification.ProjectId is Guid projectId && await ProjectCanAsync(projectId, actor, QualityReviewPermissions.AdoptionConfirm, ct)) result.Add(notification);
            }
            else if (await CompanyCanAsync(actor, permission, ct)) result.Add(notification);
        }
        return result;
    }
}
