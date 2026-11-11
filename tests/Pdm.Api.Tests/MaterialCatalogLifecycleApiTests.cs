using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class MaterialCatalogLifecycleApiTests
{
    [Fact]
    public async Task LifecycleActionsRequireExplicitAuthorizationAndProtectPublishedVersions()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "料品操作测试员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var relations = factory.Services.GetRequiredService<IMaterialRelationRepository>();
        var kits = factory.Services.GetRequiredService<IEngineeringKitRepository>();
        var now = DateTimeOffset.UtcNow;
        var draft = await relations.SaveDraftAsync(null, new(Guid.NewGuid(), "待删除草稿", null, null, []), "M1", "主料", [], "admin", now, default);
        var prior = await relations.SaveDraftAsync(null, new(Guid.NewGuid(), "已发布配置", null, null, []), "M2", "主料", [], "admin", now, default);
        var published = await relations.PublishAsync(prior.Id, prior.DraftRevision!.Id, prior.DraftRevision.RowVersion, "admin", now, default);
        var kitId = Guid.NewGuid();
        var kitDraft = new EngineeringKitRevision(Guid.NewGuid(), kitId, 1, EngineeringKitRevisionState.Draft, null, [], "admin", now, null, null);
        var kit = await kits.SaveDraftAsync(new(kitId, null, "TEST-001", "待删除套件", "UPTON", null, null, [kitDraft], "admin", now, "admin", now, 1), kitDraft, null, default);
        var releasedKitId = Guid.NewGuid();
        var releasedRevision = kitDraft with { Id = Guid.NewGuid(), KitId = releasedKitId, State = EngineeringKitRevisionState.Released, PublishedAt = now };
        var releasedKit = await kits.SaveDraftAsync(kit with { Id = releasedKitId, Code = "UKIT-TEST", CurrentReleasedRevisionId = releasedRevision.Id, Revisions = [releasedRevision] }, releasedRevision, null, default);
        var grants = new[] { PermissionCodes.MaterialRelationActivity, PermissionCodes.MaterialRelationDelete, PermissionCodes.EngineeringKitDelete };
        var roleDirectory = await client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        Assert.All(grants, code => Assert.Contains(roleDirectory.GetProperty("permissions").EnumerateArray(),
            entry => entry.GetProperty("code").GetString() == code && entry.GetProperty("sensitive").GetBoolean()));
        var originalGrants = (await repository.GetRolePermissionsAsync(UserRole.Administrator, default)).ToArray();
        Assert.DoesNotContain(originalGrants, grants.Contains);
        var draftUrl = $"/api/material-relations/templates/{draft.Id}?expectedRowVersion={draft.RowVersion}";
        var kitUrl = $"/api/engineering-kits/{kit.Id}?expectedRowVersion={kit.RowVersion}";
        var activityUrl = $"/api/material-relations/templates/{published.Id}/active";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(draftUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(kitUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(activityUrl, new { isActive = false, expectedRowVersion = published.RowVersion })).StatusCode);

        await repository.SetRolePermissionsAsync("Administrator", originalGrants.Concat(grants).ToArray(), default);
        using (var outsideCompany = new HttpRequestMessage(HttpMethod.Delete, draftUrl))
        {
            outsideCompany.Headers.Add("X-Company-Id", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(outsideCompany)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/material-relations/templates/{draft.Id}?expectedRowVersion=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/material-relations/templates/{published.Id}?expectedRowVersion={published.RowVersion}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/engineering-kits/{releasedKit.Id}?expectedRowVersion={releasedKit.RowVersion}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(draftUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(kitUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(draftUrl)).StatusCode);
        Assert.Null(await kits.FindAsync(kitId, default));
        var archivedResponse = await client.PutAsJsonAsync(activityUrl, new { isActive = false, expectedRowVersion = published.RowVersion });
        Assert.True(archivedResponse.IsSuccessStatusCode, await archivedResponse.Content.ReadAsStringAsync());
        var archived = (await archivedResponse.Content.ReadFromJsonAsync<MaterialRelationTemplate>())!;
        Assert.True(archived.IsArchived);
        Assert.Equal(published.PublishedRevision!.Id, archived.PublishedRevision!.Id);
        Assert.Empty((await client.GetFromJsonAsync<MaterialRelationTemplate[]>("/api/material-relations/templates"))!);
        Assert.True(Assert.Single((await client.GetFromJsonAsync<MaterialRelationTemplate[]>("/api/material-relations/templates?includeDraft=true"))!).IsArchived);
        var restored = await client.PutAsJsonAsync(activityUrl, new { isActive = true, expectedRowVersion = archived.RowVersion });
        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        Assert.False((await restored.Content.ReadFromJsonAsync<MaterialRelationTemplate>())!.IsArchived);

        await repository.SetRolePermissionsAsync("Administrator", originalGrants, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(activityUrl, new { isActive = false, expectedRowVersion = archived.RowVersion + 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(draftUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(kitUrl)).StatusCode);
    }
}
