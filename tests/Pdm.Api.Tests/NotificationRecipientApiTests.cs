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

public sealed partial class NotificationRecipientApiTests
{
    private static string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task MixedRolesReceiveOnlyTasksAllowedByConfiguredApprovalPermissions()
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
        Assert.Equal(new[] { assigned.Id, administratorOnly.Id, businessEmergency.Id }.Order(), engineerTasks.Select(item => item.GetProperty("id").GetGuid()).Order());
        Assert.True(engineerTasks.Single(item => item.GetProperty("id").GetGuid() == assigned.Id).GetProperty("canEmergencyDecide").GetBoolean());
        var management = (await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications"))!;
        Assert.False(management.Single(item => item.GetProperty("id").GetGuid() == legacy.Id).GetProperty("canDecide").GetBoolean());
        Assert.True(management.Single(item => item.GetProperty("id").GetGuid() == administratorOnly.Id).GetProperty("canEmergencyDecide").GetBoolean());

        var own = await repository.RegisterDocumentAsync(new(project.Id, "MIXED-OWN", "自己的零件", "MIXED-OWN.SLDPRT", DocumentKind.Part), "admin", default);
        var other = await repository.RegisterDocumentAsync(new(project.Id, "MIXED-OTHER", "其他人的零件", "MIXED-OTHER.SLDPRT", DocumentKind.Part), "admin", default);
        await repository.CheckoutAsync(own.Id, "mixed-engineer", Guid.NewGuid(), "工程电脑", DateTimeOffset.UtcNow.AddMinutes(10), default);
        await repository.CheckoutAsync(other.Id, "admin", Guid.NewGuid(), "其他电脑", DateTimeOffset.UtcNow.AddMinutes(10), default);
        var visibleLocks = (await client.GetFromJsonAsync<JsonElement[]>("/api/edit-locks"))!;
        Assert.Contains(visibleLocks, item => item.GetProperty("documentId").GetGuid() == own.Id && item.GetProperty("ownedByCurrentUser").GetBoolean());
        Assert.Contains(visibleLocks, item => item.GetProperty("documentId").GetGuid() == other.Id && !item.GetProperty("ownedByCurrentUser").GetBoolean());

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("mixed-approver"));
        var configuredTasks = (await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!;
        Assert.Equal(new[] { legacy.Id, assigned.Id, administratorOnly.Id }.Order(), configuredTasks.Select(item => item.GetProperty("id").GetGuid()).Order());
        var authorization = await repository.GetAuthorizationSettingsAsync(default);
        await repository.SaveAuthorizationSettingsAsync(authorization with {
            Templates = authorization.Templates.Select(template => template.RoleCode == "Administrator"
                ? template with { Grants = template.Grants.Where(grant => grant.Permission != PermissionCodes.ApprovalEmergencySubstitute).ToArray() } : template).ToArray()
        }, authorization.Revision, default);
        Assert.Equal(legacy.Id, Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!).GetProperty("id").GetGuid());
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("mixed-engineer"));
        Assert.Equal(assigned.Id, Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!).GetProperty("id").GetGuid());
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
                new(Guid.NewGuid(), actor, "ProjectUpdate", "业务发布结果", "发布已完成", project.Id, null, "business-result", DateTimeOffset.UtcNow, null)
            ], default);
        }
        await ApiTestAuthorization.GrantRoleAsync(repository, "developer", PermissionCodes.ApprovalDecide, AuthorizationActions.BusinessNotifications);
        var application = await materials.CreateMaterialCodeApplicationAsync(new(Guid.NewGuid(), project.Id, Guid.NewGuid(),
            MaterialCodeApplicationStatus.Pending, "business-recipient", DateTimeOffset.UtcNow, null, null, null, null, null, 1)
        { ApprovalWorkflow = new("business-inbox-test", 1, [new(Guid.NewGuid(), 1, ApprovalStage.ProcessReview, "业务审批", ["business-recipient"])], []) }, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("system-recipient"));
        Assert.Equal("System", Assert.Single((await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!).Category);
        foreach (var endpoint in new[] { "/api/approval-tasks/mine", "/api/validation-plan-approval-tasks/mine", "/api/quality/validation-plan-approval-tasks/mine", "/api/program-templates/tasks/mine", "/api/edit-locks", "/api/material-code/applications?status=Pending" })
            Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>(endpoint))!);
        Assert.Contains((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications"))!, item => item.GetProperty("id").GetGuid() == application.Id);

        var catalog = (await client.GetFromJsonAsync<WorkflowNotificationCatalog>("/api/workflow-notifications"))!;
        var invalidRole = await client.PutAsJsonAsync("/api/workflow-notifications/flows/standard-release",
            new SaveWorkflowNotification(true, [new("CompanyRole", null, RoleCode: "Administrator")], catalog.RowVersion, catalog.CompanyId));
        Assert.Equal(HttpStatusCode.BadRequest, invalidRole.StatusCode);
        Assert.Contains("所选岗位未获准接收业务消息", await invalidRole.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("business-recipient"));
        Assert.Equal(2, (await client.GetFromJsonAsync<UserNotification[]>("/api/notifications/mine"))!.Length);
        Assert.Contains((await client.GetFromJsonAsync<JsonElement[]>("/api/material-code/applications?status=Pending"))!, item => item.GetProperty("id").GetGuid() == application.Id);
    }
}
