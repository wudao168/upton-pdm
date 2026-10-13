using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectOplApiTests
{
    [Fact]
    public async Task MultipartCreateUpdateValidationAndDisabledIntegration()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        const string path = "/api/projects/11111111-1111-1111-1111-111111111111/opl";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        static MultipartFormDataContent Body(OplInput input)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))), "data");
            return form;
        }
        var input = new OplInput("接口测试问题", "普通", "待处理", "", null, "", 0, []);
        using var create = await client.PostAsync(path, Body(input));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var issue = (await create.Content.ReadFromJsonAsync<OplIssue>())!;
        Assert.Equal("admin", issue.CreatedBy);
        var completion = await client.PostAsJsonAsync($"{path}/{issue.Id}/complete", new { expectedRowVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var completed = (await completion.Content.ReadFromJsonAsync<OplIssue>())!;
        Assert.Equal("已完成", completed.Status); Assert.NotNull(completed.CompletedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{issue.Id}/complete", new { expectedRowVersion = 1 })).StatusCode);
        using var update = await client.PutAsync($"{path}/{issue.Id}", Body(input with { ExpectedRowVersion = 2, Solution = "处理完成", Status = "已完成" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.NotNull((await update.Content.ReadFromJsonAsync<OplIssue>())!.CompletedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"{path}/{issue.Id}", Body(input))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, Body(input with { Description = "" }))).StatusCode);
        var integration = await client.GetFromJsonAsync<JsonElement>($"{path}/integrations/dingtalk");
        Assert.False(integration.GetProperty("enabled").GetBoolean());
        Assert.Single((await client.GetFromJsonAsync<OplView>(path))!.Rows);
        var lightweight = (await client.GetFromJsonAsync<OplView>(path + "?includePeople=false"))!;
        Assert.Single(lightweight.Rows); Assert.Empty(lightweight.People); Assert.Empty(lightweight.Subprojects);
        var summary = (await client.GetFromJsonAsync<ProjectWorkbench>("/api/workbench?scope=company&summaryOnly=true"))!;
        var overviewProject = Assert.Single(summary.Projects, item => item.Project.Id == issue.ProjectId);
        Assert.Equal(0, overviewProject.OplOpenCount); // completed issues are not counted as open.
        Assert.Null(overviewProject.Budget); Assert.Null(overviewProject.Procurement); Assert.Null(overviewProject.Quality);
        var details = (await client.GetFromJsonAsync<ProjectWorkbench>($"/api/workbench?scope=company&projectId={issue.ProjectId}"))!;
        Assert.All(details.Projects, item => Assert.True(item.Project.Id == issue.ProjectId || item.Project.RootProjectId == issue.ProjectId || item.Project.ParentProjectId == issue.ProjectId));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workbench?scope=company&projectId={Guid.NewGuid()}")).StatusCode);
    }
}
