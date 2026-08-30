using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9MaterialIntegrationService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient client,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> SampleCategoryCodes = new(StringComparer.OrdinalIgnoreCase) { "0101", "0102", "0204" };

    public async Task<U9ConnectionTestResult> TestConnectionAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权测试U9C连接。");

        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("请先保存U9C应用密钥，再测试认证。");

        var request = new U9AuthenticationRequest(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext));
        await client.AuthenticateAsync(request, cancellationToken);

        var testedAt = timeProvider.GetUtcNow();
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(),
            testedAt,
            actor,
            "u9.material-integration.authenticate",
            nameof(U9MaterialIntegrationConfiguration),
            "u9-material",
            $"U9C OAuth2认证测试成功：{configuration.BaseUrl}；企业：{configuration.EnterpriseCode}；组织：{configuration.OrganizationCode}；应用：{configuration.ClientId}"), cancellationToken);

        return new U9ConnectionTestResult(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            testedAt);
    }

    public async Task<U9ItemQueryResult> QueryByCodeAsync(
        string materialCode,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialView, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权查询U9C料品。");
        var code = materialCode.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PdmRuleException("物料编码不能为空。");
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext)) throw new PdmRuleException("U9C应用密钥尚未配置。");
        var authentication = await client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);
        var result = await client.QueryItemsAsync(
            configuration.BaseUrl,
            configuration.ItemQueryPath,
            authentication.Token,
            U9MaterialPayloadFactory.QueryPayload(code, $"pdm-query-{Guid.NewGuid():N}"),
            cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "u9.material.query", nameof(PdmMaterial), code,
            $"按编码只读查询U9C料品：{code}；结果：{result.Items.Count}条"), cancellationToken);
        return result;
    }

    public async Task<U9MaterialSamplePreview> PreviewSampleAsync(
        IReadOnlyList<string>? categoryCodes,
        int limitPerCategory,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权读取U9C料品样本。");
        var categoriesToQuery = NormalizeSampleCategories(categoryCodes);
        if (limitPerCategory is < 1 or > 10) throw new PdmRuleException("每个分类最多只能同步10个料品。");

        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("请先保存U9C应用密钥，再读取料品样本。");
        var authentication = await client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);

        var items = new List<U9MaterialSampleItem>(categoriesToQuery.Count * limitPerCategory);
        foreach (var categoryCode in categoriesToQuery)
        {
            var category = await materials.FindCategoryAsync(categoryCode, cancellationToken)
                ?? throw new PdmRuleException($"PLM中尚未维护U9C分类 {categoryCode}。");
            if (category.PdmKind is null) throw new PdmRuleException($"分类 {categoryCode} 尚未配置PLM业务类型。");
            var filter = $"MainItemCategory.Code = '{categoryCode}'";
            var referencePayload = JsonSerializer.Serialize(new
            {
                ReferenceCode = "ItemMaster",
                ReferenceEntityFullName = "UFIDA.U9.CBO.SCM.Item.ItemMaster",
                ReferenceDefaultFilter = filter,
                Transclude = string.Empty,
                TargetOrgCode = configuration.OrganizationCode,
                PageIndex = 0,
                PageSize = limitPerCategory,
                Filter = string.Empty,
                FilterObjectXML = string.Empty
            });
            var references = await client.QueryCustomerReferencesAsync(
                configuration.BaseUrl,
                configuration.CustomerQueryPath,
                authentication.Token,
                referencePayload,
                cancellationToken);
            if (references.ResponseCode != 0)
                throw new PdmRuleException($"U9C分类 {categoryCode} 料品参照查询失败（ResCode={references.ResponseCode}）：{references.ResponseMessage ?? "未返回错误说明"}。");

            foreach (var reference in references.Customers.Take(limitPerCategory))
            {
                var detail = await client.QueryItemsAsync(
                    configuration.BaseUrl,
                    configuration.ItemQueryPath,
                    authentication.Token,
                    U9MaterialPayloadFactory.QueryPayload(reference.Code, $"pdm-u9-sample-{categoryCode}-{Guid.NewGuid():N}"),
                    cancellationToken);
                if (detail.ResponseCode != 0)
                    throw new PdmRuleException($"U9C料品 {reference.Code} 查询失败（ResCode={detail.ResponseCode}）：{detail.ResponseMessage ?? "未返回错误说明"}。");
                var item = detail.Items.FirstOrDefault(value => string.Equals(value.U9ItemCode, reference.Code, StringComparison.OrdinalIgnoreCase));
                if (item is null) continue;
                if (!string.Equals(item.U9CategoryCode, categoryCode, StringComparison.OrdinalIgnoreCase))
                    throw new PdmRuleException($"U9C参照返回料品 {reference.Code}，但ItemMaster/Query分类为 {item.U9CategoryCode ?? "空"}；为避免错分未导入任何料品。");
                if (string.IsNullOrWhiteSpace(item.U9UnitCode))
                    throw new PdmRuleException($"U9C料品 {reference.Code} 未返回库存计量单位；为避免错误单位未导入任何料品。");

                var existing = await materials.FindMaterialByCodeAsync(reference.Code, cancellationToken);
                var canImport = existing is null || existing.MasterOwner == MaterialMasterOwner.U9C;
                var weight = item.U9Weight is > 0 ? item.U9Weight : null;
                items.Add(new U9MaterialSampleItem(
                    item.U9ItemId ?? string.Empty,
                    reference.Code.Trim(),
                    (item.U9ItemName ?? reference.Name).Trim(),
                    category.Code,
                    category.Name,
                    category.PdmKind.Value,
                    ResolveSupplyMode(item.U9ItemFormAttribute, category.DefaultSupplyMode),
                    U9UnitCatalog.Normalize(item.U9UnitCode),
                    Clean(item.U9Specification),
                    Clean(item.U9Brand),
                    Clean(item.U9Material),
                    Clean(item.U9SurfaceTreatment),
                    Clean(item.U9Description),
                    weight,
                    weight is null ? null : Clean(item.U9WeightUnitCode),
                    Clean(item.U9PurchaseLink),
                    existing is not null,
                    canImport,
                    existing is null ? "新建" : canImport ? "刷新U9C来源料品" : "同号料品由PLM主控，跳过"));
            }
        }

        return new U9MaterialSamplePreview(categoriesToQuery, limitPerCategory, items, timeProvider.GetUtcNow());
    }

    public async Task<U9MaterialSampleImportResult> ImportSampleAsync(
        IReadOnlyList<string>? categoryCodes,
        int limitPerCategory,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewSampleAsync(categoryCodes, limitPerCategory, actor, role, cancellationToken);
        var importedAt = timeProvider.GetUtcNow();
        var savedMaterials = new List<PdmMaterial>();
        var createdCount = 0;
        var refreshedCount = 0;
        var skippedCount = 0;
        foreach (var item in preview.Items)
        {
            if (!item.CanImport)
            {
                skippedCount++;
                continue;
            }
            var candidate = new PdmMaterial(
                Guid.NewGuid(), item.MaterialCode, item.Name, item.Kind, item.SupplyMode, item.UnitCode,
                item.Specification, item.Material, item.Remark, item.Brand, item.SurfaceTreatment,
                item.Weight, item.WeightUnit, null,
                MaterialApprovalStatus.Approved, actor, importedAt, item.CategoryCode,
                item.U9ItemId, item.MaterialCode, MaterialSyncStatus.Succeeded,
                actor, importedAt, actor, importedAt, 1, item.CategoryCode,
                U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C,
                LastU9SyncedAt: importedAt, PurchaseLink: item.PurchaseLink);
            var saved = await materials.UpsertU9MaterialAsync(candidate, cancellationToken);
            if (saved.MasterOwner != MaterialMasterOwner.U9C)
            {
                skippedCount++;
                continue;
            }
            if (saved.Id == candidate.Id) createdCount++;
            else refreshedCount++;
            savedMaterials.Add(saved);
        }

        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), importedAt, actor, "u9.material.sample-import", nameof(PdmMaterial), string.Join(',', preview.CategoryCodes),
            $"U9C料品样本导入：每类上限{preview.LimitPerCategory}；新建{createdCount}，刷新{refreshedCount}，跳过{skippedCount}；未执行U9C写入。"), cancellationToken);
        return new U9MaterialSampleImportResult(preview, createdCount, refreshedCount, skippedCount, savedMaterials, importedAt);
    }

    public async Task<MaterialSyncExecutionResult> ExecuteTaskAsync(
        Guid taskId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行U9C料品同步。");

        return await ExecuteApprovedTaskCoreAsync(taskId, actor, cancellationToken);
    }

    internal Task<MaterialSyncExecutionResult> ExecuteApprovedTaskAsync(
        Guid taskId,
        string actor,
        CancellationToken cancellationToken) =>
        ExecuteApprovedTaskCoreAsync(taskId, actor, cancellationToken);

    private async Task<MaterialSyncExecutionResult> ExecuteApprovedTaskCoreAsync(
        Guid taskId,
        string actor,
        CancellationToken cancellationToken)
    {

        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!configuration.WriteEnabled)
            throw new PdmRuleException("U9C料品真实写入尚未启用。请由管理员核对请求预览后显式开启。");
        if (!string.Equals(configuration.ItemCreatePath, U9MaterialContract.CreatePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration.ItemModifyPath, U9MaterialContract.ModifyPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Create/Modify/Query路径与已冻结的官方合同不一致。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置。");

        var existingTask = await materials.FindSyncTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C同步任务不存在。");
        if (existingTask.Operation is not (MaterialSyncOperation.Create or MaterialSyncOperation.Update))
            throw new PdmRuleException("当前同步任务类型不受支持。");
        var sourceMaterial = await materials.FindMaterialAsync(existingTask.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的物料主档不存在。");
        if (sourceMaterial.IsArchived)
            throw new PdmRuleException("已归档料品不能同步到U9C。");
        if (sourceMaterial.ApprovalStatus != MaterialApprovalStatus.Approved)
            throw new PdmRuleException("只有已批准物料才能同步到U9C。");
        if (existingTask.Operation == MaterialSyncOperation.Update && !sourceMaterial.U9SyncConfirmed)
            throw new PdmRuleException("料品没有本系统实际写入U9C的确认凭据，不能执行修改。");

        var u9UnitCode = U9MaterialPayloadFactory.ResolveUnitCode(sourceMaterial.UnitCode);
        if (!PayloadUsesUnitCode(existingTask, u9UnitCode))
            throw new PdmRuleException($"料品计量单位编码已变更为 {u9UnitCode}。请先点击“重试”重新生成请求预览和SHA-256，再执行同步。");

        var task = await materials.BeginSyncTaskAsync(taskId, timeProvider.GetUtcNow(), cancellationToken);
        var writeAttempted = false;
        var writeResponseReceived = false;
        var postWriteVerificationStarted = false;
        try
        {
            var authentication = await client.AuthenticateAsync(new U9AuthenticationRequest(
                configuration.BaseUrl,
                configuration.EnterpriseCode,
                configuration.OrganizationCode,
                configuration.UserCode,
                configuration.ClientId,
                secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);

            var uomQuery = await client.QueryUomsAsync(
                configuration.BaseUrl,
                authentication.Token,
                U9MaterialPayloadFactory.UomQueryPayload(u9UnitCode, task.CorrelationId),
                cancellationToken);
            if (uomQuery.ResponseCode != 0)
                throw new PdmRuleException($"U9C计量单位查询失败（ResCode={uomQuery.ResponseCode}）：{uomQuery.ResponseMessage ?? "未返回错误说明"}。");
            if (!uomQuery.Units.Any(unit => string.Equals(unit.U9UomCode?.Trim(), u9UnitCode, StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException($"PLM计量单位编码 {u9UnitCode} 在U9C中不存在；未执行料品写入。");

            var query = await client.QueryItemsAsync(
                configuration.BaseUrl,
                configuration.ItemQueryPath,
                authentication.Token,
                U9MaterialPayloadFactory.QueryPayload(sourceMaterial.MaterialCode, task.CorrelationId),
                cancellationToken);
            if (query.ResponseCode != 0)
                throw new PdmRuleException($"U9C料品幂等查询失败（ResCode={query.ResponseCode}）：{query.ResponseMessage ?? "未返回错误说明"}。");

            var existingItem = query.Items.FirstOrDefault(item =>
                string.Equals(item.U9ItemCode, sourceMaterial.MaterialCode, StringComparison.OrdinalIgnoreCase));
            if (task.Operation == MaterialSyncOperation.Create
                && existingTask.Status == MaterialSyncStatus.NeedsReview
                && existingItem is null)
                throw new PdmRuleException($"U9C回查未找到待复核料号 {sourceMaterial.MaterialCode}；为避免重复创建，任务保持待复核，请人工核对U9C返回记录。");

            if (task.Operation == MaterialSyncOperation.Create && existingItem is not null)
            {
                if (existingTask.Status != MaterialSyncStatus.NeedsReview)
                    throw new PdmRuleException($"U9C已存在料号 {sourceMaterial.MaterialCode}。系统不会自动绑定同号料品；请校准该分类流水并重新创建PLM料品。");

                var differences = CompareMappedFields(sourceMaterial, existingItem);
                if (differences.Count > 0)
                    throw new PdmRuleException($"U9C已存在待复核料号 {sourceMaterial.MaterialCode}，但字段仍不一致：{string.Join("；", differences)}");

                return await CompleteAsync(
                    task,
                    sourceMaterial,
                    existingItem.U9ItemId,
                    existingItem.U9ItemCode ?? sourceMaterial.MaterialCode,
                    created: false,
                    alreadyExisted: true,
                    updated: false,
                    actor,
                    cancellationToken);
            }

            if (task.Operation == MaterialSyncOperation.Update && existingItem is null)
                throw new PdmRuleException("U9C不存在同料号，不能执行修改；请先核对创建任务和料号映射。");

            writeAttempted = true;
            var write = await client.PostBatchAsync(
                configuration.BaseUrl,
                task.Operation == MaterialSyncOperation.Create ? configuration.ItemCreatePath : configuration.ItemModifyPath,
                authentication.Token,
                task.PayloadJson,
                cancellationToken);
            writeResponseReceived = true;
            var operationName = task.Operation == MaterialSyncOperation.Create ? "创建" : "修改";
            if (write.ResponseCode != 0)
                throw new PdmRuleException($"U9C料品{operationName}失败（ResCode={write.ResponseCode}）：{write.ResponseMessage ?? "未返回错误说明"}。");
            if (write.Rows.Count > 0 && write.Rows.Any(row => !row.IsSuccess))
                throw new PdmRuleException($"U9C料品{operationName}失败：{write.Rows.First(row => !row.IsSuccess).ErrorMessage ?? "未返回错误说明"}。");
            var resultRow = write.Rows.FirstOrDefault();
            postWriteVerificationStarted = true;
            var verifiedItem = await VerifyWrittenMaterialAsync(
                configuration,
                authentication.Token,
                task,
                sourceMaterial,
                cancellationToken);

            return await CompleteAsync(
                task,
                sourceMaterial,
                verifiedItem.U9ItemId ?? resultRow?.U9ItemId ?? existingItem?.U9ItemId ?? sourceMaterial.U9ItemId,
                verifiedItem.U9ItemCode ?? resultRow?.U9ItemCode ?? existingItem?.U9ItemCode ?? sourceMaterial.MaterialCode,
                created: task.Operation == MaterialSyncOperation.Create,
                alreadyExisted: false,
                updated: task.Operation == MaterialSyncOperation.Update,
                actor,
                cancellationToken);
        }
        catch (PdmRuleException exception)
        {
            var status = existingTask.Status == MaterialSyncStatus.NeedsReview
                || writeAttempted && (!writeResponseReceived || postWriteVerificationStarted)
                ? MaterialSyncStatus.NeedsReview
                : MaterialSyncStatus.Failed;
            var now = timeProvider.GetUtcNow();
            var audit = new AuditEntry(
                Guid.NewGuid(),
                now,
                actor,
                "u9.material.sync.failed",
                nameof(PdmMaterial),
                sourceMaterial.Id.ToString(),
                $"U9C料品同步{(status == MaterialSyncStatus.NeedsReview ? "结果不确定，需先回查" : "失败")}：{sourceMaterial.MaterialCode}；{exception.Message}");
            await materials.FailSyncTaskAsync(task.Id, status, exception.Message, null, audit, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var status = existingTask.Status == MaterialSyncStatus.NeedsReview || writeAttempted
                ? MaterialSyncStatus.NeedsReview
                : MaterialSyncStatus.Failed;
            var now = timeProvider.GetUtcNow();
            var audit = new AuditEntry(
                Guid.NewGuid(), now, actor, "u9.material.sync.failed", nameof(PdmMaterial), sourceMaterial.Id.ToString(),
                $"U9C料品同步{(status == MaterialSyncStatus.NeedsReview ? "结果不确定，需先回查" : "失败")}：{sourceMaterial.MaterialCode}；{exception.Message}");
            await materials.FailSyncTaskAsync(task.Id, status, exception.Message, null, audit, CancellationToken.None);
            throw;
        }
    }

    private async Task<U9ItemReference> VerifyWrittenMaterialAsync(
        U9MaterialIntegrationConfiguration configuration,
        string token,
        MaterialSyncTask task,
        PdmMaterial expected,
        CancellationToken cancellationToken)
    {
        var retryGaps = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
        IReadOnlyList<string> lastDifferences = ["U9C写后查询未返回料品。"];
        foreach (var delay in retryGaps)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            U9ItemQueryResult query;
            try
            {
                query = await client.QueryItemsAsync(
                    configuration.BaseUrl,
                    configuration.ItemQueryPath,
                    token,
                    U9MaterialPayloadFactory.QueryPayload(expected.MaterialCode, $"{task.CorrelationId}-verify"),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastDifferences = [$"U9C写后查询失败：{exception.Message}"];
                continue;
            }
            if (query.ResponseCode != 0)
            {
                lastDifferences = [$"U9C写后查询失败（ResCode={query.ResponseCode}）：{query.ResponseMessage ?? "未返回错误说明"}"];
                continue;
            }
            var actual = query.Items.FirstOrDefault(item => SameCode(item.U9ItemCode, expected.MaterialCode));
            if (actual is null)
            {
                lastDifferences = [$"U9C写后查询未找到料号 {expected.MaterialCode}。"];
                continue;
            }
            var differences = CompareMappedFields(expected, actual);
            if (differences.Count == 0) return actual;
            lastDifferences = differences;
        }
        throw new PdmRuleException($"U9C写入已返回，但写后回查未确认字段一致，任务进入待复核：{string.Join("；", lastDifferences)}");
    }

    private static IReadOnlyList<string> CompareMappedFields(PdmMaterial expected, U9ItemReference actual)
    {
        var differences = new List<string>();
        CompareText("名称", expected.Name, actual.U9ItemName, differences);
        CompareCode("料号", expected.MaterialCode, actual.U9ItemCode, differences);
        CompareCode("U9分类", expected.CategoryCode ?? expected.U9CategoryCode, actual.U9CategoryCode, differences);
        CompareCode("计量单位", U9MaterialPayloadFactory.ResolveUnitCode(expected.UnitCode), actual.U9UnitCode, differences);
        CompareText("型号/规格", expected.Specification, actual.U9Specification, differences);
        CompareText("备注", expected.Remark, actual.U9Description, differences);
        CompareText("品牌", expected.Brand, actual.U9Brand, differences);
        CompareText("材质", expected.Material, actual.U9Material, differences);
        CompareText("表面处理", expected.SurfaceTreatment, actual.U9SurfaceTreatment, differences);
        CompareText("采购链接", expected.PurchaseLink, actual.U9PurchaseLink, differences);
        if (NormalizeWeight(expected.Weight) != NormalizeWeight(actual.U9Weight))
            differences.Add($"重量 PLM={Display(expected.Weight)} U9C={Display(actual.U9Weight)}");
        return differences;
    }

    private static void CompareText(string field, string? expected, string? actual, ICollection<string> differences)
    {
        if (!string.Equals(NormalizeText(expected), NormalizeText(actual), StringComparison.Ordinal))
            differences.Add($"{field} PLM={Display(expected)} U9C={Display(actual)}");
    }

    private static void CompareCode(string field, string? expected, string? actual, ICollection<string> differences)
    {
        if (!SameCode(expected, actual)) differences.Add($"{field} PLM={Display(expected)} U9C={Display(actual)}");
    }

    private static bool SameCode(string? left, string? right) =>
        string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? NormalizeWeight(decimal? value) => value is null or 0m ? null : decimal.Round(value.Value, 6, MidpointRounding.AwayFromZero);

    private static string Display(object? value) => value?.ToString() ?? "<空>";

    private static bool PayloadUsesUnitCode(MaterialSyncTask task, string expectedCode)
    {
        try
        {
            using var document = JsonDocument.Parse(task.PayloadJson);
            var row = document.RootElement[0];
            if (task.Operation == MaterialSyncOperation.Create)
                return row.TryGetProperty("InventoryUOM", out var inventoryUom)
                    && inventoryUom.TryGetProperty("Code", out var code)
                    && string.Equals(code.GetString()?.Trim(), expectedCode, StringComparison.OrdinalIgnoreCase);

            if (!row.TryGetProperty("Attributes", out var attributes)) return false;
            foreach (var attribute in attributes.EnumerateArray())
            {
                if (!attribute.TryGetProperty("AttributeName", out var name)
                    || !string.Equals(name.GetString(), "InventoryUOM", StringComparison.OrdinalIgnoreCase)
                    || !attribute.TryGetProperty("EntityValue", out var entityValue)
                    || !entityValue.TryGetProperty("Code", out var code)) continue;
                return string.Equals(code.GetString()?.Trim(), expectedCode, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<MaterialSyncExecutionResult> CompleteAsync(
        MaterialSyncTask task,
        PdmMaterial material,
        string? u9ItemId,
        string u9ItemCode,
        bool created,
        bool alreadyExisted,
        bool updated,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var preview = JsonSerializer.Serialize(new
        {
            responseCode = 0,
            u9ItemId,
            u9ItemCode,
            created,
            alreadyExisted,
            updated
        }, JsonOptions);
        var audit = new AuditEntry(
            Guid.NewGuid(),
            now,
            actor,
            updated ? "u9.material.sync.updated" : alreadyExisted ? "u9.material.sync.idempotent-hit" : "u9.material.sync.created",
            nameof(PdmMaterial),
            material.Id.ToString(),
            updated
                ? $"U9C料品修改成功：{material.MaterialCode}"
                : alreadyExisted
                ? $"U9C已存在同料号，幂等回写成功：{material.MaterialCode} → {u9ItemCode}"
                : $"U9C料品创建成功：{material.MaterialCode} → {u9ItemCode}");
        var completed = await materials.CompleteSyncTaskAsync(
            task.Id,
            u9ItemId,
            u9ItemCode,
            preview,
            audit,
            cancellationToken);
        return new MaterialSyncExecutionResult(completed.Material, completed.Task, created, alreadyExisted, updated);
    }

    private static IReadOnlyList<string> NormalizeSampleCategories(IReadOnlyList<string>? categoryCodes)
    {
        var normalized = (categoryCodes ?? []).Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0) throw new PdmRuleException("请至少选择一个料品分类。");
        var unsupported = normalized.Where(value => !SampleCategoryCodes.Contains(value)).ToArray();
        if (unsupported.Length > 0) throw new PdmRuleException($"样本同步仅允许分类0101、0102、0204；不支持：{string.Join('、', unsupported)}。");
        return normalized;
    }

    private static MaterialSupplyMode ResolveSupplyMode(int? itemFormAttribute, MaterialSupplyMode fallback) => itemFormAttribute switch
    {
        9 => MaterialSupplyMode.Purchase,
        10 => MaterialSupplyMode.Manufacture,
        4 => MaterialSupplyMode.Outsource,
        _ => fallback
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
