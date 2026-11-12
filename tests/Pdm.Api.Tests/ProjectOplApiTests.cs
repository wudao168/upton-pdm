using System.IdentityModel.Tokens.Jwt;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectOplApiTests
{
    [Fact]
    public async Task AssignmentTodoFollowsTheIssueAndAuthorizedUsersCanFillTheResponse()
    {
        await using var host = new PdmApiFactory();
        await using var factory = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IOplSpeechRecognizer, TestSpeechRecognizer>()));
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "待办测试项目", null, null, 1, "opl-manager", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var child = await repo.CreateSubprojectAsync(new(project.Id, "待办测试子项目", null, 1), default);
        var otherProject = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "未授权测试项目", null, null, 1, "opl-manager", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        string[] names = ["opl-manager", "opl-owner", "opl-replacement", "opl-colleague"];
        foreach (var name in names)
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, "OPL测试人员", "unused", UserRole.Engineer, true, RoleCode: name, CompanyId: company), default);
        string[] projectGrants = [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("opl"),
            ProjectCenterPermissions.Write, "project.opl.view", "project.opl.edit"];
        var roles = names.Select(name => new AuthorizationTemplate(name, name, "",
            new[] { new AuthorizationGrant(AuthorizationActions.BusinessNotifications, ["company"]) }) { RoleCode = name }).ToArray();
        var worker = new AuthorizationTemplate("opl-worker", "OPL项目人员", "", projectGrants.Select(code => new AuthorizationGrant(code, ["company"])).ToArray()) { ProjectAssignable = true };
        var manager = worker with { Key = "opl-management", Name = "OPL项目经理",
            Grants = worker.Grants.Concat([new("project.opl.manage", ["company"]), new("project.opl.create", ["company"])]).ToArray() };
        settings = await repo.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Concat(roles).Concat([worker, manager]).ToArray(),
            Assignments = names.Select(name => new AuthorizationAssignment(Guid.NewGuid(), name, name == "opl-manager" ? manager.Key : worker.Key, company, "project", project.Id)).ToArray()
        }, settings.Revision, default);
        void Login(string name)
        {
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, name), new(ClaimTypes.Role, "Engineer")],
                expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        static MultipartFormDataContent Body(object data)
        {
            var form = new MultipartFormDataContent();
            form.Add(JsonContent.Create(data), "data");
            return form;
        }
        async Task<UserNotification[]> Todos() => (await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!
            .Where(item => item.Category == ProjectOplService.AssignmentCategory).ToArray();
        async Task<HttpResponseMessage> Transcribe(string path, byte[]? wave = null)
        {
            using var content = new ByteArrayContent(wave ?? TestWave());
            content.Headers.ContentType = new("audio/wav");
            return await client.PostAsync(path + "/speech/transcribe", content);
        }

        Login("opl-manager");
        var path = $"/api/projects/{project.Id}/opl";
        using var speech = await Transcribe(path);
        Assert.True(speech.IsSuccessStatusCode, await speech.Content.ReadAsStringAsync());
        Assert.Equal("现场支架松动。", (await speech.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Transcribe(path, [0, 1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Transcribe($"/api/projects/{otherProject.Id}/opl")).StatusCode);
        var input = new OplInput("设备问题", "普通", "待处理", "opl-owner", new(2026, 11, 30), "", 0, []);
        using var createForm = Body(input);
        createForm.Add(new ByteArrayContent(TestWave()), "files", "OPL录音-测试.wav");
        using var created = await client.PostAsync(path, createForm);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var issue = (await created.Content.ReadFromJsonAsync<OplIssue>())!;
        input = input with { AttachmentIds = [Assert.Single(issue.Attachments).Id] };
        path += $"/{issue.Id}";
        Login("opl-owner");
        Assert.Equal(HttpStatusCode.OK, (await Transcribe(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Transcribe($"/api/projects/{project.Id}/opl")).StatusCode);
        using var playback = await client.GetAsync($"{path}/attachments/{issue.Attachments[0].Id}?preview=true");
        Assert.Equal(HttpStatusCode.OK, playback.StatusCode);
        Assert.Equal("audio/wav", playback.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestWave(), await playback.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{child.Id}/opl/{issue.Id}/attachments/{issue.Attachments[0].Id}?preview=true")).StatusCode);
        var todo = Assert.Single(await Todos());
        Assert.Equal(ProjectOplService.AssignmentKey(issue.Id), todo.SourceKey);
        Assert.Null(todo.ResolvedAt);
        Assert.True((await client.GetFromJsonAsync<OplView>($"/api/projects/{project.Id}/opl"))!.Rows.Single().CanEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync($"/api/projects/{otherProject.Id}/opl/{issue.Id}", Body(input with { ExpectedRowVersion = 1 }))).StatusCode);

        Login("opl-colleague");
        Assert.Empty(await Todos());
        // 指派决定待办归属；实际编辑和转写能力由权限中心授权。
        Assert.Equal(HttpStatusCode.OK, (await Transcribe(path)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<OplView>($"/api/projects/{project.Id}/opl"))!.Rows.Single().CanEdit);
        Login("opl-manager");
        var reassigned = input with { Owner = "opl-replacement", ExpectedRowVersion = 1 };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(path, Body(reassigned))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync(path, Body(reassigned))).StatusCode);
        Login("opl-owner");
        Assert.NotNull(Assert.Single(await Todos()).ResolvedAt);
        Assert.True((await client.GetFromJsonAsync<OplView>($"/api/projects/{project.Id}/opl"))!.Rows.Single().CanEdit);

        Login("opl-replacement");
        Assert.Null(Assert.Single(await Todos()).ResolvedAt);
        var response = reassigned with { ExpectedRowVersion = 2, Solution = "更换支架", ExpectedCompletionDate = new(2026, 11, 20) };
        using var filled = await client.PutAsync(path, Body(response));
        Assert.True(filled.IsSuccessStatusCode, await filled.Content.ReadAsStringAsync());
        var saved = (await filled.Content.ReadFromJsonAsync<OplIssue>())!;
        Assert.Equal(input.DueDate, saved.DueDate);
        Assert.Equal(response.ExpectedCompletionDate, saved.ExpectedCompletionDate);
        Assert.NotNull(Assert.Single(await Todos()).ResolvedAt);

        Login("opl-manager");
        var adjustment = response with { ExpectedRowVersion = 3, ExpectedCompletionDate = new(2026, 11, 21) };
        using var adjusted = await client.PutAsync(path, Body(adjustment));
        Assert.Equal(adjustment.ExpectedCompletionDate, (await adjusted.Content.ReadFromJsonAsync<OplIssue>())!.ExpectedCompletionDate);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Assignments = settings.Assignments.Where(item => item.Username != "opl-replacement").ToArray() }, settings.Revision, default);
        Login("opl-replacement");
        Assert.Empty(await Todos());
        Assert.Equal(HttpStatusCode.Forbidden, (await Transcribe(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, Body(adjustment with { ExpectedRowVersion = 4 }))).StatusCode);
    }

    internal static byte[] TestWave()
    {
        var wave = new byte[46];
        "RIFF"u8.CopyTo(wave);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        "data"u8.CopyTo(wave.AsSpan(36));
        foreach (var (offset, value) in new[] { (4, 38), (16, 16), (20, 0x00010001), (24, 16000), (28, 32000), (32, 0x00100002), (40, 2) })
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(offset, 4), value);
        return wave;
    }

    private sealed class TestSpeechRecognizer : IOplSpeechRecognizer
    {
        public Task<string?> RecognizeAsync(byte[] wave, CancellationToken ct) => Task.FromResult<string?>("现场支架松动。");
    }

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
                settings.CompanyId = Guid.Parse("70000000-0000-0000-0000-000000000001"); settings.PublicUrl = "http://192.168.2.8:5173";
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
