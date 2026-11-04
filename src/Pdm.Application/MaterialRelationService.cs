using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class MaterialRelationService(
    IMaterialRelationRepository relations,
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider, IStandardLibraryRepository? standardLibrary = null, IEngineeringKitRepository? kits = null) : IMaterialRelationReleaseGuard
{
    public async Task<IReadOnlyList<MaterialRelationMainMaterial>> SearchMainOptionsAsync(string? query, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationManage, ct);
        var result = (await materials.ListMaterialsAsync(query, null, false, 100, ct))
            .Where(material => !material.IsArchived && material.ApprovalStatus == MaterialApprovalStatus.Approved)
            .Select(material => new MaterialRelationMainMaterial(material.Id, material.MaterialCode, material.Name, material.Specification, material.Brand, material.Remark)).ToList();
        if (kits is not null)
            result.AddRange((await kits.ListAsync(true, ct)).Where(kit => kit.IsActive && kit.CurrentReleasedRevision is not null
                && (string.IsNullOrWhiteSpace(query) || new[] { kit.Code, kit.Name, kit.Model, kit.Brand }.Any(value => value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true)))
                .Select(KitMain));
        return result.OrderBy(main => main.MaterialCode).Take(100).ToArray();
    }

    public async Task<IReadOnlyList<MaterialRelationTemplate>> ListTemplatesAsync(bool includeDraft, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationView, cancellationToken);
        var manage = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialRelationManage, cancellationToken)
            || await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialRelationPublish, cancellationToken);
        return await WithCategoriesAsync(await relations.ListTemplatesAsync(includeDraft && manage, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<StandardLibraryCategory>> ListCategoryCatalogAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationView, ct);
        return standardLibrary is null ? [] : await standardLibrary.ListCategoriesAsync(false, ct);
    }

    public async Task<IReadOnlyList<StandardLibraryCategory>> GetMainCategoriesAsync(Guid materialId, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationView, ct);
        return await MainCategoriesAsync(materialId, ct);
    }

    public async Task<IReadOnlyList<StandardLibraryCategory>> AssignMainCategoryAsync(Guid materialId, Guid categoryId, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationManage, ct);
        var (main, syncReady) = await RequireMainAsync(materialId, ct);
        if (!syncReady) throw new PdmRuleException("主料尚未完成U9C同步，请先完成同步后补选标准物料分类。");
        if (standardLibrary is null) throw new PdmRuleException("标准物料分类服务尚未就绪，请联系管理员。");
        var current = await MainCategoriesAsync(materialId, ct);
        if (current.Count > 0)
        {
            if (current.Any(item => item.Id == categoryId)) return current;
            throw new PdmConflictException("主料已有标准物料分类，请在标准物料中维护归属，关联配置会自动继承。");
        }
        var category = await standardLibrary.FindCategoryAsync(categoryId, ct);
        if (category is null || !category.IsActive || StandardLibraryDefaults.IsUncategorized(category))
            throw new PdmRuleException("请选择有效的标准物料分类，不能选择未分类或已停用分类。");
        if (main.IsEngineeringKit)
        {
            var kit = (await kits!.FindAsync(materialId, ct))!;
            await kits.MoveCategoryAsync(kit.Id, category.Id, kit.RowVersion, actor, timeProvider.GetUtcNow(), ct);
            await AuditAsync(actor, "material-relation.category.assign", materialId, $"套件收录标准物料分类：{category.Name}", ct);
            return await MainCategoriesAsync(materialId, ct);
        }
        await standardLibrary.AddMembershipsAsync([new(category.Id, materialId, actor, timeProvider.GetUtcNow())], ct);
        foreach (var value in (await standardLibrary.ListCategoriesAsync(true, ct)).Where(StandardLibraryDefaults.IsUncategorized))
            await standardLibrary.RemoveMembershipAsync(value.Id, materialId, ct);
        await AuditAsync(actor, "material-relation.category.assign", materialId, $"主料收录标准物料分类：{category.Name}；关联配置继承主料归属", ct);
        return await MainCategoriesAsync(materialId, ct);
    }

    private async Task<IReadOnlyList<StandardLibraryCategory>> MainCategoriesAsync(Guid materialId, CancellationToken ct)
    {
        if (standardLibrary is null) return [];
        if (kits is not null && await kits.FindAsync(materialId, ct) is { } kit)
            return (await standardLibrary.ListCategoriesAsync(false, ct)).Where(category => category.Id == kit.StandardLibraryCategoryId && !StandardLibraryDefaults.IsUncategorized(category)).ToArray();
        var ids = (await standardLibrary.ListMembershipsAsync(ct)).Where(item => item.MaterialId == materialId).Select(item => item.CategoryId).ToHashSet();
        return (await standardLibrary.ListCategoriesAsync(false, ct)).Where(item => ids.Contains(item.Id) && !StandardLibraryDefaults.IsUncategorized(item)).ToArray();
    }

    private async Task<IReadOnlyList<MaterialRelationTemplate>> WithCategoriesAsync(IReadOnlyList<MaterialRelationTemplate> values, CancellationToken ct)
    {
        if (standardLibrary is null || values.Count == 0) return values;
        var categories = (await standardLibrary.ListCategoriesAsync(false, ct)).Where(item => !StandardLibraryDefaults.IsUncategorized(item)).ToDictionary(item => item.Id);
        var memberships = (await standardLibrary.ListMembershipsAsync(ct)).ToLookup(item => item.MaterialId);
        var kitById = kits is null ? new Dictionary<Guid, EngineeringKit>() : (await kits.ListAsync(false, ct)).ToDictionary(kit => kit.Id);
        MaterialRelationMainMaterial Enrich(MaterialRelationMainMaterial main) => kitById.TryGetValue(main.MaterialId, out var kit)
            ? main with { IsEngineeringKit = true, StandardLibraryCategories = kit.StandardLibraryCategoryId is Guid categoryId && categories.TryGetValue(categoryId, out var category) ? [category] : [] }
            : main with { StandardLibraryCategories = memberships[main.MaterialId].Select(link => categories.GetValueOrDefault(link.CategoryId)).OfType<StandardLibraryCategory>().OrderBy(category => category.SortOrder).ThenBy(category => category.Name).ToArray() };
        return values.Select(item =>
        {
            var enriched = item with {
                DraftRevision = item.DraftRevision is { } draft ? draft with { MainMaterials = item.MainMaterials.Select(Enrich).ToArray() } : null,
                PublishedRevision = item.PublishedRevision is { } published ? published with { MainMaterials = item.PublishedMainMaterials.Select(Enrich).ToArray() } : null };
            return enriched with { StandardLibraryCategories = enriched.MainMaterials.SelectMany(main => main.StandardLibraryCategories).DistinctBy(category => category.Id).ToArray() };
        }).ToArray();
    }

    public async Task<MaterialRelationTemplate> SaveDraftAsync(Guid? templateId, SaveMaterialRelationTemplateCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationManage, cancellationToken);
        var templates = await relations.ListTemplatesAsync(true, cancellationToken);
        var existing = templateId.HasValue ? templates.FirstOrDefault(item => item.Id == templateId) : command.MainMaterialIds is null
            ? templates.FirstOrDefault(item => item.MainMaterials.Concat(item.PublishedMainMaterials).Any(main => main.MaterialId == command.MainMaterialId)) : null;
        if (existing is not null) templateId = existing.Id;
        var mainIds = (command.MainMaterialIds ?? existing?.MainMaterials.Select(item => item.MaterialId).ToArray() ?? [command.MainMaterialId]).Distinct().ToArray();
        if (mainIds.Length == 0 || mainIds.Contains(Guid.Empty)) throw new PdmRuleException("请至少选择一个已批准的主物料。");
        var mainRows = new List<MaterialRelationMainMaterial>();
        foreach (var id in mainIds)
        {
            var (material, _) = await RequireMainAsync(id, cancellationToken);
            if (templates.FirstOrDefault(item => item.Id != templateId && !item.IsArchived && item.MainMaterials.Concat(item.PublishedMainMaterials).Any(main => main.MaterialId == id)) is { } conflict)
                throw new PdmConflictException($"主料{material.MaterialCode}已归属配置“{conflict.Name}”，请先调整原配置的主料范围并发布。");
            mainRows.Add(material);
        }
        var main = mainRows[0];
        var name = Required(command.Name, "关联配置名称");
        if (name.Length > 160) throw new PdmRuleException("关联配置名称不能超过160个字符。");
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
                if (mainIds.Contains(material.Id)) throw new PdmRuleException($"主物料{material.MaterialCode}不能同时作为本组的关联配件。");
                if (material.Kind == MaterialKind.Product) throw new PdmRuleException($"成品“{material.MaterialCode}”不能作为BOM配件。");
                if (option.QuantityPerSet <= 0) throw new PdmRuleException($"配件“{material.MaterialCode}”的每套数量必须大于0。");
                U9UnitCatalog.RequireWholeQuantity(option.QuantityPerSet, material.UnitCode, $"配件“{material.MaterialCode}”的每套数量");
                options.Add(new MaterialRelationOption(Guid.NewGuid(), material.Id, material.MaterialCode, material.Name, material.Kind, material.UnitCode,
                    option.QuantityMode, option.QuantityPerSet, option.IsDefault, option.SortOrder == 0 ? optionIndex + 1 : option.SortOrder,
                    string.IsNullOrWhiteSpace(option.SelectionAdvice) ? null : option.SelectionAdvice.Trim(), material.Specification, material.Brand));
            }
            groups.Add(new MaterialRelationGroup(Guid.NewGuid(), groupName, input.IsRequired, input.SelectionMode, min, max,
                input.AutoSelectUnique, input.SortOrder == 0 ? groupIndex + 1 : input.SortOrder, options));
        }

        var saved = await relations.SaveDraftAsync(templateId, command with { MainMaterialId = main.MaterialId, MainMaterialIds = mainIds, Name = name }, main.MaterialCode, main.MaterialName, groups, actor, timeProvider.GetUtcNow(), cancellationToken, mainRows);
        await AuditAsync(actor, "material-relation.draft.save", saved.Id, $"{main.MaterialCode} · {saved.Name} · 草稿V{saved.DraftRevision?.Version}", cancellationToken);
        return (await WithCategoriesAsync([saved], cancellationToken)).Single();
    }

    public async Task<MaterialRelationTemplate> PublishAsync(Guid templateId, Guid revisionId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.MaterialRelationPublish, cancellationToken);
        var template = (await relations.ListTemplatesAsync(true, cancellationToken)).FirstOrDefault(item => item.Id == templateId)
            ?? throw new PdmNotFoundException("关联配置不存在。");
        var draft = template.DraftRevision is { } current && current.Id == revisionId ? current : throw new PdmConflictException("待发布草稿不存在或已变化，请刷新后重试。");
        foreach (var option in draft.Groups.SelectMany(group => group.Options))
            _ = await RequireApprovedMaterialAsync(option.MaterialId, $"配件“{option.MaterialCode}”", cancellationToken);
        foreach (var value in template.MainMaterials)
        {
            var (_, syncReady) = await RequireMainAsync(value.MaterialId, cancellationToken);
            if (standardLibrary is null) continue;
            if ((await MainCategoriesAsync(value.MaterialId, cancellationToken)).Count == 0)
                throw new PdmRuleException($"主料{value.MaterialCode}尚未归入标准物料分类，请由标准化人员补选分类后再发布关联配置。");
            if (!syncReady) throw new PdmRuleException($"主料{value.MaterialCode}尚未完成U9C同步，请完成同步后再发布关联配置。");
        }
        var published = await relations.PublishAsync(templateId, revisionId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material-relation.publish", templateId, $"{template.MainMaterialCode} · V{published.PublishedRevision?.Version}", cancellationToken);
        return (await WithCategoriesAsync([published], cancellationToken)).Single();
    }

    public async Task<MaterialRelationCompleteness> GetCompletenessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken, Guid? releasePackageId = null)
    {
        await RequireAsync(actor, role, releasePackageId.HasValue ? PermissionCodes.ReleaseManage : PermissionCodes.MaterialRelationView, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的查看权限。");
        if (!releasePackageId.HasValue) return await CalculateCompletenessAsync(projectId, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId.Value, cancellationToken) ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.ProjectId != projectId) throw new UnauthorizedAccessException("无权查看该发布包的关联核对结果。");
        return await CalculateCompletenessAsync(projectId, cancellationToken, ReleaseItemIds(package));
    }

    internal static IReadOnlyCollection<Guid> ReleaseItemIds(ReleasePackage package) => package.SelectedBomItemIds.Count > 0 || package.ConfirmedRemovedBomItemIds.Count > 0
        ? package.SelectedBomItemIds.Concat(package.ConfirmedRemovedBomItemIds).Distinct().ToArray()
        : package.StandardBomSnapshot.Concat(package.NonStandardBomSnapshot).Concat(package.ElectricalBomSnapshot).Concat(package.MechanicalBomSnapshot)
            .Select(item => item.Id).Distinct().ToArray();

    public async Task<MaterialRelationCompleteness> ApplyAsync(Guid projectId, IReadOnlyList<ApplyMaterialRelationsCommand> commands, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var activePackage = (await repository.ListReleasePackagesAsync(projectId, cancellationToken)).FirstOrDefault(item =>
            item.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing);
        if (activePackage is not null) throw new PdmConflictException($"发布包{activePackage.ChangeNumber ?? activePackage.Number}正在审批或发布，BOM已锁定。");
        if (commands.Count == 0) throw new PdmRuleException("请选择需要配置的主物料。");
        if (commands.Select(item => item.MainBomItemId).Distinct().Count() != commands.Count)
            throw new PdmRuleException("同一主物料行不能重复提交。");

        var byKind = await LoadBomByKindAsync(projectId, cancellationToken);
        var activeItems = byKind.Values.SelectMany(item => item).Where(IsActive).ToArray();
        var templates = (await relations.ListTemplatesAsync(false, cancellationToken))
            .Where(item => item.PublishedRevision is not null && !item.IsArchived)
            .SelectMany(item => item.PublishedMainMaterials.Select(main => (main.MaterialCode, Template: item))).ToDictionary(item => item.MaterialCode, item => item.Template, StringComparer.OrdinalIgnoreCase);
        var existingSelections = (await relations.ListSelectionsAsync(projectId, cancellationToken)).ToArray();
        var allItems = (await RelationMainsAsync(activeItems, templates.Keys, cancellationToken)).ToDictionary(item => item.Id);
        var selectionsToSave = new Dictionary<Guid, IReadOnlyList<MaterialRelationSelection>>();
        var reviewsToSave = new Dictionary<Guid, IReadOnlyList<MaterialRelationReview>>();
        var affectedKinds = new HashSet<BomKind>();

        foreach (var command in commands)
        {
            if (!allItems.TryGetValue(command.MainBomItemId, out var main)) throw new PdmNotFoundException("主物料BOM行不存在或已被排除。");
            affectedKinds.Add(main.Kind);
            if (!templates.TryGetValue(main.DrawingNumber.Trim(), out var template) || template.PublishedRevision is not { } revision)
                throw new PdmRuleException($"主物料“{main.DrawingNumber}”尚未发布生效的关联配置。");
            var allocations = BuildAllocations(main, revision, command);
            main = main with { RelationAllocations = allocations };
            allItems[main.Id] = main;
            foreach (var entry in byKind)
                for (var index = 0; index < entry.Value.Count; index++)
                    if (entry.Value[index].Id == main.Id || entry.Value[index].EngineeringKitReferenceId == main.Id)
                    {
                        entry.Value[index] = entry.Value[index].Id == main.Id
                            ? entry.Value[index] with { RelationAllocations = allocations }
                            : entry.Value[index] with { EngineeringKitRelationAllocations = allocations };
                        affectedKinds.Add(entry.Key);
                    }
            var oldForMain = existingSelections.Where(item => item.MainBomItemId == main.Id).ToArray();
            foreach (var old in oldForMain)
                foreach (var entry in byKind)
                    if (entry.Value.RemoveAll(item => item.Id == old.AccessoryBomItemId) > 0) affectedKinds.Add(entry.Key);

            var savedSelections = new List<MaterialRelationSelection>();
            var savedReviews = new List<MaterialRelationReview>();
            foreach (var group in revision.Groups)
            {
                var choices = allocations.Select(allocation => allocation.Groups.Single(item => item.GroupId == group.Id)).ToArray();
                var selectedIds = choices.SelectMany(choice => choice.OptionIds).Distinct().ToArray();
                var reviewed = choices.All(choice => choice.OptionIds.Count > 0 || choice.ConfirmNoAccessory);
                if (selectedIds.Length > 0 && reviewed)
                    savedReviews.Add(new MaterialRelationReview(projectId, main.Id, revision.Id, group.Id,
                        MaterialRelationReviewDecision.Selected, main.Quantity, null, actor, timeProvider.GetUtcNow()));
                else if (reviewed)
                    savedReviews.Add(new MaterialRelationReview(projectId, main.Id, revision.Id, group.Id,
                        MaterialRelationReviewDecision.NoAccessory, main.Quantity,
                        string.Join("；", choices.Select(choice => string.IsNullOrWhiteSpace(choice.NoAccessoryReason)
                            ? "工程师确认本次无需配套" : choice.NoAccessoryReason.Trim()).Distinct()), actor, timeProvider.GetUtcNow()));
                foreach (var optionId in selectedIds)
                {
                    var option = group.Options.FirstOrDefault(item => item.Id == optionId)
                        ?? throw new PdmRuleException($"配件组“{group.Name}”包含无效选项。");
                    var material = await RequireApprovedMaterialAsync(option.MaterialId, $"配件“{option.MaterialCode}”", cancellationToken);
                    var expected = AllocationQuantity(allocations, group.Id, option);
                    U9UnitCatalog.RequireWholeQuantity(expected, material.UnitCode, $"配件“{material.MaterialCode}”的数量");
                    var old = oldForMain.FirstOrDefault(item => item.GroupId == group.Id && item.OptionId == option.Id);
                    var accessoryId = old?.AccessoryBomItemId ?? Guid.NewGuid();
                    var kind = ToBomKind(material.Kind);
                    affectedKinds.Add(kind);
                    var target = byKind[kind];
                    target.Add(new BomItem(accessoryId, projectId, kind, target.Count + 1, material.MaterialCode, material.Name, expected,
                        U9UnitCatalog.NormalizeBomUnit(material.UnitCode), material.Material, material.Specification, "W1", true)
                    {
                        Remark = $"关联物料：{main.DrawingNumber} / {group.Name}",
                        Brand = material.Brand,
                        SurfaceTreatment = material.SurfaceTreatment,
                        Weight = material.Weight.HasValue ? material.Weight.Value.ToString("0.####") : null,
                        Source = "MaterialRelation",
                        RelationMainBomItemId = main.Id,
                        IsManuallyRetained = true,
                        ReconciliationStatus = "RelationGenerated",
                        ReconciliationNote = $"由工程师按关联配置V{revision.Version}选配加入，来源主物料行{main.Sequence}。"
                            + string.Join("；", allocations.Where(allocation => allocation.Groups.Any(choice => choice.GroupId == group.Id && choice.OptionIds.Contains(option.Id)))
                                .Select(allocation => $"{allocation.Name}：{allocation.Quantity:0.####}")),
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = timeProvider.GetUtcNow()
                    });
                    savedSelections.Add(new MaterialRelationSelection(projectId, main.Id, accessoryId, revision.Id, group.Id, option.Id, expected, actor, timeProvider.GetUtcNow()));
                }
            }
            selectionsToSave[main.Id] = savedSelections;
            reviewsToSave[main.Id] = savedReviews;
        }

        static List<BomItem> Resequence(List<BomItem> items) => items.OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
            .Select((item, index) => item with { Sequence = index + 1 }).ToList();
        foreach (var kind in affectedKinds)
        {
            await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role,
                kind == BomKind.Electrical ? PermissionCodes.BomElectricalEdit : PermissionCodes.BomMechanicalEdit, cancellationToken);
            byKind[kind] = Resequence(byKind[kind]);
        }
        var finalItems = byKind.Values.SelectMany(items => items).Where(IsActive).ToDictionary(item => item.Id);
        foreach (var entry in reviewsToSave.Where(entry => entry.Value.Count > 0).ToArray())
        {
            var accessories = selectionsToSave[entry.Key]
                .Select(selection => finalItems.GetValueOrDefault(selection.AccessoryBomItemId))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            var fingerprint = CalculateRelationFingerprint(finalItems.GetValueOrDefault(entry.Key) ?? allItems[entry.Key], accessories, entry.Value[0].RevisionId);
            reviewsToSave[entry.Key] = entry.Value.Select(review => review with { BomFingerprint = fingerprint }).ToArray();
        }
        var audit = new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material-relation.apply", nameof(BomItem), projectId.ToString(), $"主物料{commands.Count}项");
        await repository.ApplyBomBatchAsync(projectId, byKind[BomKind.Standard], byKind[BomKind.NonStandard], byKind[BomKind.Unclassified], byKind[BomKind.Electrical], byKind[BomKind.Virtual], byKind[BomKind.ComponentDrawing], [], [audit], cancellationToken);
        foreach (var entry in selectionsToSave)
            await relations.ReplaceSelectionsAsync(projectId, entry.Key, entry.Value, cancellationToken);
        foreach (var entry in reviewsToSave)
            await relations.ReplaceReviewsAsync(projectId, entry.Key, entry.Value, cancellationToken);
        foreach (var kind in affectedKinds.Where(kind => kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
        {
            var publishable = byKind[kind].Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded).ToArray();
            await repository.SaveBomDraftAsync(projectId, kind, publishable, actor, cancellationToken);
            if (publishable.Length > 0) await repository.SetBomEmptyDeclarationAsync(projectId, kind, false, actor, cancellationToken);
        }
        return await CalculateCompletenessAsync(projectId, cancellationToken);
    }

    public Task EnsureCompleteAsync(Guid projectId, CancellationToken cancellationToken) =>
        EnsureCompleteAsync(projectId, null, cancellationToken);

    public async Task EnsureCompleteAsync(Guid projectId, IReadOnlyCollection<Guid>? selectedBomItemIds, CancellationToken cancellationToken)
    {
        if (selectedBomItemIds is { Count: 0 }) return;
        var result = await CalculateCompletenessAsync(projectId, cancellationToken, selectedBomItemIds);
        var mains = result.MainMaterials.Where(main => !main.IsComplete).ToArray();
        if (mains.Length == 0) return;
        var pending = mains
            .SelectMany(main => main.Groups.Where(group => !group.IsComplete)
                .Select(group => $"第{main.BomSequence}行 {main.MainMaterialCode} {main.MainMaterialName} / {group.GroupName}：{group.Status}"));
        throw new PdmRuleException("关联物料未处理，不能发布。请在BOM“关联物料”中选择配件或确认“无需配套”，保存核对结果后重试。未处理项："
            + string.Join("；", pending));
    }

    private async Task<MaterialRelationCompleteness> CalculateCompletenessAsync(Guid projectId, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? selectedBomItemIds = null)
    {
        var byKind = await LoadBomByKindAsync(projectId, cancellationToken);
        var all = byKind.Values.SelectMany(item => item).Where(IsActive).ToArray();
        var itemById = all.ToDictionary(item => item.Id);
        var selections = (await relations.ListSelectionsAsync(projectId, cancellationToken)).ToArray();
        var scope = selectedBomItemIds?.ToHashSet();
        if (scope is not null)
        {
            scope.UnionWith(await relations.ListRelatedMainBomItemIdsAsync(projectId, scope, cancellationToken));
            scope.UnionWith(all.Where(item => scope.Contains(item.Id) && item.EngineeringKitReferenceId.HasValue)
                .Select(item => item.EngineeringKitReferenceId!.Value).ToArray());
        }
        var candidates = scope is null ? all : all.Where(item => scope.Contains(item.Id)
            || item.EngineeringKitReferenceId is Guid reference && scope.Contains(reference)).ToArray();
        var templates = (await relations.ListTemplatesAsync(false, cancellationToken))
            .Where(item => item.PublishedRevision is not null && !item.IsArchived)
            .SelectMany(item => item.PublishedMainMaterials.Select(main => (main.MaterialCode, Template: item))).ToDictionary(item => item.MaterialCode, item => item.Template, StringComparer.OrdinalIgnoreCase);
        var reviews = (await relations.ListReviewsAsync(projectId, cancellationToken)).ToArray();
        var checks = new List<MaterialRelationMainCheck>();
        foreach (var main in (await RelationMainsAsync(candidates, templates.Keys, cancellationToken))
            .Where(item => templates.ContainsKey(item.DrawingNumber.Trim()) && (scope is null || scope.Contains(item.Id))))
        {
            var template = templates[main.DrawingNumber.Trim()];
            var revision = template.PublishedRevision!;
            var allocations = main.RelationAllocations.Where(allocation => allocation.RevisionId == revision.Id).ToArray();
            var linkedSelections = selections.Where(item => item.MainBomItemId == main.Id && item.RevisionId == revision.Id).ToArray();
            var relationFingerprint = CalculateRelationFingerprint(main,
                linkedSelections.Select(item => itemById.GetValueOrDefault(item.AccessoryBomItemId))
                    .Where(item => item is not null).Select(item => item!).ToArray(),
                revision.Id);
            var groupChecks = new List<MaterialRelationGroupCheck>();
            foreach (var group in revision.Groups)
            {
                var linked = selections.Where(item => item.MainBomItemId == main.Id && item.RevisionId == revision.Id && item.GroupId == group.Id).ToArray();
                var selected = linked.Select(item => item.OptionId).Distinct().ToArray();
                var expected = linked.Sum(item => item.ExpectedQuantity);
                var actual = linked.Where(item => itemById.ContainsKey(item.AccessoryBomItemId)).Sum(item => itemById[item.AccessoryBomItemId].Quantity);
                var review = reviews.LastOrDefault(item => item.MainBomItemId == main.Id && item.RevisionId == revision.Id
                    && item.GroupId == group.Id && item.MainQuantity == main.Quantity);
                var status = selected.Length == 0 && review?.Decision == MaterialRelationReviewDecision.NoAccessory
                    ? "已确认无需"
                    : ChoiceStatus(group, selected);
                if (allocations.Length > 0)
                {
                    var pending = allocations.FirstOrDefault(allocation => allocation.Quantity <= 0
                        || !allocation.Groups.Any(choice => choice.GroupId == group.Id && (choice.ConfirmNoAccessory && choice.OptionIds.Count == 0
                            || choice.OptionIds.Count > 0 && ChoiceStatus(group, choice.OptionIds) == "已选配")));
                    status = allocations.Sum(allocation => allocation.Quantity) != main.Quantity ? "组合分配数量与主料数量不一致"
                        : pending is not null ? $"{pending.Name}待核对"
                        : selected.Length > 0 ? "已选配" : "已确认无需";
                    var frozenOptions = allocations.SelectMany(allocation => allocation.Groups.Where(choice => choice.GroupId == group.Id))
                        .SelectMany(choice => choice.OptionIds).Distinct().ToHashSet();
                    if (!frozenOptions.SetEquals(selected)) status = "组合配件与保存的选配不一致";
                }
                if (status == "已选配")
                {
                    foreach (var selection in linked)
                    {
                        var option = group.Options.FirstOrDefault(item => item.Id == selection.OptionId);
                        if (option is null || !itemById.TryGetValue(selection.AccessoryBomItemId, out var accessory)) { status = "关联配件行不存在"; break; }
                        var expectedForOption = allocations.Length > 0 ? AllocationQuantity(allocations, group.Id, option)
                            : option.QuantityMode == MaterialRelationQuantityMode.PerMainQuantity ? main.Quantity * option.QuantityPerSet : option.QuantityPerSet;
                        if (!string.Equals(accessory.DrawingNumber.Trim(), option.MaterialCode, StringComparison.OrdinalIgnoreCase)) { status = "关联配件与当前配置不一致"; break; }
                        if (accessory.Quantity != expectedForOption || selection.ExpectedQuantity != expectedForOption) { status = $"数量不匹配，应为{expectedForOption:0.####}"; break; }
                    }
                }
                if ((U9UnitCatalog.RequiresWholeQuantity(main.Unit) || main.EngineeringKitReferenceId == main.Id)
                    && (main.Quantity != decimal.Truncate(main.Quantity) || allocations.Any(allocation => allocation.Quantity != decimal.Truncate(allocation.Quantity))))
                    status = "计件数量必须为整数，请先修正BOM及组合分配";
                var isComplete = status is "已选配" or "已确认无需";
                if (isComplete && IsRelationFingerprintStale(review?.BomFingerprint, relationFingerprint))
                {
                    isComplete = false;
                    status = "BOM已变化或尚未核对，请重新核对";
                }
                var selectedAudit = linked.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
                groupChecks.Add(new MaterialRelationGroupCheck(group.Id, group.Name, group.IsRequired, group.SelectionMode, group.MaxSelection,
                    isComplete, status, expected, actual, selected,
                    selected.Length > 0 ? MaterialRelationReviewDecision.Selected : review?.Decision,
                    selected.Length > 0 ? null : review?.Reason,
                    selected.Length > 0 ? selectedAudit?.UpdatedBy : review?.UpdatedBy,
                    selected.Length > 0 ? selectedAudit?.UpdatedAt : review?.UpdatedAt,
                    group.Options) {AccessoryBomItemIds=linked.GroupBy(selection=>selection.OptionId).ToDictionary(group=>group.Key,group=>group.First().AccessoryBomItemId)});
            }
            checks.Add(new MaterialRelationMainCheck(main.Id, main.DrawingNumber, main.Name, main.Quantity, template.Id, revision.Id, revision.Version,
                groupChecks.All(group => group.IsComplete), groupChecks) { BomSequence = main.Sequence, Allocations = allocations });
        }
        return new MaterialRelationCompleteness(projectId, checks.All(item => item.IsComplete), checks.Count,
            checks.Sum(item => item.Groups.Count(group => !group.IsComplete)), checks);
    }

    /// <summary>
    /// 核对指纹只覆盖该主物料行及其关联配件行：其它物料的新增、删除或修改不会让工程师重新核对。
    /// 带前缀的是当前算法；历史数据保存的是整个项目BOM的指纹（无前缀），按兼容处理不再据此判为待核对。
    /// </summary>
    private const string RelationFingerprintPrefix = "R2-";

    private static string CalculateRelationFingerprint(BomItem main, IReadOnlyList<BomItem> accessories, Guid revisionId)
    {
        // Only the main row, its accessory rows and the published revision participate; remark, sequence and
        // reconciliation timestamps do not invalidate a review.
        var content = new List<object> { revisionId, RelationContent(main) };
        if (main.RelationAllocations.Count > 1) content.Add(main.RelationAllocations);
        content.AddRange(accessories.OrderBy(item => item.Id).Select(item => (object)RelationContent(item)));
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
        return RelationFingerprintPrefix + hash[..40];
    }

    private static object RelationContent(BomItem item) => new
    {
        item.Id, item.Kind, item.DrawingNumber, item.Name,
        Quantity = item.Quantity.ToString("G29", CultureInfo.InvariantCulture), item.Unit,
        Material = item.Material ?? "", Specification = item.Specification ?? "", item.Revision,
        Brand = item.Brand ?? "", SurfaceTreatment = item.SurfaceTreatment ?? "",
        HeatTreatment = item.HeatTreatment ?? "", Weight = item.Weight ?? "",
        item.IsManuallyExcluded, item.IsReleaseExcluded, item.EngineeringKitReferenceId, item.EngineeringKitRevisionId
    };

    private static IReadOnlyList<MaterialRelationAllocation> BuildAllocations(BomItem main, MaterialRelationRevision revision, ApplyMaterialRelationsCommand command)
    {
        var unit = main.EngineeringKitReferenceId == main.Id ? "011" : main.Unit;
        U9UnitCatalog.RequireWholeQuantity(main.Quantity, unit, $"主料“{main.DrawingNumber}”的数量");
        var inputs = command.Allocations ?? [new(main.Id, "统一配置", main.Quantity, command.Choices)];
        foreach (var allocation in inputs) U9UnitCatalog.RequireWholeQuantity(allocation.Quantity, unit, $"主料“{main.DrawingNumber}”的组合数量");
        if (inputs.Count == 0 || inputs.Any(item => item.Id == Guid.Empty || item.Quantity <= 0 || decimal.Round(item.Quantity, 4) != item.Quantity)
            || inputs.Select(item => item.Id).Distinct().Count() != inputs.Count)
            throw new PdmRuleException("组合分配无效：每个组合须有独立标识，数量须大于0并最多保留四位小数。");
        if (inputs.Sum(item => item.Quantity) != main.Quantity)
            throw new PdmRuleException($"主料“{main.DrawingNumber}”的组合分配数量合计必须等于{main.Quantity:0.####}。");
        return inputs.Select((input, index) =>
        {
            if (input.Choices.Any(choice => revision.Groups.All(group => group.Id != choice.GroupId)))
                throw new PdmConflictException("关联配置已发生变化，请重新加载后选择组合。");
            var groups = revision.Groups.Select(group =>
            {
                var choice = input.Choices.LastOrDefault(choice => choice.GroupId == group.Id);
                var options = choice?.OptionIds.Distinct().ToArray() ?? [];
                ValidateChoice(group, options);
                if (options.Length > 0 && choice?.ConfirmNoAccessory == true)
                    throw new PdmRuleException("同一组合的配件组不能同时选择配件和确认无需配套。");
                return new MaterialRelationAllocationGroup(group.Id, group.Name, options, choice?.ConfirmNoAccessory == true, choice?.NoAccessoryReason?.Trim())
                {
                    Options = group.Options.Where(option => options.Contains(option.Id)).Select(option => new MaterialRelationAllocatedOption(
                        option.Id, option.MaterialCode, option.MaterialName, option.Specification, option.Brand, option.UnitCode,
                        option.QuantityMode, option.QuantityPerSet, option.QuantityMode == MaterialRelationQuantityMode.PerMainQuantity
                            ? input.Quantity * option.QuantityPerSet : option.QuantityPerSet)).ToArray()
                };
            }).ToArray();
            var name = string.IsNullOrWhiteSpace(input.Name) ? $"组合{index + 1}" : input.Name.Trim();
            if (name.Length > 80) throw new PdmRuleException("组合名称不能超过80个字。");
            return new MaterialRelationAllocation(input.Id, name, input.Quantity, revision.Id, revision.Version, groups);
        }).ToArray();
    }

    private static decimal AllocationQuantity(IReadOnlyList<MaterialRelationAllocation> allocations, Guid groupId, MaterialRelationOption option) =>
        allocations.Where(allocation => allocation.Groups.Any(choice => choice.GroupId == groupId && choice.OptionIds.Contains(option.Id)))
            .Sum(allocation => option.QuantityMode == MaterialRelationQuantityMode.PerMainQuantity ? allocation.Quantity * option.QuantityPerSet : option.QuantityPerSet);

    private static bool IsRelationFingerprintStale(string? stored, string current) =>
        !string.IsNullOrEmpty(stored)
        && stored.StartsWith(RelationFingerprintPrefix, StringComparison.Ordinal)
        && !string.Equals(stored, current, StringComparison.Ordinal);

    private async Task<Dictionary<BomKind, List<BomItem>>> LoadBomByKindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<BomKind, List<BomItem>>();
        foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified, BomKind.Electrical, BomKind.Virtual, BomKind.ComponentDrawing })
            result[kind] = (await repository.GetBomAsync(projectId, kind, cancellationToken)).ToList();
        return result;
    }

    private async Task<IReadOnlyList<BomItem>> RelationMainsAsync(IReadOnlyList<BomItem> items, IEnumerable<string> configuredCodes, CancellationToken ct)
    {
        var result = items.ToList();
        if (kits is null) return result;
        var codes = configuredCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in items.Where(item => item.EngineeringKitReferenceId.HasValue && codes.Contains(item.EngineeringKitCode ?? ""))
            .GroupBy(item => item.EngineeringKitReferenceId!.Value))
        {
            var first = reference.First();
            var kit = first.EngineeringKitId is Guid kitId ? await kits.FindAsync(kitId, ct) : null;
            var revision = kit?.Revisions.FirstOrDefault(revision => revision.Id == first.EngineeringKitRevisionId);
            if (kit is null || revision is null) throw new PdmRuleException($"套件“{first.EngineeringKitCode}”的引用版本不存在，请刷新并核对套件引用。");
            var quantities = reference.Select(row => {
                var component = revision.Components.FirstOrDefault(component => component.Id == row.EngineeringKitComponentId);
                if (component is null || component.Quantity <= 0) throw new PdmRuleException($"套件“{kit.Code}”的子料信息不完整，请通过套件维护重新引用。");
                return row.Quantity / component.Quantity;
            }).Distinct().ToArray();
            if (quantities.Length != 1 || quantities[0] <= 0) throw new PdmRuleException($"套件“{kit.Code}”的子料数量不一致，请通过套件维护调整数量后再核对关联配置。");
            result.Add(first with { Id = reference.Key, DrawingNumber = kit.Code!, Name = kit.Name, Specification = kit.Model,
                Quantity = quantities[0], EngineeringKitComponentId = null, Files = null,
                RelationAllocations = first.EngineeringKitRelationAllocations });
        }
        return result;
    }

    private static MaterialRelationMainMaterial KitMain(EngineeringKit kit) => new(kit.Id, kit.Code!, kit.Name, kit.Model, kit.Brand, kit.Description) { IsEngineeringKit = true };

    private async Task<(MaterialRelationMainMaterial Main, bool SyncReady)> RequireMainAsync(Guid id, CancellationToken ct)
    {
        if (await materials.FindMaterialAsync(id, ct) is { } material)
        {
            _ = await RequireApprovedMaterialAsync(id, "主物料", ct);
            return (new(material.Id, material.MaterialCode, material.Name, material.Specification, material.Brand, material.Remark), material.U9SyncConfirmed);
        }
        var kit = kits is null ? null : await kits.FindAsync(id, ct);
        if (kit is null) throw new PdmNotFoundException("主物料或料品套件不存在。");
        if (!kit.IsActive || kit.CurrentReleasedRevision is null) throw new PdmRuleException($"料品套件“{kit.Code ?? kit.Name}”尚未发布或已停用，不能作为主物料。");
        return (KitMain(kit), true);
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
        if (selected.Count == 0) return "待核对";
        if (group.MaxSelection.HasValue && selected.Count > group.MaxSelection.Value) return "选择数量超过上限";
        if (selected.Any(id => group.Options.All(option => option.Id != id))) return "存在失效选项";
        return "已选配";
    }

    private static void ValidateChoice(MaterialRelationGroup group, IReadOnlyCollection<Guid> selected)
    {
        var status = ChoiceStatus(group, selected);
        if (selected.Count > 0 && status != "已选配") throw new PdmRuleException($"配件组“{group.Name}”：{status}。");
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
