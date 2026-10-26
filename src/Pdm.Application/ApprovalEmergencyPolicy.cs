using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public static class ApprovalEmergencyPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, IReadOnlyList<string>? roles,
        string actor, UserRole role, CancellationToken cancellationToken, bool businessInbox = false)
    {
        if (roles?.Count == 0) return false;
        if (businessInbox)
        {
            var recipient = await repository.FindUserAsync(actor, cancellationToken);
            if (recipient is not null && NotificationRecipientPolicy.HasSystemAdministrator(recipient.EffectiveRoleCodes))
                return recipient.IsActive && (roles is null
                    ? await NotificationRecipientPolicy.HasBusinessPermissionAsync(repository, recipient.EffectiveRoleCodes, PermissionCodes.ApprovalEmergencySubstitute, cancellationToken)
                    : roles.Intersect(NotificationRecipientPolicy.BusinessRoles(recipient.EffectiveRoleCodes), StringComparer.OrdinalIgnoreCase).Any())
                    && await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken);
        }
        if (roles is null) return await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)
            && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalEmergencySubstitute, cancellationToken);
        var user = await repository.FindUserAsync(actor, cancellationToken);
        if (user is null || !user.IsActive) return false;
        var actualRoles = user.EffectiveRoleCodes;
        return roles.Any(code => actualRoles.Contains(code, StringComparer.OrdinalIgnoreCase))
            && await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken);
    }
}
