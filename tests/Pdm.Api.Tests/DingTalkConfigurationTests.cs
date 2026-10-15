using Microsoft.AspNetCore.DataProtection;
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

public sealed class DingTalkConfigurationTests
{
    private sealed class BotHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(request.RequestUri!.AbsolutePath=="/v1.0/oauth2/accessToken" ? "{\"accessToken\":\"fake\"}" : "{\"processQueryKey\":\"accepted\"}")});
    }
    private static readonly Guid RootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static string Token(string username, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
    [Fact]
    public async Task SelfServiceConfigurationEncryptsSecretsAndPreventsSharedGroupsWithOptimisticConcurrency()
    {
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => new BotHandler())));
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Where(x => x.IsActive).ToArray();
        var company = companies[0].Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator")); client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/settings/dingtalk");
        Assert.Equal(0, initial.GetProperty("rowVersion").GetInt64());
        var input = new { enabled = true, corpId = "test-corp", appKey = "test-key", robotCode = "robot-test", appSecret = "private-app-secret", publicUrl = "http://192.168.2.8:5173", users = new[] { new { dingTalkUserId = "ding-admin", username = "admin" } }, expectedRowVersion = 0 };
        var savedResponse = await client.PutAsJsonAsync("/api/settings/dingtalk", input);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var text = await savedResponse.Content.ReadAsStringAsync(); Assert.DoesNotContain("private-app-secret", text);
        var saved = JsonSerializer.Deserialize<JsonElement>(text); Assert.True(saved.GetProperty("hasAppSecret").GetBoolean());
        Assert.Equal($"http://192.168.2.8:5173/?dingtalkOpl=1&companyId={company}", saved.GetProperty("entryUrl").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/settings/dingtalk", input)).StatusCode);
        var configs = factory.Services.GetRequiredService<IDingTalkConfigurationRepository>();
        var stored = (await configs.FindAsync(company, default))!;
        Assert.DoesNotContain("private-app-secret", stored.ProtectedPayload); Assert.DoesNotContain("ding-admin", stored.ProtectedPayload);
        using var scope=factory.Services.CreateScope();
        var discovery=scope.ServiceProvider.GetRequiredService<DingTalkConfigurationService>();
        var eventBody=JsonSerializer.SerializeToElement(new {conversationType="2",chatbotCorpId="test-corp",robotCode="robot-test",conversationId="cid-shared",conversationTitle="共享测试群",text=new {content=" 登记项目群 "}});
        Assert.False(await discovery.RegisterGroupAsync(company,"other-corp","test-key","robot-test",eventBody,default));
        Assert.False(await discovery.RegisterGroupAsync(company,"test-corp","test-key","other-robot",eventBody,default));
        Assert.True(await discovery.RegisterGroupAsync(company,"test-corp","test-key","robot-test",eventBody,default));
        Assert.True(await discovery.RegisterGroupAsync(company,"test-corp","test-key","robot-test",eventBody,default));
        var discovered=await client.GetFromJsonAsync<JsonElement>(" /api/settings/dingtalk".Trim());
        Assert.Equal(2,discovered.GetProperty("rowVersion").GetInt64());
        var groupId=discovered.GetProperty("groups")[0].GetProperty("id").GetGuid();
        Assert.True(discovered.GetProperty("groups")[0].GetProperty("registered").GetBoolean());
        var boundResponse=await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk",new {groupId,expectedRowVersion=2});
        Assert.Equal(HttpStatusCode.OK,boundResponse.StatusCode);
        Assert.Contains("提交入口卡片已发到群内",(await boundResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entryMessage").GetString());
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync($"/api/projects/{RootId}/dingtalk/entry",null)).StatusCode);
        var second = await repository.CreateProjectAsync(new CreateProjectCommand("DING-SECOND", "第二项目", "admin", "unused", "unused"), "admin", default);
        var duplicate = await client.PutAsJsonAsync($"/api/projects/{second.Id}/dingtalk", new { groupId, expectedRowVersion = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var root = (await repository.FindProjectAsync(RootId, default))!;
        Assert.Contains(root.Code, await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{second.Id}/dingtalk")).GetProperty("groupId").ValueKind);
        var selected = await client.GetFromJsonAsync<JsonElement>($"/api/integrations/dingtalk/opl/projects?groupId={groupId}");
        Assert.Equal(1, selected.GetArrayLength());
        var shared = await client.GetFromJsonAsync<JsonElement>("/api/settings/dingtalk");
        Assert.Equal(1, shared.GetProperty("groups")[0].GetProperty("projectCount").GetInt32());
        var boundProject = Assert.Single(shared.GetProperty("groups")[0].GetProperty("boundProjects").EnumerateArray());
        Assert.Equal(RootId, boundProject.GetProperty("id").GetGuid());
        Assert.Equal(root.Code, boundProject.GetProperty("code").GetString());
        Assert.Equal(root.Name, boundProject.GetProperty("name").GetString());
        Assert.True(shared.GetProperty("hasAppSecret").GetBoolean());
        Assert.Equal("robot-test",shared.GetProperty("robotCode").GetString()); Assert.Equal("cid-shared",shared.GetProperty("groups")[0].GetProperty("openConversationId").GetString());
        var preserved = await client.PutAsJsonAsync("/api/settings/dingtalk", new { input.enabled, input.corpId, input.appKey, appSecret = "", input.publicUrl, input.users, expectedRowVersion = 3 });
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.True((await preserved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasAppSecret").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk", new { groupId = Guid.NewGuid(), expectedRowVersion = 4 })).StatusCode);
        Assert.Equal(groupId, (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{RootId}/dingtalk")).GetProperty("groupId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk", new { groupId, expectedRowVersion = 4 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk", new { expectedRowVersion = 5 })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>($"/api/integrations/dingtalk/opl/projects?groupId={groupId}")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/projects/{second.Id}/dingtalk", new { groupId, expectedRowVersion = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/projects/{second.Id}/dingtalk", new { groupId, expectedRowVersion = 6 })).StatusCode);
        var bindingOptions = await discovery.LoadAsync(company, default);
        var alias = Guid.NewGuid();
        bindingOptions.Groups[alias] = new() { Name = "同群的旧登记", OpenConversationId = "cid-shared" };
        var protector = factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("Upton.Pdm.DingTalk.Configuration.v1");
        await configs.SaveAsync(new(company, protector.Protect(JsonSerializer.Serialize(bindingOptions, new JsonSerializerOptions(JsonSerializerDefaults.Web))), bindingOptions.RowVersion), 7, default);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk", new { groupId = alias, expectedRowVersion = 8 })).StatusCode);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "other", "普通用户", "unused", UserRole.Engineer, true), default);
        await repository.SetUserCompanyScopeAsync("other", company, false, [], "admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("other", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings/dingtalk")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/projects/{second.Id}/dingtalk", new { groupId, expectedRowVersion = 6 })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        if (companies.Length > 1)
        {
            client.DefaultRequestHeaders.Remove("X-Company-Id"); client.DefaultRequestHeaders.Add("X-Company-Id", companies[1].Id.ToString());
            var other = await client.GetFromJsonAsync<JsonElement>("/api/settings/dingtalk");
            Assert.False(other.GetProperty("hasAppSecret").GetBoolean()); Assert.Empty(other.GetProperty("groups").EnumerateArray());
        }
    }
}
