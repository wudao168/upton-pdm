using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectNameplate(Guid ProjectId, bool Enabled = false, string PowerSupply = "", string Dimensions = "", string AirPressure = "", string Weight = "", DateOnly? FactoryDate = null, long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null);
public sealed record SaveProjectNameplateCommand(bool Enabled, string PowerSupply, string Dimensions, string AirPressure, string Weight, DateOnly? FactoryDate, long ExpectedRowVersion);
public sealed record NameplateTemplate(string Title, IReadOnlyDictionary<string, string> Labels, long RowVersion = 0)
{
    public static NameplateTemplate Default => new("设备铭牌", new Dictionary<string, string> { ["name"] = "名称", ["model"] = "型号", ["serial"] = "序列号", ["powerSupply"] = "电源", ["dimensions"] = "尺寸", ["airPressure"] = "气压", ["weight"] = "重量", ["factoryDate"] = "出厂日期" });
}
public sealed record ProjectNameplateView(ProjectNameplate Nameplate, string Name, string Model, IReadOnlyList<string> SerialNumbers, NameplateTemplate Template, bool CanEdit, bool CanManageTemplate);
public interface IProjectNameplateRepository
{
    Task<ProjectNameplate?> FindAsync(Guid projectId, CancellationToken ct);
    Task<ProjectNameplate> SaveAsync(ProjectNameplate value, long expectedRowVersion, CancellationToken ct);
    Task<NameplateTemplate?> ReadTemplateAsync(CancellationToken ct);
    Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate value, long expectedRowVersion, CancellationToken ct);
}

public sealed class ProjectNameplateService(IPdmRepository repository, IProjectNameplateRepository nameplates, TimeProvider timeProvider)
{
    public async Task<ProjectNameplateView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目铭牌。");
        return new(await nameplates.FindAsync(projectId, ct) ?? new(projectId), project.Name, project.DeviceModel ?? "", project.SerialNumbers,
            await nameplates.ReadTemplateAsync(ct) ?? NameplateTemplate.Default,
            await CanEditAsync(project, actor, role, ct), CanManageTemplate(role));
    }
    public async Task<ProjectNameplateView> SaveAsync(Guid projectId, SaveProjectNameplateCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var view = await GetAsync(projectId, actor, role, ct);
        if (!view.CanEdit) throw new UnauthorizedAccessException("当前账号没有该项目铭牌的维护权限。");
        var values = new[] { command.PowerSupply, command.Dimensions, command.AirPressure, command.Weight };
        if (values.Any(value => value is null || value.Length > 100)) throw new PdmRuleException("铭牌字段最多100个字符。");
        var saved = await nameplates.SaveAsync(new(projectId, command.Enabled, command.PowerSupply.Trim(), command.Dimensions.Trim(), command.AirPressure.Trim(), command.Weight.Trim(), command.FactoryDate, UpdatedBy: actor, UpdatedAt: timeProvider.GetUtcNow()), command.ExpectedRowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.nameplate.update", nameof(Project), projectId.ToString(), $"铭牌{(saved.Enabled ? "启用" : "停用")} · 版本 {saved.RowVersion}"), ct);
        return await GetAsync(projectId, actor, role, ct);
    }
    public async Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate command, string actor, UserRole role, CancellationToken ct)
    {
        if (!CanManageTemplate(role)) throw new UnauthorizedAccessException("仅管理员可维护铭牌模板。");
        if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 100 || command.Labels is null
            || command.Labels.Count != NameplateTemplate.Default.Labels.Count
            || NameplateTemplate.Default.Labels.Keys.Any(key => !command.Labels.TryGetValue(key, out var label) || string.IsNullOrWhiteSpace(label) || label.Length > 30)) throw new PdmRuleException("请填写模板标题及全部字段名称，标题最多100字，字段名称最多30字。");
        var saved = await nameplates.SaveTemplateAsync(command with { Title = command.Title.Trim(), Labels = command.Labels.ToDictionary(x => x.Key, x => x.Value.Trim()) }, command.RowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "nameplate.template.update", "NameplateTemplate", "default", $"铭牌模板 · 版本 {saved.RowVersion}"), ct);
        return saved;
    }
    private async Task<bool> CanEditAsync(Project project, string actor, UserRole role, CancellationToken ct)
    {
        if (await ProjectPermissionPolicy.CanAsync(repository, project.Id, actor, role, PermissionCodes.ProjectEdit, ct)) return true;
        var roles = TenantContext.Current?.EffectiveRoleCodes
            ?? (await repository.FindUserAsync(actor, ct))?.EffectiveRoleCodes
            ?? [role.ToString()];
        if (roles.Any(code => code.Equals("HardwareEngineer", StringComparison.OrdinalIgnoreCase)
            || code.Equals(nameof(UserRole.ProcessReviewer), StringComparison.OrdinalIgnoreCase)
            || code.Equals(nameof(UserRole.Approver), StringComparison.OrdinalIgnoreCase))) return true;
        var root = project.ParentProjectId is null ? project
            : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct);
        if (string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase)
            || project.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || string.Equals(root?.DesignLead, actor, StringComparison.OrdinalIgnoreCase)
            || root?.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase) == true) return true;
        return roles.Contains(nameof(UserRole.Engineer), StringComparer.OrdinalIgnoreCase)
            && project.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase);
    }
    private static bool CanManageTemplate(UserRole role) => role == UserRole.Administrator || TenantContext.Current?.HasRole("developer") == true;
}
