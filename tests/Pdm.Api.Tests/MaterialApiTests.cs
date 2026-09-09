using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api.Tests;

public sealed class MaterialApiTests : IClassFixture<PdmApiFactory>
{
    private readonly HttpClient client;
    private readonly PdmApiFactory factory;

    public MaterialApiTests(PdmApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));
    }

    [Fact]
    public async Task U9IntegrationApi_SavesSecretAndTestsOAuthWithoutReturningToken()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "test-secret",
            itemCreatePath = "/webapi/ItemMaster/Create",
            itemQueryPath = "/webapi/ItemMaster/Query",
            writeEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);

        var testResponse = await client.PostAsync("/api/u9-material-integration/test", null);
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);
        using var result = JsonDocument.Parse(await testResponse.Content.ReadAsStringAsync());
        Assert.Equal("01", result.RootElement.GetProperty("enterpriseCode").GetString());
        Assert.False(result.RootElement.TryGetProperty("token", out _));
        Assert.Equal("test-secret", factory.Services.GetRequiredService<TestU9OpenApiClient>().LastRequest?.ClientSecret);
    }

    [Fact]
    public async Task U9MaterialFullSyncStatusApi_ReturnsDynamicCreatableCategoriesAndSchedule()
    {
        var response = await client.GetAsync("/api/u9-material-full-sync/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("02:00", result.RootElement.GetProperty("scheduleTime").GetString());
        Assert.Equal(30, result.RootElement.GetProperty("checkIntervalMinutes").GetInt32());
        var categories = result.RootElement.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Contains(categories, category => category.GetProperty("code").GetString() == "0102");
        Assert.DoesNotContain(categories, category => category.GetProperty("code").GetString() == "01");
    }

    [Fact]
    public async Task U9MaterialQueryApi_ReturnsSpecificationForCodeAndSpecificationValidation()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "query-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            writeEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.QueryResult = new U9ItemQueryResult(0, null,
            [new("u9-2001", "01020000002", "气缸", "CDQ2B32-100")]);

        var queryResponse = await client.GetAsync("/api/u9-material-query/01020000002");

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        using var result = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        var item = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("01020000002", item.GetProperty("u9ItemCode").GetString());
        Assert.Equal("CDQ2B32-100", item.GetProperty("u9Specification").GetString());
    }

    [Fact]
    public async Task U9BomQueryApi_UsesSavedOrganizationAndReturnsReadOnlyPreview()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "bom-query-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            writeEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.BomResult = new U9BomQueryResult(0, null,
        [
            new("1001", "03010000001", "测试设备", "V1", "7", "昆山工厂", 0, 1, "001", "个",
                null, null, 2, 0, 1, "ASM-001", "测试BOM", null, false, 0, 0,
                [new(10, "2001", "01020000057", "阀岛", null, 2m, "001", "个", 1m,
                    0, true, null, null, null, null, 0, 0, false, false)])
        ]);

        var response = await client.PostAsJsonAsync("/api/u9-boms/query", new
        {
            itemCode = "03010000001",
            bomVersionCode = "V1",
            lot = 1,
            productUomCode = "001"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(U9BomContract.QueryPath, result.RootElement.GetProperty("queryPath").GetString());
        Assert.False(result.RootElement.GetProperty("requestPreview").GetString()!.Contains("token", StringComparison.OrdinalIgnoreCase));
        var bom = Assert.Single(result.RootElement.GetProperty("result").GetProperty("boms").EnumerateArray());
        Assert.Equal("03010000001", bom.GetProperty("itemCode").GetString());
        Assert.Equal("01020000057", Assert.Single(bom.GetProperty("components").EnumerateArray()).GetProperty("itemCode").GetString());
        using var payload = JsonDocument.Parse(fake.LastBomPayload);
        var request = Assert.Single(payload.RootElement.EnumerateArray());
        Assert.Equal("7", request.GetProperty("Org").GetProperty("Code").GetString());
        Assert.Equal("03010000001", request.GetProperty("ItemMaster").GetProperty("Code").GetString());
        Assert.Equal("V1", request.GetProperty("BOMVersionCode").GetString());
    }

    [Fact]
    public async Task U9BomWriteApi_UsesFixedA1AndOnlyAppendsDuringModify()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "bom-write-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            writeEnabled = true
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.BomResults.Clear();
        fake.BomResult = new(0, null, []);
        fake.BusinessResult = new(0, null, [new(true, null, "bom-1", "TEST-BOM")]);

        object Command(int operation, params object[] components) => new
        {
            operation,
            itemCode = "02010003168",
            bomVersionCode = "A1",
            productUomCode = "001",
            lot = 1,
            explain = "PLM受控接口测试",
            components
        };

        object Component(int sequence, string itemCode, decimal usageQty) =>
            new { sequence, itemCode, usageQty, issueUomCode = "001", parentQty = 1m };

        U9BomReference Existing(string? marker, IReadOnlyList<U9BomComponentReference> components, string? explain = null) => new(
            "bom-1", "02010003168", "测试母件", "A1", "7", "测试组织", 0, 1, "001", "个",
            null, null, 0, 0, 0, null, explain ?? "PLM受控接口测试", null, false, 0, 0,
            components, marker);

        U9BomComponentReference ExistingComponent(int sequence, string itemCode, decimal usageQty) =>
            new(sequence, $"id-{sequence}", itemCode, "测试子件", null, usageQty, "001", "个", 1m,
                0, true, null, null, null, null, 0, 0, false, false)
            { UsageQtyType = 1, IsSpecialUseItem = true, IsIssueOrgFixed = true, IssueOrgCode = "7" };

        async Task<JsonDocument> PreviewAsync(object command)
        {
            var response = await client.PostAsJsonAsync("/api/u9-boms/write-preview", command);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            return JsonDocument.Parse(body);
        }

        async Task<JsonDocument> ExecuteAsync(object command, JsonDocument preview)
        {
            var response = await client.PostAsJsonAsync("/api/u9-boms/write-execute", new
            {
                command,
                requestSha256 = preview.RootElement.GetProperty("requestSha256").GetString(),
                confirmation = preview.RootElement.GetProperty("requiredConfirmation").GetString()
            });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            return JsonDocument.Parse(body);
        }

        var originalComponent = Component(10, "01020000057", 2m);
        using var createPreview = await PreviewAsync(Command((int)U9BomWriteOperation.Create, originalComponent));
        Assert.Equal(U9BomContract.CreatePath, createPreview.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, createPreview.RootElement.GetProperty("addedComponentCount").GetInt32());
        Assert.Equal(0, createPreview.RootElement.GetProperty("retainedHistoricalComponentCount").GetInt32());
        using var createPayload = JsonDocument.Parse(createPreview.RootElement.GetProperty("requestPreview").GetString()!);
        var marker = Assert.Single(createPayload.RootElement.EnumerateArray()).GetProperty("OtherID").GetString()!;
        var stampedExplain = Assert.Single(createPayload.RootElement.EnumerateArray()).GetProperty("Explain").GetString()!;
        Assert.StartsWith("pdm-bom-", marker);
        Assert.Contains($"[PDM:{marker}]", stampedExplain, StringComparison.OrdinalIgnoreCase);
        fake.BomResults.Enqueue(new(0, null, []));
        var baseline = Existing(null, [ExistingComponent(10, "01020000057", 2m)], stampedExplain);
        fake.BomResults.Enqueue(new(0, null, [baseline]));
        using var createExecution = await ExecuteAsync(Command((int)U9BomWriteOperation.Create, originalComponent), createPreview);
        Assert.Equal(U9BomContract.CreatePath, fake.LastPostPath);
        Assert.Single(createExecution.RootElement.GetProperty("verification").GetProperty("boms").EnumerateArray());

        fake.BomResult = new(0, null, [baseline]);
        var appendedComponent = Component(20, "01020000058", 1m);
        using var modifyPreview = await PreviewAsync(Command((int)U9BomWriteOperation.Modify, appendedComponent));
        Assert.Equal(U9BomContract.ModifyPath, modifyPreview.RootElement.GetProperty("path").GetString());
        Assert.Equal(1, modifyPreview.RootElement.GetProperty("addedComponentCount").GetInt32());
        Assert.Equal(1, modifyPreview.RootElement.GetProperty("retainedHistoricalComponentCount").GetInt32());
        using var modifyPayload = JsonDocument.Parse(modifyPreview.RootElement.GetProperty("requestPreview").GetString()!);
        var modifyRow = Assert.Single(modifyPayload.RootElement.EnumerateArray());
        var appendedPayload = Assert.Single(modifyRow.GetProperty("BOMComponents").EnumerateArray());
        Assert.Equal(20, appendedPayload.GetProperty("Sequence").GetInt32());
        var afterAppend = Existing(null,
            [ExistingComponent(10, "01020000057", 2m), ExistingComponent(20, "01020000058", 1m)], stampedExplain);
        fake.BomResults.Enqueue(new(0, null, [baseline]));
        fake.BomResults.Enqueue(new(0, null, [baseline]));
        fake.BomResults.Enqueue(new(0, null, [afterAppend]));
        using var modifyExecution = await ExecuteAsync(Command((int)U9BomWriteOperation.Modify, appendedComponent), modifyPreview);
        Assert.Equal(U9BomContract.ModifyPath, fake.LastPostPath);
        Assert.Equal(2, modifyExecution.RootElement.GetProperty("verification").GetProperty("boms")[0].GetProperty("components").GetArrayLength());

        var changedExistingResponse = await client.PostAsJsonAsync("/api/u9-boms/write-preview",
            Command((int)U9BomWriteOperation.Modify, Component(10, "01020000057", 3m)));
        Assert.Equal(HttpStatusCode.BadRequest, changedExistingResponse.StatusCode);
        Assert.Contains("原有项次10不能修改", await changedExistingResponse.Content.ReadAsStringAsync());

        var deleteCommand = new
        {
            operation = (int)U9BomWriteOperation.Delete,
            itemCode = "02010003168",
            bomVersionCode = "A1",
            productUomCode = "001",
            lot = 1,
            components = Array.Empty<object>()
        };
        var deleteResponse = await client.PostAsJsonAsync("/api/u9-boms/write-preview", deleteCommand);
        Assert.Equal(HttpStatusCode.BadRequest, deleteResponse.StatusCode);
        Assert.Contains("禁止删除整张BOM", await deleteResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MaterialApi_CreatesApprovesAndListsPreviewTask()
    {
        await ConfigureReadOnlyU9Async();
        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = "API测试电气件",
            kind = "Electrical",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "M12"
        });
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();
        var rowVersion = created.RootElement.GetProperty("rowVersion").GetInt64();
        var materialCode = created.RootElement.GetProperty("materialCode").GetString()!;
        Assert.StartsWith("0101", materialCode);
        Assert.Equal(11, materialCode.Length);
        Assert.Equal(0, created.RootElement.GetProperty("referenceCount").GetInt32());

        var approvedResponse = await client.PostAsync($"/api/materials/{materialId}/approve?expectedRowVersion={rowVersion}", null);
        Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
        using var approved = JsonDocument.Parse(await approvedResponse.Content.ReadAsStringAsync());
        Assert.Equal("0101", approved.RootElement.GetProperty("material").GetProperty("u9CategoryCode").GetString());
        Assert.Equal("PreviewReady", approved.RootElement.GetProperty("task").GetProperty("status").GetString());

        var tasksResponse = await client.GetAsync("/api/material-sync-tasks");
        Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
        using var tasks = JsonDocument.Parse(await tasksResponse.Content.ReadAsStringAsync());
        Assert.Contains(tasks.RootElement.EnumerateArray(), item =>
            item.GetProperty("materialId").GetGuid() == materialId
            && item.GetProperty("payloadJson").GetString()!.Contains("\"MainItemCategory\""));
    }

    [Fact]
    public async Task MaterialRemovalReadinessApi_ReturnsClosedU9ReferenceGate()
    {
        await ConfigureReadOnlyU9Async();
        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = $"删除预检-{Guid.NewGuid():N}",
            kind = "Electrical",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "M12"
        });
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.GetAsync($"/api/materials/{materialId}/removal-readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var readiness = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, readiness.RootElement.GetProperty("pdmReferenceCount").GetInt32());
        Assert.True(readiness.RootElement.GetProperty("isPdmMaster").GetBoolean());
        Assert.True(readiness.RootElement.GetProperty("localDeletePreconditionsPassed").GetBoolean());
        Assert.False(readiness.RootElement.GetProperty("u9ReferenceCheckAvailable").GetBoolean());
        Assert.False(readiness.RootElement.GetProperty("synchronizedDeleteAvailable").GetBoolean());
    }

    [Fact]
    public async Task MaterialApi_EditsApprovedUnconfirmedMaterialAndSupersedesOldPreview()
    {
        await ConfigureReadOnlyU9Async();
        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = "API待变更气缸",
            kind = "Standard",
            categoryCode = "0102",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "CDQ2B32"
        });
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();
        var rowVersion = created.RootElement.GetProperty("rowVersion").GetInt64();
        var materialCode = created.RootElement.GetProperty("materialCode").GetString();
        var approvedResponse = await client.PostAsync($"/api/materials/{materialId}/approve?expectedRowVersion={rowVersion}", null);
        using var approved = JsonDocument.Parse(await approvedResponse.Content.ReadAsStringAsync());
        var approvedVersion = approved.RootElement.GetProperty("material").GetProperty("rowVersion").GetInt64();
        var oldTaskId = approved.RootElement.GetProperty("task").GetProperty("id").GetGuid();

        var changeResponse = await client.PostAsJsonAsync($"/api/materials/{materialId}/change", new
        {
            materialCode,
            name = "API待变更气缸新名称",
            kind = "Standard",
            categoryCode = "0102",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "CDQ2B32-100",
            expectedRowVersion = approvedVersion
        });

        Assert.True(changeResponse.IsSuccessStatusCode, await changeResponse.Content.ReadAsStringAsync());
        using var changed = JsonDocument.Parse(await changeResponse.Content.ReadAsStringAsync());
        Assert.Equal("Create", changed.RootElement.GetProperty("task").GetProperty("operation").GetString());
        Assert.Equal("API待变更气缸新名称", changed.RootElement.GetProperty("material").GetProperty("name").GetString());
        var tasksResponse = await client.GetAsync("/api/material-sync-tasks");
        using var tasks = JsonDocument.Parse(await tasksResponse.Content.ReadAsStringAsync());
        var oldTask = Assert.Single(tasks.RootElement.EnumerateArray(), item => item.GetProperty("id").GetGuid() == oldTaskId);
        Assert.Equal("Superseded", oldTask.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MaterialApi_DeletesMaterialBeforeConfirmedU9Write()
    {
        await ConfigureReadOnlyU9Async();
        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = "API待删除料品",
            kind = "Electrical",
            supplyMode = "Purchase",
            unitCode = "001"
        });
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();
        var rowVersion = created.RootElement.GetProperty("rowVersion").GetInt64();
        var approvedResponse = await client.PostAsync($"/api/materials/{materialId}/approve?expectedRowVersion={rowVersion}", null);
        using var approved = JsonDocument.Parse(await approvedResponse.Content.ReadAsStringAsync());
        var approvedVersion = approved.RootElement.GetProperty("material").GetProperty("rowVersion").GetInt64();

        var deleteResponse = await client.DeleteAsync($"/api/materials/{materialId}?expectedRowVersion={approvedVersion}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        using var deleted = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync());
        Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());
        Assert.False(deleted.RootElement.GetProperty("archived").GetBoolean());
        var materialsResponse = await client.GetAsync($"/api/materials?includeArchived=true&query={Uri.EscapeDataString("API待删除料品")}");
        using var remaining = JsonDocument.Parse(await materialsResponse.Content.ReadAsStringAsync());
        Assert.Empty(remaining.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task MaterialApi_DeletesU9ThenVerifiesAbsenceBeforeDeletingPdm()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "delete-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            itemModifyPath = U9MaterialContract.ModifyPath,
            itemDeletePath = U9MaterialContract.DeletePath,
            writeEnabled = true
        });
        Assert.True(settingsResponse.IsSuccessStatusCode, await settingsResponse.Content.ReadAsStringAsync());

        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.QueryResults.Clear();
        fake.QueryResult = new U9ItemQueryResult(0, null, []);
        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = $"API同步删除-{Guid.NewGuid():N}",
            kind = "Electrical",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "M18"
        });
        Assert.True(createdResponse.IsSuccessStatusCode, await createdResponse.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();
        var rowVersion = created.RootElement.GetProperty("rowVersion").GetInt64();
        var materialCode = created.RootElement.GetProperty("materialCode").GetString()!;

        fake.QueryResults.Enqueue(new U9ItemQueryResult(0, null, [new("12345", materialCode)]));
        fake.QueryResults.Enqueue(new U9ItemQueryResult(0, null, []));
        fake.BusinessResult = new U9BusinessBatchResult(0, null, [new(true, null, "12345", materialCode)]);

        var readinessResponse = await client.GetAsync($"/api/materials/{materialId}/removal-readiness");
        using var readiness = JsonDocument.Parse(await readinessResponse.Content.ReadAsStringAsync());
        Assert.True(readiness.RootElement.GetProperty("synchronizedDeleteAvailable").GetBoolean());

        var deleteResponse = await client.DeleteAsync($"/api/materials/{materialId}?expectedRowVersion={rowVersion}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Equal(U9MaterialContract.DeletePath, fake.LastPostPath);
        using var payload = JsonDocument.Parse(fake.LastPostPayload);
        Assert.Equal(materialCode, payload.RootElement[0].GetProperty("Code").GetString());
        Assert.Equal(12345, payload.RootElement[0].GetProperty("ID").GetInt64());
    }

    [Fact]
    public async Task MaterialSyncApi_QueriesBeforeCreatingAndReturnsCompletedTask()
    {
        var settingsResponse = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "execute-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            writeEnabled = true
        });
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);

        var createdResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = "API同步测试电气件",
            kind = "Electrical",
            supplyMode = "Purchase",
            unitCode = "001",
            specification = "M12"
        });
        using var created = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
        var materialId = created.RootElement.GetProperty("id").GetGuid();
        var rowVersion = created.RootElement.GetProperty("rowVersion").GetInt64();
        var materialCode = created.RootElement.GetProperty("materialCode").GetString()!;
        var approvedResponse = await client.PostAsync($"/api/materials/{materialId}/approve?expectedRowVersion={rowVersion}", null);
        using var approved = JsonDocument.Parse(await approvedResponse.Content.ReadAsStringAsync());
        var taskId = approved.RootElement.GetProperty("task").GetProperty("id").GetGuid();

        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.QueryResults.Clear();
        fake.QueryResults.Enqueue(new U9ItemQueryResult(0, null, []));
        fake.QueryResults.Enqueue(new U9ItemQueryResult(0, null,
            [new U9ItemReference("u9-1001", materialCode, "API同步测试电气件", "M12", "0101", null, "001")
            {
                CreationAttributes = new Dictionary<string, string?>
                {
                    ["ItemFormAttribute"] = "9", ["IsPurchaseEnable"] = "true", ["IsBuildEnable"] = "true",
                    ["IsOutsideOperationEnable"] = "true", ["IsMRPEnable"] = "true", ["IsBOMEnable"] = "true",
                    ["IsSalesEnable"] = "true", ["IsInventoryEnable"] = "true", ["IsVarRatio"] = "true",
                    ["Effective.IsEffective"] = "true", ["CostCurrency.Code"] = "C001",
                    ["InventoryInfo.PurchaseControlMode"] = "1", ["InventoryInfo.TurnOverRate"] = "0",
                    ["InventoryInfo.LotControlMode"] = "2", ["InventoryInfo.IsBalanceByProject"] = "true",
                    ["InventoryInfo.IsInvCalculateBySeiban"] = "true", ["MrpInfo.MRPPlanningType"] = "0",
                    ["MrpInfo.ForecastContorlType"] = "1", ["MrpInfo.IsTraceRequirement"] = "true",
                    ["MrpInfo.IsControlByDC"] = "true", ["MrpInfo.DemandRule"] = "0",
                    ["MfgInfo.IsInheritBomMasterNo"] = "true", ["MfgInfo.DesignationRule"] = "1",
                    ["MfgInfo.IsExpandByOrder"] = "true", ["MfgInfo.BuildShrinkageRate"] = "1",
                    ["PurchaseInfo.IsNeedRequest"] = "true", ["PurchaseInfo.ReceiptModeAllowModify"] = "true",
                    ["PurchaseInfo.IsPUTradePathModify"] = "true", ["PurchaseInfo.IsPURtnTradePathModify"] = "true",
                    ["SaleInfo.IsReturnable"] = "true", ["SaleInfo.IsRMAAllowModify"] = "true",
                    ["SaleInfo.IsSDTradePathModify"] = "true", ["SaleInfo.IsSDRtnTradePathModify"] = "true",
                    ["SaleInfo.SupplySource"] = "4", ["SaleInfo.DemandTransType"] = "4", ["SaleInfo.SupplyOrg.Code"] = "7"
                }
            }]));
        fake.BusinessResult = new U9BusinessBatchResult(0, null, [new(true, null, "u9-1001", materialCode)]);
        var executeResponse = await client.PostAsync($"/api/material-sync-tasks/{taskId}/execute", null);

        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        using var result = JsonDocument.Parse(await executeResponse.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal("Succeeded", result.RootElement.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal("u9-1001", result.RootElement.GetProperty("material").GetProperty("u9ItemId").GetString());
        Assert.True(fake.QueryCallCount >= 2);
        Assert.True(fake.PostCallCount >= 1);
    }

    [Fact]
    public async Task MaterialCategoryApi_MaintainsU9TreeAndGeneratesConfiguredCode()
    {
        await ConfigureReadOnlyU9Async();
        var categoryResponse = await client.PostAsJsonAsync("/api/material-categories", new
        {
            code = "010401",
            name = "劳保用品",
            parentCode = "0104",
            pdmKind = "Electrical",
            defaultSupplyMode = "Purchase",
            allowCreate = true,
            isVisible = true,
            isActive = true,
            numberPrefix = "LB-",
            sequenceLength = 7,
            counterScope = "labor-protection",
            sortOrder = 10401
        });
        Assert.True(categoryResponse.IsSuccessStatusCode, await categoryResponse.Content.ReadAsStringAsync());

        var calibrationResponse = await client.PutAsJsonAsync("/api/material-categories/010401/counter", new
        {
            lastMaterialCode = "LB-1000041"
        });
        Assert.True(calibrationResponse.IsSuccessStatusCode, await calibrationResponse.Content.ReadAsStringAsync());

        var materialResponse = await client.PostAsJsonAsync("/api/materials", new
        {
            name = "API防护手套",
            kind = "Electrical",
            categoryCode = "010401",
            supplyMode = "Purchase",
            unitCode = "001"
        });
        Assert.True(materialResponse.IsSuccessStatusCode, await materialResponse.Content.ReadAsStringAsync());
        using var material = JsonDocument.Parse(await materialResponse.Content.ReadAsStringAsync());
        Assert.Equal("LB-1000042", material.RootElement.GetProperty("materialCode").GetString());
        Assert.Equal("010401", material.RootElement.GetProperty("categoryCode").GetString());

        var searchResponse = await client.GetAsync("/api/materials?query=LB-1000042");
        Assert.True(searchResponse.IsSuccessStatusCode, await searchResponse.Content.ReadAsStringAsync());
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync());
        Assert.Single(search.RootElement.EnumerateArray());
    }

    private async Task ConfigureReadOnlyU9Async()
    {
        var response = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "query-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            writeEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var fake = factory.Services.GetRequiredService<TestU9OpenApiClient>();
        fake.QueryResults.Clear();
        fake.QueryResult = new U9ItemQueryResult(0, null, []);
    }

    private static string CreateToken(string username, string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            "upton-pdm",
            "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
