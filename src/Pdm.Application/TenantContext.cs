namespace Upton.Pdm.Application;

public sealed record CurrentTenant(
    Guid UserId,
    Guid CompanyId,
    Guid PrimaryCompanyId,
    string Username,
    string RoleCode,
    bool CrossCompanyView,
    IReadOnlySet<string> Permissions)
{
    public bool HasPermission(string permission) => Permissions.Contains(permission);
    public bool IsPlatformAdministrator =>
        string.Equals(RoleCode, "platform_admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(RoleCode, "developer", StringComparison.OrdinalIgnoreCase);
}

public static class TenantContext
{
    private static readonly AsyncLocal<CurrentTenant?> CurrentTenant = new();

    public static CurrentTenant? Current => CurrentTenant.Value;

    public static Guid? CompanyId => CurrentTenant.Value?.CompanyId;

    public static void Set(CurrentTenant tenant) => CurrentTenant.Value = tenant;

    public static void Clear() => CurrentTenant.Value = null;
}
