using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectAssignmentNotifications
{
    private static string Key(Guid project, Guid division, string kind) => $"project-assignment:{project:N}:{division:N}:{kind}";

    public static async Task AssignedAsync(IPdmRepository repository, Project previous, Project project,
        OrganizationDirectory directory, DateTimeOffset now, CancellationToken ct)
    {
        if (project.ExecutionUnitId is not Guid division || previous.ExecutionUnitId == division) return;
        if (previous.ExecutionUnitId is Guid oldDivision)
            await repository.ResolveUserNotificationsAsync([Key(project.Id, oldDivision, "notice"), Key(project.Id, oldDivision, "manager")], now, ct);
        var units = directory.Units.Where(unit => unit.IsActive && unit.OrganizationId == project.OrganizationId).ToDictionary(unit => unit.Id);
        bool WithinDivision(Guid id)
        {
            var visited = new HashSet<Guid>();
            while (visited.Add(id) && units.TryGetValue(id, out var unit))
            {
                if (id == division) return true;
                if (unit.Kind == OrganizationUnitKind.BusinessDivision || unit.ParentUnitId is not Guid parent) return false;
                id = parent;
            }
            return false;
        }
        var targetUnits = units.Values.Where(unit => unit.Id == division || WithinDivision(unit.Id)
            && new[] { "项目组", "机械组", "电气组" }.Any(name => unit.Name.Contains(name, StringComparison.Ordinal))).Select(unit => unit.Id).ToHashSet();
        var active = directory.Users.Where(user => user.IsActive).Select(user => user.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var divisionLeaders = directory.Managers.Where(item => item.UnitId == division)
            .SelectMany(item => item.CollaborativeManagers.Prepend(item.PrimaryManager)).Where(active.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recipients = directory.Managers.Where(item => targetUnits.Contains(item.UnitId))
            .SelectMany(item => item.CollaborativeManagers.Prepend(item.PrimaryManager)).Where(active.Contains).Distinct(StringComparer.OrdinalIgnoreCase);
        var notifications = recipients.Select(recipient =>
        {
            var needsManager = divisionLeaders.Contains(recipient) && string.IsNullOrWhiteSpace(project.PrimaryProjectManager);
            return new UserNotification(Guid.NewGuid(), recipient, needsManager ? "ProjectStaffingPending" : "ProjectAssigned",
                needsManager ? "新项目待指定项目经理" : "新项目已分配到事业部",
                $"{project.Code} · {project.Name} 已分配至 {project.ExecutionUnitName}。" + (needsManager ? "请进入项目人员信息指定项目经理，主设计可稍后分配。" : "请查看项目信息，安排相关工作。"),
                project.Id, null, Key(project.Id, division, needsManager ? "manager" : "notice"), now, null);
        }).ToArray();
        await repository.UpsertUserNotificationsAsync(notifications, ct);
    }

    public static Task StaffedAsync(IPdmRepository repository, Project project, DateTimeOffset now, CancellationToken ct) =>
        project.ExecutionUnitId is Guid division && !string.IsNullOrWhiteSpace(project.PrimaryProjectManager)
            ? repository.ResolveUserNotificationsAsync([Key(project.Id, division, "manager")], now, ct)
            : Task.CompletedTask;
}
