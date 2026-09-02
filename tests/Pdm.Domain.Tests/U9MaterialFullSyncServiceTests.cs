using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class U9MaterialFullSyncServiceTests
{
    [Fact]
    public async Task SynchronizeAsync_UsesDynamicCreatableCategoriesAndOnlyReadsU9()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);

        var categories = await materials.ListCategoriesAsync(true, default);
        var excluded = categories.Single(category => category.Code == "0101");
        await materials.SaveCategoryAsync(excluded with { AllowCreate = false }, excluded.RowVersion, default);
        var standardCategory = categories.Single(category => category.Code == "0102");
        var now = timeProvider.GetUtcNow();
        await materials.CreateMaterialAsync(new(
            Guid.NewGuid(), "01020000151", "PLM自有标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PLM-151", null, null, "UPTON", null, null, null, null, MaterialApprovalStatus.Draft,
            null, null, "0102", null, null, MaterialSyncStatus.NotQueued,
            "admin", now, "admin", now, 1, "0102"), standardCategory, default);
        var previousSync = now.AddDays(-1);
        await materials.UpsertU9MaterialAsync(new(
            Guid.NewGuid(), "01020000149", "U9旧料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "OLD-149", null, null, "U9品牌", null, null, null, null, MaterialApprovalStatus.Approved,
            "u9-sync", previousSync, "0102", "u9-149", "01020000149", MaterialSyncStatus.Succeeded,
            "u9-sync", previousSync, "u9-sync", previousSync, 1, "0102",
            U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C,
            LastU9SyncedAt: previousSync), default);

        var client = new RecordingClient();
        var service = new U9MaterialFullSyncService(materials, repository, new TestProtector(), client, timeProvider);

        var run = await service.SynchronizeAsync("system:test", "Test", default);

        Assert.Equal(U9MaterialFullSyncStatus.Succeeded, run.Status);
        Assert.DoesNotContain("0101", client.QueriedCategories);
        Assert.Contains("0102", client.QueriedCategories);
        Assert.Equal(client.QueriedCategories.Count, client.AuthenticationCallCount);
        Assert.Equal([1, 1], client.QueriedPages["0102"]);
        Assert.DoesNotContain("Code >", client.QueriedFilters["0102"][0]);
        Assert.Contains("Code > '01020000151'", client.QueriedFilters["0102"][1]);
        Assert.Equal(2, run.DiscoveredCount);
        Assert.Equal(1, run.CreatedCount);
        Assert.Equal(1, run.SkippedCount);
        Assert.Equal(1, run.CategoryResults.Single(result => result.CategoryCode == "0102").ConflictCount);
        Assert.Equal(1, run.CategoryResults.Single(result => result.CategoryCode == "0102").InactivatedCount);
        Assert.Equal(0, client.PostCallCount);
        Assert.Equal(MaterialMasterOwner.U9C, (await materials.FindMaterialByCodeAsync("01020000150", default))?.MasterOwner);
        Assert.Equal(MaterialMasterOwner.Pdm, (await materials.FindMaterialByCodeAsync("01020000151", default))?.MasterOwner);
        Assert.True((await materials.FindMaterialByCodeAsync("01020000149", default))?.IsArchived);
        var imported = await materials.FindMaterialByCodeAsync("01020000150", default);
        Assert.Equal("SPEC-01020000150", imported?.Specification);
        Assert.Equal("U9品牌", imported?.Brand);
        Assert.Equal(151, (await materials.FindCategoryAsync("0102", default))?.CurrentSequence);
        Assert.Equal(run.Id, (await service.GetLatestRunAsync(default))?.Id);
    }

    [Fact]
    public void NormalizeInbound_PreservesU9OwnedUnitCodesWithoutRelaxingManualCodes()
    {
        Assert.Equal("L007", U9UnitCatalog.NormalizeInbound(" L007 "));
        Assert.Equal("EA", U9UnitCatalog.NormalizeInbound("EA"));
        Assert.Throws<PdmRuleException>(() => U9UnitCatalog.Normalize("EA"));
        Assert.Throws<PdmRuleException>(() => U9UnitCatalog.Normalize("L007"));
    }

    [Fact]
    public async Task SynchronizeAsync_ReauthenticatesAndRetriesExpiredDetailToken()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        foreach (var category in await materials.ListCategoriesAsync(true, default))
        {
            if (category.Code != "0102")
                await materials.SaveCategoryAsync(category with { AllowCreate = false }, category.RowVersion, default);
        }

        var client = new RecordingClient(expireFirstDetailToken: true);
        var service = new U9MaterialFullSyncService(materials, repository, new TestProtector(), client, timeProvider);

        var run = await service.SynchronizeAsync("system:test", "Test", default);

        Assert.Equal(U9MaterialFullSyncStatus.Succeeded, run.Status);
        Assert.Equal(2, client.AuthenticationCallCount);
        Assert.Equal(1, client.ExpiredDetailResponseCount);
        Assert.Equal(2, run.CreatedCount);
    }

    [Fact]
    public async Task SynchronizeAsync_RetriesFailedReferenceQueryWithSmallerCursorPage()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        foreach (var category in await materials.ListCategoriesAsync(true, default))
        {
            if (category.Code != "0102")
                await materials.SaveCategoryAsync(category with { AllowCreate = false }, category.RowVersion, default);
        }

        var client = new RecordingClient(failFirstReferenceRequest: true);
        var service = new U9MaterialFullSyncService(materials, repository, new TestProtector(), client, timeProvider);

        var run = await service.SynchronizeAsync("system:test", "Test", default);

        Assert.Equal(U9MaterialFullSyncStatus.Succeeded, run.Status);
        Assert.Equal(2, client.AuthenticationCallCount);
        Assert.Equal([1000, 200, 200], client.QueriedPageSizes["0102"]);
        Assert.Equal(2, run.CreatedCount);
    }

    [Fact]
    public async Task SynchronizeAsync_DoesNotArchiveObservedMaterialWhenDetailIsTemporarilyMissing()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        foreach (var category in await materials.ListCategoriesAsync(true, default))
        {
            if (category.Code != "0102")
                await materials.SaveCategoryAsync(category with { AllowCreate = false }, category.RowVersion, default);
        }

        var previousSync = timeProvider.GetUtcNow().AddDays(-1);
        await materials.UpsertU9MaterialAsync(new(
            Guid.NewGuid(), "01020000152", "U9暂缺明细料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "OLD-152", null, null, "U9品牌", null, null, null, null, MaterialApprovalStatus.Approved,
            "u9-sync", previousSync, "0102", "u9-152", "01020000152", MaterialSyncStatus.Succeeded,
            "u9-sync", previousSync, "u9-sync", previousSync, 1, "0102",
            U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C,
            LastU9SyncedAt: previousSync), default);

        var service = new U9MaterialFullSyncService(
            materials, repository, new TestProtector(), new RecordingClient(missingDetailCode: "01020000152"), timeProvider);

        var run = await service.SynchronizeAsync("system:test", "Test", default);

        Assert.Equal(U9MaterialFullSyncStatus.Succeeded, run.Status);
        Assert.Equal(1, run.DiscoveredCount);
        Assert.Equal(1, run.SkippedCount);
        Assert.Equal(0, run.CategoryResults.Single().InactivatedCount);
        var observed = await materials.FindMaterialByCodeAsync("01020000152", default);
        Assert.NotNull(observed);
        Assert.False(observed.IsArchived);
        Assert.True(observed.LastU9SyncedAt > previousSync);
    }

    private sealed class RecordingClient : IU9OpenApiClient
    {
        private readonly bool expireFirstDetailToken;
        private readonly bool failFirstReferenceRequest;
        private readonly string? missingDetailCode;
        private bool detailTokenExpired;
        private bool referenceRequestFailed;

        public RecordingClient(
            bool expireFirstDetailToken = false,
            bool failFirstReferenceRequest = false,
            string? missingDetailCode = null)
        {
            this.expireFirstDetailToken = expireFirstDetailToken;
            this.failFirstReferenceRequest = failFirstReferenceRequest;
            this.missingDetailCode = missingDetailCode;
        }

        public HashSet<string> QueriedCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<int>> QueriedPages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> QueriedFilters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<int>> QueriedPageSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int AuthenticationCallCount { get; private set; }
        public int PostCallCount { get; private set; }
        public int ExpiredDetailResponseCount { get; private set; }

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
        {
            AuthenticationCallCount++;
            return Task.FromResult(new U9AuthenticationResult($"token-{AuthenticationCallCount}"));
        }

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            PostCallCount++;
            throw new InvalidOperationException("Full synchronization must remain read-only.");
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            if (expireFirstDetailToken && !detailTokenExpired)
            {
                detailTokenExpired = true;
                ExpiredDetailResponseCount++;
                return Task.FromResult(new U9ItemQueryResult(402, "token已过期", []));
            }

            using var document = JsonDocument.Parse(payloadJson);
            var items = document.RootElement.EnumerateArray()
                .Select(element => element.GetProperty("ItemMaster").GetProperty("Code").GetString())
                .Where(code => !string.IsNullOrWhiteSpace(code)
                    && !string.Equals(code, missingDetailCode, StringComparison.OrdinalIgnoreCase))
                .Select(code => new U9ItemReference(
                    $"u9-{code}", code, $"U9-{code}", $"SPEC-{code}", "0102", "机械外购件", "001",
                    9, "U9品牌", null, null, null, null, null, null))
                .ToArray();
            return Task.FromResult(new U9ItemQueryResult(0, null, items));
        }

        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9UomQueryResult(0, null, []));

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
            string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var filter = document.RootElement.GetProperty("ReferenceDefaultFilter").GetString() ?? string.Empty;
            var categoryCode = filter.Split('\'').Skip(1).FirstOrDefault() ?? string.Empty;
            var pageIndex = document.RootElement.GetProperty("PageIndex").GetInt32();
            var pageSize = document.RootElement.GetProperty("PageSize").GetInt32();
            QueriedCategories.Add(categoryCode);
            if (!QueriedPages.TryGetValue(categoryCode, out var pages))
            {
                pages = [];
                QueriedPages[categoryCode] = pages;
            }
            pages.Add(pageIndex);
            if (!QueriedFilters.TryGetValue(categoryCode, out var filters))
            {
                filters = [];
                QueriedFilters[categoryCode] = filters;
            }
            filters.Add(filter);
            if (!QueriedPageSizes.TryGetValue(categoryCode, out var pageSizes))
            {
                pageSizes = [];
                QueriedPageSizes[categoryCode] = pageSizes;
            }
            pageSizes.Add(pageSize);
            if (failFirstReferenceRequest && !referenceRequestFailed)
            {
                referenceRequestFailed = true;
                throw new PdmRuleException("U9C客户参照查询请求超时。");
            }
            IReadOnlyList<U9CustomerReference> rows = categoryCode == "0102" && !filter.Contains("Code >", StringComparison.Ordinal)
                ? missingDetailCode is null
                    ? [new("01020000150", "U9标准件150"), new("01020000151", "U9标准件151")]
                    : [new(missingDetailCode, "U9暂缺明细料品")]
                : [];
            return Task.FromResult(new U9CustomerQueryResult(0, null, rows, rows.Count > 0 ? pageSize : 0));
        }
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
