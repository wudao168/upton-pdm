using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api.Tests;

public sealed class ReleaseNotificationSettingsTests
{
    [Fact]
    public async Task RemovedSettingsCannotBeReadOrReenabled()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        foreach (var response in new[] { await client.GetAsync("/api/settings/release-notifications"), await client.PutAsJsonAsync("/api/settings/release-notifications", new { enabled = true }) })
        {
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            Assert.Contains("发布通知配置功能已移除", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task HistoricalEnabledConfigurationIsRetainedButNeverActivated()
    {
        using var factory = new PdmApiFactory();
        using var scope = factory.Services.CreateScope();
        var company = Guid.NewGuid();
        var store = scope.ServiceProvider.GetRequiredService<IReleaseDeliveryStore>();
        await store.SaveConfigurationAsync(new(company, "historical-protected-settings", 0, true, DateTimeOffset.UtcNow), 0, default);
        var provider = scope.ServiceProvider.GetRequiredService<IReleaseNotificationConfigurationProvider>();
        var loaded = await provider.LoadAsync(company, default);
        Assert.False(loaded.Settings.Enabled);
        Assert.Empty(loaded.Settings.Rules);
        Assert.Equal(0, loaded.Version);
        Assert.True((await store.FindConfigurationAsync(company, default))!.Enabled);
    }
}
