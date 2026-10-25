using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public static class MaterialStockWorkbook
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static byte[] Apply(byte[] bytes, IReadOnlyList<MaterialStockPlan> plans)
    {
        var codes = plans.Where(plan => plan.IsStocked).Select(plan => plan.MaterialCode.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0) return bytes;
        using var output = new MemoryStream();
        output.Write(bytes);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            var stylesEntry = archive.GetEntry("xl/styles.xml");
            if (stylesEntry is null) return bytes;
            XDocument Read(ZipArchiveEntry entry) { using var stream = entry.Open(); return XDocument.Load(stream); }
            void Save(string path, XDocument document) { archive.GetEntry(path)?.Delete(); using var stream = archive.CreateEntry(path).Open(); document.Save(stream); }
            var styles = Read(stylesEntry);
            var fonts = styles.Root!.Element(Ns + "fonts")!;
            var cellStyles = styles.Root.Element(Ns + "cellXfs")!;
            var purpleStyles = new Dictionary<int, int>();
            int Purple(int original)
            {
                if (purpleStyles.TryGetValue(original, out var cached)) return cached;
                var style = new XElement(cellStyles.Elements().ElementAt(original));
                var font = new XElement(fonts.Elements().ElementAt((int?)style.Attribute("fontId") ?? 0));
                font.Elements(Ns + "color").Remove();
                font.Add(new XElement(Ns + "color", new XAttribute("rgb", "FF7C3AED")));
                style.SetAttributeValue("fontId", fonts.Elements().Count());
                style.SetAttributeValue("applyFont", "1");
                fonts.Add(font); fonts.SetAttributeValue("count", fonts.Elements().Count());
                var index = cellStyles.Elements().Count();
                cellStyles.Add(style); cellStyles.SetAttributeValue("count", index + 1);
                return purpleStyles[original] = index;
            }
            var strings = archive.GetEntry("xl/sharedStrings.xml") is { } shared
                ? Read(shared).Descendants(Ns + "si").Select(item => string.Concat(item.Descendants(Ns + "t").Select(text => text.Value))).ToArray() : [];
            string Text(XElement cell) => (string?)cell.Attribute("t") == "s"
                ? strings.ElementAtOrDefault(int.Parse(cell.Element(Ns + "v")?.Value ?? "-1", CultureInfo.InvariantCulture)) ?? ""
                : string.Concat(cell.Descendants(Ns + "t").Select(text => text.Value)) is { Length: > 0 } inline ? inline : cell.Element(Ns + "v")?.Value ?? "";
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToArray())
            {
                var path = entry.FullName;
                var sheet = Read(entry);
                var rows = sheet.Descendants(Ns + "row").ToArray();
                var header = rows.FirstOrDefault(row => row.Elements(Ns + "c").Any(cell => Text(cell) is "物料编码" or "料号" or "图号"));
                if (header is null) continue;
                var codeCell = header.Elements(Ns + "c").First(cell => Text(cell) is "物料编码" or "料号" or "图号");
                var codeColumn = Column(codeCell);
                var nameColumn = header.Elements(Ns + "c").FirstOrDefault(cell => Text(cell) is "名称" or "物料名称" or "品名") is { } name ? Column(name) : "";
                var matched = rows.SkipWhile(row => row != header).Skip(1).Where(row => row.Elements(Ns + "c").Any(cell => Column(cell) == codeColumn && codes.Contains(Text(cell).Trim()))).ToArray();
                if (matched.Length == 0) continue;
                var column = ColumnName(rows.SelectMany(row => row.Elements(Ns + "c")).Max(cell => ColumnIndex(Column(cell))) + 1);
                void Append(XElement row, string text, int style) => row.Add(new XElement(Ns + "c", new XAttribute("r", column + row.Attribute("r")!.Value), new XAttribute("s", style), new XAttribute("t", "inlineStr"), new XElement(Ns + "is", new XElement(Ns + "t", text))));
                Append(header, "常备件", (int?)codeCell.Attribute("s") ?? 0);
                foreach (var row in matched)
                {
                    foreach (var cell in row.Elements(Ns + "c").Where(cell => Column(cell) == codeColumn || Column(cell) == nameColumn)) cell.SetAttributeValue("s", Purple((int?)cell.Attribute("s") ?? 0));
                    Append(row, "常备件", Purple(0));
                }
                foreach (var range in sheet.Descendants().Where(element => element.Name == Ns + "autoFilter" || element.Name == Ns + "dimension"))
                {
                    var reference = (string?)range.Attribute("ref");
                    if (reference?.Contains(':') == true) range.SetAttributeValue("ref", reference.Split(':')[0] + ":" + column + new string(reference.Split(':')[1].Where(char.IsDigit).ToArray()));
                }
                Save(path, sheet);
            }
            if (purpleStyles.Count > 0) Save("xl/styles.xml", styles);
        }
        return output.ToArray();
    }
    private static string Column(XElement cell) => new(((string?)cell.Attribute("r") ?? "").TakeWhile(char.IsLetter).ToArray());
    private static int ColumnIndex(string column) => column.Aggregate(0, (value, letter) => value * 26 + letter - 'A' + 1);
    private static string ColumnName(int index) { var result = ""; for (; index > 0; index = (index - 1) / 26) result = (char)('A' + (index - 1) % 26) + result; return result; }
}
