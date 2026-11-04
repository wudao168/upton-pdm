using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class MaterialApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileU9PreservesPlmBaselineAndOptionallyQueuesApprovedCorrection(bool synchronize)
    {
        using var scope = factory.Services.CreateScope();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var erp = (TestU9OpenApiClient)scope.ServiceProvider.GetRequiredService<IU9OpenApiClient>();
        var protector = scope.ServiceProvider.GetRequiredService<IU9SecretProtector>();
        var config = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(config with { ClientSecretCiphertext = protector.Protect("test-secret") }, default);
        var at = DateTimeOffset.UtcNow;
        var code = "0102-RECONCILE-" + Guid.NewGuid().ToString("N");
        var material = new PdmMaterial(Guid.NewGuid(), code, "PLM保留名称", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PLM型号", null, null, "TEST", null, null, null, null, MaterialApprovalStatus.Approved, "admin", at,
            "0102", "u9-" + code, code, MaterialSyncStatus.Succeeded, "admin", at, "admin", at, 1, "0102",
            IsArchived: true, U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C, MasterOwner: MaterialMasterOwner.U9C);
        await materials.CreateMaterialAsync(material, (await materials.FindCategoryAsync("0102", default))!, default);
        var before = erp.QueryResult;
        var postCount = erp.PostCallCount;
        erp.QueryResult = new(0, null, [new(material.U9ItemId, code, "ERP名称", "ERP型号", "0102", "机械外购件", "001", 9, "TEST")
            { CreationAttributes = new Dictionary<string, string?> { ["Effective.IsEffective"] = "true" } }]);
        try
        {
            var response = await client.PostAsync($"/api/materials/{material.Id}/reconcile-u9?expectedRowVersion=1&synchronize={synchronize.ToString().ToLowerInvariant()}", null);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var saved = json.RootElement.GetProperty("material");
            Assert.Equal("PLM保留名称", saved.GetProperty("name").GetString());
            Assert.Equal("PLM型号", saved.GetProperty("specification").GetString());
            Assert.True(saved.GetProperty("isArchived").GetBoolean());
            Assert.Equal("U9C", saved.GetProperty("sourceSystem").GetString());
            Assert.Equal("Pdm", saved.GetProperty("masterOwner").GetString());
            if (synchronize)
            {
                var task = json.RootElement.GetProperty("task");
                Assert.Equal("Update", task.GetProperty("operation").GetString());
                Assert.Single(await materials.ListSyncBatchItemsForTaskAsync(task.GetProperty("id").GetGuid(), default));
                Assert.Contains("Effective.IsEffective", task.GetProperty("payloadJson").GetString());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("task").ValueKind);
                Assert.Equal(postCount, erp.PostCallCount);
            }
        }
        finally { erp.QueryResult = before; }
    }

    [Fact]
    public async Task OrdinaryEngineerCannotReconcileOrSynchronizeMaterialMaster()
    {
        using var engineer = factory.CreateClient();
        engineer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        var response = await engineer.PostAsync($"/api/materials/{Guid.NewGuid()}/reconcile-u9?expectedRowVersion=1&synchronize=true", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
