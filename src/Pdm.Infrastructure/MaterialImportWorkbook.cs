using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public static class MaterialImportWorkbook
{
    public static IReadOnlyList<string> ReadMaterialCodes(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml") ?? throw new PdmRuleException("Excel中未找到第一张工作表。");
        if (sheet.Length > 5 * 1024 * 1024 || archive.GetEntry("xl/sharedStrings.xml")?.Length > 5 * 1024 * 1024) throw new PdmRuleException("Excel工作表过大，单次最多导入200个料号。");
        var strings = ReadSharedStrings(archive);
        using var stream = sheet.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = XDocument.Load(stream).Descendants(ns + "row").Select(row => ReadCells(row, ns, strings)).ToArray();
        var names = new[] { "料号", "物料编码", "料品编码", "MaterialCode", "Code" };
        var header = Array.FindIndex(rows, row => row.Values.Any(value => names.Contains(value, StringComparer.OrdinalIgnoreCase)));
        var column = header < 0 ? 0 : rows[header].First(cell => names.Contains(cell.Value, StringComparer.OrdinalIgnoreCase)).Key;
        var codes = rows.Skip(header + 1).Select(row => Value(row, column)).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (codes.Length is < 1 or > 200) throw new PdmRuleException("Excel需包含1至200个料号，料号列可命名为“料号”或放在第一列。");
        if (codes.Any(code => System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d+(\.\d+)?[Ee][+-]?\d+$")))
            throw new PdmRuleException("料号包含科学计数格式，请将Excel料号列按文本保存后重试，避免前导0或数字丢失。");
        return codes;
    }

    private static readonly string[] RequiredHeaders = ["U9C分类编码", "物料名称", "计量单位", "规格型号"];
    private static readonly string[] OptionalHeaders = ["材质", "品牌", "表面处理", "重量", "重量单位", "备注", "采购链接", "选型建议", "参考价格", "3D链接", "资料链接", "优先推荐"];

    public static IReadOnlyList<MaterialImportRowCommand> Read(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new PdmRuleException("Excel中未找到“料品导入”工作表。");
        var sharedStrings = ReadSharedStrings(archive);
        XDocument document;
        using (var stream = sheet.Open()) document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").ToArray();
        if (rows.Length < 2) throw new PdmRuleException("Excel没有可导入的数据行。");

        var headerIndex = Array.FindIndex(rows, row => RequiredHeaders.All(header => FindHeaderColumn(ReadCells(row, ns, sharedStrings), header) >= 0));
        if (headerIndex < 0) throw new PdmRuleException($"Excel必须包含列：{string.Join("、", RequiredHeaders)}。");
        var headers = ReadCells(rows[headerIndex], ns, sharedStrings);
        var columns = RequiredHeaders.Concat(OptionalHeaders).ToDictionary(
            header => header,
            header => FindHeaderColumn(headers, header),
            StringComparer.OrdinalIgnoreCase);
        var missing = RequiredHeaders.Where(header => columns[header] < 0).ToArray();
        if (missing.Length > 0) throw new PdmRuleException($"Excel必须包含列：{string.Join("、", missing)}。");

        var result = new List<MaterialImportRowCommand>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var cells = ReadCells(row, ns, sharedStrings);
            if (cells.Values.All(string.IsNullOrWhiteSpace)) continue;
            var category = Value(cells, columns["U9C分类编码"]);
            var name = Value(cells, columns["物料名称"]);
            var specification = Value(cells, columns["规格型号"]);
            var hasOptionalValue = OptionalHeaders
                .Where(header => header != "优先推荐")
                .Any(header => !string.IsNullOrWhiteSpace(Value(cells, columns[header])));
            if (string.IsNullOrWhiteSpace(category)
                && string.IsNullOrWhiteSpace(name)
                && string.IsNullOrWhiteSpace(specification)
                && !hasOptionalValue)
                continue;
            var rowNumber = int.TryParse(row.Attribute("r")?.Value, out var parsedRow) ? parsedRow : result.Count + 2;
            result.Add(new(
                rowNumber,
                CategoryCode(category),
                name,
                Value(cells, columns["计量单位"]),
                specification,
                Optional(cells, columns["材质"]),
                Optional(cells, columns["品牌"]),
                Optional(cells, columns["表面处理"]),
                Decimal(cells, columns["重量"], "重量", rowNumber),
                Optional(cells, columns["重量单位"]),
                Optional(cells, columns["备注"]),
                Optional(cells, columns["采购链接"]),
                Optional(cells, columns["选型建议"]),
                Decimal(cells, columns["参考价格"], "参考价格", rowNumber),
                Optional(cells, columns["3D链接"]),
                Optional(cells, columns["资料链接"]),
                Boolean(cells, columns["优先推荐"])));
        }
        if (result.Count == 0) throw new PdmRuleException("Excel没有可导入的数据行。");
        if (result.Count > 1000) throw new PdmRuleException("单次最多导入1000行料品。");
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

    private static Dictionary<int, string> ReadCells(XElement row, XNamespace ns, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        foreach (var cell in row.Elements(ns + "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var column = ColumnIndex(reference);
            if (column < 0) continue;
            var type = cell.Attribute("t")?.Value;
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants(ns + "t").Select(item => item.Value))
                : cell.Element(ns + "v")?.Value ?? string.Empty;
            if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count) value = sharedStrings[index];
            values[column] = value.Trim();
        }
        return values;
    }

    private static int FindHeaderColumn(IReadOnlyDictionary<int, string> values, string header) =>
        values.FirstOrDefault(pair => string.Equals(pair.Value.Trim(), header, StringComparison.OrdinalIgnoreCase), new(-1, string.Empty)).Key;

    private static string Value(IReadOnlyDictionary<int, string> cells, int column) => column < 0 ? string.Empty : cells.GetValueOrDefault(column, string.Empty).Trim();
    private static string? Optional(IReadOnlyDictionary<int, string> cells, int column) => string.IsNullOrWhiteSpace(Value(cells, column)) ? null : Value(cells, column);
    private static decimal? Decimal(IReadOnlyDictionary<int, string> cells, int column, string field, int row)
    {
        var value = Value(cells, column);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)) return parsed;
        throw new PdmRuleException($"第{row}行{field}必须是数字。");
    }
    private static bool Boolean(IReadOnlyDictionary<int, string> cells, int column) => Value(cells, column).ToUpperInvariant() is "是" or "TRUE" or "1" or "Y" or "YES";
    private static string CategoryCode(string value) => value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    private static int ColumnIndex(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0) return -1;
        var result = 0;
        foreach (var letter in letters.ToUpperInvariant()) result = result * 26 + letter - 'A' + 1;
        return result - 1;
    }
}
