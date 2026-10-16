using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record BomSummaryEdit(IReadOnlyList<Guid> ItemIds, string Fingerprint, IReadOnlyList<string> Fields, BomItemInput Values);

public static class BomSummaryProjection
{
    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string AttributeKey(BomItem item) => item.SourceDocumentId.HasValue ? JsonSerializer.Serialize(new {
        item.SourceDocumentId, Configuration = Normalize(item.SourceConfiguration), item.Kind, item.IsManuallyExcluded, item.IsPendingRemoval,
        Name = Normalize(item.Name), Model = Normalize(item.Specification), Brand = Normalize(item.Brand), Material = Normalize(item.Material),
        Unit = Normalize(item.Unit), Surface = Normalize(item.SurfaceTreatment), Weight = Normalize(item.Weight), Remark = Normalize(item.Remark), Revision = Normalize(item.Revision)
    }) : string.Empty;

    public static IReadOnlyList<BomItem> Group(IReadOnlyList<BomItem> items)
    {
        var codesByAttributes = items.Where(item => !string.IsNullOrWhiteSpace(item.DrawingNumber) && item.SourceDocumentId.HasValue)
            .GroupBy(AttributeKey).Where(group => group.Select(item => Normalize(item.DrawingNumber)).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => Normalize(group.First().DrawingNumber));
        string Code(BomItem item) => !string.IsNullOrWhiteSpace(item.DrawingNumber) ? Normalize(item.DrawingNumber)
            : codesByAttributes.GetValueOrDefault(AttributeKey(item), string.Empty);
        return items
        .GroupBy(item => JsonSerializer.Serialize(new {
            item.Kind, item.IsManuallyExcluded, item.IsPendingRemoval, item.EngineeringKitReferenceId,
            DeletedSource = item.IsManuallyExcluded && item.SourceDocumentId.HasValue,
            Code = Code(item), Unit = Normalize(item.Unit), Revision = Normalize(item.Revision),
            Identity = string.IsNullOrWhiteSpace(Code(item))
                ? item.SourceDocumentId.HasValue ? AttributeKey(item) : item.Id.ToString()
                : string.Empty
        }))
        .Select((group, index) => {
            var members = group.OrderBy(item => string.IsNullOrWhiteSpace(item.DrawingNumber)).ThenBy(item => item.Sequence).ThenBy(item => item.Id).ToArray();
            return members[0] with {
                Sequence = index + 1, Quantity = members.Sum(item => item.Quantity),
                IsComplete = members.All(item => item.IsComplete),
                InventoryReplacementMaterialCode = members.Any(item => !string.IsNullOrWhiteSpace(item.InventoryReplacementMaterialCode)
                    && Normalize(item.InventoryReplacementMaterialCode) == Normalize(item.DrawingNumber)) ? members[0].DrawingNumber : null,
                SummaryItemIds = members.Select(item => item.Id).ToArray(),
                ReleaseReviewTokens = members.SelectMany(item => item.ReleaseReviewTokens ?? new Dictionary<Guid, string>()).ToDictionary(pair => pair.Key, pair => pair.Value),
                ReleaseConfirmation = members.All(item => item.ReleaseConfirmation is not null) ? members[0].ReleaseConfirmation : null,
                SummaryFingerprint = Fingerprint(members)
            };
        }).ToArray();
    }

