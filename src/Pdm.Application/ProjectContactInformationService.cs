using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectContactEntry(string Contact, string Phone, string Address, bool IsShippingAddress = false, string Position = "", string Remarks = "");
public sealed record ProjectContactInformation(Guid ProjectId, string CustomerContact = "", string CustomerPhone = "", string ShippingAddress = "", string ShippingContact = "", string ShippingPhone = "", long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null, IReadOnlyList<ProjectContactEntry>? Entries = null, string SalesPeople = "", string PresalesPeople = "");
public sealed record SaveProjectContactInformationCommand(string CustomerContact, string CustomerPhone, string ShippingAddress, string ShippingContact, string ShippingPhone, long ExpectedRowVersion, IReadOnlyList<ProjectContactEntry>? Entries = null, string SalesPeople = "", string PresalesPeople = "");
public sealed record ProjectContactInformationView(ProjectContactInformation Information, string CustomerName, bool CanEdit);
public interface IProjectContactInformationRepository
{
    Task<ProjectContactInformation?> FindAsync(Guid projectId, CancellationToken ct);
    Task<ProjectContactInformation> SaveAsync(ProjectContactInformation value, long expectedRowVersion, CancellationToken ct);
}

public sealed class ProjectContactInformationService(IPdmRepository repository, IProjectContactInformationRepository contacts, TimeProvider timeProvider)
{
    private async Task<Project> ReadRootAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("无权查看该项目信息。");
        return project.ParentProjectId is null ? project
            : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct) ?? throw new PdmNotFoundException("主项目不存在。");
    }

    public async Task<ProjectContactInformationView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var root = await ReadRootAsync(projectId, actor, role, ct);
        var information = await contacts.FindAsync(root.Id, ct) ?? new(root.Id);
        return new(information with { Entries = information.Entries ?? LegacyEntries(information.CustomerContact, information.CustomerPhone, information.ShippingAddress, information.ShippingContact, information.ShippingPhone) }, root.CustomerName ?? "", await CanEditAsync(actor, role, ct));
    }

    public async Task<ProjectContactInformationView> SaveAsync(Guid projectId, SaveProjectContactInformationCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var root = await ReadRootAsync(projectId, actor, role, ct);
        if (!await CanEditAsync(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有客户及发货信息维护权限。");
        var shortFields = new[] { command.CustomerContact, command.CustomerPhone, command.ShippingContact, command.ShippingPhone };
        if (shortFields.Any(value => value is null || value.Length > 100) || command.ShippingAddress is null || command.ShippingAddress.Length > 500)
            throw new PdmRuleException("联系人、联系方式和职位最多100个字符，收货地址和备注最多500个字符。");
        if (command.SalesPeople is null || command.PresalesPeople is null || command.SalesPeople.Length > 500 || command.PresalesPeople.Length > 500) throw new PdmRuleException("销售、售前人员最多500个字符。");
        var entries = (command.Entries ?? LegacyEntries(command.CustomerContact, command.CustomerPhone, command.ShippingAddress, command.ShippingContact, command.ShippingPhone)).ToArray();
        if (entries.Any(entry => entry is null || entry.Contact is null || entry.Phone is null || entry.Address is null
            || entry.Contact.Length > 100 || entry.Phone.Length > 100 || entry.Address.Length > 500
            || entry.Position is null || entry.Remarks is null || entry.Position.Length > 100 || entry.Remarks.Length > 500))
            throw new PdmRuleException("联系人、联系方式和职位最多100个字符，收货地址和备注最多500个字符。");
        entries = entries.Select(entry => entry with { Contact = entry.Contact.Trim(), Phone = entry.Phone.Trim(), Address = entry.Address.Trim(), Position = entry.Position.Trim(), Remarks = entry.Remarks.Trim() }).ToArray();
        if (entries.Any(entry => entry.IsShippingAddress && entry.Address.Length == 0)) throw new PdmRuleException("标记为收货地址的记录必须填写地址。");
        if (entries.Any(entry => entry.Contact.Length == 0 && entry.Phone.Length == 0 && entry.Address.Length == 0)) throw new PdmRuleException("请填写联系人信息或地址。");
        var saved = await contacts.SaveAsync(new(root.Id, command.CustomerContact.Trim(), command.CustomerPhone.Trim(), command.ShippingAddress.Trim(), command.ShippingContact.Trim(), command.ShippingPhone.Trim(), UpdatedBy: actor, UpdatedAt: timeProvider.GetUtcNow(), Entries: entries, SalesPeople: command.SalesPeople.Trim(), PresalesPeople: command.PresalesPeople.Trim()), command.ExpectedRowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.contact-information.update", nameof(Project), root.Id.ToString(), $"客户及发货信息 · 版本 {saved.RowVersion}"), ct);
        return new(saved, root.CustomerName ?? "", true);
    }

    private static IReadOnlyList<ProjectContactEntry> LegacyEntries(string contact, string phone, string address, string shippingContact, string shippingPhone)
    {
        var entries = new List<ProjectContactEntry>();
        if (!string.IsNullOrWhiteSpace(contact) || !string.IsNullOrWhiteSpace(phone)) entries.Add(new(contact, phone, ""));
        if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(shippingContact) || !string.IsNullOrWhiteSpace(shippingPhone))
            entries.Add(new(shippingContact, shippingPhone, address, !string.IsNullOrWhiteSpace(address)));
        return entries;
    }

    private Task<bool> CanEditAsync(string actor, UserRole role, CancellationToken ct) =>
        repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectContactEdit, ct);
}
