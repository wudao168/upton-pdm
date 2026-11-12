using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class AiAccess(IPdmRepository repository)
{
    public CurrentTenant Tenant => TenantContext.Current ?? throw new UnauthorizedAccessException("请重新登录并选择公司。");
    public async Task<bool> CanUseBusinessAsync(CancellationToken ct)
    {
        var original = Tenant; TenantContext.Set(original with { Authorization = null });
        try {
        if (!await AllowsAsync(AiPermissions.Chat, null, ct)) return false;
        var user = await repository.FindUserAsync(Tenant.Username, ct);
        return user is not null && AuthorizationPolicy.PotentialPermissions(await repository.GetAuthorizationSettingsAsync(ct), user, Tenant.CompanyId).Contains(AiPermissions.Business);
        } finally { TenantContext.Set(original); }
    }
    public async Task<bool> AllowsAsync(string permission, Guid? project, CancellationToken ct)
    {
        // Streaming requests must not keep the authorization snapshot captured at login/request start.
        var original = Tenant; TenantContext.Set(original with { Authorization = null });
        try {
        var user = await repository.FindUserAsync(Tenant.Username, ct);
        if (user is null || !user.IsActive) return false;
        var companyScope = await repository.GetUserCompanyScopeAsync(user.Username, ct);
        if (user.CompanyId != Tenant.CompanyId && (companyScope?.CrossCompanyView != true || !companyScope.AccessibleCompanyIds.Contains(Tenant.CompanyId))) return false;
        return project is Guid id ? await AuthorizationPolicy.CanAsync(repository, id, Tenant.Username, permission, ct)
            : AuthorizationPolicy.GlobalAllows(await repository.GetAuthorizationSettingsAsync(ct), user, Tenant.CompanyId, permission);
        } finally { TenantContext.Set(original); }
    }
    public async Task RequireAsync(string permission, Guid? project, CancellationToken ct)
    {
        if (!await AllowsAsync(permission, project, ct)) throw new UnauthorizedAccessException("当前账号未获授权或资料权限已撤销，请在权限中心核对当前公司及项目的授权。");
    }
    public async Task ReferencesAsync(IEnumerable<AiReference> references, CancellationToken ct)
    {
        foreach (var source in references.Distinct()) {
            if (source.Kind != "web") await RequireAsync(AiPermissions.Business, source.ProjectId, ct);
            foreach (var permission in source.Permissions) await RequireAsync(permission, source.ProjectId, ct);
        }
    }
    public Task HistoryAsync(AiConversation conversation, CancellationToken ct) => ReferencesAsync(conversation.Messages.SelectMany(m => m.Sources ?? []), ct);
}
