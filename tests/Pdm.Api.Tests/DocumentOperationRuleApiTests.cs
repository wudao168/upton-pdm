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
    public async Task RuleSwitchesRetireLowRiskBlocksAndProtectConcurrentUpdates()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/document-operation-rules")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var initial = (await client.GetFromJsonAsync<DocumentOperationRuleSettings>("/api/document-operation-rules"))!;
        Assert.Equal(133, DocumentOperationRuleIds.All.Count);
        Assert.Equal(23, DocumentOperationRuleCatalog.NonBlockingIds.Count);
        Assert.All(DocumentOperationRuleIds.All, id => Assert.Equal(DocumentOperationRuleCatalog.CanBlock(id), initial.IsEnabled(id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/document-operation-rules/A10", new { enabled = true, expectedRevision = initial.Revision })).StatusCode);
        var response = await client.PutAsJsonAsync("/api/document-operation-rules/F11", new { enabled = false, expectedRevision = initial.Revision });
        response.EnsureSuccessStatusCode();
        var changed = (await response.Content.ReadFromJsonAsync<DocumentOperationRuleSettings>())!;
        Assert.False(changed.IsEnabled("F11"));
        Assert.True(changed.IsEnabled("F20"));
        Assert.Equal(changed.Revision, (await client.GetFromJsonAsync<DocumentOperationRuleSettings>("/api/document-operation-rules"))!.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/document-operation-rules/F20", new { enabled = false, expectedRevision = initial.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/document-operation-rules/F99", new { enabled = false, expectedRevision = changed.Revision })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/document-operation-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/document-operation-rules/F11", new { enabled = true, expectedRevision = changed.Revision })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        (await client.PutAsJsonAsync("/api/document-operation-rules/F11", new { enabled = true, expectedRevision = changed.Revision })).EnsureSuccessStatusCode();
        Assert.Equal(DocumentOperationRuleCatalog.NonBlockingIds.OrderBy(id => id),
            (await client.GetFromJsonAsync<DocumentOperationRuleSettings>("/api/document-operation-rules"))!.DisabledRuleIds.OrderBy(id => id));
    }
}
