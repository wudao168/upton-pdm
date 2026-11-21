using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record UserAccountOperations(bool CanDeactivate, string? DeactivateReason, bool CanDelete, string? DeleteReason);
public sealed record ManagedUserEditActor(string Username, Guid? CompanyId);

public static class UserAccountAdministration
{
    public static Guid? ManagedCompanyId => TenantContext.CompanyId;

    public static bool Allows(AuthorizationSettings settings, UserAccount actor, Guid? companyId, string permission) =>
        companyId is Guid company
        && (actor.CompanyId == company || CanManagePlatform(settings, actor))
        && AuthorizationPolicy.GlobalAllows(settings, actor, company, permission);

    private static string? GetBlockReason(AuthorizationSettings settings, IReadOnlyList<UserAccount> accounts, UserAccount actor, UserAccount target,
        bool delete, bool allowAlreadyInactive = false)
    {
        if (actor.Id == target.Id) return delete ? "不能删除当前登录账号。" : "不能停用当前登录账号。";
        if (CanManagePlatform(settings, target with { IsActive = true }) && !CanManagePlatform(settings, actor))
            return "该账号具有平台管理权限，请由具备平台管理权限的管理员操作。";
        if (!delete && !target.IsActive && !allowAlreadyInactive) return "该账号已停用。";
        if (CanManagePermissions(settings, target) && !accounts.Any(user => user.Id != target.Id && CanManagePermissions(settings, user)))
            return "至少保留一位启用且能管理权限的人员。";
        if (delete && settings.Assignments.Any(item => string.Equals(item.Username, target.Username, StringComparison.OrdinalIgnoreCase)))
            return "该账号仍有权限中心的个人授权，请先核对授权范围。";
        return null;
    }

    public static UserAccount RequireOperation(AuthorizationSettings settings, IReadOnlyList<UserAccount> accounts,
        string actorName, string username, Guid? companyId, bool delete)
    {
        var permission = delete ? PermissionCodes.UserDelete : PermissionCodes.UserDeactivate;
        var actor = accounts.FirstOrDefault(user => string.Equals(user.Username, actorName, StringComparison.OrdinalIgnoreCase));
        if (actor is null || !Allows(settings, actor, companyId, permission))
            throw new UnauthorizedAccessException(delete ? "当前账号没有删除用户的权限。" : "当前账号没有停用用户的权限。");
        var target = accounts.FirstOrDefault(user => user.CompanyId == companyId && string.Equals(user.Username, username.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("当前公司内没有该账号。");
        if (GetBlockReason(settings, accounts, actor, target, delete, allowAlreadyInactive: true) is { } reason)
            throw new PdmRuleException(reason);
        return target;
    }

    public static void RequireEdit(AuthorizationSettings settings, IReadOnlyList<UserAccount> accounts, ManagedUserEditActor actor,
        string username, UserRole role, string roleCode, IReadOnlyList<string> roleCodes, bool isActive)
    {
        var current = accounts.FirstOrDefault(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("用户不存在。");
        if (current.IsActive && !isActive) RequireOperation(settings, accounts, actor.Username, username, actor.CompanyId, false);
        var candidate = current with { Role = role, RoleCode = roleCode, RoleCodes = roleCodes, IsActive = isActive };
        if (!accounts.Select(user => user.Id == current.Id ? candidate : user).Any(user => CanManagePermissions(settings, user)))
            throw new PdmRuleException("至少保留一位启用且能管理权限的人员。");
    }

    public static async Task<IReadOnlyDictionary<string, UserAccountOperations>> ListOperationsAsync(IPdmRepository repository,
        AuthorizationSettings settings, IReadOnlyList<UserAccount> accounts, UserAccount actor, Guid? companyId,
        bool checkDeleteReferences, CancellationToken ct)
    {
        var canDeactivate = Allows(settings, actor, companyId, PermissionCodes.UserDeactivate);
        var canDelete = Allows(settings, actor, companyId, PermissionCodes.UserDelete);
        if (!canDeactivate && !canDelete) return new Dictionary<string, UserAccountOperations>();

        var users = accounts.Where(user => user.CompanyId == companyId).ToArray();
        var deleteReasons = canDelete ? users.ToDictionary(user => user.Username, user => GetBlockReason(settings, accounts, actor, user, true), StringComparer.OrdinalIgnoreCase) : null;
        var references = checkDeleteReferences && deleteReasons is not null
            ? await repository.GetUserDeletionReferencesAsync(users.Where(user => deleteReasons[user.Username] is null).ToArray(), ct)
            : new Dictionary<string, string>();
        return users.ToDictionary(user => user.Username, user =>
        {
            var deactivateReason = canDeactivate ? GetBlockReason(settings, accounts, actor, user, false) : null;
            var deleteReason = deleteReasons?.GetValueOrDefault(user.Username) ?? references.GetValueOrDefault(user.Username);
            return new UserAccountOperations(canDeactivate && deactivateReason is null, deactivateReason,
                canDelete && checkDeleteReferences && deleteReason is null, deleteReason);
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanManagePermissions(AuthorizationSettings settings, UserAccount user) =>
        AuthorizationPolicy.GlobalAllows(settings, user, user.CompanyId, PermissionCodes.RoleSettingsView)
        && AuthorizationPolicy.GlobalAllows(settings, user, user.CompanyId, PermissionCodes.RoleSettingsEdit);

    private static bool CanManagePlatform(AuthorizationSettings settings, UserAccount user) =>
        AuthorizationPolicy.GlobalAllows(settings, user, user.CompanyId, AuthorizationActions.PlatformManage);
}
