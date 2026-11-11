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

public sealed class ReleasePreviewPolicyApiTests
{
    [Theory]
    [InlineData(ReleaseScope.StandardFormal)]
    [InlineData(ReleaseScope.StandardSupplement)]
    [InlineData(ReleaseScope.ElectricalFormal)]
    [InlineData(ReleaseScope.ElectricalSupplement)]
    public async Task BomOnlyReleaseRejectsRetryAndReturnsNoConversionItemsEvenWithStaleStatus(ReleaseScope scope)
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        const string actor = "preview-policy-test";
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "转图类型测试", "unused",
            UserRole.Engineer, true, RoleCode: actor, CompanyId: company), default);
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer,
            "无转图发布测试", null, null, 1, actor, Path.GetTempPath(), Path.GetTempPath()), default);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var manager = new AuthorizationTemplate(actor, actor, "", ManagerGrants()) { ProjectAssignable = true, RoleCode = actor };
        await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Append(manager).ToArray(),
            Assignments = settings.Assignments.Append(new(Guid.NewGuid(), actor, manager.Key, company, "project", project.Id)).ToArray(),
        }, settings.Revision, default);
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-NO-CONVERSION", ReleasePackageState.Published,
            null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "unused") {
            Scope = scope, LocksDocuments = true, PreviewState = ReleasePreviewState.Pending, PreviewAttempts = 5
        };
        await repository.CreateReleasePackageAsync(package, default);
        Login(client, actor, company);
        var response = await client.PostAsJsonAsync($"/api/release-packages/{package.Id}/preview/retry", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不支持转图", await response.Content.ReadAsStringAsync());
        var unchanged = (await repository.FindReleasePackageAsync(package.Id, default))!;
        Assert.Equal(5, unchanged.PreviewAttempts);
        var items = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/projects/{project.Id}/release-preview-items?releasePackageId={package.Id}");
        Assert.Equal(0, items.GetArrayLength());
    }


    [Fact]
    public async Task PreviewRetryRequiresProjectWriteAndReleaseManagementForThePackage()
    {
        const string actor = "preview-retry-test";
        var storage = Directory.CreateTempSubdirectory("pdm-preview-retry-");
        try
        {
            await using var factory = new PdmApiFactory();
            using var client = factory.CreateClient();
            var repository = factory.Services.GetRequiredService<IPdmRepository>();
            var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
            var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
            await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "转图权限测试", "unused",
                UserRole.Engineer, true, RoleCode: actor, CompanyId: company), default);

            async Task<ReleasePackage> CreatePackage(string suffix)
            {
                var project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer,
                    $"转图权限测试{suffix}", null, null, 1, actor, storage.FullName, storage.FullName), default);
                var package = new ReleasePackage(Guid.NewGuid(), project.Id, $"RP-RETRY-{suffix}",
                    ReleasePackageState.Published, null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
                {
                    Scope = ReleaseScope.NonStandardWithDrawing, LocksDocuments = true,
                    PreviewState = ReleasePreviewState.Failed, PreviewAttempts = 5,
                };
                await repository.CreateReleasePackageAsync(package, default);
                return package;
            }

            var assigned = await CreatePackage("IN");
            var other = await CreatePackage("OUT");
            var viewer = new AuthorizationTemplate("preview-viewer", "发布查看", "",
                new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("release") }
                    .Select(code => new AuthorizationGrant(code, ["company"])).ToArray()) { ProjectAssignable = true };
            var editor = viewer with { Key = "preview-editor", Name = "项目编辑",
                Grants = viewer.Grants.Append(new(ProjectCenterPermissions.Write, ["company"])).ToArray() };
            var manager = viewer with { Key = "preview-manager", Name = "发布管理", Grants = ManagerGrants() };
            var roleTemplate = new AuthorizationTemplate(actor, actor, "", []) { RoleCode = actor };
            var assignment = new AuthorizationAssignment(Guid.NewGuid(), actor, viewer.Key, company, "project", assigned.ProjectId);
            var settings = await repository.GetAuthorizationSettingsAsync(default);
            settings = await repository.SaveAuthorizationSettingsAsync(settings with
            {
                Templates = settings.Templates.Concat([roleTemplate, viewer, editor, manager]).ToArray(),
                Assignments = settings.Assignments.Append(assignment).ToArray()
            }, settings.Revision, default);
            Login(client, actor, company);

            string Retry(ReleasePackage package) => $"/api/release-packages/{package.Id}/preview/retry";
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Retry(assigned), null)).StatusCode);
            async Task AssignAsync(string template)
            {
                settings = await repository.SaveAuthorizationSettingsAsync(settings with
                {
                    Assignments = settings.Assignments.Select(item => item.Id == assignment.Id ? item with { TemplateKey = template } : item).ToArray()
                }, settings.Revision, default);
            }
            await AssignAsync(manager.Key);
            using var response = await client.PostAsync(Retry(assigned), null);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Retry(other), null)).StatusCode);
            await AssignAsync(editor.Key);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(Retry(assigned), null)).StatusCode);
        }
        finally
        {
            storage.Delete(recursive: true);
        }
    }

    private static AuthorizationGrant[] ManagerGrants() =>
        new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, ProjectCenterPermissions.Tab("release"),
            ProjectCenterPermissions.Write, PermissionCodes.ReleaseManage }
            .Select(code => new AuthorizationGrant(code, ["company"])).ToArray();

    private static void Login(HttpClient client, string actor, Guid company)
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, UserRole.Engineer.ToString())],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
    }
}
