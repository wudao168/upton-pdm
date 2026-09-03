using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomWorkbookTests
{
    [Theory]
    [InlineData("关联料号")]
    [InlineData("父项料号")]
    [InlineData("上级物料编码")]
    public void Read_AcceptsOldAndNewParentMaterialHeaders(string header)
    {
        using var stream = new MemoryStream();
        stream.Write(BomWorkbook.Write(new[]
        {
            new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "PART", "零件", 1, "个", null, null, "W1", true)
            {
                ParentDrawingNumber = "ASSEMBLY"
            }
        }));
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml.Replace("上级物料编码", header));
        }
        stream.Position = 0;
        Assert.Equal("ASSEMBLY", Assert.Single(BomWorkbook.Read(stream)).ParentDrawingNumber);
    }

    [Fact]
    public void WriteAndRead_RoundTripsStandardBomColumns()
    {
        var projectId = Guid.NewGuid();
        var source = new[]
        {
            new BomItem(Guid.NewGuid(), projectId, BomKind.Electrical, 1, "EL-001", "光电传感器", 4, "件", "不锈钢", "M18 PNP", "A", true)
            {
                Remark = "常开型",
                Brand = "SICK",
                SurfaceTreatment = "本色",
                Weight = "0.25 kg",
                ParentDrawingNumber = "ASM-001"
            },
            new BomItem(Guid.NewGuid(), projectId, BomKind.Electrical, 2, "EL-002", "伺服驱动器", 2.5m, "件", "铝", "750W", "W2", false)
        };

        using var stream = new MemoryStream(BomWorkbook.Write(source));
        var imported = BomWorkbook.Read(stream);

        Assert.Equal(2, imported.Count);
        Assert.Equal("EL-001", imported[0].DrawingNumber);
        Assert.Equal(4, imported[0].Quantity);
        Assert.True(imported[0].IsComplete);
        Assert.Equal("常开型", imported[0].Remark);
        Assert.Equal("SICK", imported[0].Brand);
        Assert.Equal("不锈钢", imported[0].Material);
        Assert.Equal("本色", imported[0].SurfaceTreatment);
        Assert.Equal("0.25 kg", imported[0].Weight);
        Assert.Equal("ASM-001", imported[0].ParentDrawingNumber);
        Assert.Equal("750W", imported[1].Specification);
        Assert.False(imported[1].IsComplete);
    }

    [Fact]
    public void Read_AcceptsLegacyHeadersWithoutNewMaterialColumns()
    {
        using var workbook = new MemoryStream();
        using (var archive = new ZipArchive(workbook, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>序号</t></is></c><c r="B1" t="inlineStr"><is><t>图号</t></is></c><c r="C1" t="inlineStr"><is><t>名称</t></is></c><c r="D1" t="inlineStr"><is><t>数量</t></is></c><c r="E1" t="inlineStr"><is><t>单位</t></is></c><c r="F1" t="inlineStr"><is><t>材料</t></is></c><c r="G1" t="inlineStr"><is><t>规格</t></is></c><c r="H1" t="inlineStr"><is><t>版本</t></is></c><c r="I1" t="inlineStr"><is><t>完整</t></is></c></row>
                  <row r="2"><c r="A2"><v>1</v></c><c r="B2" t="inlineStr"><is><t>OLD-001</t></is></c><c r="C2" t="inlineStr"><is><t>旧物料</t></is></c><c r="D2"><v>2</v></c><c r="E2" t="inlineStr"><is><t>件</t></is></c><c r="F2" t="inlineStr"><is><t>钢</t></is></c><c r="G2" t="inlineStr"><is><t>M8</t></is></c><c r="H2" t="inlineStr"><is><t>W1</t></is></c><c r="I2" t="inlineStr"><is><t>是</t></is></c></row>
                </sheetData></worksheet>
                """);
        }
        workbook.Position = 0;

        var imported = BomWorkbook.Read(workbook);

        var item = Assert.Single(imported);
        Assert.Equal("OLD-001", item.DrawingNumber);
        Assert.Equal("旧物料", item.Name);
        Assert.Equal("钢", item.Material);
        Assert.Equal("M8", item.Specification);
        Assert.Null(item.Brand);
        Assert.Null(item.Weight);
    }

    [Fact]
    public void WriteExport_CreatesApprovedTwelveColumnLayoutWithProjectHeaderAndLogo()
    {
        var item = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Standard, 1, "01020013531", "平垫 φ3", 2, "001", "钢", "DIN 125-A", "W2", true)
        {
            Remark = "项目装配标准件",
            Brand = "国优",
            SurfaceTreatment = "镀锌",
            Weight = "1.50"
        };
        var exportedAt = new DateTimeOffset(2026, 9, 3, 10, 32, 18, TimeSpan.FromHours(8));
        var publishedAt = new DateTimeOffset(2026, 9, 2, 16, 20, 0, TimeSpan.FromHours(8));
        var context = new BomWorkbookExportContext(
            "标准件BOM", "P700005", "钢珠检测设备", "P700005-3", "3号工位", "W2", "机械设计部",
            "刘鹏", "王工、吕浩哲",
            [new("工程师", "王工"), new("主设", "马文豪"), new("机械主管", "李工")],
            publishedAt, exportedAt, [1, 2, 3, 4]);

        using var stream = new MemoryStream(BomWorkbook.WriteExport([item], context));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sheet = ReadEntryXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = sheet.Descendants(ns + "row").ToDictionary(row => (int)row.Attribute("r")!);
        var headers = rows[7].Elements(ns + "c").Select(cell => string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))).ToArray();
        var data = rows[8].Elements(ns + "c").Select(cell => string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))).ToArray();
        var metadata = rows.Values
            .Where(row => (int)row.Attribute("r")! is >= 3 and <= 5)
            .SelectMany(row => row.Elements(ns + "c"))
            .ToDictionary(cell => (string)cell.Attribute("r")!, cell => string.Concat(cell.Descendants(ns + "t").Select(text => text.Value)));
        var widths = sheet.Descendants(ns + "col").Select(column => (int)double.Parse((string)column.Attribute("width")!, CultureInfo.InvariantCulture)).ToArray();
        var mergedRanges = sheet.Descendants(ns + "mergeCell").Select(cell => (string)cell.Attribute("ref")!).ToArray();

        Assert.Equal(new[] { "序号", "物料分类", "单位", "物料编码", "物料名称", "型号", "备注信息", "品牌", "材质", "表面处理", "重量", "数量" }, headers);
        Assert.DoesNotContain("上级物料编码", headers);
        Assert.DoesNotContain("版本", headers);
        Assert.DoesNotContain("完整", headers);
        Assert.Equal("个", data[2]);
        Assert.Equal(new[] { 10, 10, 10, 15, 15, 25, 30, 10, 10, 15, 10, 10 }, widths);
        Assert.Contains("A1:L2", mergedRanges);
        Assert.Contains("B3:C3", mergedRanges);
        Assert.Contains("K4:L4", mergedRanges);
        Assert.DoesNotContain("A5:B5", mergedRanges);
        Assert.Contains("B5:C5", mergedRanges);
        Assert.Contains("E5:F5", mergedRanges);
        Assert.Contains("H5:I5", mergedRanges);
        Assert.Contains("K5:L5", mergedRanges);
        Assert.Contains("标准件BOM", sheet.ToString());
        Assert.Contains("主项目号", sheet.ToString());
        Assert.Contains("P700005", sheet.ToString());
        Assert.Contains("主项目名称", sheet.ToString());
        Assert.Contains("钢珠检测设备", sheet.ToString());
        Assert.Contains("工程师", sheet.ToString());
        Assert.Contains("王工、吕浩哲", sheet.ToString());
        Assert.Equal("主项目号", metadata["A3"]);
        Assert.Equal("P700005", metadata["B3"]);
        Assert.Equal("主项目名称", metadata["D3"]);
        Assert.Equal("钢珠检测设备", metadata["E3"]);
        Assert.Equal("王工、吕浩哲", metadata["K4"]);
        Assert.Equal("工程师", metadata["A5"]);
        Assert.Equal("王工", metadata["B5"]);
        Assert.Equal("主设", metadata["D5"]);
        Assert.Equal("马文豪", metadata["E5"]);
        Assert.Equal("机械主管", metadata["G5"]);
        Assert.Equal("李工", metadata["H5"]);
        Assert.Equal("发布日期", metadata["J5"]);
        Assert.Equal("2026-09-02", metadata["K5"]);
        Assert.All(rows[5].Elements(ns + "c").Where(cell => new[] { "A5", "D5", "G5", "J5" }.Contains((string?)cell.Attribute("r"))), cell => Assert.Equal("5", (string?)cell.Attribute("s")));
        Assert.All(rows[3].Elements(ns + "c").Where(cell => new[] { "A3", "D3", "G3", "J3" }.Contains((string?)cell.Attribute("r"))), cell => Assert.Equal("5", (string?)cell.Attribute("s")));
        Assert.All(rows[7].Elements(ns + "c"), cell => Assert.Equal("3", (string?)cell.Attribute("s")));
        Assert.All(rows[8].Elements(ns + "c"), cell => Assert.Equal("4", (string?)cell.Attribute("s")));

        var styles = ReadEntryXml(archive, "xl/styles.xml").ToString();
        Assert.Contains("<b", styles);
        Assert.Contains("horizontal=\"center\"", styles);
        var drawing = ReadEntryXml(archive, "xl/drawings/drawing1.xml").ToString();
        Assert.Contains("cx=\"1466667\"", drawing);
        Assert.Contains("cy=\"188000\"", drawing);
        Assert.Equal(4, archive.GetEntry("xl/media/image1.png")!.Length);
    }

    [Fact]
    public void WriteExport_DisplaysNotPublishedWhenReleaseHasNoPublishedAt()
    {
        var context = new BomWorkbookExportContext(
            "标准件BOM", "P700005", "钢珠检测设备", "P700005-3", "3号工位", "W2", "机械设计部",
            "刘鹏", "王工", [], null, DateTimeOffset.Now, [1, 2, 3, 4]);

        using var stream = new MemoryStream(BomWorkbook.WriteExport([], context));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sheet = ReadEntryXml(archive, "xl/worksheets/sheet1.xml");

        Assert.Contains("发布日期", sheet.ToString());
        Assert.Contains("未发布", sheet.ToString());
    }

    [Fact]
    public void WriteExport_DefaultsToSummaryAndCanPreserveStructureRows()
    {
        var projectId = Guid.NewGuid();
        var items = new[]
        {
            new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 3, "MAT-001", "汇总件", 1.5m, "001", null, "M8", "W1", true),
            new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 7, "mat-001", "汇总件", 2.5m, "个", null, "M8", "W1", true),
            new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 8, "", "无编码一", 1, "001", null, null, "W1", false),
            new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 9, "", "无编码二", 1, "001", null, null, "W1", false)
        };
        var context = new BomWorkbookExportContext(
            "标准件BOM", "P700005", "钢珠检测设备", "P700005-3", "3号工位", "W2", "机械设计部",
            "刘鹏", "王工", [], null, DateTimeOffset.Now, [1, 2, 3, 4]);

        var summaryRows = ExportDataRows(BomWorkbook.WriteExport(items, context));
        Assert.Equal(3, summaryRows.Length);
        Assert.Equal("1", CellValue(summaryRows[0], "A"));
        Assert.Equal(4m, decimal.Parse(CellValue(summaryRows[0], "L"), CultureInfo.InvariantCulture));

        var structureRows = ExportDataRows(BomWorkbook.WriteExport(items, context, BomWorkbookExportMode.Structure));
        Assert.Equal(4, structureRows.Length);
        Assert.Equal(new[] { "3", "7", "8", "9" }, structureRows.Select(row => CellValue(row, "A")));
        Assert.Equal(new[] { 1.5m, 2.5m, 1m, 1m }, structureRows.Select(row => decimal.Parse(CellValue(row, "L"), CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ExportFileName_UsesProjectBomVersionAndTimestamp()
    {
        var exportedAt = new DateTimeOffset(2026, 9, 3, 10, 32, 18, TimeSpan.FromHours(8));

        var fileName = BomWorkbook.ExportFileName("P700005-3", "标准件BOM", "W2", exportedAt);

        Assert.Equal("P700005-3_标准件BOM_W2_20260903_103218.xlsx", fileName);
    }

    private static XDocument ReadEntryXml(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return XDocument.Parse(reader.ReadToEnd());
    }

    private static XElement[] ExportDataRows(byte[] workbook)
    {
        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var sheet = ReadEntryXml(archive, "xl/worksheets/sheet1.xml");
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return sheet.Descendants(ns + "row").Where(row => (int)row.Attribute("r")! >= 8).ToArray();
    }

    private static string CellValue(XElement row, string column)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rowNumber = (string)row.Attribute("r")!;
        var cell = row.Elements(ns + "c").Single(item => string.Equals((string?)item.Attribute("r"), $"{column}{rowNumber}", StringComparison.Ordinal));
        return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value).Concat(cell.Elements(ns + "v").Select(value => value.Value)));
    }
}
