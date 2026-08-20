using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using System.IO.Compression;
using System.Text;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomWorkbookTests
{
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
                Weight = "0.25 kg"
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
}
