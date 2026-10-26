using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class DingTalkConfigurationTests
{
    [Fact]
    public async Task ProjectCommandBindsOnlyItsProjectAndCannotBeReusedOrOverwriteAnotherBinding()
    {
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => new BotHandler())));
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "command-admin", "项目管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("command-admin", company, false, [], "command-admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("command-admin", "Administrator"));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/dingtalk", new { enabled = true, corpId = "test-corp", appKey = "test-key", appSecret = "private-app-secret", robotCode = "robot-test", publicUrl = "http://192.168.2.8:5173", expectedRowVersion = 0 })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DingTalkConfigurationService>();
        async Task<string> Command(Guid project) => (await (await client.PostAsync($"/api/projects/{project}/dingtalk/binding-command", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("command").GetString()!;
        static JsonElement Message(string command, string conversation = "cid-direct", string robot = "robot-test") => JsonSerializer.SerializeToElement(new { conversationType = "2", chatbotCorpId = "test-corp", robotCode = robot, conversationId = conversation, conversationTitle = "目标项目群", text = new { content = command } });
        var oldCommand = await Command(RootId);
        var command = await Command(RootId);
        Assert.StartsWith("绑定项目 ", command);
        Assert.DoesNotContain("@UPLM", command);
        Assert.NotEqual(oldCommand, command);
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(oldCommand), default));
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(command, robot: "wrong-robot"), default));
        Assert.Null(await service.BindProjectGroupAsync(Guid.NewGuid(), "test-corp", "test-key", "robot-test", Message(command), default));
        var bound = await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(command), default);
        Assert.NotNull(bound);
        Assert.Null(TenantContext.Current);
        var rootView = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{RootId}/dingtalk");
        Assert.Equal(bound, rootView.GetProperty("groupId").GetGuid());
        Assert.Single(rootView.GetProperty("groups").EnumerateArray());
        Assert.Contains("直接绑定", rootView.GetProperty("bindingMessage").GetString());
        Assert.True((await scope.ServiceProvider.GetRequiredService<DingTalkOplIntegration>().PublishEntryAsync(bound.Value, default, company)).Sent);
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(command, "cid-replay"), default));

        var second = await repository.CreateProjectAsync(new CreateProjectCommand("DING-DIRECT-2", "另一个项目", "command-admin", "unused", "unused"), "command-admin", default);
        var secondCommand = await Command(second.Id);
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(secondCommand), default));
        var secondView = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{second.Id}/dingtalk");
        Assert.Equal(JsonValueKind.Null, secondView.GetProperty("groupId").ValueKind);
        Assert.Empty(secondView.GetProperty("groups").EnumerateArray());
        Assert.Contains("其他主项目", secondView.GetProperty("bindingMessage").GetString());
        var rootCommand = await Command(RootId);
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(rootCommand, "cid-other-group"), default));
        rootView = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{RootId}/dingtalk");
        Assert.Equal(bound, rootView.GetProperty("groupId").GetGuid());
        Assert.Contains("解除原绑定", rootView.GetProperty("bindingMessage").GetString());

        var cancelledCommand = await Command(RootId);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/projects/{RootId}/dingtalk", new { groupId = (Guid?)null, expectedRowVersion = rootView.GetProperty("rowVersion").GetInt64() })).StatusCode);
        Assert.Null(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message(cancelledCommand, "cid-cancelled"), default));
        rootView = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{RootId}/dingtalk");
        Assert.Equal(JsonValueKind.Null, rootView.GetProperty("groupId").ValueKind);
        Assert.Empty(rootView.GetProperty("groups").EnumerateArray());
        command = await Command(RootId);
        Assert.NotNull(await service.BindProjectGroupAsync(company, "test-corp", "test-key", "robot-test", Message("@UPLM " + command, "cid-corrected"), default));
        await repository.CreateUserAsync(new(Guid.NewGuid(), "command-reader", "普通用户", "unused", UserRole.Engineer, true), default);
        await repository.SetUserCompanyScopeAsync("command-reader", company, false, [], "command-admin", default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("command-reader", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/projects/{RootId}/dingtalk/binding-command", null)).StatusCode);
    }

    private sealed class BindingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task BindingCodesExpireInvalidatePreviousCodesAndAllowOnlyOneConsumer()
    {
        var clock = new BindingClock();
        var codes = new DingTalkProjectBindingCodes(clock);
        var tenant = new CurrentTenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "manager", "Engineer", false, new HashSet<string>());
        var old = codes.Issue(RootId, "P700014", tenant, "app", "robot");
        var current = codes.Issue(RootId, "P700014", tenant, "app", "robot");
        Assert.Null(codes.Take(tenant.CompanyId, "P700014", old.Code));
        Assert.Null(codes.Take(Guid.NewGuid(), "P700014", current.Code));
        Assert.Null(codes.Take(tenant.CompanyId, "P700015", current.Code));
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => codes.Take(tenant.CompanyId, "P700014", current.Code))));
        Assert.Single(results, item => item is not null);
        var expired = codes.Issue(RootId, "P700014", tenant, "app", "robot");
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Null(codes.Take(tenant.CompanyId, "P700014", expired.Code));
        Assert.Contains("过期", codes.Message(tenant.CompanyId, RootId));
    }
}
