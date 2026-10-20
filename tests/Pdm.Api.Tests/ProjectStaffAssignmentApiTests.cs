using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Api.Tests;
public sealed class ProjectStaffAssignmentApiTests
{
    [Fact]
    public async Task ExistingPhaseOwnerEndpointPersistsFourIndependentProjectAssignmentsAndClearing()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "HTTP岗位独立分配", null, new(2026, 10, 19), 1, "admin", "D:/test", "D:/release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "HTTP独立子项目", null, 1), default);
        var unit = await repository.SaveOrganizationUnitAsync(new(null, root.OrganizationId!.Value, null, "HTTP-STAFF", "HTTP岗位测试事业部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        foreach (var username in new[] { "staff-pm", "staff-worker", "staff-outsider" }) {
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, username, "unused", UserRole.Engineer, true), default);
            await repository.SetOrganizationMembershipsAsync(username, [unit.Id], unit.Id, default);
        }
        await repository.SetProjectExecutionUnitAsync(root.Id, unit.Id, "admin", default);
        await repository.SetMainProjectStaffingAsync(root.Id, new("staff-pm", [], ["staff-pm"]), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectDesignerAssign], default);
        void Authenticate(string username) {
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, "Engineer")], expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        Authenticate("staff-pm");
        var keys = new[] { "ElectricalEngineer", "ElectricalHardware", "MechanicalAssembly", "ElectricalAssembly" };
        foreach (var project in new[] { root, child }) {
            var owner = project.Id == root.Id ? "staff-pm" : "staff-worker";
            var owners = keys.ToDictionary(key => key, _ => owner);
            owners["Acceptance"] = "staff-pm";
            var response = await client.PutAsJsonAsync($"/api/projects/{project.Id}/phase-owners", new { phaseOwners = owners });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = (await client.GetFromJsonAsync<Project>($"/api/projects/{project.Id}"))!;
            Assert.All(keys, key => Assert.Equal(owner, saved.PhaseOwners[key]));
        }
        var childOwners = keys.ToDictionary(key => key, _ => "staff-worker");
        childOwners["ElectricalEngineer"] = "";
        childOwners["Acceptance"] = "staff-pm";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/projects/{child.Id}/phase-owners", new { phaseOwners = childOwners })).StatusCode);
        var cleared = (await client.GetFromJsonAsync<Project>($"/api/projects/{child.Id}"))!;
        Assert.False(cleared.PhaseOwners.ContainsKey("ElectricalEngineer"));
        Assert.Equal("staff-worker", cleared.PhaseOwners["ElectricalHardware"]);
        Assert.Equal("staff-pm", cleared.PhaseOwners["Acceptance"]);
        Assert.Equal("staff-pm", (await client.GetFromJsonAsync<Project>($"/api/projects/{root.Id}"))!.PhaseOwners["ElectricalEngineer"]);
        Authenticate("staff-outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/projects/{root.Id}/phase-owners", new { phaseOwners = childOwners })).StatusCode);
    }
}
