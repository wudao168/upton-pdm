using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MicrosoftIdentityModelTokens = Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BatchPropertyAccessApiTests
{
    [Fact]
    public async Task DirectWritebackOperationsRequireCurrentResourceAuthorization()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
        var project = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "属性权限验证", null, null, 1, "property-worker", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        var other = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "无权属性项目", null, null, 1, "property-worker", @"C:\PDM\Vault", @"C:\PDM\Release"), default);
        await repo.CreateUserAsync(new(Guid.NewGuid(), "property-worker", "属性测试人员", "unused", UserRole.Engineer, true, RoleCode: "property-worker", CompanyId: company), default);
        var document = await repo.RegisterDocumentAsync(new(project.Id, "PROP", "属性图档", "PROP.SLDPRT", DocumentKind.Part), "admin", default);
        var foreign = await repo.RegisterDocumentAsync(new(other.Id, "FOREIGN", "其他项目图档", "FOREIGN.SLDPRT", DocumentKind.Part), "admin", default);
        var codes = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.DocumentEdit,
            ProjectCenterPermissions.Tab("documents"), ProjectCenterPermissions.Write,
            "document.archive", "document.checkout", AuthorizationActions.ArchiveSubmit,
            AuthorizationActions.FolderView, AuthorizationActions.FolderEdit, AuthorizationActions.FolderDownload };
        var grants = codes.ToHashSet();
        var pending = new Queue<string>(codes);
        while (pending.TryDequeue(out var code))
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies)
                if (grants.Add(dependency)) pending.Enqueue(dependency);
        var template = new AuthorizationTemplate("property-test", "属性写回测试权限", "", grants.Select(code => new AuthorizationGrant(code, ["company"])).ToArray()) { ProjectAssignable = true };
        var original = await repo.GetAuthorizationSettingsAsync(default);
        var settings = await repo.SaveAuthorizationSettingsAsync(original with { Templates = [.. original.Templates, template],
            Assignments = [.. original.Assignments, new(Guid.NewGuid(), "property-worker", template.Key, company, "project", project.Id)] }, original.Revision, default);
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "property-worker"), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new MicrosoftIdentityModelTokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), MicrosoftIdentityModelTokens.SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        async Task<DocumentVersion> Archive(char hash)
        {
            var checkedOut = await repo.CheckoutAsync(document.Id, "property-worker", default);
            var root = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName,
                document.Name, document.Kind, "默认", 1, ReferenceNodeStatus.Normal, checkedOut.Revision, "property-worker", []);
            var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "property-worker", root, new string(hash, 64));
            var result = await repo.CheckInVersionAsync(document.Id, "property-worker", new DocumentVersionCommit(
                new StoredFile(".versions/property-test/" + hash, 128, new string(hash, 64), DateTimeOffset.UtcNow),
                "属性验证版本", new Dictionary<string, string?> { ["型号"] = hash.ToString() }, snapshot, [], []), default);
            return Assert.IsType<DocumentVersion>(result.Version);
        }
        async Task<CadPropertyWriteback> Request(PdmDocument source)
        {
            var latest = (await repo.ListDocumentVersionsAsync(source.Id, default)).FirstOrDefault();
            return await repo.EnqueueCadPropertyWritebackAsync(new(Guid.NewGuid(), source.ProjectId, Guid.NewGuid(), source.Id,
                "默认", latest?.Id ?? Guid.NewGuid(), latest?.Revision.Display ?? "W1",
                new Dictionary<string, string?> { ["型号"] = "验证" }, CadPropertyWritebackStatus.Pending, "admin", DateTimeOffset.UtcNow), default);
        }
        async Task Denied(CadPropertyWriteback request)
        {
            foreach (var operation in new[] { "start", "complete", "fail" })
            {
                var result = await client.PostAsJsonAsync($"/api/cad-property-writebacks/{request.Id}/{operation}",
                    new { resultVersionId = request.ExpectedVersionId, error = "测试失败原因", conflict = false });
                Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            }
            Assert.Equal(CadPropertyWritebackStatus.Pending, (await repo.FindCadPropertyWritebackAsync(request.Id, default))!.Status);
        }

        await Archive('A');
        var allowed = await Request(document);
        var metadata = (await client.GetFromJsonAsync<PdmDocument>($"/api/documents/{document.Id}"))!;
        Assert.True(metadata.OperationAccess!.CanArchive, metadata.OperationAccess.Reason);
        var started = await client.PostAsJsonAsync($"/api/cad-property-writebacks/{allowed.Id}/start", new { });
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        var written = await Archive('B');
        var completed = await client.PostAsJsonAsync($"/api/cad-property-writebacks/{allowed.Id}/complete", new { resultVersionId = written.Id });
        Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync());
        Assert.Equal(CadPropertyWritebackStatus.Succeeded, (await repo.FindCadPropertyWritebackAsync(allowed.Id, default))!.Status);
        var toFail = await Request(document);
        var failed = await client.PostAsJsonAsync($"/api/cad-property-writebacks/{toFail.Id}/fail", new { error = "测试中止", conflict = false });
        Assert.True(failed.IsSuccessStatusCode, await failed.Content.ReadAsStringAsync());
        Assert.Equal(CadPropertyWritebackStatus.Failed, (await repo.FindCadPropertyWritebackAsync(toFail.Id, default))!.Status);
        await Denied(await Request(foreign));

        // Revoke only the operation, retaining project and document read access.
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(item => item.Key == template.Key
            ? item with { Grants = item.Grants.Where(grant => grant.Permission != "document.archive").ToArray() } : item).ToArray() }, settings.Revision, default);
        var revoked = (await client.GetFromJsonAsync<PdmDocument>($"/api/documents/{document.Id}"))!;
        Assert.False(revoked.OperationAccess!.CanArchive);
        await Denied(await Request(document));

        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(item => item.Key == template.Key
            ? template : item).ToArray() }, settings.Revision, default);
        Assert.True((await client.GetFromJsonAsync<PdmDocument>($"/api/documents/{document.Id}"))!.OperationAccess!.CanArchive);
        client.DefaultRequestHeaders.Add("X-Company-Id", Guid.NewGuid().ToString());
        var companyRequest = await Request(document);
        foreach (var operation in new[] { "start", "complete", "fail" })
            Assert.False((await client.PostAsJsonAsync($"/api/cad-property-writebacks/{companyRequest.Id}/{operation}",
                new { resultVersionId = companyRequest.ExpectedVersionId, error = "测试失败原因", conflict = false })).IsSuccessStatusCode);
        Assert.Equal(CadPropertyWritebackStatus.Pending, (await repo.FindCadPropertyWritebackAsync(companyRequest.Id, default))!.Status);
    }
}
