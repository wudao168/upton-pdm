using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

internal sealed record U9MaterialReadRow(string Code, U9ItemReference? Item, string? Error);

public sealed class StandardLibraryImportService(IStandardLibraryRepository library, IMaterialRepository materials,
    IPdmRepository repository, IU9SecretProtector secrets, IU9OpenApiClient client, TimeProvider clock)
{
    public async Task<StandardLibraryImportResult> ImportAsync(IReadOnlyList<string> materialCodes, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, ct))
            throw new UnauthorizedAccessException("当前角色没有标准物料导入权限。");
        return await ImportForSynchronizationAsync(materialCodes, actor, ct);
    }

    // 两种入站流程共用鉴权、批量查询、单号补查和组织/有效性校验。
    internal async Task<IReadOnlyList<U9MaterialReadRow>> ReadForSynchronizationAsync(IReadOnlyList<string> materialCodes, CancellationToken ct)
    {
        if (materialCodes is null || materialCodes.Count is < 1 or > 200) throw new PdmRuleException("请提供1至200个料号。");
        var codes = materialCodes.Select(code => code?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Any(code => code.Length is < 1 or > 100 || code.Any(char.IsControl))) throw new PdmRuleException("料号不能为空、不能含控制字符，且最多100个字符。");
        var configuration = await materials.GetIntegrationConfigurationAsync(ct);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("ERP料品查询尚未正确配置，请联系管理员核对U9C查询接口与应用密钥。");
        var secret = secrets.Unprotect(configuration.ClientSecretCiphertext);
        async Task<string> Authenticate() => (await client.AuthenticateAsync(new(configuration.BaseUrl, configuration.EnterpriseCode,
            configuration.OrganizationCode, configuration.UserCode, configuration.ClientId, secret), ct)).Token;
        var token = await Authenticate();
        async Task<U9ItemQueryResult> Query(IReadOnlyList<string> requested)
        {
            var payload = U9MaterialPayloadFactory.QueryPayload(requested, "material-inbound-" + Guid.NewGuid().ToString("N"));
            var response = await client.QueryItemsAsync(configuration.BaseUrl, configuration.ItemQueryPath, token, payload, ct);
            if (response.ResponseCode != 402) return response;
            token = await Authenticate();
            return await client.QueryItemsAsync(configuration.BaseUrl, configuration.ItemQueryPath, token, payload, ct);
        }
        var rows = new List<U9MaterialReadRow>();
        foreach (var batch in codes.Chunk(50))
        {
            ct.ThrowIfCancellationRequested();
            U9ItemQueryResult result;
            try { result = await Query(batch); }
            catch (PdmRuleException) when (batch.Length > 1) { result = new(-1, null, []); }
            foreach (var code in batch)
            {
                try
                {
                    var detail = result;
                    if (batch.Length > 1 && (result.ResponseCode != 0 || !result.Items.Any(item => string.Equals(item.U9ItemCode?.Trim(), code, StringComparison.OrdinalIgnoreCase))))
                    {
                        try { detail = await Query([code]); }
                        catch (PdmRuleException) { throw new PdmRuleException("ERP料品读取失败，请稍后重试或联系管理员检查接口。"); }
                    }
                    if (detail.ResponseCode != 0) throw new PdmRuleException("ERP料品读取失败，请稍后重试或联系管理员检查接口。");
                    var matches = detail.Items.Where(item => string.Equals(item.U9ItemCode?.Trim(), code, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0) throw new PdmRuleException("ERP中未找到该料号，未导入。");
                    if (matches.Length != 1) throw new PdmRuleException("ERP返回多个同号料品，请核对ERP资料后重试。");
                    var item = matches[0];
                    if (!string.IsNullOrWhiteSpace(item.StockPlan?.OrganizationCode) && item.StockPlan.OrganizationCode != configuration.OrganizationCode)
                        throw new PdmRuleException("ERP返回的料品不属于当前U9C组织，未导入。");
                    if (item.CreationAttributes.GetValueOrDefault("Effective.IsEffective") is "false" or "0") throw new PdmRuleException("ERP料品已停用，未导入。");
                    if (string.IsNullOrWhiteSpace(item.U9ItemName) || string.IsNullOrWhiteSpace(item.U9UnitCode)) throw new PdmRuleException("ERP料品缺少名称或计量单位，请补齐ERP资料后重试。");
                    rows.Add(new(code, item, null));
                }
                catch (PdmRuleException error) { rows.Add(new(code, null, error.Message)); }
            }
        }
        return rows;
    }

    internal async Task<StandardLibraryImportResult> ImportForSynchronizationAsync(IReadOnlyList<string> materialCodes, string actor,
        CancellationToken ct, IReadOnlyList<U9MaterialReadRow>? source = null, bool addToLibrary = true)
    {
        var read = source ?? await ReadForSynchronizationAsync(materialCodes, ct);
        var categories = await materials.ListCategoriesAsync(true, ct);
        var uncategorized = (await library.ListCategoriesAsync(false, ct)).FirstOrDefault(StandardLibraryDefaults.IsUncategorized)
            ?? throw new PdmRuleException("未分类目录尚未初始化，请联系管理员完成数据库升级。");
        var members = (await library.ListMembershipsAsync(ct)).Select(item => item.MaterialId).ToHashSet();
        var rows = new List<StandardLibraryImportRow>();
        foreach (var row in read)
        {
            try
            {
                if (row.Item is not { } item) throw new PdmRuleException(row.Error!);
                if (StockedMaterialPolicy.ManagementOnlyCategory(item.U9CategoryCode)) throw new PdmRuleException("该分类仅用于常备件管理，不能导入通用料品或标准库。");
                var category = categories.FirstOrDefault(value => value.Code.Equals(item.U9CategoryCode, StringComparison.OrdinalIgnoreCase) && value.IsActive && value.PdmKind.HasValue);
                if (category is null) throw new PdmRuleException("ERP分类尚未配置对应的物料类型，请先维护料品分类。");
                var existing = await materials.FindMaterialByCodeAsync(row.Code, ct);
                var restoreObservedU9 = !addToLibrary && existing is { MasterOwner: MaterialMasterOwner.U9C,
                    ArchivedBy: "system:u9-material-full-sync", ApprovalStatus: MaterialApprovalStatus.Approved, U9SyncConfirmed: true };
                if (existing?.IsArchived == true && !restoreObservedU9) throw new PdmRuleException("料品主档已停用，请先核对并启用料品。");
                if (existing is not null && (existing.ApprovalStatus != MaterialApprovalStatus.Approved || !existing.U9SyncConfirmed))
                    throw new PdmRuleException("同号料品主档尚未批准或确认ERP同步，请先处理原料品。");
                var now = clock.GetUtcNow();
                var supply = item.U9ItemFormAttribute switch { 9 => MaterialSupplyMode.Purchase, 2 => MaterialSupplyMode.Manufacture, _ => category.DefaultSupplyMode };
                var candidate = new PdmMaterial(Guid.NewGuid(), row.Code, item.U9ItemName!.Trim(), category.PdmKind!.Value, supply,
                    U9UnitCatalog.NormalizeInbound(item.U9UnitCode!), item.U9Specification, item.U9Material, item.U9Description, item.U9Brand, item.U9SurfaceTreatment,
                    item.U9Weight, item.U9WeightUnitCode, null, MaterialApprovalStatus.Approved, actor, now, category.Code,
                    item.U9ItemId, row.Code, MaterialSyncStatus.Succeeded, actor, now, actor, now, 1, category.Code,
                    U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C, LastU9SyncedAt: now, PurchaseLink: item.U9PurchaseLink);
                if (existing?.MasterOwner == MaterialMasterOwner.U9C) candidate = candidate with
                {
                    SelectionAdvice = existing.SelectionAdvice, ReferencePrice = existing.ReferencePrice, IsRecommended = existing.IsRecommended,
                    CoverImageAttachmentId = existing.CoverImageAttachmentId, Model3DLink = existing.Model3DLink, DocumentLink = existing.DocumentLink,
                    ReferenceCount = existing.ReferenceCount, Model3DAttachmentCount = existing.Model3DAttachmentCount, DocumentAttachmentCount = existing.DocumentAttachmentCount
                };
                var saved = existing?.MasterOwner == MaterialMasterOwner.Pdm ? existing : await materials.UpsertU9MaterialAsync(candidate, ct);
                var alreadyMember = !members.Add(saved.Id);
                if (addToLibrary && !alreadyMember) await library.AddMembershipsAsync([new(uncategorized.Id, saved.Id, actor, now)], ct);
                rows.Add(new(row.Code, alreadyMember ? "Existing" : "Imported", alreadyMember ? "已在标准库，保留原分类。" : "ERP料品已导入。", saved.Name, saved.Specification, saved.Brand));
            }
            catch (PdmRuleException error) { rows.Add(new(row.Code, "Failed", error.Message)); }
        }
        var imported = new StandardLibraryImportResult(rows);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "standard-library.import", nameof(PdmMaterial), "ERP",
            $"按料号只读导入ERP料品：新增{imported.ImportedCount}，已存在{imported.ExistingCount}，失败{imported.FailedCount}；未向ERP写入。"), ct);
        return imported;
    }
}
