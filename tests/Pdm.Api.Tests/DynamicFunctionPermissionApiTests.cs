using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class DynamicFunctionPermissionApiTests
{
    [Fact]
    public async Task CustomPositionCanBeCreatedAssignedAndProtectedFromReferencedDeletion()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "岗位接口验证", null, new(2026, 10, 16), 1, "admin", "D:/test", "D:/release"), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "测试管理员", "unused", UserRole.Administrator, true), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "engineer", "测试工程师", "unused", UserRole.Engineer, true), default);
        var unit = await repository.SaveOrganizationUnitAsync(new(null, project.OrganizationId!.Value, null, "POST-API", "岗位测试事业部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        await repository.SetUserCompanyScopeAsync("admin", project.OrganizationId.Value, true, [project.OrganizationId.Value], "admin", default);
        await repository.SetOrganizationMembershipsAsync("engineer", [unit.Id], unit.Id, default);
        var settings = await repository.GetProjectPermissionSettingsAsync(default);
        var key = "custom-22222222222222222222222222222222";
        var rules = settings.Rules.ToDictionary(item => item.Key, item => item.Value);
        rules[key] = ["bom.mechanical.row.create", "bom.mechanical.row.view"];
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var saved = await client.PutAsJsonAsync("/api/project-permissions", new { rules, customPositions = new[] { new ProjectPositionDefinition(key, "复核", "接口验证", "subtree") }, schemaVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var assigned = await client.PutAsJsonAsync($"/api/projects/{project.Id}/position-members", new { members = new Dictionary<string, string[]> { [key] = ["engineer"] } });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        Assert.Equal("engineer", Assert.Single((await repository.FindProjectAsync(project.Id, default))!.CustomPositionMembers[key]));
        var deleted = await client.PutAsJsonAsync("/api/project-permissions", settings);
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        var definitions = await client.GetFromJsonAsync<JsonElement>("/api/project-permissions");
        Assert.Contains(definitions.GetProperty("positions").EnumerateArray(), item => item.GetProperty("key").GetString() == key && item.GetProperty("projectCount").GetInt32() == 1);
    }
    [Fact]
    public async Task RoleAndPositionPagesReadSameFunctionDefinitions()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var roles = await client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        var positions = await client.GetFromJsonAsync<JsonElement>("/api/project-permissions");
        var all = roles.GetProperty("permissions").EnumerateArray().Where(item => item.GetProperty("scope").GetString() == "project" && !item.GetProperty("hidden").GetBoolean()).Select(item => item.GetProperty("code").GetString()).Order().ToArray();
        var scoped = positions.GetProperty("permissions").EnumerateArray().Select(item => item.GetProperty("code").GetString()).Order().ToArray();
        Assert.Equal(all, scoped);
        Assert.Contains("project.plan.create", scoped);
        Assert.Contains("bom.mechanical.row.delete", scoped);
        Assert.Equal(2, positions.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task GranularMiddlewareDeniesUnselectedActionEvenWhenLegacyCapabilityExists()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        await repository.SetMainProjectStaffingAsync(project.Id, new("engineer", [], []), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [FunctionPermissionCatalog.SchemaMarker, "project.plan.create"], default);
        Assert.True(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, PermissionCodes.ProjectPlanEdit, default));
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        var deleted = await client.DeleteAsync($"/api/projects/{project.Id}/plan?expectedRowVersion=1");
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        var update = await client.PutAsJsonAsync($"/api/projects/{project.Id}/plan", new { });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        var files = await client.PostAsJsonAsync($"/api/projects/{project.Id}/folders/{Guid.NewGuid()}/children", new { name = "无权创建" });
        Assert.Equal(HttpStatusCode.Forbidden, files.StatusCode);
    }

    [Fact]
    public async Task DirectoryRoutesMatchRealRegisteredEndpoints()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var declaration in RolePermissionCatalog.Permissions.SelectMany(item => item.Routes))
            Assert.Contains(endpoints, endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(declaration.Method) == true
                && Matches(endpoint.RoutePattern.RawText!, declaration.Path));
    }

    private static bool Matches(string template, string path)
    {
        var left = template.Trim('/').Split('/');
        var right = path.Trim('/').Split('/');
        return left.Length == right.Length && left.Zip(right).All(pair => pair.First.StartsWith('{') || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static string Token(string actor, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role)],
        expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
