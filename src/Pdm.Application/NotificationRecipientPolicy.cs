using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class NotificationRecipientPolicy
{
    public static IReadOnlyList<string> BusinessRoles(IEnumerable<string> roles) => roles
        .Where(role => !role.Equals(nameof(UserRole.Administrator), StringComparison.OrdinalIgnoreCase)
            && !role.Equals("platform_admin", StringComparison.OrdinalIgnoreCase)
            && !role.Equals(nameof(UserRole.PlatformAdministrator), StringComparison.OrdinalIgnoreCase)
            && !role.Equals("developer", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool HasSystemAdministrator(IEnumerable<string> roles) =>
        roles.Contains(nameof(UserRole.Administrator), StringComparer.OrdinalIgnoreCase);

    public static bool SystemOnly(IEnumerable<string> roles) =>
        HasSystemAdministrator(roles) && BusinessRoles(roles).Count == 0;

    public static async Task<bool> HasBusinessPermissionAsync(IPdmRepository repository, IEnumerable<string> roles, string permission, CancellationToken ct)
    {
        var businessRoles = BusinessRoles(roles);
        return (await repository.GetRolePermissionDirectoryAsync(ct)).Roles
            .Any(role => businessRoles.Contains(role.Role, StringComparer.OrdinalIgnoreCase) && role.Permissions.Contains(permission));
    }

    public static bool SystemOnly(UserAccount? user) => user is not null && SystemOnly(user.EffectiveRoleCodes);

    public static async Task<bool> InBusinessCompanyAsync(IPdmRepository repository, UserAccount user, Guid? companyId, CancellationToken ct)
    {
        if (companyId is null) return true;
        var scope = await repository.GetUserCompanyScopeAsync(user.Username, ct);
        return scope?.PrimaryCompanyId == companyId || user.CrossCompanyView && scope?.AccessibleCompanyIds.Contains(companyId.Value) == true;
    }

    public static bool IsSystemMessage(UserNotification notification) =>
        notification.Category.Equals("System", StringComparison.OrdinalIgnoreCase)
        && notification.ProjectId is null && notification.ReleasePackageId is null;

    public static async Task<IReadOnlyList<UserNotification>> FilterAsync(
        IPdmRepository repository, IReadOnlyList<UserNotification> notifications, CancellationToken ct)
    {
        var systemOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in notifications.Select(item => item.Recipient).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var user = await repository.FindUserAsync(recipient, ct);
            if (SystemOnly(user)) systemOnly.Add(recipient);
            if (user?.IsActive == false) inactive.Add(recipient);
        }
        return notifications.Where(item => !inactive.Contains(item.Recipient) && (!systemOnly.Contains(item.Recipient) || IsSystemMessage(item) || item.IsSenderReceipt && item.ReadAt is not null)).ToArray();
    }
}
