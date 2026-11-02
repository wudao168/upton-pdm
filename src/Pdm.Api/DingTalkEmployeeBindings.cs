using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed partial class DingTalkConfigurationService
{
    public string OfficeEntry(DingTalkOplOptions value) => string.IsNullOrWhiteSpace(value.PublicUrl) ? "" :
        value.PublicUrl.TrimEnd('/') + "/?dingtalkOffice=1&companyId=" + value.CompanyId;

    private async Task<OrganizationDirectoryUser[]> EmployeeAccountsAsync(Guid companyId, CancellationToken ct) =>
        (await projects.GetOrganizationDirectoryAsync(ct)).Users.Where(user => user.IsActive &&
            (user.CompanyId == companyId || user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)
             || user.EffectiveRoleCodes.Any(role => role is "developer" or "platform_admin")
             || user.CrossCompanyView && (user.AccessibleCompanyIds ?? []).Contains(companyId))).ToArray();

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
