using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class QualityUploadPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, string kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return false;
        if (await ProjectPermissionPolicy.CanAsync(repository, projectId, actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken)) return true;
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var user = directory.Users.FirstOrDefault(x => x.IsActive && string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
        if (user is null) return false;
        if (user.EffectiveRoleCodes.Any(x => x is "QualityManager" or "QualityInspector")
            && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken)) return true;
        var units = directory.Units.Where(x => x.IsActive && directory.Memberships.Any(m => m.UnitId == x.Id && string.Equals(m.Username, actor, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (kind == "quality") return units.Any(x => x.Name.Contains("质量", StringComparison.OrdinalIgnoreCase));
        if (kind is "incoming" or "assembly") return user.EffectiveRoleCodes.Any(x => x is "ProductionManager" or "ProductionAssistant")
            || units.Any(x => x.Name.Contains("生产", StringComparison.OrdinalIgnoreCase) && directory.Managers.Any(m => m.UnitId == x.Id && (string.Equals(m.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || m.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase))));
        if (kind is "preAcceptance" or "finalAcceptance")
        {
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            var root = project?.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, cancellationToken);
            return user.EffectiveRoleCodes.Any(x => x is "TechnicalAssistant" or "ProjectAssistant")
                || new[] { project, root }.Any(x => x is not null && (string.Equals(x.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase) || x.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)));
        }
        return false;
    }
    public static async Task RequireAsync(IPdmRepository repository, Guid projectId, string kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await CanAsync(repository, projectId, kind, actor, role, cancellationToken)) throw new UnauthorizedAccessException("当前账号无权上传该类检验资料。");
    }
}
