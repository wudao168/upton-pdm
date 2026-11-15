using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class PdfTitleBlockSettingsApiTests
{
    [Fact]
    public async Task AdminChoicesRoundTripAndLegacyClientsPreserveConfiguration()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var settings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Empty(settings.PdfTitleBlockFill.EnabledFields);
        var response = await client.PutAsJsonAsync("/api/system-settings", settings with { PdfTitleBlockFill = new() { EnabledFields = ["weight", "materialCode", "wearPart"], DrawingLabels = new Dictionary<string, string> { ["reviewer"] = " 复核 ", ["weight"] = "净重" } } });
        response.EnsureSuccessStatusCode();
        var saved = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal(new[] { "wearPart", "materialCode", "weight" }, saved.PdfTitleBlockFill.EnabledFields);
        var legacy = JsonNode.Parse(await (await client.GetAsync("/api/system-settings")).Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("复核", saved.PdfTitleBlockFill.DrawingLabels!["reviewer"]);
        Assert.Equal("净重", saved.PdfTitleBlockFill.DrawingLabels!["weight"]);
        legacy["pdfTitleBlockFill"]!.AsObject().Remove("drawingLabels");
        (await client.PutAsJsonAsync("/api/system-settings", legacy)).EnsureSuccessStatusCode();
        legacy.Remove("pdfTitleBlockFill");
        (await client.PutAsJsonAsync("/api/system-settings", legacy)).EnsureSuccessStatusCode();
        saved = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal(new[] { "wearPart", "materialCode", "weight" }, saved.PdfTitleBlockFill.EnabledFields);
        Assert.Equal("复核", saved.PdfTitleBlockFill.DrawingLabels!["reviewer"]);
        response = await client.PutAsJsonAsync("/api/system-settings", saved with
        {
            PdfTitleBlockFill = saved.PdfTitleBlockFill with { DrawingLabels = new Dictionary<string, string> { ["reviewer"] = " ： " } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();

        response = await client.PutAsJsonAsync("/api/system-settings", saved with { PdfTitleBlockFill = new() { EnabledFields = ["invalid"] } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var role = (await repository.GetRolePermissionDirectoryAsync(default)).Roles.Single(r => r.Role == "Administrator");
        await repository.SetRolePermissionsAsync(role.Role, role.Permissions.Where(code => code != PermissionCodes.StorageSettingsManage).ToArray(), default);
        var denied = await client.PutAsJsonAsync("/api/system-settings", saved with
        {
            PdfTitleBlockFill = saved.PdfTitleBlockFill with { DrawingLabels = new Dictionary<string, string> { ["reviewer"] = "校核" } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await repository.SetRolePermissionsAsync(role.Role, role.Permissions, default);
        Assert.Equal("复核", (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!.PdfTitleBlockFill.DrawingLabels!["reviewer"]);
    }
}
