using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9MaterialFullSyncService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9SecretProtector secretProtector,
    IU9OpenApiClient client,
    TimeProvider timeProvider)
{
    private const int ReferencePageSize = 1000;
    private const int FallbackReferencePageSize = 200;
    private const int MaximumReferencePages = 100;
    private const int DetailBatchSize = 50;
    private const int ExpiredTokenResponseCode = 402;

    public Task<U9MaterialFullSyncRun?> GetLatestRunAsync(CancellationToken cancellationToken) =>
        materials.GetLatestU9MaterialFullSyncRunAsync(cancellationToken);

    public async Task<U9MaterialFullSyncRun> SynchronizeAsync(
        string actor,
        string triggerKind,
        CancellationToken cancellationToken)
    {
        var categories = (await materials.ListCategoriesAsync(true, cancellationToken))
            .Where(category => category.AllowCreate && category.IsVisible && category.IsActive && category.PdmKind is not null)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var startedAt = timeProvider.GetUtcNow();
        var run = new U9MaterialFullSyncRun(
            Guid.NewGuid(), triggerKind.Trim(), U9MaterialFullSyncStatus.Running,
            categories.Select(category => category.Code).ToArray(), [], categories.Length,
            0, 0, 0, 0, 0, 0, null, startedAt, null);
        await materials.SaveU9MaterialFullSyncRunAsync(run, cancellationToken);

        if (categories.Length == 0)
        {
            run = run with { Status = U9MaterialFullSyncStatus.Succeeded, CompletedAt = timeProvider.GetUtcNow() };
            return await materials.SaveU9MaterialFullSyncRunAsync(run, cancellationToken);
        }

        try
        {
            var configuration = await RequireConfigurationAsync(cancellationToken);
            var clientSecret = secretProtector.Unprotect(configuration.ClientSecretCiphertext);

            var results = new List<U9MaterialFullSyncCategoryResult>(categories.Length);
            foreach (var category in categories)
            {
                U9MaterialFullSyncCategoryResult result;
                try
                {
                    var authentication = await client.AuthenticateAsync(new(
                        configuration.BaseUrl,
                        configuration.EnterpriseCode,
                        configuration.OrganizationCode,
                        configuration.UserCode,
                        configuration.ClientId,
                        clientSecret), cancellationToken);
                    result = await SynchronizeCategoryAsync(
                        configuration, authentication.Token, clientSecret, category, actor, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result = new(category.Code, category.Name, 0, 0, 0, 0, category.CurrentSequence, false, exception.Message);
                }
                results.Add(result);
                run = Aggregate(run, results, completed: false);
                await materials.SaveU9MaterialFullSyncRunAsync(run, cancellationToken);
            }

            run = Aggregate(run, results, completed: true);
            await materials.SaveU9MaterialFullSyncRunAsync(run, cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(
                Guid.NewGuid(), run.CompletedAt!.Value, actor, "u9.material.full-sync", nameof(U9MaterialFullSyncRun), run.Id.ToString(),
                $"U9C料品自动全量同步：分类{run.CategoryCount}，完成{run.CompletedCategoryCount}，失败{run.FailedCategoryCount}；" +
                $"发现{run.DiscoveredCount}，新建{run.CreatedCount}，刷新{run.RefreshedCount}，停用{run.CategoryResults.Sum(item => item.InactivatedCount)}，" +
                $"冲突{run.CategoryResults.Sum(item => item.ConflictCount)}，跳过{run.SkippedCount}；未执行U9C写入。"), cancellationToken);
            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            run = run with
            {
                Status = U9MaterialFullSyncStatus.Failed,
                LastError = exception.Message,
                CompletedAt = timeProvider.GetUtcNow()
            };
            await materials.SaveU9MaterialFullSyncRunAsync(run, CancellationToken.None);
            throw;
        }
    }

    private async Task<U9MaterialFullSyncCategoryResult> SynchronizeCategoryAsync(
        U9MaterialIntegrationConfiguration configuration,
        string token,
        string clientSecret,
        MaterialCategory category,
        string actor,
        CancellationToken cancellationToken)
    {
        var discovered = 0;
        var created = 0;
        var refreshed = 0;
        var skipped = 0;
        var conflicts = 0;
        var categorySyncStartedAt = timeProvider.GetUtcNow();
        var maximumSequence = category.CurrentSequence;
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? codeCursor = null;
        var referencePageSize = ReferencePageSize;

        for (var pageAttempt = 1; pageAttempt <= MaximumReferencePages; pageAttempt++)
        {
            var referenceFilter = $"MainItemCategory.Code = '{category.Code}'";
            if (codeCursor is not null)
                referenceFilter += $" and Code > '{codeCursor.Replace("'", "''", StringComparison.Ordinal)}'";
            var payload = BuildReferencePayload(configuration, referenceFilter, referencePageSize);
            U9CustomerQueryResult page;
            try
            {
                page = await client.QueryCustomerReferencesAsync(
                    configuration.BaseUrl, configuration.CustomerQueryPath, token, payload, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                referencePageSize = FallbackReferencePageSize;
                token = await AuthenticateAsync(configuration, clientSecret, cancellationToken);
                payload = BuildReferencePayload(configuration, referenceFilter, referencePageSize);
                page = await client.QueryCustomerReferencesAsync(
                    configuration.BaseUrl, configuration.CustomerQueryPath, token, payload, cancellationToken);
            }
            if (page.ResponseCode == ExpiredTokenResponseCode)
            {
                token = await AuthenticateAsync(configuration, clientSecret, cancellationToken);
                page = await client.QueryCustomerReferencesAsync(
                    configuration.BaseUrl, configuration.CustomerQueryPath, token, payload, cancellationToken);
            }
            if (page.ResponseCode != 0)
                throw new PdmRuleException($"U9C分类 {category.Code} 全量查询失败（ResCode={page.ResponseCode}）：{page.ResponseMessage ?? "未返回错误说明"}。");

            var pageCodes = page.Customers
                .Select(item => item.Code.Trim())
                .Where(code => code.Length > 0 && seenCodes.Add(code))
                .ToArray();
            if (page.RawCount > 0 && pageCodes.Length == 0)
                throw new PdmRuleException($"U9C分类 {category.Code} 分页未向后推进，已停止本次同步。");
            if (pageCodes.Length > 0)
            {
                var nextCursor = pageCodes.Max(StringComparer.OrdinalIgnoreCase)!;
                if (codeCursor is not null && string.Compare(nextCursor, codeCursor, StringComparison.OrdinalIgnoreCase) <= 0)
                    throw new PdmRuleException($"U9C分类 {category.Code} 料号游标未向后推进，已停止本次同步。");
                codeCursor = nextCursor;
            }

            discovered += pageCodes.Length;
            foreach (var code in pageCodes)
                if (TryParseSequence(code, category, out var sequence)) maximumSequence = Math.Max(maximumSequence, sequence);

            var existingByCode = (await materials.FindMaterialsByCodesAsync(pageCodes, cancellationToken))
                .ToDictionary(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase);
            foreach (var codeBatch in pageCodes.Chunk(DetailBatchSize))
            {
                var detailResult = await QueryDetailsAsync(
                    configuration, token, clientSecret, category.Code, codeBatch, cancellationToken);
                token = detailResult.Token;
                var details = detailResult.Items;
                var detailsByCode = details
                    .Where(item => !string.IsNullOrWhiteSpace(item.U9ItemCode))
                    .GroupBy(item => item.U9ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var code in codeBatch)
                {
                    if (!detailsByCode.TryGetValue(code, out var item))
                    {
                        skipped++;
                        continue;
                    }
                    if (!string.Equals(item.U9CategoryCode, category.Code, StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(item.U9UnitCode))
                    {
                        skipped++;
                        continue;
                    }
                    if (existingByCode.TryGetValue(code, out var existing)
                        && existing.MasterOwner != MaterialMasterOwner.U9C)
                    {
                        skipped++;
                        conflicts++;
                        continue;
                    }

                    var importedAt = timeProvider.GetUtcNow();
                    var weight = item.U9Weight is > 0 ? item.U9Weight : null;
                    var candidate = new PdmMaterial(
                        Guid.NewGuid(), code, (item.U9ItemName ?? code).Trim(), category.PdmKind!.Value,
                        ResolveSupplyMode(item.U9ItemFormAttribute, category.DefaultSupplyMode),
                        U9UnitCatalog.NormalizeInbound(item.U9UnitCode), Clean(item.U9Specification), Clean(item.U9Material),
                        Clean(item.U9Description), Clean(item.U9Brand), Clean(item.U9SurfaceTreatment),
                        weight, weight is null ? null : Clean(item.U9WeightUnitCode), null,
                        MaterialApprovalStatus.Approved, actor, importedAt, category.Code,
                        item.U9ItemId, code, MaterialSyncStatus.Succeeded,
                        actor, importedAt, actor, importedAt, 1, category.Code,
                        U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C,
                        LastU9SyncedAt: importedAt, PurchaseLink: Clean(item.U9PurchaseLink));
                    await materials.UpsertU9MaterialAsync(candidate, cancellationToken);
                    if (existing is null) created++;
                    else refreshed++;
                }
            }

            if (page.RawCount == 0 || page.RawCount < referencePageSize) break;
            if (pageAttempt == MaximumReferencePages)
                throw new PdmRuleException($"U9C分类 {category.Code} 超过最大安全分页范围，已停止本次同步。");
        }

        if (maximumSequence > category.CurrentSequence)
            await materials.AdvanceCategoryCounterAsync(category, maximumSequence, cancellationToken);
        await materials.MarkU9MaterialsObservedAsync(
            category.Code, seenCodes, categorySyncStartedAt, cancellationToken);
        var inactivated = await materials.ArchiveMissingU9MaterialsAsync(
            category.Code, categorySyncStartedAt, actor, timeProvider.GetUtcNow(), cancellationToken);
        return new(category.Code, category.Name, discovered, created, refreshed, skipped, maximumSequence, true, null,
            inactivated, conflicts);
    }

    private async Task<(IReadOnlyList<U9ItemReference> Items, string Token)> QueryDetailsAsync(
        U9MaterialIntegrationConfiguration configuration,
        string token,
        string clientSecret,
        string categoryCode,
        IReadOnlyList<string> materialCodes,
        CancellationToken cancellationToken)
    {
        var correlationId = $"pdm-u9-full-{categoryCode}-{Guid.NewGuid():N}";
        U9ItemQueryResult batch;
        try
        {
            batch = await QueryItemsWithTokenRefreshAsync(
                U9MaterialPayloadFactory.QueryPayload(materialCodes, correlationId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException && materialCodes.Count > 1)
        {
            batch = new U9ItemQueryResult(-1, exception.Message, []);
        }
        if (batch.ResponseCode == 0)
        {
            var returnedCodes = batch.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.U9ItemCode))
                .Select(item => item.U9ItemCode!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (materialCodes.All(returnedCodes.Contains)) return (batch.Items, token);
        }

        var items = new List<U9ItemReference>(materialCodes.Count);
        foreach (var code in materialCodes)
        {
            var detail = await QueryItemsWithTokenRefreshAsync(
                U9MaterialPayloadFactory.QueryPayload(code, correlationId));
            if (detail.ResponseCode != 0)
                throw new PdmRuleException($"U9C料品 {code} 查询失败（ResCode={detail.ResponseCode}）：{detail.ResponseMessage ?? "未返回错误说明"}。");
            var item = detail.Items.FirstOrDefault(value => string.Equals(value.U9ItemCode, code, StringComparison.OrdinalIgnoreCase));
            if (item is not null) items.Add(item);
        }
        return (items, token);

        async Task<U9ItemQueryResult> QueryItemsWithTokenRefreshAsync(string payload)
        {
            var result = await client.QueryItemsAsync(
                configuration.BaseUrl, configuration.ItemQueryPath, token, payload, cancellationToken);
            if (result.ResponseCode != ExpiredTokenResponseCode) return result;

            token = await AuthenticateAsync(configuration, clientSecret, cancellationToken);
            return await client.QueryItemsAsync(
                configuration.BaseUrl, configuration.ItemQueryPath, token, payload, cancellationToken);
        }
    }

    private async Task<string> AuthenticateAsync(
        U9MaterialIntegrationConfiguration configuration,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var authentication = await client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            clientSecret), cancellationToken);
        return authentication.Token;
    }

    private static string BuildReferencePayload(
        U9MaterialIntegrationConfiguration configuration,
        string referenceFilter,
        int pageSize) =>
        JsonSerializer.Serialize(new
        {
            ReferenceCode = "ItemMaster",
            ReferenceEntityFullName = "UFIDA.U9.CBO.SCM.Item.ItemMaster",
            ReferenceDefaultFilter = referenceFilter,
            Transclude = string.Empty,
            TargetOrgCode = configuration.OrganizationCode,
            PageIndex = 1,
            PageSize = pageSize,
            Filter = string.Empty,
            FilterObjectXML = string.Empty
        });

    private async Task<U9MaterialIntegrationConfiguration> RequireConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (!string.Equals(configuration.ItemQueryPath, U9MaterialContract.QueryPath, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C料品Query路径与已冻结的官方合同不一致。");
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("U9C应用密钥尚未配置。");
        return configuration;
    }

    private U9MaterialFullSyncRun Aggregate(
        U9MaterialFullSyncRun run,
        IReadOnlyList<U9MaterialFullSyncCategoryResult> results,
        bool completed)
    {
        var failed = results.Count(result => !result.Succeeded);
        var status = completed
            ? failed == 0
                ? U9MaterialFullSyncStatus.Succeeded
                : failed == results.Count
                    ? U9MaterialFullSyncStatus.Failed
                    : U9MaterialFullSyncStatus.PartiallySucceeded
            : U9MaterialFullSyncStatus.Running;
        return run with
        {
            Status = status,
            CategoryResults = results.ToArray(),
            CompletedCategoryCount = results.Count,
            DiscoveredCount = results.Sum(result => result.DiscoveredCount),
            CreatedCount = results.Sum(result => result.CreatedCount),
            RefreshedCount = results.Sum(result => result.RefreshedCount),
            SkippedCount = results.Sum(result => result.SkippedCount),
            FailedCategoryCount = failed,
            LastError = results.LastOrDefault(result => !result.Succeeded)?.Error,
            CompletedAt = completed ? timeProvider.GetUtcNow() : null
        };
    }

    private static bool TryParseSequence(string materialCode, MaterialCategory category, out long sequence)
    {
        sequence = 0;
        if (!materialCode.StartsWith(category.NumberPrefix, StringComparison.OrdinalIgnoreCase)
            || materialCode.Length != category.NumberPrefix.Length + category.SequenceLength) return false;
        var suffix = materialCode[category.NumberPrefix.Length..];
        return suffix.All(char.IsDigit) && long.TryParse(suffix, out sequence);
    }

    private static MaterialSupplyMode ResolveSupplyMode(int? u9ItemFormAttribute, MaterialSupplyMode fallback) =>
        u9ItemFormAttribute switch
        {
            9 => MaterialSupplyMode.Purchase,
            2 => MaterialSupplyMode.Manufacture,
            _ => fallback
        };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
