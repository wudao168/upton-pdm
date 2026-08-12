using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public static class BomWorkbook
{
    private static readonly string[] Headers = ["序号", "图号", "名称", "数量", "单位", "材料", "规格", "版本", "完整"];

    public static byte[] Write(IReadOnlyList<BomItem> items)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteText(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            WriteText(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteText(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="BOM" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteText(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);

            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
            writer.WriteStartDocument(true);
            writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            writer.WriteStartElement("sheetData");
            WriteRow(writer, 1, Headers.Cast<object?>().ToArray());
            var rowNumber = 2;
            foreach (var item in items.OrderBy(item => item.Sequence))
            {
                WriteRow(writer, rowNumber++, [item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, item.Revision, item.IsComplete ? "是" : "否"]);
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    public static IReadOnlyList<BomItemInput> Read(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new PdmRuleException("Excel中未找到第一个BOM工作表。");
        var sharedStrings = ReadSharedStrings(archive);
        XDocument document;
        using (var stream = sheet.Open()) document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").ToArray();
        if (rows.Length < 2) throw new PdmRuleException("BOM Excel没有数据行。");

        var headerValues = ReadCells(rows[0], ns, sharedStrings);
        var columns = Headers.ToDictionary(
            header => header,
            header => FindHeaderColumn(headerValues, header),
            StringComparer.OrdinalIgnoreCase);
        if (columns.Any(pair => pair.Value < 0))
        {
            throw new PdmRuleException($"BOM Excel必须包含列：{string.Join("、", Headers)}。");
        }

        var result = new List<BomItemInput>();
        foreach (var row in rows.Skip(1))
        {
            var cells = ReadCells(row, ns, sharedStrings);
            if (cells.Values.All(string.IsNullOrWhiteSpace)) continue;
            var sequence = ParseInt(Value(cells, columns["序号"]), "序号");
            var quantity = ParseDecimal(Value(cells, columns["数量"]), "数量");
            result.Add(new BomItemInput(
                sequence,
                Value(cells, columns["图号"]),
                Value(cells, columns["名称"]),
                quantity,
                Value(cells, columns["单位"]),
                EmptyToNull(Value(cells, columns["材料"])),
                EmptyToNull(Value(cells, columns["规格"])),
                Value(cells, columns["版本"]),
                ParseComplete(Value(cells, columns["完整"]))));
        }

        if (result.Count == 0) throw new PdmRuleException("BOM Excel没有有效数据行。");
        return result;
    }

    private static void WriteRow(XmlWriter writer, int rowNumber, IReadOnlyList<object?> values)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", $"{ColumnName(index)}{rowNumber}");
            if (value is int or long or decimal or double or float)
            {
                writer.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteElementString("t", value?.ToString() ?? string.Empty);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static Dictionary<int, string> ReadCells(XElement row, XNamespace ns, IReadOnlyList<string> sharedStrings)
    {
        var result = new Dictionary<int, string>();
        foreach (var cell in row.Elements(ns + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            var column = ColumnIndex(reference);
            var type = (string?)cell.Attribute("t");
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))
                : cell.Element(ns + "v")?.Value ?? string.Empty;
            if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                value = sharedStrings[sharedIndex];
            result[column] = value.Trim();
        }
        return result;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si").Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToArray();
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static int FindHeaderColumn(IReadOnlyDictionary<int, string> values, string header) =>
        values.FirstOrDefault(pair => string.Equals(NormalizeHeader(pair.Value), header, StringComparison.OrdinalIgnoreCase), new KeyValuePair<int, string>(-1, string.Empty)).Key;

    private static string NormalizeHeader(string value) => value.Trim().Replace("物料编码", "图号", StringComparison.OrdinalIgnoreCase).Replace("物料名称", "名称", StringComparison.OrdinalIgnoreCase).Replace("是否完整", "完整", StringComparison.OrdinalIgnoreCase);
    private static string Value(IReadOnlyDictionary<int, string> cells, int column) => cells.TryGetValue(column, out var value) ? value : string.Empty;
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static int ParseInt(string value, string field) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new PdmRuleException($"BOM{field}“{value}”不是有效整数。");
    private static decimal ParseDecimal(string value, string field) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) || decimal.TryParse(value, out result) ? result : throw new PdmRuleException($"BOM{field}“{value}”不是有效数字。");
    private static bool ParseComplete(string value) => value.Equals("是", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter)) result = result * 26 + char.ToUpperInvariant(character) - 'A' + 1;
        return result - 1;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var value = index + 1; value > 0; value = (value - 1) / 26) name = (char)('A' + (value - 1) % 26) + name;
        return name;
    }
}
