using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class BomPublishedRevisionApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Theory]
    [InlineData(BomKind.Standard, ReleaseScope.StandardFormal, "S")]
    [InlineData(BomKind.Electrical, ReleaseScope.ElectricalFormal, "E")]
    public async Task CurrentBomAndExportUseTheirPublishedBomVersion(BomKind kind, ReleaseScope scope, string prefix)
    {
        using var services = factory.Services.CreateScope();
        var repository = services.ServiceProvider.GetRequiredService<IPdmRepository>();
        var project = await ApiTestAuthorization.CreateProjectAsync(repository, new("VERSION-" + Guid.NewGuid().ToString("N"), "BOM版本验证", "admin", "vault", "release"), "admin", default);
        var row = new BomItem(Guid.NewGuid(), project.Id, kind, 1, "01020000999", "版本验证物料", 1, "001", null, "MODEL", "W1", true);
        var added = row with { Id = Guid.NewGuid(), Sequence = 2, DrawingNumber = "01020000998" };
        await repository.ReplaceBomAsync(project.Id, kind, [row, added], default);
        await repository.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-VERSION", ReleasePackageState.Published, null, "", kind == BomKind.Electrical ? $"{prefix}-{project.Code}-B06" : "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
        { Scope = scope, StandardBomRevision = kind == BomKind.Standard ? $"{prefix}-{project.Code}-B06" : null,
            StandardBomSnapshot = kind == BomKind.Standard ? [row] : [], ElectricalBomSnapshot = kind == BomKind.Electrical ? [row] : [] }, default);
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project.Id}/boms/{kind}?summary=true")).EnumerateArray().ToArray();
        var published = Assert.Single(rows, item => item.GetProperty("drawingNumber").GetString() == row.DrawingNumber);
        Assert.Equal($"{prefix}-{project.Code}-V06", published.GetProperty("displayRevision").GetString());
        Assert.Equal("W1", published.GetProperty("revision").GetString());
        Assert.Equal("未发布", Assert.Single(rows, item => item.GetProperty("drawingNumber").GetString() == added.DrawingNumber).GetProperty("displayRevision").GetString());
        using var workbook = new MemoryStream(await client.GetByteArrayAsync($"/api/projects/{project.Id}/boms/{kind}/export?mode=Summary"));
        var exported = BomWorkbook.Read(workbook);
        Assert.Equal("W1", Assert.Single(exported, item => item.DrawingNumber == row.DrawingNumber).Revision);
        workbook.Position = 0;
        using var archive = new ZipArchive(workbook, ZipArchiveMode.Read);
        using var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var dataRow = XDocument.Load(sheet).Descendants(ns + "row").Single(element => element.Elements(ns + "c").Any(cell => cell.Value == row.DrawingNumber));
        Assert.Equal($"{prefix}-{project.Code}-V06", dataRow.Elements(ns + "c").Single(cell => cell.Attribute("r")!.Value.StartsWith("Q")).Value);
    }
}
