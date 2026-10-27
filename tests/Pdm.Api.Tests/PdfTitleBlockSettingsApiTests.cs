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
        var response = await client.PutAsJsonAsync("/api/system-settings", settings with { PdfTitleBlockFill = new() { EnabledFields = ["weight", "materialCode", "wearPart"] } });
        response.EnsureSuccessStatusCode();
        var saved = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal(new[] { "wearPart", "materialCode", "weight" }, saved.PdfTitleBlockFill.EnabledFields);
        var legacy = JsonNode.Parse(await (await client.GetAsync("/api/system-settings")).Content.ReadAsStringAsync())!.AsObject();
        legacy.Remove("pdfTitleBlockFill");
        (await client.PutAsJsonAsync("/api/system-settings", legacy)).EnsureSuccessStatusCode();
        saved = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal(new[] { "wearPart", "materialCode", "weight" }, saved.PdfTitleBlockFill.EnabledFields);
        using var scope = factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<PdmWorkflowService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.UpdateSystemSettingsAsync(
            saved with { PdfTitleBlockFill = new() }, "admin", UserRole.Engineer, default));
        response = await client.PutAsJsonAsync("/api/system-settings", saved with { PdfTitleBlockFill = new() { EnabledFields = ["invalid"] } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
