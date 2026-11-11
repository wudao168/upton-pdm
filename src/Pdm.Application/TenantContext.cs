namespace Upton.Pdm.Application;

public sealed record CurrentTenant(
    Guid UserId,
    Guid CompanyId,
    Guid PrimaryCompanyId,
    string Username,
    string RoleCode,
    bool CrossCompanyView,
    IReadOnlySet<string> Permissions,
    IReadOnlyList<string>? RoleCodes = null)
{
    public Guid? ResourceProjectId { get; init; }
    public Upton.Pdm.Domain.AuthorizationSettings? Authorization { get; init; }
    public IReadOnlyList<string> EffectiveRoleCodes => (RoleCodes ?? [])
        .Prepend(RoleCode)
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool HasPermission(string permission) => Permissions.Contains(permission);
    public bool HasRole(string roleCode) => EffectiveRoleCodes.Contains(roleCode, StringComparer.OrdinalIgnoreCase);
    public bool IsPlatformAdministrator =>
        HasPermission(Upton.Pdm.Domain.AuthorizationActions.PlatformManage);
}

public static class TenantContext
{
    private static readonly AsyncLocal<CurrentTenant?> CurrentTenant = new();

    public static CurrentTenant? Current => CurrentTenant.Value;

    public static Guid? CompanyId => CurrentTenant.Value?.CompanyId;

    public static void Set(CurrentTenant tenant) => CurrentTenant.Value = tenant;

    public static void Clear() => CurrentTenant.Value = null;
}
