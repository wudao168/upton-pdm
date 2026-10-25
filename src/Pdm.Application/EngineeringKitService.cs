using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class EngineeringKitService(
    IEngineeringKitRepository kits,
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider,
    IStandardLibraryRepository standardLibrary)
{
    public async Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, releasedOnly ? PermissionCodes.StandardLibraryView : PermissionCodes.StandardLibraryManage, cancellationToken);
        var rows = await kits.ListAsync(releasedOnly, cancellationToken);
        return releasedOnly ? rows.Select(ReleasedOnly).ToArray() : rows;
    }

    public async Task<EngineeringKit> GetAsync(Guid kitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        var kit = await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        if (await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken)) return kit;
        if (kit.CurrentReleasedRevisionId is null) throw new PdmNotFoundException("套件尚未发布。");
        return ReleasedOnly(kit);
    }

    private static EngineeringKit ReleasedOnly(EngineeringKit kit) => kit with
    { Revisions = kit.Revisions.Where(item => item.State == EngineeringKitRevisionState.Released).ToArray() };

    public async Task<EngineeringKit> SaveLibraryMetadataAsync(Guid kitId, SaveEngineeringKitLibraryMetadataCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权维护套件资料。");
        var kit = await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        if (kit.CurrentReleasedRevisionId is null) throw new PdmRuleException("套件发布后才能维护资料。");
        if (!kit.IsActive) throw new PdmRuleException("已停用套件不能维护资料，请先恢复启用。");
        var remark = string.IsNullOrWhiteSpace(command.Remark) ? null : command.Remark.Trim();
        var advice = string.IsNullOrWhiteSpace(command.SelectionAdvice) ? null : command.SelectionAdvice.Trim();
        if (remark?.Length > 500) throw new PdmRuleException("套件备注不能超过500个字符。");
        if (advice?.Length > 1000) throw new PdmRuleException("选型建议不能超过1000个字符。");
        if (command.ReferencePrice is < 0 or > 9999999999999999.99m) throw new PdmRuleException("参考价格须为有效的非负金额。");
        if (command.CoverImageAttachmentId is Guid coverId)
        {
            var cover = await kits.FindLibraryAttachmentAsync(coverId, cancellationToken);
            if (cover?.MaterialId != kitId || cover.Kind != MaterialAttachmentKind.CoverImage)
                throw new PdmRuleException("只能将本套件的图片附件设为封面。");
        }
        var saved = await kits.UpdateLibraryMetadataAsync(kit with { Description = remark, SelectionAdvice = advice,
            ReferencePrice = command.ReferencePrice, IsRecommended = command.IsRecommended, CoverImageAttachmentId = command.CoverImageAttachmentId,
            UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() }, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "engineering-kit.library-metadata.update", kitId, "维护套件备注、选型建议、参考价格、推荐和封面；组成版本保持不变", cancellationToken);
        return ReleasedOnly(saved);
    }

    public async Task<IReadOnlyList<EngineeringKit>> ListLibraryAsync(Guid? categoryId, string? query, string? brand, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(true, actor, role, cancellationToken);
        var categories = await standardLibrary.ListCategoriesAsync(true, cancellationToken);
        var scope = categoryId is null ? null : new HashSet<Guid> { categoryId.Value };
        if (scope is not null)
            while (true)
            {
                var children = categories.Where(item => item.ParentId.HasValue && scope.Contains(item.ParentId.Value) && !scope.Contains(item.Id)).ToArray();
                if (children.Length == 0) break;
                foreach (var child in children) scope.Add(child.Id);
            }
        return rows.Where(kit => (scope is null || kit.StandardLibraryCategoryId.HasValue && scope.Contains(kit.StandardLibraryCategoryId.Value))
            && (string.IsNullOrWhiteSpace(brand) || string.Equals(kit.Brand, brand.Trim(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(query) || new[] { kit.Code, kit.Name, kit.Model, kit.Brand }.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true))).ToArray();
    }

    public async Task<EngineeringKit> MoveCategoryAsync(Guid kitId, Guid categoryId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        await RequireCategoryAsync(categoryId, cancellationToken);
        var saved = await kits.MoveCategoryAsync(kitId, categoryId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "engineering-kit.category.move", kitId, $"套件归入标准分类：{categoryId}", cancellationToken);
        return saved;
    }

    public async Task<EngineeringKit> SetActiveAsync(Guid kitId, bool isActive, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var saved = await kits.SetActiveAsync(kitId, isActive, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, isActive ? "engineering-kit.activate" : "engineering-kit.deactivate", kitId,
            $"{(isActive ? "恢复启用" : "停用")}套件：{saved.Code ?? saved.Name}", cancellationToken);
        return saved;
    }

    private async Task<StandardLibraryCategory> RequireCategoryAsync(Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is null) throw new PdmRuleException("请选择标准物料分类。");
        var category = await standardLibrary.FindCategoryAsync(categoryId.Value, cancellationToken);
        if (category is null || !category.IsActive || StandardLibraryDefaults.IsUncategorized(category))
            throw new PdmRuleException("请选择已启用的具体标准物料分类。");
        return category;
    }

    private async Task<string?> RootCategoryCodeAsync(StandardLibraryCategory category, CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid> { category.Id };
        while (category.ParentId is Guid parentId)
        {
            if (!seen.Add(parentId)) throw new PdmRuleException("标准分类层级异常，请联系标准化维护。");
            category = await standardLibrary.FindCategoryAsync(parentId, cancellationToken)
                ?? throw new PdmRuleException("标准分类的上级分类不存在，请刷新后重试。");
        }
        return category.Code;
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

    private static IReadOnlyList<EngineeringKitOptionEntry> NormalizeCodes(IReadOnlyList<EngineeringKitOptionEntry>? entries, string label)
    {
        var normalized = new List<EngineeringKitOptionEntry>();
        foreach (var entry in entries ?? [])
        {
            var name = entry?.Name?.Trim();
            var code = entry?.Code?.Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(code)) continue;
            if (string.IsNullOrWhiteSpace(name)) throw new PdmRuleException($"{label}的分类名称不能为空。");
            if (string.IsNullOrWhiteSpace(code)) throw new PdmRuleException($"{label}“{name}”的代码不能为空。");
            if (name.Length > 60) throw new PdmRuleException($"{label}名称不能超过60个字符。");
            if (code.Length > 32) throw new PdmRuleException($"{label}代码不能超过32个字符。");
            if (normalized.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            normalized.Add(new EngineeringKitOptionEntry(name, code));
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
        var existing = kitId is null ? null : await kits.FindAsync(kitId.Value, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        var category = await RequireCategoryAsync(command.StandardLibraryCategoryId, cancellationToken);
        var manualModel = existing?.CurrentReleasedRevisionId is not null ? existing.Model : command.ModelMode == EngineeringKitModelMode.Manual
            ? Required(command.Model ?? string.Empty, 160, "套件型号")
            : null;
        string? standardCode = null, standardName = null;
        var categoryCode = await RootCategoryCodeAsync(category, cancellationToken);
        var categoryName = category.Name;
        if (command.ModelMode == EngineeringKitModelMode.Auto && manualModel is null)
        {
            EngineeringKitNumbering.Model(categoryCode, 1);
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
                Components(revisionId, normalized, materialRows), actor, now, null, null, category.Id);
            kit = new(id, null, manualModel, name, brand, description, null, [revision], actor, now, actor, now, 1,
                command.ModelMode, standardName, standardCode, categoryName, categoryCode);
        }
        else
        {
            var current = existing!;
            if (command.ExpectedRowVersion is null) throw new PdmRuleException("编辑套件时必须提供当前数据版本。");
            var draft = current.DraftRevision;
            var versionNumber = draft?.VersionNumber ?? current.Revisions.Select(item => item.VersionNumber).DefaultIfEmpty(0).Max() + 1;
            var revisionId = draft?.Id ?? Guid.NewGuid();
            revision = new(revisionId, current.Id, versionNumber, EngineeringKitRevisionState.Draft, changeNote,
                Components(revisionId, normalized, materialRows), draft?.CreatedBy ?? actor, draft?.CreatedAt ?? now, null, null, category.Id);
            kit = current with
            {
                Model = manualModel,
                ModelMode = current.CurrentReleasedRevisionId is not null ? current.ModelMode : command.ModelMode,
                StandardName = standardName,
                StandardCode = standardCode,
                CategoryName = categoryName,
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
        if (!current.IsActive) throw new PdmRuleException("套件已停用，请恢复启用后再发布。");
        var draft = current.DraftRevision ?? throw new PdmRuleException("套件没有待发布草稿。");
        if (draft.Components.Count == 0)
            throw new PdmRuleException("套件至少需要一个明细物料。");
        var category = await RequireCategoryAsync(draft.StandardLibraryCategoryId, cancellationToken);
        var categoryCode = await RootCategoryCodeAsync(category, cancellationToken);
        if (current.Code is null && current.ModelMode == EngineeringKitModelMode.Auto)
            EngineeringKitNumbering.Model(categoryCode, 1);
        var saved = await kits.PublishAsync(kitId, expectedRowVersion, actor, timeProvider.GetUtcNow(), categoryCode, cancellationToken);
        await AuditAsync(actor, "engineering-kit.publish", saved.Id, $"{saved.Code} / V{draft.VersionNumber:D2}", cancellationToken);
        return saved;
    }

    public async Task<EngineeringKitExpansion> ExpandAsync(Guid kitId, ExpandEngineeringKitCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        if (command.Quantity <= 0) throw new PdmRuleException("套件数量必须大于0。");
        var kit = await kits.FindAsync(kitId, cancellationToken) ?? throw new PdmNotFoundException("套件不存在。");
        if (!kit.IsActive) throw new PdmRuleException("套件已停用，请恢复启用后再引用。");
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
            command.Quantity, materials[index].UnitCode, command.IsOptional, index + 1, materials[index].Specification, materials[index].Brand, materials[index].Remark)).ToArray();

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
