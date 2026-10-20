using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectOplApiTests
{
    [Fact]
    public async Task HttpOplHistoryUsesPublishedPackagesAndNewReleaseContractValidatesAssociations()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectOplService>();
        var issue = await service.SaveAsync(project.Id, null, new("发布关联接口测试", "普通", "待处理", "", null, "", 0, []), [], "admin", UserRole.Administrator, default);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-HTTP-OPL", ReleasePackageState.Published,
            null, "M", "E", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test")
            { Scope = ReleaseScope.NonStandardSupplement, RelatedOplIssueIds = [issue.Id], DrawingDocumentCount = 5 };
        await repository.CreateReleasePackageAsync(package, default);
        var view = (await client.GetFromJsonAsync<OplView>($"/api/projects/{project.Id}/opl"))!;
        var history = Assert.Single(view.Rows.Single(row => row.Issue.Id == issue.Id).Releases);
        Assert.Equal(package.Id, history.ReleasePackageId);
        Assert.Equal(5, history.DrawingCount);
        var invalid = await client.PostAsJsonAsync("/api/release-packages", new CreateReleasePackageRequest(project.Id,
            Scope: ReleaseScope.StandardFormal, SelectedBomItemIds: [], RelatedOplIssueIds: [issue.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("只有增补/变更", await invalid.Content.ReadAsStringAsync());
    }

    private sealed class NotificationHandler : HttpMessageHandler
    {
        public List<string> Cards { get; } = [];
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/oauth2/accessToken") return new(HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"test\"}") };
            Cards.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(Fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent("{\"processQueryKey\":\"accepted\"}") };
        }
    }
    [Fact]
    public async Task UpdatesAndCompletionNotifyOnlyWhenRequestedAfterSuccessfulSave()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var handler = new NotificationHandler();
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.Configure<DingTalkOplOptions>(settings => {
                settings.Enabled = true; settings.CorpId = "corp"; settings.AppKey = "app"; settings.AppSecret = "secret"; settings.RobotCode = "robot";
                settings.CompanyId = Guid.NewGuid(); settings.PublicUrl = "http://192.168.2.8:5173";
                settings.Projects[projectId] = new() { OpenConversationId = "cid" };
            });
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => handler);
        }));
        using var client = factory.CreateClient();
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name,"admin"),new(ClaimTypes.Role,"Administrator")], expires:DateTime.UtcNow.AddMinutes(10),
            signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer",new JwtSecurityTokenHandler().WriteToken(jwt));
        var path = $"/api/projects/{projectId}/opl";
        static MultipartFormDataContent Body(OplInput input) { var form = new MultipartFormDataContent(); form.Add(JsonContent.Create(input),"data"); return form; }
        var input = new OplInput("更新通知测试", "普通", "待处理", "", null, "", 0, []);
        var issue = (await (await client.PostAsync(path,Body(input))).Content.ReadFromJsonAsync<OplIssue>())!;
        Assert.Single(handler.Cards);
        await client.PutAsync($"{path}/{issue.Id}",Body(input with {ExpectedRowVersion=1,Priority="高"}));
        Assert.Single(handler.Cards);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsync($"{path}/{issue.Id}",Body(input with {ExpectedRowVersion=1,NotifyGroup=true}))).StatusCode);
        Assert.Single(handler.Cards);
        var updated = await client.PutAsync($"{path}/{issue.Id}",Body(input with {ExpectedRowVersion=2,Priority="高",Status="处理中",NotifyGroup=true}));
        Assert.True((await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("notification").GetProperty("sent").GetBoolean());
        Assert.Equal(2,handler.Cards.Count);
        using var updatedCard = JsonDocument.Parse(JsonDocument.Parse(handler.Cards[1]).RootElement.GetProperty("cardData").GetString()!);
        Assert.Equal("OPL 问题已更新",updatedCard.RootElement.GetProperty("header").GetProperty("title").GetProperty("text").GetString());
        handler.Fail = true;
        var completed = await client.PostAsJsonAsync($"{path}/{issue.Id}/complete",new {expectedRowVersion=3,notifyGroup=true});
        Assert.Equal(HttpStatusCode.OK,completed.StatusCode);
        var result = await completed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("已完成",result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("notification").GetProperty("sent").GetBoolean());
        Assert.Equal("已完成",(await client.GetFromJsonAsync<OplView>(path))!.Rows.Single().Issue.Status);
    }
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
