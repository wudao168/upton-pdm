using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectActivityItem(Guid Id, Guid ProjectId, string ProjectCode, Guid? ReleasePackageId,
    string Title, string Content, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Important, string Target, string Kind);

/// <summary>生效事件的知会动态。复用通知存储，独立于消息已读和待办状态。</summary>
public static class ProjectActivityFeed
{
    public const string Prefix = "ProjectActivity.";
    public static bool IsCandidate(UserNotification item) => item.Category.StartsWith(Prefix, StringComparison.Ordinal)
        || item.Category == "ReleaseDepartment";

    public static IEnumerable<string> Members(Project project) => project.ResponsibleUsers.Concat(project.Designers)
        .Concat(project.DesignLeads).Concat(project.CollaborativeProjectManagers)
        .Concat(project.PhaseOwners.Values.SelectMany(value => value.Split('、')))
        .Concat(project.CustomPositionMembers.Values.SelectMany(value => value))
        .Append(project.PrimaryProjectManager ?? "").Append(project.DesignLead ?? "").Append(project.Owner)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static async Task EmitAsync(IPdmRepository repository, Project project, string kind, string title, string content,
        string actor, string eventKey, DateTimeOffset now, CancellationToken ct, Guid? packageId = null, IEnumerable<string>? extraRecipients = null)
    {
        var root = project.RootProjectId ?? project.ParentProjectId;
        var parent = root is Guid rootId && rootId != project.Id ? await repository.FindProjectAsync(rootId, ct) : null;
        var descendants = project.ParentProjectId is null
            ? (await repository.ListProjectsAsync(ct)).Where(item => item.Id != project.Id && (item.RootProjectId == project.Id || item.ParentProjectId == project.Id)).SelectMany(Members)
            : [];
        var names = Members(project).Concat(parent is null ? [] : Members(parent)).Concat(descendants).Concat(extraRecipients ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var sender = directory.Users.FirstOrDefault(user => user.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? actor;
        var notices = new List<UserNotification>();
        foreach (var name in names)
        {
            var user = await repository.FindUserAsync(name, ct);
            if (user is null || !user.IsActive || !await NotificationRecipientPolicy.CanReceiveAsync(repository, user.Username, ct)
                || !await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, project.OrganizationId, ct)
                || !await repository.HasProjectContentReadAccessAsync(project.Id, user.Username, user.Role, ct)) continue;
            notices.Add(new(Guid.NewGuid(), user.Username, Prefix + kind, title, content + (string.IsNullOrWhiteSpace(sender) ? "" : $" · {sender}"),
                project.Id, packageId, $"project-activity:{project.Id:N}:{kind}:{eventKey}", now, now));
        }
        await repository.CreateUserNotificationsAsync(notices, ct);
    }

    public static async Task StaffingAsync(IPdmRepository repository, Project before, Project after, string actor, DateTimeOffset now, CancellationToken ct)
    {
        static string Signature(Project p) => string.Join("|", new[] { p.PrimaryProjectManager ?? "", string.Join("、", p.DesignLeads.Order()), string.Join("、", p.Designers.Order()),
            string.Join("、", p.CollaborativeProjectManagers.Order()), string.Join("、", p.PhaseOwners.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value)), string.Join("、", p.CustomPositionMembers.OrderBy(x => x.Key).Select(x => x.Key + "=" + string.Join(",", x.Value.Order()))) });
        if (Signature(before) == Signature(after)) return;
        var changes = new List<string>();
        if (before.PrimaryProjectManager != after.PrimaryProjectManager) changes.Add($"项目经理：{before.PrimaryProjectManager ?? "未分配"} → {after.PrimaryProjectManager ?? "未分配"}");
        if (!before.DesignLeads.Order().SequenceEqual(after.DesignLeads.Order())) changes.Add($"主设计：{string.Join("、", before.DesignLeads)} → {string.Join("、", after.DesignLeads)}");
        if (!before.Designers.Order().SequenceEqual(after.Designers.Order())) changes.Add($"设计人员：{string.Join("、", before.Designers)} → {string.Join("、", after.Designers)}");
        if (!before.CollaborativeProjectManagers.Order().SequenceEqual(after.CollaborativeProjectManagers.Order())) changes.Add($"协同项目经理：{string.Join("、", before.CollaborativeProjectManagers)} → {string.Join("、", after.CollaborativeProjectManagers)}");
        if (changes.Count == 0) changes.Add("项目分工已调整，请核对团队负责人");
        await EmitAsync(repository, after, "Staffing", "项目负责人变更", string.Join("；", changes), actor, Guid.NewGuid().ToString("N"), now, ct, extraRecipients: Members(before));
    }

    public static async Task<IReadOnlyList<ProjectActivityItem>> ReadAsync(IPdmRepository repository, string actor, UserRole role, DateTimeOffset now, CancellationToken ct)
    {
        var user = await repository.FindUserAsync(actor, ct);
        if (user is null || !user.IsActive || !await NotificationRecipientPolicy.CanReceiveAsync(repository, user.Username, ct) || TenantContext.CompanyId is not Guid company) return [];
        var notices = await repository.ListProjectActivityNotificationsAsync(actor, now.AddDays(-3), ct);
        var result = new List<ProjectActivityItem>();
        foreach (var group in notices.Where(IsCandidate).Where(item => item.ProjectId is not null).GroupBy(item => item.ProjectId!.Value))
        {
            var project = await repository.FindProjectAsync(group.Key, ct);
            if (project?.OrganizationId != company || !await repository.HasProjectContentReadAccessAsync(group.Key, actor, role, ct)) continue;
            foreach (var notice in group)
            {
                var important = notice.Category is "ProjectActivity.Plan" or "ProjectActivity.Status" or "ProjectActivity.Staffing";
                var expires = notice.CreatedAt.AddHours(important ? 72 : 24);
                if (expires <= now || notice.CreatedAt > now) continue;
                if (notice.ReleasePackageId is Guid id && (await repository.FindReleasePackageAsync(id, ct))?.State != ReleasePackageState.Published) continue;
                var target = notice.ReleasePackageId is not null ? "release" : notice.Category is "ProjectActivity.Plan" or "ProjectActivity.Status" ? "project-plan" : "overview";
                result.Add(new(notice.Id, project.Id, project.Code, notice.ReleasePackageId, notice.Title, notice.Content, notice.CreatedAt, expires, important, target, notice.Category == "ReleaseDepartment" ? "Release" : notice.Category[Prefix.Length..]));
            }
        }
        return result.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .GroupBy(item => item.ReleasePackageId is Guid id ? $"release:{id}" : $"{item.ProjectId}:{item.Kind}")
            .Select(items => items.First()).OrderByDescending(item => item.Important).ThenByDescending(item => item.CreatedAt).Take(100).ToArray();
    }
}
