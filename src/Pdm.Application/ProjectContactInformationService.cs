using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectContactInformation(Guid ProjectId, string CustomerContact = "", string CustomerPhone = "", string ShippingAddress = "", string ShippingContact = "", string ShippingPhone = "", long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null);
public sealed record SaveProjectContactInformationCommand(string CustomerContact, string CustomerPhone, string ShippingAddress, string ShippingContact, string ShippingPhone, long ExpectedRowVersion);
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
        return new(await contacts.FindAsync(root.Id, ct) ?? new(root.Id), root.CustomerName ?? "", CanEdit(root, actor, role));
    }

    public async Task<ProjectContactInformationView> SaveAsync(Guid projectId, SaveProjectContactInformationCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var root = await ReadRootAsync(projectId, actor, role, ct);
        if (!CanEdit(root, actor, role)) throw new UnauthorizedAccessException("仅主项目经理可维护客户及发货信息。");
        var shortFields = new[] { command.CustomerContact, command.CustomerPhone, command.ShippingContact, command.ShippingPhone };
        if (shortFields.Any(value => value is null || value.Length > 100) || command.ShippingAddress is null || command.ShippingAddress.Length > 500)
            throw new PdmRuleException("联系人和联系方式最多100个字符，收货地址最多500个字符。");
        var saved = await contacts.SaveAsync(new(root.Id, command.CustomerContact.Trim(), command.CustomerPhone.Trim(), command.ShippingAddress.Trim(), command.ShippingContact.Trim(), command.ShippingPhone.Trim(), UpdatedBy: actor, UpdatedAt: timeProvider.GetUtcNow()), command.ExpectedRowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.contact-information.update", nameof(Project), root.Id.ToString(), $"客户及发货信息 · 版本 {saved.RowVersion}"), ct);
        return new(saved, root.CustomerName ?? "", true);
    }

    private static bool CanEdit(Project root, string actor, UserRole role) =>
        role == UserRole.Administrator || TenantContext.Current?.HasRole("developer") == true
        || string.Equals(root.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
        || root.CollaborativeProjectManagers.Any(user => string.Equals(user, actor, StringComparison.OrdinalIgnoreCase));
}
