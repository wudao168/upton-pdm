using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ReleaseDrawingReworkNotifications
{
    public const string Category = "ReleaseDrawingChangesRequested";
    public const string ResubmittedCategory = "ReleaseDrawingResubmitted";

    public static bool IsDrawingReworkCategory(string category) => category is Category or ResubmittedCategory;

    public static async Task SyncAsync(IPdmRepository repository, CancellationToken ct)
    {
        foreach (var packageId in await repository.ListReleasePackagesWithDrawingReworkAsync(ct))
        {
            var package = await repository.FindReleasePackageAsync(packageId, ct);
            if (package?.DrawingReview is not { } review || ReleaseDrawingRevision.CurrentTask(package) is not { } task)
                continue;
            var project = await repository.FindProjectAsync(package.ProjectId, ct);
            if (project is null) continue;
            var access = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var notices = new List<UserNotification>();
            foreach (var item in review.Items)
            {
                var issue = ReleaseDrawingRevision.ActiveReturn(package, item);
                if (issue is null)
                {
                    if (NeedsRecheck(package, review, item, task))
                        await AddResubmittedNoticesAsync(repository, package, project, review, task, item, notices, ct);
                    continue;
                }
                var version = await repository.FindDocumentVersionAsync(item.DocumentId, item.VersionId, ct);
                var source = version?.SourceVersionId is Guid sourceId
                    ? await repository.FindDocumentVersionAsync(item.DocumentId, sourceId, ct) : null;
                var submitter = package.ApprovalTasks.MinBy(step => step.StepOrder)?.Assignee;
                foreach (var recipient in new[] { source?.CreatedBy ?? version?.CreatedBy, submitter }.OfType<string>()
                             .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!access.TryGetValue(recipient, out var allowed))
                        access[recipient] = allowed = await CanReadAsync(repository, project.Id, recipient, ct)
                            && await NotificationRecipientPolicy.CanReceiveAsync(repository, recipient, ct);
                    if (!allowed) continue;
                    notices.Add(new(Guid.NewGuid(), recipient, Category, "图纸已退回，待修改存档",
                        $"{project.Code} · {package.Number} · {item.FileName}。点击查看审核意见，修改后提交存档生成新版本，系统自动安排复审。",
                        project.Id, package.Id, SourceKey(package, item, issue), issue.At, null));
                    break;
                }
            }
            await repository.CreateUserNotificationsAsync(notices, ct);
        }
    }

    private static async Task AddResubmittedNoticesAsync(IPdmRepository repository, ReleasePackage package, Project project,
        ReleaseDrawingReview review, ApprovalTask task, ReleaseDrawingReviewItem item, List<UserNotification> notices, CancellationToken ct)
    {
        var recipients = (task.Participants?.Remaining() ?? [task.Assignee])
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient)
                && !ReleaseDrawingReviewService.IsDrawingConfirmed(review, item, task.Id, recipient))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 0) return;
        var version = await repository.FindDocumentVersionAsync(item.DocumentId, item.VersionId, ct);
        if (version is null || version.Sha256 != item.Sha256) return;
        foreach (var recipient in recipients)
        {
            if (!await CanReadAsync(repository, project.Id, recipient, ct, requireApproval: true)) continue;
            notices.Add(new(Guid.NewGuid(), recipient, ResubmittedCategory, "退回图纸已重新存档，待复审",
                $"{project.Code} · {package.Number} · {item.FileName} · {version.DrawingVersion}。请在图档页审核新版。",
                project.Id, package.Id, ResubmittedSourceKey(package, review, task, item), version.CreatedAt, null));
        }
    }

    public static async Task<bool> CanReadAsync(IPdmRepository repository, Guid projectId, string username,
        CancellationToken ct, bool requireApproval = false)
    {
        var context = await AuthorizationPolicy.CreateContextAsync(repository, projectId, username, ct);
        if (context is null) return false;
        var permissions = AuthorizationPolicy.Decisions(await repository.GetAuthorizationSettingsAsync(ct), context)
            .Where(decision => decision.Allowed).Select(decision => decision.Permission).ToHashSet(StringComparer.Ordinal);
        return permissions.Contains(PermissionCodes.ProjectContentView) && permissions.Contains("release.view")
            && permissions.Contains("project.tab.documents.view")
            && (!requireApproval || permissions.Contains(PermissionCodes.ApprovalDecide));
    }

    public static bool IsPending(UserNotification notice, ReleasePackage? package)
    {
        if (package?.DrawingReview is not { } review || ReleaseDrawingRevision.CurrentTask(package) is not { } task
            || notice.ProjectId != package.ProjectId) return false;
        if (notice.Category == ResubmittedCategory)
        {
            if (!(task.Participants?.CanSign(notice.Recipient)
                ?? task.Assignee.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase))) return false;
            return review.Items.Any(item => notice.SourceKey == ResubmittedSourceKey(package, review, task, item)
                && NeedsRecheck(package, review, item, task) && ReleaseDrawingRevision.ActiveReturn(package, item) is null
                && !ReleaseDrawingReviewService.IsDrawingConfirmed(review, item, task.Id, notice.Recipient));
        }

        return review.Items.Any(item => ReleaseDrawingRevision.ActiveReturn(package, item) is { } issue
            && notice.SourceKey == SourceKey(package, item, issue));
    }

    private static bool NeedsRecheck(ReleasePackage package, ReleaseDrawingReview review, ReleaseDrawingReviewItem item, ApprovalTask task)
    {
        if (!item.Required) return false;
        var issue = item.Notes.LastOrDefault(note => note.Status == "Issue" && note.RoundId == review.RoundId
            && note.VersionId != item.VersionId);
        var returnTask = issue is null ? null : package.ApprovalTasks.FirstOrDefault(step => step.Id == issue.ApprovalTaskId);
        return returnTask is not null && task.StepOrder <= returnTask.StepOrder
            && task.StepOrder >= ReleaseDrawingRevision.RecheckStart(package, returnTask).StepOrder;
    }

    private static string ResubmittedSourceKey(ReleasePackage package, ReleaseDrawingReview review, ApprovalTask task,
        ReleaseDrawingReviewItem item) =>
        $"release-drawing-resubmitted:{item.DocumentId:N}:{package.Id:N}:{review.RoundId:N}:{task.Id:N}:{item.VersionId:N}";

    private static string SourceKey(ReleasePackage package, ReleaseDrawingReviewItem item, ReleaseDrawingReviewNote issue) =>
        $"release-drawing-return:{item.DocumentId:N}:{package.Id:N}:{issue.ApprovalTaskId:N}:{issue.RoundId:N}:{issue.VersionId:N}:{issue.At.UtcTicks}";
}
