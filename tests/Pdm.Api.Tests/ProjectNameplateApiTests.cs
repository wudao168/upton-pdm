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
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectNameplateApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task SplitEntryPublishesContentOnlyAndManufacturingReceivesTheSameFrozenContent()
    {
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "铭牌接口验收", null, new(2026, 10, 1), 1, "admin", "D:/test", "D:/release"), default);
        await repository.SetChildProjectDesignersAsync(project.Id, ["engineer"], "admin", default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "nameplate-hardware", "铭牌硬件", "unused", UserRole.Engineer, true, RoleCode: "HardwareEngineer"), default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectNameplateEdit], default);
        await repository.SetRolePermissionsAsync("HardwareEngineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectNameplateEdit], default);
        await repository.SetProjectPhaseOwnersAsync(project.Id, new Dictionary<string, string> { ["ElectricalHardware"] = "nameplate-hardware" }, "admin", default);
        var url = $"/api/projects/{project.Id}/nameplate";
        Authorize(client, "engineer", "Engineer");
        var command = new SaveProjectNameplateCommand(true, "", "1500mm x 1800mm x 2200mm", "0.5~0.7 MPa", "12000 kg", new(2026, 10, 1), 0);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, command)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url + "/confirm", new ConfirmProjectNameplateCommand(1))).StatusCode);
        Authorize(client, "nameplate-hardware", "Engineer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(url, command with { Weight = "1 kg", ExpectedRowVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, command with { PowerSupply = "380V 50Hz / 5A", ExpectedRowVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url + "/confirm", new ConfirmProjectNameplateCommand(1))).StatusCode);
        var response = await client.PostAsJsonAsync(url + "/confirm", new ConfirmProjectNameplateCommand(2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("nameplate").GetProperty("releasePackageId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url + "/confirm", new ConfirmProjectNameplateCommand(2))).StatusCode);
        var package = Assert.Single(await repository.ListReleasePackagesAsync(project.Id, default));
        Assert.Equal(id, package.Id);
        Assert.False(package.LocksDocuments);
        Assert.Empty(package.VersionBindings!);
        Authorize(client, "admin", "Administrator");
        using var production = JsonDocument.Parse(await client.GetStringAsync($"/api/production-tracking/releases/{id}"));
        var content = Assert.Single(production.RootElement.GetProperty("state").GetProperty("lines").EnumerateArray()).GetProperty("nameplateContent");
        Assert.Equal(project.SerialNumbers[0], content.GetProperty("serialNumber").GetString());
        Assert.Equal("1500mm x 1800mm x 2200mm", content.GetProperty("dimensions").GetString());
        using var scan = JsonDocument.Parse(await client.GetStringAsync($"/api/production-tracking/scan?code={Uri.EscapeDataString(project.SerialNumbers[0])}"));
        Assert.Equal(id, Assert.Single(scan.RootElement.EnumerateArray()).GetProperty("releasePackageId").GetGuid());
        using var execution = JsonDocument.Parse(await client.GetStringAsync($"/api/release-packages/{id}/execution"));
        Assert.True(execution.RootElement.GetProperty("ready").GetBoolean());
        Assert.False(execution.RootElement.GetProperty("progress").GetProperty("requiresU9").GetBoolean());
        Assert.False(execution.RootElement.GetProperty("progress").GetProperty("previewRequired").GetBoolean());
    }

    private static void Authorize(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
