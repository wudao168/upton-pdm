using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class UserTablePreferenceTests
{
    [Fact]
    public async Task PreferencesBelongToAuthenticatedAccountAndSurviveNewClient()
    {
        await using var factory = new PdmApiFactory();
        using var alice = await Login(factory, "columns-a-" + Guid.NewGuid().ToString("N"));
        var name = "upton-pdm:procurement-columns:example";
        var saved = await alice.PutAsJsonAsync("/api/me/table-preferences", new SaveUserTablePreference(name, "{\"visible\":[\"materialName\",\"purchaseOrderNumbers\"]}"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        using var reopened = factory.CreateClient();
        reopened.DefaultRequestHeaders.Authorization = alice.DefaultRequestHeaders.Authorization;
        var result = await reopened.GetFromJsonAsync<JsonElement>("/api/me/table-preferences");
        Assert.Contains("purchaseOrderNumbers", result.GetProperty("preferences").GetProperty(name).GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync("/api/me/table-preferences", new SaveUserTablePreference(name, "invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync("/api/me/table-preferences", new SaveUserTablePreference("pdm_session", "{}"))).StatusCode);
        result = await reopened.GetFromJsonAsync<JsonElement>("/api/me/table-preferences");
        Assert.Contains("purchaseOrderNumbers", result.GetProperty("preferences").GetProperty(name).GetString());
        using var bob = await Login(factory, "columns-b-" + Guid.NewGuid().ToString("N"));
        var other = await bob.GetFromJsonAsync<JsonElement>("/api/me/table-preferences");
        Assert.Empty(other.GetProperty("preferences").EnumerateObject());
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/table-preferences")).StatusCode);
    }

    [Fact]
    public async Task StoreIsolatesCompaniesAndPreservesIndependentSettings()
    {
        var store = new UserTablePreferenceStore(Options.Create(new PdmDatabaseOptions()));
        var company = Guid.NewGuid();
        await store.SaveAsync(company, "alice", new("upton-pdm:bom-columns:alice:Standard", "{\"visible\":[\"name\"]}"), default);
        await store.SaveAsync(company, "alice", new("upton-pdm:production-drawing-columns:alice", "{\"visible\":[\"revision\"]}"), default);
        Assert.Equal(2, (await store.ReadAsync(company, "ALICE", default)).Count);
        Assert.Empty(await store.ReadAsync(Guid.NewGuid(), "alice", default));
        Assert.Empty(await store.ReadAsync(company, "bob", default));
        await Assert.ThrowsAsync<PdmRuleException>(() => store.SaveAsync(company, "alice", new("pdm_session", "{}"), default));
        await Assert.ThrowsAnyAsync<JsonException>(() => store.SaveAsync(company, "alice", new("upton-pdm:bom-columns:alice:Standard", "invalid"), default));
    }

    private static async Task<HttpClient> Login(PdmApiFactory factory, string username)
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "列设置测试", passwords.Hash("Password1"), UserRole.Administrator, true), default);
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password1" });
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        return client;
    }
}
