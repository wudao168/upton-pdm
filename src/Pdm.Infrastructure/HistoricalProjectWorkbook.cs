using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public static class HistoricalProjectWorkbook
{
    private static readonly string[] RequiredHeaders = ["项目号", "项目名称", "设备型号", "序列号", "客户名称"];
    private static readonly char[] SerialSeparators = ['\t', '\r', '\n', ',', '，', '、', ';', '；'];

    public static IReadOnlyList<HistoricalProjectRow> Read(Stream input, CancellationToken ct)
    {
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
            var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
                ?? throw new PdmRuleException("Excel中未找到第一张工作表。");
            if (sheet.Length > HistoricalProjectImport.MaximumFileBytes ||
                archive.GetEntry("xl/sharedStrings.xml")?.Length > HistoricalProjectImport.MaximumFileBytes ||
                archive.GetEntry("xl/workbook.xml")?.Length > HistoricalProjectImport.MaximumFileBytes)
                throw new PdmRuleException("Excel工作表内容过大，请拆分清单。");
            var sharedStrings = MaterialImportWorkbook.ReadSharedStrings(archive);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var uses1904Dates = false;
            if (archive.GetEntry("xl/workbook.xml") is { } settings)
            {
                using var stream = settings.Open();
                uses1904Dates = XDocument.Load(stream).Root?.Element(ns + "workbookPr")?.Attribute("date1904")?.Value is "true" or "1";
            }

            using var sheetStream = sheet.Open();
            var source = XDocument.Load(sheetStream).Descendants(ns + "row")
                .Select((row, index) => (Number: (int?)row.Attribute("r") ?? index + 1,
                    Cells: MaterialImportWorkbook.ReadCells(row, ns, sharedStrings)))
                .Where(row => row.Cells.Values.Any(value => value.Length > 0)).ToArray();
            if (source.Length == 0) throw new PdmRuleException("Excel没有可导入的项目。");
            var header = source[0].Cells;
            var hasHeaders = RequiredHeaders.All(field => header.Values.Contains(field));
            if (!hasHeaders && header.Values.Any(value => RequiredHeaders.Contains(value)))
                throw new PdmRuleException("Excel缺少必填列：" + string.Join("、", RequiredHeaders.Where(field => !header.Values.Contains(field))) + "。");
            int Column(string field, int legacyIndex = -1) => hasHeaders
                ? header.FirstOrDefault(item => item.Value == field, new(-1, "")).Key : legacyIndex;
            var code = Column("项目号", 1);
            var name = Column("项目名称", 2);
            var alias = Column("项目别名", 3);
            var signedDate = Column("签订日期", 4);
            var model = Column("设备型号", 5);
            var serial = Column("序列号", 6);
            var customer = Column("客户名称", 7);
            var updatedDate = Column("更新时间", 8);
            var remarks = Column("备注", 9);
            var deliveryDate = Column("交货日期");
            var bomCategory = Column("BOM节点类型");
            var parentCode = Column("上级项目号");
            var result = new List<HistoricalProjectRow>();
            foreach (var row in source.Skip(hasHeaders ? 1 : 0))
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= HistoricalProjectImport.MaximumRows)
                    throw new PdmRuleException($"每批最多导入{HistoricalProjectImport.MaximumRows}行项目，请按完整项目树拆分清单。");
                string ReadCell(int column) => row.Cells.GetValueOrDefault(column, "");
                string? Optional(int column) => ReadCell(column) is { Length: > 0 } value ? value : null;
                var note = Optional(remarks);
                var order = ReadDate(ReadCell(signedDate), row.Number, "签订日期", uses1904Dates) ??
                    ReadNoteDate(note, "订单日期", row.Number);
                var delivery = ReadDate(ReadCell(deliveryDate), row.Number, "交货日期", uses1904Dates) ??
                    ReadNoteDate(note, "交货日期", row.Number);
                result.Add(new(row.Number, ReadCell(code), ReadCell(name), Optional(alias), ReadCell(model),
                    ReadCell(serial).Split(SerialSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    ReadCell(customer), order, delivery, Optional(bomCategory),
                    ReadDate(ReadCell(updatedDate), row.Number, "更新时间", uses1904Dates), note, Optional(parentCode)));
            }
            if (result.Count == 0) throw new PdmRuleException("Excel没有可导入的项目。");
            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or FormatException or OverflowException)
        {
            throw new PdmRuleException("无法读取Excel，请用Excel另存为未加密的 .xlsx 文件后重试。");
        }
    }

    private static DateOnly? ReadNoteDate(string? note, string field, int row)
    {
        var match = Regex.Match(note ?? "", field + @"\s*[:：]\s*(\d{4}年\d{1,2}月\d{1,2}日|\d{4}[-/]\d{1,2}[-/]\d{1,2})");
        return match.Success ? ReadDate(match.Groups[1].Value, row, field, false) : null;
    }

    private static DateOnly? ReadDate(string value, int row, string field, bool uses1904Dates)
    {
        if (value.Length == 0) return null;
        string[] formats = ["yyyy-M-d", "yyyy/M/d", "yyyy年M月d日", "yyyy-M-d H:m:s", "yyyy/M/d H:m:s", "yyyy-MM-dd'T'HH:mm:ss"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            && date.Year >= 1900) return DateOnly.FromDateTime(date);
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var serial)
            && serial >= (uses1904Dates ? 0 : 61) && serial < 2957000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial + (uses1904Dates ? 1462 : 0)));
        throw new PdmRuleException($"第{row}行{field}无效，请使用Excel日期或“年-月-日”格式。");
    }
}
