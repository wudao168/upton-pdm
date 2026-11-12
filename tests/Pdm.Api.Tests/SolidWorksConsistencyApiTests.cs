using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class SolidWorksConsistencyApiTests
{
    [Fact]
    public async Task DocumentCapabilitiesMatchCheckoutAndRespectRevocationAndProjectScope()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var a = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "插件权限测试", null, null, 1, "sw-worker", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var b = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "未授权项目", null, null, 1, "sw-worker", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "sw-worker", "图档测试人员", "unused", UserRole.Engineer, true, RoleCode: "sw-worker", CompanyId: company), default);
        var first = await repo.RegisterDocumentAsync(new(a.Id, "FIRST", "第一图档", "FIRST.SLDASM", DocumentKind.Assembly), "admin", default);
        var second = await repo.RegisterDocumentAsync(new(b.Id, "SECOND", "第二图档", "SECOND.SLDASM", DocumentKind.Assembly), "admin", default);
        var codes = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit,
            ProjectCenterPermissions.Tab("documents"), ProjectCenterPermissions.Write,
            "document.archive", "document.checkout", "document.restore", AuthorizationActions.ArchiveSubmit,
            AuthorizationActions.FolderView, AuthorizationActions.FolderEdit, AuthorizationActions.FolderDownload };
        var grants = codes.ToHashSet();
        var pending = new Queue<string>(codes);
        while (pending.TryDequeue(out var code))
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies)
                if (grants.Add(dependency)) pending.Enqueue(dependency);
        var template = new AuthorizationTemplate("sw-test", "插件图档权限", "", grants.Select(code => new AuthorizationGrant(code, ["company"])).ToArray()) { ProjectAssignable = true };
        var old = await repo.GetAuthorizationSettingsAsync(default);
        var settings = await repo.SaveAuthorizationSettingsAsync(old with { Templates = [.. old.Templates, template],
            Assignments = [.. old.Assignments, new(Guid.NewGuid(), "sw-worker", template.Key, company, "project", a.Id)] }, old.Revision, default);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/documents/{first.Id}")).StatusCode);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "sw-worker"), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var response = await client.GetAsync($"/api/documents/{first.Id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var metadata = (await response.Content.ReadFromJsonAsync<PdmDocument>())!;
        var decisions = AuthorizationPolicy.Decisions(await repo.GetAuthorizationSettingsAsync(default),
            (await AuthorizationPolicy.CreateContextAsync(repo, a.Id, "sw-worker", default))!);
        Assert.True(metadata.OperationAccess!.CanCheckout, metadata.OperationAccess.Reason + "; folder=" + metadata.FolderId + "; "
            + string.Join("; ", decisions.Where(item => codes.Contains(item.Permission)).Select(item => item.Permission + "=" + item.Allowed + ":" + item.Reason)));
        Assert.True(metadata.OperationAccess.CanArchive);
        Assert.Equal(first.ProjectId, metadata.ProjectId); // No manifest or file storage needed.
        var session = Guid.NewGuid();
        var acquired = await client.PostAsJsonAsync($"/api/documents/{first.Id}/checkout", new { sessionId = session, machineName = "QA" });
        Assert.True(acquired.IsSuccessStatusCode, await acquired.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/documents/{second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/documents/{second.Id}/checkout", new { sessionId = session, machineName = "QA" })).StatusCode);
        await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(item => item.Key == template.Key
            ? item with { Grants = item.Grants.Where(grant => grant.Permission != "document.checkout").ToArray() } : item).ToArray() }, settings.Revision, default);
        var revoked = (await client.GetFromJsonAsync<PdmDocument>($"/api/documents/{first.Id}"))!;
        Assert.False(revoked.OperationAccess!.CanCheckout);
        Assert.Contains("未授权", revoked.OperationAccess.Reason);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/documents/{first.Id}/checkout", new { sessionId = session, machineName = "QA" })).StatusCode);
        client.DefaultRequestHeaders.Add("X-Company-Id", Guid.NewGuid().ToString());
        var foreign = await client.GetAsync($"/api/documents/{first.Id}");
        Assert.False(foreign.IsSuccessStatusCode);
        Assert.False((await client.PostAsJsonAsync($"/api/documents/{first.Id}/checkout", new { sessionId = session, machineName = "QA" })).IsSuccessStatusCode);
    }
}
