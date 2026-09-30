using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectPermissionPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, string actor, UserRole role, string operation, CancellationToken cancellationToken)
    {
        if (!ProjectPermissionSettings.Operations.Contains(operation, StringComparer.Ordinal)
            || !await repository.HasUserPermissionAsync(actor, role, operation, cancellationToken)
            || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            return false;
        if (role == UserRole.Administrator || TenantContext.Current?.HasRole("developer") == true) return true;

        if (operation == PermissionCodes.ProjectDesignerAssign
            && (await repository.ListProjectsForUserAsync(actor, role, cancellationToken))
                .Any(project => project.Id == projectId && project.CanAssignDesigners))
            return true;

        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null) return false;
        var root = project.ParentProjectId is null ? project
            : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, cancellationToken);
        if (root is null) return false;
        var settings = await repository.GetProjectPermissionSettingsAsync(cancellationToken);
        return Allows(project, root, actor, settings, operation);
    }

    public static IReadOnlyList<string> AllowedOperations(Project project, Project root, string actor,
        IReadOnlySet<string> rolePermissions, ProjectPermissionSettings settings) =>
        ProjectPermissionSettings.Operations.Where(operation => rolePermissions.Contains(operation)
            && Allows(project, root, actor, settings, operation)).ToArray();

    public static bool Allows(Project project, Project root, string actor, ProjectPermissionSettings settings, string operation)
    {
        var positions = new List<string>();
        if (Equals(root.PrimaryProjectManager, actor) || root.CollaborativeProjectManagers.Any(user => Equals(user, actor)))
            positions.Add(ProjectPermissionPositions.MainManager);
        if (project.ParentProjectId is not null && Equals(project.PrimaryProjectManager, actor))
            positions.Add(ProjectPermissionPositions.ChildManager);
        if (Equals(root.DesignLead, actor) || root.DesignLeads.Any(user => Equals(user, actor)))
            positions.Add(ProjectPermissionPositions.MainDesigner);
        if (project.Designers.Any(user => Equals(user, actor)))
            positions.Add(ProjectPermissionPositions.Engineer);
        if (positions.Count == 0) positions.Add(ProjectPermissionPositions.Other);
        return positions.Any(position => settings.Rules.TryGetValue(position, out var permissions) && permissions.Contains(operation, StringComparer.Ordinal));
    }

    public static async Task RequireAsync(IPdmRepository repository, Guid projectId, string actor, UserRole role, string operation, CancellationToken cancellationToken)
    {
        if (!await CanAsync(repository, projectId, actor, role, operation, cancellationToken))
            throw new UnauthorizedAccessException("当前账号没有该项目的操作权限。");
    }

    private static bool Equals(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
