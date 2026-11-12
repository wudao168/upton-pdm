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

public sealed class QualityReviewApiTests
{
    [Fact]
    public async Task CompanyReviewRequiresExplicitGrantAndSeparatesDetailsAndCompanies()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/quality-review")).StatusCode);
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var issues = factory.Services.GetRequiredService<IProjectOplRepository>();
        var directory = await repo.GetOrganizationDirectoryAsync(default);
        var company = directory.Organizations[0].Id;
        var otherCompany = directory.Organizations[1].Id;
        const string actor = "quality-review-api-qa", role = "quality-review-api-qa-role";
        await repo.CreateUserAsync(new(Guid.NewGuid(), actor, "复盘验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        await repo.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var customer = (await repo.ListCustomersAsync(true, default)).First().Id;
        var storage = Path.Combine(Path.GetTempPath(), "pdm-quality-api", Guid.NewGuid().ToString("N"));
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "复盘验收项目", null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        var outside = await repo.CreateNumberedProjectAsync(new(otherCompany, "P", 2, customer, "公司外项目", null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        var now = DateTimeOffset.UtcNow;
        OplIssue Issue(Guid projectId, string text) => new(Guid.NewGuid(), projectId, 0, text, "普通", "待处理", actor, null, "", actor, now, null, actor, now, 0, []);
        var local = await issues.SaveAsync(Issue(project.Id, "本公司问题"), 0, default);
        await issues.SaveAsync(Issue(outside.Id, "公司外问题"), 0, default);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate(role, "复盘验收权限", "", [], role);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = [.. settings.Templates, template] }, settings.Revision, default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(actor));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/quality-review")).StatusCode);
        Assert.Contains(RolePermissionCatalog.Permissions, p => p.Code == QualityReviewPermissions.View && p.Name == "查看公司问题汇总");
        Assert.True(FunctionPermissionCatalog.RequiresExplicitGrant(QualityReviewPermissions.View));
        await Grants(QualityReviewPermissions.View);
        var summary = await Read();
        Assert.Empty(summary.GetProperty("issues").EnumerateArray());
        Assert.Empty(summary.GetProperty("catalog").GetProperty("profiles").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/quality-review/themes/" + Guid.NewGuid(), new SaveQualityTheme(0, "未授权主题", "", true))).StatusCode);
        await Grants(QualityReviewPermissions.View, QualityReviewPermissions.Detail);
        var detail = await Read();
        var row = Assert.Single(detail.GetProperty("issues").EnumerateArray());
        Assert.Equal(local.Id, row.GetProperty("issue").GetProperty("id").GetGuid());
        Assert.DoesNotContain("公司外问题", detail.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{project.Id}/quality-review/issues/{local.Id}")).StatusCode);
        await Grants(QualityReviewPermissions.View, QualityReviewPermissions.Detail, QualityReviewPermissions.Manage);
        var themeId = Guid.NewGuid();
        var save = await client.PutAsJsonAsync("/api/quality-review/themes/" + themeId, new SaveQualityTheme(0, "装配定位偏差", "定位基准不一致", true));
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(saved.EnumerateObject());
        Assert.Equal(1, saved.GetProperty("rowVersion").GetInt64());
        var conflict = await client.PutAsJsonAsync("/api/quality-review/themes/" + themeId, new SaveQualityTheme(0, "旧版本覆盖", "", true));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("刷新", await conflict.Content.ReadAsStringAsync());
        var updated = await Read();
        Assert.Equal("装配定位偏差", Assert.Single(updated.GetProperty("catalog").GetProperty("themes").EnumerateArray()).GetProperty("name").GetString());
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", otherCompany.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/quality-review")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        await Grants();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/quality-review")).StatusCode);

        async Task Grants(params string[] permissions)
        {
            settings = await repo.SaveAuthorizationSettingsAsync(settings with {
                Templates = settings.Templates.Select(t => t.Key == role ? t with { Grants = permissions.Select(p => new AuthorizationGrant(p, ["company"])).ToArray() } : t).ToArray()
            }, settings.Revision, default);
        }
        async Task<JsonElement> Read()
        {
            var response = await client.GetAsync("/api/quality-review");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    [Fact]
    public async Task SimplifiedIssuePutOmitsLegacyFieldsPreservesHistoryAndRequiresProjectManage()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var issues = factory.Services.GetRequiredService<IProjectOplRepository>();
        var store = factory.Services.GetRequiredService<IQualityReviewStore>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations[0].Id;
        const string actor = "quality-simple-api", role = "quality-simple-api-role";
        await repo.CreateUserAsync(new(Guid.NewGuid(), actor, "简化录入验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        await repo.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var customer = (await repo.ListCustomersAsync(true, default)).First().Id;
        var storage = Path.Combine(Path.GetTempPath(), "pdm-quality-simple-api", Guid.NewGuid().ToString("N"));
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "质量录入简化验收", null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).Date);
        Task<OplIssue> Seed(string text) => issues.SaveAsync(new(Guid.NewGuid(), project.Id, 0, text, "普通", "待处理", actor,
            null, "", actor, now, null, actor, now, 0, []), 0, default);
        var duplicate = await Seed("保留历史重复关系");
        var recurrence = await Seed("保留历史复发关系");
        var fresh = await Seed("新问题默认独立事件");
        QualityIssueProfile Profile(Guid id) => new(id, "质量", "历史分类", "普通", today, null, null, null,
            "", "", "", "未验证", "", [], actor, now);
        var duplicateTarget = Guid.NewGuid(); var recurrenceTarget = Guid.NewGuid();
        var catalog = await store.ReadAsync(company, default);
        Assert.True(await store.SaveAsync(catalog with { Profiles = [Profile(duplicate.Id) with { DuplicateOfIssueId = duplicateTarget },
            Profile(recurrence.Id) with { RecurrenceOfIssueId = recurrenceTarget }] }, catalog.RowVersion, default));
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate(role, "简化录入验收授权", "", [], role);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = [.. settings.Templates, template] }, settings.Revision, default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(actor));

        await Grant(QualityReviewPermissions.IssueView);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(duplicate)).StatusCode);
        await Grant(QualityReviewPermissions.IssueManage);
        foreach (var issue in new[] { duplicate, recurrence })
        {
            var response = await Put(issue);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        catalog = await store.ReadAsync(company, default);
        var oldDuplicate = catalog.Profiles.Single(p => p.IssueId == duplicate.Id);
        var oldRecurrence = catalog.Profiles.Single(p => p.IssueId == recurrence.Id);
        Assert.Equal("历史分类", oldDuplicate.Category); Assert.Equal(duplicateTarget, oldDuplicate.DuplicateOfIssueId);
        Assert.Null(oldDuplicate.RecurrenceOfIssueId);
        Assert.Equal("历史分类", oldRecurrence.Category); Assert.Equal(recurrenceTarget, oldRecurrence.RecurrenceOfIssueId);
        Assert.Null(oldRecurrence.DuplicateOfIssueId);
        var freshResponse = await Put(fresh, forged: true);
        Assert.True(freshResponse.IsSuccessStatusCode, await freshResponse.Content.ReadAsStringAsync());
        var saved = (await store.ReadAsync(company, default)).Profiles.Single(p => p.IssueId == fresh.Id);
        Assert.Empty(saved.Category); Assert.Null(saved.DuplicateOfIssueId); Assert.Null(saved.RecurrenceOfIssueId);
        var panel = await client.GetAsync($"/api/projects/{project.Id}/quality-review/issues/{fresh.Id}");
        Assert.True(panel.IsSuccessStatusCode, await panel.Content.ReadAsStringAsync());
        Assert.Empty((await panel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("relatedIssues").EnumerateArray());
        await Grant(QualityReviewPermissions.IssueView);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(fresh)).StatusCode);

