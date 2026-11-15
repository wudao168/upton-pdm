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

    public static Dictionary<string, string> NormalizeDrawingLabels(IReadOnlyDictionary<string, string>? labels)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels is null) return normalized;
        foreach (var (key, raw) in labels.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!PdfTitleBlockFields.All.Any(field => field.Key == key))
                throw new PdmRuleException("PDF图框映射包含未知字段，请刷新后重试。");
            var label = raw?.Trim() ?? "";
            if (label.Length > 60 || label.Any(char.IsControl) || PdfTitleBlockFields.NormalizeLabel(label).Length == 0)
                throw new PdmRuleException($"“{PdfTitleBlockFields.Get(key).Name}”的图框标签须为1至60个字符。");
            normalized.Add(key, label);
        }
        return normalized;
    }

    public static async Task<PdfTitleBlockSnapshot> BuildAsync(IPdmRepository repository, ReleasePackage package,
        Project project, PdmSystemSettings settings, CancellationToken ct) =>
        await BuildAsync(repository, package, project, settings.PdfTitleBlockFill, ct);

    private static async Task<PdfTitleBlockSnapshot> BuildAsync(IPdmRepository repository, ReleasePackage package,
        Project project, PdfTitleBlockFillSettings settings, CancellationToken ct)
    {
        var fields = Normalize(settings.EnabledFields);
        var drawings = new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>();
        if (fields.Length == 0) return new(fields, drawings, true);
        var merged = package.DrawingReview is not null;
        var reviews = !merged && fields.Any(key => key is "designer" or "drafter" or "designedOn" or "reviewer" or "reviewedOn" or "approver" or "approvedOn")
            ? await repository.ListDrawingReviewPackagesAsync(project.Id, ct) : [];
        var items = ProductionDrawingQuantities.FrozenItems(package);
        foreach (var source in (await ReleaseBoundSourceReader.ReadAsync(repository, package, true, ct)).Where(s => s.Kind == DocumentKind.Drawing))
        {
            var version = await repository.FindDocumentVersionAsync(source.DocumentId, source.SourceVersionId, ct)
                ?? throw new PdmConflictException($"图纸{source.FileName}的冻结版本不存在。");
            var modelIds = ReleaseBoundSourceReader.ModelDocumentIds(package, source.DocumentId, version.ReferenceSnapshot).ToHashSet();
            var bom = items.Where(item => item.SourceDocumentId.HasValue && modelIds.Contains(item.SourceDocumentId.Value)).ToArray();
            var matches = reviews.Where(review => review.State == DrawingReviewPackageState.Approved)
                .SelectMany(review => review.Items.Where(item => item.DrawingDocumentId == source.DocumentId
                    && (item.EffectiveDrawingVersionId == version.Id || item.EffectiveDrawingVersionId == version.SourceVersionId))
                    .Select(item => (Review: review, Item: item))).ToArray();
            var reviewMatch = matches.OrderByDescending(match => match.Review.ApprovedAt).FirstOrDefault();
            // 审核回写和正式版本的创建人不代表设计人；追溯本张图纸实际送审的存档版本。
            var designVersion = version;
            if (fields.Any(key => key is "designer" or "drafter" or "designedOn"))
            {
                var designVersionId = reviewMatch.Item?.DrawingVersionId ?? version.SourceVersionId ?? version.Id;
                if (designVersionId != version.Id)
                    designVersion = await repository.FindDocumentVersionAsync(source.DocumentId, designVersionId, ct)
                        ?? throw new PdmConflictException($"图纸{source.FileName}的PLM送审存档版本不存在，请核对图纸审核记录。");
            }
            var designer = fields.Any(key => key is "designer" or "drafter")
                ? await PersonNameAsync(repository, designVersion.CreatedBy, null, ct) : null;
            var values = new Dictionary<string, string>();
            foreach (var key in fields)
            {
                // 当前发布审批尚未发生；仅冻结来源规则，实际签字在最终批准后取值。
                if (merged && (IsApprovalField(key) || key == "materialCode")) continue;
                if (key == "quantity")
                {
                    if (bom.Length == 0 || bom.Select(BomReleaseAggregation.MaterialKey).Distinct().Count() != 1)
                        throw new PdmRuleException($"图纸{source.FileName}没有唯一的发布物料和单位，无法填写本次下发数量。");
                    continue; // 数量在正式发布时冻结，不能在排队重转时重新计算。
                }
                string? value = key switch
                {
                    "designer" or "drafter" => designer,
                    "designedOn" => Date(designVersion.CreatedAt),
                    "reviewer" => await PersonNameAsync(repository, reviewMatch.Item?.DrawingReviewer, reviewMatch.Item?.DrawingReviewerName, ct),
                    "reviewedOn" => Date(reviewMatch.Item?.DrawingReviewedAt),
                    "approver" => await PersonNameAsync(repository, reviewMatch.Review?.SupervisorReviewedBy, reviewMatch.Review?.SupervisorReviewedByName, ct),
                    "approvedOn" => Date(reviewMatch.Review?.SupervisorReviewedAt),
                    "projectNumber" => project.Code,
                    "projectName" => project.Name,
                    "revision" => source.ExpectedFormalRevision,
                    _ => MaterialValue(bom, key, source.FileName)
                };
                if (string.IsNullOrWhiteSpace(value))
                    throw new PdmRuleException($"图纸{source.FileName}缺少已启用的“{PdfTitleBlockFields.Get(key).Name}”，请补齐PLM中的{SourceDescription(key)}后重新提交发布。");
                values[key] = value.Trim();
            }
            var binding = package.VersionBindings!.Single(item => item.DocumentId == source.DocumentId);
            drawings[source.DocumentId] = new(binding.VersionId, binding.Sha256, values, bom.Select(item => item.Id).ToArray());
        }
        var labels = NormalizeDrawingLabels(settings.DrawingLabels);
        return new(fields, drawings, !fields.Any(key => key is "quantity" or "materialCode" || merged && IsApprovalField(key)))
        {
            UseReleaseApproval = merged,
            DrawingLabels = fields.Where(key => labels.ContainsKey(key) || PdfTitleBlockFields.PersonLabels.ContainsKey(key))
                .ToDictionary(key => key, key => labels.GetValueOrDefault(key, PdfTitleBlockFields.DefaultLabel(key)))
        };
    }

    public static async Task<ReleasePackage> RestoreMissingLongLeadSnapshotAsync(IPdmRepository repository, ReleasePackage package,
        Project project, CancellationToken ct)
    {
        if (!ReleasePreviewPolicy.DeliversFrozenDrawings(package)
            || package.PdfTitleBlockSnapshot is not { Enabled: true, Drawings.Count: 0 } missing
            || package.DrawingReview?.Items.Count is not > 0) return package;

        var snapshot = await BuildAsync(repository, package, project, new PdfTitleBlockFillSettings
        {
            EnabledFields = missing.EnabledFields,
            DrawingLabels = missing.DrawingLabels
        }, ct);
        if (snapshot.Drawings.Count != package.DrawingReview.Items.Count)
            throw new PdmConflictException("长交期发布包的PDF图签与冻结审核图纸清单不一致。");
        var restored = await FreezeForPublicationAsync(repository, package with { PdfTitleBlockSnapshot = snapshot },
            await repository.ListReleasePackagesAsync(package.ProjectId, ct), ct);
        return await repository.SaveReleasePdfTitleBlockAsync(package.Id, restored, ct);
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

    private static bool IsApprovalField(string key) => key is "reviewer" or "reviewedOn" or "approver" or "approvedOn";

    public static async Task<PdfTitleBlockSnapshot> FreezeForPublicationAsync(IPdmRepository repository,
        ReleasePackage package, IEnumerable<ReleasePackage> history, CancellationToken ct)
    {
        var snapshot = package.PdfTitleBlockSnapshot!;
        if (snapshot.QuantitiesFrozen) return snapshot;
        var frozen = FreezeQuantities(package, history);
        if (!snapshot.UseReleaseApproval || !snapshot.EnabledFields.Any(IsApprovalField)) return frozen;
        if (package.ApprovalTasks.Count == 0 || package.ApprovalTasks.Any(t => t.Decision != ApprovalDecision.Approved))
            throw new PdmConflictException("发布审批尚未全部通过，不能生成正式PDF签字。");
        var review = ReleaseDrawingReview.ReviewTask(package);
        var approval = package.ApprovalTasks.OrderBy(t => t.StepOrder).LastOrDefault(t => t.Stage == ApprovalStage.MechanicalSupervisor)
            ?? package.ApprovalTasks.OrderBy(t => t.StepOrder).Last();
        async Task<string> Names(ApprovalTask? task)
        {
            if (task is null) throw new PdmConflictException("发布包缺少实际审核节点，无法填写PDF。");
            var actors = (task.Participants?.Signatures ?? []).Concat(task.Participants?.Departments.SelectMany(d => d.Signatures) ?? [])
                .Select(s => s.Actor).Append(task.DecisionBy ?? "").Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var actor in actors) names.Add(await PersonNameAsync(repository, actor, null, ct) ?? "姓名待补充");
            if (names.Count == 0) throw new PdmConflictException("发布审批缺少实际签字人员，请核对审批记录。");
            return string.Join("、", names);
        }
        var values = new Dictionary<string, string>();
        foreach (var key in snapshot.EnabledFields.Where(IsApprovalField))
        {
            var value = key switch {
                "reviewer" => await Names(review), "reviewedOn" => Date(review?.DecidedAt),
                "approver" => await Names(approval), _ => Date(approval.DecidedAt)
            };
            if (string.IsNullOrWhiteSpace(value)) throw new PdmConflictException("发布审批缺少实际处理日期，请核对审批记录。");
            values[key] = value;
        }
        return frozen with { Drawings = frozen.Drawings.ToDictionary(p => p.Key, p => {
            var filled = new Dictionary<string,string>(p.Value.Values);
            foreach (var entry in values) filled[entry.Key] = entry.Value;
            return p.Value with { Values = filled };
        }) };
    }

    private static string? Date(DateTimeOffset? date) => date?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<string?> PersonNameAsync(IPdmRepository repository, string? username, string? snapshotName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(snapshotName) && !string.Equals(snapshotName, username, StringComparison.OrdinalIgnoreCase))
            return snapshotName.Trim();
        if (string.IsNullOrWhiteSpace(username)) return null;
        var user = await repository.FindUserAsync(username, ct);
        return string.IsNullOrWhiteSpace(user?.DisplayName) || string.Equals(user.DisplayName, username, StringComparison.OrdinalIgnoreCase)
            ? "姓名待补充" : user.DisplayName.Trim();
    }

    private static string SourceDescription(string key) => key switch
    {
        "designer" or "drafter" or "designedOn" => "图纸送审版本存档记录",
        "reviewer" or "reviewedOn" => "图纸实际审核记录",
        "approver" or "approvedOn" => "图纸实际批准记录",
        "projectNumber" or "projectName" => "项目信息",
        "revision" => "图纸发布版本信息",
        _ => "发布BOM物料信息"
    };

    private static string? BomValue(BomItem item, string key) => key switch
    {
        "name" => item.Name, "specification" => item.Specification, "material" => item.Material,
        "surfaceTreatment" => item.SurfaceTreatment, "heatTreatment" => item.HeatTreatment,
        "wearPart" => item.IsWearPart ? "是" : "否", "materialCode" => item.DrawingNumber, "weight" => item.Weight,
        _ => throw new PdmRuleException("不支持的PDF物料属性。")
    };

    private static string? MaterialValue(IReadOnlyList<BomItem> items, string key, string fileName)
    {
        var values = items.Select(item => BomValue(item, key)?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length != 1) throw new PdmRuleException($"图纸{fileName}对应的“{PdfTitleBlockFields.Get(key).Name}”不唯一，请核对模型配置和发布BOM。");
        // PDF 图签以 PLM 发布 BOM 为准，CAD 属性既不参与取值，也不作为一致性门槛。
        return values[0];
    }

    public static DocumentVersion DeliveryVersion(ReleasePackage package, DocumentVersion version) =>
        package.PdfTitleBlockSnapshot is { Enabled: true } snapshot && snapshot.Drawings.ContainsKey(version.DocumentId)
            ? version with { Preview = package.PdfDeliveryArtifacts.GetValueOrDefault(version.DocumentId) } : version;
}
