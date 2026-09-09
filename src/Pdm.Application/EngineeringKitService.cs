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

    public async Task<EngineeringKit> SaveDraftAsync(Guid? kitId, SaveEngineeringKitDraftCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var name = Required(command.Name, 160, "套件名称");
        var description = Optional(command.Description, 500, "套件说明");
        var changeNote = Optional(command.ChangeNote, 500, "变更说明");
        var normalized = command.Components.OrderBy(item => item.SortOrder).ToArray();
        if (normalized.Length == 0) throw new PdmRuleException("套件至少需要一个必选子料。");
        if (!normalized.Any(item => !item.IsOptional)) throw new PdmRuleException("套件至少需要一个必选子料。");
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
            kit = new(id, null, name, description, null, [revision], actor, now, actor, now, 1);
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
            kit = current with { Name = name, Description = description, UpdatedBy = actor, UpdatedAt = now };
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
        if (draft.Components.Count == 0 || !draft.Components.Any(item => !item.IsOptional))
            throw new PdmRuleException("套件至少需要一个必选子料。");
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
