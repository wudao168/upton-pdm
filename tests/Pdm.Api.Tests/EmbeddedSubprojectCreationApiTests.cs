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

public sealed class EmbeddedSubprojectCreationApiTests
{
    private static readonly Guid OrganizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PreviewReusesReleasedMainAndMissingChildNumbersWithoutReservation()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", UserRole.Administrator));

        var createFirst = await client.PostAsJsonAsync("/api/projects", Body("释放主项目号", null));
        Assert.Equal(HttpStatusCode.Created, createFirst.StatusCode);
        var first = (await createFirst.Content.ReadFromJsonAsync<Project>())!;
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/projects", Body("后续主项目", null))).StatusCode);
        await repository.DeleteProjectAsync(first.Id, default);

        var previewUrl = $"/api/projects/numbering/preview?organizationId={OrganizationId}&projectTypeCode=P&childCount=3";
        var previewResponse = await client.GetAsync(previewUrl);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.True(previewResponse.Headers.CacheControl?.NoStore);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<ProjectCodePreview>())!;
        Assert.Equal(first.Code, preview.ProjectCode);
        Assert.Equal(new[] { $"{first.Code}-1", $"{first.Code}-2", $"{first.Code}-3" }, preview.ChildCodes);
        var repeated = (await client.GetFromJsonAsync<ProjectCodePreview>(previewUrl))!;
        Assert.Equal(preview.ProjectCode, repeated.ProjectCode);
        Assert.Equal(preview.ChildCodes.ToArray(), repeated.ChildCodes.ToArray());

        var create = await client.PostAsJsonAsync("/api/projects", Body("复用主项目号",
            [new { name = "子一", quantity = 1 }, new { name = "子二", quantity = 1 }, new { name = "子三", quantity = 1 }]));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var main = (await create.Content.ReadFromJsonAsync<Project>())!;
        Assert.Equal(preview.ProjectCode, main.Code);
        var children = (await repository.ListProjectsAsync(default)).Where(project => project.ParentProjectId == main.Id)
            .OrderBy(project => project.ChildSequence).ToArray();
        Assert.Equal(preview.ChildCodes, children.Select(project => project.Code));
        await repository.DeleteProjectAsync(children[1].Id, default);

        var childPreviewUrl = $"/api/projects/{main.Id}/children/numbering/preview?childCount=2";
        var childPreview = (await client.GetFromJsonAsync<ProjectCodePreview>(childPreviewUrl))!;
        Assert.Equal(main.Code, childPreview.ProjectCode);
        Assert.Equal(new[] { $"{main.Code}-2", $"{main.Code}-4" }, childPreview.ChildCodes);
        var append = await client.PostAsJsonAsync($"/api/projects/{main.Id}/children",
            new { items = new[] { new { name = "补齐子二", quantity = 1 }, new { name = "追加子四", quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Created, append.StatusCode);
        var appended = (await append.Content.ReadFromJsonAsync<Project[]>())!;
        Assert.Equal(childPreview.ChildCodes, appended.Select(project => project.Code));

        var overCapacity = await client.GetAsync($"/api/projects/{main.Id}/children/numbering/preview?childCount=96");
        Assert.Equal(HttpStatusCode.BadRequest, overCapacity.StatusCode);
        Assert.Contains("还可创建95个子项目", await overCapacity.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", UserRole.Engineer));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(childPreviewUrl)).StatusCode);
    }

    [Fact]
    public async Task MainAndChildrenAreCreatedTogetherAndInvalidRowsLeaveNoMainProject()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", UserRole.Administrator));

        var invalid = await client.PostAsJsonAsync("/api/projects", Body("无效行主项目",
            [new { name = "本行有效", quantity = 1 }, new { name = "本行数量无效", quantity = 0 }]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("第2行", await invalid.Content.ReadAsStringAsync());
        Assert.DoesNotContain(await repository.ListProjectsAsync(default), project => project.Name == "无效行主项目");

        var response = await client.PostAsJsonAsync("/api/projects", Body("带子项目的主项目",
            [new { name = " 子项目甲 ", projectAlias = " 别名甲 ", quantity = 3 }, new { name = "子项目乙", quantity = 1 }]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var main = await response.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(main);
        Assert.Null(main.ParentProjectId);
        Assert.Equal($"/api/projects/{main.Id}", response.Headers.Location?.OriginalString);
        var children = (await repository.ListProjectsAsync(default)).Where(project => project.ParentProjectId == main.Id)
            .OrderBy(project => project.ChildSequence).ToArray();
        Assert.Equal(new[] { $"{main.Code}-1", $"{main.Code}-2" }, children.Select(project => project.Code));
        Assert.Equal(new[] { "子项目甲", "子项目乙" }, children.Select(project => project.Name));
        Assert.Equal("别名甲", children[0].ProjectAlias);
        Assert.Equal(new[] { 3, 1 }, children.Select(project => project.Quantity));
        Assert.Equal(6, children.SelectMany(project => project.SerialNumbers).Concat(main.SerialNumbers).Distinct().Count());
        var folders = await repository.ListProjectFoldersAsync(main.Id, "admin", UserRole.Administrator, default);
        Assert.All(children, project => Assert.Contains(folders, folder => folder.TargetProjectId == project.Id));
    }

    [Fact]
    public async Task ParentCreationPermissionCannotBypassNewParentChildScope()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        await repository.SetRolePermissionsAsync(nameof(UserRole.Engineer),
            [FunctionPermissionCatalog.SchemaMarker, PermissionCodes.ProjectView, PermissionCodes.ProjectCreate, PermissionCodes.ProjectChildCreate], default);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var templates = settings.Templates.Select(template => template.RoleCode == nameof(UserRole.Engineer)
            ? template with { Grants = template.Grants.Select(grant => grant.Permission == PermissionCodes.ProjectChildCreate
                ? grant with { Scopes = ["MainManager"] }
                : grant).ToArray() }
            : template).ToArray();
        await repository.SaveAuthorizationSettingsAsync(settings with { Templates = templates }, settings.Revision, default);
        Assert.True(await repository.HasUserPermissionAsync("engineer", UserRole.Engineer, PermissionCodes.ProjectChildCreate, default));
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", UserRole.Engineer));

        var projectCount = (await repository.ListProjectsAsync(default)).Count;
        var counter = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Single(item => item.Id == OrganizationId);
        var denied = await client.PostAsJsonAsync("/api/projects", Body("尚无子项目范围的主项目", [new { name = "无权限子项目", quantity = 1 }]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedPreview = await client.GetAsync($"/api/projects/numbering/preview?organizationId={OrganizationId}&projectTypeCode=P&childCount=1");
        Assert.Equal(HttpStatusCode.Forbidden, deniedPreview.StatusCode);
        Assert.Equal(projectCount, (await repository.ListProjectsAsync(default)).Count);
        var current = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Single(item => item.Id == OrganizationId);
        Assert.Equal(counter.CurrentProjectSequence, current.CurrentProjectSequence);
        Assert.Equal(counter.CurrentSerialSequence, current.CurrentSerialSequence);

        var allowed = await client.PostAsJsonAsync("/api/projects", Body("只建立主项目", null));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    private static object Body(string name, object[]? children) => new
    {
        organizationId = OrganizationId, projectTypeCode = "P", equipmentTypeCode = 2,
        customerId = "c0046500-0000-0000-0000-000000000001", name, quantity = 2, children
    };

    private static string Token(string actor, UserRole role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        "upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role.ToString())],
        expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
}
