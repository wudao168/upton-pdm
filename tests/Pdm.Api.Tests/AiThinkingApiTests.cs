using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class AiAssistantApiTests
{
    [Fact]
    public async Task ThinkingWebToolsRetainReasoningButNeverReplayEarlierTurns()
    {
        var web = new FakeWeb(); await using var f = await Fixture.Create(web);
        await using var upstream = await MockModel.Start();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Cloud, AiPermissions.Web);
        await ConfigureWeb(f, upstream.Url);
        var store = (IAiStore)f.Repo; var config = await store.SettingsAsync(f.Company, default);
        await store.SaveSettingsAsync(f.Company, config with { Providers = config.Providers.Select(p => p.Id == f.Provider
            ? p with { Models = p.Models.Select(m => m with { Thinking = true, ReasoningEffort = "low" }).ToArray() } : p).ToArray() }, default);
        var c = await f.Conversation();
        foreach (var question in new[] { "本轮公开问题一", "本轮公开问题二" }) {
            var response = await f.Client.PostAsJsonAsync($"/api/ai/conversations/{c.Id}/messages", new AiSendInput(Guid.NewGuid(), question, f.Model, Web: true));
            Assert.DoesNotContain("event: error", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, web.Searches);
        var last = upstream.Requests.Last(); Assert.DoesNotContain("本轮公开问题一", last.GetRawText());
        var call = last.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("内部思考-0-续段", call.GetProperty("reasoning_content").GetString());
        Assert.Null((await store.FindAsync(f.Company, f.UserId, c.Id, default))!.Messages[^1].ModelContext);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    [InlineData("max")]
    public async Task AdminThinkingAppliesToChatToolsAndHistoryWithoutExposingPrivateContext(string effort)
    {
        await using var upstream = await MockModel.Start(toolRounds: 2);
        await using var f = await Fixture.Create();
        await f.Grant(AiPermissions.Chat, AiPermissions.Settings, AiPermissions.Business, PermissionCodes.ProjectView);
        var config = f.Configuration(upstream.Url);
        config = config with { Providers = [config.Providers[0] with { Models = [new("test-model", "思考模型", Tools: true, Thinking: true, ReasoningEffort: effort)] }] };
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PutAsJsonAsync("/api/ai/settings", config)).StatusCode);
        var savedSettings = await f.Client.GetFromJsonAsync<JsonElement>("/api/ai/settings");
        Assert.Equal(effort, savedSettings.GetProperty("providers")[0].GetProperty("models")[0].GetProperty("reasoningEffort").GetString());
        var test = await f.Client.PostAsJsonAsync($"/api/ai/providers/{f.Provider}/test", new AiTestInput("test-model", true));
        Assert.True(test.IsSuccessStatusCode, await test.Content.ReadAsStringAsync());
        var c = await f.Conversation(); var path = $"/api/ai/conversations/{c.Id}/messages";
        await ((IAiStore)f.Repo).SaveAsync(c with { Messages = [new(Guid.NewGuid(), "user", "之前的问题", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "assistant", "旧非思考回答", DateTimeOffset.UtcNow)] }, default);
        // Per-user attempts to override the administrator's setting are ignored.
        var plain = await f.Client.PostAsJsonAsync(path, new { requestId = Guid.NewGuid(), text = "普通问答", model = f.Model, thinking = false, reasoningEffort = "invalid" });
        var plainText = await plain.Content.ReadAsStringAsync(); Assert.DoesNotContain("event: error", plainText); Assert.DoesNotContain("内部思考", plainText);
        foreach (var question in new[] { "查询项目", "继续核对上次查询" }) {
            var response = await f.Client.PostAsJsonAsync(path, new AiSendInput(Guid.NewGuid(), question, f.Model, true));
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("event: error", body); Assert.DoesNotContain("内部思考", body);
        }
        var latest = upstream.Requests.Last();
        var messages = latest.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(messages, m => m.GetProperty("role").GetString() == "tool");
        foreach (var message in messages.Where(m => m.GetProperty("role").GetString() == "assistant")) {
            if (message.GetProperty("content").GetString() == "旧非思考回答") Assert.Equal("", message.GetProperty("reasoning_content").GetString());
            else Assert.StartsWith("内部思考-", message.GetProperty("reasoning_content").GetString());
        }
        foreach (var request in upstream.Requests) {
            Assert.Equal("enabled", request.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal(effort, request.GetProperty("reasoning_effort").GetString());
        }
        var stored = await ((IAiStore)f.Repo).FindAsync(f.Company, f.UserId, c.Id, default);
        Assert.NotNull(stored!.Messages[^1].ModelContext); Assert.DoesNotContain("内部思考", stored.Messages[^1].ModelContext!);
        var history = await f.Client.GetStringAsync($"/api/ai/conversations/{c.Id}");
        Assert.DoesNotContain("modelContext", history); Assert.DoesNotContain("内部思考", history);
        await f.Grant(AiPermissions.Chat); // Settings cannot be changed after revocation.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.PutAsJsonAsync("/api/ai/settings", config with { Revision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.GetAsync("/api/ai/settings")).StatusCode);
        f.CompanyHeader(f.Other);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.PutAsJsonAsync("/api/ai/settings", config)).StatusCode);
    }

    [Theory]
    [InlineData("deepseek", "medium", false)]
    [InlineData("deepseek", "bad", true)]
    [InlineData("compatible", "high", true)]
    public async Task ThinkingRejectsUnsupportedConfiguration(string adapter, string effort, bool thinking)
    {
        await using var f = await Fixture.Create(); await f.Grant(AiPermissions.Settings);
        var config = f.Configuration("http://127.0.0.1:12345/v1");
        config = config with { Providers = [config.Providers[0] with { Adapter = adapter, Models = [new("test-model", "模拟", Thinking: thinking, ReasoningEffort: effort)] }] };
        var response = await f.Client.PutAsJsonAsync("/api/ai/settings", config);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("思考", await response.Content.ReadAsStringAsync());
    }
}
