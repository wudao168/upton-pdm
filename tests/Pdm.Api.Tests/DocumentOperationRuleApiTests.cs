using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class DocumentOperationRuleApiTests
{
    private static string Token(string name, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task OldRulesAreReadOnlyAndArchiveWarningsAreAdministratorControlled()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/archive-warning-policy")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        Assert.Equal(HttpStatusCode.Gone, (await client.PutAsJsonAsync("/api/document-operation-rules/F11", new { enabled = false, expectedRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/document-operation-rules")).StatusCode);
        var initial = (await client.GetFromJsonAsync<ArchiveWarningPolicy>("/api/archive-warning-policy"))!;
        var catalog = (await client.GetFromJsonAsync<ArchiveWarningDefinition[]>("/api/archive-warning-policy/catalog"))!;
        Assert.Equal(24, catalog.Length);
        Assert.True(catalog.Single(item => item.Code == "ReferenceTypeUnknown").Supported);
        Assert.True((await client.GetFromJsonAsync<Dictionary<string, bool>>("/api/archive-capabilities"))!["unverifiedReferences"]);
        Assert.False(initial.Blocks("MissingConfiguration"));
        var response = await client.PutAsJsonAsync("/api/archive-warning-policy", new ArchiveWarningPolicy { Revision = initial.Revision, BlockingCodes = ["MissingConfiguration"] });
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<ArchiveWarningPolicy>())!;
        Assert.True(saved.Blocks("MissingConfiguration"));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/archive-warning-policy", initial)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/archive-warning-policy", new ArchiveWarningPolicy { Revision = saved.Revision, BlockingCodes = ["NotRebuilt"] })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/archive-warning-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/archive-warning-policy", saved)).StatusCode);
    }
}
