using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class StandardLibraryImportService(
    IStandardLibraryRepository library, StandardLibraryService standardLibrary,
    IMaterialRepository materials, IPdmRepository repository,
    IU9SecretProtector secrets, IU9OpenApiClient client, TimeProvider clock)
{
    public async Task<StandardLibraryImportResult> ImportAsync(IReadOnlyList<string> materialCodes, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, ct))
            throw new UnauthorizedAccessException("当前角色没有标准物料导入权限。");
        if (materialCodes is null || materialCodes.Count is < 1 or > 200) throw new PdmRuleException("请提供1至200个料号。");
        var codes = materialCodes.Select(code => code?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Any(code => code.Length is < 1 or > 100 || code.Any(char.IsControl))) throw new PdmRuleException("料号不能为空、不能含控制字符，且最多100个字符。");
        var uncategorized = (await library.ListCategoriesAsync(false, ct)).FirstOrDefault(StandardLibraryDefaults.IsUncategorized)
            ?? throw new PdmRuleException("未分类目录尚未初始化，请联系管理员完成数据库升级。");
        var configuration = await materials.GetIntegrationConfigurationAsync(ct);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("ERP料品查询尚未正确配置，请联系管理员核对U9C查询接口与应用密钥。");
        var secret = secrets.Unprotect(configuration.ClientSecretCiphertext);
        async Task<string> Authenticate() => (await client.AuthenticateAsync(new(configuration.BaseUrl, configuration.EnterpriseCode,
            configuration.OrganizationCode, configuration.UserCode, configuration.ClientId, secret), ct)).Token;
        var token = await Authenticate();
        async Task<U9ItemQueryResult> Query(IReadOnlyList<string> requested)
        {
            var payload = U9MaterialPayloadFactory.QueryPayload(requested, "standard-library-import-" + Guid.NewGuid().ToString("N"));
            var response = await client.QueryItemsAsync(configuration.BaseUrl, configuration.ItemQueryPath, token, payload, ct);
            if (response.ResponseCode != 402) return response;
            token = await Authenticate();
            return await client.QueryItemsAsync(configuration.BaseUrl, configuration.ItemQueryPath, token, payload, ct);
        }
        var categories = await materials.ListCategoriesAsync(true, ct);
        var members = (await library.ListMembershipsAsync(ct)).Select(item => item.MaterialId).ToHashSet();
        var rows = new List<StandardLibraryImportRow>();
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
                    // Some ERP versions return only one item for a batch. Retry missing codes individually.
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
                    if (item.CreationAttributes.GetValueOrDefault("Effective.IsEffective") is "false" or "0") throw new PdmRuleException("ERP料品已停用，未导入。");
                    var category = categories.FirstOrDefault(value => value.Code.Equals(item.U9CategoryCode, StringComparison.OrdinalIgnoreCase) && value.IsActive && value.PdmKind.HasValue);
                    if (category is null) throw new PdmRuleException("ERP分类尚未配置对应的物料类型，请先维护料品分类。");
                    if (string.IsNullOrWhiteSpace(item.U9ItemName) || string.IsNullOrWhiteSpace(item.U9UnitCode)) throw new PdmRuleException("ERP料品缺少名称或计量单位，请补齐ERP资料后重试。");
                    var existing = await materials.FindMaterialByCodeAsync(code, ct);
                    if (existing?.IsArchived == true) throw new PdmRuleException("料品主档已停用，请先核对并启用料品。");
                    if (existing is not null && (existing.ApprovalStatus != MaterialApprovalStatus.Approved || !existing.U9SyncConfirmed))
                        throw new PdmRuleException("同号料品主档尚未批准或确认ERP同步，请先处理原料品。");
                    var now = clock.GetUtcNow();
                    var supply = item.U9ItemFormAttribute switch { 9 => MaterialSupplyMode.Purchase, 2 => MaterialSupplyMode.Manufacture, _ => category.DefaultSupplyMode };
                    var candidate = new PdmMaterial(Guid.NewGuid(), code, item.U9ItemName.Trim(), category.PdmKind!.Value, supply,
                        U9UnitCatalog.NormalizeInbound(item.U9UnitCode), item.U9Specification, item.U9Material, item.U9Description, item.U9Brand, item.U9SurfaceTreatment,
                        item.U9Weight, item.U9WeightUnitCode, null, MaterialApprovalStatus.Approved, actor, now, category.Code,
                        item.U9ItemId, code, MaterialSyncStatus.Succeeded, actor, now, actor, now, 1, category.Code,
                        U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C, LastU9SyncedAt: now, PurchaseLink: item.U9PurchaseLink);
                    // Refresh ERP fields while preserving PLM advice, attachments, recommendation and price.
                    if (existing?.MasterOwner == MaterialMasterOwner.U9C) candidate = candidate with
                    {
                        SelectionAdvice = existing.SelectionAdvice, ReferencePrice = existing.ReferencePrice, IsRecommended = existing.IsRecommended,
                        CoverImageAttachmentId = existing.CoverImageAttachmentId, Model3DLink = existing.Model3DLink, DocumentLink = existing.DocumentLink,
                        ReferenceCount = existing.ReferenceCount,
                        Model3DAttachmentCount = existing.Model3DAttachmentCount, DocumentAttachmentCount = existing.DocumentAttachmentCount
                    };
                    var saved = existing?.MasterOwner == MaterialMasterOwner.Pdm ? existing : await materials.UpsertU9MaterialAsync(candidate, ct);
                    await standardLibrary.AddMaterialsAsync(new([uncategorized.Id], [saved.Id]), actor, role, ct);
                    var alreadyMember = !members.Add(saved.Id);
                    rows.Add(new(code, alreadyMember ? "Existing" : "Imported", alreadyMember ? "已在标准库，保留原分类。" : "已从ERP导入，归入未分类。", saved.Name, saved.Specification, saved.Brand));
                }
                catch (PdmRuleException error) { rows.Add(new(code, "Failed", error.Message)); }
            }
        }
        var imported = new StandardLibraryImportResult(rows);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "standard-library.import", nameof(PdmMaterial), "ERP",
            $"按料号只读导入ERP料品：新增入库{imported.ImportedCount}，已存在{imported.ExistingCount}，失败{imported.FailedCount}；未向ERP写入。"), ct);
        return imported;
    }
}
