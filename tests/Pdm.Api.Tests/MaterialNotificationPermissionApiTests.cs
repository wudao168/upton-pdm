using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class MaterialNotificationPermissionApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task StandardizationReceivesWhileProjectManagerAndPureAdministratorDoNot()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        using var client = factory.CreateClient();
        foreach (var (code, role, expected) in new[] { ("ProjectManager", UserRole.Engineer, false), ("Approver", UserRole.Approver, true), ("Administrator", UserRole.Administrator, false) })
        {
            var username = "material-notice-" + code;
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, "消息验证", "unused", role, true, RoleCode: code), default);
            Authorize(client, username, role);
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/materials/notification-permission"));
            Assert.Equal(expected, result.RootElement.GetProperty("enabled").GetBoolean());
        }
    }

    [Fact]
    public async Task ChangingNotificationPermissionIsReflectedWithoutChangingViewPermission()
    {
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var username = "material-notice-switch";
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "开关验证", "unused", UserRole.Engineer, true, RoleCode: "ProjectManager"), default);
        Authorize(client, username, UserRole.Engineer);
        foreach (var enabled in new[] { false, true, false })
        {
            await repository.SetRolePermissionsAsync("ProjectManager", enabled ? [PermissionCodes.MaterialView, PermissionCodes.MaterialNotify] : [PermissionCodes.MaterialView], default);
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/materials/notification-permission"));
            Assert.Equal(enabled, result.RootElement.GetProperty("enabled").GetBoolean());
            Assert.True(await repository.HasUserPermissionAsync(username, UserRole.Engineer, PermissionCodes.MaterialView, default));
        }
    }

    private static void Authorize(HttpClient client, string username, UserRole role)
    {
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role.ToString())], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
