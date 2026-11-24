using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectReleaseChangeOverviewApiTests
{
    [Fact]
    public async Task StatsCountPublishedSupplementsAndFollowReleasePermissionsForEveryProject()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var directory = await repository.GetOrganizationDirectoryAsync(default);
        var company = directory.Organizations.First().Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First().Id;
        const string role = "release-overview-reader";
        var actor = new UserAccount(Guid.NewGuid(), "release-overview-reader", "发布统计查看人", "unused",
            UserRole.Engineer, true, RoleCode: role, CompanyId: company);
        await repository.CreateUserAsync(actor, default);
        var vault = Path.Combine(AppContext.BaseDirectory, ".data", "release-overview-tests", Guid.NewGuid().ToString("N"));
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { VaultRoot = vault }, default);
        Task<Project> Create(string name) => repository.CreateNumberedProjectAsync(
            new(company, "P", 2, customer, name, null, null, 1, actor.Username, vault, Path.Combine(vault, "release")), default);
        var root = await Create("发布概览主项目");
        var child = await repository.CreateSubprojectAsync(new(root.Id, "可看发布的子项目", null, 1), default);
        var hidden = await repository.CreateSubprojectAsync(new(root.Id, "无发布权限的子项目", null, 1), default);
        var outside = await Create("其他项目");
        await repository.SetMainProjectStaffingAsync(root.Id, new(actor.Username, [], []), actor.Username, default);

        var wrong = new ReleaseChangeReasonSelection("MaterialIssue", "WrongMaterial", "物料问题", "买错物料");
        var design = new ReleaseChangeReasonSelection("DesignIssue", "DesignChange", "设计问题", "设计变更");
        var supplement = new ReleaseChangeReasonSelection("FormalSupplement", "FormalSupplement", "正式补充", "正式补充");
        var now = DateTimeOffset.UtcNow;
        Task Add(Project project, string number, ReleaseScope scope, ReleasePackageState state,
            IReadOnlyList<ReleaseChangeReasonSelection> reasons, string? reasonText = null) => repository.CreateReleasePackageAsync(
                new ReleasePackage(Guid.NewGuid(), project.Id, number, state, null, "", "", [], now, now, null)
                { Scope = scope, ChangeReasonSelections = reasons, ChangeReason = reasonText }, default);
        await Add(root, "RP-OVERVIEW-STANDARD", ReleaseScope.StandardSupplement, ReleasePackageState.Published, [wrong, wrong, design]);
        await Add(root, "RP-OVERVIEW-FORMAL", ReleaseScope.StandardFormal, ReleasePackageState.Published, [wrong]);
        await Add(root, "RP-OVERVIEW-DRAFT", ReleaseScope.StandardSupplement, ReleasePackageState.Draft, [wrong]);
        await Add(child, "RP-OVERVIEW-NONSTANDARD", ReleaseScope.NonStandardSupplement, ReleasePackageState.Published, [], "图纸问题 / 图纸错误；物料问题 / 买错物料");
        await Add(child, "RP-OVERVIEW-ELECTRICAL", ReleaseScope.ElectricalSupplement, ReleasePackageState.Published, [supplement]);
        await Add(hidden, "RP-OVERVIEW-HIDDEN", ReleaseScope.ElectricalSupplement, ReleasePackageState.Published, [wrong]);
        await Add(outside, "RP-OVERVIEW-OUTSIDE", ReleaseScope.ElectricalSupplement, ReleasePackageState.Published, [wrong]);

        var before = await repository.GetAuthorizationSettingsAsync(default);
        const string releaseTemplate = "release-overview-project-assignment";
        var authorization = await repository.SaveAuthorizationSettingsAsync(before with
        {
            Templates = [
                new(role, "发布统计查看测试", "", [
                    new(PermissionCodes.ProjectView, ["company"]),
                    new(PermissionCodes.ProjectContentView, ["MainManager"]),
                    new(ProjectCenterPermissions.Tab("overview"), ["MainManager"]),
                    new(ProjectCenterPermissions.Tab("release"), ["MainManager"]),
                ], role),
                new(releaseTemplate, "项目发布查看", "", [new("release.view", ["company"])], ProjectAssignable: true),
            ],
            Assignments = [new(Guid.NewGuid(), actor.Username, releaseTemplate, company, "project", root.Id),
                new(Guid.NewGuid(), actor.Username, releaseTemplate, company, "project", child.Id)]
        }, before.Revision, default);
        static string Route(Project project) => $"/api/projects/{project.Id}/release-packages/change-overview";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route(root))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(actor, TimeSpan.FromMinutes(10)));

        var family = (await client.GetFromJsonAsync<ReleaseChangeOverviewGroup[]>(Route(root)))!.ToDictionary(group => group.Category);
        Assert.Equal(3, family["All"].Count);
        Assert.Equal(1, family["Standard"].Count);
        Assert.Equal(1, family["NonStandard"].Count);
        Assert.Equal(1, family["Electrical"].Count);
        Assert.Equal(2, family["All"].Reasons.Single(reason => reason.Reason == "买错物料").Count);
        Assert.Equal(3, family["All"].Reasons.Count);
        Assert.Empty(family["Electrical"].Reasons);
        var childOnly = (await client.GetFromJsonAsync<ReleaseChangeOverviewGroup[]>(Route(child)))!.ToDictionary(group => group.Category);
        Assert.Equal(2, childOnly["All"].Count);
        Assert.Equal(0, childOnly["Standard"].Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route(hidden))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route(outside))).StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, Route(root));
        foreign.Headers.Add("X-Company-Id", directory.Organizations.First(item => item.Id != company).Id.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(foreign)).StatusCode);

        await repository.SaveAuthorizationSettingsAsync(authorization with { Assignments = [] }, authorization.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route(root))).StatusCode);
    }
}
