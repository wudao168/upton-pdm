using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class MaterialSyncBatchServiceTests
{
    [Fact]
    public async Task CreateAsync_OrdersItemsByMaterialCodeAndPersistsProgress()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        var materialService = new MaterialService(materials, repository, new TestProtector(), new NoOpClient(), timeProvider);
        var firstDraft = await materialService.CreateAsync(new(
            null, "标准件一", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "ONE", null, null, null, "UPTON", null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var first = await materialService.ApproveAsync(firstDraft.Id, firstDraft.RowVersion, "admin", UserRole.Administrator, default);
        var secondDraft = await materialService.CreateAsync(new(
            null, "标准件二", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "TWO", null, null, null, "UPTON", null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var second = await materialService.ApproveAsync(secondDraft.Id, secondDraft.RowVersion, "admin", UserRole.Administrator, default);

        var service = new MaterialSyncBatchService(materials, repository, timeProvider);
        var batch = await service.CreateAsync(
            [second.Task.Id, first.Task.Id], "admin", UserRole.Administrator, default);

        Assert.Equal([first.Task.Id, second.Task.Id], batch.Items.Select(item => item.TaskId));
        var firstClaim = await materials.ClaimNextSyncBatchItemAsync(
            timeProvider.GetUtcNow(), timeProvider.GetUtcNow().AddMinutes(15), default);
        Assert.Equal(first.Task.Id, firstClaim?.Item.TaskId);
        var progressed = await materials.CompleteSyncBatchItemAsync(
            batch.Id, firstClaim!.Item.Id, MaterialSyncBatchItemStatus.Succeeded, "完成", timeProvider.GetUtcNow(), default);
        Assert.Equal(1, progressed.CompletedCount);
        Assert.Equal(MaterialSyncBatchStatus.Running, progressed.Status);
    }

    private sealed class NoOpClient : IU9OpenApiClient
    {
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));
        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9BusinessBatchResult(0, null, []));
        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9ItemQueryResult(0, null, []));
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9UomQueryResult(0, null, []));
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9CustomerQueryResult(0, null, [], 0));
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
