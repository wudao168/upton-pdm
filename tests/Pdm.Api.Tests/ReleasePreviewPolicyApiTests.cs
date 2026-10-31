using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ReleasePreviewPolicyApiTests
{
    [Theory]
    [InlineData(ReleaseScope.StandardFormal)]
    [InlineData(ReleaseScope.StandardSupplement)]
    [InlineData(ReleaseScope.ElectricalFormal)]
    [InlineData(ReleaseScope.ElectricalSupplement)]
    public async Task BomOnlyReleaseRejectsRetryAndReturnsNoConversionItemsEvenWithStaleStatus(ReleaseScope scope)
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default)).First();
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-NO-CONVERSION", ReleasePackageState.Published,
            null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "unused") {
            Scope = scope, LocksDocuments = true, PreviewState = ReleasePreviewState.Pending, PreviewAttempts = 5
        };
        await repository.CreateReleasePackageAsync(package, default);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, UserRole.Administrator.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        if (project.OrganizationId.HasValue) client.DefaultRequestHeaders.Add("X-Company-Id", project.OrganizationId.Value.ToString());
        var response = await client.PostAsJsonAsync($"/api/release-packages/{package.Id}/preview/retry", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不支持转图", await response.Content.ReadAsStringAsync());
        var unchanged = (await repository.FindReleasePackageAsync(package.Id, default))!;
        Assert.Equal(5, unchanged.PreviewAttempts);
        var items = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/projects/{project.Id}/release-preview-items?releasePackageId={package.Id}");
        Assert.Equal(0, items.GetArrayLength());
    }
}
