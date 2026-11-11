using Upton.Pdm.Application;
using Upton.Pdm.Domain;

// 权限测试必须使用真实已注册账号，不能通过传入角色参数假冒管理员。
internal static class PermissionTestUsers
{
    public static async Task Ensure(IPdmRepository repository, params (string Name, UserRole Role)[] users)
    {
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(o => o.Id == Guid.Parse("70000000-0000-0000-0000-000000000001")).Id;
        foreach (var (name, role) in users)
            if (await repository.FindUserAsync(name, default) is null)
                await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", role, true, CompanyId: company), default);
    }

    public static async Task Grant(IPdmRepository repo, string role, params string[] permissions)
    {
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var current = settings.Templates.Single(t => t.RoleCode == role).Grants.Select(g => g.Permission);
        await repo.SaveAuthorizationSettingsAsync(AuthorizationPolicy.UpdateRole(settings, role, current.Concat(permissions).ToArray()), settings.Revision, default);
    }

    public static async Task Revoke(IPdmRepository repo, string role, string permission)
    {
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var codes = RolePermissionCatalog.Permissions.Where(p => p.Code == permission || p.LegacyPermission == permission).Select(p => p.Code).ToHashSet();
        await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(t => t.RoleCode == role ? t with { Grants = t.Grants.Where(g => !codes.Contains(g.Permission)).ToArray() } : t).ToArray() }, settings.Revision, default);
    }

    public static async Task Scope(IPdmRepository repo, string role, string permission, params string[] scopes)
    {
        await Grant(repo, role, permission);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(t => t.RoleCode == role ? t with { Grants = t.Grants.Select(g => g.Permission == permission ? g with { Scopes = scopes } : g).ToArray() } : t).ToArray() }, settings.Revision, default);
    }
}
