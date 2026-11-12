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

public sealed class MaterialApprovalAuthorizationApiTests
{
    [Fact]
    public async Task AssignedApproverRequiresCurrentProjectPermissionAndRevocationTakesEffectWithoutRelogin()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var company = (await repository.FindUserAsync("admin", default))!.CompanyId!.Value;
        var foreignCompany = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.Id != company).Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        Task<Project> Project(Guid companyId, string name) => repository.CreateNumberedProjectAsync(
            new(companyId, "P", 2, customer, name, null, null, 1, "admin", Path.Combine(Path.GetTempPath(), "pdm-material-approval-vault"), Path.Combine(Path.GetTempPath(), "pdm-material-approval-release")), default);
        var project = await Project(company, "料号审批授权项目");
        var other = await Project(company, "范围外料号审批项目");
        var foreign = await Project(foreignCompany, "其他公司料号审批项目");
        const string actor = "material-assigned-approver";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "料号审批验收人", "unused", UserRole.Engineer, true,
            RoleCode: "material-approval-reader", CompanyId: company), default);
        var authorization = await repository.GetAuthorizationSettingsAsync(default);
        var reader = new AuthorizationTemplate("material-approval-reader", "料号审批读取", "",
            [new(PermissionCodes.ProjectView, ["company"]), new(PermissionCodes.ProjectContentView, ["company"])]) { RoleCode = "material-approval-reader" };
        var decider = new AuthorizationTemplate("material-approval-decider", "料号审批处理", "",
            [new(PermissionCodes.ProjectView, ["company"]), new(PermissionCodes.ProjectContentView, ["company"]), new(PermissionCodes.ApprovalDecide, ["company"])]) { ProjectAssignable = true };
        authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Templates = [.. authorization.Templates, reader, decider] }, authorization.Revision, default);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        async Task<MaterialCodeApplication> Pending(Guid projectId) => await materials.CreateMaterialCodeApplicationAsync(
            new(Guid.NewGuid(), projectId, Guid.NewGuid(), MaterialCodeApplicationStatus.Pending, "admin", DateTimeOffset.UtcNow, null, null, null, null, null, 1)
            { ApprovalWorkflow = new("authorization-test", 1, [new(Guid.NewGuid(), 1, ApprovalStage.ProcessReview, "料号审批", [actor])], []) }, default);
        Task<HttpResponseMessage> Reject(MaterialCodeApplication application) => client.PostAsJsonAsync($"/api/material-code/applications/{application.Id}/decision",
            new { expectedRowVersion = 1, approved = false, comment = "隔离验收退回" });
        async Task AssertPending(MaterialCodeApplication application) => Assert.Equal(MaterialCodeApplicationStatus.Pending,
            (await materials.FindMaterialCodeApplicationAsync(application.Id, default))!.Status);
        var first = await Pending(project.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reject(first)).StatusCode);
        await AssertPending(first);
        var listed = (await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications"))!;
        Assert.False(listed.Single(item => item.GetProperty("id").GetGuid() == first.Id).GetProperty("canDecide").GetBoolean());
        var assignment = new AuthorizationAssignment(Guid.NewGuid(), actor, decider.Key, company, "project", project.Id);
        authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Assignments = [.. authorization.Assignments, assignment] }, authorization.Revision, default);
        var allowed = await Reject(first);
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(MaterialCodeApplicationStatus.Rejected, (await materials.FindMaterialCodeApplicationAsync(first.Id, default))!.Status);
        foreach (var blockedProject in new[] { other, foreign })
        {
            var blocked = await Pending(blockedProject.Id);
            Assert.Equal(HttpStatusCode.Forbidden, (await Reject(blocked)).StatusCode);
            await AssertPending(blocked);
        }
        authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Assignments = authorization.Assignments.Where(item => item.Id != assignment.Id).ToArray() }, authorization.Revision, default);
        var revoked = await Pending(project.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Reject(revoked)).StatusCode);
        await AssertPending(revoked);
    }
}