    private static string Fingerprint(IEnumerable<BomItem> items) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items.OrderBy(item => item.Id).Select(item => new {
            Values = Input(item), item.Source, item.IsManuallyOverridden, item.IsPendingRemoval,
            item.IsReleaseExcluded, item.ReleaseExclusionReason, item.IsManuallyExcluded
        })))));

    public static IReadOnlyList<BomItemInput> Expand(IReadOnlyList<BomItem> existing,
        IReadOnlyList<BomSummaryEdit> edits, IReadOnlyList<BomItemInput> additions, IReadOnlyList<Guid>? order = null)
    {
        var groups = Group(existing).ToDictionary(item => item.Id);
        var updated = existing.Where(item => !item.IsManuallyExcluded).ToDictionary(item => item.Id, Input);
        var seen = new HashSet<Guid>();
        foreach (var edit in edits)
        {
            if (edit.ItemIds.Count == 0 || !groups.TryGetValue(edit.ItemIds[0], out var group)
                || group.IsManuallyExcluded || group.SummaryFingerprint != edit.Fingerprint
                || !group.SummaryItemIds!.ToHashSet().SetEquals(edit.ItemIds))
                throw new PdmConflictException("汇总物料已变化，请刷新BOM后重试。");
            if (edit.Fields.SequenceEqual(["removeEngineeringKit"]))
            {
                if (!group.EngineeringKitReferenceId.HasValue) throw new PdmRuleException("只有套件引用可以通过汇总保存移除。");
                foreach (var id in edit.ItemIds) {
                    if (!seen.Add(id)) throw new PdmRuleException("同一汇总物料不能重复提交。");
                    updated.Remove(id);
                }
                continue;
            }
            if (edit.Fields.Any(field => field is not ("drawingNumber" or "name" or "unit" or "material" or "specification" or "remark" or "brand" or "surfaceTreatment" or "heatTreatment" or "weight" or "isWearPart" or "impactStage" or "inventoryReplacementMaterialCode" or "revision" or "quantity" or "kind")))
                throw new PdmRuleException("汇总BOM包含不支持的修改字段。");
            var positiveCount = edit.ItemIds.Count(id => updated[id].Quantity > 0);
            var minimum = edit.Values.Quantity > 0 ? positiveCount * 0.0001m : 0;
            if (edit.Fields.Contains("quantity") && edit.Values.Quantity > 0 && edit.Values.Quantity < minimum)
                throw new PdmRuleException("汇总数量过小，无法保留所有实例；请先在源数据核对结构。");
            var assigned = 0m;
            for (var index = 0; index < edit.ItemIds.Count; index++)
            {
                var id = edit.ItemIds[index];
                if (!seen.Add(id)) throw new PdmRuleException("同一汇总物料不能重复提交。");
                var value = updated[id];
                foreach (var field in edit.Fields)
                    value = field switch {
                        "drawingNumber" => value with { DrawingNumber = edit.Values.DrawingNumber },
                        "name" => value with { Name = edit.Values.Name },
                        "unit" => value with { Unit = edit.Values.Unit },
                        "material" => value with { Material = edit.Values.Material },
                        "specification" => value with { Specification = edit.Values.Specification },
                        "remark" => value with { Remark = edit.Values.Remark },
                        "brand" => value with { Brand = edit.Values.Brand },
                        "surfaceTreatment" => value with { SurfaceTreatment = edit.Values.SurfaceTreatment },
                        "heatTreatment" => value with { HeatTreatment = edit.Values.HeatTreatment },
                        "weight" => value with { Weight = edit.Values.Weight },
                        "isWearPart" => value with { IsWearPart = edit.Values.IsWearPart },
                        "impactStage" => value with { ImpactStage = edit.Values.ImpactStage },
                        "inventoryReplacementMaterialCode" => value with { InventoryReplacementMaterialCode = edit.Values.InventoryReplacementMaterialCode },
                        "revision" => value with { Revision = edit.Values.Revision },
                        _ => value
                    };
                if (edit.Fields.Contains("quantity"))
                {
                    if (decimal.Round(edit.Values.Quantity, 4) != edit.Values.Quantity) throw new PdmRuleException("数量最多支持4位小数。" );
                    if (edit.Values.Quantity < 0) throw new PdmRuleException("数量不能小于0。");
                    var quantity = index == edit.ItemIds.Count - 1 ? edit.Values.Quantity - assigned
                        : group.Quantity == 0 ? 0 : (edit.Values.Quantity == 0 || value.Quantity == 0 ? 0 : 0.0001m + decimal.Floor((edit.Values.Quantity - minimum) * value.Quantity / group.Quantity * 10_000m) / 10_000m);
                    assigned += quantity;
                    value = value with { Quantity = quantity };
                }
                updated[id] = value;
            }
        }
        if (additions.Any(item => item.Id.HasValue)) throw new PdmRuleException("新增汇总行不能指定已有实例ID。");
        var ranks = new Dictionary<Guid, int>();
        if (order is not null)
        {
            if (order.Distinct().Count() != order.Count) throw new PdmRuleException("汇总排序不能包含重复物料。");
            for (var index = 0; index < order.Count; index++) {
                if (!groups.TryGetValue(order[index], out var group) || group.IsManuallyExcluded) throw new PdmConflictException("汇总排序已变化，请刷新BOM。");
                foreach (var id in group.SummaryItemIds!) ranks[id] = index;
            }
        }
        return updated.Values.Concat(additions).OrderBy(item => item.Id.HasValue && ranks.TryGetValue(item.Id.Value, out var rank) ? rank : order is null ? item.Sequence : item.Sequence - 1)
            .ThenBy(item => item.Sequence)
            .Select((item, index) => item with { Sequence = index + 1 }).ToArray();
    }

    private static BomItemInput Input(BomItem item) => new(item.Sequence, item.DrawingNumber, item.Name, item.Quantity,
        item.Unit, item.Material, item.Specification, item.Revision, item.IsComplete, item.SourceDocumentId,
        item.SourceConfiguration, item.Remark, item.Brand, item.SurfaceTreatment, item.Weight, item.IsPendingClassification,
        item.IsManualUnmatched, item.IsManuallyRetained, item.Id, item.SourceInstancePath, item.ParentDrawingNumber,
        item.HeatTreatment, item.EngineeringKitReferenceId, item.EngineeringKitId, item.EngineeringKitRevisionId,
        item.EngineeringKitCode, item.EngineeringKitVersionNumber, item.EngineeringKitComponentId,
        item.EngineeringKitComponentOptional, item.IsWearPart, item.ImpactStage, item.InventoryReplacementMaterialCode);
}
