using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectApplicationApiTests
{
    private static void Authenticate(HttpClient client)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    [Fact]
    public async Task HttpWorkflowAcceptsNullOrderDateAndPreservesSubmissionAndApproval()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        const string path = "/api/project-applications";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Authenticate(client);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true), default);
        var view = (await client.GetFromJsonAsync<ProjectApplicationView>(path))!;
        Assert.True(view.CanApply); Assert.True(view.CanApprove);
        var person = view.People.First();
        var input = new ProjectApplicationInput(null, "提前启动新客户", "HTTP特殊立项", null, new(2026, 12, 30), person.Username, [person.Username], "合同未签", true);
        var createdResponse = await client.PostAsJsonAsync(path, new SaveProjectApplicationInput(input, 0, true));
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Matches("^LX[0-9]{8}-[0-9]{4}$", created.Number);
        Assert.Null(created.Data.Input.OrderDate);
        Assert.True((await client.GetFromJsonAsync<ProjectApplicationDetail>($"{path}/{created.Id}"))!.CanEdit);
        var revisedInput = input with { ProjectName = "HTTP申请人修改后", DeliveryDate = new(2027, 1, 20) };
        var revisedResponse = await client.PutAsJsonAsync($"{path}/{created.Id}", new SaveProjectApplicationInput(revisedInput, created.RowVersion));
        Assert.Equal(HttpStatusCode.OK, revisedResponse.StatusCode);
        var revised = (await revisedResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(created.Number, revised.Number);
        Assert.Equal("待审批", revised.Status);
        Assert.Equal(2, revised.Revision);
        var finalInput = revisedInput with { ProjectName = "HTTP审批后最终项目", OrderDate = new(2026, 11, 2), Remarks = "审批修改的备注", IsSpecial = false };
        var approval = new ProjectApplicationApprovalInput(revised.RowVersion, view.Customers.First().Id, "P", 2, 1, "0301", null, "批准提前立项", finalInput);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval with { ExpectedRowVersion = created.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/{created.Id}/return", new ProjectApplicationActionInput(revised.RowVersion, ""))).StatusCode);
        var approveResponse = await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approved = (await approveResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.NotNull(approved.ProjectId);
        var project = Assert.Single((await client.GetFromJsonAsync<List<Project>>("/api/projects"))!, x => x.Id == approved.ProjectId);
        Assert.Equal(finalInput.ProjectName, project.Name);
        Assert.Equal(finalInput.OrderDate, project.SignedDate);
        Assert.Equal(finalInput.DeliveryDate, project.DeliveryDate);
        Assert.False(project.IsSpecialInitiation);
        var repeated = (await (await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval)).Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(approved.ProjectId, repeated.ProjectId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{path}/{created.Id}", new SaveProjectApplicationInput(input with { ProjectName = "非法覆盖" }, approved.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{path}/projects/{approved.ProjectId}/order-date", new ProjectOrderDateInput(new(2026, 11, 3), finalInput.OrderDate))).StatusCode);
        var detail = (await client.GetFromJsonAsync<ProjectApplicationDetail>($"{path}/{created.Id}"))!;
        Assert.Equal(3, detail.History.Count);
        Assert.All(detail.History, x => Assert.Null(x.Data.Input.OrderDate));
        Assert.Equal("HTTP特殊立项", detail.History[0].Data.Input.ProjectName);
        Assert.Equal("HTTP申请人修改后", detail.Application.Data.Input.ProjectName);
        Assert.Equal(finalInput.ProjectName, detail.History.Last().ApprovedData!.Input.ProjectName);
        Assert.Equal(finalInput.OrderDate, detail.History.Last().ApprovedData!.Input.OrderDate);
        Assert.Equal("批准提前立项", detail.History.Last().Comment);
        Assert.False(detail.CanEdit);
    }
}
