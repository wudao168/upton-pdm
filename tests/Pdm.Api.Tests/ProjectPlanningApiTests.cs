using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectPlanningApiTests : IClassFixture<PdmApiFactory>
{
    private readonly PdmApiFactory factory;
    public ProjectPlanningApiTests(PdmApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generate_plan_accepts_post_delivery_dates_and_deferral_flags(bool deferred)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var project = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "交付后排期接口测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        project = await AssignProjectManager(repository, project);
        var template = (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default))[0];
        using var client = factory.CreateClient();
        AuthorizeAdmin(client);
        var start = new DateOnly(2026, 9, 10);
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/plan/generate", new
        {
            templateId = template.Id, startDate = start, totalDurationDays = 60, replaceExisting = false,
            independentStages = deferred ? Array.Empty<ProjectPlanStageSchedule>() : [new(ProjectPlanStage.ClientCommissioning, start.AddDays(60), 15), new(ProjectPlanStage.AcceptanceProgress, start.AddDays(75), 15)],
            deferredStages = deferred ? new[] { ProjectPlanStage.ClientCommissioning, ProjectPlanStage.AcceptanceProgress } : Array.Empty<string>()
        });
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tasks = result.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
        if (deferred)
            Assert.DoesNotContain(tasks, task => task.GetProperty("stage").GetString() is ProjectPlanStage.ClientCommissioning or ProjectPlanStage.AcceptanceProgress);
        else
        {
            Assert.Equal("2026-11-09", tasks.Single(task => task.GetProperty("stage").GetString() == ProjectPlanStage.ClientCommissioning).GetProperty("plannedStart").GetString());
            Assert.Equal("2026-11-24", tasks.Single(task => task.GetProperty("stage").GetString() == ProjectPlanStage.AcceptanceProgress).GetProperty("plannedStart").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_plan_requires_auth_revision_and_removes_plan_content(bool isChild)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
        var planning = scope.ServiceProvider.GetRequiredService<IProjectPlanningRepository>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var root = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "删除计划接口测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        root = await AssignProjectManager(repository, root);
        var child = isChild ? await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default) : root;
        var template = (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default))[0];
        var draft = await service.GenerateAsync(child.Id, new(template.Id, new DateOnly(2026, 9, 10), 100, false, null), "admin", UserRole.Administrator, default);
        using var client = factory.CreateClient();
        var url = $"/api/projects/{child.Id}/plan";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{url}?expectedRowVersion={draft.RowVersion}")).StatusCode);
        Authorize(client, "other-admin", "Administrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{url}?expectedRowVersion={draft.RowVersion}")).StatusCode);
        AuthorizeAdmin(client);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{url}?expectedRowVersion=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}?expectedRowVersion={draft.RowVersion}")).StatusCode);
        Assert.Null(await planning.FindPlanAsync(child.Id, default));
        Assert.Empty(await planning.ListVersionsAsync(draft.Id, default));
    }

    [Fact]
    public async Task Supplement_stage_schedule_api_preserves_existing_tasks()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
        var planning = scope.ServiceProvider.GetRequiredService<IProjectPlanningRepository>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var project = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "补充阶段接口测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        project = await AssignProjectManager(repository, project);
        var template = (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default))[0];
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var legacy = await planning.SavePlanAsync(plan with { StageSchedules = [] }, plan.RowVersion, null, default);
        using var client = factory.CreateClient();
        AuthorizeAdmin(client);
        var response = await client.PostAsJsonAsync($"/api/projects/{project.Id}/plan/stage-schedule", new { startDate = "2026-09-10", totalDurationDays = 60, expectedRowVersion = legacy.RowVersion });
        response.EnsureSuccessStatusCode();
        var saved = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Equal(legacy.Tasks, saved.Tasks);
        Assert.Equal(60, saved.StageSchedules.Sum(stage => stage.DurationDays));
        Assert.Equal(legacy.RowVersion + 1, saved.RowVersion);
    }

    [Fact]
    public async Task Explicit_sync_api_creates_a_missing_follower_draft()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
        var planning = scope.ServiceProvider.GetRequiredService<IProjectPlanningRepository>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var root = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "显式同步接口测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        root = await AssignProjectManager(repository, root);
        var template = (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default))[0];
        var master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var lateChild = await repository.CreateSubprojectAsync(new(root.Id, "后建子项目", null, 1), default);
        using var client = factory.CreateClient();
        AuthorizeAdmin(client);

        var response = await client.PutAsJsonAsync($"/api/projects/{root.Id}/plan", new
        {
            tasks = master.Tasks,
            changeReason = "同步跟随项目",
            expectedRowVersion = master.RowVersion,
            createMissingFollowers = true
        });

        response.EnsureSuccessStatusCode();
        var follower = Assert.IsType<ProjectPlan>(await planning.FindPlanAsync(lateChild.Id, default));
        Assert.True(follower.FollowsParentPlan);
        Assert.Equal(master.Id, follower.ParentPlanId);
    }

    private static Task<Project> AssignProjectManager(IPdmRepository repository, Project project) =>
        repository.SetMainProjectStaffingAsync(project.Id, new("admin", [], []), "admin", default);

    private static void AuthorizeAdmin(HttpClient client) => Authorize(client, "admin", "Administrator");

    private static void Authorize(HttpClient client, string username, string role)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    [Fact]
    public async Task Approved_plan_change_request_keeps_schedule_until_decision()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ProjectPlanningService>();
        var planning = scope.ServiceProvider.GetRequiredService<IProjectPlanningRepository>();
        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var customer = (await repository.ListCustomersAsync(true, default))[0];
        var project = await repository.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "计划变更申请接口测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        project = await AssignProjectManager(repository, project);
        var template = (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default))[0];
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var plan = await planning.SavePlanAsync(draft with { ApprovalStatus = ProjectPlanApprovalStatus.Approved, ApprovedBy = "admin" }, draft.RowVersion, null, default);
        using var client = factory.CreateClient();
        var change = new SubmitProjectPlanChangeCommand([], "客户调整", plan.RowVersion);
        var url = $"/api/projects/{project.Id}/plan";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"{url}/change-request", change)).StatusCode);
        AuthorizeAdmin(client);
        Assert.False((await client.PutAsJsonAsync(url, new { tasks = plan.Tasks, changeReason = "直接修改", expectedRowVersion = plan.RowVersion })).IsSuccessStatusCode);
        (await client.PostAsJsonAsync($"{url}/change-request", change)).EnsureSuccessStatusCode();
        var pending = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Equal(plan.Tasks, pending.Tasks);
        Assert.Equal(ProjectPlanApprovalStatus.Pending, pending.ChangeRequest!.Status);
        (await client.PostAsJsonAsync($"{url}/decision", new { expectedRowVersion = pending.RowVersion, approve = true, comment = "同意" })).EnsureSuccessStatusCode();
        var editable = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.NotNull(editable.ChangeDraftSource);
        Assert.Equal(plan.Tasks, editable.Tasks);
        var tasks = editable.Tasks.Select((task, index) => index == 0
            ? task with { PlannedStart = new(2026, 9, 12), PlannedFinish = new(2026, 9, 14), DurationDays = 3 }
            : task).ToArray();
        (await client.PutAsJsonAsync(url, new { tasks, changeReason = "保存草稿", expectedRowVersion = editable.RowVersion })).EnsureSuccessStatusCode();
        var savedDraft = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Equal(plan.Tasks[0].PlannedStart, savedDraft.ChangeDraftSource!.Tasks[0].PlannedStart);
        (await client.PostAsJsonAsync($"{url}/change-complete", new { expectedRowVersion = savedDraft.RowVersion })).EnsureSuccessStatusCode();
        var activated = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Null(activated.ChangeDraftSource);
        Assert.Equal(new DateOnly(2026, 9, 12), activated.Tasks[0].PlannedStart);
        Assert.Equal(ProjectPlanApprovalStatus.Approved, activated.ApprovalStatus);
    }
}
