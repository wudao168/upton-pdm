using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed partial class NotificationRecipientApiTests
{
    [Fact]
    public async Task ProjectActivityEndpointRequiresLoginAndUsesOnlyCurrentRecipient()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/project-activities/mine")).StatusCode);
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var project = (await repo.ListProjectsAsync(default)).First() with { OrganizationId = company };
        ((ConcurrentDictionary<Guid, Project>)typeof(InMemoryPdmRepository).GetField("projects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repo)!)[project.Id] = project;
        foreach (var name in new[] { "activity-user", "activity-other" })
            await repo.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, true, CompanyId: company), default);
        var now = DateTimeOffset.UtcNow;
        await repo.CreateUserNotificationsAsync([new(Guid.NewGuid(), "activity-user", "ProjectActivity.Plan", "项目计划变更已生效", "11/20 → 11/25",
            project.Id, null, "activity-api-test", now, now)], default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("activity-user"));
        var items = (await client.GetFromJsonAsync<ProjectActivityItem[]>("/api/project-activities/mine"))!;
        Assert.Equal("项目计划变更已生效", Assert.Single(items).Title);
        Assert.Empty((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("activity-other"));
        Assert.Empty((await client.GetFromJsonAsync<ProjectActivityItem[]>("/api/project-activities/mine?recipient=activity-user"))!);
    }
}
