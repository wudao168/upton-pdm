using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record BomConfigurationQuantityMember(Guid MainBomItemId, Guid? AllocationId = null);
public sealed record BomConfigurationQuantityChange(IReadOnlyList<BomConfigurationQuantityMember> Members,
    decimal Quantity, decimal ExpectedQuantity, bool Confirmed = false, string? ConfirmationToken = null);
public sealed record BomQuantityDifference(Guid? BomItemId, string MaterialCode, string MaterialName,
    decimal Before, decimal After, bool IsAccessory, string Unit = "");
public sealed record BomConfigurationQuantityPreview(IReadOnlyList<BomQuantityDifference> Changes, IReadOnlyList<BomItem> Items, string ConfirmationToken);

public sealed partial class MaterialRelationService
{
    private sealed record QuantityRecipeGroup(Guid GroupId, string State, IReadOnlyList<Guid> Options);
    private static bool QuantityActive(BomItem item)=>!item.IsManuallyExcluded && !item.IsPendingRemoval;
    public async Task<BomConfigurationQuantityPreview> ChangeQuantitiesAsync(Guid projectId,
        IReadOnlyList<BomConfigurationQuantityChange> commands, string actor, UserRole role, CancellationToken ct, bool previewOnly = false, bool validateConfirmation = false)
    {
        await RequireAsync(actor, role, PermissionCodes.BomEdit, ct);
        await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role, PermissionCodes.BomEdit, ct);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var packages = await repository.ListReleasePackagesAsync(projectId, ct);
        if (packages.Any(package => package.State is
                ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing))
            throw new PdmConflictException("项目正在审批或发布，暂时不能修改BOM数量。");
        if (commands.Count == 0 || commands.Any(command => command.Members.Count == 0 || command.Quantity < 0
                || decimal.Round(command.Quantity, 4) != command.Quantity || !previewOnly && !command.Confirmed))
            throw new PdmRuleException("请先核对主料与配件的数量变化，再确认同步修改。");

