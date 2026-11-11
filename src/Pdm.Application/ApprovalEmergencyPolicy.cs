using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public static class ApprovalEmergencyPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, IReadOnlyList<string>? roles,
        string actor, UserRole role, CancellationToken cancellationToken, bool businessInbox = false)
    {
        if (roles?.Count == 0 || !await AuthorizationPolicy.CanAsync(repository, projectId, actor, PermissionCodes.ApprovalEmergencySubstitute, cancellationToken)) return false;
        if (businessInbox && !await NotificationRecipientPolicy.CanReceiveAsync(repository, actor, cancellationToken)) return false;
        if (roles is null) return true;
        var user = await repository.FindUserAsync(actor, cancellationToken);
        if (user is null || !user.IsActive) return false;
        var actualRoles = user.EffectiveRoleCodes;
        return roles.Any(code => actualRoles.Contains(code, StringComparer.OrdinalIgnoreCase))
            && await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken);
    }
}
