using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class U9MaterialIntegrationServiceTests
{
    [Fact]
    public async Task TestConnection_DecryptsSecretButDoesNotReturnToken()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret-value",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow()), default);
        var client = new RecordingClient();
        var service = new U9MaterialIntegrationService(materials, repository, new TestProtector(), client, timeProvider);

        var result = await service.TestConnectionAsync("admin", UserRole.Administrator, default);

        Assert.Equal("secret-value", client.Request?.ClientSecret);
        Assert.Equal("01", result.EnterpriseCode);
        Assert.Equal("7", result.OrganizationCode);
        Assert.DoesNotContain("token", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnection_RequiresSavedSecret()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        var service = new U9MaterialIntegrationService(materials, repository, new TestProtector(), new RecordingClient(), timeProvider);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.TestConnectionAsync("admin", UserRole.Administrator, default));

        Assert.Contains("应用密钥", exception.Message);
    }

    [Fact]
    public async Task QueryByCode_IsReadOnlyAndWorksWhileWritesAreDisabled()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret-value",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow()), default);
        var client = new RecordingClient
        {
            QueryResult = new U9ItemQueryResult(0, null, [new("u9-1", "01010000001")])
        };
        var service = new U9MaterialIntegrationService(materials, repository, new TestProtector(), client, timeProvider);

        var result = await service.QueryByCodeAsync("01010000001", "admin", UserRole.Administrator, default);

        Assert.Single(result.Items);
        Assert.Equal(1, client.QueryCallCount);
        Assert.Equal(0, client.PostCallCount);
    }

    [Fact]
    public async Task PreviewSample_RejectsMoreThanTenPerCategoryBeforeCallingU9()
    {
        var fixture = await CreateSampleFixtureAsync();

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.PreviewSampleAsync(
            ["0101"], 11, "admin", UserRole.Administrator, default));

        Assert.Contains("最多只能同步10个", exception.Message);
        Assert.Null(fixture.Client.Request);
    }

    [Fact]
    public async Task ImportSample_IsIdempotentAndNeverCallsU9WriteEndpoint()
    {
        var fixture = await CreateSampleFixtureAsync();
        fixture.Client.CustomerResult = new U9CustomerQueryResult(0, null, [new("01010000001", "光电传感器")], 1);
        fixture.Client.QueryResultsByCode["01010000001"] = new U9ItemQueryResult(0, null,
            [new("u9-item-1", "01010000001", "光电传感器", "M18 PNP", "0101", "电气外购件", "001", 9)]);

        var first = await fixture.Service.ImportSampleAsync(["0101"], 10, "admin", UserRole.Administrator, default);
        var second = await fixture.Service.ImportSampleAsync(["0101"], 10, "admin", UserRole.Administrator, default);

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(1, second.RefreshedCount);
        Assert.Single(await fixture.Materials.ListMaterialsAsync(null, "0101", false, 100, default));
        Assert.Equal(0, fixture.Client.PostCallCount);
        using var referencePayload = System.Text.Json.JsonDocument.Parse(fixture.Client.LastReferencePayload);
        Assert.Equal("MainItemCategory.Code = '0101'", referencePayload.RootElement.GetProperty("ReferenceDefaultFilter").GetString());
    }

    [Fact]
    public async Task ImportSample_DoesNotOverwriteSameCodeOwnedByPdm()
    {
        var fixture = await CreateSampleFixtureAsync();
        var category = (await fixture.Materials.FindCategoryAsync("0101", default))!;
        var now = fixture.TimeProvider.GetUtcNow();
        var local = new PdmMaterial(
            Guid.NewGuid(), "01010000001", "PDM自建名称", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "LOCAL", null, null, null, null, null, null, null, MaterialApprovalStatus.Draft, null, null, "0101", null, null,
            MaterialSyncStatus.NotQueued, "admin", now, "admin", now, 1, "0101");
        await fixture.Materials.CreateMaterialAsync(local, category, default);
        fixture.Client.CustomerResult = new U9CustomerQueryResult(0, null, [new("01010000001", "U9名称")], 1);
        fixture.Client.QueryResultsByCode["01010000001"] = new U9ItemQueryResult(0, null,
            [new("u9-item-1", "01010000001", "U9名称", "U9-SPEC", "0101", "电气外购件", "001", 9)]);

        var result = await fixture.Service.ImportSampleAsync(["0101"], 10, "admin", UserRole.Administrator, default);

        Assert.Equal(1, result.SkippedCount);
        var saved = await fixture.Materials.FindMaterialByCodeAsync("01010000001", default);
        Assert.Equal("PDM自建名称", saved?.Name);
        Assert.Equal(MaterialMasterOwner.Pdm, saved?.MasterOwner);
    }

    [Fact]
    public async Task ExecuteTask_QueriesByCodeThenCreatesAndCompletesTask()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: true);
        fixture.Client.QueryResult = new U9ItemQueryResult(0, null, []);
        fixture.Client.BatchResult = new U9BusinessBatchResult(0, null,
            [new U9BusinessRowResult(true, null, "1001", fixture.Material.MaterialCode)]);

        var result = await fixture.Service.ExecuteTaskAsync(
            fixture.Task.Id, "admin", UserRole.Administrator, default);

        Assert.True(result.Created);
        Assert.False(result.AlreadyExisted);
        Assert.Equal(MaterialSyncStatus.Succeeded, result.Task.Status);
        Assert.Equal("1001", result.Material.U9ItemId);
        Assert.True(result.Material.U9SyncConfirmed);
        Assert.Equal(1, fixture.Client.QueryCallCount);
        Assert.Equal(1, fixture.Client.PostCallCount);
        Assert.Contains("\"MainItemCategory\"", fixture.Client.LastCreatePayload);
        Assert.Contains("\"Code\": \"001\"", fixture.Client.LastUomPayload);
        Assert.Contains("\"Code\": \"001\"", fixture.Client.LastCreatePayload);
        Assert.Contains("\"PubDescSeg1\": \"https://shop.example.test/item/sensor\"", fixture.Client.LastCreatePayload);
    }

    [Fact]
    public async Task ExecuteTask_WhenCodeAlreadyExists_BlocksAutomaticBinding()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: true);
        fixture.Client.QueryResult = new U9ItemQueryResult(0, null,
            [new U9ItemReference("existing-1001", fixture.Material.MaterialCode)]);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ExecuteTaskAsync(
            fixture.Task.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("不会自动绑定", exception.Message);
        Assert.Equal(0, fixture.Client.PostCallCount);
        var material = await fixture.Materials.FindMaterialAsync(fixture.Material.Id, default);
        Assert.False(material?.U9SyncConfirmed);
    }

    [Fact]
    public async Task ExecuteTask_WhenUomDoesNotExist_BlocksWrite()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: true);
        fixture.Client.UomResult = new U9UomQueryResult(0, null, []);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ExecuteTaskAsync(
            fixture.Task.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("PDM计量单位编码 001 在U9C中不存在", exception.Message);
        Assert.Equal(0, fixture.Client.QueryCallCount);
        Assert.Equal(0, fixture.Client.PostCallCount);
    }

    [Fact]
    public async Task ExecuteTask_WhenWriteIsDisabled_DoesNotCallBusinessEndpoints()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: false);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ExecuteTaskAsync(
            fixture.Task.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("尚未启用", exception.Message);
        Assert.Equal(0, fixture.Client.QueryCallCount);
        Assert.Equal(0, fixture.Client.PostCallCount);
    }

    [Fact]
    public async Task ExecuteTask_WhenCreateTransportIsUncertain_MarksNeedsReview()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: true);
        fixture.Client.QueryResult = new U9ItemQueryResult(0, null, []);
        fixture.Client.ThrowOnPost = true;

        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ExecuteTaskAsync(
            fixture.Task.Id, "admin", UserRole.Administrator, default));

        var saved = await fixture.Materials.FindSyncTaskAsync(fixture.Task.Id, default);
        Assert.Equal(MaterialSyncStatus.NeedsReview, saved?.Status);
        Assert.Contains("超时", saved?.LastError);
    }

    [Fact]
    public async Task ExecuteTask_ModifyRequiresExistingU9ItemAndUsesOfficialModifyPath()
    {
        var fixture = await CreateApprovedTaskAsync(writeEnabled: true);
        await fixture.Materials.BeginSyncTaskAsync(fixture.Task.Id, fixture.TimeProvider.GetUtcNow(), default);
        var synced = await fixture.Materials.CompleteSyncTaskAsync(
            fixture.Task.Id, "u9-1001", fixture.Material.MaterialCode, "{}",
            new AuditEntry(Guid.NewGuid(), fixture.TimeProvider.GetUtcNow(), "admin", "test", nameof(PdmMaterial), fixture.Material.Id.ToString(), "test"), default);
        var materialService = new MaterialService(fixture.Materials, fixture.Repository, new TestProtector(), fixture.Client, fixture.TimeProvider);
        var change = await materialService.ChangeApprovedAsync(fixture.Material.Id, new(
            fixture.Material.MaterialCode, "改名后的传感器", fixture.Material.Kind, fixture.Material.SupplyMode, fixture.Material.UnitCode,
            fixture.Material.Specification, fixture.Material.Material, fixture.Material.Remark, fixture.Material.Brand, fixture.Material.SurfaceTreatment,
            fixture.Material.Weight, fixture.Material.WeightUnit, synced.Material.RowVersion, "0101", "https://shop.example.test/item/sensor-v2"),
            "admin", UserRole.Administrator, default);
        fixture.Client.QueryResult = new U9ItemQueryResult(0, null, [new("u9-1001", fixture.Material.MaterialCode)]);
        fixture.Client.BatchResult = new U9BusinessBatchResult(0, null, []);

        var result = await fixture.Service.ExecuteTaskAsync(change.Task.Id, "admin", UserRole.Administrator, default);

        Assert.True(result.Updated);
        Assert.Equal(U9MaterialContract.ModifyPath, fixture.Client.LastPostPath);
        Assert.Contains("\"Attributes\"", fixture.Client.LastCreatePayload);
        Assert.Contains("\"AttributeName\": \"DescFlexField.PubDescSeg1\"", fixture.Client.LastCreatePayload);
        Assert.Contains("\"AttributeValue\": \"https://shop.example.test/item/sensor-v2\"", fixture.Client.LastCreatePayload);
    }

    private static async Task<ExecutionFixture> CreateApprovedTaskAsync(bool writeEnabled)
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret-value",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, writeEnabled, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        var client = new RecordingClient();
        var materialService = new MaterialService(materials, repository, new TestProtector(), client, timeProvider);
        var material = await materialService.CreateAsync(new(
            $"0101{Guid.NewGuid():N}", "光电传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18 PNP", null, null, "SICK", null, null, null, PurchaseLink: "https://shop.example.test/item/sensor"), "admin", UserRole.Administrator, default);
        var approved = await materialService.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        client.ResetCalls();
        var service = new U9MaterialIntegrationService(materials, repository, new TestProtector(), client, timeProvider);
        return new ExecutionFixture(service, repository, materials, client, timeProvider, approved.Material, approved.Task);
    }

    private static async Task<ExecutionFixture> CreateSampleFixtureAsync()
    {
        var timeProvider = TimeProvider.System;
        var repository = new InMemoryPdmRepository(timeProvider);
        var materials = new InMemoryMaterialRepository(timeProvider);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:secret-value",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);
        var client = new RecordingClient();
        var service = new U9MaterialIntegrationService(materials, repository, new TestProtector(), client, timeProvider);
        return new ExecutionFixture(service, repository, materials, client, timeProvider, null!, null!);
    }

    private sealed record ExecutionFixture(
        U9MaterialIntegrationService Service,
        InMemoryPdmRepository Repository,
        InMemoryMaterialRepository Materials,
        RecordingClient Client,
        TimeProvider TimeProvider,
        PdmMaterial Material,
        MaterialSyncTask Task);

    private sealed class RecordingClient : IU9OpenApiClient
    {
        public U9AuthenticationRequest? Request { get; private set; }
        public U9ItemQueryResult QueryResult { get; set; } = new(0, null, []);
        public U9UomQueryResult UomResult { get; set; } = new(0, null, [new("uom-pcs", "001")]);
        public U9BusinessBatchResult BatchResult { get; set; } = new(0, null, []);
        public U9CustomerQueryResult CustomerResult { get; set; } = new(0, null, [], 0);
        public Dictionary<string, U9ItemQueryResult> QueryResultsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int QueryCallCount { get; private set; }
        public int PostCallCount { get; private set; }
        public bool ThrowOnPost { get; set; }
        public string LastCreatePayload { get; private set; } = string.Empty;
        public string LastUomPayload { get; private set; } = string.Empty;
        public string LastPostPath { get; private set; } = string.Empty;
        public string LastReferencePayload { get; private set; } = string.Empty;

        public void ResetCalls()
        {
            QueryCallCount = 0;
            PostCallCount = 0;
        }

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new U9AuthenticationResult("token-123"));
        }

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            PostCallCount++;
            LastCreatePayload = payloadJson;
            LastPostPath = path;
            if (ThrowOnPost) throw new PdmRuleException("U9C业务请求超时。");
            return Task.FromResult(BatchResult);
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            QueryCallCount++;
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                && document.RootElement.GetArrayLength() > 0
                && document.RootElement[0].TryGetProperty("ItemMaster", out var item)
                && item.TryGetProperty("Code", out var code)
                && QueryResultsByCode.TryGetValue(code.GetString() ?? string.Empty, out var result)) return Task.FromResult(result);
            return Task.FromResult(QueryResult);
        }

        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            LastUomPayload = payloadJson;
            return Task.FromResult(UomResult);
        }

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
            string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            LastReferencePayload = payloadJson;
            return Task.FromResult(CustomerResult);
        }
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
