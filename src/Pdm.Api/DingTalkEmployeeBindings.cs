using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed partial class DingTalkConfigurationService
{
    public async Task<object> SelfBindingAsync(DingTalkEmployeePairing pairing, bool issue, bool qr, CancellationToken ct)
    {
        var (tenant, account, value) = await RequireSelfBindingAsync(ct);
        var bound = value.EmployeeBindings.Any(item => item.CorpId == value.CorpId
            && string.Equals(item.Username, tenant.Username, StringComparison.OrdinalIgnoreCase));
        if (!bound && qr && issue && (!Uri.TryCreate(value.PublicUrl, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo) || address.IsLoopback))
            throw new PdmRuleException("请让管理员在钉钉应用设置中填写手机可访问的 PLM 地址。");
        var employeeName = string.IsNullOrWhiteSpace(account.DisplayName) || string.Equals(account.DisplayName.Trim(), account.Username, StringComparison.OrdinalIgnoreCase)
            ? "未登记姓名" : account.DisplayName.Trim();
        var ticket = bound ? null : issue ? pairing.Issue(tenant, value, qr, employeeName) : pairing.Read(tenant);
        if (ticket is not null && ((ticket.ScanUrl is not null) != qr || !DingTalkEmployeePairing.Matches(ticket, value))) ticket = null;
        return new { bound, code = ticket?.Code, scanUrl = ticket?.ScanUrl, expiresAt = ticket?.ExpiresAt, dingTalkName = ticket?.Visitor?.Name };
    }

    public async Task ConfirmSelfBindingAsync(DingTalkEmployeePairing pairing, string code, CancellationToken ct)
    {
        var (tenant, _, value) = await RequireSelfBindingAsync(ct);
        var ticket = pairing.TakeVerified(tenant, code);
        var identity = ticket.Visitor!;
        for (var attempt = 0; ; attempt++)
        {
            if (!DingTalkEmployeePairing.Matches(ticket, value))
                throw new PdmConflictException("钉钉应用配置已更新，请在客户端重新生成绑定请求。");
            if (value.EmployeeBindings.Any(item => item.CorpId == value.CorpId
                && (item.UserId == identity.UserId || string.Equals(item.Username, tenant.Username, StringComparison.OrdinalIgnoreCase))))
                throw new PdmConflictException("该钉钉账号或当前UPLM账号已有绑定，请联系管理员核对。");
            if (value.EmployeeBindings.Count(item => item.CorpId == value.CorpId) >= 2000)
                throw new PdmRuleException("当前企业的员工绑定数量已达上限，请联系管理员。");
            value.EmployeeBindings.Add(new(value.CorpId, identity.UserId, tenant.Username));
            try
            {
                await PersistAsync(value, value.RowVersion, tenant.Username, "account.dingtalk.bind", ct);
                return;
            }
            catch (PdmConflictException) when (ticket.ScanUrl is not null && attempt < 2)
            {
                value = await LoadAsync(tenant.CompanyId, ct);
            }
        }
    }

    private async Task<(CurrentTenant Tenant, UserAccount Account, DingTalkOplOptions Config)> RequireSelfBindingAsync(CancellationToken ct)
    {
        var tenant = TenantContext.Current ?? throw new UnauthorizedAccessException("请先登录并选择当前公司。");
        var account = await projects.FindUserAsync(tenant.Username, ct);
        if (account is null || !account.IsActive || account.Id != tenant.UserId
            || !AuthorizationPolicy.GlobalAllows(await projects.GetAuthorizationSettingsAsync(ct), account, tenant.CompanyId, PermissionCodes.DingTalkSelfBind))
            throw new UnauthorizedAccessException("当前账号无权自助绑定钉钉，请联系管理员核对角色权限。");
        var config = await LoadAsync(tenant.CompanyId, ct);
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.CorpId) || string.IsNullOrWhiteSpace(config.AppKey) || string.IsNullOrWhiteSpace(config.AppSecret))
            throw new PdmRuleException("当前公司尚未启用钉钉移动办公，请联系管理员。");
        return (tenant, account, config);
    }

    public string OfficeEntry(DingTalkOplOptions value) => string.IsNullOrWhiteSpace(value.PublicUrl) ? "" :
        value.PublicUrl.TrimEnd('/') + "/?dingtalkOffice=1&companyId=" + value.CompanyId;

    private async Task<OrganizationDirectoryUser[]> EmployeeAccountsAsync(Guid companyId, CancellationToken ct)
    {
        var settings = await projects.GetAuthorizationSettingsAsync(ct);
        var accounts = (await projects.ListUsersAsync(ct)).ToDictionary(u => u.Username, StringComparer.OrdinalIgnoreCase);
        return (await projects.GetOrganizationDirectoryAsync(ct)).Users.Where(user => user.IsActive && (user.CompanyId == companyId
            || user.CrossCompanyView && (user.AccessibleCompanyIds ?? []).Contains(companyId)
            || accounts.TryGetValue(user.Username, out var account) && AuthorizationPolicy.GlobalAllows(settings, account, account.CompanyId, AuthorizationActions.PlatformManage))).ToArray();
    }

    private async Task<object> EmployeeBindingsViewAsync(DingTalkOplOptions value, CancellationToken ct) => new {
        value.CompanyId, value.CorpId, value.RowVersion, officeEntryUrl = OfficeEntry(value),
        bindings = value.EmployeeBindings.Where(item => item.CorpId == value.CorpId).ToArray(),
        accounts = (await EmployeeAccountsAsync(value.CompanyId, ct)).Select(user => new { user.Username, user.DisplayName }).ToArray()
    };

    public async Task<object> GetEmployeeBindingsAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireSystemAsync(actor, role, ct);
        return await EmployeeBindingsViewAsync(await LoadAsync(TenantContext.CompanyId, ct), ct);
    }

    public async Task<object> SaveEmployeeBindingsAsync(SaveDingTalkEmployeeBindings input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireSystemAsync(actor, role, ct);
        var value = await LoadAsync(TenantContext.CompanyId, ct);
        if (string.IsNullOrWhiteSpace(value.CorpId)) throw new PdmRuleException("请先保存企业CorpId，再配置员工绑定。");
        if (input.Bindings is null || input.Bindings.Count > 2000) throw new PdmRuleException("员工绑定资料不完整或数量过多。");
        var accounts = await EmployeeAccountsAsync(value.CompanyId, ct);
        var bindings = new List<DingTalkEmployeeBinding>();
        foreach (var item in input.Bindings)
        {
            var userId = item.UserId?.Trim() ?? "";
            var account = accounts.FirstOrDefault(user => string.Equals(user.Username, item.Username?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item.CorpId != value.CorpId || userId.Length is < 1 or > 200 || account is null)
                throw new PdmRuleException("请填写当前企业的钉钉用户ID，并选择有权访问当前公司的在职UPLM账号。");
            bindings.Add(new(value.CorpId, userId, account.Username));
        }
        if (bindings.Select(item => item.UserId).Distinct(StringComparer.Ordinal).Count() != bindings.Count
            || bindings.Select(item => item.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bindings.Count)
            throw new PdmRuleException("钉钉用户与UPLM账号须一一对应，请删除重复绑定。");
        value.EmployeeBindings = bindings;
        return await EmployeeBindingsViewAsync(await PersistAsync(value, input.ExpectedRowVersion, actor, "settings.dingtalk.employees.save", ct), ct);
    }
}
