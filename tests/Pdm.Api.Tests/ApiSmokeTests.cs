using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Upton.Pdm.Api.Tests;

public sealed class ApiSmokeTests : IClassFixture<PdmApiFactory>
{
    private readonly HttpClient client;

    public ApiSmokeTests(PdmApiFactory factory)
    {
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

    private sealed record HealthResponse(string Status, string Service, string Database, int ApiPort, int MySqlPort);
}

public sealed class PdmApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
