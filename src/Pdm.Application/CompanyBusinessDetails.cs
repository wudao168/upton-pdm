using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record CompanyInvoiceDetails(string? Title, string? TaxNumber, string? Address, string? Phone, string? Bank, string? BankAccount);
public sealed record CompanyReceivingDetails(string? Recipient, string? Contact, string? Address);
public sealed record CompanyBusinessDetails(long RowVersion = 0, CompanyInvoiceDetails? Invoice = null, CompanyReceivingDetails? Receiving = null);
public sealed record CompanyBusinessDetailsStatus(Guid OrganizationId, bool InvoiceConfigured, bool ReceivingConfigured);

public sealed partial class PdmWorkflowService
{
    public async Task<IReadOnlyList<CompanyBusinessDetailsStatus>> ListCompanyBusinessDetailsAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, ct);
        var tenant = TenantContext.Current;
        if (tenant is { IsPlatformAdministrator: false } && tenant.CompanyId != tenant.PrimaryCompanyId) return [];
        return await repository.ListCompanyBusinessDetailsAsync(tenant is { IsPlatformAdministrator: false } ? tenant.PrimaryCompanyId : null, ct);
    }

    public async Task<CompanyBusinessDetails> ReadCompanyBusinessDetailsAsync(Guid organizationId, string kind, string actor, UserRole role, CancellationToken ct)
    {
        await RequireCompanyBusinessDetailsAsync(organizationId, kind, actor, role, ct);
        return await repository.ReadCompanyBusinessDetailsAsync(organizationId, kind, ct);
    }

    public async Task<CompanyBusinessDetails> SaveCompanyBusinessDetailsAsync(Guid organizationId, string kind, CompanyBusinessDetails input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireCompanyBusinessDetailsAsync(organizationId, kind, actor, role, ct);
        if (input.RowVersion < 0) throw new PdmRuleException("资料版本无效，请重新打开后保存。");
        var normalized = kind switch
        {
            "invoice" when input.Invoice is { } item && input.Receiving is null => input with
            {
                Invoice = new(Field(item.Title, 200, "开票抬头"), Field(item.TaxNumber, 50, "纳税人识别号"),
                    Field(item.Address, 300, "注册地址"), Field(item.Phone, 100, "注册电话"),
                    Field(item.Bank, 200, "开户银行"), Field(item.BankAccount, 80, "银行账号")),
            },
            "receiving" when input.Receiving is { } item && input.Invoice is null => input with
            {
                Receiving = new(Field(item.Recipient, 80, "收货人"), Field(item.Contact, 100, "联系方式"), Field(item.Address, 300, "收货地址")),
            },
            _ => throw new PdmRuleException("请填写当前类型的公司资料。"),
        };
        var saved = await repository.SaveCompanyBusinessDetailsAsync(organizationId, kind, normalized, ct);
        await AuditAsync(actor, "organization.business-details.update", nameof(ProjectOrganization), organizationId.ToString(),
            kind == "invoice" ? "更新公司开票资料" : "更新公司收货信息", ct);
        return saved;

        static string? Field(string? value, int maximum, string label)
        {
            value = value?.Trim();
            if (value?.Length > maximum) throw new PdmRuleException($"{label}最多{maximum}个字符。");
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private async Task RequireCompanyBusinessDetailsAsync(Guid organizationId, string kind, string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, ct);
        if (TenantContext.Current is { IsPlatformAdministrator: false })
        {
            RequirePrimaryCompany(organizationId);
            RequireActiveCompany(organizationId);
        }
        if (kind is not ("invoice" or "receiving")) throw new PdmNotFoundException("公司资料类型不存在。");
    }
}
