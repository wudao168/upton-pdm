using System.Globalization;
using System.Text.RegularExpressions;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectNameplate(Guid ProjectId, bool Enabled = false, string PowerSupply = "", string Dimensions = "", string AirPressure = "", string Weight = "", DateOnly? FactoryDate = null, long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null)
{
    public string? ExecutionSavedBy { get; init; }
    public DateTimeOffset? ExecutionSavedAt { get; init; }
    public string? HardwareSavedBy { get; init; }
    public DateTimeOffset? HardwareSavedAt { get; init; }
    public Guid? ReleasePackageId { get; init; }
    public string? ReleasePackageNumber { get; init; }
    public string? ConfirmedBy { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
}
public sealed record SaveProjectNameplateCommand(bool Enabled, string PowerSupply, string Dimensions, string AirPressure, string Weight, DateOnly? FactoryDate, long ExpectedRowVersion);
public sealed record ConfirmProjectNameplateCommand(long ExpectedRowVersion);
public sealed record NameplateTemplate(string Title, IReadOnlyDictionary<string, string> Labels, long RowVersion = 0)
{
    public static NameplateTemplate Default => new("设备铭牌", new Dictionary<string, string> { ["name"] = "名称", ["model"] = "型号", ["serial"] = "序列号", ["powerSupply"] = "电源", ["dimensions"] = "尺寸", ["airPressure"] = "气压", ["weight"] = "重量", ["factoryDate"] = "出厂日期" });
}
public sealed record ProjectNameplateView(ProjectNameplate Nameplate, string Name, string Model, IReadOnlyList<string> SerialNumbers, NameplateTemplate Template, bool CanEdit, bool CanManageTemplate)
{
    public bool CanEditExecution { get; init; }
    public bool CanEditHardware { get; init; }
    public bool CanConfirm { get; init; }
}
public interface IProjectNameplateRepository
{
    Task<ProjectNameplate?> FindAsync(Guid projectId, CancellationToken ct);
    Task<ProjectNameplate> SaveAsync(ProjectNameplate value, long expectedRowVersion, CancellationToken ct);
    Task<ProjectNameplate> PublishAsync(ProjectNameplate value, ReleasePackage package, long expectedRowVersion, CancellationToken ct);
    Task<NameplateTemplate?> ReadTemplateAsync(CancellationToken ct);
    Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate value, long expectedRowVersion, CancellationToken ct);
}

