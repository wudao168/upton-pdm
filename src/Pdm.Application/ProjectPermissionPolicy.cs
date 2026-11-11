using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectPermissionPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, string actor, UserRole role, string operation, CancellationToken cancellationToken)
    {
        return await AuthorizationPolicy.CanAsync(repository, projectId, actor, operation, cancellationToken);
    }

    public static IReadOnlyList<string> AllowedOperations(Project project, Project root, string actor,
        IReadOnlySet<string> rolePermissions, ProjectPermissionSettings settings)
    {
        if (!rolePermissions.Contains(PermissionCodes.ProjectContentView) || !IsValidProjectScope(project, root, actor))
            return [];
        // 预算查看沿用公司级角色授权或项目岗位授权；其他操作仍须同时具备角色与岗位权限。
        return RolePermissionCatalog.Permissions.Where(item => item.Scope == "project").Select(item => item.Code).Where(operation => operation == PermissionCodes.ProjectBudgetView
            ? rolePermissions.Contains(operation) || Allows(project, root, actor, settings, operation)
            : rolePermissions.Contains(operation) && (RolePermissionCatalog.Permissions.Single(item => item.Code == operation).ProjectAuthorization == "role" || Allows(project, root, actor, settings, operation))).ToArray();
    }

    public static bool Allows(Project project, Project root, string actor, ProjectPermissionSettings settings, string operation)
    {
        if (!ProjectPermissionSettings.Operations.Contains(operation, StringComparer.Ordinal)
            || !IsValidProjectScope(project, root, actor))
            return false;


        var positions = new List<string>();
        if (Equals(root.PrimaryProjectManager, actor) || root.CollaborativeProjectManagers.Any(user => Equals(user, actor)))
            positions.Add(ProjectPermissionPositions.MainManager);
        if (project.ParentProjectId is not null && Equals(project.PrimaryProjectManager, actor))
            positions.Add(ProjectPermissionPositions.ChildManager);
        if (Equals(root.DesignLead, actor) || root.DesignLeads.Any(user => Equals(user, actor)))
            positions.Add(ProjectPermissionPositions.MainDesigner);
        if (project.Designers.Any(user => Equals(user, actor))
            || IsAssignedEngineer(project, actor) || IsAssignedEngineer(root, actor))
            positions.Add(ProjectPermissionPositions.Engineer);
        foreach (var position in settings.CustomPositions ?? [])
            if (position.IsActive && (project.CustomPositionMembers.GetValueOrDefault(position.Key, []).Any(user => Equals(user, actor))
                || position.Scope == "subtree" && root.CustomPositionMembers.GetValueOrDefault(position.Key, []).Any(user => Equals(user, actor)))) positions.Add(position.Key);
        if (positions.Count == 0) positions.Add(ProjectPermissionPositions.Other);
        if (settings.SchemaVersion < 2 && RolePermissionCatalog.Permissions.Any(item => item.Code == operation && item.LegacyAllPositions)) return true;
        return positions.Any(position => settings.Rules.TryGetValue(position, out var permissions) && FunctionPermissionCatalog.Contains(FunctionPermissionCatalog.Expand(permissions), operation));
    }

    public static async Task RequireAsync(IPdmRepository repository, Guid projectId, string actor, UserRole role, string operation, CancellationToken cancellationToken)
    {
        if (!await CanAsync(repository, projectId, actor, role, operation, cancellationToken))
            throw new UnauthorizedAccessException("当前账号没有该项目的操作权限。");
    }

    public static bool CustomAllows(Project project, Project root, string actor, ProjectPermissionSettings settings, string operation)
    {
        if (!IsValidProjectScope(project, root, actor)) return false;
        return (settings.CustomPositions ?? []).Any(position => position.IsActive
            && (project.CustomPositionMembers.GetValueOrDefault(position.Key, []).Any(user => Equals(user, actor))
                || position.Scope == "subtree" && root.CustomPositionMembers.GetValueOrDefault(position.Key, []).Any(user => Equals(user, actor)))
            && settings.Rules.TryGetValue(position.Key, out var permissions) && FunctionPermissionCatalog.Contains(permissions.ToHashSet(StringComparer.Ordinal), operation));
    }

    private static bool Equals(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidProjectScope(Project project, Project root, string actor) =>
        !string.IsNullOrWhiteSpace(actor)
        && root.ParentProjectId is null
        && root.Id == (project.ParentProjectId is null ? project.Id : project.RootProjectId ?? project.ParentProjectId.Value)
        && project.OrganizationId == root.OrganizationId;

    private static bool IsAssignedEngineer(Project project, string actor) =>
        new[] { "ElectricalEngineer", "ElectricalHardware" }.Any(key => project.PhaseOwners.TryGetValue(key, out var owners)
            && owners.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(user => Equals(user, actor)));
}
