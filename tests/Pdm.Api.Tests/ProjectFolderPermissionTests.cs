using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectFolderPermissionTests
{
    [Fact]
    public async Task ManagerNeedsRolePositionAndDirectoryPermissionToCreateFolder()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        Assert.IsType<InMemoryPdmRepository>(repository);
        var project = (await repository.ListProjectsAsync(default)).First();
        const string manager = "folder-manager";
        await repository.CreateUserAsync(new(Guid.NewGuid(), manager, "目录测试经理", "unused", UserRole.Engineer, true, RoleCode: "ProjectManager", CompanyId: project.OrganizationId), default);
        await repository.SetMainProjectStaffingAsync(project.Id, new(manager, [], []), "admin", default);
        await repository.SetRolePermissionsAsync("ProjectManager", [FunctionPermissionCatalog.SchemaMarker, "project.file.create"], default);
        var settings = (await repository.GetProjectPermissionSettingsAsync(default)).Normalize();
        var rules = settings.Rules.ToDictionary(item => item.Key, item => item.Value);
        rules[ProjectPermissionPositions.MainManager] = ["project.file.view"];
        rules[ProjectPermissionPositions.Other] = [];
        await repository.UpdateProjectPermissionSettingsAsync(new(rules, [], 2), default);
        var folder = (await repository.ListProjectFoldersAsync(project.Id, "admin", UserRole.Administrator, default)).Single(item => item.TemplateKey == "project-files");
        await repository.SetProjectFolderPermissionsAsync(project.Id, folder.Id,
            [new(FolderPrincipalType.Role, "ProjectManager", FolderAccess.View | FolderAccess.Edit)], "admin", UserRole.Administrator, default);
        Authorize(client, manager);
        var url = $"/api/projects/{project.Id}/folders/{folder.Id}/children";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new { name = "岗位尚未授权" })).StatusCode);

        rules[ProjectPermissionPositions.MainManager] = ["project.file.create"];
        await repository.UpdateProjectPermissionSettingsAsync(new(rules, [], 2), default);
        var created = await client.PostAsJsonAsync(url, new { name = "经理创建目录" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        Assert.Contains(await repository.ListProjectFoldersAsync(project.Id, manager, UserRole.Engineer, default), item => item.Name == "经理创建目录");

        await repository.SetProjectFolderPermissionsAsync(project.Id, folder.Id,
            [new(FolderPrincipalType.Role, "ProjectManager", FolderAccess.View)], "admin", UserRole.Administrator, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new { name = "目录未授权" })).StatusCode);
        await repository.SetProjectFolderPermissionsAsync(project.Id, folder.Id,
            [new(FolderPrincipalType.Role, "ProjectManager", FolderAccess.View | FolderAccess.Edit)], "admin", UserRole.Administrator, default);
        await repository.SetRolePermissionsAsync("ProjectManager", [FunctionPermissionCatalog.SchemaMarker, "project.file.view"], default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new { name = "角色未授权" })).StatusCode);
    }

    [Fact]
    public async Task TemplateUpdatesExistingFoldersAndInheritedChildrenButPreservesIndependentRules()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        Assert.IsType<InMemoryPdmRepository>(repository);
        var project = (await repository.ListProjectsAsync(default)).First();
        var folders = await repository.ListProjectFoldersAsync(project.Id, "admin", UserRole.Administrator, default);
        var inherited = folders.Single(item => item.TemplateKey == "project-files");
        var independent = folders.Single(item => item.TemplateKey == "purchase");
        var child = await repository.CreateProjectFolderAsync(project.Id, inherited.Id, "沿用上级权限", default);
        await repository.SetProjectFolderPermissionsAsync(project.Id, independent.Id,
            [new(FolderPrincipalType.Role, "Engineer", FolderAccess.View | FolderAccess.Edit)], "admin", UserRole.Administrator, default);
        var originalRules = (await repository.ListProjectFoldersAsync(project.Id, "admin", UserRole.Administrator, default)).Single(item => item.Id == independent.Id).Permissions;
        var template = await repository.ListFolderTemplateAsync(default);
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: Credentials());
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        foreach (var access in new[] { FolderAccess.View, FolderAccess.View | FolderAccess.Upload | FolderAccess.Edit, FolderAccess.None })
        {
            var nodes = template.Select(node => new SaveFolderTemplateNodeCommand(node.FolderKey, node.Name, node.SortOrder, node.InheritPermissions,
                node.FolderKey is "project-files" or "purchase" ? [new(FolderPrincipalType.Role, "Engineer", access)] : node.Permissions.Select(rule => new SaveFolderPermissionCommand(rule.PrincipalType, rule.PrincipalKey, rule.Access)).ToArray())).ToArray();
            var saved = await client.PutAsJsonAsync("/api/folder-template", new { nodes });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var updated = await repository.ListProjectFoldersAsync(project.Id, "engineer", UserRole.Engineer, default);
            Assert.Equal(access, updated.Single(item => item.Id == inherited.Id).EffectiveAccess);
            Assert.Equal(access, updated.Single(item => item.Id == child.Id).EffectiveAccess);
            var preserved = updated.Single(item => item.Id == independent.Id);
            Assert.Equal(FolderAccess.View | FolderAccess.Edit, preserved.EffectiveAccess);
            Assert.Equal(originalRules, preserved.Permissions);
        }
    }

    private static SigningCredentials Credentials() => new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256);
    private static void Authorize(HttpClient client, string username)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: Credentials());
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }
}
