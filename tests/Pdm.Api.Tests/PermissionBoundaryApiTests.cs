using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class PermissionBoundaryApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterialCodeReadDoesNotWriteWithoutElectricalEditGrant(bool canEditMechanical)
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var project = await repository.CreateProjectAsync(new($"PERMISSION-{Guid.NewGuid():N}", "接口权限测试", "admin", @"D:\PDM\Permission", @"D:\PDM\PermissionRelease"), "admin", default);
        await repository.SetMainProjectStaffingAsync(project.Id, new("admin", [], ["engineer"]), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", canEditMechanical
            ? [PermissionCodes.ProjectContentView, PermissionCodes.BomEdit, PermissionCodes.BomMechanicalEdit]
            : [PermissionCodes.ProjectContentView], default);
        var category = (await materials.FindCategoryAsync("0101", default))!;
        var now = DateTimeOffset.UtcNow;
        var material = await materials.CreateMaterialAsync(new(
            Guid.NewGuid(), "01010000099", "电气模块", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001", "TEST-MODEL", null, null, "TEST", null,
            null, null, null, MaterialApprovalStatus.Approved, "admin", now, "0101", null, "01010000099",
            MaterialSyncStatus.Succeeded, "admin", now, "admin", now, 1, "0101", U9SyncConfirmed: true), category, default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Electrical, 1, "", material.Name, 1, "001", null, material.Specification, "W1", true) { Brand = material.Brand };
        await repository.ReplaceBomAsync(project.Id, BomKind.Electrical, [item], default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        var read = await client.PostAsJsonAsync("/api/material-code/resolve", new { projectId = project.Id, bomItemIds = new[] { item.Id } });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("Matched", await read.Content.ReadAsStringAsync());
        Assert.Null(await materials.FindMaterialBySourceBomItemAsync(item.Id, default));
        Assert.Empty((await repository.FindBomItemAsync(project.Id, item.Id, default))!.DrawingNumber);
        var write = await client.PostAsJsonAsync("/api/material-code/applications", new { projectId = project.Id, bomItemIds = new[] { item.Id } });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.BomEdit, PermissionCodes.BomElectricalEdit], default);
        var allowed = await client.PostAsJsonAsync("/api/material-code/resolve", new { projectId = project.Id, bomItemIds = new[] { item.Id } });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(material.Id, (await materials.FindMaterialBySourceBomItemAsync(item.Id, default))!.Id);
        Assert.Equal(material.MaterialCode, (await repository.FindBomItemAsync(project.Id, item.Id, default))!.DrawingNumber);
    }

    private static string Token(string actor, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
