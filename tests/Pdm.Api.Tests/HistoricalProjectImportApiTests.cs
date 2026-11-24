using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Xunit.Abstractions;

namespace Upton.Pdm.Api.Tests;

public sealed class HistoricalProjectImportApiTests(ITestOutputHelper output)
{
    private static readonly Guid Company = Guid.Parse("70000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task LegacyWorkbookPreservesIdentityAndDatesAndMakesRepeatedCommitSafe()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client, "admin", UserRole.Administrator);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var workbook = Workbook([
            Row("P702229", "设备产线", "AK-1-C00465-765-00", "70006483", "46347",
                "订单日期：2026年11月18日 交货日期：2027年3月25日"),
            Row("P702229-3", "扭矩设备", "AK-1-C00465-765-03", "70006486\n70006487", "46350"),
            Row("P702229-1", "检测设备", "AK-1-C00465-765-01", "70006484", "46347")
        ]);
        var before = (await repository.ListProjectsAsync(default)).Count;
        var preview = await Post(client, workbook, true);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(before, (await repository.ListProjectsAsync(default)).Count);

        var writes = await Task.WhenAll(Post(client, workbook, false), Post(client, workbook, false));
        Assert.Single(writes, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(writes, result => result.StatusCode == HttpStatusCode.Conflict);
        var projects = await repository.ListProjectsAsync(default);
        Assert.Equal(before + 3, projects.Count);
        var root = Assert.Single(projects, project => project.Code == "P702229");
        var child = Assert.Single(projects, project => project.Code == "P702229-3");
        Assert.Equal(root.Id, child.ParentProjectId);
        Assert.Equal(root.Id, child.RootProjectId);
        Assert.Equal(3, child.ChildSequence);
        Assert.Equal("AK-1-C00465-765-03", child.DeviceModel);
        Assert.Equal(new[] { "70006486", "70006487" }, child.SerialNumbers);
        Assert.Equal(new DateOnly(2026, 11, 24), child.HistoricalUpdatedDate);
        Assert.Equal(new DateOnly(2026, 11, 18), root.SignedDate);
        Assert.Equal(new DateOnly(2027, 3, 25), root.DeliveryDate);
        Assert.Contains("交货日期", root.HistoricalRemarks);
        Assert.True((await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Single(item => item.Id == Company).CurrentSerialSequence >= 6487);

        var conflict = await Post(client, Workbook([
            Row("P702230", "不应写入的主项目", "AK-1-C00465-766-00", "70006488", ""),
            Row("P702230-1", "编号冲突", "AK-1-C00465-766-01", "70006486", "")
        ]), false);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.DoesNotContain(await repository.ListProjectsAsync(default), project => project.Code == "P702230");
    }

    [Fact]
    public async Task CustomCodesAndUnsyncedCustomerArePreservedWithExplicitHierarchy()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client, "admin", UserRole.Administrator);
        var workbook = Workbook([
            ["项目号", "项目名称", "设备型号", "序列号", "客户名称", "上级项目号"],
            ["子档.03", "原设备", "MODEL S", "SN-0000000002", "原始客户", "LEGACY-2023"],
            ["LEGACY-2023", "原主项目", "MODEL R", "SN-0000000001", "原始客户", ""],
            ["P702229", "原自动编号项目", "AK-1-C00613-001-00", "70006483", "Sogefi Air & Cooling Canada Corp.", ""]
        ]);
        using var response = await Post(client, workbook, false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var projects = await factory.Services.GetRequiredService<IPdmRepository>().ListProjectsAsync(default);
        var parent = Assert.Single(projects, project => project.Code == "LEGACY-2023");
        var child = Assert.Single(projects, project => project.Code == "子档.03");
        var numbered = Assert.Single(projects, project => project.Code == "P702229");
        Assert.Equal(parent.Id, child.ParentProjectId);
        Assert.Equal("MODEL S", child.DeviceModel);
        Assert.Equal("SN-0000000002", Assert.Single(child.SerialNumbers));
        Assert.Equal("原始客户", child.CustomerName);
        Assert.Equal("Sogefi Air & Cooling Canada Corp.", numbered.CustomerName);
        Assert.Equal("C00613", numbered.CustomerCode);
    }

    [Fact]
    public async Task ImportRequiresExplicitPermissionAndDefaultUpgradeDoesNotRestoreRevocation()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        Assert.Contains(RolePermissionCatalog.Permissions, permission =>
            permission.Code == PermissionCodes.ProjectHistoricalImport && permission.Sensitive);
        var workbook = Workbook([Row("P702229", "历史测试", "AK-1-C00465-765-00", "70006483", "")]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, workbook, true)).StatusCode);
        Authenticate(client, "engineer", UserRole.Engineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, workbook, false)).StatusCode);
        Authenticate(client, "admin", UserRole.Administrator);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, workbook, true)).StatusCode);
        var outsideCompany = Guid.Parse("30000000-0000-0000-0000-000000000001");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, workbook, false, outsideCompany)).StatusCode);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        Assert.True(settings.HistoricalProjectImportDefaultsApplied);
        Assert.Single(settings.Templates.Where(template => template.Grants.Any(grant => grant.Permission == PermissionCodes.ProjectHistoricalImport)),
            template => template.RoleCode == nameof(UserRole.Administrator));
        var revoked = settings with
        {
            Templates = settings.Templates.Select(template => template with
            {
                Grants = template.Grants.Where(grant => grant.Permission != PermissionCodes.ProjectHistoricalImport).ToArray()
            }).ToArray()
        };
        await repository.SaveAuthorizationSettingsAsync(revoked, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, workbook, false)).StatusCode);
        Assert.DoesNotContain(AuthorizationMigration.EnableHistoricalProjectImportDefaults(revoked).Templates.SelectMany(role => role.Grants),
            grant => grant.Permission == PermissionCodes.ProjectHistoricalImport);
    }

    [Fact]
    public async Task MaximumWorkbookPreviewAndCommitHaveBoundedScope()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client, "admin", UserRole.Administrator);
        var rows = Enumerable.Range(1, 50).SelectMany(root => Enumerable.Range(0, 4).Select(child =>
            Row($"P78{root:D4}" + (child == 0 ? "" : $"-{child}"), $"历史测试 {root}-{child}",
                $"AK-1-C00465-{800 + root:D3}-{child:D2}", $"701{root * 4 + child:D5}", ""))).ToArray();
        var workbook = Workbook(rows);
        var stopwatch = Stopwatch.StartNew();
        var preview = await Post(client, workbook, true);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewMs = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        var commit = await Post(client, workbook, false);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        output.WriteLine($"{rows.Length} rows, {workbook.Length} bytes, in-memory API. Cold preview {previewMs:F1} ms, warm commit {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, byte[] workbook, bool preview, Guid? company = null)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent(workbook), "file", "history.xlsx" } };
        return client.PostAsync($"/api/projects/history/import?organizationId={company ?? Company}&preview={preview}", content);
    }

    private static string[] Row(string code, string name, string model, string serial, string updated, string note = "") =>
        ["", code, name, "", "", model, serial, "C00465", updated, note];

    private static byte[] Workbook(IEnumerable<string[]> rows)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheet = new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows.Select((row, index) =>
                new XElement(ns + "row", new XAttribute("r", index + 1), row.Select((value, column) =>
                    new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + column)}{index + 1}"), new XAttribute("t", "inlineStr"),
                        new XElement(ns + "is", new XElement(ns + "t", value))))))));
            using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
            sheet.Save(stream);
        }
        return output.ToArray();
    }

    private static void Authenticate(HttpClient client, string actor, UserRole role) =>
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, actor), new Claim(ClaimTypes.Role, role.ToString())],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256))));
}
