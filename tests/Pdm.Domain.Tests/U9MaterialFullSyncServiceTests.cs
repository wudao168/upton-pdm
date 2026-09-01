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

        var client = new RecordingClient();
        var service = new U9MaterialFullSyncService(materials, repository, new TestProtector(), client, timeProvider);

        var run = await service.SynchronizeAsync("system:test", "Test", default);

        Assert.Equal(U9MaterialFullSyncStatus.Succeeded, run.Status);
        Assert.DoesNotContain("0101", client.QueriedCategories);
        Assert.Contains("0102", client.QueriedCategories);
        Assert.Equal(2, run.DiscoveredCount);
        Assert.Equal(1, run.CreatedCount);
        Assert.Equal(1, run.SkippedCount);
        Assert.Equal(0, client.PostCallCount);
        Assert.Equal(MaterialMasterOwner.U9C, (await materials.FindMaterialByCodeAsync("01020000150", default))?.MasterOwner);
        Assert.Equal(MaterialMasterOwner.Pdm, (await materials.FindMaterialByCodeAsync("01020000151", default))?.MasterOwner);
        Assert.Equal(151, (await materials.FindCategoryAsync("0102", default))?.CurrentSequence);
        Assert.Equal(run.Id, (await service.GetLatestRunAsync(default))?.Id);
    }

    private sealed class RecordingClient : IU9OpenApiClient
    {
        public HashSet<string> QueriedCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PostCallCount { get; private set; }

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            PostCallCount++;
            throw new InvalidOperationException("Full synchronization must remain read-only.");
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var items = document.RootElement.EnumerateArray()
                .Select(element => element.GetProperty("ItemMaster").GetProperty("Code").GetString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
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
            QueriedCategories.Add(categoryCode);
            IReadOnlyList<U9CustomerReference> rows = categoryCode == "0102"
                ? [new("01020000150", "U9标准件150"), new("01020000151", "U9标准件151")]
                : [];
            return Task.FromResult(new U9CustomerQueryResult(0, null, rows, rows.Count));
        }
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
