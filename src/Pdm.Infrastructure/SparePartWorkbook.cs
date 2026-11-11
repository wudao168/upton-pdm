using System.Globalization;
using System.Xml.Linq;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public static class SparePartWorkbook
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static byte[] Write(IReadOnlyList<SparePartRow> rows, ProjectWorkbookMetadata metadata, bool quotation)
    {
        string[] headings = quotation
            ? ["项目号", "子项目号", "设备型号", "设备序列号", "物料号", "物料名称", "规格", "品牌", "单位", "单套数量", "建议数量", "币种", "未税售价"]
            : ["项目号", "子项目号", "设备型号", "设备序列号", "物料号", "物料名称", "规格", "品牌", "单位", "单套数量", "建议数量", "币种", "未税售价", "未税成本", "图纸版本", "分类", "采购日期"];
        var data = new XElement(Sheet + "sheetData", Row(1, headings));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var part = row.Part;
            var values = new List<object?> { row.Root.Code, row.Project.Id == row.Root.Id ? null : row.Project.Code,
                row.Project.DeviceModel, string.Join("、", row.Project.SerialNumbers),
                part.MaterialCode, part.Name, part.Specification, part.Brand, part.Unit, part.QuantityPerSet, part.Quantity, row.Currency, row.Price };
            if (!quotation)
                values.AddRange([row.Cost, string.Join(" / ", part.Drawings.Select(drawing => drawing.Revision).Distinct()),
                    row.CategoryName, row.PurchasedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd")]);
            data.Add(Row(index + 2, values));
        }
        var cols = new XElement(Sheet + "cols", headings.Select((_, index) =>
            new XElement(Sheet + "col", new XAttribute("min", index + 1), new XAttribute("max", index + 1),
                new XAttribute("width", index is 2 or 3 or 5 or 6 ? 24 : 15), new XAttribute("customWidth", 1))));
        return ProjectWorkbookHeader.Apply(ProjectFileFormWorkbook.Package(metadata.Title, new XElement(Sheet + "worksheet", cols, data)),
            metadata, originalHeaderRow: 1);
    }

    private static XElement Row(int row, IEnumerable<object?> values) => new(Sheet + "row", new XAttribute("r", row),
        values.Select((value, index) =>
        {
            var cell = new XElement(Sheet + "c", new XAttribute("r", $"{(char)('A' + index)}{row}"));
            if (value is decimal number) cell.Add(new XElement(Sheet + "v", number.ToString(CultureInfo.InvariantCulture)));
            else cell.Add(new XAttribute("t", "inlineStr"), new XElement(Sheet + "is", new XElement(Sheet + "t", value ?? "")));
            return cell;
        }));
}
