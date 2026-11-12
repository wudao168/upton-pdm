using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
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

public sealed class PurchaseAnalysisApiTests
{
    [Fact]
    public async Task CatalogueScopeCostExportAndRevocationAreEnforcedAtDirectEndpoints()
    {
        await using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        const string path = "/api/purchase-analysis"; var filter = new PurchaseAnalysisFilter { FrequentOrders = 2 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path + "/query", filter)).StatusCode);
        var repo = factory.Services.GetRequiredService<IPdmRepository>(); var store = (IPurchaseAnalysisStore)repo;
        var directory = await repo.GetOrganizationDirectoryAsync(default); var company = directory.Organizations[0].Id; var other = directory.Organizations[1].Id;
        var customer = (await repo.ListCustomersAsync(true, default)).First().Id;
        const string actor = "purchase-analysis-qa"; const string role = "purchase-analysis-qa-role";
        await repo.CreateUserAsync(new(Guid.NewGuid(), actor, "采购分析验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        await repo.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var storage = Path.Combine(AppContext.BaseDirectory, ".data", "purchase-analysis-tests", Guid.NewGuid().ToString("N"));
        var system = await repo.GetSystemSettingsAsync(default); await repo.UpdateSystemSettingsAsync(system with { VaultRoot = storage }, default);
        var first = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "可查看项目", null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        var second = await repo.CreateNumberedProjectAsync(new(company, "P", 2, customer, "范围外项目", null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        await store.SaveSettingsAsync(company, new(0, ["ORG1"]), default); await store.SaveSettingsAsync(other, new(0, ["ORG2"]), default);
        var now = DateTimeOffset.UtcNow; var day = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).Date);
        PurchaseAnalysisLine Line(string id, string project, string org = "ORG1") => new(id, org, id, "PO-" + id, 1, day, "PO01", "标准采购", "M-" + id,
            "物料" + id, "规格", "品牌", "0102", "外购件", "S-" + id, "供应商" + id, "B1", "姓名待补充", project, "", "001", "个", 10, "人民币", 200);
        await store.ReplaceAsync(company, 1, new(new(now, now, day.AddYears(-2), day), [Line("1", first.Code), Line("2", second.Code), Line("3", "")]), default);
        await store.ReplaceAsync(other, 1, new(new(now, now, day.AddYears(-2), day), [Line("FOREIGN", "", "ORG2")]), default);
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var settings = await repo.GetAuthorizationSettingsAsync(default);
        var common = new AuthorizationTemplate(role, "分析验收目录", "", [new(PermissionCodes.RoleSettingsView, ["company"])], role);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = [..settings.Templates, common] }, settings.Revision, default);
        var catalogue = await client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        Assert.Contains(catalogue.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("code").GetString() == PurchaseAnalysisPermissions.View);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/query", filter)).StatusCode);
        var template = new AuthorizationTemplate("analysis-project", "指定项目采购分析", "", Grants(PurchaseAnalysisPermissions.View, PurchaseAnalysisPermissions.Export));
        var assignment = new AuthorizationAssignment(Guid.NewGuid(), actor, template.Key, company, "project", first.Id);
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = [..settings.Templates, template], Assignments = [..settings.Assignments, assignment] }, settings.Revision, default);
        var result = await Query(); var line = Assert.Single(result.Details); Assert.Equal("M-1", line.MaterialCode); Assert.Null(line.NetAmount);
        Assert.Empty(result.Amounts); Assert.Empty(result.Spend); Assert.Single(result.Options["suppliers"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/query", filter with { MinAmount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path + "/synchronize", null)).StatusCode);
        var export = await client.PostAsJsonAsync(path + "/export", filter); Assert.True(export.IsSuccessStatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync()))) {
            Assert.NotNull(zip.GetEntry("xl/media/uplm-company-logo.png")); using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = await reader.ReadToEndAsync(); Assert.Contains("M-1", xml); Assert.DoesNotContain("M-2", xml); Assert.DoesNotContain("未税单价", xml);
            var sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml")).ToArray();
            Assert.Equal(4, sheets.Length);
            foreach (var sheet in sheets) { using var contents = new StreamReader(sheet.Open()); var content = await contents.ReadToEndAsync();
                Assert.DoesNotContain("M-2", content); Assert.DoesNotContain("未税金额", content); Assert.DoesNotContain("加权均价", content); }
        }
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(t => t.Key == template.Key ? t with { Grants = Grants(PurchaseAnalysisPermissions.View, PurchaseAnalysisPermissions.Cost, PurchaseAnalysisPermissions.Export) } : t).ToArray() }, settings.Revision, default);
        Assert.Equal(200, Assert.Single((await Query()).Details).NetAmount);
        using (var zip = new ZipArchive(new MemoryStream(await (await client.PostAsJsonAsync(path + "/export", filter)).Content.ReadAsByteArrayAsync())))
            Assert.Equal(7, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet") && e.FullName.EndsWith(".xml")));
        settings = await repo.SaveAuthorizationSettingsAsync(settings with { Assignments = settings.Assignments.Select(a => a.Id == assignment.Id ? a with { Scope = "company", TargetId = null } : a).ToArray() }, settings.Revision, default);
        Assert.Equal(3, (await Query()).DetailCount);
        client.DefaultRequestHeaders.Remove("X-Company-Id"); client.DefaultRequestHeaders.Add("X-Company-Id", other.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path + "/query", filter)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id"); client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        await repo.SaveAuthorizationSettingsAsync(settings with { Assignments = settings.Assignments.Where(a => a.Id != assignment.Id).ToArray() }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/query", filter)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/export", filter)).StatusCode);
        async Task<PurchaseAnalysisResult> Query() {
            var response = await client.PostAsJsonAsync(path + "/query", filter); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(); return json.GetProperty("data").Deserialize<PurchaseAnalysisResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
    private static AuthorizationGrant[] Grants(params string[] permissions) => permissions.Select(p => new AuthorizationGrant(p, ["company"])).ToArray();
}
