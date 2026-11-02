using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ReleaseManagementNotifications
{
    internal static string RiskSource(Guid id) => $"release-package:{id:N}:management-risk";

    internal static async Task<IReadOnlyList<string>> ManagersAsync(IPdmRepository repository, Project project, OrganizationDirectory directory, CancellationToken ct)
    {
        var root = project;
        if (project.RootProjectId is Guid rootId && rootId != project.Id) root = await repository.FindProjectAsync(rootId, ct) ?? project;
        else while (root.ParentProjectId is Guid parentId)
        {
            var parent = await repository.FindProjectAsync(parentId, ct);
            if (parent is null) break;
            root = parent;
        }
        var names = root.CollaborativeProjectManagers.Concat(project.CollaborativeProjectManagers)
            .Append(root.PrimaryProjectManager).Append(project.PrimaryProjectManager)
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return directory.Users.Where(user => names.Contains(user.Username) && user.IsActive
            && !NotificationRecipientPolicy.SystemOnly(user.EffectiveRoleCodes)
            && (project.OrganizationId is null || user.CompanyId == project.OrganizationId || user.CompanyId is null
                && directory.Memberships.Any(member => member.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)
                    && directory.Units.Any(unit => unit.Id == member.UnitId && unit.OrganizationId == project.OrganizationId && unit.IsActive))))
            .Select(user => user.Username).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadersAsync(IPdmRepository repository, Project project, OrganizationDirectory directory, IEnumerable<string> usernames, CancellationToken ct)
    {
        var result = new List<string>();
        foreach (var name in usernames)
        {
            var user = directory.Users.First(item => item.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (await repository.HasProjectContentReadAccessAsync(project.Id, user.Username, user.Role, ct)) result.Add(user.Username);
        }
        return result;
    }

    public static async Task NotifyPublishedAsync(IPdmRepository repository, ReleasePackage package, Project project, DateTimeOffset now, CancellationToken ct)
    {
        package = await repository.FindReleasePackageAsync(package.Id, ct) ?? package;
        if (package.State != ReleasePackageState.Published) return;
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var recipients = await ReadersAsync(repository, project, directory, await ManagersAsync(repository, project, directory, ct), ct);
        var changed = package.Scope is ReleaseScope.StandardSupplement or ReleaseScope.NonStandardSupplement or ReleaseScope.ElectricalSupplement;
        var early = package.Scope is ReleaseScope.StandardLongLead or ReleaseScope.NonStandardLongLead or ReleaseScope.ElectricalLongLead;
        await repository.CreateUserNotificationsAsync(recipients.Select(name => new UserNotification(Guid.NewGuid(), name, "ReleaseDepartment",
            changed ? "BOM增补/变更已发布" : early ? "BOM已先行发布" : "BOM已正式发布",
            $"{project.Code} · {package.Number} 已发布，请在发布页核对本次内容及后续采购、制造安排。",
            project.Id, package.Id, WorkflowNotificationFlows.ReleaseSourceKey(package.Id, changed ? "Changed" : "Published"), now, null)).ToArray(), ct);
    }

    public static async Task ReconcileRiskAsync(IPdmRepository repository, Guid packageId, DateTimeOffset now, CancellationToken ct,
        bool archiveFailed = false, bool archiveVerified = false)
    {
        var package = await repository.FindReleasePackageAsync(packageId, ct);
        if (package is null) return;
        var project = await repository.FindProjectAsync(package.ProjectId, ct);
        if (project is null) return;
        var needsConversion = ReleasePreviewPolicy.RequiresConversion(package);
        var failed = needsConversion && (package.PreviewState == ReleasePreviewState.Failed
            || package.PreviewAttempts >= ReleasePreviewService.MaxAttempts && package.PreviewState != ReleasePreviewState.Succeeded);
        if (!project.IsActive || package.State is not (ReleasePackageState.Published or ReleasePackageState.Publishing))
        {
            await repository.ResolveUserNotificationsAsync([RiskSource(package.Id)], now, ct);
            return;
        }
        if (archiveFailed || failed)
        {
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var technical = ReleasePreviewNotifications.Recipients(directory, package, project);
            var managers = (await ManagersAsync(repository, project, directory, ct)).Except(technical, StringComparer.OrdinalIgnoreCase);
            var recipients = await ReadersAsync(repository, project, directory, managers, ct);
            await repository.UpsertUserNotificationsAsync(recipients.Select(name => new UserNotification(Guid.NewGuid(), name, "ReleaseManagementRisk",
                "生产资料未齐，需协调", $"{project.Code} · {package.Number} 生产资料尚未齐套，请协调发布人与技术助理处理，并在发布页核对后续投放。",
                project.Id, package.Id, RiskSource(package.Id), now, null)).ToArray(), ct);
        }
        else if (archiveVerified && (!needsConversion || package.PreviewState is ReleasePreviewState.None or ReleasePreviewState.Succeeded))
            await repository.ResolveUserNotificationsAsync([RiskSource(package.Id)], now, ct);
    }

    public static async Task<int> ArchiveLegacyAsync(IPdmRepository repository, string? recipient, DateTimeOffset now, CancellationToken ct)
    {
        var notices = await repository.ListUnreadReleaseNotificationsAsync(recipient, ct);
        var decisions = await ReviewLegacyAsync(repository, notices, ct);
        foreach (var decision in decisions)
            if (decision.Resolved) await repository.ResolveUserNotificationAsync(decision.Notification.Id, decision.Notification.Recipient, now, ct);
            else await repository.MarkUserNotificationReadAsync(decision.Notification.Id, decision.Notification.Recipient, now, ct);
        foreach (var id in notices.Select(item => item.ReleasePackageId!.Value).Distinct()) await ReconcileRiskAsync(repository, id, now, ct);
        return decisions.Count;
    }

    public static async Task<IReadOnlyList<NotificationCleanupDecision>> ReviewLegacyAsync(IPdmRepository repository, IReadOnlyList<UserNotification> notices, CancellationToken ct)
    {
        if (notices.Count == 0) return [];
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var decisions = new List<NotificationCleanupDecision>();
        foreach (var group in notices.GroupBy(item => item.ReleasePackageId!.Value))
        {
            var package = await repository.FindReleasePackageAsync(group.Key, ct);
            if (package is null) continue;
            var project = await repository.FindProjectAsync(package.ProjectId, ct);
            if (project is null) continue;
            var managers = (await ManagersAsync(repository, project, directory, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var technical = ReleasePreviewNotifications.Recipients(directory, package, project).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var submitters = ReleasePreviewNotifications.Submitters(package).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentPendingIds = group.Where(item => item.Category == "ReleaseApprovalPending")
                .GroupBy(item => item.Recipient, StringComparer.OrdinalIgnoreCase)
                .Select(items => items.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).First().Id).ToHashSet();
            foreach (var notice in group)
            {
                var keep = notice.Category == "ReleaseApprovalPending"
                    ? currentPendingIds.Contains(notice.Id) && package.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval
                        && package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault(task => task.Decision is null)?.Assignee.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase) == true
                    : notice.Category == "ReleaseApprovalRejected" ? submitters.Contains(notice.Recipient)
                    : notice.Category == "ReleaseManagementRisk" ? managers.Contains(notice.Recipient) && !technical.Contains(notice.Recipient) : technical.Contains(notice.Recipient);
                var finished = notice.Category == "ReleaseApprovalPending" && package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval)
                    || notice.Category == "ReleaseApprovalRejected" && package.State != ReleasePackageState.Rejected
                    || notice.Category == "ReleasePreviewPending" && package.PreviewState is ReleasePreviewState.Succeeded or ReleasePreviewState.None
                    || notice.Category == "ReleasePreviewResult" && package.PreviewState == ReleasePreviewState.Succeeded
                        && (notice.SourceKey == ReleasePreviewNotifications.Source(package.Id, "failed") || notice.Title is "图纸转换多次失败" or "图纸转换失败，自动重试已暂停");
                if (finished) { decisions.Add(new(notice, true, "本轮发布办理或资料处理已结束")); continue; }
                if (keep) continue;
                decisions.Add(new(notice, false, "不属于当前办理人、资料处理人或项目管理职责"));
            }
        }
        return decisions;
    }
}
