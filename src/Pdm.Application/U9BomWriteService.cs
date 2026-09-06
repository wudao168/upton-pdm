using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9BomWriteService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9OpenApiClient writeClient,
    IU9BomQueryClient queryClient,
    IU9SecretProtector secretProtector,
    TimeProvider timeProvider)
{
    private sealed record ReconciledCommand(
        U9BomWriteCommand Command,
        IReadOnlyList<U9BomQuantityReconciliation> Quantities);

    public async Task<U9BomWritePreview> PreviewAsync(
        U9BomWriteCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await DemandPermissionAsync(actor, role, cancellationToken);
        return await PreviewCoreAsync(command, cancellationToken);
    }

    internal async Task<U9BomWritePreview> PreviewUpsertAsync(
        U9BomWriteCommand command,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(command with { Operation = U9BomWriteOperation.Create });
        var context = await LoadContextAsync(cancellationToken);
        var current = await QueryAsync(context, normalized.ItemCode, normalized.BomVersionCode, cancellationToken);
        var operation = FindMatching(current, normalized) is null
            ? U9BomWriteOperation.Create
            : U9BomWriteOperation.Modify;
        normalized = normalized with { Operation = operation };
        var reconciliation = ReconcileComponentTotals(normalized, current);
        normalized = reconciliation.Command;
        ValidateCurrent(normalized, current);
        return BuildPreview(context, normalized, current, reconciliation.Quantities);
    }

    private async Task<U9BomWritePreview> PreviewCoreAsync(
        U9BomWriteCommand command,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(command);
        var context = await LoadContextAsync(cancellationToken);
        var current = await QueryAsync(context, normalized.ItemCode, normalized.BomVersionCode, cancellationToken);
        var reconciliation = ReconcileComponentTotals(normalized, current);
        normalized = reconciliation.Command;
        ValidateCurrent(normalized, current);
        return BuildPreview(context, normalized, current, reconciliation.Quantities);
    }

    public async Task<U9BomWriteExecution> ExecuteAsync(
        U9BomWriteCommand command,
        string requestSha256,
        string confirmation,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await DemandPermissionAsync(actor, role, cancellationToken);
        return await ExecuteCoreAsync(command, requestSha256, confirmation, actor, cancellationToken);
    }

    internal Task<U9BomWriteExecution> ExecuteAuthorizedAsync(
        U9BomWriteCommand command,
        string requestSha256,
        string confirmation,
        string actor,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(command, requestSha256, confirmation, actor, cancellationToken);

    internal Task<U9BomWriteExecution> ExecuteApprovalAutomationAsync(
        U9BomWriteCommand command,
        U9BomWritePreview preview,
        string actor,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(command, preview.RequestSha256, preview.RequiredConfirmation, actor, cancellationToken);

    private async Task<U9BomWriteExecution> ExecuteCoreAsync(
        U9BomWriteCommand command,
        string requestSha256,
        string confirmation,
        string actor,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewCoreAsync(command, cancellationToken);
        if (!string.Equals(preview.RequestSha256, Required(requestSha256, "请求SHA-256"), StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("BOM请求已变化，请重新生成预览后再确认。");
        if (!string.Equals(preview.RequiredConfirmation, confirmation?.Trim(), StringComparison.Ordinal))
            throw new PdmRuleException($"确认文字不正确，请输入“{preview.RequiredConfirmation}”。");

        var normalized = Normalize(command);
        var context = await LoadContextAsync(cancellationToken);
        var authentication = await AuthenticateAsync(context, cancellationToken);
        var current = normalized.Operation == U9BomWriteOperation.Create
            ? new U9BomQueryResult(0, null, [])
            : await QueryAsync(context, normalized.ItemCode, normalized.BomVersionCode, cancellationToken);
        normalized = ReconcileComponentTotals(normalized, current).Command;
        if (normalized.Operation != U9BomWriteOperation.Create
            && !string.Equals(Fingerprint(current), preview.BaselineSha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("BOM在确认后已被其他客户端修改，请重新生成预览。");
        var write = await writeClient.PostBatchAsync(
            context.BaseUrl, preview.Path, authentication.Token, preview.RequestPreview, cancellationToken);
        EnsureWriteSucceeded(write, "写入");

        var verification = await QueryAsync(context, normalized.ItemCode, normalized.BomVersionCode, cancellationToken);
        VerifyResult(normalized, current, verification);
        var executedAt = timeProvider.GetUtcNow();
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), executedAt, actor, $"u9.bom.{command.Operation.ToString().ToLowerInvariant()}",
            nameof(U9BomReference), $"{normalized.ItemCode}/{normalized.BomVersionCode}",
            $"已执行U9C BOM{OperationName(normalized.Operation)}；A1固定版本；新增{preview.AddedComponentCount}项；U9C历史保留{preview.RetainedHistoricalComponentCount}项；接口{preview.Path}；请求SHA-256 {preview.RequestSha256}；自动回查通过。"), cancellationToken);
        return new(preview, write, verification, executedAt);
    }

    private U9BomWritePreview BuildPreview(
        WriteContext context,
        U9BomWriteCommand normalized,
        U9BomQueryResult current,
        IReadOnlyList<U9BomQuantityReconciliation> quantityReconciliations)
    {
        var payload = BuildPayload(context.OrganizationCode, normalized, current);
        var baseline = Fingerprint(current);
        var (addedCount, retainedCount) = ComponentDelta(normalized, current);
        return new(
            normalized.Operation,
            PathFor(context, normalized.Operation),
            payload,
            Hash($"{normalized.Operation}\n{baseline}\n{payload}"),
            baseline,
            ConfirmationFor(normalized),
            addedCount,
            retainedCount,
            quantityReconciliations,
            timeProvider.GetUtcNow());
    }

    private async Task DemandPermissionAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权维护U9C BOM。");
    }

    private async Task<WriteContext> LoadContextAsync(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!configuration.WriteEnabled)
            throw new PdmRuleException("U9C真实写入尚未启用，不能执行BOM创建或追加修改。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("请先保存U9C应用密钥，再维护BOM。");
        return new(
            Required(configuration.BaseUrl, "U9C地址"),
            Required(configuration.EnterpriseCode, "U9C企业编码"),
            Required(configuration.OrganizationCode, "U9C组织编码"),
            Required(configuration.UserCode, "U9C用户编码"),
            Required(configuration.ClientId, "U9C应用ID"),
            secretProtector.Unprotect(configuration.ClientSecretCiphertext),
            Required(configuration.BomCreatePath, "BOM创建接口"),
            Required(configuration.BomQueryPath, "BOM查询接口"),
            Required(configuration.BomModifyPath, "BOM修改接口"));
    }

    private Task<U9AuthenticationResult> AuthenticateAsync(WriteContext context, CancellationToken cancellationToken) =>
        queryClient.AuthenticateAsync(new(
            context.BaseUrl, context.EnterpriseCode, context.OrganizationCode,
            context.UserCode, context.ClientId, context.ClientSecret), cancellationToken);

    private async Task<U9BomQueryResult> QueryAsync(
        WriteContext context,
        string itemCode,
        string versionCode,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, cancellationToken);
        var payload = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["Org"] = Archive(context.OrganizationCode),
                ["ItemMaster"] = Archive(itemCode),
                ["BOMVersionCode"] = versionCode
            }
        });
        var result = await queryClient.QueryBomsAsync(context.BaseUrl, context.BomQueryPath, authentication.Token, payload, cancellationToken);
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C BOM前置查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");
        return result;
    }

    private static U9BomWriteCommand Normalize(U9BomWriteCommand command)
    {
        if (command.Operation == U9BomWriteOperation.Delete)
            throw new PdmRuleException("U9C BOM长期保持A1且已有业务引用，PLM禁止删除整张BOM。");
        var itemCode = Required(command.ItemCode, "母件料号");
        var requestedVersionCode = Required(command.BomVersionCode, "BOM版本");
        if (!string.Equals(requestedVersionCode, "A1", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C BOM版本固定为A1，不允许创建或修改其他版本。");
        var productUomCode = Required(command.ProductUomCode, "产品计量单位");
        if (command.Lot <= 0) throw new PdmRuleException("生产批量必须大于0。");
        if (command.Components.Count == 0
            && !(command.Operation == U9BomWriteOperation.Create && command.AllowEmptyCreate))
            throw new PdmRuleException("创建或修改BOM时至少需要一条子件。");

        var components = command.Components.Select(component =>
        {
            if (component.Sequence <= 0) throw new PdmRuleException("子件行号必须大于0。");
            if (component.UsageQty <= 0) throw new PdmRuleException($"子件{component.Sequence}的用量必须大于0。");
            if (component.ParentQty <= 0) throw new PdmRuleException($"子件{component.Sequence}的母件底数必须大于0。");
            if (component.IsDelete)
                throw new PdmRuleException("U9C A1采用只追加策略，不能提交删除行。");
            return component with
            {
                ItemCode = Required(component.ItemCode, $"子件{component.Sequence}料号"),
                IssueUomCode = Required(component.IssueUomCode, $"子件{component.Sequence}发料单位"),
                ItemVersionCode = NormalizeText(component.ItemVersionCode),
                Remark = NormalizeText(component.Remark)
            };
        }).ToArray();
        if (components.GroupBy(component => component.Sequence).Any(group => group.Count() > 1))
            throw new PdmRuleException("子件行号不能重复。");

        return command with
        {
            ItemCode = itemCode,
            BomVersionCode = "A1",
            ProductUomCode = productUomCode,
            Components = components,
            ProjectMapNum = NormalizeText(command.ProjectMapNum),
            Explain = NormalizeText(command.Explain)
        };
    }

    private static ReconciledCommand ReconcileComponentTotals(U9BomWriteCommand command, U9BomQueryResult current)
    {
        if (!command.ReconcileComponentTotals) return new(command, []);
        static string Key(string itemCode, string? unitCode, decimal parentQty) =>
            $"{itemCode.Trim()}|{unitCode?.Trim()}|{parentQty.ToString("G29", CultureInfo.InvariantCulture)}";
        var desired = command.Components
            .GroupBy(component => Key(component.ItemCode, component.IssueUomCode, component.ParentQty), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Template: group.First(), Total: group.Sum(component => component.UsageQty)))
            .ToArray();
        if (command.Operation != U9BomWriteOperation.Modify)
            return new(command, desired.Select(item => new U9BomQuantityReconciliation(
                item.Template.ItemCode, item.Template.IssueUomCode, item.Template.ParentQty,
                item.Total, 0, item.Total)).ToArray());

        var existing = FindMatching(current, command)?.Components
            .Where(component => component.IsDelete != true)
            .ToArray() ?? [];
        if (existing.Any(component => component.Sequence is null))
            throw new PdmRuleException("U9C BOM返回了缺少项次的历史子件，不能安全计算追加数量，请人工复核。");
        foreach (var item in desired)
        {
            var related = existing.Where(component => string.Equals(
                component.ItemCode?.Trim(), item.Template.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (related.Any(component => !component.UsageQty.HasValue
                || !component.ParentQty.HasValue
                || string.IsNullOrWhiteSpace(component.IssueUomCode)))
                throw new PdmRuleException($"U9C BOM中子件{item.Template.ItemCode}的数量、发料单位或母件底数不完整，不能安全计算追加数量，请人工复核。");
            if (related.Any(component => !string.Equals(
                Key(component.ItemCode!, component.IssueUomCode, component.ParentQty!.Value), item.Key,
                StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException($"U9C BOM中子件{item.Template.ItemCode}存在不同发料单位或母件底数，不能自动合并数量，请人工复核。");
        }
        var existingTotals = existing
            .Where(component => !string.IsNullOrWhiteSpace(component.ItemCode) && component.UsageQty.HasValue && component.ParentQty.HasValue)
            .GroupBy(component => Key(component.ItemCode!, component.IssueUomCode, component.ParentQty!.Value), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(component => component.UsageQty!.Value), StringComparer.OrdinalIgnoreCase);
        var nextSequence = existing.Select(component => component.Sequence!.Value).DefaultIfEmpty(0).Max();
        var additions = new List<U9BomComponentCommand>();
        var quantities = new List<U9BomQuantityReconciliation>();
        foreach (var (key, template, total) in desired)
        {
            var existingTotal = existingTotals.GetValueOrDefault(key);
            if (existingTotal > total)
                throw new PdmRuleException($"U9C BOM中子件{template.ItemCode}现有总用量{FormatQuantity(existingTotal)}大于PLM审核总量{FormatQuantity(total)}；只追加策略不能自动减少，请人工复核。");
            var delta = total - existingTotal;
            quantities.Add(new(template.ItemCode, template.IssueUomCode, template.ParentQty, total, existingTotal, delta));
            if (delta <= 0) continue;
            nextSequence = ((nextSequence / 10) + 1) * 10;
            additions.Add(template with { Sequence = nextSequence, UsageQty = delta });
        }
        return new(command with { Components = additions }, quantities);
    }

    private static string FormatQuantity(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static void ValidateCurrent(U9BomWriteCommand command, U9BomQueryResult current)
    {
        var matching = FindMatching(current, command);
        if (command.Operation == U9BomWriteOperation.Create)
        {
            if (matching is not null) throw new PdmRuleException("U9C中已存在相同母件和版本的BOM，不能重复创建。");
            return;
        }

        if (matching is null) throw new PdmRuleException("U9C中未找到要修改的A1 BOM。");
        if (!IsPdmOwned(matching, command))
            throw new PdmRuleException("该BOM不是由PLM受控创建，当前仅允许查询，不能修改。");
        ValidateAppendOnlyModification(command, matching);
    }

    private static void EnsureWriteSucceeded(U9BusinessBatchResult result, string action)
    {
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C BOM{action}失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");
        var row = result.Rows.SingleOrDefault()
            ?? throw new PdmRuleException($"U9C BOM{action}未返回逐条处理结果，无法确认成功。");
        if (!row.IsSuccess)
            throw new PdmRuleException($"U9C BOM{action}失败：{row.ErrorMessage ?? "未返回错误说明"}。");
    }

    private static void VerifyResult(U9BomWriteCommand command, U9BomQueryResult beforeWrite, U9BomQueryResult verification)
    {
        var matching = FindMatching(verification, command);
        if (matching is null) throw new PdmRuleException("U9C已返回写入成功，但自动回查未找到该BOM版本。");
        if (!IsPdmOwned(matching, command))
            throw new PdmRuleException("U9C自动回查的BOM缺少PLM来源标识。");

        var expected = command.Components.OrderBy(component => component.Sequence).ToArray();
        var actual = matching.Components.Where(component => component.IsDelete != true).OrderBy(component => component.Sequence).ToArray();
        if (command.Operation == U9BomWriteOperation.Create)
        {
            if (expected.Length == actual.Length && expected.All(component =>
                    actual.Any(reference => SameCore(component, reference)))) return;
            throw new PdmRuleException(
                $"U9C自动回查的BOM子件与PLM提交内容不一致：期望{DescribeComponents(expected)}；实际{DescribeComponents(actual)}。");
        }

        var baseline = FindMatching(beforeWrite, command)?.Components.Where(component => component.IsDelete != true).ToArray() ?? [];
        if (baseline.Any(component => !actual.Any(reference => SameCore(component, reference))))
            throw new PdmRuleException("U9C追加修改后回查发现原有子件被删除或变更，已停止确认本次同步成功。");
        var baselineSequences = baseline.Select(component => component.Sequence).ToHashSet();
        var additions = expected.Where(component => !baselineSequences.Contains(component.Sequence)).ToArray();
        if (additions.Any(component => !actual.Any(reference => SameCore(component, reference))))
            throw new PdmRuleException($"U9C追加修改后回查缺少新增子件：{DescribeComponents(additions)}。");
    }

    private static U9BomReference? FindMatching(U9BomQueryResult result, U9BomWriteCommand command) =>
        result.Boms.FirstOrDefault(bom =>
            string.Equals(bom.ItemCode, command.ItemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bom.BomVersionCode, command.BomVersionCode, StringComparison.OrdinalIgnoreCase));

    private static void ValidateAppendOnlyModification(U9BomWriteCommand command, U9BomReference current)
    {
        var currentBySequence = current.Components
            .Where(component => component.IsDelete != true && component.Sequence is not null)
            .ToDictionary(component => component.Sequence!.Value);
        foreach (var component in command.Components)
        {
            if (!currentBySequence.TryGetValue(component.Sequence, out var existing)) continue;
            if (!SameCore(component, existing))
                throw new PdmRuleException($"U9C A1的原有项次{component.Sequence}不能修改料号、用量、单位或母件底数；请保留原行并新增项次。");
        }
    }

    private static (int Added, int Retained) ComponentDelta(U9BomWriteCommand command, U9BomQueryResult current)
    {
        if (command.Operation == U9BomWriteOperation.Create) return (command.Components.Count, 0);
        var existing = FindMatching(current, command)?.Components
            .Where(component => component.IsDelete != true && component.Sequence is not null)
            .ToArray() ?? [];
        var existingSequences = existing.Select(component => component.Sequence!.Value).ToHashSet();
        var requestedSequences = command.Components.Select(component => component.Sequence).ToHashSet();
        return (
            command.Components.Count(component => !existingSequences.Contains(component.Sequence)),
            existing.Count(component => !requestedSequences.Contains(component.Sequence!.Value)));
    }

    private static bool SameCore(U9BomComponentCommand expected, U9BomComponentReference actual) =>
        expected.Sequence == actual.Sequence
        && string.Equals(expected.ItemCode, actual.ItemCode, StringComparison.OrdinalIgnoreCase)
        && expected.UsageQty == actual.UsageQty
        && expected.ParentQty == actual.ParentQty
        && string.Equals(expected.IssueUomCode, actual.IssueUomCode, StringComparison.OrdinalIgnoreCase);

    private static bool SameCore(U9BomComponentReference expected, U9BomComponentReference actual) =>
        expected.Sequence == actual.Sequence
        && string.Equals(expected.ItemCode, actual.ItemCode, StringComparison.OrdinalIgnoreCase)
        && expected.UsageQty == actual.UsageQty
        && expected.ParentQty == actual.ParentQty
        && string.Equals(expected.IssueUomCode, actual.IssueUomCode, StringComparison.OrdinalIgnoreCase);

    private static string DescribeComponents(IEnumerable<U9BomComponentCommand> components) =>
        string.Join(",", components.Select(component =>
            $"{component.Sequence}/{component.ItemCode}/{component.UsageQty.ToString(CultureInfo.InvariantCulture)}/{component.IssueUomCode}/{component.ParentQty.ToString(CultureInfo.InvariantCulture)}"));

    private static string DescribeComponents(IEnumerable<U9BomComponentReference> components) =>
        string.Join(",", components.Select(component =>
            $"{component.Sequence}/{component.ItemCode}/{component.UsageQty?.ToString(CultureInfo.InvariantCulture)}/{component.IssueUomCode}/{component.ParentQty?.ToString(CultureInfo.InvariantCulture)}"));

    private static string BuildPayload(
        string organizationCode,
        U9BomWriteCommand command,
        U9BomQueryResult current)
    {
        var row = new Dictionary<string, object?>
        {
            ["BOMComponents"] = BuildComponents(command, current),
            ["ItemMaster"] = Archive(command.ItemCode),
            ["BOMVersionCode"] = command.BomVersionCode,
            ["AlternateType"] = 0,
            ["Lot"] = command.Lot,
            ["ProductUOM"] = Archive(command.ProductUomCode),
            ["EffectiveDate"] = FormatDate(command.EffectiveDate ?? new DateOnly(2000, 1, 1)),
            ["DisableDate"] = FormatDate(command.DisableDate ?? new DateOnly(9999, 12, 31)),
            ["FromQty"] = 0,
            ["ToQty"] = 0,
            ["IsPrimaryLot"] = true,
            ["Status"] = 0,
            ["Org"] = Archive(organizationCode),
            ["BOMSort"] = command.BomSort,
            ["ProjectMapNum"] = command.ProjectMapNum ?? string.Empty,
            ["Explain"] = AddOwnershipStamp(command.Explain, command.ItemCode, command.BomVersionCode),
            ["BOMType"] = command.BomType,
            ["OtherID"] = OwnershipMarker(command.ItemCode, command.BomVersionCode),
            ["IsCostRoll"] = false,
            ["ItemSource"] = 0
        };
        if (command.Operation == U9BomWriteOperation.Modify)
        {
            row["BOMMasterChangeDTOList"] = new[]
            {
                Change("Explain", AddOwnershipStamp(command.Explain, command.ItemCode, command.BomVersionCode))
            };
        }
        return JsonSerializer.Serialize(new[] { row });
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildComponents(
        U9BomWriteCommand command,
        U9BomQueryResult current)
    {
        if (command.Operation == U9BomWriteOperation.Create)
            return command.Components.Select(BuildComponent).ToArray();
        var existingSequences = FindMatching(current, command)?.Components
            .Where(component => component.IsDelete != true && component.Sequence is not null)
            .Select(component => component.Sequence!.Value)
            .ToHashSet() ?? [];
        return command.Components
            .Where(component => !existingSequences.Contains(component.Sequence))
            .Select(BuildComponent)
            .ToArray();
    }

    private static Dictionary<string, object?> BuildComponent(U9BomComponentCommand component) => new()
        {
            ["Sequence"] = component.Sequence,
            ["ItemMaster"] = Archive(component.ItemCode),
            ["ComponentType"] = component.ComponentType,
            ["ItemVersionCode"] = component.ItemVersionCode ?? string.Empty,
            ["IsEffective"] = component.IsEffective,
            ["EffectiveDate"] = FormatDate(component.EffectiveDate ?? new DateOnly(2000, 1, 1)),
            ["DisableDate"] = FormatDate(component.DisableDate ?? new DateOnly(9999, 12, 31)),
            ["Remark"] = component.Remark ?? string.Empty,
            ["IssueStyle"] = component.IssueStyle,
            ["SupplyStyle"] = component.SupplyStyle,
            ["IsPhantomPart"] = component.IsPhantomPart,
            ["UsageQtyType"] = 0,
            ["UsageQty"] = component.UsageQty,
            ["IssueUOM"] = Archive(component.IssueUomCode),
            ["ParentQty"] = component.ParentQty,
            ["IsDelete"] = component.IsDelete
        };

    private static Dictionary<string, string> Change(string property, string value) => new()
    {
        ["PropName"] = property,
        ["PropValue"] = value
    };

    private static string Fingerprint(U9BomQueryResult result)
    {
        var normalized = result.Boms
            .OrderBy(bom => bom.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(bom => bom.BomVersionCode, StringComparer.OrdinalIgnoreCase)
            .Select(bom => new
            {
                bom.ItemCode,
                bom.BomVersionCode,
                bom.OtherId,
                bom.Lot,
                bom.ProductUomCode,
                bom.EffectiveDate,
                bom.DisableDate,
                bom.Status,
                bom.ProjectMapNum,
                bom.Explain,
                Components = bom.Components.OrderBy(component => component.Sequence).Select(component => new
                {
                    component.Sequence,
                    component.ItemCode,
                    component.UsageQty,
                    component.IssueUomCode,
                    component.ParentQty,
                    component.IsDelete
                })
            });
        return Hash(JsonSerializer.Serialize(normalized));
    }

    private static string OwnershipMarker(string itemCode, string versionCode) =>
        $"pdm-bom-{Hash($"{itemCode.Trim().ToUpperInvariant()}|{versionCode.Trim().ToUpperInvariant()}")[..24].ToLowerInvariant()}";

    private static string OwnershipStamp(string itemCode, string versionCode) =>
        $"[PDM:{OwnershipMarker(itemCode, versionCode)}]";

    private static string AddOwnershipStamp(string? explain, string itemCode, string versionCode)
    {
        var stamp = OwnershipStamp(itemCode, versionCode);
        if (explain?.Contains(stamp, StringComparison.OrdinalIgnoreCase) == true) return explain.Trim();
        return string.IsNullOrWhiteSpace(explain) ? stamp : $"{stamp} {explain.Trim()}";
    }

    private static bool IsPdmOwned(U9BomReference bom, U9BomWriteCommand command)
    {
        var marker = OwnershipMarker(command.ItemCode, command.BomVersionCode);
        if (string.Equals(bom.OtherId, marker, StringComparison.OrdinalIgnoreCase)) return true;
        return bom.Explain?.Contains(OwnershipStamp(command.ItemCode, command.BomVersionCode), StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ConfirmationFor(U9BomWriteCommand command) =>
        $"{OperationName(command.Operation)} {command.ItemCode}/{command.BomVersionCode}";

    private static string OperationName(U9BomWriteOperation operation) => operation switch
    {
        U9BomWriteOperation.Create => "创建",
        U9BomWriteOperation.Modify => "修改",
        U9BomWriteOperation.Delete => throw new PdmRuleException("PLM禁止删除U9C A1 BOM。"),
        _ => throw new PdmRuleException("不支持的BOM写入操作。")
    };

    private static string PathFor(WriteContext context, U9BomWriteOperation operation) => operation switch
    {
        U9BomWriteOperation.Create => context.BomCreatePath,
        U9BomWriteOperation.Modify => context.BomModifyPath,
        U9BomWriteOperation.Delete => throw new PdmRuleException("PLM禁止删除U9C A1 BOM。"),
        _ => throw new PdmRuleException("不支持的BOM写入操作。")
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Dictionary<string, string> Archive(string code) => new() { ["Code"] = code };
    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Required(string? value, string field) => NormalizeText(value) ?? throw new PdmRuleException($"{field}不能为空。");
    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record WriteContext(
        string BaseUrl,
        string EnterpriseCode,
        string OrganizationCode,
        string UserCode,
        string ClientId,
        string ClientSecret,
        string BomCreatePath,
        string BomQueryPath,
        string BomModifyPath);
}
