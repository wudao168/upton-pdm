using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectOplViewStateTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => {
                foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Namespace == "Upton.Pdm.Api").ToArray()) services.Remove(registration);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
        }
    }
    private static HttpClient Client(Factory factory, string username)
    {
        var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
    private static async Task<bool> IsNew(HttpClient client, Guid projectId, Guid id)
    {
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/opl");
        return view.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("issue").GetProperty("id").GetGuid() == id).GetProperty("isNew").GetBoolean();
    }
    [Fact]
    public async Task ReadReceiptFollowsAccountAcrossClientsWithoutChangingIssueOrResettingOnEdits()
    {
        using var factory = new Factory();
        using var client = Client(factory, "admin");
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        var service = scope.ServiceProvider.GetRequiredService<ProjectOplService>();
        var issue = await service.SaveAsync(project.Id, null, new("新增标记测试", "普通", "待处理", "", null, "", 0, []), [], "admin", UserRole.Administrator, default);
        using var reader = Client(factory, "reader");
        Assert.True(await IsNew(reader, project.Id, issue.Id));
        Assert.False(await IsNew(client, project.Id, issue.Id));
        var path = $"/api/projects/{project.Id}/opl/{issue.Id}/view";
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync(path, null)).StatusCode);
        using var otherClient = Client(factory, "READER");
        using var otherUser = Client(factory, "someone-else");
        Assert.False(await IsNew(otherClient, project.Id, issue.Id));
        Assert.True(await IsNew(otherUser, project.Id, issue.Id));
        var unchanged = (await service.GetAsync(project.Id, "admin", UserRole.Administrator, default)).Rows.Single().Issue;
        Assert.Equal(issue, unchanged);
        await service.SaveAsync(project.Id, issue.Id, new("修改内容", "高", "处理中", "", null, "已有处理措施", issue.RowVersion, []), [], "admin", UserRole.Administrator, default);
        Assert.False(await IsNew(otherClient, project.Id, issue.Id));
    }
    [Fact]
    public async Task AcknowledgementRejectsMissingIssuesAndSiblingProjectScope()
    {
        using var factory = new Factory();
        using var client = Client(factory, "admin");
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var root = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "标记范围测试", null, null, 1, "admin", "D:/test", "D:/release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        var sibling = await repository.CreateSubprojectAsync(new(root.Id, "同级子项目", null, 1), default);
        var issue = await scope.ServiceProvider.GetRequiredService<ProjectOplService>().SaveAsync(root.Id, null, new("子项目问题", "普通", "待处理", "", null, "", 0, [], child.Id), [], "admin", UserRole.Administrator, default);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{sibling.Id}/opl/{issue.Id}/view", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{root.Id}/opl/{Guid.NewGuid()}/view", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/projects/{child.Id}/opl/{issue.Id}/view", null)).StatusCode);
    }
    [Fact]
    public async Task UnauthenticatedClientsCannotReadOrAcknowledge()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var path = $"/api/projects/{Guid.NewGuid()}/opl";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"{path}/{Guid.NewGuid()}/view", null)).StatusCode);
    }
}
