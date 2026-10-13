using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class SystemConnectionStatusTests
{
    [Fact]
    public async Task StatusRequiresLoginButNotAdministrativePermissions()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/system-connections")).StatusCode);
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "engineer"), new Claim(ClaimTypes.Role, "Engineer")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        var response = await client.GetAsync("/api/system-connections");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("clientSecret", text);
        Assert.DoesNotContain("agentToken", text);
        Assert.DoesNotContain("baseUrl", text);
    }

    [Theory]
    [InlineData(true, "connected")]
    [InlineData(false, "disconnected")]
    public async Task ProbesWorkerReadinessAuthenticatesU9AndCachesResults(bool workerExists, string expectedState)
    {
        using var factory = new PdmApiFactory();
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { PreviewConversion = new() { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent.test", AgentToken = "private-agent-token" } }, default);
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var secrets = scope.ServiceProvider.GetRequiredService<IU9SecretProtector>();
        var configuration = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(configuration with { ClientSecretCiphertext = secrets.Protect("private-u9-secret") }, default);
        var probes = new ProbeClients(workerExists);
        var service = new SystemConnectionStatus(repository, materials, secrets, scope.ServiceProvider.GetRequiredService<IU9OpenApiClient>(),
            probes, new(), TimeProvider.System, NullLogger<SystemConnectionStatus>.Instance);
        var results = await Task.WhenAll(service.ReadAsync(default), service.ReadAsync(default));
        Assert.Equal(expectedState, results[0].Pdf.State);
        Assert.Equal("connected", results[0].U9c.State);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, probes.Calls);
        var json = JsonSerializer.Serialize(results[0]);
        Assert.DoesNotContain("private-", json);
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<TestU9OpenApiClient>().PostCallCount);
    }

    private sealed class ProbeClients(bool workerExists) : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(new ProbeHandler(this, workerExists));
        private sealed class ProbeHandler(ProbeClients owner, bool workerExists) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Calls);
                Assert.Equal("/health", request.RequestUri!.AbsolutePath);
                Assert.Equal("private-agent-token", request.Headers.GetValues(Upton.Pdm.Infrastructure.PreviewAgentProtocol.TokenHeader).Single());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { workerExists }) });
            }
        }
    }
}
