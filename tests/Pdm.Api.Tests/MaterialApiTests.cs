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
        fake.QueryResult = new U9ItemQueryResult(0, null, []);
        fake.BusinessResult = new U9BusinessBatchResult(0, null, [new(true, null, "u9-1001", materialCode)]);
        var executeResponse = await client.PostAsync($"/api/material-sync-tasks/{taskId}/execute", null);

        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        using var result = JsonDocument.Parse(await executeResponse.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal("Succeeded", result.RootElement.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal("u9-1001", result.RootElement.GetProperty("material").GetProperty("u9ItemId").GetString());
        Assert.True(fake.QueryCallCount >= 1);
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
            sequenceLength = 5,
            counterScope = "labor-protection",
            sortOrder = 10401
        });
        Assert.True(categoryResponse.IsSuccessStatusCode, await categoryResponse.Content.ReadAsStringAsync());

        var calibrationResponse = await client.PutAsJsonAsync("/api/material-categories/010401/counter", new
        {
            lastMaterialCode = "LB-00041"
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
        Assert.Equal("LB-00042", material.RootElement.GetProperty("materialCode").GetString());
        Assert.Equal("010401", material.RootElement.GetProperty("categoryCode").GetString());

        var searchResponse = await client.GetAsync("/api/materials?query=LB-00042");
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
