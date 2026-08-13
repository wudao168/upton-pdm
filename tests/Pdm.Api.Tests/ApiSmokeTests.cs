using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ApiSmokeTests : IClassFixture<PdmApiFactory>
{
    private readonly HttpClient client;
    private readonly PdmApiFactory factory;

    public ApiSmokeTests(PdmApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReportsIsolatedPdmPorts()
    {
        var health = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(5080, health.ApiPort);
        Assert.Equal(3308, health.MySqlPort);
    }

    [Fact]
    public async Task ProjectApi_RequiresAuthentication()
    {
        var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Engineer_CreatesAndListsOnlyResponsibleProjects()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        var before = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        Assert.Empty(before!);

        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            organizationId = "70000000-0000-0000-0000-000000000001",
            projectTypeCode = "P",
            equipmentTypeCode = 2,
            customerId = "c0046500-0000-0000-0000-000000000001",
            name = "项目创建接口验收",
            projectAlias = "验收别名",
            signedDate = "2026-08-13",
            quantity = 2
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(created);
        Assert.Equal("P700001", created.Code);
        Assert.Equal("AK-2-C00465-001-00", created.DeviceModel);
        Assert.Equal(["70000001", "70000002"], created.SerialNumbers);
        Assert.Equal(@"D:\PDM\Vault\P700001", created.VaultLocation);
        Assert.Equal(["engineer"], created.ResponsibleUsers);

        var childResponse = await client.PostAsJsonAsync($"/api/projects/{created.Id}/children", new
        {
            name = "子项目一",
            projectAlias = "子项目别名",
            quantity = 2
        });
        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var child = await childResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(child);
        Assert.Equal("P700001-1", child.Code);
        Assert.Equal("AK-2-C00465-001-01", child.DeviceModel);
        Assert.Equal(["70000003", "70000004"], child.SerialNumbers);

        var secondChildResponse = await client.PostAsJsonAsync($"/api/projects/{created.Id}/children", new { name = "子项目二", quantity = 1 });
        var secondChild = await secondChildResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.Equal("P700001-2", secondChild!.Code);
        Assert.Equal("AK-2-C00465-001-02", secondChild.DeviceModel);
        Assert.Equal(["70000005"], secondChild.SerialNumbers);

        var after = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        Assert.Equal(3, after!.Count);
        Assert.Equal(created.Id, after[0].Id);
    }

    [Fact]
    public async Task Administrator_MaintainsMasterDataAndMultipleProjectResponsibles()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var customerResponse = await client.PostAsJsonAsync("/api/customers", new { code = "C00999", name = "接口维护客户", isActive = true });
        Assert.Equal(HttpStatusCode.Created, customerResponse.StatusCode);
        var customer = await customerResponse.Content.ReadFromJsonAsync<CustomerResponse>();
        Assert.NotNull(customer);
        var customers = await client.GetFromJsonAsync<List<CustomerResponse>>("/api/customers");
        Assert.Contains(customers!, item => item.Id == customer.Id && item.Code == "C00999");

        var settings = await client.GetFromJsonAsync<SystemSettingsResponse>("/api/system-settings");
        Assert.Equal(@"D:\PDM\Vault", settings!.VaultRoot);
        var equipmentTypes = await client.GetFromJsonAsync<List<EquipmentTypeResponse>>("/api/system-settings/equipment-types");
        Assert.Equal(100, equipmentTypes!.Count);
        var equipmentResponse = await client.PutAsJsonAsync("/api/system-settings/equipment-types/99", new { name = "停用验收类型", isActive = false });
        Assert.Equal(HttpStatusCode.OK, equipmentResponse.StatusCode);
        var numberingOptions = await client.GetFromJsonAsync<NumberingOptionsResponse>("/api/project-numbering/options");
        Assert.DoesNotContain(numberingOptions!.EquipmentTypes, item => item.Code == 99);

        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        if (await repository.FindUserAsync("admin", CancellationToken.None) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "admin", "系统管理员", "unused-in-api-test", UserRole.Administrator, true), CancellationToken.None);
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "secondary", "第二负责人", "unused-in-api-test", UserRole.Engineer, true), CancellationToken.None);
        var response = await client.PutAsJsonAsync("/api/projects/11111111-1111-1111-1111-111111111111/responsibles", new { usernames = new[] { "admin", "secondary" } });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.Equal(["admin", "secondary"], project!.ResponsibleUsers);
    }

    [Fact]
    public async Task Engineer_CannotReadAProjectOutsideOwnScope()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("unassigned", "Engineer"));

        var response = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var documentResponse = await client.GetAsync("/api/documents/22222222-2222-2222-2222-222222222222/versions");
        Assert.Equal(HttpStatusCode.Unauthorized, documentResponse.StatusCode);

        var counterResponse = await client.PutAsJsonAsync("/api/project-numbering/organizations/70000000-0000-0000-0000-000000000001/counters", new { currentProjectSequence = 2130, currentSerialSequence = 6071 });
        Assert.Equal(HttpStatusCode.Forbidden, counterResponse.StatusCode);
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

    private sealed record HealthResponse(string Status, string Service, string Database, int ApiPort, int MySqlPort);
    private sealed record ProjectResponse(Guid Id, string Code, string Name, string Owner, string VaultLocation, string ReleaseLocation, bool IsActive, string? DeviceModel, IReadOnlyList<string> SerialNumbers, IReadOnlyList<string> ResponsibleUsers);
    private sealed record CustomerResponse(Guid Id, string Code, string Name, bool IsActive);
    private sealed record SystemSettingsResponse(string VaultRoot, string ReleaseRoot);
    private sealed record EquipmentTypeResponse(int Code, string Name, bool IsActive);
    private sealed record NumberingOptionsResponse(IReadOnlyList<EquipmentTypeResponse> EquipmentTypes);
}

public sealed class PdmApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
