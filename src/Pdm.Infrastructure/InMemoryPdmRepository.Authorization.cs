using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private AuthorizationSettings? authorizationSettings;
    public async Task<AuthorizationSettings> GetAuthorizationSettingsAsync(CancellationToken ct)
    {
        if (authorizationSettings is not null) return authorizationSettings;
        var migrated = AuthorizationMigration.PreserveLegacyPlatformUser(AuthorizationMigration.Create(await GetRolePermissionDirectoryAsync(ct), await GetProjectPermissionSettingsAsync(ct)), await ListUsersAsync(ct));
        lock (gate)
        {
            if (authorizationSettings is not null) return authorizationSettings;
            var unified = AuthorizationPolicy.BindRoles(AuthorizationMigration.EnableAiWebDefaults(AuthorizationMigration.EnableDingTalkSelfBindingDefaults(migrated)));
            SyncAuthorizationRoles(migrated, unified);
            return authorizationSettings = unified;
        }
    }
    public Task<AuthorizationSettings> SaveAuthorizationSettingsAsync(AuthorizationSettings settings, long expectedRevision, CancellationToken ct, bool updateRoles = false)
    {
        lock (gate)
        {
            if (authorizationSettings?.Revision != expectedRevision) throw new PdmConflictException("权限配置已被其他人修改，请刷新后重新核对。");
            var next = (updateRoles ? AuthorizationPolicy.BindRoles(settings) : settings) with
            {
                Revision = expectedRevision + 1,
                RolesUnified = authorizationSettings.RolesUnified || updateRoles,
                DingTalkSelfBindingDefaultsApplied = authorizationSettings.DingTalkSelfBindingDefaultsApplied,
                AiWebDefaultsApplied = authorizationSettings.AiWebDefaultsApplied
            };
            if (updateRoles) SyncAuthorizationRoles(authorizationSettings, next);
            authorizationSettings = next;
            return Task.FromResult(authorizationSettings);
        }
    }

    private void SyncAuthorizationRoles(AuthorizationSettings previous, AuthorizationSettings next)
    {
        var codes = next.Templates.Select(t => t.RoleCode!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = previous.Templates.Select(t => t.RoleCode).OfType<string>()
            .Where(code => !codes.Contains(code) && roleDefinitions.ContainsKey(code)).ToArray();
        foreach (var code in removed)
            AuthorizationPolicy.CheckRoleDeletion(previous, next, code, roleDefinitions[code].IsSystem, users.Values.Any(u => u.HasRole(code)));
        foreach (var code in removed)
        {
            roleDefinitions.TryRemove(code, out _);
            rolePermissions.TryRemove(code, out _);
        }
        foreach (var code in codes) roleDefinitions.TryAdd(code, new(code, code, string.Empty, UserRole.ProductionViewer));
    }
}