        async Task Grant(params string[] permissions)
        {
            var codes = permissions.ToHashSet();
            var queue = new Queue<string>(codes);
            while (queue.TryDequeue(out var code))
                foreach (var dependency in RolePermissionCatalog.Permissions.Single(p => p.Code == code).Dependencies)
                    if (codes.Add(dependency)) queue.Enqueue(dependency);
            settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(t => t.Key == role
                ? t with { Grants = codes.Select(p => new AuthorizationGrant(p, ["company"])).ToArray() } : t).ToArray() }, settings.Revision, default);
        }
        async Task<HttpResponseMessage> Put(OplIssue issue, bool forged = false)
        {
            var version = (await store.ReadAsync(company, default)).RowVersion;
            var body = new Dictionary<string, object?> {
                ["expectedRowVersion"] = version, ["domain"] = "质量", ["severity"] = "普通", ["occurredOn"] = today, ["themeId"] = null,
                ["immediateAction"] = "补充临时处置", ["rootCause"] = "", ["correctiveAction"] = "", ["verificationResult"] = "未验证",
                ["evidenceNote"] = "", ["evidenceAttachmentIds"] = Array.Empty<Guid>()
            };
            if (forged) { body["category"] = "过期客户端尝试分类"; body["duplicateOfIssueId"] = duplicate.Id; body["recurrenceOfIssueId"] = recurrence.Id; }
            return await client.PutAsJsonAsync($"/api/projects/{project.Id}/quality-review/issues/{issue.Id}", body);
        }
    }

    [Fact]
    public async Task ThemeDeleteRequiresCurrentCompanyManageGrantAndVersionAndPreservesHistory()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var store = factory.Services.GetRequiredService<IQualityReviewStore>();
        var directory = await repo.GetOrganizationDirectoryAsync(default);
        var company = directory.Organizations[0].Id;
        var outsideCompany = directory.Organizations[1].Id;
        const string actor = "quality-theme-delete-qa", role = "quality-theme-delete-qa-role";
        await repo.CreateUserAsync(new(Guid.NewGuid(), actor, "主题删除验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        await repo.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var unused = new QualityTheme(Guid.NewGuid(), "未引用主题", "", true);
        var historical = new QualityTheme(Guid.NewGuid(), "历史快照主题", "", false);
        var revoked = new QualityTheme(Guid.NewGuid(), "撤权后不可删除", "", true);
        var outside = new QualityTheme(Guid.NewGuid(), "公司外主题", "", true);
        var now = DateTimeOffset.UtcNow;
        var profile = new QualityIssueProfile(Guid.NewGuid(), "质量", "历史分类", "普通", null, historical.Id,
            null, null, "", "", "", "未验证", "", [], actor, now);
        var snapshot = new QualityIssueSnapshot(profile.IssueId, 1, Guid.NewGuid(), "历史项目", "历史问题", "已完成", "历史负责人", "已处理", profile, 1);
        var summary = new ProjectRetrospective(Guid.NewGuid(), snapshot.ProjectId, 1, "最终总结", "已发布", "冻结总结",
            "", "", "", actor, now, actor, now, [snapshot]);
        var catalog = await store.ReadAsync(company, default);
        Assert.True(await store.SaveAsync(catalog with { Themes = [unused, historical, revoked], Summaries = [summary] }, catalog.RowVersion, default));
        var external = await store.ReadAsync(outsideCompany, default);
        Assert.True(await store.SaveAsync(external with { Themes = [outside] }, external.RowVersion, default));
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with {
            Templates = [.. settings.Templates, new AuthorizationTemplate(role, "主题删除验收授权", "", [], role)]
        }, settings.Revision, default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(unused.Id, 1)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(actor));
        await Grants(QualityReviewPermissions.View);
        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(unused.Id, 1)).StatusCode);
        await Grants(QualityReviewPermissions.View, QualityReviewPermissions.Manage);
        Assert.Equal(HttpStatusCode.Conflict, (await Delete(unused.Id, 0)).StatusCode);
        // An identifier from another company never selects that company's catalog.
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(outside.Id, 1)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", outsideCompany.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(outside.Id, 1)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var protectedResponse = await Delete(historical.Id, 1);
        Assert.Equal(HttpStatusCode.BadRequest, protectedResponse.StatusCode);
        var reason = await protectedResponse.Content.ReadAsStringAsync();
        Assert.Contains("项目总结快照", reason); Assert.Contains("停用", reason);
        var deleted = await Delete(unused.Id, 1);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        var response = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(response.EnumerateObject()); Assert.Equal(2, response.GetProperty("rowVersion").GetInt64());
        catalog = await store.ReadAsync(company, default);
        Assert.DoesNotContain(catalog.Themes, t => t.Id == unused.Id);
        Assert.Contains(catalog.Themes, t => t.Id == historical.Id);
        Assert.Equal(historical.Id, Assert.Single(Assert.Single(catalog.Summaries).Issues).Profile!.ThemeId);
        Assert.Single((await store.ReadAsync(outsideCompany, default)).Themes);
        await Grants(QualityReviewPermissions.View);
        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(revoked.Id, 2)).StatusCode);
        Assert.Equal(2, (await store.ReadAsync(company, default)).RowVersion);
        var audit = Assert.Single(await repo.ListAuditAsync(actor, UserRole.Engineer, 100, default), a => a.Action == "quality.theme.delete");
        Assert.Equal(unused.Id.ToString(), audit.EntityId);
        Assert.Equal($"已删除共性问题主题：{unused.Name}", audit.Detail);

        Task<HttpResponseMessage> Delete(Guid id, long version) => client.DeleteAsync($"/api/quality-review/themes/{id}?expectedRowVersion={version}");
        async Task Grants(params string[] permissions)
        {
            settings = await repo.SaveAuthorizationSettingsAsync(settings with {
                Templates = settings.Templates.Select(t => t.Key == role ? t with { Grants = permissions.Select(p => new AuthorizationGrant(p, ["company"])).ToArray() } : t).ToArray()
            }, settings.Revision, default);
        }
    }

    [Fact]
    public async Task MainProjectSummaryIncludesOnlyAuthorizedLegacyChildRecordsAndRejectsNewChildSummaries()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var store = factory.Services.GetRequiredService<IQualityReviewStore>();
        var directory = await repo.GetOrganizationDirectoryAsync(default);
        var company = directory.Organizations[0].Id;
        const string actor = "summary-scope-qa", template = "summary-scope-projects";
        await repo.CreateUserAsync(new(Guid.NewGuid(), actor, "总结验收", "unused", UserRole.Engineer, true, RoleCode: actor, CompanyId: company), default);
        await repo.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var customer = (await repo.ListCustomersAsync(true, default)).First().Id;
        var storage = Path.Combine(Path.GetTempPath(), "pdm-summary-scope", Guid.NewGuid().ToString("N"));
        Task<Project> Main(string name) => repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, name, null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        var main = await Main("总结验收主项目");
        var other = await Main("总结验收其他项目");
        var child = await repo.CreateSubprojectAsync(new(main.Id, "授权子项目", null, 1), default);
        var restricted = await repo.CreateSubprojectAsync(new(main.Id, "未授权子项目", null, 1), default);
        ProjectRetrospective Legacy(Project project) => new(Guid.NewGuid(), project.Id, 1, "阶段总结", "草稿", project.Name,
            "", "", "", actor, DateTimeOffset.UtcNow, null, null, []);
        var legacy = Legacy(child);
        var hidden = Legacy(restricted);
        var unrelated = Legacy(other);
        var catalog = await store.ReadAsync(company, default);
        Assert.True(await store.SaveAsync(catalog with { Summaries = [legacy, hidden, unrelated] }, catalog.RowVersion, default));

        var codes = new HashSet<string>();
        void Add(string code)
        {
            if (!codes.Add(code)) return;
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies) Add(dependency);
        }
        Add(QualityReviewPermissions.SummaryManage);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = [.. settings.Templates,
            new(template, "总结验收项目授权", "", codes.Select(code => new AuthorizationGrant(code, ["company"])).ToArray())] }, settings.Revision, default);
        await Grant(main.Id, child.Id, other.Id);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(actor));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var result = await Read();
        Assert.Equal(legacy.Id, Assert.Single(result.Summaries).Id);
        Assert.True(result.SummarySources[child.Id].CanEdit);
        Assert.Equal(HttpStatusCode.BadRequest, (await Save(child.Id, Guid.NewGuid(), result.RowVersion, "禁止子项目新建")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Save(main.Id, unrelated.Id, result.RowVersion, "禁止跨项目写入")).StatusCode);
        var saved = await Save(main.Id, Guid.NewGuid(), result.RowVersion, "主项目总结");
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        result = await Read();
        Assert.Contains(result.Summaries, item => item.ProjectId == main.Id && item.Title == "主项目总结");
        var edited = await Save(child.Id, legacy.Id, result.RowVersion, "原子项目草稿更新");
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
        await Grant(main.Id, other.Id);
        result = await Read();
        Assert.Equal(main.Id, Assert.Single(result.Summaries).ProjectId);
        Assert.Empty(result.SummarySources);
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(child.Id, legacy.Id, result.RowVersion, "撤权后禁止编辑")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", directory.Organizations[1].Id.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/projects/{main.Id}/quality-review")).StatusCode);

        async Task Grant(params Guid[] projects) => settings = await repo.SaveAuthorizationSettingsAsync(settings with {
            Assignments = [.. settings.Assignments.Where(item => item.TemplateKey != template),
                .. projects.Select(id => new AuthorizationAssignment(Guid.NewGuid(), actor, template, company, "project", id))]
        }, settings.Revision, default);
        async Task<ProjectRetrospectiveWorkspace> Read() => (await client.GetFromJsonAsync<ProjectRetrospectiveWorkspace>($"/api/projects/{main.Id}/quality-review"))!;
        Task<HttpResponseMessage> Save(Guid projectId, Guid summaryId, long version, string title) =>
            client.PutAsJsonAsync($"/api/projects/{projectId}/retrospectives/{summaryId}", new SaveProjectRetrospective(version, "阶段总结", title, "", "", ""));
    }

    private static string Token(string actor) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Engineer")],
        expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: new(new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
