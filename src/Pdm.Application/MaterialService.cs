using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient u9Client,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken) =>
        materials.ListMaterialsAsync(query, categoryCode, includeArchived, limit, cancellationToken);

    public Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, CancellationToken cancellationToken) =>
        materials.ListCategoriesAsync(includeHidden, cancellationToken);

    public Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(CancellationToken cancellationToken) =>
        materials.ListCategoryRulesAsync(cancellationToken);

    public Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken) =>
        materials.ListSyncTasksAsync(cancellationToken);

    public async Task<PdmMaterial> CreateAsync(SaveMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var category = await RequireCreatableCategoryAsync(command.CategoryCode, command.Kind, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var normalized = Normalize(Guid.NewGuid(), command, null, actor, now, category.Code);
        var reservation = await ReserveAvailableMaterialCodeAsync(category, normalized.UnitCode, normalized.Specification, cancellationToken);
        var material = normalized with { MaterialCode = reservation.Code };
        var saved = await materials.CreateMaterialAsync(material, category, cancellationToken);
        await AuditAsync(actor, "material.create", saved.Id,
            $"创建物料主档：{saved.MaterialCode} · {saved.Name}；创建前只读校验U9C编码和规格，跳过同规格占用{reservation.SameSpecificationCount}个、规格冲突占用{reservation.DifferentSpecificationCount}个。", cancellationToken);
        return saved;
    }

    public async Task<PdmMaterial> UpdateAsync(Guid materialId, SaveMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
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
        var item = await repository.FindBomItemAsync(command.ProjectId, command.BomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        if (item.IsPendingClassification || item.IsManualUnmatched || item.IsPendingRemoval || item.IsManuallyExcluded)
            throw new PdmRuleException("该BOM物料仍有待处理状态，不能创建物料主档。");

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
        var reservation = await ReserveAvailableMaterialCodeAsync(category, normalized.UnitCode, normalized.Specification, cancellationToken);
        var material = normalized with { MaterialCode = reservation.Code };
        var saved = await materials.CreateMaterialAsync(material, category, cancellationToken);
        await materials.LinkBomItemAsync(item.Id, saved.Id, actor, now, cancellationToken);
        await AuditAsync(actor, "material.create-from-bom", saved.Id, $"从BOM创建物料主档：{saved.MaterialCode} · {saved.Name}", cancellationToken);
        return saved;
    }

    private async Task<MaterialCodeReservation> ReserveAvailableMaterialCodeAsync(
        MaterialCategory category,
        string unitCode,
        string? specification,
        CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致，无法在创建前校验编码。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置，无法在创建前校验最新可用编码。");

        var authentication = await u9Client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);

        await RequireAvailableU9UnitCodeAsync(
            configuration,
            authentication.Token,
            unitCode,
            $"pdm-create-uom-check-{Guid.NewGuid():N}",
            cancellationToken);

        var sameSpecificationCount = 0;
        var differentSpecificationCount = 0;
        while (true)
        {
            var candidate = await materials.ReserveNextMaterialCodeAsync(category, cancellationToken);
            var result = await u9Client.QueryItemsAsync(
                configuration.BaseUrl,
                configuration.ItemQueryPath,
                authentication.Token,
                U9MaterialPayloadFactory.QueryPayload(candidate, $"pdm-create-check-{Guid.NewGuid():N}"),
                cancellationToken);
            if (result.ResponseCode != 0)
                throw new PdmRuleException($"U9C创建前编码校验失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");

            var codeMatches = result.Items.Where(item =>
                string.Equals(item.U9ItemCode?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (codeMatches.Length == 0)
                return new MaterialCodeReservation(candidate, sameSpecificationCount, differentSpecificationCount);

            if (codeMatches.Any(item => SameSpecification(item.U9Specification, specification))) sameSpecificationCount++;
            else differentSpecificationCount++;
        }
    }

    private static bool SameSpecification(string? left, string? right) =>
        string.Equals(NormalizeSpecification(left), NormalizeSpecification(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSpecification(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<string> RequireAvailableU9UnitCodeAsync(
        U9MaterialIntegrationConfiguration configuration,
        string token,
        string pdmUnitCode,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(pdmUnitCode);
        var result = await u9Client.QueryUomsAsync(
            configuration.BaseUrl,
            token,
            U9MaterialPayloadFactory.UomQueryPayload(u9UnitCode, correlationId),
            cancellationToken);
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C创建前计量单位校验失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");
        if (!result.Units.Any(unit => string.Equals(unit.U9UomCode?.Trim(), u9UnitCode, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException($"PDM计量单位编码 {u9UnitCode} 在U9C中不存在；未创建料品，也未占用流水号。");
        return u9UnitCode;
    }

    private sealed record MaterialCodeReservation(
        string Code,
        int SameSpecificationCount,
        int DifferentSpecificationCount);

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("物料主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已归档料品不能批准。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Draft) throw new PdmRuleException("物料主档已经批准。");
        ValidateForApproval(material);
        var category = await RequireCreatableCategoryAsync(material.CategoryCode, material.Kind, cancellationToken);
        var rule = new MaterialCategoryRule(material.Kind, category.Code, category.Name, category.DefaultSupplyMode, category.AllowCreate, category.UpdatedBy, category.UpdatedAt);
        ValidateSupplyMode(material, rule);

        var now = timeProvider.GetUtcNow();
        var taskId = Guid.NewGuid();
        var correlationId = $"pdm-material-{material.Id:N}-v{expectedRowVersion}";
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(material.UnitCode);
        var payloadJson = U9MaterialPayloadFactory.CreatePayload(material, rule, configuration.OrganizationCode, correlationId, u9UnitCode);
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
        var audit = new AuditEntry(Guid.NewGuid(), now, actor, "material.approve", nameof(PdmMaterial), material.Id.ToString(), $"批准物料并生成U9C请求预览：{material.MaterialCode} · {rule.U9CategoryCode}");
        return await materials.ApproveAndEnqueueAsync(material.Id, expectedRowVersion, rule.U9CategoryCode, task, audit, cancellationToken);
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ChangeApprovedAsync(
        Guid materialId,
        SaveMaterialCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.IsArchived) throw new PdmRuleException("已归档料品不能变更。");
        if (existing.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("只有已批准料品才能通过变更流程修改。");
        if (existing.SyncStatus == MaterialSyncStatus.Pending)
            throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能修改料品。");
        if (command.ExpectedRowVersion is null) throw new PdmRuleException("变更料品必须提供数据版本。");
        var category = await RequireCreatableCategoryAsync(command.CategoryCode ?? existing.CategoryCode, command.Kind, cancellationToken);
        var updated = Normalize(materialId, command, existing, actor, timeProvider.GetUtcNow(), category.Code);
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

    public async Task<MaterialRemovalResult> RemoveAsync(Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.RowVersion != expectedRowVersion)
            throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
        if (existing.SourceSystem != MaterialDataSource.Pdm || existing.MasterOwner != MaterialMasterOwner.Pdm)
            throw new PdmRuleException("只有PDM来源且PDM主控的料品可以删除；U9C主控料品只能停用或由U9C维护。");
        if (await materials.HasMaterialReferencesAsync(materialId, cancellationToken))
            throw new PdmRuleException("料品已被BOM引用或来源于BOM，不能删除；可改为停用。");
        if ((await materials.ListSyncTasksAsync(cancellationToken)).Any(task =>
                task.MaterialId == materialId && task.Status == MaterialSyncStatus.Pending))
            throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能删除。");

        var deletedFromU9 = await EnsureMaterialAbsentFromU9Async(existing, actor, role, cancellationToken);
        var deleted = await materials.DeleteLocalMaterialAsync(materialId, expectedRowVersion, true, cancellationToken);
        await AuditAsync(actor, deletedFromU9 ? "material.delete-synchronized" : "material.delete-local", deleted.Id,
            deletedFromU9
                ? $"同步删除料品：{deleted.MaterialCode}；U9C删除成功并回查确认不存在后删除PDM主档；删除前已确认无PDM/BOM引用。"
                : $"安全删除料品：{deleted.MaterialCode}；删除前已确认无BOM引用且U9C实时查询不存在；本地历史同步标记={existing.U9SyncConfirmed}；清理本地同步任务，未调用U9C写接口。", cancellationToken);
        return new MaterialRemovalResult(deleted, true, false);
    }

    public async Task<MaterialRemovalReadiness> InspectRemovalAsync(
        Guid materialId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
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
            ? "U9C主控料品不允许从PDM发起物理删除。"
            : referenceCount > 0
                ? $"PDM中已有{referenceCount}处BOM引用，不能删除。"
                : synchronizedDeleteAvailable
                    ? "PDM未发现引用；若U9C存在，将先由U9C删除接口校验引用并删除，回查确认不存在后才删除PDM主档。"
                    : "PDM未发现引用；U9C真实写入或删除接口尚未启用，只能删除U9C中不存在的料品。";
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
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        var existing = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("物料主档不存在。");
        if (existing.IsArchived) throw new PdmRuleException("料品已经归档。");
        var archived = await materials.ArchiveMaterialAsync(materialId, expectedRowVersion, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.archive", archived.Id, $"归档料品主档：{archived.MaterialCode}；未调用U9C物理删除。", cancellationToken);
        return archived;
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

        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        if (!configuration.WriteEnabled)
            throw new PdmRuleException($"U9C已存在料品 {material.MaterialCode}，但U9C真实写入尚未启用；未删除U9C和PDM主档。");
        if (!string.Equals(configuration.ItemDeletePath, U9MaterialContract.DeletePath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Delete路径与已冻结的官方合同不一致；未删除U9C和PDM主档。");

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
            throw new PdmRuleException($"U9C删除请求未确认：{exception.Message}；PDM主档保持不变，请先回查U9C后重试。");
        }

        if (deletion.ResponseCode != 0)
            throw new PdmRuleException($"U9C拒绝删除料品 {material.MaterialCode}（ResCode={deletion.ResponseCode}）：{deletion.ResponseMessage ?? "未返回错误说明"}；PDM主档保持不变。");
        var failedRow = deletion.Rows.FirstOrDefault(row => !row.IsSuccess);
        if (failedRow is not null)
            throw new PdmRuleException($"U9C拒绝删除料品 {material.MaterialCode}：{failedRow.ErrorMessage ?? "可能存在业务引用"}；PDM主档保持不变。");

        var verification = await u9Client.QueryItemsAsync(
            configuration.BaseUrl,
            configuration.ItemQueryPath,
            authentication.Token,
            U9MaterialPayloadFactory.QueryPayload(material.MaterialCode, $"{correlationId}-verify"),
            cancellationToken);
        if (verification.ResponseCode != 0)
            throw new PdmRuleException($"U9C删除后回查失败（ResCode={verification.ResponseCode}）：{verification.ResponseMessage ?? "未返回错误说明"}；PDM主档保持不变，请人工确认U9C结果。");
        if (verification.Items.Any(item => string.Equals(item.U9ItemCode?.Trim(), material.MaterialCode, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException($"U9C返回删除成功，但回查仍存在料品 {material.MaterialCode}；PDM主档保持不变。");
        return true;
    }

    public async Task<PdmMaterial> LinkBomMaterialAsync(LinkBomMaterialCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        _ = await repository.FindBomItemAsync(command.ProjectId, command.BomItemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        var material = await materials.FindMaterialAsync(command.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已归档料品不能建立新的BOM引用。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("BOM只能引用已批准的料品主档。");
        await materials.LinkBomItemAsync(command.BomItemId, material.Id, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "material.link-bom", material.Id, $"BOM引用料品：{command.BomItemId} → {material.MaterialCode}", cancellationToken);
        return material;
    }

    public async Task<MaterialSyncTask> RetrySyncTaskAsync(Guid taskId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
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
        if (command.AllowCreate && command.PdmKind is null) throw new PdmRuleException("开放创建前必须选择PDM业务分类。");
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
            new Dictionary<string, string>());
        var saved = await materials.SaveIntegrationConfigurationAsync(configuration, cancellationToken);
        await AuditAsync(actor, "u9.material-integration.update", "u9-material", $"更新U9C料品集成配置：{saved.BaseUrl}；应用：{saved.ClientId}；真实写入：{(saved.WriteEnabled ? "开启" : "关闭")}", cancellationToken);
        return ToSettings(saved);
    }

    private static PdmMaterial Normalize(Guid id, SaveMaterialCommand command, PdmMaterial? existing, string actor, DateTimeOffset now, string categoryCode)
    {
        var code = existing?.MaterialCode ?? string.Empty;
        var name = Required(command.Name, "物料名称");
        var unit = U9UnitCatalog.Normalize(command.UnitCode);
        if (name.Length > 300) throw new PdmRuleException("物料名称不能超过300个字符。");
        if (command.Weight is <= 0) throw new PdmRuleException("重量必须大于0。");
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
            PurchaseLink: NormalizePurchaseLink(command.PurchaseLink));
    }

    private static void ValidateForApproval(PdmMaterial material)
    {
        _ = Required(material.MaterialCode, "PDM物料编码");
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
        if (!category.IsVisible) throw new PdmRuleException("料品分类在PDM中已屏蔽。");
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

    private static string? NormalizePurchaseLink(string? value)
    {
        var normalized = Clean(value);
        if (normalized is null) return null;
        if (normalized.Length > 2048) throw new PdmRuleException("料品采购链接不能超过2048个字符。");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PdmRuleException("料品采购链接必须是有效的HTTP或HTTPS地址。");
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
        configuration.UnitCodeMappings ?? new Dictionary<string, string>());
}
