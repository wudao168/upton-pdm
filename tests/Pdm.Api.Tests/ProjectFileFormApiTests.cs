using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectFileFormApiTests
{
    [Fact]
    public async Task CompanyDetailsReuseScopedCompanyManagementAndDoNotExposeValuesInTheDirectory()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetOrganizationDirectoryAsync(default)).Organizations;
        var company = companies[0].Id;
        var other = companies.First(item => item.Id != company).Id;
        const string actor = "company-details-editor";
        const string role = "company-details-test";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "公司资料验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate(role, "公司资料验收", "", [new(PermissionCodes.OrganizationSettingsManage, ["company"])], role);
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template] }, settings.Revision, default);
        Login(client, actor);
        var prefix = $"/api/organizations/{company}/business-details";
        var invoice = new CompanyBusinessDetails(Invoice: new("测试公司", " 测试税号 ", null, null, "测试银行", "0012345"));
        var receiving = new CompanyBusinessDetails(Receiving: new("测试联系人", "0512-00000000", "测试收货地址"));
        Assert.Equal(0, (await Read<CompanyBusinessDetails>(await client.GetAsync(prefix + "/invoice"))).RowVersion);
        var savedInvoice = await Read<CompanyBusinessDetails>(await client.PutAsJsonAsync(prefix + "/invoice", invoice));
        Assert.Equal("测试税号", savedInvoice.Invoice?.TaxNumber);
        Assert.Equal("0012345", savedInvoice.Invoice?.BankAccount);
        Assert.Equal(1, savedInvoice.RowVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(prefix + "/invoice", invoice)).StatusCode);
        Assert.Equal(1, (await Read<CompanyBusinessDetails>(await client.PutAsJsonAsync(prefix + "/receiving", receiving))).RowVersion);
        Assert.Null((await Read<CompanyBusinessDetails>(await client.GetAsync(prefix + "/receiving"))).Invoice);
        var status = Assert.Single(await Read<CompanyBusinessDetailsStatus[]>(await client.GetAsync("/api/organizations/business-details")));
        Assert.Equal(new(company, true, true), status);
        var otherPath = $"/api/organizations/{other}/business-details/invoice";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(otherPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(otherPath, invoice)).StatusCode);
        var directory = await client.GetStringAsync("/api/organization-directory");
        Assert.DoesNotContain("0012345", directory);
        Assert.DoesNotContain("测试收货地址", directory);
        await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template with { Grants = [] }] }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/receiving")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(prefix + "/receiving", receiving)).StatusCode);
    }

    [Fact]
    public async Task AuthorizedProjectPositionsGenerateVersionedWorkbooksAndEvidenceAndRevocationTakesEffect()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First().Id;
        var root = Path.Combine(AppContext.BaseDirectory, ".data", "project-form-tests", Guid.NewGuid().ToString("N"));
        var storage = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(storage with { VaultRoot = root }, default);
        foreach (var (login, name) in new[] { ("form-manager", "样件经理"), ("form-lead", "主设验收"), ("form-other", "未分派人员") })
            await repository.CreateUserAsync(new(Guid.NewGuid(), login, name, "unused", UserRole.Engineer, true, RoleCode: "form-role", CompanyId: company), default);
        async Task<Project> Project(string name) => await repository.CreateNumberedProjectAsync(
            new(company, "P", 2, customer, name, null, null, 1, "form-manager", root, Path.Combine(root, "release")), default);
        var project = await Project("单据存档验收");
        var other = await Project("无岗位项目");
        await repository.SetMainProjectStaffingAsync(project.Id, new("form-manager", [], ["form-lead"]), "form-manager", default);
        var child = await repository.CreateSubprojectAsync(new(project.Id, "设计人员负责的子项目", null, 1), default);
        await repository.SetChildProjectDesignersAsync(child.Id, ["form-other"], "form-manager", default);
        await repository.EnsureProjectFolderTreeAsync(project.Id, default);
        await repository.EnsureProjectFolderTreeAsync(other.Id, default);
        var permissions = ProjectFileFormPermissions.Definitions().ToArray();
        Assert.All(permissions, item => Assert.Contains(RolePermissionCatalog.Permissions, permission => permission.Code == item.Code && permission.Sensitive));
        Assert.DoesNotContain(RolePermissionCatalog.InitialPermissions("Administrator", UserRole.Administrator), ProjectFileFormPermissions.RequiresExplicitGrant);
        var existing = await repository.GetAuthorizationSettingsAsync(default);
        var prerequisites = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.RoleSettingsView, ProjectCenterPermissions.Tab("files"),
            ProjectCenterPermissions.Write, ProjectCenterPermissions.Export, ProjectCenterPermissions.Download, ProjectCenterPermissions.Tab("validation-plan"),
            "project.file.view", "project.file.upload", "project.file.download", AuthorizationActions.FolderView, AuthorizationActions.FolderUpload, AuthorizationActions.FolderDownload };
        var template = new AuthorizationTemplate("form-template", "单据验收", "", prerequisites.Select(code => new AuthorizationGrant(code, ["company"])).ToArray())
            with { RoleCode = "form-role" };
        var settings = await repository.SaveAuthorizationSettingsAsync(existing with { Templates = [template] }, existing.Revision, default);
        var prefix = $"/api/projects/{project.Id}/files";
        var samplePath = prefix + "/sample-demand";
        var changePath = prefix + "/customer-change";
        var receiving = new CompanyReceivingDetails("测试收货", "0512-00000000", "本公司测试收货地址");
        await repository.SaveCompanyBusinessDetailsAsync(company, "receiving", new(Receiving: receiving), default);
        Login(client, "form-manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(samplePath)).StatusCode);
        var catalog = await Read<JsonElement>(await client.GetAsync("/api/role-permissions"));
        Assert.All(permissions, permission => Assert.Contains(catalog.GetProperty("permissions").EnumerateArray(),
            item => item.GetProperty("code").GetString() == permission.Code));
        var grants = template.Grants.Concat(permissions.Select(permission => new AuthorizationGrant(permission.Code, ["MainManager", "ChildManager", "MainDesigner", "current-designer"]))).ToArray();
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template with { Grants = grants }] }, settings.Revision, default);
        var sampleIndex = await Read<ProjectFileFormIndex>(await client.GetAsync(samplePath));
        Assert.True(sampleIndex.CanArchive);
        Assert.Equal(receiving, sampleIndex.Receiving);
        Assert.Null((await Read<ProjectFileFormIndex>(await client.GetAsync(changePath))).Receiving);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{other.Id}/files/sample-demand")).StatusCode);
        Login(client, "form-other");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(samplePath)).StatusCode);
        var childIndex = await Read<ProjectFileFormIndex>(await client.GetAsync($"/api/projects/{child.Id}/files/sample-demand"));
        Assert.True(childIndex.CanEdit);

        Login(client, "form-lead");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jWwAAAABJRU5ErkJggg==");
        var id = Guid.NewGuid();
        var sample = new ProjectFileFormData(Recipient: "收货姓名", Contact: "0512-00000000", Address: "样件测试地址",
            Items: [new("导轨", 2, "件", new(2026, 11, 10), "装配", "首版", new(Guid.NewGuid(), "导轨.png", png))]);
        var invalid = sample with { Items = [sample.Items![0] with { Quantity = 1.5m }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{samplePath}/{id}", new SaveProjectFileForm(0, invalid))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{samplePath}/{id}", new SaveProjectFileForm(0,
            sample with { Items = [sample.Items![0] with { Phase = "未定义阶段" }] }))).StatusCode);
        var draft = await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{samplePath}/{id}", new SaveProjectFileForm(0, sample)));
        Assert.Equal(1, draft.Summary.RowVersion);
        var archivePath = $"{samplePath}/{id}/archive";
        var v1 = await Archive(archivePath, 1);
        Assert.Equal(1, v1.FileVersion);
        var repeat = await Archive(archivePath, 1);
        Assert.Equal(v1.FileId, repeat.FileId);
        Assert.Equal(1, repeat.FileVersion);
        var sampleBytes = await client.GetByteArrayAsync($"{prefix}/{v1.FileId}/content");
        using (var zip = new ZipArchive(new MemoryStream(sampleBytes)))
        {
            Assert.NotNull(zip.GetEntry("xl/media/uplm-company-logo.png"));
            Assert.NotNull(zip.GetEntry("xl/media/sample-picture-1.png"));
            Assert.Contains("导轨", ReadXml(zip, "xl/worksheets/sheet1.xml").Value);
            Assert.Contains("收货姓名", ReadXml(zip, "xl/worksheets/sheet1.xml").Value);
            Assert.Contains("主设验收", ReadXml(zip, "xl/worksheets/sheet1.xml").Value);
        }
        var modified = sample with { Items = [sample.Items![0] with { Quantity = 3 }] };
        await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{samplePath}/{id}", new SaveProjectFileForm(1, modified)));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(archivePath, new { expectedRowVersion = 1 })).StatusCode);
        var v2 = await Archive(archivePath, 2);
        Assert.Equal(v1.FileId, v2.FileId);
        Assert.Equal(2, v2.FileVersion);
        var versions = await Read<ProjectFileVersion[]>(await client.GetAsync($"{prefix}/{v1.FileId}/versions"));
        Assert.Equal([2, 1], versions.Select(version => version.VersionNumber));
        Assert.Equal(sampleBytes, await client.GetByteArrayAsync($"{prefix}/{v1.FileId}/content?versionId={versions[1].Id}"));

        var secondSample = await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{samplePath}/{Guid.NewGuid()}",
            new SaveProjectFileForm(0, sample with { Recipient = "第二份样件收货人" })));
        Assert.NotEqual(draft.Summary.Number, secondSample.Summary.Number);
        var secondSampleArchive = await Archive($"{samplePath}/{secondSample.Summary.Id}/archive", 1);
        Assert.NotEqual(v1.FileId, secondSampleArchive.FileId);
        Assert.Equal(1, secondSampleArchive.FileVersion);
        Assert.Equal(2, (await Read<ProjectFileFormIndex>(await client.GetAsync(samplePath))).Forms.Count);
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"{prefix}/{secondSampleArchive.FileId}/content"))))
            Assert.Contains("第二份样件收货人", ReadXml(zip, "xl/worksheets/sheet1.xml").Value);

        var change = new ProjectFileFormData(Supplier: "供方公司", Customer: "需方公司", ChangedOn: new(2026, 11, 9),
            Reason: "客户确认调整", Before: "原规格", After: "新规格", Paid: true, Note: "已收到邮件",
            Evidence: [new(Guid.NewGuid(), "客户确认.eml", Encoding.UTF8.GetBytes("Subject: approval\r\n\r\nConfirmed."))]);
        var firstId = Guid.NewGuid();
        var first = await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{changePath}/{firstId}", new SaveProjectFileForm(0, change)));
        Assert.Null(Assert.Single(first.Data.Evidence!).Content);
        var second = await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{changePath}/{Guid.NewGuid()}", new SaveProjectFileForm(0, change with { Reason = "另一项变更" })));
        Assert.NotEqual(first.Summary.Number, second.Summary.Number);
        var changeArchive = await Archive($"{changePath}/{firstId}/archive", 1);
        Assert.NotNull(changeArchive.EvidenceFileId);
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"{prefix}/{changeArchive.EvidenceFileId}/content"))))
            Assert.Equal("01_客户确认.eml", Assert.Single(zip.Entries).FullName);
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync($"{prefix}/{changeArchive.FileId}/content"))))
        {
            var sheet = ReadXml(zip, "xl/worksheets/sheet1.xml");
            Assert.Contains("需方签字", sheet.Value);
            Assert.Contains("客户确认.eml", sheet.Value);
            var ns = sheet.Name.Namespace;
            var styles = ReadXml(zip, "xl/styles.xml").Element(ns + "cellXfs")!.Elements().ToArray();
            foreach (var text in new[] { "变更原因", "变更前内容", "变更后内容", "备注", "客户确认调整", "原规格", "新规格", "已收到邮件" })
            {
                var cell = Assert.Single(sheet.Descendants(ns + "c"), cell => cell.Value == text);
                Assert.Equal("left", (string?)styles[(int)cell.Attribute("s")!].Element(ns + "alignment")!.Attribute("horizontal"));
            }
            var drawing = ReadXml(zip, "xl/drawings/uplm-header1.xml");
            var draw = drawing.Name.Namespace;
            var from = drawing.Element(draw + "oneCellAnchor")!.Element(draw + "from")!;
            Assert.Equal("0", from.Element(draw + "col")!.Value);
            Assert.Equal("0", from.Element(draw + "colOff")!.Value);
        }
        Assert.Equal(3, changeArchive.ArchivedLayoutVersion);
        Assert.Equal(1, (await Archive($"{changePath}/{firstId}/archive", 1)).FileVersion);
        var secondChangeArchive = await Archive($"{changePath}/{second.Summary.Id}/archive", 1);
        Assert.NotEqual(changeArchive.FileId, secondChangeArchive.FileId);
        Assert.Equal(1, secondChangeArchive.FileVersion);

        var exports = new List<string>();
        foreach (var (kind, title, suffix) in new[] { ("pre-acceptance", "预验收单", "PAT"), ("final-acceptance", "终验收单", "FAT") })
        {
            var path = prefix + "/" + kind;
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/projects/{other.Id}/files/{kind}")).StatusCode);
            var index = await Read<ProjectFileFormIndex>(await client.GetAsync(path));
            Assert.False(index.CanArchive);
            Assert.True(index.CanExport);
            var acceptanceId = Guid.NewGuid();
            var data = new ProjectFileFormData(Supplier: index.Company, Customer: "需方公司",
                Acceptance: new(ContractNumber: "TEST-2026", Location: "验收现场", Standard: "技术协议", Comments: "意见待签字"));
            var saved = await Read<ProjectFileForm>(await client.PutAsJsonAsync($"{path}/{acceptanceId}", new SaveProjectFileForm(0, data)));
            Assert.EndsWith(suffix, saved.Summary.Number);
            var excelPath = $"{path}/{acceptanceId}/excel";
            exports.Add(excelPath);
            using var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync(excelPath)));
            var cells = ReadXml(zip, "xl/worksheets/sheet1.xml").Value;
            foreach (var value in new[] { title, project.Code, "TEST-2026", "草稿", "需方确认", "验收日期", "□ 验收合格　□ 验收不合格" })
                Assert.Contains(value, cells);
            Assert.Null((await Read<ProjectFileFormIndex>(await client.GetAsync(path))).Forms.Single().FileId);
            using var blocked = await client.PostAsJsonAsync($"{path}/{acceptanceId}/archive", new { expectedRowVersion = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{path}/{Guid.NewGuid()}", new SaveProjectFileForm(0, data))).StatusCode);
        }

        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template with
        {
            Grants = grants.Where(grant => grant.Permission is not (AuthorizationActions.FolderUpload or ProjectCenterPermissions.Export)).ToArray()
        }] }, settings.Revision, default);
        Assert.False((await Read<ProjectFileFormIndex>(await client.GetAsync(changePath))).CanArchive);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{changePath}/{second.Summary.Id}/archive", new { expectedRowVersion = 1 })).StatusCode);
        foreach (var excelPath in exports)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(excelPath)).StatusCode);
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template] }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{changePath}/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{changePath}/{firstId}", new SaveProjectFileForm(1, change))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/pre-acceptance")).StatusCode);

        async Task<ProjectFileFormSummary> Archive(string path, long revision) =>
            await Read<ProjectFileFormSummary>(await client.PostAsJsonAsync(path, new { expectedRowVersion = revision }));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }

    private static XElement ReadXml(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XElement.Load(stream);
    }

    private static void Login(HttpClient client, string actor)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026"));
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }
}
