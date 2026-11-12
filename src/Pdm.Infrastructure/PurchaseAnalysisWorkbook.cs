using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public static class PurchaseAnalysisWorkbook
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static byte[] Write(PurchaseAnalysisResult result, ProjectWorkbookMetadata metadata)
    {
        string[] headers = ["组织", "订单号", "行号", "采购日期", "采购类型", "料号", "名称", "规格", "品牌", "物料分类", "供应商", "采购员", "项目", "子项目", "单位", "数量", "币种"];
        if (result.CanCost) headers = [..headers, "未税金额", "未税单价"];
        var data = new XElement(S + "sheetData", Row(1, headers));
        var index = 2;
        foreach (var r in result.Details) {
            var values = new List<object?> { r.Organization, r.OrderNumber, r.LineNumber, r.Date.ToString("yyyy-MM-dd"), r.TypeName,
                r.MaterialCode, r.MaterialName, r.Specification, r.Brand, r.CategoryName, r.SupplierName, r.BuyerName,
                r.ProjectCode, r.SubprojectCode, r.UnitName, r.Quantity, r.Currency };
            if (result.CanCost) values.AddRange([r.NetAmount, r.UnitPrice]);
            data.Add(Row(index++, values));
        }
        var columns = new XElement(S + "cols", headers.Select((_, i) => new XElement(S + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", i is 6 or 7 or 10 ? 25 : 16), new XAttribute("customWidth", 1))));
        var sheets = new List<(string Name, XElement Sheet)> { ("采购明细", new XElement(S + "worksheet", columns, data)) };
        void Analysis(string name, IReadOnlyList<PurchaseAnalysisMetric> rows, int[] fields) {
            string[] titles = ["编码", "名称", "单位", "采购次数", "采购数量", "月均次数", "平均间隔天数", "平均批量", "供应商数", "项目数", "短期下单次数", "分析依据"];
            if (result.CanCost) titles = [..titles, "币种", "未税金额", "加权均价", "最近日均价", "首末日涨幅%", "金额占比%", "累计占比%", "分级"];
            fields = fields.Where(i => i < titles.Length).ToArray();
            var cells = new XElement(S + "sheetData", Row(1, fields.Select(i => titles[i]))); var index = 2;
            foreach (var m in rows) {
                var values = new List<object?> { m.Code, m.Name, m.Unit, m.Orders, m.Unit == "—" ? null : m.Quantity, m.OrdersPerMonth,
                    m.AverageInterval, m.Unit == "—" ? null : m.AverageBatch, m.Suppliers, m.Projects, m.FragmentOrders, m.Reason };
                if (result.CanCost) values.AddRange([m.Currency, m.Amount, m.AveragePrice, m.LastPrice, m.PriceChange, m.Share, m.CumulativeShare, m.Abc]);
                cells.Add(Row(index++, fields.Select(i => values[i])));
            }
            sheets.Add((name, new XElement(S + "worksheet", new XElement(S + "cols", fields.Select((field, i) => new XElement(S + "col",
                new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", field == 11 ? 42 : field == 1 ? 26 : 15), new XAttribute("customWidth", 1)))), cells)));
        }
        Analysis("高频采购", result.Frequent, [0,1,2,3,4,5,6,8,9,12,13]);
        Analysis("大批量采购", result.Volume, [0,1,2,3,4,7,8,9,12,13]);
        Analysis("零散采购", result.Fragmented, [0,1,2,3,4,7,10,11,12,13]);
        if (result.CanCost) {
            Analysis("高金额采购", result.Spend, [0,1,2,3,4,12,13,17,18,19]);
            Analysis("价格差异", result.Prices, [0,1,2,3,4,8,12,13,14,15,16]);
            Analysis("供应商集中度", result.Suppliers, [0,1,3,9,12,13,17,18,19]);
        }
        using var stream = new MemoryStream(); stream.Write(ProjectFileFormWorkbook.Package(sheets[0].Name, sheets[0].Sheet)); stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true)) {
            XDocument Read(string path) { using var reader = zip.GetEntry(path)!.Open(); return XDocument.Load(reader); }
            void Save(string path, XDocument xml) { zip.GetEntry(path)?.Delete(); using var writer = zip.CreateEntry(path).Open(); xml.Save(writer); }
            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships", doc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", types = "http://schemas.openxmlformats.org/package/2006/content-types";
            var workbook = Read("xl/workbook.xml"); var relationships = Read("xl/_rels/workbook.xml.rels"); var content = Read("[Content_Types].xml");
            var styles = Read("xl/styles.xml");
            styles.Root!.AddFirst(new XElement(S + "numFmts", new XAttribute("count", 1), new XElement(S + "numFmt", new XAttribute("numFmtId", 164), new XAttribute("formatCode", "#,##0.00##"))));
            var numeric = new XElement(styles.Root.Element(S + "cellXfs")!.Elements().ElementAt(1));
            numeric.SetAttributeValue("numFmtId", 164); numeric.SetAttributeValue("applyNumberFormat", 1);
            styles.Root.Element(S + "cellXfs")!.Add(numeric); styles.Root.Element(S + "cellXfs")!.SetAttributeValue("count", 6);
            Save("xl/styles.xml", styles);
            for (var i = 1; i < sheets.Count; i++) {
                var id = "analysis" + i; var path = $"xl/worksheets/sheet{i + 1}.xml";
                Save(path, new(sheets[i].Sheet));
                workbook.Root!.Element(S + "sheets")!.Add(new XElement(S + "sheet", new XAttribute("name", sheets[i].Name), new XAttribute("sheetId", i + 1), new XAttribute(doc + "id", id)));
                relationships.Root!.Add(new XElement(rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", doc.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{i + 1}.xml")));
                content.Root!.Add(new XElement(types + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            }
            Save("xl/workbook.xml", workbook); Save("xl/_rels/workbook.xml.rels", relationships); Save("[Content_Types].xml", content);
        }
        return ProjectWorkbookHeader.Apply(stream.ToArray(), metadata, originalHeaderRow: 1);
    }
    private static XElement Row(int row, IEnumerable<object?> values) => new(S + "row", new XAttribute("r", row), new XAttribute("ht", 32), new XAttribute("customHeight", 1), values.Select((value, i) => {
        var cell = new XElement(S + "c", new XAttribute("r", $"{(char)('A' + i)}{row}"), new XAttribute("s", value is decimal ? 5 : 1));
        if (value is decimal or int) cell.Add(new XElement(S + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
        else cell.Add(new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", value ?? "")));
        return cell;
    }));
}
