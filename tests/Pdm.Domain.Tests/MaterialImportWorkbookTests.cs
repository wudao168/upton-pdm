using System.IO.Compression;
using System.Text;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class MaterialImportWorkbookTests
{
    [Fact]
    public void Read_MapsFilledRowAndIgnoresRowsContainingOnlyDefaultUnit()
    {
        using var workbook = new MemoryStream();
        using (var archive = new ZipArchive(workbook, ZipArchiveMode.Create, true))
        {
            var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(sheet.Open(), new UTF8Encoding(false));
            writer.Write("""
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>U9C分类编码</t></is></c><c r="B1" t="inlineStr"><is><t>物料名称</t></is></c><c r="C1" t="inlineStr"><is><t>计量单位</t></is></c><c r="D1" t="inlineStr"><is><t>规格型号</t></is></c><c r="E1" t="inlineStr"><is><t>材质</t></is></c><c r="F1" t="inlineStr"><is><t>品牌</t></is></c><c r="G1" t="inlineStr"><is><t>表面处理</t></is></c><c r="H1" t="inlineStr"><is><t>重量</t></is></c><c r="I1" t="inlineStr"><is><t>重量单位</t></is></c><c r="J1" t="inlineStr"><is><t>备注</t></is></c><c r="K1" t="inlineStr"><is><t>采购链接</t></is></c><c r="L1" t="inlineStr"><is><t>选型建议</t></is></c><c r="M1" t="inlineStr"><is><t>参考价格</t></is></c><c r="N1" t="inlineStr"><is><t>3D链接</t></is></c><c r="O1" t="inlineStr"><is><t>资料链接</t></is></c><c r="P1" t="inlineStr"><is><t>优先推荐</t></is></c></row>
                  <row r="2"><c r="A2" t="inlineStr"><is><t>0102 机械外购件</t></is></c><c r="B2" t="inlineStr"><is><t>轴承</t></is></c><c r="C2" t="inlineStr"><is><t>个</t></is></c><c r="D2" t="inlineStr"><is><t>BRG-01</t></is></c><c r="H2"><v>1.25</v></c><c r="P2" t="inlineStr"><is><t>是</t></is></c></row>
                  <row r="3"><c r="C3" t="inlineStr"><is><t>个</t></is></c></row>
                </sheetData></worksheet>
                """);
        }
        workbook.Position = 0;

        var row = Assert.Single(MaterialImportWorkbook.Read(workbook));
        Assert.Equal(2, row.RowNumber);
        Assert.Equal("0102", row.CategoryCode);
        Assert.Equal("轴承", row.Name);
        Assert.Equal("个", row.UnitCode);
        Assert.Equal("BRG-01", row.Specification);
        Assert.Equal(1.25m, row.Weight);
        Assert.True(row.IsRecommended);
    }
}
