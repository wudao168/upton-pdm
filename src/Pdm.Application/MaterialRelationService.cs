using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialRelationService(
    IMaterialRelationRepository relations,
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider) : IMaterialRelationReleaseGuard
{
    public async Task<IReadOnlyList<MaterialRelationTemplate>> ListTemplatesAsync(bool includeDraft, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationView, cancellationToken);
        return await relations.ListTemplatesAsync(includeDraft, cancellationToken);
    }

    public async Task<MaterialRelationTemplate> SaveDraftAsync(Guid? templateId, SaveMaterialRelationTemplateCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationManage, cancellationToken);
        var main = await RequireApprovedMaterialAsync(command.MainMaterialId, "主物料", cancellationToken);
        var name = Required(command.Name, "关联配置名称");
        if (command.Groups.Count == 0) throw new PdmRuleException("关联配置至少需要一个配件组。");

        var groups = new List<MaterialRelationGroup>();
        foreach (var (input, groupIndex) in command.Groups.OrderBy(item => item.SortOrder).Select((item, index) => (item, index)))
        {
            var groupName = Required(input.Name, "配件组名称");
            if (input.Options.Count == 0) throw new PdmRuleException($"配件组“{groupName}”至少需要一个可选物料。");
            if (input.Options.Select(item => item.MaterialId).Distinct().Count() != input.Options.Count)
                throw new PdmRuleException($"配件组“{groupName}”中存在重复物料。");
            var min = input.IsRequired ? Math.Max(1, input.MinSelection) : 0;
            var max = input.SelectionMode == MaterialRelationSelectionMode.Single ? 1 : input.MaxSelection;
            if (max.HasValue && (max.Value < min || max.Value > input.Options.Count))
                throw new PdmRuleException($"配件组“{groupName}”的最少/最多选择数量无效。");
            if (input.SelectionMode == MaterialRelationSelectionMode.Single && input.Options.Count(item => item.IsDefault) > 1)
                throw new PdmRuleException($"单选组“{groupName}”只能设置一个默认项。");

            var options = new List<MaterialRelationOption>();
            foreach (var (option, optionIndex) in input.Options.OrderBy(item => item.SortOrder).Select((item, index) => (item, index)))
            {
                var material = await RequireApprovedMaterialAsync(option.MaterialId, $"配件组“{groupName}”中的物料", cancellationToken);
                if (material.Id == main.Id) throw new PdmRuleException("主物料不能同时作为自己的关联配件。");
                if (material.Kind == MaterialKind.Product) throw new PdmRuleException($"成品“{material.MaterialCode}”不能作为BOM配件。");
                if (option.QuantityPerSet <= 0) throw new PdmRuleException($"配件“{material.MaterialCode}”的每套数量必须大于0。");
                options.Add(new MaterialRelationOption(Guid.NewGuid(), material.Id, material.MaterialCode, material.Name, material.Kind, material.UnitCode,
                    option.QuantityMode, option.QuantityPerSet, option.IsDefault, option.SortOrder == 0 ? optionIndex + 1 : option.SortOrder));
            }
            groups.Add(new MaterialRelationGroup(Guid.NewGuid(), groupName, input.IsRequired, input.SelectionMode, min, max,
                input.AutoSelectUnique, input.SortOrder == 0 ? groupIndex + 1 : input.SortOrder, options));
        }

        var saved = await relations.SaveDraftAsync(templateId, command with { Name = name }, main.MaterialCode, main.Name, groups, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material-relation.draft.save", saved.Id, $"{main.MaterialCode} · {saved.Name} · 草稿V{saved.DraftRevision?.Version}", cancellationToken);
        return saved;
    }

    public async Task<MaterialRelationTemplate> PublishAsync(Guid templateId, Guid revisionId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationPublish, cancellationToken);
        var template = (await relations.ListTemplatesAsync(true, cancellationToken)).FirstOrDefault(item => item.Id == templateId)
            ?? throw new PdmNotFoundException("关联配置不存在。");
        var draft = template.DraftRevision is { } current && current.Id == revisionId ? current : throw new PdmConflictException("待发布草稿不存在或已变化，请刷新后重试。");
        foreach (var option in draft.Groups.SelectMany(group => group.Options))
            _ = await RequireApprovedMaterialAsync(option.MaterialId, $"配件“{option.MaterialCode}”", cancellationToken);
        _ = await RequireApprovedMaterialAsync(template.MainMaterialId, "主物料", cancellationToken);
        var published = await relations.PublishAsync(templateId, revisionId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material-relation.publish", templateId, $"{template.MainMaterialCode} · V{published.PublishedRevision?.Version}", cancellationToken);
        return published;
    }

    public async Task<MaterialRelationCompleteness> GetCompletenessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationView, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的查看权限。");
        return await CalculateCompletenessAsync(projectId, cancellationToken);
    }

    public async Task<MaterialRelationCompleteness> ApplyAsync(Guid projectId, IReadOnlyList<ApplyMaterialRelationsCommand> commands, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var activePackage = (await repository.ListReleasePackagesAsync(projectId, cancellationToken)).FirstOrDefault(item =>
            item.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing);
        if (activePackage is not null) throw new PdmConflictException($"发布包{activePackage.ChangeNumber ?? activePackage.Number}正在审批或发布，BOM已锁定。");
        if (commands.Count == 0) throw new PdmRuleException("请选择需要配置的主物料。");
        if (commands.Select(item => item.MainBomItemId).Distinct().Count() != commands.Count)
            throw new PdmRuleException("同一主物料行不能重复提交。");

        var byKind = await LoadBomByKindAsync(projectId, cancellationToken);
        var allItems = byKind.Values.SelectMany(item => item).Where(IsActive).ToDictionary(item => item.Id);
        var templates = (await relations.ListTemplatesAsync(false, cancellationToken))
            .Where(item => item.PublishedRevision is not null && !item.IsArchived)
            .ToDictionary(item => item.MainMaterialCode, StringComparer.OrdinalIgnoreCase);
        var existingSelections = (await relations.ListSelectionsAsync(projectId, cancellationToken)).ToArray();
        var selectionsToSave = new Dictionary<Guid, IReadOnlyList<MaterialRelationSelection>>();

        foreach (var command in commands)
        {
            if (!allItems.TryGetValue(command.MainBomItemId, out var main)) throw new PdmNotFoundException("主物料BOM行不存在或已被排除。");
            if (!templates.TryGetValue(main.DrawingNumber.Trim(), out var template) || template.PublishedRevision is not { } revision)
                throw new PdmRuleException($"主物料“{main.DrawingNumber}”尚未发布生效的关联配置。");
            var choiceByGroup = command.Choices.GroupBy(item => item.GroupId).ToDictionary(group => group.Key, group => group.Last().OptionIds.Distinct().ToArray());
            var oldForMain = existingSelections.Where(item => item.MainBomItemId == main.Id).ToArray();
            foreach (var old in oldForMain)
                foreach (var list in byKind.Values) list.RemoveAll(item => item.Id == old.AccessoryBomItemId);

            var savedSelections = new List<MaterialRelationSelection>();
            foreach (var group in revision.Groups)
            {
                var selectedIds = choiceByGroup.GetValueOrDefault(group.Id) ?? [];
                if (selectedIds.Length == 0 && group.Options.Count == 1
                    && ((group.IsRequired && group.AutoSelectUnique) || group.Options[0].IsDefault))
                    selectedIds = [group.Options[0].Id];
                ValidateChoice(group, selectedIds);
                foreach (var optionId in selectedIds)
                {
                    var option = group.Options.FirstOrDefault(item => item.Id == optionId)
                        ?? throw new PdmRuleException($"配件组“{group.Name}”包含无效选项。");
                    var material = await RequireApprovedMaterialAsync(option.MaterialId, $"配件“{option.MaterialCode}”", cancellationToken);
                    var expected = option.QuantityMode == MaterialRelationQuantityMode.PerMainQuantity
                        ? main.Quantity * option.QuantityPerSet : option.QuantityPerSet;
                    var old = oldForMain.FirstOrDefault(item => item.GroupId == group.Id && item.OptionId == option.Id);
                    var accessoryId = old?.AccessoryBomItemId ?? Guid.NewGuid();
                    var kind = ToBomKind(material.Kind);
                    var target = byKind[kind];
                    target.Add(new BomItem(accessoryId, projectId, kind, target.Count + 1, material.MaterialCode, material.Name, expected,
                        U9UnitCatalog.NormalizeBomUnit(material.UnitCode), material.Material, material.Specification, "W1", true)
                    {
                        Remark = $"关联物料：{main.DrawingNumber} / {group.Name}",
                        Brand = material.Brand,
                        SurfaceTreatment = material.SurfaceTreatment,
                        Weight = material.Weight.HasValue ? material.Weight.Value.ToString("0.####") : null,
                        Source = "MaterialRelation",
                        IsManuallyRetained = true,
                        ReconciliationStatus = "RelationGenerated",
                        ReconciliationNote = $"由关联配置V{revision.Version}自动带入，来源主物料行{main.Sequence}。",
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = timeProvider.GetUtcNow()
                    });
                    savedSelections.Add(new MaterialRelationSelection(projectId, main.Id, accessoryId, revision.Id, group.Id, option.Id, expected, actor, timeProvider.GetUtcNow()));
                }
            }
            selectionsToSave[main.Id] = savedSelections;
        }

        static List<BomItem> Resequence(List<BomItem> items) => items.OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
            .Select((item, index) => item with { Sequence = index + 1 }).ToList();
        foreach (var kind in byKind.Keys.ToArray()) byKind[kind] = Resequence(byKind[kind]);
        var audit = new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material-relation.apply", nameof(BomItem), projectId.ToString(), $"主物料{commands.Count}项");
        await repository.ApplyBomBatchAsync(projectId, byKind[BomKind.Standard], byKind[BomKind.NonStandard], byKind[BomKind.Unclassified], byKind[BomKind.Electrical], byKind[BomKind.Virtual], [], [audit], cancellationToken);
        foreach (var entry in selectionsToSave)
            await relations.ReplaceSelectionsAsync(projectId, entry.Key, entry.Value, cancellationToken);
        foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Electrical })
        {
            var publishable = byKind[kind].Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded).ToArray();
            await repository.SaveBomDraftAsync(projectId, kind, publishable, actor, cancellationToken);
            if (publishable.Length > 0) await repository.SetBomEmptyDeclarationAsync(projectId, kind, false, actor, cancellationToken);
        }
        return await CalculateCompletenessAsync(projectId, cancellationToken);
    }

    public async Task EnsureCompleteAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await CalculateCompletenessAsync(projectId, cancellationToken);
        if (!result.IsComplete)
        {
            var details = result.MainMaterials.SelectMany(main => main.Groups.Where(group => !group.IsComplete)
                .Select(group => $"{main.MainMaterialCode}[行{main.MainBomItemId.ToString()[..8]}]/{group.GroupName}：{group.Status}"));
            throw new PdmRuleException($"关联物料完整性校验未通过，不能提交发布。{string.Join("；", details.Take(8))}");
        }
    }

    private async Task<MaterialRelationCompleteness> CalculateCompletenessAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var byKind = await LoadBomByKindAsync(projectId, cancellationToken);
        var all = byKind.Values.SelectMany(item => item).Where(IsActive).ToArray();
        var itemById = all.ToDictionary(item => item.Id);
        var templates = (await relations.ListTemplatesAsync(false, cancellationToken))
            .Where(item => item.PublishedRevision is not null && !item.IsArchived)
            .ToDictionary(item => item.MainMaterialCode, StringComparer.OrdinalIgnoreCase);
        var selections = (await relations.ListSelectionsAsync(projectId, cancellationToken)).ToArray();
        var checks = new List<MaterialRelationMainCheck>();
        foreach (var main in all.Where(item => templates.ContainsKey(item.DrawingNumber.Trim())))
        {
            var template = templates[main.DrawingNumber.Trim()];
            var revision = template.PublishedRevision!;
            var groupChecks = new List<MaterialRelationGroupCheck>();
            foreach (var group in revision.Groups)
            {
                var linked = selections.Where(item => item.MainBomItemId == main.Id && item.RevisionId == revision.Id && item.GroupId == group.Id).ToArray();
                var selected = linked.Select(item => item.OptionId).Distinct().ToArray();
                var expected = linked.Sum(item => item.ExpectedQuantity);
                var actual = linked.Where(item => itemById.ContainsKey(item.AccessoryBomItemId)).Sum(item => itemById[item.AccessoryBomItemId].Quantity);
                var status = ChoiceStatus(group, selected);
                if (status == "完整")
                {
                    foreach (var selection in linked)
                    {
                        var option = group.Options.FirstOrDefault(item => item.Id == selection.OptionId);
                        if (option is null || !itemById.TryGetValue(selection.AccessoryBomItemId, out var accessory)) { status = "关联配件行不存在"; break; }
                        var expectedForOption = option.QuantityMode == MaterialRelationQuantityMode.PerMainQuantity ? main.Quantity * option.QuantityPerSet : option.QuantityPerSet;
                        if (!string.Equals(accessory.DrawingNumber.Trim(), option.MaterialCode, StringComparison.OrdinalIgnoreCase)) { status = "关联配件与当前配置不一致"; break; }
                        if (accessory.Quantity != expectedForOption || selection.ExpectedQuantity != expectedForOption) { status = $"数量不匹配，应为{expectedForOption:0.####}"; break; }
                    }
                }
                groupChecks.Add(new MaterialRelationGroupCheck(group.Id, group.Name, group.IsRequired, group.SelectionMode, group.MaxSelection,
                    status == "完整", status, expected, actual, selected, group.Options));
            }
            checks.Add(new MaterialRelationMainCheck(main.Id, main.DrawingNumber, main.Name, main.Quantity, template.Id, revision.Id, revision.Version,
                groupChecks.All(group => group.IsComplete), groupChecks));
        }
        return new MaterialRelationCompleteness(projectId, checks.All(item => item.IsComplete), checks.Count,
            checks.Sum(item => item.Groups.Count(group => !group.IsComplete)), checks);
    }

    private async Task<Dictionary<BomKind, List<BomItem>>> LoadBomByKindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<BomKind, List<BomItem>>();
        foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified, BomKind.Electrical, BomKind.Virtual })
            result[kind] = (await repository.GetBomAsync(projectId, kind, cancellationToken)).ToList();
        return result;
    }

    private async Task<PdmMaterial> RequireApprovedMaterialAsync(Guid materialId, string label, CancellationToken cancellationToken)
    {
        var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException($"{label}不存在。");
        if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException($"{label}“{material.MaterialCode}”未批准或已停用。");
        return material;
    }

    private static string ChoiceStatus(MaterialRelationGroup group, IReadOnlyCollection<Guid> selected)
    {
        if (selected.Count < group.MinSelection) return selected.Count == 0 && group.Options.Count == 1 && group.AutoSelectUnique ? "可自动带出" : "缺少必选配件";
        if (group.MaxSelection.HasValue && selected.Count > group.MaxSelection.Value) return "选择数量超过上限";
        if (selected.Any(id => group.Options.All(option => option.Id != id))) return "存在失效选项";
        return "完整";
    }

    private static void ValidateChoice(MaterialRelationGroup group, IReadOnlyCollection<Guid> selected)
    {
        var status = ChoiceStatus(group, selected);
        if (status != "完整" && !(selected.Count == 0 && !group.IsRequired)) throw new PdmRuleException($"配件组“{group.Name}”：{status}。");
    }

    private static BomKind ToBomKind(MaterialKind kind) => kind switch
    {
        MaterialKind.Standard => BomKind.Standard,
        MaterialKind.NonStandard => BomKind.NonStandard,
        MaterialKind.Electrical => BomKind.Electrical,
        _ => throw new PdmRuleException("成品不能作为BOM配件。")
    };

    private static bool IsActive(BomItem item) => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval;
    private static string Required(string? value, string label) => string.IsNullOrWhiteSpace(value) ? throw new PdmRuleException($"{label}不能为空。") : value.Trim();

    private async Task RequireAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有执行此操作的权限。");
    }

    private Task AuditAsync(string actor, string action, Guid id, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, nameof(MaterialRelationTemplate), id.ToString(), detail), cancellationToken);
}
