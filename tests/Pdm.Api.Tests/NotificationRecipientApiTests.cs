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

public sealed class NotificationRecipientApiTests
{
    private static string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task MixedAdministratorsReceiveTheirBusinessTasksWithoutAdministrativeFallbackTasks()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var approverRole = (await repository.GetSystemSettingsAsync(default)).MaterialCodeApproval.ApproverRoleCodes.First(role => role != "Administrator");
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mixed-engineer", "兼任工程师", "unused", UserRole.Administrator, true, RoleCodes: ["Administrator", "Engineer"]), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mixed-approver", "兼任审批人", "unused", UserRole.Administrator, true, RoleCodes: ["Administrator", approverRole]), default);
        await repository.CreateUserNotificationsAsync([
            new(Guid.NewGuid(), "mixed-engineer", "System", "系统维护", "系统通知", null, null, "system-status", DateTimeOffset.UtcNow, null),
            new(Guid.NewGuid(), "mixed-engineer", "ProjectTodo", "工程任务", "分配给自己的业务事项", project.Id, null, "business-task", DateTimeOffset.UtcNow, null)
        ], default);
        var legacy = await materials.CreateMaterialCodeApplicationAsync(new(Guid.NewGuid(), project.Id, Guid.NewGuid(),
            MaterialCodeApplicationStatus.Pending, "other", DateTimeOffset.UtcNow, null, null, null, null, null, 1), default);
        async Task<MaterialCodeApplication> Application(string assignee, string emergencyRole) =>
            await materials.CreateMaterialCodeApplicationAsync(new(Guid.NewGuid(), project.Id, Guid.NewGuid(),
                MaterialCodeApplicationStatus.Pending, "other", DateTimeOffset.UtcNow, null, null, null, null, null, 1)
            { ApprovalWorkflow = new("mixed-test", 1, [new(Guid.NewGuid(), 1, ApprovalStage.ProcessReview, "业务审批", [assignee])], [emergencyRole]) }, default);
        var assigned = await Application("mixed-engineer", "Administrator");
        var administratorOnly = await Application("other", "Administrator");
        var businessEmergency = await Application("other", "Engineer");

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("mixed-engineer"));
        Assert.Equal(2, (await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!.Length);
        var engineerTasks = (await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!;
        Assert.Equal(new[] { assigned.Id, businessEmergency.Id }.Order(), engineerTasks.Select(item => item.GetProperty("id").GetGuid()).Order());
        Assert.False(engineerTasks.Single(item => item.GetProperty("id").GetGuid() == assigned.Id).GetProperty("canEmergencyDecide").GetBoolean());
        var management = (await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications"))!;
        Assert.True(management.Single(item => item.GetProperty("id").GetGuid() == legacy.Id).GetProperty("canDecide").GetBoolean());
        Assert.True(management.Single(item => item.GetProperty("id").GetGuid() == administratorOnly.Id).GetProperty("canEmergencyDecide").GetBoolean());

        var own = await repository.RegisterDocumentAsync(new(project.Id, "MIXED-OWN", "自己的零件", "MIXED-OWN.SLDPRT", DocumentKind.Part), "admin", default);
        var other = await repository.RegisterDocumentAsync(new(project.Id, "MIXED-OTHER", "其他人的零件", "MIXED-OTHER.SLDPRT", DocumentKind.Part), "admin", default);
        await repository.CheckoutAsync(own.Id, "mixed-engineer", Guid.NewGuid(), "工程电脑", DateTimeOffset.UtcNow.AddMinutes(10), default);
        await repository.CheckoutAsync(other.Id, "admin", Guid.NewGuid(), "其他电脑", DateTimeOffset.UtcNow.AddMinutes(10), default);
        Assert.Equal(own.Id, Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/edit-locks"))!).GetProperty("documentId").GetGuid());

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("mixed-approver"));
        Assert.Equal(legacy.Id, Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task SystemAdministratorInboxContainsOnlySystemMessages_WithoutRemovingBusinessManagementAccess()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        foreach (var actor in new[] { "system-recipient", "business-recipient" })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), actor, actor, "unused", UserRole.Administrator, true,
                RoleCode: actor == "system-recipient" ? "Administrator" : "developer"), default);
            await repository.CreateUserNotificationsAsync([
                new(Guid.NewGuid(), actor, "System", "系统维护通知", "系统连接恢复", null, null, "system-status", DateTimeOffset.UtcNow, null),
                new(Guid.NewGuid(), actor, "ReleasePreviewResult", "业务发布结果", "发布已完成", project.Id, null, "business-result", DateTimeOffset.UtcNow, null)
            ], default);
        }
        var application = await materials.CreateMaterialCodeApplicationAsync(new(Guid.NewGuid(), project.Id, Guid.NewGuid(),
            MaterialCodeApplicationStatus.Pending, "business-recipient", DateTimeOffset.UtcNow, null, null, null, null, null, 1), default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("system-recipient"));
        Assert.Equal("System", Assert.Single((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!).Category);
        foreach (var endpoint in new[] { "/api/approval-tasks/mine", "/api/validation-plan-approval-tasks/mine", "/api/quality/validation-plan-approval-tasks/mine", "/api/program-templates/tasks/mine", "/api/edit-locks", "/api/material-code/applications?status=Pending" })
            Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(endpoint))!);
        Assert.Contains((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications"))!, item => item.GetProperty("id").GetGuid() == application.Id);

        var catalog = (await client.GetFromJsonAsync<WorkflowNotificationCatalog>("/api/workflow-notifications"))!;
        var invalidRole = await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release",
            new SaveWorkflowNotification(true, [new("CompanyRole", null, RoleCode: "Administrator")], catalog.RowVersion, catalog.CompanyId));
        Assert.Equal(HttpStatusCode.BadRequest, invalidRole.StatusCode);
        Assert.Contains("系统管理员角色不用于业务通知", await invalidRole.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("business-recipient"));
        Assert.Equal(2, (await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!.Length);
        Assert.Contains((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!, item => item.GetProperty("id").GetGuid() == application.Id);
    }
}