        var original = await LoadBomByKindAsync(projectId, ct);
        var byKind = original.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var items = byKind.Values.SelectMany(rows => rows).Where(QuantityActive).ToArray();
        var releasedVersions = (await repository.ListBomVersionsAsync(projectId, null, ct)).Where(version=>version.State==BomVersionState.Released).ToArray();
        var selections = (await relations.ListSelectionsAsync(projectId, ct)).ToArray();
        var reviews = (await relations.ListReviewsAsync(projectId,ct)).ToArray();
        var rules = await LoadProjectRelationRulesAsync(items, selections, reviews, ct);
        var mains = (await RelationMainsAsync(items, rules.MainCodes.Concat(items.Select(item => item.EngineeringKitCode ?? "")), ct)).ToDictionary(item => item.Id);
        var validationRules=(await repository.GetSystemSettingsAsync(ct)).ValidationRules;
        MaterialRelationMainCheck? WorkCheck(BomItem main)
        {
            var template = rules.For(main);
            if (template?.PublishedRevision is not { } revision) return null;
            return new(main.Id,main.DrawingNumber,main.Name,main.Quantity,template.Id,revision.Id,revision.Version,false,
                revision.Groups.Select(group=>{
                    var linked=selections.Where(selection=>selection.MainBomItemId==main.Id && selection.RevisionId==revision.Id && selection.GroupId==group.Id).ToArray();
                    var review=reviews.FirstOrDefault(review=>review.MainBomItemId==main.Id && review.RevisionId==revision.Id && review.GroupId==group.Id);
                    return new MaterialRelationGroupCheck(group.Id,group.Name,group.IsRequired,group.SelectionMode,group.MaxSelection,false,"待核对",0,0,
                        linked.Select(selection=>selection.OptionId).ToArray(),linked.Length>0?MaterialRelationReviewDecision.Selected:review?.Decision,
                        review?.Reason,review?.UpdatedBy,review?.UpdatedAt,group.Options);
                }).ToArray()) {Allocations=main.RelationAllocations};
        }
        var checks=mains.Values.Select(main=>WorkCheck(main)).Where(check=>check is not null).ToDictionary(check=>check!.MainBomItemId,check=>check!);
        var desiredAllocations = new Dictionary<Guid, List<MaterialRelationAllocation>>();
        var desiredTotals = new Dictionary<Guid, decimal>();
        var previewOptionIds = new Dictionary<Guid,Guid>();
        var accessoryMaterials = new Dictionary<Guid,PdmMaterial>();
        var kitDefinitions = new Dictionary<Guid, EngineeringKit>();
        async Task<EngineeringKitRevision> KitRevisionAsync(BomItem source)
        {
            if(kits is null || source.EngineeringKitId is not Guid kitId)throw new PdmRuleException("套件资料不存在，请重新核对套件引用。");
            if(!kitDefinitions.TryGetValue(kitId,out var kit)) kitDefinitions[kitId]=kit=await kits.FindAsync(kitId,ct) ?? throw new PdmRuleException("套件资料不存在，请重新核对套件引用。");
            return kit.Revisions.FirstOrDefault(revision=>revision.Id==source.EngineeringKitRevisionId) ?? throw new PdmRuleException("套件引用版本不存在，请重新核对。");
        }
        var seen = new HashSet<(Guid, Guid?)>();
        foreach (var command in commands)
        {
            var members = command.Members.Select(member =>
            {
                if (!seen.Add((member.MainBomItemId, member.AllocationId)) || !mains.TryGetValue(member.MainBomItemId, out var main))
                    throw new PdmConflictException("配置行已变化，请刷新BOM后重试。");
                if(main.RelationMainBomItemId.HasValue && !main.EngineeringKitReferenceId.HasValue)
                    throw new PdmRuleException("此主料数量由上级配套决定，请修改对应上级主料数量，配件会同步更新。");
                var allocations = desiredAllocations.GetValueOrDefault(main.Id);
                if (allocations is null) desiredAllocations[main.Id] = allocations = main.RelationAllocations.ToList();
                var allocation = member.AllocationId.HasValue ? allocations.SingleOrDefault(item => item.Id == member.AllocationId) : null;
                if (member.AllocationId.HasValue && allocation is null || !member.AllocationId.HasValue && allocations.Count > 1)
                    throw new PdmConflictException("配套组合已变化，请重新选择该配置行。");
                return (Member: member, Main: main, Allocation: allocation, Quantity: allocation is not null && allocations.Count>1 ? allocation.Quantity : main.Quantity);
            }).ToArray();
            var first = members[0].Main;
            if (members.Any(member => member.Main.DrawingNumber != first.DrawingNumber || member.Main.EngineeringKitRevisionId != first.EngineeringKitRevisionId))
                throw new PdmRuleException("只能合并修改同一物料、同一套件版本的配置数量。");
            var recipes = members.Select(member => {
                var check=checks.GetValueOrDefault(member.Main.Id);
                var groups=member.Allocation is { } allocation
                    ? allocation.Groups.Select(group=>new QuantityRecipeGroup(group.GroupId,group.OptionIds.Count>0?"Selected":group.ConfirmNoAccessory?"NoAccessory":"Pending",group.OptionIds.Order().ToArray()))
                    : check?.Groups.Select(group=>new QuantityRecipeGroup(group.GroupId,group.SelectedOptionIds.Count>0?"Selected":group.ReviewDecision==MaterialRelationReviewDecision.NoAccessory?"NoAccessory":"Pending",group.SelectedOptionIds.Order().ToArray())) ?? [];
                return System.Text.Json.JsonSerializer.Serialize(new {Revision=member.Allocation?.RevisionId ?? check?.RevisionId,Groups=groups.OrderBy(group=>group.GroupId).ToArray()});
            }).Distinct().ToArray();
            if (recipes.Length > 1) throw new PdmRuleException("不同配套组合必须分行修改，不能在同一折叠栏混合调整。");
            var publicationGroups=members.Select(member=>member.Main.EngineeringKitReferenceId==member.Main.Id
                ? string.Join("|",items.Where(row=>row.EngineeringKitReferenceId==member.Main.Id).Select(row=>BomDrawingRevision.PublishedBomVersion(row,packages,releasedVersions)??"未发布").Distinct().Order())
                : BomDrawingRevision.PublishedBomVersion(member.Main,packages,releasedVersions)??"未发布").Distinct().ToArray();
            if(publicationGroups.Length>1)throw new PdmRuleException("不同发布状态或发布版本的配置必须分开修改数量。");
            if (members.Any(member => member.Allocation is { } allocation && rules.For(member.Main)?.PublishedRevision is { } revision
                    && allocation.RevisionId != revision.Id))
                throw new PdmConflictException("配套版本已更新，请先核对该组合，再修改数量。");
            U9UnitCatalog.RequireWholeQuantity(command.Quantity, first.EngineeringKitReferenceId == first.Id ? "007" : first.Unit, "主料汇总数量");
            var total = members.Sum(member => member.Quantity);
            if (total != command.ExpectedQuantity) throw new PdmConflictException("主料数量已变化，请刷新后重新确认配件同步变化。");
            var quantities = members.Select(member => member.Quantity).ToArray();
            var delta = command.Quantity - total;
            if (members.Any(member => member.Quantity != decimal.Truncate(member.Quantity))
                && U9UnitCatalog.RequiresWholeQuantity(first.EngineeringKitReferenceId==first.Id?"007":first.Unit))
            {
                // Repair legacy fractional references as integer business quantities, never as CAD-position fractions.
                quantities = quantities.Select((_,index)=>index<command.Quantity?1m:0m).ToArray();
                quantities[0] += command.Quantity-quantities.Sum();
            }
            else if (delta >= 0) quantities[0] += delta;
            else for (var index = quantities.Length - 1; index >= 0 && delta < 0; index--)
            {
                var reduction = Math.Min(quantities[index], -delta); quantities[index] -= reduction; delta += reduction;
            }
            for (var index = 0; index < members.Length; index++)
            {
                var member = members[index];
                if (member.Allocation is not null)
                {
                    var list = desiredAllocations[member.Main.Id];
                    list[list.FindIndex(value => value.Id == member.Allocation.Id)] = member.Allocation with { Quantity = quantities[index] };
                    desiredTotals[member.Main.Id] = list.Sum(value => value.Quantity);
                }
                else desiredTotals[member.Main.Id] = quantities[index];
            }
        }