public sealed class ProjectNameplateService(IPdmRepository repository, IProjectNameplateRepository nameplates, TimeProvider timeProvider,
    IProductionTrackingObserver? tracking = null, IReleaseDeliveryObserver? delivery = null)
{
    public async Task<ProjectNameplateView> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目铭牌。");
        var plate = await nameplates.FindAsync(projectId, ct) ?? new(projectId);
        var released = plate.ReleasePackageId.HasValue ? await repository.FindReleasePackageAsync(plate.ReleasePackageId.Value, ct) : null;
        var content = released?.NonStandardBomSnapshot.FirstOrDefault()?.NameplateContent;
        var permission = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectNameplateEdit, ct);
        var execution = permission && (role == UserRole.Administrator || project.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase));
        var hardware = permission && project.PhaseOwners.GetValueOrDefault("ElectricalHardware", "")
            .Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(actor, StringComparer.OrdinalIgnoreCase);
        return new(plate, content?.Name ?? project.Name, content?.Model ?? project.DeviceModel ?? "",
            released is null ? project.SerialNumbers : released.NonStandardBomSnapshot.Select(item => item.NameplateContent!.SerialNumber).ToArray(),
            await nameplates.ReadTemplateAsync(ct) ?? NameplateTemplate.Default,
            (execution || hardware || permission && role == UserRole.Administrator) && plate.ReleasePackageId is null,
            await repository.HasUserPermissionAsync(actor, role, PermissionCodes.NameplateTemplateManage, ct))
        {
            CanEditExecution = execution,
            CanEditHardware = hardware || permission && role == UserRole.Administrator,
            CanConfirm = hardware && plate.ReleasePackageId is null
        };
    }
    public async Task<ProjectNameplateView> SaveAsync(Guid projectId, SaveProjectNameplateCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var view = await GetAsync(projectId, actor, role, ct);
        if (view.Nameplate.ReleasePackageId is not null) throw new PdmConflictException("铭牌已确认发布，不能覆盖已发布的生产内容。");
        if (!view.CanEdit) throw new UnauthorizedAccessException("当前账号没有该项目铭牌的维护权限。");
        var values = new[] { command.PowerSupply, command.Dimensions, command.AirPressure, command.Weight };
        if (values.Any(value => value is null || value.Length > 100)) throw new PdmRuleException("铭牌字段最多100个字符。");
        var current = view.Nameplate;
        if (!view.CanEditExecution && (command.Dimensions.Trim() != current.Dimensions || command.AirPressure.Trim() != current.AirPressure
            || command.Weight.Trim() != current.Weight || command.FactoryDate != current.FactoryDate))
            throw new UnauthorizedAccessException("设备尺寸、重量、气源压力和出厂月份由该项目执行工程师填写。");
        if (!view.CanEditHardware && command.PowerSupply.Trim() != current.PowerSupply)
            throw new UnauthorizedAccessException("电压、频率和电流由该项目硬件工程师填写。");
        var now = timeProvider.GetUtcNow();
        var saved = await nameplates.SaveAsync(current with
        {
            Enabled = command.Enabled, PowerSupply = command.PowerSupply.Trim(), Dimensions = command.Dimensions.Trim(),
            AirPressure = command.AirPressure.Trim(), Weight = command.Weight.Trim(), FactoryDate = command.FactoryDate,
            UpdatedBy = actor, UpdatedAt = now,
            ExecutionSavedBy = view.CanEditExecution ? actor : current.ExecutionSavedBy,
            ExecutionSavedAt = view.CanEditExecution ? now : current.ExecutionSavedAt,
            HardwareSavedBy = view.CanEditHardware ? actor : current.HardwareSavedBy,
            HardwareSavedAt = view.CanEditHardware ? now : current.HardwareSavedAt
        }, command.ExpectedRowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.nameplate.update", nameof(Project), projectId.ToString(), $"铭牌{(saved.Enabled ? "启用" : "停用")} · 版本 {saved.RowVersion}"), ct);
        return await GetAsync(projectId, actor, role, ct);
    }

    public async Task<ProjectNameplateView> ConfirmAsync(Guid projectId, ConfirmProjectNameplateCommand command, string actor, UserRole role, CancellationToken ct)
    {
        var view = await GetAsync(projectId, actor, role, ct);
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!view.CanConfirm && !(view.Nameplate.ReleasePackageId.HasValue && project.PhaseOwners.GetValueOrDefault("ElectricalHardware", "")
            .Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(actor, StringComparer.OrdinalIgnoreCase)
            && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectNameplateEdit, ct)))
            throw new UnauthorizedAccessException("仅该项目指定的硬件工程师可以确认发布铭牌。");
        var plate = view.Nameplate;
        if (plate.ReleasePackageId is null)
        {
            if (plate.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("铭牌已被修改，请重新打开并核对后确认。");
            if (!plate.Enabled) throw new PdmRuleException("请先启用铭牌并填写完整内容。");
            if (string.IsNullOrWhiteSpace(plate.Dimensions) || string.IsNullOrWhiteSpace(plate.Weight)
                || string.IsNullOrWhiteSpace(plate.AirPressure) || plate.FactoryDate is null || plate.ExecutionSavedAt is null)
                throw new PdmRuleException("请执行工程师先保存完整的设备尺寸、重量、气源压力和出厂月份。");
            decimal ElectricalValue(string unit)
            {
                var match = Regex.Match(plate.PowerSupply, @"(\d+(?:\.\d+)?)\s*" + unit + @"\b", RegexOptions.IgnoreCase);
                if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > 1_000_000)
                    throw new PdmRuleException("请硬件工程师填写有效的电压、频率和电流后确认发布。");
                return value;
            }
            var voltage = ElectricalValue("V");
            _ = ElectricalValue("Hz");
            var current = ElectricalValue("A");
            var serials = view.SerialNumbers.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (string.IsNullOrWhiteSpace(view.Name) || string.IsNullOrWhiteSpace(view.Model) || serials.Length != Math.Max(1, project.Quantity))
                throw new PdmRuleException("请先完善项目的设备名称、型号及与设备数量一致的序列号。");
            var now = timeProvider.GetUtcNow();
            var id = Guid.NewGuid();
            var number = $"{project.Code}-MP-{now:yyyyMMddHHmmss}-{id.ToString("N")[..6]}";
            var items = serials.Select((serial, index) => new BomItem(Guid.NewGuid(), projectId, BomKind.NonStandard,
                index + 1, $"MP-{project.Code}-{serial}", "设备铭牌", 1, "001", null, serial, "A", true)
            {
                Source = "Nameplate",
                NameplateContent = new(view.Name, view.Model, serial, plate.PowerSupply, decimal.Round(voltage * current / 1000, 3),
                    plate.Dimensions, plate.AirPressure, plate.Weight, plate.FactoryDate.Value, actor, now)
            }).ToArray();
            var task = new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.HardwareEngineer, actor, actor,
                ApprovalDecision.Approved, "硬件填写完成并确认铭牌内容", now) { StepName = "硬件确认铭牌" };
            var package = new ReleasePackage(id, projectId, number, ReleasePackageState.Published, null, "", "", [task], now, now, null)
            {
                Scope = ReleaseScope.Nameplate, WorkflowCode = "Nameplate", WorkflowVersion = 1,
                NonStandardBomSnapshot = items, SelectedBomItemIds = items.Select(item => item.Id).ToArray(),
                LocksDocuments = false, CreatesManufacturingBaseline = false, VersionBindings = [], DrawingRequiredOn = project.DeliveryDate
            };
            plate = await nameplates.PublishAsync(plate with { ReleasePackageId = id, ReleasePackageNumber = number,
                ConfirmedBy = actor, ConfirmedAt = now, UpdatedBy = actor, UpdatedAt = now }, package, command.ExpectedRowVersion, ct);
            await repository.AppendAuditAsync(new(Guid.NewGuid(), now, actor, "project.nameplate.publish", nameof(Project),
                projectId.ToString(), $"铭牌确认发布 · {number} · {serials.Length}块"), ct);
        }
        if (tracking is not null) await tracking.PublishedAsync(plate.ReleasePackageId!.Value, ct);
        if (delivery is not null) await delivery.PublishedAsync(plate.ReleasePackageId!.Value, ct);
        return await GetAsync(projectId, actor, role, ct);
    }
    public async Task<NameplateTemplate> SaveTemplateAsync(NameplateTemplate command, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.NameplateTemplateManage, ct)) throw new UnauthorizedAccessException("当前账号没有铭牌模板维护权限。");
        if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 100 || command.Labels is null
            || command.Labels.Count != NameplateTemplate.Default.Labels.Count
            || NameplateTemplate.Default.Labels.Keys.Any(key => !command.Labels.TryGetValue(key, out var label) || string.IsNullOrWhiteSpace(label) || label.Length > 30)) throw new PdmRuleException("请填写模板标题及全部字段名称，标题最多100字，字段名称最多30字。");
        var saved = await nameplates.SaveTemplateAsync(command with { Title = command.Title.Trim(), Labels = command.Labels.ToDictionary(x => x.Key, x => x.Value.Trim()) }, command.RowVersion, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "nameplate.template.update", "NameplateTemplate", "default", $"铭牌模板 · 版本 {saved.RowVersion}"), ct);
        return saved;
    }
}
