using System.Globalization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class PdfTitleBlockSnapshotBuilder
{
    public static string[] Normalize(IEnumerable<string> fields)
    {
        var selected = fields.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(key => !PdfTitleBlockFields.All.Any(field => field.Key == key)))
            throw new PdmRuleException("PDF自动填写包含未知属性，请刷新设置页面后重新保存。");
        return PdfTitleBlockFields.All.Where(field => selected.Contains(field.Key)).Select(field => field.Key).ToArray();
    }

    public static async Task<PdfTitleBlockSnapshot> BuildAsync(IPdmRepository repository, ReleasePackage package,
        Project project, PdmSystemSettings settings, CancellationToken ct)
    {
        var fields = Normalize(settings.PdfTitleBlockFill.EnabledFields);
        var drawings = new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>();
        if (fields.Length == 0) return new(fields, drawings, true);
        var reviews = fields.Any(key => key is "reviewer" or "reviewedOn" or "approver" or "approvedOn")
            ? await repository.ListDrawingReviewPackagesAsync(project.Id, ct) : [];
        var items = ProductionDrawingQuantities.FrozenItems(package);
        foreach (var source in (await ReleaseBoundSourceReader.ReadAsync(repository, package, true, ct)).Where(s => s.Kind == DocumentKind.Drawing))
        {
            var version = await repository.FindDocumentVersionAsync(source.DocumentId, source.SourceVersionId, ct)
                ?? throw new PdmConflictException($"图纸{source.FileName}的冻结版本不存在。");
            var modelIds = ReleaseBoundSourceReader.ModelDocumentIds(version.ReferenceSnapshot).ToHashSet();
            var bom = items.Where(item => item.SourceDocumentId.HasValue && modelIds.Contains(item.SourceDocumentId.Value)).ToArray();
            var matches = reviews.Where(review => review.State == DrawingReviewPackageState.Approved)
                .SelectMany(review => review.Items.Where(item => item.DrawingDocumentId == source.DocumentId
                    && (item.EffectiveDrawingVersionId == version.Id || item.EffectiveDrawingVersionId == version.SourceVersionId))
                    .Select(item => (Review: review, Item: item))).ToArray();
            var reviewMatch = matches.OrderByDescending(match => match.Review.ApprovedAt).FirstOrDefault();
            var values = new Dictionary<string, string>();
            foreach (var key in fields)
            {
                if (key == "quantity")
                {
                    if (bom.Length == 0 || bom.Select(BomReleaseAggregation.MaterialKey).Distinct().Count() != 1)
                        throw new PdmRuleException($"图纸{source.FileName}没有唯一的发布物料和单位，无法填写本次下发数量。");
                    continue; // 数量在正式发布时冻结，不能在排队重转时重新计算。
                }
                string? value = key switch
                {
                    "designer" or "drafter" => Property(version, null, "设计"),
                    "designedOn" => DesignDate(Property(version, null, "设计日期"), source.FileName),
                    "reviewer" => reviewMatch.Item?.DrawingReviewerName,
                    "reviewedOn" => Date(reviewMatch.Item?.DrawingReviewedAt),
                    "approver" => reviewMatch.Review?.SupervisorReviewedByName,
                    "approvedOn" => Date(reviewMatch.Review?.SupervisorReviewedAt),
                    "projectNumber" => project.Code,
                    "projectName" => project.Name,
                    "revision" => source.ExpectedFormalRevision,
                    _ => await MaterialValueAsync(repository, package, version, bom, key, settings, source.FileName, ct)
                };
                if (string.IsNullOrWhiteSpace(value))
                    throw new PdmRuleException($"图纸{source.FileName}缺少已启用的“{PdfTitleBlockFields.Get(key).Name}”，请补齐来源数据并重新提交发布。");
                values[key] = value.Trim();
            }
            var binding = package.VersionBindings!.Single(item => item.DocumentId == source.DocumentId);
            drawings[source.DocumentId] = new(binding.VersionId, binding.Sha256, values, bom.Select(item => item.Id).ToArray());
        }
        return new(fields, drawings, !fields.Any(key => key is "quantity" or "materialCode"));
    }

    public static PdfTitleBlockSnapshot FreezeQuantities(ReleasePackage package, IEnumerable<ReleasePackage> history)
    {
        var snapshot = package.PdfTitleBlockSnapshot!;
        if (snapshot.QuantitiesFrozen) return snapshot;
        var quantities = ProductionDrawingQuantities.Build(package, history);
        var items = ProductionDrawingQuantities.FrozenItems(package);
        var drawings = snapshot.Drawings.ToDictionary(pair => pair.Key, pair =>
        {
            var values = new Dictionary<string, string>(pair.Value.Values);
            if (snapshot.EnabledFields.Contains("quantity"))
            {
                var matches = quantities.Where(q => q.SourceBomItemIds.Any(pair.Value.SourceBomItemIds.Contains)).ToArray();
                if (matches.Length != 1) throw new PdmConflictException("图纸的下发数量与冻结物料不唯一，请核对发布快照。");
                values["quantity"] = matches[0].NetIssueQuantity.ToString("0.############################", CultureInfo.InvariantCulture);
            }
            if (snapshot.EnabledFields.Contains("materialCode"))
            {
                // 自动申请料号在最终审批后完成；只同步同一冻结BOM行的正式料号。
                var codes = items.Where(item => pair.Value.SourceBomItemIds.Contains(item.Id)).Select(item => item.DrawingNumber).Distinct().ToArray();
                if (codes.Length != 1 || string.IsNullOrWhiteSpace(codes[0])) throw new PdmConflictException("图纸的正式物料编码不唯一，请核对发布快照。");
                values["materialCode"] = codes[0];
            }
            return pair.Value with { Values = values };
        });
        return snapshot with { Drawings = drawings, QuantitiesFrozen = true };
    }

    private static string? Property(DocumentVersion version, string? configuration, string name) =>
        CadPropertyCardSnapshot.Read(version.PropertySnapshot, configuration ?? string.Empty, name)?.Trim();

    private static string? Date(DateTimeOffset? date) => date?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? DesignDate(string? value, string fileName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        throw new PdmRuleException($"图纸{fileName}的设计日期无法识别，请按yyyy-MM-dd填写后重新存档。");
    }

    private static string? BomValue(BomItem item, string key) => key switch
    {
        "name" => item.Name, "specification" => item.Specification, "material" => item.Material,
        "surfaceTreatment" => item.SurfaceTreatment, "heatTreatment" => item.HeatTreatment,
        "wearPart" => item.IsWearPart ? "是" : "否", "materialCode" => item.DrawingNumber, "weight" => item.Weight,
        _ => throw new PdmRuleException("不支持的PDF物料属性。")
    };

    private static async Task<string?> MaterialValueAsync(IPdmRepository repository, ReleasePackage package,
        DocumentVersion drawing, IReadOnlyList<BomItem> items, string key, PdmSystemSettings settings, string fileName, CancellationToken ct)
    {
        var values = items.Select(item => BomValue(item, key)?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length != 1) throw new PdmRuleException($"图纸{fileName}对应的“{PdfTitleBlockFields.Get(key).Name}”不唯一，请核对模型配置和发布BOM。");
        var propertyKey = key == "materialCode" ? "drawingNumber" : key;
        var propertyName = BomPropertyMappingCatalog.SolidWorksProperty(settings, propertyKey, PdfTitleBlockFields.Get(key).Name);
        void Check(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (key == "wearPart") value = BomPropertyMappingCatalog.IsWearPartValue(value) ? "是" : "否";
            if (!string.Equals(value.Trim(), values[0], StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException($"图纸{fileName}的“{PdfTitleBlockFields.Get(key).Name}”与冻结BOM不一致，请修正、重新存档并审核后发布。");
        }
        foreach (var item in items)
        {
            Check(Property(drawing, item.SourceConfiguration, propertyName));
            var binding = package.VersionBindings?.SingleOrDefault(b => b.DocumentId == item.SourceDocumentId);
            if (binding is null) throw new PdmConflictException($"图纸{fileName}的模型没有冻结版本，无法核对物料属性。");
            var model = await repository.FindDocumentVersionAsync(binding.DocumentId, binding.VersionId, ct)
                ?? throw new PdmConflictException($"图纸{fileName}的冻结模型版本不存在。");
            Check(Property(model, item.SourceConfiguration, propertyName));
        }
        return values[0];
    }

    public static DocumentVersion DeliveryVersion(ReleasePackage package, DocumentVersion version) =>
        package.PdfTitleBlockSnapshot is { Enabled: true } snapshot && snapshot.Drawings.ContainsKey(version.DocumentId)
            ? version with { Preview = package.PdfDeliveryArtifacts.GetValueOrDefault(version.DocumentId) } : version;
}