        var selectionPlans = new Dictionary<Guid,IReadOnlyList<MaterialRelationSelection>>();
        var reviewPlans = new Dictionary<Guid,IReadOnlyList<MaterialRelationReview>>();
        var revisionPlans = new Dictionary<Guid,Guid>();
        var removedOwners = new HashSet<Guid>();
        void RemoveDerivedScope(IEnumerable<Guid> roots)
        {
            var removed=roots.ToHashSet();var more=true;
            while(more)
            {
                more=false;
                foreach(var selection in selections)if(removed.Contains(selection.MainBomItemId))more|=removed.Add(selection.AccessoryBomItemId);
                foreach(var row in byKind.Values.SelectMany(rows=>rows))
                    if(row.RelationMainBomItemId.HasValue && removed.Contains(row.RelationMainBomItemId.Value)
                        || row.EngineeringKitReferenceId.HasValue && removed.Contains(row.EngineeringKitReferenceId.Value))more|=removed.Add(row.Id);
            }
            removedOwners.UnionWith(removed);
            foreach(var id in removed){selectionPlans.Remove(id);reviewPlans.Remove(id);revisionPlans.Remove(id);}
            foreach(var rows in byKind.Values)rows.RemoveAll(row=>removed.Contains(row.Id));
        }
        foreach (var entry in desiredTotals.ToArray())
        {
            var main = mains[entry.Key];
            if (main.EngineeringKitReferenceId is not Guid reference || reference == main.Id) continue;
            var kit = mains[reference];
            var definition=await KitRevisionAsync(main);
            var perSet = definition.Components.Single(component=>component.Id==main.EngineeringKitComponentId).Quantity;
            var sets = entry.Value / perSet;
            if (sets != decimal.Truncate(sets)) throw new PdmRuleException($"此子料变化会使所属套件数量为{sets:0.####}套，请按整套整数修改。");
            if (desiredTotals.TryGetValue(reference, out var specified) && specified != sets)
                throw new PdmRuleException("同一套件的数量修改不一致，请分别核对后重试。");
            desiredTotals[reference] = sets;
        }
        var queue = new Queue<Guid>(desiredTotals.Keys.OrderBy(id => mains[id].EngineeringKitReferenceId == id ? 0 : 1));
        var handled = new HashSet<Guid>();
        while (queue.TryDequeue(out var id))
        {
            if (!handled.Add(id) || removedOwners.Contains(id)) continue;
            var main = mains[id]; var quantity = desiredTotals[id];
            var kitMain = main.EngineeringKitReferenceId == id;
            var kitRevision=kitMain?await KitRevisionAsync(main):null;
            var owned = byKind.Values.SelectMany(rows => rows).Where(item => kitMain ? item.EngineeringKitReferenceId == id : item.Id == id).ToArray();
            if (quantity == 0)
            {
                RemoveDerivedScope(owned.Select(owner=>owner.Id).Append(id));
                foreach(var owner in owned)byKind[owner.Kind].Add(owner with {IsManuallyExcluded=true,IsPendingRemoval=false,IsManualUnmatched=false,
                    IsSummaryQuantity=true,IsComplete=false,DeletedAt=timeProvider.GetUtcNow(),DeletedBy=actor,DeleteReason="配置数量设为0，移入回收站"});
                continue;
            }
            foreach (var owner in owned)
            {
                var nextQuantity = kitMain ? kitRevision!.Components.Single(component=>component.Id==owner.EngineeringKitComponentId).Quantity * quantity : quantity;
                U9UnitCatalog.RequireWholeQuantity(nextQuantity, owner.Unit, $"物料“{owner.DrawingNumber}”的数量");
                var rows = byKind[owner.Kind];
                rows[rows.FindIndex(row => row.Id == owner.Id)] = owner with { Quantity = nextQuantity, ParentDrawingNumber=null, IsManuallyRetained=true, IsSummaryQuantity=true };
                if (kitMain && checks.ContainsKey(owner.Id))
                {
                    desiredTotals[owner.Id] = nextQuantity;
                    desiredAllocations.TryAdd(owner.Id, owner.RelationAllocations.ToList());
                    queue.Enqueue(owner.Id);
                }
            }
            if (rules.For(main)?.PublishedRevision is not { } revision || !checks.TryGetValue(id, out var check))
            {
                if(selections.Any(selection=>selection.MainBomItemId==id))throw new PdmConflictException("关联配置已停用或缺少有效规则，请先核对配置，再修改主料数量。");
                continue;
            }
            var allocations = desiredAllocations.GetValueOrDefault(id) ?? main.RelationAllocations.ToList();
            if (allocations.Count > 0)
            {
                if (allocations.Sum(allocation => allocation.Quantity) != quantity)
                {
                    var delta = quantity - allocations.Sum(allocation => allocation.Quantity);
                    if (delta >= 0) allocations[0] = allocations[0] with { Quantity = allocations[0].Quantity + delta };
                    else for (var index = allocations.Count - 1; index >= 0 && delta < 0; index--)
                    {
                        var reduction = Math.Min(allocations[index].Quantity, -delta);
                        allocations[index] = allocations[index] with { Quantity = allocations[index].Quantity - reduction }; delta += reduction;
                    }
                }
                allocations.RemoveAll(allocation => allocation.Quantity == 0);
            }
            var choices = check.Groups.Select(group => new MaterialRelationChoice(group.GroupId, group.SelectedOptionIds,
                group.ReviewDecision == MaterialRelationReviewDecision.NoAccessory, group.ReviewReason)).ToArray();
            if (allocations.Count == 0 && check.Groups.Any(group => group.SelectedOptionIds.Count == 0 && group.ReviewDecision != MaterialRelationReviewDecision.NoAccessory))
            {
                if(check.Groups.Any(group=>group.SelectedOptionIds.Count>0))throw new PdmConflictException("部分配件组尚未核对，请先补齐或确认其他配件组，再修改主料数量。");
                continue;
            }
            var command = new ApplyMaterialRelationsCommand(id, choices, allocations.Count == 0 ? null : allocations.Select(allocation => new MaterialRelationAllocationCommand(
                allocation.Id, allocation.Name, allocation.Quantity, allocation.Groups.Select(group => new MaterialRelationChoice(group.GroupId, group.OptionIds, group.ConfirmNoAccessory, group.NoAccessoryReason)).ToArray())).ToArray());
            var resolved = BuildAllocations(main with { Quantity = quantity }, revision, command);
            revisionPlans[id]=revision.Id;
            var newSelections=new List<MaterialRelationSelection>();
            var newReviews=new List<MaterialRelationReview>();
            selectionPlans[id]=newSelections;reviewPlans[id]=newReviews;
            foreach(var group in revision.Groups)
            {
                var choicesForGroup=resolved.SelectMany(allocation=>allocation.Groups.Where(choice=>choice.GroupId==group.Id)).ToArray();
                var selected=choicesForGroup.Any(choice=>choice.OptionIds.Count>0);
                newReviews.Add(new(projectId,id,revision.Id,group.Id,selected?MaterialRelationReviewDecision.Selected:MaterialRelationReviewDecision.NoAccessory,
                    quantity,selected?null:string.Join("；",choicesForGroup.Select(choice=>choice.NoAccessoryReason??"工程师确认本次无需配套").Distinct()),actor,timeProvider.GetUtcNow()));
            }
            var oldForMain=selections.Where(selection=>selection.MainBomItemId==id).ToArray();
            var wanted=resolved.SelectMany(allocation=>allocation.Groups.SelectMany(group=>group.OptionIds.Select(option=>(group.GroupId,Option:option)))).ToHashSet();
            var removedParts=oldForMain.Where(selection=>!wanted.Contains((selection.GroupId,selection.OptionId))).Select(selection=>selection.AccessoryBomItemId)
                .Concat(byKind.Values.SelectMany(rows=>rows).Where(row=>row.RelationMainBomItemId==id && row.Source=="MaterialRelation" && !oldForMain.Any(selection=>selection.AccessoryBomItemId==row.Id)).Select(row=>row.Id)).ToArray();
            RemoveDerivedScope(removedParts);
            foreach (var rows in byKind.Values)
                for (var index = 0; index < rows.Count; index++)
                    if (kitMain ? rows[index].EngineeringKitReferenceId == id : rows[index].Id == id)
                        rows[index] = kitMain ? rows[index] with { EngineeringKitRelationAllocations = resolved } : rows[index] with { RelationAllocations = resolved };
            foreach (var group in revision.Groups)
            foreach (var option in group.Options.Where(option=>resolved.Any(allocation=>allocation.Groups.Any(choice=>choice.GroupId==group.Id && choice.OptionIds.Contains(option.Id)))))
            {
                if(!accessoryMaterials.TryGetValue(option.MaterialId,out var material))
                    accessoryMaterials[option.MaterialId]=material=await RequireApprovedMaterialAsync(option.MaterialId,$"配件“{option.MaterialCode}”",ct);
                var selection=oldForMain.FirstOrDefault(selection=>selection.GroupId==group.Id && selection.OptionId==option.Id);
                var expected = AllocationQuantity(resolved, group.Id, option);
                U9UnitCatalog.RequireWholeQuantity(expected,material.UnitCode,$"配件“{material.MaterialCode}”的数量");
                var previousPart=selection is null?null:byKind.Values.SelectMany(rows=>rows).FirstOrDefault(item=>item.Id==selection.AccessoryBomItemId);
                if(previousPart is not null && (!string.Equals(previousPart.DrawingNumber,material.MaterialCode,StringComparison.OrdinalIgnoreCase)
                    || U9UnitCatalog.NormalizeBomUnit(previousPart.Unit)!=U9UnitCatalog.NormalizeBomUnit(material.UnitCode)))
                    throw new PdmConflictException($"配件“{material.MaterialCode}”的料号或计量单位已变更，请先核对配置。");
                var part=previousPart is not null?previousPart with {Quantity=expected,IsManuallyExcluded=false,IsPendingRemoval=false,IsManualUnmatched=false,
                        IsSummaryQuantity=true,IsManuallyRetained=true,DeletedAt=null,DeletedBy=null,DeleteReason=null}
                    :new BomItem(selection?.AccessoryBomItemId??Guid.NewGuid(),projectId,ToBomKind(material.Kind),
                        byKind[ToBomKind(material.Kind)].Select(item=>item.Sequence).DefaultIfEmpty(0).Max()+1,
                        material.MaterialCode,material.Name,expected,material.UnitCode,material.Material,material.Specification,"W1",true)
                        {Source="MaterialRelation",RelationMainBomItemId=id,IsManuallyRetained=true,IsSummaryQuantity=true,
                            Brand=material.Brand,SurfaceTreatment=material.SurfaceTreatment,Weight=material.Weight?.ToString("0.####"),
                            Remark=$"关联物料：{main.DrawingNumber} / {group.Name}",ReconciliationStatus="RelationGenerated",
                            ReconciliationNote="配套数量已由工程师确认随主料同步。",ReconciliationUpdatedBy=actor,ReconciliationUpdatedAt=timeProvider.GetUtcNow()};
                part=part with {IsComplete=BomValidationFieldCatalog.IsComplete(part,part.Kind,validationRules)};
                if(previousPart is not null)byKind[previousPart.Kind].RemoveAll(item=>item.Id==part.Id);
                byKind[part.Kind].Add(part);
                previewOptionIds[part.Id]=option.Id;
                newSelections.Add(new(projectId,id,part.Id,revision.Id,group.Id,option.Id,expected,actor,timeProvider.GetUtcNow()));
                var nested=previousPart ?? part with {Quantity=selection?.ExpectedQuantity??0};
                var nestedCheck=WorkCheck(nested);
                if(nestedCheck is not null)
                {
                    mains.TryAdd(nested.Id,nested);checks[nested.Id]=nestedCheck;
                    if(handled.Contains(nested.Id) && desiredTotals.GetValueOrDefault(nested.Id)!=expected)
                        throw new PdmRuleException("关联配置存在循环或数量冲突，请核对配套关系后再修改。");
                    desiredTotals[nested.Id]=expected;desiredAllocations.TryAdd(nested.Id,nested.RelationAllocations.ToList());queue.Enqueue(nested.Id);
                }

            }
        }
        var afterById = byKind.Values.SelectMany(rows => rows).ToDictionary(item => item.Id);
        var beforeById=original.Values.SelectMany(rows=>rows).ToDictionary(item=>item.Id);
        var differences=beforeById.Keys.Concat(afterById.Keys).Distinct().Select(id=>{
            var before=beforeById.GetValueOrDefault(id);var after=afterById.GetValueOrDefault(id);var item=after??before!;
            var oldQuantity=before is not null && QuantityActive(before)?before.Quantity:0;
            var newQuantity=after is not null && QuantityActive(after)?after.Quantity:0;
            return new BomQuantityDifference(id,item.DrawingNumber,item.Name,oldQuantity,newQuantity,item.RelationMainBomItemId.HasValue,item.Unit);
        }).Where(change=>change.Before!=change.After).ToArray();
        var finalMains=(await RelationMainsAsync(afterById.Values.Where(QuantityActive).ToArray(),rules.MainCodes.Concat(afterById.Values.Select(item=>item.EngineeringKitCode??"")),ct)).ToDictionary(main=>main.Id);
        foreach(var owner in reviewPlans.Keys.ToArray())
        {
            if(!finalMains.TryGetValue(owner,out var main))continue;
            var fingerprint=CalculateRelationFingerprint(main,selectionPlans[owner].Select(selection=>afterById[selection.AccessoryBomItemId]).ToArray(),revisionPlans[owner]);
            reviewPlans[owner]=reviewPlans[owner].Select(review=>review with {BomFingerprint=fingerprint}).ToArray();
        }
        var affectedKinds=desiredTotals.Keys.Where(mains.ContainsKey).Select(id=>mains[id].Kind)
            .Concat(differences.Select(change=>afterById.GetValueOrDefault(change.BomItemId!.Value)?.Kind??beforeById[change.BomItemId.Value].Kind)).Distinct().ToArray();
        foreach (var kind in affectedKinds)
        {
            await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role,
                kind == BomKind.Electrical ? PermissionCodes.BomElectricalEdit : PermissionCodes.BomMechanicalEdit, ct);

        }
        foreach(var kind in commands.Where(command=>command.Quantity==0).SelectMany(command=>command.Members.Select(member=>mains[member.MainBomItemId].Kind)).Distinct())
            await ProjectPermissionPolicy.RequireAsync(repository,projectId,actor,role,$"bom.{(kind==BomKind.Electrical?"electrical":"mechanical")}.row.delete",ct);
        foreach(var kind in commands.SelectMany(command=>command.Members.Select(member=>mains[member.MainBomItemId].Kind)).Distinct())
            await ProjectPermissionPolicy.RequireAsync(repository,projectId,actor,role,$"bom.{(kind==BomKind.Electrical?"electrical":"mechanical")}.row.update",ct);
        static object Allocations(IReadOnlyList<MaterialRelationAllocation> values)=>values.Select(allocation=>new {
            allocation.Id,allocation.Quantity,allocation.RevisionId,Groups=allocation.Groups.Select(group=>new {
                group.GroupId,group.OptionIds,group.ConfirmNoAccessory,Options=group.Options.Select(option=>new {
                    option.OptionId,option.MaterialCode,option.UnitCode,option.QuantityMode,option.QuantityPerSet,option.Quantity
                }).ToArray()
            }).ToArray()
        }).ToArray();
        var confirmationScope=handled.Concat(removedOwners).ToHashSet();
        var confirmationState=new {
            Commands=commands.Select(command=>new {command.Members,command.Quantity,command.ExpectedQuantity}).ToArray(),
            Rows=original.Values.SelectMany(rows=>rows).Where(item=>confirmationScope.Contains(item.Id)
                || item.EngineeringKitReferenceId.HasValue && confirmationScope.Contains(item.EngineeringKitReferenceId.Value)
                || item.RelationMainBomItemId.HasValue && confirmationScope.Contains(item.RelationMainBomItemId.Value)).OrderBy(item=>item.Id).Select(item=>new {
                    item.Id,item.Kind,Code=item.DrawingNumber.Trim().ToUpperInvariant(),Unit=U9UnitCatalog.NormalizeBomUnit(item.Unit),item.Quantity,item.Source,item.IsSummaryQuantity,item.IsManuallyExcluded,item.IsPendingRemoval,item.IsReleaseExcluded,
                    item.RelationMainBomItemId,item.EngineeringKitReferenceId,item.EngineeringKitRevisionId,item.EngineeringKitComponentId,
                    Relations=Allocations(item.RelationAllocations),KitRelations=Allocations(item.EngineeringKitRelationAllocations),
                    Published=BomDrawingRevision.PublishedBomVersion(item,packages,releasedVersions)
                }).ToArray(),
            Selections=selections.Where(selection=>confirmationScope.Contains(selection.MainBomItemId)).OrderBy(selection=>selection.AccessoryBomItemId)
                .Select(selection=>new {selection.MainBomItemId,selection.AccessoryBomItemId,selection.RevisionId,selection.GroupId,selection.OptionId,selection.ExpectedQuantity}).ToArray(),
            Rules=confirmationScope.Where(mains.ContainsKey).Order().Select(id=>new {
                MainId=id, Template=rules.For(mains[id]) is { } rule ? new { rule.IsArchived, Revision=rule.PublishedRevision } : null
            }).ToArray(),
            Materials=accessoryMaterials.Values.OrderBy(material=>material.Id).Select(material=>new {material.Id,material.Kind,material.MaterialCode,material.UnitCode}).ToArray(),
            Kits=kitDefinitions.Values.OrderBy(kit=>kit.Id).Select(kit=>new {kit.Id,Revisions=kit.Revisions.Where(revision=>mains.Values.Any(main=>confirmationScope.Contains(main.Id)&&main.EngineeringKitRevisionId==revision.Id))
                .OrderBy(revision=>revision.Id).Select(revision=>new {revision.Id,revision.Components}).ToArray()}).ToArray()
        };
        var token=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(confirmationState)));
        if(validateConfirmation && commands.Any(command=>!string.Equals(command.ConfirmationToken,token,StringComparison.Ordinal)))
            throw new PdmConflictException("主料、配件或选配规则已变化，请重新预览并确认数量联动。");
        if (!previewOnly)
        {
            var audit = new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.configuration-quantity", nameof(BomItem), projectId.ToString(),
                System.Text.Json.JsonSerializer.Serialize(differences));
            await repository.ApplyBomBatchAsync(projectId, byKind[BomKind.Standard], byKind[BomKind.NonStandard], byKind[BomKind.Unclassified], byKind[BomKind.Electrical], byKind[BomKind.Virtual], byKind[BomKind.ComponentDrawing], [], [audit], ct);
            foreach (var id in removedOwners) { await relations.ReplaceSelectionsAsync(projectId, id, [], ct); await relations.ReplaceReviewsAsync(projectId, id, [], ct); }
            foreach(var entry in selectionPlans)await relations.ReplaceSelectionsAsync(projectId,entry.Key,entry.Value,ct);
            foreach(var entry in reviewPlans)await relations.ReplaceReviewsAsync(projectId,entry.Key,entry.Value,ct);
            foreach(var kind in affectedKinds.Where(kind=>kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
                await repository.SaveBomDraftAsync(projectId,kind,byKind[kind].Where(QuantityActive).ToArray(),actor,ct);
        }
        return new(differences, afterById.Values.Where(QuantityActive).Select(item => {
            var publication=BomDrawingRevision.PublishedBomVersion(item,packages,releasedVersions);
            return item with {IsPublishedInBom=publication is not null,BomPublishedVersion=publication,
                RelationOptionId=previewOptionIds.TryGetValue(item.Id,out var optionId)?optionId:null};
        }).ToArray(),token);
    }
}
