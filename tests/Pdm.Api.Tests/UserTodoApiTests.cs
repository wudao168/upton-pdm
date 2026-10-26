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

public sealed class UserTodoApiTests
{
    private static string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, "Engineer")], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
    [Fact]
    public async Task CalendarTodo_IsSharedWithTheExistingInbox_AndPrivateAgainstOtherAdmins()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        foreach (var actor in new[] { "todo-creator", "todo-recipient", "todo-outsider" })
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), actor, actor, "unused", UserRole.Engineer, true), default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("todo-creator"));
        var response = await client.PostAsJsonAsync("/api/todos", new { content = "仅接收人可见的资料跟进", dueDate = "2026-10-16", recipientUsernames = new[] { "todo-recipient" } });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<UserNotification>())!;
        Assert.Equal("ProjectTodo", created.Category);
        Assert.Null(created.ProjectId);
        var creatorTodos = (await client.GetFromJsonAsync<UserNotification[]>("/api/calendar-todos?from=2026-10-01&to=2026-11-30"))!;
        Assert.Single(creatorTodos);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("todo-recipient"));
        Assert.Single((await client.GetFromJsonAsync<UserNotification[]>("/api/calendar-todos?from=2026-10-01&to=2026-11-30"))!);
        var inbox = await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine");
        Assert.Contains(inbox!, item => item.SourceKey == created.SourceKey && item.Category == "ProjectTodo");
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("todo-outsider"));
        Assert.Empty((await client.GetFromJsonAsync<UserNotification[]>("/api/calendar-todos?from=2026-10-01&to=2026-11-30"))!);
        Assert.DoesNotContain((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!, item => item.SourceKey == created.SourceKey);
        var invalid = await client.PostAsJsonAsync("/api/todos", new { content = "测试", recipientUsernames = new[] { "not-a-user" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
