using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class EngineeringKitService(
    IEngineeringKitRepository kits,
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, releasedOnly ? PermissionCodes.StandardLibraryView : PermissionCodes.StandardLibraryManage, cancellationToken);
        return await kits.ListAsync(releasedOnly, cancellationToken);
    }

    public async Task<EngineeringKit> GetAsync(Guid kitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        return await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
    }

    /// <summary>套件型号自动生成用的标准代码/分类代码选项：查看权限即可读取。</summary>
    public async Task<EngineeringKitOptionCatalog> GetOptionCatalogAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        return settings.EngineeringKitOptions;
    }

    /// <summary>维护标准代码/分类代码选项：需要标准库管理权限。</summary>
    public async Task<EngineeringKitOptionCatalog> SaveOptionCatalogAsync(EngineeringKitOptionCatalog catalog, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var normalized = new EngineeringKitOptionCatalog(
            NormalizeCodes(catalog?.StandardCodes, "标准代码"),
            NormalizeCodes(catalog?.CategoryCodes, "分类代码"));
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        await repository.UpdateSystemSettingsAsync(settings with { EngineeringKitOptions = normalized }, cancellationToken);
        await AuditAsync(actor, "engineering-kit.options.update", Guid.Empty, $"标准代码{normalized.StandardCodes.Count}项、分类代码{normalized.CategoryCodes.Count}项", cancellationToken);
        return normalized;
    }

    private static IReadOnlyList<string> NormalizeCodes(IReadOnlyList<string>? values, string label)
    {
        var normalized = new List<string>();
        foreach (var value in values ?? [])
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.Length > 32) throw new PdmRuleException($"{label}不能超过32个字符。");
            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) normalized.Add(trimmed);
        }
        if (normalized.Count > 200) throw new PdmRuleException($"{label}最多维护200项。");
        return normalized;
    }

    public async Task<EngineeringKit> SaveDraftAsync(Guid? kitId, SaveEngineeringKitDraftCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var name = Required(command.Name, 160, "套件名称");
        var brand = Required(command.Brand, 160, "套件品牌");
        var description = Optional(command.Description, 500, "套件说明");
        var changeNote = Optional(command.ChangeNote, 500, "变更说明");
        // 型号可手动填写，也可按“标准代码-分类代码-序列号”在首次发布时自动生成。
        var manualModel = command.ModelMode == EngineeringKitModelMode.Manual
            ? Required(command.Model ?? string.Empty, 160, "套件型号")
            : Optional(command.Model, 160, "套件型号");
        var standardCode = Optional(command.StandardCode, 32, "标准代码");
        var categoryCode = Optional(command.CategoryCode, 32, "分类代码");
        if (command.ModelMode == EngineeringKitModelMode.Auto && manualModel is null)
        {
            // 已经生成过型号的套件（存量数据）允许不带代码继续维护；尚未生成型号的必须选好标准代码与分类代码。
            var existingModel = kitId is null ? null : (await kits.FindAsync(kitId.Value, cancellationToken))?.Model;
            if (string.IsNullOrWhiteSpace(existingModel))
            {
                if (standardCode is null) throw new PdmRuleException("选择自动生成型号时必须填写标准代码。");
                if (categoryCode is null) throw new PdmRuleException("选择自动生成型号时必须填写分类代码。");
            }
        }
        var normalized = command.Components.OrderBy(item => item.SortOrder).ToArray();
        if (normalized.Length == 0) throw new PdmRuleException("套件至少需要一个明细物料。");
        if (normalized.Any(item => item.IsOptional)) throw new PdmRuleException("套件明细全部为固定组成物料，不支持可选子料。");
        if (normalized.Any(item => item.Quantity <= 0)) throw new PdmRuleException("套件子料数量必须大于0。");
        if (normalized.GroupBy(item => item.MaterialId).Any(group => group.Count() > 1)) throw new PdmRuleException("同一真实物料在一个套件版本中只能出现一次。");

        var materialRows = new List<PdmMaterial>(normalized.Length);
        foreach (var component in normalized)
        {
            var material = await materials.FindMaterialAsync(component.MaterialId, cancellationToken)
                ?? throw new PdmRuleException("套件子料必须来自料品主档，不能引用另一个套件。");
            if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved || string.IsNullOrWhiteSpace(material.MaterialCode))
                throw new PdmRuleException($"物料“{material.Name}”不是有效的已批准真实料品。");
            materialRows.Add(material);
        }

        var now = timeProvider.GetUtcNow();
        EngineeringKit kit;
        EngineeringKitRevision revision;
        if (kitId is null)
        {
            var id = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            revision = new(revisionId, id, 1, EngineeringKitRevisionState.Draft, changeNote,
                Components(revisionId, normalized, materialRows), actor, now, null, null);
            kit = new(id, null, manualModel, name, brand, description, null, [revision], actor, now, actor, now, 1,
                command.ModelMode, standardCode, categoryCode);
        }
        else
        {
            var current = await kits.FindAsync(kitId.Value, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
            if (command.ExpectedRowVersion is null) throw new PdmRuleException("编辑套件时必须提供当前数据版本。");
            var draft = current.DraftRevision;
            var versionNumber = draft?.VersionNumber ?? current.Revisions.Select(item => item.VersionNumber).DefaultIfEmpty(0).Max() + 1;
            var revisionId = draft?.Id ?? Guid.NewGuid();
            revision = new(revisionId, current.Id, versionNumber, EngineeringKitRevisionState.Draft, changeNote,
                Components(revisionId, normalized, materialRows), draft?.CreatedBy ?? actor, draft?.CreatedAt ?? now, null, null);
            kit = current with
            {
                Model = manualModel,
                ModelMode = command.ModelMode,
                StandardCode = standardCode,
                CategoryCode = categoryCode,
                Name = name,
                Brand = brand,
                Description = description,
                UpdatedBy = actor,
                UpdatedAt = now
            };
        }

        var saved = await kits.SaveDraftAsync(kit, revision, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "engineering-kit.draft.save", saved.Id, $"{saved.Code ?? "待发布"} / V{revision.VersionNumber:D2}", cancellationToken);
        return saved;
    }

    public async Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var current = await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        var draft = current.DraftRevision ?? throw new PdmRuleException("套件没有待发布草稿。");
        if (draft.Components.Count == 0)
            throw new PdmRuleException("套件至少需要一个明细物料。");
        var saved = await kits.PublishAsync(kitId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "engineering-kit.publish", saved.Id, $"{saved.Code} / V{draft.VersionNumber:D2}", cancellationToken);
        return saved;
    }

    public async Task<EngineeringKitExpansion> ExpandAsync(Guid kitId, ExpandEngineeringKitCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        if (command.Quantity <= 0) throw new PdmRuleException("套件数量必须大于0。");
        var kit = await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        var revision = command.RevisionId is null
            ? kit.CurrentReleasedRevision
            : kit.Revisions.FirstOrDefault(item => item.Id == command.RevisionId.Value && item.State == EngineeringKitRevisionState.Released);
        if (revision is null || string.IsNullOrWhiteSpace(kit.Code)) throw new PdmRuleException("只能引用已发布的套件版本。");

        var selected = command.SelectedOptionalComponentIds.Distinct().ToHashSet();
        var optionalIds = revision.Components.Where(item => item.IsOptional).Select(item => item.Id).ToHashSet();
        if (selected.Any(id => !optionalIds.Contains(id))) throw new PdmRuleException("可选子料选择与套件版本不匹配，请刷新后重试。");
        var chosen = revision.Components.Where(item => !item.IsOptional || selected.Contains(item.Id)).OrderBy(item => item.SortOrder).ToArray();
        var materialRows = new Dictionary<Guid, PdmMaterial>();
        foreach (var component in chosen)
        {
            var material = await materials.FindMaterialAsync(component.MaterialId, cancellationToken)
                ?? throw new PdmRuleException($"套件子料“{component.MaterialCode}”已不存在。");
            if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved)
                throw new PdmRuleException($"套件子料“{component.MaterialCode}”当前不可引用。");
            materialRows[component.MaterialId] = material;
        }

        var lines = chosen.Select(component =>
        {
            var material = materialRows[component.MaterialId];
            return new EngineeringKitExpansionLine(component.Id, component.MaterialId, material.MaterialCode, material.Name,
                component.Quantity * command.Quantity, material.UnitCode, material.Material, material.Specification, material.Remark,
                material.Brand, material.SurfaceTreatment, material.Weight?.ToString(System.Globalization.CultureInfo.InvariantCulture), component.IsOptional);
        }).ToArray();
        return new(Guid.NewGuid(), kit.Id, revision.Id, kit.Code, kit.Name, revision.VersionNumber, command.Quantity, lines);
    }

    private static IReadOnlyList<EngineeringKitComponent> Components(
        Guid revisionId,
        IReadOnlyList<SaveEngineeringKitComponentCommand> commands,
        IReadOnlyList<PdmMaterial> materials) =>
        commands.Select((command, index) => new EngineeringKitComponent(
            Guid.NewGuid(), revisionId, command.MaterialId, materials[index].MaterialCode, materials[index].Name,
            command.Quantity, materials[index].UnitCode, command.IsOptional, index + 1)).ToArray();

    private static string Required(string value, int maxLength, string label)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) throw new PdmRuleException($"{label}不能为空。");
        if (normalized.Length > maxLength) throw new PdmRuleException($"{label}不能超过{maxLength}个字符。");
        return normalized;
    }

    private static string? Optional(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength) throw new PdmRuleException($"{label}不能超过{maxLength}个字符。");
        return normalized;
    }

    private async Task RequireAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行此操作。");
    }

    private Task AuditAsync(string actor, string action, Guid id, string summary, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, nameof(EngineeringKit), id.ToString(), summary), cancellationToken);
}
