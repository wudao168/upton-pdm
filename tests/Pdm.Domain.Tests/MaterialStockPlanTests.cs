using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using System.IO.Compression;
using System.Xml.Linq;

namespace Pdm.Domain.Tests;

public sealed class MaterialStockPlanTests
{
    [Fact]
    public void Export_AddsStockLabelAndPurpleWithoutChangingBomQuantitiesOrRevisions()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "01020014079", "键盘", 3, "001", null, "K400+", "W2", true);
        var context = new BomWorkbookExportContext("标准件BOM", "P700016", "主项目", "P700016-1", "子项目", "V05", "机械", "经理", "工程师", [], null, DateTimeOffset.UtcNow);
        var bytes = BomWorkbook.WriteExport([item, item with { Id = Guid.NewGuid(), Sequence = 2, DrawingNumber = "UKIT-000001" }], context);
        var plan = new MaterialStockPlan("01", "7", item.DrawingNumber, 15, 50, true, "001", "个", DateTimeOffset.UtcNow);
        var marked = MaterialStockWorkbook.Apply(bytes, [plan]);
        using var archive = new ZipArchive(new MemoryStream(marked));
        using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var sheet = XDocument.Load(sheetStream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("常备件", sheet.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "T7").Value);
        Assert.Equal("常备件", sheet.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "T8").Value);
        Assert.DoesNotContain(sheet.Descendants(ns + "c"), cell => (string?)cell.Attribute("r") == "T9");
        Assert.Equal("3", sheet.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "P8").Value);
        Assert.Equal("W2", sheet.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "S8").Value);
        using var styleStream = archive.GetEntry("xl/styles.xml")!.Open();
        Assert.Contains("FF7C3AED", XDocument.Load(styleStream).ToString());
        Assert.Equal(3, item.Quantity);
    }
    [Fact]
    public async Task Plans_AreScoped_AndOlderSnapshotsCannotRestoreClearedFlags()
    {
        var repository = new InMemoryMaterialRepository(TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var plan = new MaterialStockPlan("01", "7", "01020014079", 15, 50, true, "001", "个", now);
        await repository.SaveStockPlansAsync([plan, plan with { OrganizationCode = "8" }, plan with { EnterpriseCode = "02" }], default);
        Assert.Single(await repository.ListStockedMaterialPlansAsync("01", "7", default));
        await repository.SaveStockPlansAsync([plan with { SafetyStockQuantity = 0, SyncedAt = now.AddMinutes(1) }], default);
        await repository.SaveStockPlansAsync([plan], default);
        Assert.Empty(await repository.ListStockedMaterialPlansAsync("01", "7", default));
        Assert.Single(await repository.ListStockedMaterialPlansAsync("01", "8", default));
        Assert.Single(await repository.ListStockedMaterialPlansAsync("02", "7", default));
        await repository.SaveStockPlansAsync([plan with { IsEffective = false, SyncedAt = now.AddMinutes(2) }], default);
        Assert.Empty(await repository.ListStockedMaterialPlansAsync("01", "7", default));
        await repository.SaveStockPlansAsync([], default);
        Assert.Empty(await repository.ListStockedMaterialPlansAsync("01", "7", default));
    }
}
