using System.Globalization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>读取时补全发布审批抄送的业务上下文，不改写消息或审批记录。</summary>
public static class ApprovalCopyPresentation
{
    public static async Task<IReadOnlyList<UserNotification>> EnrichAsync(IPdmRepository repository,
        IReadOnlyList<UserNotification> notifications, string actor, UserRole role, CancellationToken ct)
    {
        var packages = new Dictionary<Guid, ReleasePackage?>();
        var projects = new Dictionary<Guid, Project?>();
        var access = new Dictionary<Guid, bool>();
        async Task<Project?> ProjectAsync(Guid id)
        {
            if (!projects.TryGetValue(id, out var project))
                projects[id] = project = await repository.FindProjectAsync(id, ct);
            return project;
        }
        async Task<bool> CanReadAsync(Guid id)
        {
            if (!access.TryGetValue(id, out var allowed))
                access[id] = allowed = await repository.HasProjectContentReadAccessAsync(id, actor, role, ct);
            return allowed;
        }

        var result = new List<UserNotification>(notifications.Count);
        foreach (var notice in notifications)
        {
            if (notice.Category != "ApprovalNodeCopy" || notice.ReleasePackageId is not Guid packageId
                || !string.Equals(notice.Recipient, actor, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(notice);
                continue;
            }
            if (!packages.TryGetValue(packageId, out var package))
                packages[packageId] = package = await repository.FindReleasePackageAsync(packageId, ct);
            if (package is null || notice.ProjectId is Guid noticeProjectId && noticeProjectId != package.ProjectId
                || !await CanReadAsync(package.ProjectId) || await ProjectAsync(package.ProjectId) is not { } project)
            {
                result.Add(notice);
                continue;
            }

            Project? parent = null;
            if ((project.RootProjectId ?? project.ParentProjectId) is Guid rootId && rootId != project.Id
                && await CanReadAsync(rootId))
            {
                var candidate = await ProjectAsync(rootId);
                if (candidate?.OrganizationId == project.OrganizationId) parent = candidate;
            }
            var projectCode = project.ParentProjectId.HasValue ? ProjectNumberPolicy.BusinessCode(project) : project.Code;
            var heading = parent is null ? projectCode : $"{parent.Code} · {projectCode}";
            var context = parent is null ? $"项目：{projectCode}" : $"主项目：{parent.Code}；子项目：{projectCode}";
            var task = DecisionTask(package, notice.SourceKey);
            var outcome = task is not null && IsFinalDecision(package, task)
                ? "发布包审批通过" : $"{NodeName(notice, task)} · 审批通过";
            result.Add(notice with
            {
                Title = $"{heading} · {outcome}",
                Content = $"{context}；发布包：{package.Number}" + (string.IsNullOrWhiteSpace(notice.Content) ? "" : $"\n{notice.Content}")
            });
        }
        return result;
    }

    private static ApprovalTask? DecisionTask(ReleasePackage package, string sourceKey)
    {
        var parts = sourceKey.Split(':');
        if (parts.Length != 3 || parts[0] != "approval-copy" || !Guid.TryParseExact(parts[1], "N", out var taskId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
        var matches = package.ApprovalTasks.Where(task => task.Id == taskId && task.ReleasePackageId == package.Id
            // MySQL 的 DATETIME(6) 保留微秒，原 source key 可能保留完整的 100ns tick。
            && task.Decision == ApprovalDecision.Approved && task.DecidedAt is { } decided
            && decided.UtcTicks / 10 == ticks / 10).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsFinalDecision(ReleasePackage package, ApprovalTask task) =>
        package.ApprovalTasks.All(item => item.ReleasePackageId == package.Id && item.Decision == ApprovalDecision.Approved
            && item.DecidedAt is { } decided && decided <= task.DecidedAt)
        && package.ApprovalTasks.Count(item => item.StepOrder >= task.StepOrder) == 1;

    private static string NodeName(UserNotification notice, ApprovalTask? task)
    {
        if (!string.IsNullOrWhiteSpace(task?.StepName)) return task.StepName;
        const string suffix = " · 审批抄送";
        var name = notice.Title.EndsWith(suffix, StringComparison.Ordinal) ? notice.Title[..^suffix.Length] : notice.Title;
        return string.IsNullOrWhiteSpace(name) ? "发布审批节点" : name;
    }
}
