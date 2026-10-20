using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record SaveReleaseNotificationSettings(bool Enabled, string PublicUrl, IReadOnlyList<ReleaseNotificationRule> Rules,
    ReleaseMailSettings Mail, long ExpectedRowVersion);

public sealed class ReleaseNotificationSettingsService(IReleaseDeliveryStore store, IPdmRepository repository,
    IDataProtectionProvider protection, DingTalkConfigurationService dingTalk, TimeProvider clock) : IReleaseNotificationConfigurationProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protection.CreateProtector("Upton.Pdm.ReleaseNotification.Configuration.v1");
    public async Task<(ReleaseNotificationSettings Settings, long Version)> LoadAsync(Guid companyId, CancellationToken ct)
    {
        var record = await store.FindConfigurationAsync(companyId, ct);
        if (record is null) return (new(), 0);
        try { return (JsonSerializer.Deserialize<ReleaseNotificationSettings>(protector.Unprotect(record.ProtectedPayload), Json)!, record.RowVersion); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException)
        { throw new PdmRuleException("发布通知配置无法解密，请联系管理员检查服务器密钥备份。"); }
    }
    private async Task<Guid> RequireAdminAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) throw new UnauthorizedAccessException("只有系统设置管理员可以配置发布通知。");
        return TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。");
    }
    private async Task<object> ViewAsync(Guid companyId, ReleaseNotificationSettings settings, long version, string actor, UserRole role, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var application = await dingTalk.LoadAsync(companyId, ct);
        return new
        {
            settings.Enabled, settings.EnabledAt, settings.PublicUrl, settings.Rules, rowVersion = version,
            mail = settings.Mail with { Password = "" }, hasMailPassword = !string.IsNullOrWhiteSpace(settings.Mail.Password),
            departments = directory.Units.Where(unit => unit.IsActive && unit.OrganizationId == companyId).Select(unit => new { unit.Id, unit.Name, unit.ParentUnitId }),
            projects = (await repository.ListProjectsForUserAsync(actor, role, ct)).Where(project => project.OrganizationId == companyId).Select(project => new { project.Id, project.Code, project.Name }),
            groups = application.Groups.Where(group => !string.IsNullOrWhiteSpace(group.Value.OpenConversationId)).Select(group => new { id = group.Key, name = group.Value.Name })
        };
    }
    public async Task<object> GetAsync(string actor, UserRole role, CancellationToken ct)
    {
        var company = await RequireAdminAsync(actor, role, ct); var (settings, version) = await LoadAsync(company, ct);
        return await ViewAsync(company, settings, version, actor, role, ct);
    }
    public async Task<object> SaveAsync(SaveReleaseNotificationSettings input, string actor, UserRole role, CancellationToken ct)
    {
        var company = await RequireAdminAsync(actor, role, ct); var (old, version) = await LoadAsync(company, ct);
        if (version != input.ExpectedRowVersion) throw new PdmConflictException("发布通知配置已被修改，请刷新后重试。");
        if (input.Rules is null || input.Rules.Count > 100 || input.Rules.Any(rule => rule is null) || input.Rules.Select(rule => rule.Id).Distinct().Count() != input.Rules.Count)
            throw new PdmRuleException("通知规则重复或超过100条，请检查配置。");
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var departments = directory.Units.Where(unit => unit.IsActive && unit.OrganizationId == company).Select(unit => unit.Id).ToHashSet();
        var application = await dingTalk.LoadAsync(company, ct);
        foreach (var rule in input.Rules)
        {
            if (rule.Id == Guid.Empty || rule.BomKind is not ("All" or "Standard" or "NonStandard" or "Electrical")
                || rule.Event is not ("Published" or "Changed" or "Ready")) throw new PdmRuleException("请选择有效的BOM类别和通知事件。");
            if (rule.DepartmentIds is null || rule.DepartmentIds.Count == 0 || rule.DepartmentIds.Count > 100 || rule.DepartmentIds.Any(id => !departments.Contains(id)))
                throw new PdmRuleException("接收部门必须是当前公司的有效组织，请重新选择。");
            if (rule.Channels is null || rule.Channels.Any(channel => channel is not ("InApp" or "DingTalk" or "Email"))) throw new PdmRuleException("通知渠道无效。");
            if (rule.ProjectId is Guid projectId)
            {
                var project = await repository.FindProjectAsync(projectId, ct);
                if (project?.OrganizationId != company || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new PdmRuleException("通知规则中的项目不属于当前公司或当前管理员无权访问。");
            }
            if (rule.Channels.Contains("DingTalk") && (rule.DingTalkGroupId is not Guid group || !application.Enabled
                || !application.Groups.TryGetValue(group, out var target) || string.IsNullOrWhiteSpace(target.OpenConversationId) || string.IsNullOrWhiteSpace(application.RobotCode)))
                throw new PdmRuleException("请先在钉钉应用中配置企业机器人并登记接收群，再选择钉钉渠道。");
        }
        var mail = input.Mail ?? new();
        mail = mail with { Host = mail.Host ?? "", Username = mail.Username ?? "", Password = mail.Password ?? "", From = mail.From ?? "" };
        if (mail.Host.Length > 253 || mail.Username.Length > 200 || mail.Password.Length > 1000 || mail.From.Length > 254) throw new PdmRuleException("邮件配置超出允许长度。");
        mail = mail with { Password = string.IsNullOrEmpty(mail.Password) ? old.Mail.Password : mail.Password, Host = mail.Host.Trim(), From = mail.From.Trim() };
        if (input.Rules.Any(rule => rule.Channels.Contains("Email")))
        {
            if (Uri.CheckHostName(mail.Host) == UriHostNameType.Unknown || mail.Port is not (25 or 587 or 2525)
                || !MailAddress.TryCreate(mail.From, out var address) || address.Address != mail.From)
                throw new PdmRuleException("请填写邮件服务器和有效发件邮箱；邮件服务必须支持STARTTLS，端口为25、587或2525。");
        }
        var url = input.PublicUrl?.Trim().TrimEnd('/') ?? "";
        if (url.Length > 2048) throw new PdmRuleException("PLM访问地址过长，请填写系统入口地址。");
        if (input.Enabled && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))) throw new PdmRuleException("请填写业务客户端可访问的PLM地址，不含账号、查询参数或片段。");
        if (input.Enabled && input.Rules.Count == 0) throw new PdmRuleException("启用前请至少配置一条接收部门规则。");
        var settings = new ReleaseNotificationSettings { Enabled = input.Enabled, EnabledAt = input.Enabled ? old.Enabled ? old.EnabledAt : clock.GetUtcNow() : old.EnabledAt,
            PublicUrl = url, Mail = mail, Rules = input.Rules.Select(rule => rule with { DepartmentIds = rule.DepartmentIds.Distinct().ToArray(), Channels = rule.Channels.Append("InApp").Distinct().ToArray() }).ToArray() };
        var saved = await store.SaveConfigurationAsync(new(company, protector.Protect(JsonSerializer.Serialize(settings, Json)), version, settings.Enabled, settings.EnabledAt), version, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "settings.release-notification.save", "Company", company.ToString(), $"启用={settings.Enabled}；规则={settings.Rules.Count}；未记录邮件凭据，不补发启用前的发布。"), ct);
        return await ViewAsync(company, settings, saved.RowVersion, actor, role, ct);
    }
}
