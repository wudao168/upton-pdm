using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient u9Client,
    TimeProvider timeProvider)
{
    private const string PendingApplicationCodePrefix = "PDM-PENDING-";
    private const long DefaultMaterialCodeStartSequence = 1_000_000;
    private const long MaximumMaterialCodeStartSequence = 9_999_999;
    private const int U9MaterialReferencePageSize = 1000;
    private const int MaximumU9MaterialReferencePages = 100;
    private const int ExpiredU9TokenResponseCode = 402;
    private static readonly HashSet<string> DuplicateRuleFields = new(StringComparer.OrdinalIgnoreCase) { "Name", "Specification", "Brand" };

    public Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken) =>
        materials.ListMaterialsAsync(query, categoryCode, includeArchived, limit, cancellationToken);

    public async Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAnyPermissionAsync(actor, role, [PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView], cancellationToken);
        return await materials.ListMaterialsAsync(query, categoryCode, includeArchived, limit, cancellationToken);
    }

    public async Task<MaterialPage> ListMaterialPageAsync(
        string? query,
        string? categoryCode,
        string? brand,
        bool includeArchived,
        int page,
        int pageSize,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAnyPermissionAsync(actor, role, [PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView], cancellationToken);
        return await materials.ListMaterialPageAsync(query, categoryCode, brand, includeArchived, page, pageSize, cancellationToken);
    }

    public Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, CancellationToken cancellationToken) =>
        materials.ListCategoriesAsync(includeHidden, cancellationToken);

    public async Task<MaterialNumberingSettings> GetNumberingSettingsAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        return new(await materials.GetMaterialCodeStartSequenceAsync(cancellationToken));
    }

    public async Task<MaterialNumberingSettings> UpdateNumberingSettingsAsync(
        long startSequence,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        if (startSequence is < DefaultMaterialCodeStartSequence or > MaximumMaterialCodeStartSequence)
            throw new PdmRuleException("PLM起始流水号必须是1000000到9999999之间的7位数字。");
        var saved = await materials.SaveMaterialCodeStartSequenceAsync(startSequence, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.numbering.baseline.update", "global",
            $"设置PLM全局7位起始流水号：{saved}；现有更高流水不会回退。", cancellationToken);
        return new(saved);
    }

    public async Task<IReadOnlyList<MaterialDuplicateRule>> GetDuplicateRulesAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var saved = (await materials.GetMaterialDuplicateRulesAsync(cancellationToken))
            .ToDictionary(rule => rule.CategoryCode, StringComparer.OrdinalIgnoreCase);
        return (await materials.ListCategoriesAsync(true, cancellationToken))
            .Where(category => category.AllowCreate && category.IsActive && category.IsVisible && category.PdmKind is not null)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Code, StringComparer.OrdinalIgnoreCase)
            .Select(category => saved.TryGetValue(category.Code, out var rule)
                ? NormalizeDuplicateRule(rule)
                : DefaultDuplicateRule(category))
            .ToArray();
    }

    public async Task<IReadOnlyList<MaterialDuplicateRule>> UpdateDuplicateRulesAsync(
        IReadOnlyList<MaterialDuplicateRule> rules,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var creatableCategories = (await materials.ListCategoriesAsync(true, cancellationToken))
            .Where(category => category.AllowCreate && category.IsActive && category.IsVisible && category.PdmKind is not null)
            .ToDictionary(category => category.Code, StringComparer.OrdinalIgnoreCase);
        var normalized = rules.Select(NormalizeDuplicateRule).ToArray();
        if (normalized.Select(rule => rule.CategoryCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new PdmRuleException("同一料品分类只能配置一条查重规则。");
        if (normalized.Any(rule => !creatableCategories.ContainsKey(rule.CategoryCode)))
            throw new PdmRuleException("查重规则只能配置在允许创建、启用且可见的料品分类上。");
        var saved = await materials.SaveMaterialDuplicateRulesAsync(normalized, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.duplicate-rules.update", "global",
            $"更新料品查重规则：{string.Join("；", saved.Select(rule => $"{rule.CategoryCode}={string.Join('+', rule.Fields)}"))}", cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAnyPermissionAsync(actor, role, [PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView], cancellationToken);
        return await materials.ListCategoriesAsync(includeHidden, cancellationToken);
    }

    public Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(CancellationToken cancellationToken) =>
        materials.ListCategoryRulesAsync(cancellationToken);

    public async Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAnyPermissionAsync(actor, role, [PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView], cancellationToken);
        return await materials.ListCategoryRulesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken) =>
        materials.ListSyncTasksAsync(cancellationToken);

    public async Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialView, cancellationToken);
        return await materials.ListSyncTasksAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialCodeApplication>> ListCodeApplicationsAsync(Guid? projectId, MaterialCodeApplicationStatus? status, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectView, cancellationToken);
        return await materials.ListMaterialCodeApplicationsAsync(projectId, status, cancellationToken);
    }

    public Task<IReadOnlyList<MaterialCodeApplication>> ListCodeApplicationsAsync(
        Guid? projectId,
        MaterialCodeApplicationStatus? status,
        CancellationToken cancellationToken) =>
        materials.ListMaterialCodeApplicationsAsync(projectId, status, cancellationToken);

    public async Task<IReadOnlyList<MaterialCodeResolution>> ResolveStandardBomMaterialsAsync(ResolveMaterialCodesCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(command.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        var items = new List<BomItem>();
        foreach (var itemId in command.BomItemIds.Distinct())
        {
            var item = await repository.FindBomItemAsync(command.ProjectId, itemId, cancellationToken)
                ?? throw new PdmNotFoundException("BOM物料不存在。");
            if (item.Kind != BomKind.Standard) throw new PdmRuleException("只有标准件BOM需要申请料号。");
            items.Add(item);
        }
        var materialsByCode = (await materials.FindMaterialsByCodesAsync(
                items.Where(item => !string.IsNullOrWhiteSpace(item.DrawingNumber)).Select(item => item.DrawingNumber).ToArray(), cancellationToken))
            .ToDictionary(material => material.MaterialCode, StringComparer.OrdinalIgnoreCase);
        var standardCategoryRule = await RequireEnabledCategoryRuleAsync(MaterialKind.Standard, cancellationToken);
        var standardCategory = await RequireCreatableCategoryAsync(standardCategoryRule.U9CategoryCode, MaterialKind.Standard, cancellationToken);
        var duplicateRule = await GetEffectiveDuplicateRuleAsync(standardCategory, cancellationToken);
        var pendingBySignature = new Dictionary<string, MaterialCodeApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, cancellationToken))
        {
            var signature = MaterialRequestSignature(application.BomItemName, application.Specification, application.Brand, duplicateRule.Fields);
            if (signature is not null) pendingBySignature.TryAdd(signature, application);
        }
        var pendingByBomItem = (await materials.ListMaterialCodeApplicationsAsync(command.ProjectId, MaterialCodeApplicationStatus.Pending, cancellationToken))
            .Where(application => application.BomItemId is not null)
            .GroupBy(application => application.BomItemId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(application => application.RequestedAt).First());
        var approvedByBomItem = (await materials.ListMaterialCodeApplicationsAsync(command.ProjectId, MaterialCodeApplicationStatus.Approved, cancellationToken))
            .Where(application => application.BomItemId is not null)
            .GroupBy(application => application.BomItemId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(application => application.RequestedAt).First());
        var results = new List<MaterialCodeResolution>();
        foreach (var item in items)
        {
            if (pendingByBomItem.TryGetValue(item.Id, out var pending))
            {
                results.Add(new(item.Id, MaterialCodeResolutionStatus.ApplicationPending, null, [], pending));
                continue;
            }
            if (approvedByBomItem.TryGetValue(item.Id, out var approvedApplication)
                && approvedApplication.WorkflowState != MaterialCodeWorkflowState.Completed)
            {
                var approvedMaterial = approvedApplication.MaterialId is Guid materialId ? await materials.FindMaterialAsync(materialId, cancellationToken) : null;
                results.Add(new(item.Id, MaterialCodeResolutionStatus.ApplicationApproved, approvedMaterial, approvedMaterial is null ? [] : [approvedMaterial], approvedApplication));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.DrawingNumber))
            {
                materialsByCode.TryGetValue(item.DrawingNumber.Trim(), out var codedMaterial);
                var issues = StandardBomMaterialMasterIssues(item, codedMaterial);
                results.Add(new(item.Id,
                    codedMaterial is null ? MaterialCodeResolutionStatus.CodeNotFound
                        : issues.Count == 0 ? MaterialCodeResolutionStatus.Verified : MaterialCodeResolutionStatus.ValidationFailed,
                    codedMaterial, codedMaterial is null ? [] : [codedMaterial], null) { Issues = issues });
                continue;
            }
            if (approvedApplication is not null)
            {
                var approvedMaterial = approvedApplication.MaterialId is Guid materialId ? await materials.FindMaterialAsync(materialId, cancellationToken) : null;
                results.Add(new(item.Id, MaterialCodeResolutionStatus.ApplicationApproved, approvedMaterial, approvedMaterial is null ? [] : [approvedMaterial], approvedApplication));
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Specification))
            {
                results.Add(new(item.Id, MaterialCodeResolutionStatus.NoMatch, null, [], null));
                continue;
            }
            var candidates = await FindDuplicateMaterialsAsync(standardCategory, item.Name, item.Specification, item.Brand, duplicateRule, true, false, cancellationToken);
            if (candidates.Count == 0 && MaterialRequestSignature(item, duplicateRule.Fields) is string signature && pendingBySignature.TryGetValue(signature, out var matchingPending))
            {
                results.Add(new(item.Id, MaterialCodeResolutionStatus.ApplicationPending, null, [], matchingPending));
                continue;
            }
            if (candidates.Count == 1)
            {
                var candidate = candidates[0];
                var issues = StandardBomMaterialMasterIssues(item, candidate);
                results.Add(new(item.Id,
                    StandardBomMaterialMasterBlockingIssues(item, candidate).Count == 0
                        ? MaterialCodeResolutionStatus.Matched
                        : MaterialCodeResolutionStatus.ValidationFailed,
                    candidate, candidates, null) { Issues = issues });
                continue;
            }
            results.Add(candidates.Count == 0
                ? new(item.Id, MaterialCodeResolutionStatus.NoMatch, null, [], null)
                : new(item.Id, MaterialCodeResolutionStatus.Ambiguous, null, candidates, null));
        }
        return results;
    }

    public async Task<IReadOnlyList<MaterialCodeResolution>> ApplyForMaterialCodesAsync(ApplyMaterialCodesCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var resolutions = await ResolveStandardBomMaterialsAsync(new(command.ProjectId, command.BomItemIds), actor, role, cancellationToken);
        var applicationItems = new Dictionary<Guid, BomItem>();
        foreach (var resolution in resolutions)
        {
            var item = await repository.FindBomItemAsync(command.ProjectId, resolution.BomItemId, cancellationToken)
                ?? throw new PdmNotFoundException("BOM物料不存在。");
            EnsureMaterialCodeRequiredFields(item);
            applicationItems[item.Id] = item;
        }
        var results = new List<MaterialCodeResolution>(resolutions.Count);
        var createdBySignature = new Dictionary<string, MaterialCodeApplication>(StringComparer.OrdinalIgnoreCase);
        var standardCategoryRule = await RequireEnabledCategoryRuleAsync(MaterialKind.Standard, cancellationToken);
        var standardCategory = await RequireCreatableCategoryAsync(standardCategoryRule.U9CategoryCode, MaterialKind.Standard, cancellationToken);
        var duplicateRule = await GetEffectiveDuplicateRuleAsync(standardCategory, cancellationToken);
        foreach (var resolution in resolutions)
        {
            if (resolution.Status != MaterialCodeResolutionStatus.NoMatch)
            {
                results.Add(resolution);
                continue;
            }
            var item = applicationItems[resolution.BomItemId];
            var signature = MaterialRequestSignature(item, duplicateRule.Fields);
            if (signature is not null && createdBySignature.TryGetValue(signature, out var existingApplication))
            {
                results.Add(resolution with { Status = MaterialCodeResolutionStatus.ApplicationPending, Application = existingApplication });
                continue;
            }
            var now = timeProvider.GetUtcNow();
            var application = new MaterialCodeApplication(Guid.NewGuid(), command.ProjectId, resolution.BomItemId,
                MaterialCodeApplicationStatus.Pending, actor, now, null, null, null, null, null, 1)
            {
                BomItemName = item.Name,
                Specification = item.Specification,
                Brand = item.Brand,
                Remark = item.Remark
            };
            application = await materials.CreateMaterialCodeApplicationAsync(application, cancellationToken);
            if (signature is not null) createdBySignature[signature] = application;
            await AuditAsync(actor, "material-code.application.create", application.Id, $"申请标准件料号：BOM {application.BomItemId}", cancellationToken);
            results.Add(resolution with { Status = MaterialCodeResolutionStatus.ApplicationPending, Application = application });
        }
        return results;
    }

    internal static IReadOnlyList<string> StandardBomMaterialMasterIssues(BomItem item, PdmMaterial? material)
    {
        if (material is null) return ["料号不存在于料品主档"];
        var issues = new List<string>();
        if (material.IsArchived) issues.Add("料品主档已归档");
        if (material.ApprovalStatus != MaterialApprovalStatus.Approved) issues.Add("料品主档尚未批准");
        Compare("型号", item.Specification, material.Specification);
        Compare("品牌", item.Brand, material.Brand);
        return issues;

        void Compare(string label, string? drawingValue, string? masterValue)
        {
            if (string.IsNullOrWhiteSpace(drawingValue)) issues.Add($"图档{label}缺失");
            else if (string.IsNullOrWhiteSpace(masterValue)) issues.Add($"料品主档{label}缺失");
            else if (!string.Equals(drawingValue.Trim(), masterValue.Trim(), StringComparison.OrdinalIgnoreCase))
                issues.Add($"{label}与料品主档不一致");
        }
    }

    internal static IReadOnlyList<string> StandardBomMaterialMasterBlockingIssues(BomItem item, PdmMaterial? material) =>
        StandardBomMaterialMasterIssues(item, material)
            .Where(issue => issue is not ("图档型号缺失" or "图档品牌缺失"))
            .ToArray();

    internal static IReadOnlyList<string> StandardBomMaterialMasterDifferenceFields(BomItem item, PdmMaterial? material)
    {
        var fields = new List<string>();
        if (material is null || material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved)
            fields.Add("物料编码");
        if (material is null || !SameRequiredValue(item.Specification, material.Specification))
            fields.Add("型号");
        if (material is null || !SameRequiredValue(item.Brand, material.Brand))
            fields.Add("品牌");
        return fields;

        static bool SameRequiredValue(string? left, string? right) =>
            !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? MaterialRequestSignature(BomItem item, IReadOnlyList<string> fields)
        => MaterialRequestSignature(item.Name, item.Specification, item.Brand, fields);

    private static string? MaterialRequestSignature(string? name, string? specification, string? brand, IReadOnlyList<string> fields)
    {
        var values = fields.Select(field => DuplicateFieldValue(field, name, specification, brand)?.Trim().ToUpperInvariant()).ToArray();
        return values.Length == 0 || values.Any(string.IsNullOrWhiteSpace) ? null : string.Join('|', values);
    }

    private static MaterialDuplicateRule DefaultDuplicateRule(MaterialCategory category) => new(
        category.Code,
        category.PdmKind is MaterialKind.Standard or MaterialKind.Electrical
            ? ["Specification", "Brand"]
            : ["Name", "Specification"]);

    private static MaterialDuplicateRule NormalizeDuplicateRule(MaterialDuplicateRule rule)
    {
        var categoryCode = rule.CategoryCode?.Trim() ?? string.Empty;
        if (categoryCode.Length == 0) throw new PdmRuleException("查重规则必须指定料品分类。");
        var fields = rule.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => CanonicalDuplicateRuleField(field.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fields.Length == 0) throw new PdmRuleException("每个料品分类至少选择一个查重字段。");
        if (fields.Any(field => !DuplicateRuleFields.Contains(field)))
            throw new PdmRuleException("查重字段只支持名称、型号和品牌。");
        return new(categoryCode, fields);
    }

    private async Task<MaterialDuplicateRule> GetEffectiveDuplicateRuleAsync(MaterialCategory category, CancellationToken cancellationToken)
    {
        var saved = (await materials.GetMaterialDuplicateRulesAsync(cancellationToken))
            .FirstOrDefault(rule => string.Equals(rule.CategoryCode, category.Code, StringComparison.OrdinalIgnoreCase));
        return saved is null ? DefaultDuplicateRule(category) : NormalizeDuplicateRule(saved);
    }

    private async Task<IReadOnlyList<PdmMaterial>> FindDuplicateMaterialsAsync(
        MaterialCategory category,
        string? name,
        string? specification,
        string? brand,
        MaterialDuplicateRule rule,
        bool approvedOnly,
        bool requireAllFields,
        CancellationToken cancellationToken)
    {
        if (rule.Fields.Count == 0) return [];
        var values = rule.Fields.ToDictionary(
            field => field,
            field => DuplicateFieldValue(field, name, specification, brand)?.Trim(),
            StringComparer.OrdinalIgnoreCase);
        if (requireAllFields && values.Values.Any(string.IsNullOrWhiteSpace)) return [];
        var comparableFields = rule.Fields.Where(field => !string.IsNullOrWhiteSpace(values[field])).ToArray();
        if (comparableFields.Length == 0) return [];
        var anchor = new[] { "Specification", "Brand", "Name" }
            .Where(field => comparableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            .Select(field => values[field])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (anchor is null) return [];
        var page = await materials.ListMaterialPageAsync(anchor, category.Code, null, false, 1, 200, cancellationToken);
        return page.Items
            .Where(candidate => string.Equals(candidate.CategoryCode, category.Code, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !approvedOnly || candidate.ApprovalStatus == MaterialApprovalStatus.Approved)
            .Where(candidate => comparableFields.All(field => string.Equals(
                DuplicateFieldValue(field, candidate.Name, candidate.Specification, candidate.Brand)?.Trim(),
                values[field],
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static string CanonicalDuplicateRuleField(string field) => field.ToUpperInvariant() switch
    {
        "NAME" => "Name",
        "SPECIFICATION" => "Specification",
        "BRAND" => "Brand",
        _ => field
    };

    private static string DuplicateRuleDescription(MaterialDuplicateRule rule) => string.Join('+', rule.Fields.Select(field => field switch
    {
        "Name" => "名称",
        "Specification" => "型号",
        "Brand" => "品牌",
        _ => field
    }));

    private static string? DuplicateFieldValue(string field, string? name, string? specification, string? brand) => field switch
    {
        "Name" => name,
        "Specification" => specification,
        "Brand" => brand,
        _ => null
    };

    public async Task<(MaterialCodeApplication Application, PdmMaterial? Material, MaterialSyncTask? Task)> DecideMaterialCodeApplicationAsync(Guid applicationId, long expectedRowVersion, bool approved, string? comment, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var approvalSettings = (await repository.GetSystemSettingsAsync(cancellationToken)).MaterialCodeApproval;
        if (role != UserRole.Administrator && !approvalSettings.ApproverRoleCodes.Contains(role.ToString(), StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只有标准化角色可以处理料号申请。");
        var application = await materials.FindMaterialCodeApplicationAsync(applicationId, cancellationToken)
            ?? throw new PdmNotFoundException("料号申请不存在。");
        return await DecideMaterialCodeApplicationCoreAsync(
            application, expectedRowVersion, approved, comment, actor, false, cancellationToken);
    }

    public async Task<IReadOnlyList<(MaterialCodeApplication Application, PdmMaterial? Material, MaterialSyncTask? Task)>>
        AutomaticallyApproveBomHeaderApplicationsAsync(
            IReadOnlyList<(Guid ApplicationId, long ExpectedRowVersion)> requests,
            string actor,
            CancellationToken cancellationToken)
    {
        var pending = new List<(MaterialCodeApplication Application, long ExpectedRowVersion, PdmMaterial Material)>();
        foreach (var request in requests.DistinctBy(item => item.ApplicationId))
        {
            var application = await materials.FindMaterialCodeApplicationAsync(request.ApplicationId, cancellationToken)
                ?? throw new PdmNotFoundException("BOM料号申请不存在。");
            if (application.BomHeaderKind is null)
                throw new PdmRuleException("自动批准仅适用于项目多级BOM表头料号。");
            if (application.Status != MaterialCodeApplicationStatus.Pending)
                throw new PdmRuleException("BOM料号申请状态已经变化，请刷新后重试。");
            if (application.MaterialId is not Guid materialId)
                throw new PdmRuleException("BOM料号申请未关联料品草稿，无法自动批准。");
            var material = await materials.FindMaterialAsync(materialId, cancellationToken)
                ?? throw new PdmNotFoundException("BOM料号申请对应的料品草稿不存在。");
            pending.Add((application, request.ExpectedRowVersion, material));
        }

        var synchronizedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in pending.Select(item => item.Material)
                     .Where(material => material.ApprovalStatus == MaterialApprovalStatus.Draft && !material.U9SyncConfirmed))
        {
            var category = await RequireCreatableCategoryAsync(material.CategoryCode, material.Kind, cancellationToken);
            if (synchronizedScopes.Add(category.CounterScope))
                await SynchronizeCategoryCounterFromU9Async(category, actor, cancellationToken);
        }

        var results = new List<(MaterialCodeApplication Application, PdmMaterial? Material, MaterialSyncTask? Task)>(pending.Count);
        foreach (var item in pending)
        {
            results.Add(await DecideMaterialCodeApplicationCoreAsync(
                item.Application,
                item.ExpectedRowVersion,
                true,
                "系统自动批准并同步U9C",
                actor,
                true,
                cancellationToken));
        }
        return results;
    }

    private async Task<(MaterialCodeApplication Application, PdmMaterial? Material, MaterialSyncTask? Task)> DecideMaterialCodeApplicationCoreAsync(
        MaterialCodeApplication application,
        long expectedRowVersion,
        bool approved,
        string? comment,
        string actor,
        bool automaticBomHeaderApproval,
        CancellationToken cancellationToken)
    {
        var normalizedComment = comment?.Trim();
        if (!approved && string.IsNullOrWhiteSpace(normalizedComment))
            throw new PdmRuleException("不批准料号申请时必须填写原因。");
        if (normalizedComment?.Length > 1000)
            throw new PdmRuleException("审批备注不能超过1000个字符。");
        if (!approved)
        {
            var rejected = await materials.DecideMaterialCodeApplicationAsync(application.Id, expectedRowVersion, MaterialCodeApplicationStatus.Rejected,
                actor, normalizedComment, null, null, timeProvider.GetUtcNow(), cancellationToken);
            await AuditAsync(actor, "material-code.application.reject", rejected.Id, rejected.DecisionComment ?? "退回", cancellationToken);
            return (rejected, null, null);
        }

        if (application.BomHeaderKind is not null)
        {
            if (application.MaterialId is not Guid headerMaterialId)
                throw new PdmRuleException("BOM料号申请未关联料品草稿，无法批准。");
            var headerMaterial = await materials.FindMaterialAsync(headerMaterialId, cancellationToken)
                ?? throw new PdmNotFoundException("BOM料号申请对应的料品草稿不存在。");
            MaterialSyncTask? syncTask = null;
            if (headerMaterial.ApprovalStatus == MaterialApprovalStatus.Draft)
                (headerMaterial, syncTask) = await ApproveCoreAsync(
                    headerMaterial.Id, headerMaterial.RowVersion, actor, cancellationToken,
                    reserveCodeAtApproval: true, automaticApproval: automaticBomHeaderApproval);
            else if (!headerMaterial.U9SyncConfirmed)
                syncTask = (await materials.ListSyncTasksAsync(cancellationToken))
                    .FirstOrDefault(task => task.MaterialId == headerMaterial.Id && task.Status == MaterialSyncStatus.PreviewReady);
            var approvedHeader = await materials.DecideMaterialCodeApplicationAsync(
                application.Id, expectedRowVersion, MaterialCodeApplicationStatus.Approved,
                actor, normalizedComment, headerMaterial.Id, headerMaterial.MaterialCode, timeProvider.GetUtcNow(), cancellationToken);
            await AuditAsync(actor,
                automaticBomHeaderApproval ? "bom.header.application.auto-approve" : "bom.header.application.approve",
                approvedHeader.Id,
                automaticBomHeaderApproval
                    ? $"系统自动批准{application.BomHeaderKind}料号并进入U9C同步：{headerMaterial.MaterialCode}"
                    : $"批准{application.BomHeaderKind}料号申请：{headerMaterial.MaterialCode}",
                cancellationToken);
            return (approvedHeader, headerMaterial, syncTask);
        }

        if (application.BomItemId is not Guid bomItemId)
            throw new PdmRuleException("标准件料号申请未关联BOM物料。");
        var item = await repository.FindBomItemAsync(application.ProjectId, bomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("申请对应的BOM物料不存在。");
        if (item.Kind != BomKind.Standard) throw new PdmRuleException("只有标准件使用料号申请审批。");
        var material = await CreateFromBomCoreAsync(new(application.ProjectId, bomItemId), actor, cancellationToken);
        MaterialSyncTask? materialSyncTask = null;
        if (material.ApprovalStatus == MaterialApprovalStatus.Draft)
            (material, materialSyncTask) = await ApproveCoreAsync(material.Id, material.RowVersion, actor, cancellationToken);
        else if (!material.U9SyncConfirmed)
            materialSyncTask = (await materials.ListSyncTasksAsync(cancellationToken))
                .FirstOrDefault(task => task.MaterialId == material.Id && task.Status == MaterialSyncStatus.PreviewReady);
        var decided = await materials.DecideMaterialCodeApplicationAsync(application.Id, expectedRowVersion, MaterialCodeApplicationStatus.Approved,
            actor, normalizedComment, material.Id, material.MaterialCode, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material-code.application.approve", decided.Id, $"批准标准件料号：{material.MaterialCode}", cancellationToken);
        return (decided, material, materialSyncTask);
    }

    public async Task<IReadOnlyList<PdmMaterial>> EnsureNonStandardMaterialsAsync(Guid projectId, IReadOnlyList<Guid> bomItemIds, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken);
        var result = new List<PdmMaterial>();
        foreach (var itemId in bomItemIds.Distinct())
        {
            var item = await repository.FindBomItemAsync(projectId, itemId, cancellationToken)
                ?? throw new PdmNotFoundException("非标件BOM物料不存在。");
            if (item.Kind != BomKind.NonStandard) throw new PdmRuleException("自动生成料号只适用于非标件BOM。");
            var material = await CreateFromBomCoreAsync(new(projectId, itemId), actor, cancellationToken);
            if (material.ApprovalStatus == MaterialApprovalStatus.Draft)
                (material, _) = await ApproveCoreAsync(material.Id, material.RowVersion, actor, cancellationToken);
            result.Add(material);
        }
        return result;
    }

    public async Task<PdmMaterial> CreateAsync(SaveMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var category = await RequireCreatableCategoryAsync(command.CategoryCode, command.Kind, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var normalized = Normalize(Guid.NewGuid(), command, null, actor, now, category.Code);
        var duplicateRule = await GetEffectiveDuplicateRuleAsync(category, cancellationToken);
        var duplicates = await FindDuplicateMaterialsAsync(category, normalized.Name, normalized.Specification, normalized.Brand, duplicateRule, false, true, cancellationToken);
        if (duplicates.Count > 0)
            throw new PdmConflictException($"按分类查重规则（{DuplicateRuleDescription(duplicateRule)}）已存在料品：{string.Join("、", duplicates.Take(5).Select(item => item.MaterialCode))}，请直接复用现有料品或调整查重规则。");
        var reservation = await ReserveAvailableMaterialCodeAsync(category, normalized.UnitCode, cancellationToken);
        var material = normalized with { MaterialCode = reservation.Code };
        var saved = await materials.CreateMaterialAsync(material, category, cancellationToken);
        var numberingBasis = reservation.CalibratedFromU9
            ? $"按U9C当前最新料号 {reservation.BaselineMaterialCode ?? "无"} 校准后向后生成"
            : $"按PLM分类流水 {reservation.BaselineMaterialCode ?? "无"} 向后生成";
        await AuditAsync(actor, "material.create", saved.Id,
            $"创建物料主档：{saved.MaterialCode} · {saved.Name}；{numberingBasis}。", cancellationToken);
        return saved;
    }

    public async Task<PdmMaterial> CreateApplicationDraftAsync(SaveMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        return await CreateApplicationDraftCoreAsync(command, actor, cancellationToken);
    }

    internal Task<PdmMaterial> CreateApplicationDraftAfterBomApprovalAsync(
        SaveMaterialCommand command,
        string actor,
        CancellationToken cancellationToken) =>
        CreateApplicationDraftCoreAsync(command, actor, cancellationToken);

    private async Task<PdmMaterial> CreateApplicationDraftCoreAsync(
        SaveMaterialCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        var category = await RequireCreatableCategoryAsync(command.CategoryCode, command.Kind, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var materialId = Guid.NewGuid();
        var normalized = Normalize(materialId, command, null, actor, now, category.Code);
        var material = normalized with { MaterialCode = $"{PendingApplicationCodePrefix}{materialId:N}" };
        var saved = await materials.CreateMaterialAsync(material, category, cancellationToken);
        await AuditAsync(actor, "material.application-draft.create", saved.Id,
            $"创建待审批料品草稿：{saved.Name}；审批前未查询或预留U9C正式料号。", cancellationToken);
        return saved;
    }

    public async Task<PdmMaterial> UpdateAsync(Guid materialId, SaveMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.IsArchived) throw new PdmRuleException("已归档料品不能修改。");
        if (existing.ApprovalStatus != MaterialApprovalStatus.Draft) throw new PdmRuleException("已批准物料不可直接修改，请通过后续变更流程处理。");
        if (command.ExpectedRowVersion is null) throw new PdmRuleException("更新物料必须提供数据版本。");
        var category = await RequireCreatableCategoryAsync(command.CategoryCode ?? existing.CategoryCode, command.Kind, cancellationToken);
        var updated = Normalize(materialId, command, existing, actor, timeProvider.GetUtcNow(), category.Code);
        var saved = await materials.UpdateMaterialAsync(updated, command.ExpectedRowVersion.Value, cancellationToken);
        await AuditAsync(actor, "material.update", saved.Id, $"更新物料主档：{saved.MaterialCode} · {saved.Name}", cancellationToken);
        return saved;
    }

    public async Task<PdmMaterial> CreateFromBomAsync(CreateMaterialFromBomCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        return await CreateFromBomCoreAsync(command, actor, cancellationToken);
    }

    private async Task<PdmMaterial> CreateFromBomCoreAsync(CreateMaterialFromBomCommand command, string actor, CancellationToken cancellationToken)
    {
        var item = await repository.FindBomItemAsync(command.ProjectId, command.BomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        if (item.IsPendingClassification || item.IsManualUnmatched || item.IsPendingRemoval || item.IsManuallyExcluded)
            throw new PdmRuleException("该BOM物料仍有待处理状态，不能创建物料主档。");
        EnsureMaterialCodeRequiredFields(item);

        var existing = await materials.FindMaterialBySourceBomItemAsync(item.Id, cancellationToken);
        if (existing is not null)
        {
            await materials.LinkBomItemAsync(item.Id, existing.Id, actor, timeProvider.GetUtcNow(), cancellationToken);
            return existing;
        }

        var kind = item.Kind switch
        {
            BomKind.Electrical => MaterialKind.Electrical,
            BomKind.Standard => MaterialKind.Standard,
            BomKind.NonStandard => MaterialKind.NonStandard,
            _ => throw new PdmRuleException("机械BOM物料必须先明确分类为标准件、非标件或电气件。")
        };
        var rule = await RequireEnabledCategoryRuleAsync(kind, cancellationToken);
        var category = await RequireCreatableCategoryAsync(rule.U9CategoryCode, kind, cancellationToken);
        decimal? weight = decimal.TryParse(item.Weight, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedWeight)
            ? parsedWeight
            : null;
        var now = timeProvider.GetUtcNow();
        var normalized = Normalize(Guid.NewGuid(), new SaveMaterialCommand(
            item.DrawingNumber,
            item.Name,
            kind,
            rule.DefaultSupplyMode,
            U9UnitCatalog.NormalizeBomUnit(item.Unit),
            item.Specification,
            item.Material,
            item.Remark,
            item.Brand,
            item.SurfaceTreatment,
            weight,
            weight is null ? null : "kg",
            CategoryCode: category.Code), null, actor, now, category.Code) with { SourceBomItemId = item.Id };
        var duplicateRule = await GetEffectiveDuplicateRuleAsync(category, cancellationToken);
        var duplicates = await FindDuplicateMaterialsAsync(category, normalized.Name, normalized.Specification, normalized.Brand, duplicateRule, true, true, cancellationToken);
        if (duplicates.Count == 1)
        {
            await materials.LinkBomItemAsync(item.Id, duplicates[0].Id, actor, now, cancellationToken);
            await AuditAsync(actor, "material.reuse-from-bom", duplicates[0].Id,
                $"按分类查重规则复用料品：{item.Id} → {duplicates[0].MaterialCode}", cancellationToken);
            return duplicates[0];
        }
        if (duplicates.Count > 1)
            throw new PdmConflictException($"按分类查重规则（{DuplicateRuleDescription(duplicateRule)}）匹配到多个料品：{string.Join("、", duplicates.Take(5).Select(candidate => candidate.MaterialCode))}，请先在BOM中选择应引用的料品。");
        var reservation = await ReserveAvailableMaterialCodeAsync(category, normalized.UnitCode, cancellationToken);
        var material = normalized with { MaterialCode = reservation.Code };
        var saved = await materials.CreateMaterialAsync(material, category, cancellationToken);
        await materials.LinkBomItemAsync(item.Id, saved.Id, actor, now, cancellationToken);
        await AuditAsync(actor, "material.create-from-bom", saved.Id, $"从BOM创建物料主档：{saved.MaterialCode} · {saved.Name}", cancellationToken);
        return saved;
    }

    private static void EnsureMaterialCodeRequiredFields(BomItem item)
    {
        IReadOnlyList<string> requiredFields = item.Kind switch
        {
            BomKind.Standard or BomKind.Electrical =>
                [BomValidationFieldCatalog.Name, BomValidationFieldCatalog.Specification, BomValidationFieldCatalog.Brand],
            BomKind.NonStandard =>
                [BomValidationFieldCatalog.Name, BomValidationFieldCatalog.Specification],
            _ => []
        };
        if (requiredFields.Count == 0) return;

        var missingFields = BomValidationFieldCatalog.MissingFields(item, requiredFields);
        if (missingFields.Count == 0) return;

        var kindLabel = item.Kind switch
        {
            BomKind.Standard => "标准件",
            BomKind.Electrical => "电气件",
            BomKind.NonStandard => "非标件",
            _ => "BOM物料"
        };
        var itemLabel = string.IsNullOrWhiteSpace(item.Name) ? $"第 {item.Sequence} 行" : $"“{item.Name.Trim()}”";
        throw new PdmRuleException(
            $"{kindLabel}料号申请前必须补全{string.Join("、", requiredFields.Select(BomValidationFieldCatalog.Label))}；" +
            $"{itemLabel}缺少：{string.Join("、", missingFields.Select(BomValidationFieldCatalog.Label))}。");
    }

    private async Task<MaterialCodeReservation> ReserveAvailableMaterialCodeAsync(
        MaterialCategory category,
        string unitCode,
        CancellationToken cancellationToken)
    {
        _ = U9MaterialPayloadFactory.ResolveUnitCode(unitCode);
        var startSequence = await materials.GetMaterialCodeStartSequenceAsync(cancellationToken);
        var minimumCurrentSequence = Math.Max(category.CurrentSequence, startSequence - 1);
        var baselineMaterialCode = minimumCurrentSequence == 0
            ? null
            : $"{category.NumberPrefix}{minimumCurrentSequence.ToString($"D{category.SequenceLength}")}";
        var candidate = await materials.ReserveNextMaterialCodeAsync(category, minimumCurrentSequence, cancellationToken);
        return new MaterialCodeReservation(candidate, baselineMaterialCode, false);
    }

    private async Task<U9LatestSequenceQueryResult> QueryLatestU9SequenceAsync(
        U9MaterialIntegrationConfiguration configuration,
        string token,
        MaterialCategory category,
        CancellationToken cancellationToken)
    {
        var latestSequence = 0L;
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pageIndex = 0; pageIndex < MaximumU9MaterialReferencePages; pageIndex++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                ReferenceCode = "ItemMaster",
                ReferenceEntityFullName = "UFIDA.U9.CBO.SCM.Item.ItemMaster",
                ReferenceDefaultFilter = $"MainItemCategory.Code = '{category.Code}'",
                Transclude = string.Empty,
                TargetOrgCode = configuration.OrganizationCode,
                PageIndex = pageIndex,
                PageSize = U9MaterialReferencePageSize,
                Filter = string.Empty,
                FilterObjectXML = string.Empty
            });
            var page = await u9Client.QueryCustomerReferencesAsync(
                configuration.BaseUrl,
                configuration.CustomerQueryPath,
                token,
                payload,
                cancellationToken);
            if (page.ResponseCode == ExpiredU9TokenResponseCode)
            {
                token = (await AuthenticateU9Async(configuration, cancellationToken)).Token;
                page = await u9Client.QueryCustomerReferencesAsync(
                    configuration.BaseUrl,
                    configuration.CustomerQueryPath,
                    token,
                    payload,
                    cancellationToken);
            }
            if (page.ResponseCode != 0)
                throw new PdmRuleException($"U9C最新料号查询失败（ResCode={page.ResponseCode}）：{page.ResponseMessage ?? "未返回错误说明"}。");

            var countBeforePage = seenCodes.Count;
            foreach (var item in page.Customers)
            {
                var code = item.Code.Trim();
                if (!seenCodes.Add(code)) continue;
                if (TryParseMaterialSequence(code, category, out var sequence)) latestSequence = Math.Max(latestSequence, sequence);
            }

            if (page.RawCount == 0 || page.RawCount < U9MaterialReferencePageSize)
                return new(latestSequence, token);
            if (seenCodes.Count == countBeforePage)
                throw new PdmRuleException("U9C最新料号分页查询未向后推进，无法可靠确定当前最新序号。");
        }

        throw new PdmRuleException($"U9C分类 {category.Code} 的料号超过可安全读取的分页范围，无法可靠确定当前最新序号。");
    }

    private static bool TryParseMaterialSequence(string materialCode, MaterialCategory category, out long sequence)
    {
        sequence = 0;
        if (!materialCode.StartsWith(category.NumberPrefix, StringComparison.OrdinalIgnoreCase)
            || materialCode.Length != category.NumberPrefix.Length + category.SequenceLength) return false;
        var suffix = materialCode[category.NumberPrefix.Length..];
        return suffix.All(char.IsDigit) && long.TryParse(suffix, out sequence);
    }

    private sealed record MaterialCodeReservation(
        string Code,
        string? BaselineMaterialCode,
        bool CalibratedFromU9);

    private sealed record U9LatestSequenceQueryResult(long Sequence, string Token);

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        return await ApproveCoreAsync(materialId, expectedRowVersion, actor, cancellationToken);
    }

    private async Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveCoreAsync(
        Guid materialId,
        long expectedRowVersion,
        string actor,
        CancellationToken cancellationToken,
        bool reserveCodeAtApproval = false,
        bool automaticApproval = false)
    {
        var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("物料主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已归档料品不能批准。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Draft) throw new PdmRuleException("物料主档已经批准。");
        var category = await RequireCreatableCategoryAsync(material.CategoryCode, material.Kind, cancellationToken);
        var materialForApproval = material;
        if (reserveCodeAtApproval)
        {
            var reservation = await ReserveAvailableMaterialCodeAsync(category, material.UnitCode, cancellationToken);
            materialForApproval = material with { MaterialCode = reservation.Code };
        }
        ValidateForApproval(materialForApproval);
        var rule = new MaterialCategoryRule(material.Kind, category.Code, category.Name, category.DefaultSupplyMode, category.AllowCreate, category.UpdatedBy, category.UpdatedAt);
        ValidateSupplyMode(materialForApproval, rule);

        var now = timeProvider.GetUtcNow();
        var taskId = Guid.NewGuid();
        var correlationId = $"pdm-material-{material.Id:N}-v{expectedRowVersion}";
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(material.UnitCode);
        var payloadJson = U9MaterialPayloadFactory.CreatePayload(materialForApproval, rule, configuration.OrganizationCode, correlationId, u9UnitCode);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var task = new MaterialSyncTask(
            taskId,
            material.Id,
            MaterialSyncOperation.Create,
            MaterialSyncStatus.PreviewReady,
            correlationId,
            payloadJson,
            hash,
            0,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
        var audit = new AuditEntry(
            Guid.NewGuid(),
            now,
            actor,
            automaticApproval ? "material.auto-approve" : "material.approve",
            nameof(PdmMaterial),
            material.Id.ToString(),
            automaticApproval
                ? $"多级BOM表头料号自动批准并生成U9C请求预览：{materialForApproval.MaterialCode} · {rule.U9CategoryCode}"
                : $"批准物料并生成U9C请求预览：{materialForApproval.MaterialCode} · {rule.U9CategoryCode}");
        return await materials.ApproveAndEnqueueAsync(materialForApproval, expectedRowVersion, rule.U9CategoryCode, task, audit, cancellationToken);
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask? Task)> ChangeApprovedAsync(
        Guid materialId,
        SaveMaterialCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.IsArchived) throw new PdmRuleException("已归档料品不能变更。");
        if (existing.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("只有已批准料品才能通过变更流程修改。");
        if (command.ExpectedRowVersion is null) throw new PdmRuleException("变更料品必须提供数据版本。");
        var categoryCode = command.CategoryCode ?? existing.CategoryCode ?? existing.U9CategoryCode;
        if (string.IsNullOrWhiteSpace(categoryCode)) categoryCode = (await RequireEnabledCategoryRuleAsync(command.Kind, cancellationToken)).U9CategoryCode;
        var currentCategory = await materials.FindCategoryAsync(categoryCode, cancellationToken)
            ?? throw new PdmRuleException("料品分类不存在或尚未从U9C同步。");
        var updated = Normalize(materialId, command, existing, actor, timeProvider.GetUtcNow(), currentCategory.Code);

        var u9FieldsChanged = HasU9MasterChanges(existing, updated);
        if (!u9FieldsChanged)
        {
            var saved = await materials.UpdatePlmMetadataAsync(updated, command.ExpectedRowVersion.Value, cancellationToken);
            await AuditAsync(actor, "material.plm-metadata.update", materialId,
                $"更新PLM专属字段，不生成U9C任务：{existing.MaterialCode}", cancellationToken);
            return (saved, null);
        }
        if (existing.SyncStatus is MaterialSyncStatus.Pending or MaterialSyncStatus.NeedsReview)
            throw new PdmRuleException(existing.SyncStatus == MaterialSyncStatus.Pending
                ? "U9C同步请求正在执行，结果确认前不能再次修改U9字段。"
                : "上次U9C写入仍待人工复核，处理完成前不能再次修改U9字段。");

        var category = await RequireCreatableCategoryAsync(currentCategory.Code, command.Kind, cancellationToken);
        updated = Normalize(materialId, command, existing, actor, timeProvider.GetUtcNow(), category.Code);
        ValidateForApproval(updated);
        var rule = new MaterialCategoryRule(updated.Kind, category.Code, category.Name, category.DefaultSupplyMode, category.AllowCreate, category.UpdatedBy, category.UpdatedAt);
        ValidateSupplyMode(updated, rule);

        var now = timeProvider.GetUtcNow();
        var operation = existing.U9SyncConfirmed ? MaterialSyncOperation.Update : MaterialSyncOperation.Create;
        var correlationId = existing.U9SyncConfirmed
            ? $"pdm-material-{materialId:N}-update-v{command.ExpectedRowVersion.Value}"
            : $"pdm-material-{materialId:N}-v{command.ExpectedRowVersion.Value}";
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(updated.UnitCode);
        var payloadJson = operation == MaterialSyncOperation.Update
            ? U9MaterialPayloadFactory.ModifyPayload(updated, correlationId, u9UnitCode)
            : U9MaterialPayloadFactory.CreatePayload(updated, rule, configuration.OrganizationCode, correlationId, u9UnitCode);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var task = new MaterialSyncTask(Guid.NewGuid(), materialId, operation, MaterialSyncStatus.PreviewReady,
            correlationId, payloadJson, hash, 0, null, null, null, existing.U9ItemId, existing.U9ItemCode, now, now);
        var audit = new AuditEntry(Guid.NewGuid(), now, actor, "material.change", nameof(PdmMaterial), materialId.ToString(),
            existing.U9SyncConfirmed
                ? $"变更已同步料品并生成U9C修改预览：{existing.MaterialCode}"
                : $"变更未同步料品，废止旧请求并生成新的U9C创建预览：{existing.MaterialCode}");
        return await materials.UpdateAndEnqueueAsync(updated, command.ExpectedRowVersion.Value, task, audit, cancellationToken);
    }

    private static bool HasU9MasterChanges(PdmMaterial existing, PdmMaterial updated) =>
        !SameText(existing.Name, updated.Name)
        || !SameCode(existing.CategoryCode ?? existing.U9CategoryCode, updated.CategoryCode ?? updated.U9CategoryCode)
        || !SameCode(existing.UnitCode, updated.UnitCode)
        || !SameText(existing.Specification, updated.Specification)
        || !SameText(existing.Remark, updated.Remark)
        || !SameText(existing.Brand, updated.Brand)
        || !SameText(existing.Material, updated.Material)
        || !SameText(existing.SurfaceTreatment, updated.SurfaceTreatment)
        || !SameText(existing.PurchaseLink, updated.PurchaseLink)
        || NormalizeWeight(existing.Weight) != NormalizeWeight(updated.Weight);

    private static bool SameText(string? left, string? right) =>
        string.Equals(NormalizeComparisonText(left), NormalizeComparisonText(right), StringComparison.Ordinal);

    private static bool SameCode(string? left, string? right) =>
        string.Equals(NormalizeComparisonText(left), NormalizeComparisonText(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeComparisonText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? NormalizeWeight(decimal? value) => value is null ? null : decimal.Round(value.Value, 6, MidpointRounding.AwayFromZero);

    public async Task<MaterialRemovalResult> RemoveAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.RowVersion != expectedRowVersion)
            throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
        if (existing.SourceSystem != MaterialDataSource.Pdm || existing.MasterOwner != MaterialMasterOwner.Pdm)
            throw new PdmRuleException("只有PLM来源且PLM主控的料品可以删除；U9C主控料品只能停用或由U9C维护。");
        if (await materials.HasMaterialReferencesAsync(materialId, cancellationToken))
            throw new PdmRuleException("料品已被BOM引用或来源于BOM，不能删除；可改为停用。");
        if ((await materials.ListSyncTasksAsync(cancellationToken)).Any(task =>
                task.MaterialId == materialId && task.Status == MaterialSyncStatus.Pending))
            throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能删除。");

        var deletedFromU9 = await EnsureMaterialAbsentFromU9Async(existing, actor, role, cancellationToken);
        var deleted = await materials.DeleteLocalMaterialAsync(materialId, expectedRowVersion, true, cancellationToken);
        await AuditAsync(actor, deletedFromU9 ? "material.delete-synchronized" : "material.delete-local", deleted.Id,
            deletedFromU9
                ? $"同步删除料品：{deleted.MaterialCode}；U9C删除成功并回查确认不存在后删除PLM主档；删除前已确认无PLM/BOM引用。"
                : $"安全删除料品：{deleted.MaterialCode}；删除前已确认无BOM引用且U9C实时查询不存在；本地历史同步标记={existing.U9SyncConfirmed}；清理本地同步任务，未调用U9C写接口。", cancellationToken);
        return new MaterialRemovalResult(deleted, true, false);
    }

    public async Task<MaterialRemovalReadiness> InspectRemovalAsync(
        Guid materialId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        var referenceCount = await materials.CountMaterialReferencesAsync(materialId, cancellationToken);
        var isPdmMaster = material.SourceSystem == MaterialDataSource.Pdm && material.MasterOwner == MaterialMasterOwner.Pdm;
        var localPreconditionsPassed = isPdmMaster && referenceCount == 0;
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var synchronizedDeleteAvailable = localPreconditionsPassed
            && configuration.WriteEnabled
            && !string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext)
            && string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(configuration.ItemDeletePath, U9MaterialContract.DeletePath, StringComparison.OrdinalIgnoreCase);
        var decision = !isPdmMaster
            ? "U9C主控料品不允许从PLM发起物理删除。"
            : referenceCount > 0
                ? $"PLM中已有{referenceCount}处BOM引用，不能删除。"
                : synchronizedDeleteAvailable
                    ? "PLM未发现引用；若U9C存在，将先由U9C删除接口校验引用并删除，回查确认不存在后才删除PLM主档。"
                    : "PLM未发现引用；U9C真实写入或删除接口尚未启用，只能删除U9C中不存在的料品。";
        return new MaterialRemovalReadiness(
            material.Id,
            material.MaterialCode,
            referenceCount,
            isPdmMaster,
            localPreconditionsPassed,
            U9ReferenceCheckAvailable: false,
            SynchronizedDeleteAvailable: synchronizedDeleteAvailable,
            decision);
    }

    public async Task<PdmMaterial> ArchiveAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.IsArchived) throw new PdmRuleException("料品已经归档。");
        var archived = await materials.ArchiveMaterialAsync(materialId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.archive", archived.Id, $"归档料品主档：{archived.MaterialCode}；未调用U9C物理删除。", cancellationToken);
        return archived;
    }

    public async Task<PdmMaterial> ReactivateAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        if (!existing.IsArchived) throw new PdmRuleException("料品当前已启用。");

        var u9ExistenceVerified = existing.SourceSystem == MaterialDataSource.U9C
            || existing.MasterOwner == MaterialMasterOwner.U9C;
        if (u9ExistenceVerified)
            await EnsureU9ControlledMaterialExistsAsync(existing, cancellationToken);

        var reactivated = await materials.ReactivateMaterialAsync(
            materialId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        var verification = u9ExistenceVerified ? "已只读确认U9C同号料品存在；" : string.Empty;
        await AuditAsync(actor, "material.reactivate", reactivated.Id,
            $"启用料品主档：{reactivated.MaterialCode}；{verification}保留原料号和审批记录，未创建新料号、未写入U9C。", cancellationToken);
        return reactivated;
    }

    private async Task EnsureU9ControlledMaterialExistsAsync(PdmMaterial material, CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致，无法确认料品状态。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置，无法确认料品仍然存在，已阻止启用。");

        var authentication = await u9Client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);
        var result = await u9Client.QueryItemsAsync(
            configuration.BaseUrl,
            configuration.ItemQueryPath,
            authentication.Token,
            U9MaterialPayloadFactory.QueryPayload(material.MaterialCode, $"pdm-reactivate-check-{Guid.NewGuid():N}"),
            cancellationToken);
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C料品查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}；已阻止启用。");
        if (!result.Items.Any(item => string.Equals(item.U9ItemCode?.Trim(), material.MaterialCode, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException($"U9C未找到料品 {material.MaterialCode}，不能在PLM启用；请先恢复U9C料品或等待全量同步。");
    }

    private async Task<bool> EnsureMaterialAbsentFromU9Async(
        PdmMaterial material,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致，无法安全删除。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置，无法确认料品不存在，已阻止删除。");

        var authentication = await u9Client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);
        var result = await u9Client.QueryItemsAsync(
            configuration.BaseUrl,
            configuration.ItemQueryPath,
            authentication.Token,
            U9MaterialPayloadFactory.QueryPayload(material.MaterialCode, $"pdm-delete-check-{Guid.NewGuid():N}"),
            cancellationToken);
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C删除前查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}；已阻止删除。");
        var existingItem = result.Items.FirstOrDefault(item =>
            string.Equals(item.U9ItemCode?.Trim(), material.MaterialCode, StringComparison.OrdinalIgnoreCase));
        if (existingItem is null) return false;

        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        if (!configuration.WriteEnabled)
            throw new PdmRuleException($"U9C已存在料品 {material.MaterialCode}，但U9C真实写入尚未启用；未删除U9C和PLM主档。");
        if (!string.Equals(configuration.ItemDeletePath, U9MaterialContract.DeletePath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Delete路径与已冻结的官方合同不一致；未删除U9C和PLM主档。");

        var correlationId = $"pdm-delete-{Guid.NewGuid():N}";
        U9BusinessBatchResult deletion;
        try
        {
            deletion = await u9Client.PostBatchAsync(
                configuration.BaseUrl,
                configuration.ItemDeletePath,
                authentication.Token,
                U9MaterialPayloadFactory.DeletePayload(material, existingItem, correlationId),
                cancellationToken);
        }
        catch (PdmRuleException exception)
        {
            throw new PdmRuleException($"U9C删除请求未确认：{exception.Message}；PLM主档保持不变，请先回查U9C后重试。");
        }

        if (deletion.ResponseCode != 0)
            throw new PdmRuleException($"U9C拒绝删除料品 {material.MaterialCode}（ResCode={deletion.ResponseCode}）：{deletion.ResponseMessage ?? "未返回错误说明"}；PLM主档保持不变。");
        var failedRow = deletion.Rows.FirstOrDefault(row => !row.IsSuccess);
        if (failedRow is not null)
            throw new PdmRuleException($"U9C拒绝删除料品 {material.MaterialCode}：{failedRow.ErrorMessage ?? "可能存在业务引用"}；PLM主档保持不变。");

        var verification = await u9Client.QueryItemsAsync(
            configuration.BaseUrl,
            configuration.ItemQueryPath,
            authentication.Token,
            U9MaterialPayloadFactory.QueryPayload(material.MaterialCode, $"{correlationId}-verify"),
            cancellationToken);
        if (verification.ResponseCode != 0)
            throw new PdmRuleException($"U9C删除后回查失败（ResCode={verification.ResponseCode}）：{verification.ResponseMessage ?? "未返回错误说明"}；PLM主档保持不变，请人工确认U9C结果。");
        if (verification.Items.Any(item => string.Equals(item.U9ItemCode?.Trim(), material.MaterialCode, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException($"U9C返回删除成功，但回查仍存在料品 {material.MaterialCode}；PLM主档保持不变。");
        return true;
    }

    public async Task<PdmMaterial> LinkBomMaterialAsync(LinkBomMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var bomItem = await repository.FindBomItemAsync(command.ProjectId, command.BomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        var material = await materials.FindMaterialAsync(command.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已归档料品不能建立新的BOM引用。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("BOM只能引用已批准的料品主档。");
        EnsureStandardMaterialIdentity(bomItem, material);
        await materials.LinkBomItemAsync(command.BomItemId, material.Id, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.link-bom", material.Id, $"BOM引用料品：{command.BomItemId} → {material.MaterialCode}", cancellationToken);
        return material;
    }

    public async Task<PdmMaterial?> SetBomMaterialLinkByCodeAsync(Guid projectId, Guid bomItemId, string? materialCode, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var bomItem = await repository.FindBomItemAsync(projectId, bomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        if (string.IsNullOrWhiteSpace(materialCode))
        {
            await materials.UnlinkBomItemAsync(bomItemId, cancellationToken);
            await AuditAsync(actor, "material.unlink-bom", bomItemId, "型号或品牌已变更，解除原料品引用。", cancellationToken);
            return null;
        }
        var material = await materials.FindMaterialByCodeAsync(materialCode.Trim(), cancellationToken)
            ?? throw new PdmNotFoundException("自动匹配的料品主档不存在，请重新核对型号。");
        if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("BOM只能关联已批准且未归档的料品主档。");
        EnsureStandardMaterialIdentity(bomItem, material);
        await materials.LinkBomItemAsync(bomItemId, material.Id, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.link-bom", material.Id, $"型号自动关联：{bomItemId} → {material.MaterialCode}", cancellationToken);
        return material;
    }

    public async Task<PdmMaterial> LinkAutomaticallyMatchedBomMaterialAsync(LinkBomMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var bomItem = await repository.FindBomItemAsync(command.ProjectId, command.BomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        var material = await materials.FindMaterialAsync(command.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已归档料品不能建立新的BOM引用。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("BOM只能引用已批准的料品主档。");
        EnsureStandardMaterialIdentity(bomItem, material, allowMissingDrawingIdentity: true);
        await materials.LinkBomItemAsync(command.BomItemId, material.Id, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.auto-link-bom", material.Id, $"唯一型号自动关联：{command.BomItemId} → {material.MaterialCode}", cancellationToken);
        return material;
    }

    private static void EnsureStandardMaterialIdentity(BomItem item, PdmMaterial material, bool allowMissingDrawingIdentity = false)
    {
        if (item.Kind != BomKind.Standard) return;
        var issues = allowMissingDrawingIdentity
            ? StandardBomMaterialMasterBlockingIssues(item, material)
            : StandardBomMaterialMasterIssues(item, material);
        if (issues.Count == 0) return;
        throw new PdmRuleException(
            $"料号 {material.MaterialCode} 与当前标准件的型号、品牌未形成唯一对应，已取消关联：{string.Join('、', issues)}。");
    }

    public async Task<MaterialSyncTask> RetrySyncTaskAsync(Guid taskId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
        var existingTask = await materials.FindSyncTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C同步任务不存在。");
        var material = await materials.FindMaterialAsync(existingTask.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的料品主档不存在。");
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(material.UnitCode);
        string payloadJson;
        if (existingTask.Operation == MaterialSyncOperation.Create)
        {
            var categoryCode = material.CategoryCode ?? material.U9CategoryCode
                ?? throw new PdmRuleException("料品缺少U9C分类，无法重新生成创建请求。");
            var category = await materials.FindCategoryAsync(categoryCode, cancellationToken)
                ?? throw new PdmRuleException($"料品分类 {categoryCode} 不存在，无法重新生成创建请求。");
            var rule = new MaterialCategoryRule(
                material.Kind,
                category.Code,
                category.Name,
                category.DefaultSupplyMode,
                category.AllowCreate,
                category.UpdatedBy,
                category.UpdatedAt);
            payloadJson = U9MaterialPayloadFactory.CreatePayload(
                material,
                rule,
                configuration.OrganizationCode,
                existingTask.CorrelationId,
                u9UnitCode);
        }
        else if (existingTask.Operation == MaterialSyncOperation.Update)
        {
            payloadJson = U9MaterialPayloadFactory.ModifyPayload(material, existingTask.CorrelationId, u9UnitCode);
        }
        else
        {
            throw new PdmRuleException("当前同步任务类型不受支持。");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var task = await materials.RetrySyncTaskAsync(taskId, payloadJson, hash, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.sync.retry", task.MaterialId,
            $"按当前U9C单位编码重新生成同步任务：{task.CorrelationId}；单位：{u9UnitCode}", cancellationToken);
        return task;
    }

    public Task<MaterialSyncTask> ScheduleAutomaticSyncAsync(Guid taskId, CancellationToken cancellationToken) =>
        materials.ScheduleSyncTaskAsync(taskId, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> RecoverConflictingMaterialCodeAsync(
        Guid taskId,
        string actor,
        CancellationToken cancellationToken,
        bool synchronizeWithU9 = false)
    {
        var previousTask = await materials.FindSyncTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C同步任务不存在。");
        if (previousTask.Operation != MaterialSyncOperation.Create
            || previousTask.Status is not (MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview))
            throw new PdmRuleException("只有重复号导致的创建失败任务才能自动换号。");
        _ = synchronizeWithU9;
        return await ReassignUnsynchronizedMaterialCodeAsync(
            previousTask, actor, "U9C重复料号", "material.code.reassign-after-u9-conflict", cancellationToken);
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> EnsureMaterialCodeBaselineAsync(
        Guid taskId,
        string actor,
        CancellationToken cancellationToken)
    {
        var task = await materials.FindSyncTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C同步任务不存在。");
        var material = await materials.FindMaterialAsync(task.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的料品主档不存在。");
        if (task.Operation != MaterialSyncOperation.Create
            || task.Status is not (MaterialSyncStatus.PreviewReady or MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview)
            || material.U9SyncConfirmed)
            return (material, task);

        var category = await RequireCreatableCategoryAsync(material.CategoryCode, material.Kind, cancellationToken);
        var startSequence = await materials.GetMaterialCodeStartSequenceAsync(cancellationToken);
        if (TryParseMaterialSequence(material.MaterialCode, category, out var sequence) && sequence >= startSequence)
            return (material, task);

        return await ReassignUnsynchronizedMaterialCodeAsync(
            task, actor, $"原料号低于PLM起始流水 {startSequence}", "material.code.reassign-to-baseline", cancellationToken);
    }

    private async Task<(PdmMaterial Material, MaterialSyncTask Task)> ReassignUnsynchronizedMaterialCodeAsync(
        MaterialSyncTask previousTask,
        string actor,
        string reason,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var material = await materials.FindMaterialAsync(previousTask.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的料品主档不存在。");
        if (material.U9SyncConfirmed)
            throw new PdmRuleException("料品已经完成U9C同步，不能自动换号。");
        var category = await RequireCreatableCategoryAsync(material.CategoryCode, material.Kind, cancellationToken);
        var configuration = await RequireU9ConfigurationAsync(cancellationToken);
        var startSequence = await materials.GetMaterialCodeStartSequenceAsync(cancellationToken);
        var replacementCode = await materials.ReserveNextMaterialCodeAsync(
            category, Math.Max(category.CurrentSequence, startSequence - 1), cancellationToken);
        var replacementMaterial = material with
        {
            MaterialCode = replacementCode,
            SyncStatus = MaterialSyncStatus.PreviewReady,
            UpdatedBy = actor,
            UpdatedAt = timeProvider.GetUtcNow(),
            RowVersion = material.RowVersion + 1
        };
        var rule = new MaterialCategoryRule(
            material.Kind, category.Code, category.Name, category.DefaultSupplyMode,
            category.AllowCreate, category.UpdatedBy, category.UpdatedAt);
        var correlationId = $"pdm-reissue-{Guid.NewGuid():N}";
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(material.UnitCode);
        var payloadJson = U9MaterialPayloadFactory.CreatePayload(
            replacementMaterial, rule, configuration.OrganizationCode, correlationId, u9UnitCode);
        var replacementTask = new MaterialSyncTask(
            Guid.NewGuid(), material.Id, MaterialSyncOperation.Create, MaterialSyncStatus.PreviewReady,
            correlationId, payloadJson, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))),
            0, timeProvider.GetUtcNow(), null, null, null, null, timeProvider.GetUtcNow(), timeProvider.GetUtcNow());
        var audit = new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, auditAction,
            nameof(PdmMaterial), material.Id.ToString(),
            $"{reason}：{material.MaterialCode}；按PLM本地流水自动重新分配为 {replacementCode}，旧同步任务已废止。");
        return await materials.ReassignMaterialCodeAndEnqueueAsync(
            replacementMaterial, material.RowVersion, material.MaterialCode,
            previousTask, replacementTask, audit, cancellationToken);
    }

    private async Task<MaterialCategory> SynchronizeCategoryCounterFromU9Async(
        MaterialCategory category,
        string actor,
        CancellationToken cancellationToken)
    {
        var scopedCategories = (await materials.ListCategoriesAsync(true, cancellationToken))
            .Where(candidate => candidate.AllowCreate
                && candidate.IsActive
                && string.Equals(candidate.CounterScope, category.CounterScope, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (scopedCategories.Length == 0) scopedCategories = [category];

        var configuration = await RequireU9ConfigurationAsync(cancellationToken);
        var authentication = await AuthenticateU9Async(configuration, cancellationToken);
        var token = authentication.Token;
        var latestU9Sequence = 0L;
        foreach (var candidate in scopedCategories)
        {
            var query = await QueryLatestU9SequenceAsync(configuration, token, candidate, cancellationToken);
            token = query.Token;
            latestU9Sequence = Math.Max(latestU9Sequence, query.Sequence);
        }

        if (latestU9Sequence <= category.CurrentSequence) return category;
        var saved = await materials.AdvanceCategoryCounterAsync(category, latestU9Sequence, cancellationToken);
        await AuditAsync(actor, "material.category.counter-auto-calibrate", saved.Code,
            $"多级BOM表头料号自动分配前读取U9C同类最新流水并向前校准：{saved.NumberPrefix}{saved.CurrentSequence.ToString($"D{saved.SequenceLength}")}。",
            cancellationToken);
        return saved;
    }

    public async Task<int> SynchronizeActiveCategoryCountersFromU9Async(string actor, CancellationToken cancellationToken)
    {
        var categories = (await materials.ListCategoriesAsync(true, cancellationToken))
            .Where(category => category.AllowCreate && category.IsActive)
            .GroupBy(category => category.CounterScope, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (categories.Length == 0) return 0;
        var configuration = await RequireU9ConfigurationAsync(cancellationToken);
        var authentication = await AuthenticateU9Async(configuration, cancellationToken);
        var token = authentication.Token;
        var advanced = 0;
        foreach (var category in categories)
        {
            var query = await QueryLatestU9SequenceAsync(configuration, token, category, cancellationToken);
            token = query.Token;
            var latestU9Sequence = query.Sequence;
            if (latestU9Sequence <= category.CurrentSequence) continue;
            var saved = await materials.AdvanceCategoryCounterAsync(category, latestU9Sequence, cancellationToken);
            advanced++;
            await AuditAsync(actor, "material.category.counter-auto-calibrate", saved.Code,
                $"定时读取U9C分类最新料号后向前校准流水：{saved.NumberPrefix}{saved.CurrentSequence.ToString($"D{saved.SequenceLength}")}。",
                cancellationToken);
        }
        return advanced;
    }

    private async Task<U9MaterialIntegrationConfiguration> RequireU9ConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置。");
        return configuration;
    }

    private Task<U9AuthenticationResult> AuthenticateU9Async(
        U9MaterialIntegrationConfiguration configuration,
        CancellationToken cancellationToken) =>
        u9Client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);

    public async Task<MaterialCategoryRule> SaveCategoryRuleAsync(SaveMaterialCategoryRuleCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var code = command.U9CategoryCode.Trim();
        if (code.Length is < 2 or > 20 || code.Any(character => !char.IsLetterOrDigit(character)))
            throw new PdmRuleException("U9C料品分类编码只能包含2到20位字母或数字。");
        var name = Required(command.U9CategoryName, "U9C料品分类名称");
        var rule = new MaterialCategoryRule(command.PdmKind, code, name, command.DefaultSupplyMode, command.IsEnabled, actor, timeProvider.GetUtcNow());
        var saved = await materials.SaveCategoryRuleAsync(rule, cancellationToken);
        await AuditAsync(actor, "material.category-rule.update", saved.PdmKind.ToString(), $"更新U9C料品分类规则：{saved.PdmKind} → {saved.U9CategoryCode}", cancellationToken);
        return saved;
    }

    public async Task<MaterialCategory> SaveCategoryAsync(SaveMaterialCategoryCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var code = Required(command.Code, "分类编码");
        if (code.Length > 20 || code.Any(character => !char.IsLetterOrDigit(character)))
            throw new PdmRuleException("分类编码只能包含1到20位字母或数字。");
        var parentCode = Clean(command.ParentCode);
        if (string.Equals(code, parentCode, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("分类不能将自己设置为上级分类。");
        if (parentCode is not null && await materials.FindCategoryAsync(parentCode, cancellationToken) is null)
            throw new PdmRuleException("上级料品分类不存在。");
        if (command.SequenceLength is < 1 or > 9) throw new PdmRuleException("流水位数必须在1到9之间。");
        if (command.AllowCreate && command.SequenceLength != 7)
            throw new PdmRuleException("开放创建的料品分类必须使用统一的7位流水号。");
        if (command.AllowCreate && command.PdmKind is null) throw new PdmRuleException("开放创建前必须选择PLM业务分类。");
        if (command.AllowCreate && (!command.IsVisible || !command.IsActive)) throw new PdmRuleException("只有可见且有效的分类才能开放创建。");
        var prefix = Clean(command.NumberPrefix) ?? code;
        if (prefix.Length > 40) throw new PdmRuleException("编号前缀不能超过40个字符。");
        var counterScope = Clean(command.CounterScope) ?? code;
        if (counterScope.Length > 40) throw new PdmRuleException("流水范围不能超过40个字符。");
        var now = timeProvider.GetUtcNow();
        var category = new MaterialCategory(
            code,
            Required(command.Name, "分类名称"),
            parentCode,
            Clean(command.U9CategoryId),
            command.PdmKind,
            command.DefaultSupplyMode,
            command.AllowCreate,
            command.IsVisible,
            command.IsActive,
            prefix,
            command.SequenceLength,
            counterScope,
            command.SortOrder,
            actor,
            now,
            command.ExpectedRowVersion ?? 1);
        var saved = await materials.SaveCategoryAsync(category, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "material.category.save", saved.Code,
            $"维护U9C对应分类：{saved.Code} · {saved.Name}；创建：{(saved.AllowCreate ? "开放" : "屏蔽")}", cancellationToken);
        return saved;
    }

    public async Task<MaterialCategory> CalibrateCategoryCounterAsync(
        string categoryCode,
        CalibrateMaterialCategoryCounterCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var category = await materials.FindCategoryAsync(Required(categoryCode, "分类编码"), cancellationToken)
            ?? throw new PdmNotFoundException("料品分类不存在。");
        var lastMaterialCode = Required(command.LastMaterialCode, "U9C末位料号");
        if (!lastMaterialCode.StartsWith(category.NumberPrefix, StringComparison.OrdinalIgnoreCase)
            || lastMaterialCode.Length != category.NumberPrefix.Length + category.SequenceLength)
            throw new PdmRuleException($"U9C末位料号必须符合 {category.NumberPrefix} + {category.SequenceLength}位流水。");
        var suffix = lastMaterialCode[category.NumberPrefix.Length..];
        if (suffix.Any(character => !char.IsDigit(character)) || !long.TryParse(suffix, out var value))
            throw new PdmRuleException("U9C末位料号的流水部分必须全部为数字。");
        if (value < category.CurrentSequence)
            throw new PdmRuleException($"流水不能回退；当前值为 {category.CurrentSequence.ToString($"D{category.SequenceLength}")}。");

        var saved = await materials.AdvanceCategoryCounterAsync(category, value, cancellationToken);
        await AuditAsync(actor, "material.category.counter-calibrate", saved.Code,
            $"按U9C末位料号校准分类流水：{saved.Code}；末位：{lastMaterialCode}；下一个：{saved.NumberPrefix}{(saved.CurrentSequence + 1).ToString($"D{saved.SequenceLength}")}", cancellationToken);
        return saved;
    }

    public async Task<U9MaterialIntegrationSettings> GetIntegrationSettingsAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        return ToSettings(await materials.GetIntegrationConfigurationAsync(cancellationToken));
    }

    public async Task<U9MaterialIntegrationSettings> UpdateIntegrationSettingsAsync(UpdateU9MaterialIntegrationCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var baseUrl = command.BaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PdmRuleException("U9C地址必须是有效的HTTP或HTTPS地址。");
        var existing = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var ciphertext = string.IsNullOrWhiteSpace(command.ClientSecret)
            ? existing.ClientSecretCiphertext
            : secretProtector.Protect(command.ClientSecret.Trim());
        var createPath = NormalizeContractPath(command.ItemCreatePath, U9MaterialContract.CreatePath, "料品创建");
        var queryPath = NormalizeContractPath(command.ItemQueryPath, U9MaterialContract.QueryPath, "料品查询");
        var modifyPath = NormalizeContractPath(command.ItemModifyPath ?? U9MaterialContract.ModifyPath, U9MaterialContract.ModifyPath, "料品修改");
        var deletePath = NormalizeContractPath(command.ItemDeletePath ?? U9MaterialContract.DeletePath, U9MaterialContract.DeletePath, "料品删除");
        var customerQueryPath = NormalizeContractPath(command.CustomerQueryPath ?? U9MaterialContract.CustomerReferencePath, U9MaterialContract.CustomerReferencePath, "客户查询");
        var bomCreatePath = NormalizeContractPath(command.BomCreatePath ?? U9BomContract.CreatePath, U9BomContract.CreatePath, "BOM创建");
        var bomQueryPath = NormalizeContractPath(command.BomQueryPath ?? U9BomContract.QueryPath, U9BomContract.QueryPath, "BOM查询");
        var bomModifyPath = NormalizeContractPath(command.BomModifyPath ?? U9BomContract.ModifyPath, U9BomContract.ModifyPath, "BOM修改");
        var bomDeletePath = NormalizeContractPath(command.BomDeletePath ?? U9BomContract.DeletePath, U9BomContract.DeletePath, "BOM删除");
        var bomBatchUnapprovePath = NormalizeContractPath(command.BomBatchUnapprovePath ?? U9BomContract.BatchUnApprovePath, U9BomContract.BatchUnApprovePath, "BOM弃审");
        var bomBipQueryPagePath = NormalizeContractPath(command.BomBipQueryPagePath ?? U9BomContract.BipQueryPagePath, U9BomContract.BipQueryPagePath, "BOM记录定位");
        if (command.WriteEnabled && string.IsNullOrWhiteSpace(ciphertext))
            throw new PdmRuleException("启用U9C真实写入前必须保存应用密钥。");
        var configuration = new U9MaterialIntegrationConfiguration(
            baseUrl,
            Required(command.EnterpriseCode, "企业编码"),
            Required(command.OrganizationCode, "组织编码"),
            Required(command.UserCode, "用户编码"),
            Required(command.ClientId, "应用ID"),
            ciphertext,
            createPath,
            queryPath,
            command.WriteEnabled,
            actor,
            timeProvider.GetUtcNow(),
            modifyPath,
            deletePath,
            new Dictionary<string, string>(),
            customerQueryPath,
            bomCreatePath,
            bomQueryPath,
            bomModifyPath,
            bomDeletePath,
            bomBatchUnapprovePath,
            bomBipQueryPagePath);
        var saved = await materials.SaveIntegrationConfigurationAsync(configuration, cancellationToken);
        await AuditAsync(actor, "u9.material-integration.update", "u9-material", $"更新U9C料品集成配置：{saved.BaseUrl}；应用：{saved.ClientId}；真实写入：{(saved.WriteEnabled ? "开启" : "关闭")}", cancellationToken);
        return ToSettings(saved);
    }

    private static PdmMaterial Normalize(Guid id, SaveMaterialCommand command, PdmMaterial? existing, string actor, DateTimeOffset now, string categoryCode)
    {
        var code = existing?.MaterialCode ?? string.Empty;
        var name = Required(command.Name, "物料名称");
        var unit = U9UnitCatalog.Normalize(command.UnitCode);
        var selectionAdvice = Clean(command.SelectionAdvice);
        if (name.Length > 300) throw new PdmRuleException("物料名称不能超过300个字符。");
        if (command.Weight is <= 0) throw new PdmRuleException("重量必须大于0。");
        if (selectionAdvice?.Length > 1000) throw new PdmRuleException("选型建议不能超过1000个字符。");
        if (command.ReferencePrice is < 0) throw new PdmRuleException("参考价格不能小于0。");
        return new PdmMaterial(
            id,
            code,
            name,
            command.Kind,
            command.SupplyMode,
            unit,
            Clean(command.Specification),
            Clean(command.Material),
            Clean(command.Remark),
            Clean(command.Brand),
            Clean(command.SurfaceTreatment),
            command.Weight,
            command.Weight is null ? null : Clean(command.WeightUnit) ?? "kg",
            existing?.SourceBomItemId,
            existing?.ApprovalStatus ?? MaterialApprovalStatus.Draft,
            existing?.ApprovedBy,
            existing?.ApprovedAt,
            existing?.U9CategoryCode,
            existing?.U9ItemId,
            existing?.U9ItemCode,
            existing?.SyncStatus ?? MaterialSyncStatus.NotQueued,
            existing?.CreatedBy ?? actor,
            existing?.CreatedAt ?? now,
            actor,
            now,
            existing?.RowVersion ?? 1,
            categoryCode,
            existing?.IsArchived ?? false,
            existing?.ArchivedBy,
            existing?.ArchivedAt,
            existing?.U9SyncConfirmed ?? false,
            PurchaseLink: NormalizeHttpLink(command.PurchaseLink, "料品采购链接"),
            SelectionAdvice: selectionAdvice,
            ReferencePrice: command.ReferencePrice,
            Model3DLink: NormalizeHttpLink(command.Model3DLink, "3D链接"),
            DocumentLink: NormalizeHttpLink(command.DocumentLink, "资料链接"),
            IsRecommended: command.IsRecommended,
            CoverImageAttachmentId: existing?.CoverImageAttachmentId);
    }

    private static void ValidateForApproval(PdmMaterial material)
    {
        _ = Required(material.MaterialCode, "PLM物料编码");
        _ = Required(material.Name, "物料名称");
        _ = Required(material.UnitCode, "计量单位");
        if (material.Kind == MaterialKind.Standard && string.IsNullOrWhiteSpace(material.Specification))
            throw new PdmRuleException("机械外购件批准前必须填写规格。");
        if (material.Kind == MaterialKind.NonStandard && string.IsNullOrWhiteSpace(material.Material))
            throw new PdmRuleException("非标机加件批准前必须填写材质。");
    }

    private static void ValidateSupplyMode(PdmMaterial material, MaterialCategoryRule rule)
    {
        if ((material.Kind is MaterialKind.Electrical or MaterialKind.Standard) && material.SupplyMode != MaterialSupplyMode.Purchase)
            throw new PdmRuleException($"{rule.U9CategoryName}的默认供给方式必须是采购。");
        if (material.Kind == MaterialKind.NonStandard && material.SupplyMode == MaterialSupplyMode.Purchase)
            throw new PdmRuleException("非标机加件的供给方式必须是自制或委外。");
    }

    private async Task<MaterialCategoryRule> RequireEnabledCategoryRuleAsync(MaterialKind kind, CancellationToken cancellationToken)
    {
        var rule = await materials.FindCategoryRuleAsync(kind, cancellationToken)
            ?? throw new PdmRuleException("物料分类尚未配置U9C映射规则。");
        if (!rule.IsEnabled) throw new PdmRuleException("物料分类的U9C映射规则已停用。");
        return rule;
    }

    private async Task<MaterialCategory> RequireCreatableCategoryAsync(string? categoryCode, MaterialKind kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            var legacy = await RequireEnabledCategoryRuleAsync(kind, cancellationToken);
            categoryCode = legacy.U9CategoryCode;
        }
        var category = await materials.FindCategoryAsync(categoryCode.Trim(), cancellationToken)
            ?? throw new PdmRuleException("料品分类不存在或尚未从U9C同步。");
        if (!category.IsActive) throw new PdmRuleException("料品分类已停用。");
        if (!category.IsVisible) throw new PdmRuleException("料品分类在PLM中已屏蔽。");
        if (!category.AllowCreate) throw new PdmRuleException("料品分类未开放创建。");
        if (category.PdmKind is not null && category.PdmKind != kind)
            throw new PdmRuleException($"料品分类 {category.Code} 仅允许创建{category.PdmKind}类型料品。");
        return category;
    }

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new PdmRuleException($"{field}不能为空。");
        return normalized;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeHttpLink(string? value, string field)
    {
        var normalized = Clean(value);
        if (normalized is null) return null;
        if (normalized.Length > 2048) throw new PdmRuleException($"{field}不能超过2048个字符。");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PdmRuleException($"{field}必须是有效的HTTP或HTTPS地址。");
        return normalized;
    }

    private static string NormalizeContractPath(string value, string expected, string field)
    {
        var normalized = Required(value, $"{field}接口路径");
        if (!string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException($"{field}接口路径必须是官方合同 {expected}。");
        return expected;
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken)) throw new UnauthorizedAccessException("当前角色无权执行此操作。");
    }

    private async Task RequireAnyPermissionAsync(string actor, UserRole role, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken)
    {
        foreach (var permissionCode in permissionCodes)
            if (await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken)) return;
        throw new UnauthorizedAccessException("当前角色无权查看料品资料。");
    }

    private Task AuditAsync(string actor, string action, object entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, nameof(PdmMaterial), entityId.ToString() ?? string.Empty, detail), cancellationToken);

    private static U9MaterialIntegrationSettings ToSettings(U9MaterialIntegrationConfiguration configuration) => new(
        configuration.BaseUrl,
        configuration.EnterpriseCode,
        configuration.OrganizationCode,
        configuration.UserCode,
        configuration.ClientId,
        !string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext),
        configuration.ItemCreatePath,
        configuration.ItemQueryPath,
        configuration.WriteEnabled,
        configuration.UpdatedBy,
        configuration.UpdatedAt,
        configuration.ItemModifyPath,
        configuration.ItemDeletePath,
        configuration.UnitCodeMappings ?? new Dictionary<string, string>(),
        configuration.CustomerQueryPath,
        configuration.BomCreatePath,
        configuration.BomQueryPath,
        configuration.BomModifyPath,
        configuration.BomDeletePath,
        configuration.BomBatchUnapprovePath,
        configuration.BomBipQueryPagePath);
}
