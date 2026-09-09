using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed record BomWorkbookExportContext(
    string BomName,
    string MainProjectCode,
    string MainProjectName,
    string ChildProjectCode,
    string ChildProjectName,
    string BomVersion,
    string Department,
    string ProjectManagers,
    string Designers,
    IReadOnlyList<BomWorkbookApprovalEntry> ApprovalSteps,
    DateTimeOffset? PublishedAt,
    DateTimeOffset ExportedAt,
    byte[]? CompanyLogo = null);

public sealed record BomWorkbookApprovalEntry(string StepName, string People);

public enum BomWorkbookExportMode
{
    Summary,
    Structure
}

public static class BomWorkbook
{
    private static readonly string[] Headers = ["序号", "物料分类", "单位", "物料编码", "物料名称", "上级物料编码", "型号", "备注信息", "品牌", "材质", "表面处理", "重量", "数量", "版本", "完整"];
    private static readonly string[] RequiredHeaders = ["序号", "单位", "物料编码", "物料名称", "数量", "版本", "完整"];
    private static readonly string[] ExportHeaders = ["序号", "物料分类", "单位", "物料编码", "物料名称", "型号", "备注信息", "品牌", "材质", "表面处理", "重量", "数量"];
    private static readonly int[] ExportColumnWidths = [10, 10, 10, 15, 15, 25, 30, 10, 10, 15, 10, 10];
    private static readonly IReadOnlyDictionary<string, string> UnitNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["001"] = "个", ["002"] = "台", ["003"] = "个", ["004"] = "盒", ["005"] = "卷",
        ["006"] = "捆", ["007"] = "双", ["008"] = "片", ["009"] = "桶", ["010"] = "支",
        ["011"] = "组", ["012"] = "箱", ["013"] = "包"
    };

    public static byte[] Write(IReadOnlyList<BomItem> items) => WriteCore(items, null);

    public static byte[] WriteStandardRelease(IReadOnlyList<BomItem> items, IReadOnlyDictionary<string, decimal> priorQuantities) =>
        WriteCore(items.GroupBy(BomReleaseAggregation.MaterialKey)
            .Select(group => group.First() with { Quantity = group.Sum(item => item.Quantity), ParentDrawingNumber = null })
            .ToArray(), priorQuantities);

    private static byte[] WriteCore(IReadOnlyList<BomItem> items, IReadOnlyDictionary<string, decimal>? priorQuantities)
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
            var headers = priorQuantities is null ? Headers
                : Headers.Select(header => header == "数量" ? "BOM总量" : header).Concat(["已提前发布", "本次新增下发"]).ToArray();
            WriteRow(writer, 1, headers.Cast<object?>().ToArray());
            var rowNumber = 2;
            foreach (var item in items.OrderBy(item => item.Sequence))
            {
                object?[] values = [item.Sequence, KindLabel(item.Kind), item.Unit, item.DrawingNumber, item.Name, item.ParentDrawingNumber, item.Specification, item.Remark, item.Brand, item.Material, item.SurfaceTreatment, item.Weight, item.Quantity, item.Revision, item.IsComplete ? "是" : "否"];
                if (priorQuantities is not null)
                {
                    var prior = priorQuantities.GetValueOrDefault(BomReleaseAggregation.MaterialKey(item));
                    values = [.. values, prior, Math.Max(0, item.Quantity - prior)];
                }
                WriteRow(writer, rowNumber++, values);
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    public static byte[] WriteExport(
        IReadOnlyList<BomItem> items,
        BomWorkbookExportContext context,
        BomWorkbookExportMode mode = BomWorkbookExportMode.Summary)
    {
        var exportItems = PrepareExportItems(items, mode);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteText(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                  <Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>
                </Types>
                """);
            WriteText(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteText(archive, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{EscapeXml(WorksheetName(context.BomName))}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteText(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteText(archive, "xl/styles.xml", ExportStylesXml);
            WriteText(archive, "xl/worksheets/_rels/sheet1.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/>
                </Relationships>
                """);
            WriteText(archive, "xl/drawings/drawing1.xml", ExportDrawingXml);
            WriteText(archive, "xl/drawings/_rels/drawing1.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
                </Relationships>
                """);
            WriteBinary(archive, "xl/media/image1.png", context.CompanyLogo ?? LoadCompanyLogo());

            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
            writer.WriteStartDocument(true);
            writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            writer.WriteStartElement("cols");
            for (var index = 0; index < ExportColumnWidths.Length; index++)
            {
                writer.WriteStartElement("col");
                writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("width", ExportColumnWidths[index].ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData");
            WriteStyledRow(writer, 1, 30, 1, [context.BomName]);
            WriteStyledRow(writer, 2, 12, 1, []);
            WriteStyledRow(writer, 3, 24, 2,
            [
                "主项目号", DisplayValue(context.MainProjectCode), null,
                "主项目名称", DisplayValue(context.MainProjectName), null,
                "子项目号", DisplayValue(context.ChildProjectCode), null,
                "子项目名称", DisplayValue(context.ChildProjectName), null
            ], 0, 3, 6, 9);
            WriteStyledRow(writer, 4, 24, 2,
            [
                "BOM版本", DisplayValue(context.BomVersion), null,
                "部门", DisplayValue(context.Department), null,
                "项目经理", DisplayValue(context.ProjectManagers), null,
                "设计", DisplayValue(context.Designers), null
            ], 0, 3, 6, 9);
            WriteStyledRow(writer, 5, 24, 2, ApprovalCells(context.ApprovalSteps, context.PublishedAt), 0, 3, 6, 9);
            WriteStyledRow(writer, 6, 8, 2, []);
            WriteStyledRow(writer, 7, 24, 3, ExportHeaders.Cast<object?>().ToArray());
            var rowNumber = 8;
            foreach (var item in exportItems)
            {
                WriteStyledRow(writer, rowNumber++, 22, 4,
                [
                    item.Sequence, KindLabel(item.Kind), DisplayUnit(item.Unit), item.DrawingNumber, item.Name,
                    item.Specification, item.Remark, item.Brand, item.Material, item.SurfaceTreatment, item.Weight, item.Quantity
                ]);
            }
            writer.WriteEndElement();
            writer.WriteStartElement("mergeCells");
            writer.WriteAttributeString("count", "13");
            foreach (var range in new[]
            {
                "A1:L2",
                "B3:C3", "E3:F3", "H3:I3", "K3:L3",
                "B4:C4", "E4:F4", "H4:I4", "K4:L4",
                "B5:C5", "E5:F5", "H5:I5", "K5:L5"
            })
            {
                writer.WriteStartElement("mergeCell");
                writer.WriteAttributeString("ref", range);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("drawing");
            writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId1");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    private static IReadOnlyList<BomItem> PrepareExportItems(IReadOnlyList<BomItem> items, BomWorkbookExportMode mode)
    {
        var ordered = items.OrderBy(item => item.Sequence).ToArray();
        if (mode == BomWorkbookExportMode.Structure) return ordered;

        return ordered
            .GroupBy(item => string.IsNullOrWhiteSpace(item.DrawingNumber)
                ? $"row:{item.Id:N}"
                : $"material:{item.DrawingNumber.Trim()}|{U9UnitCatalog.NormalizeBomUnit(item.Unit)}", StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => group.First() with
            {
                Sequence = index + 1,
                Quantity = group.Sum(item => item.Quantity)
            })
            .ToArray();
    }

    public static string ExportFileName(string projectCode, string bomName, string bomVersion, DateTimeOffset exportedAt)
    {
        var segments = new[] { projectCode, bomName, bomVersion, exportedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) }
            .Select(SanitizeFileNameSegment)
            .Where(value => value.Length > 0);
        return $"{string.Join('_', segments)}.xlsx";
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
        var missing = RequiredHeaders.Where(header => columns[header] < 0).ToArray();
        if (missing.Length > 0)
        {
            throw new PdmRuleException($"BOM Excel必须包含列：{string.Join("、", missing)}。");
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
                Value(cells, columns["物料编码"]),
                Value(cells, columns["物料名称"]),
                quantity,
                Value(cells, columns["单位"]),
                EmptyToNull(Value(cells, columns["材质"])),
                EmptyToNull(Value(cells, columns["型号"])),
                Value(cells, columns["版本"]),
                ParseComplete(Value(cells, columns["完整"])),
                Remark: EmptyToNull(Value(cells, columns["备注信息"])),
                Brand: EmptyToNull(Value(cells, columns["品牌"])),
                SurfaceTreatment: EmptyToNull(Value(cells, columns["表面处理"])),
                Weight: EmptyToNull(Value(cells, columns["重量"])),
                ParentDrawingNumber: EmptyToNull(Value(cells, columns["上级物料编码"]))));
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

    private static void WriteStyledRow(XmlWriter writer, int rowNumber, double height, int styleIndex, IReadOnlyList<object?> values, params int[] labelColumns)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("ht", height.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("customHeight", "1");
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value is null) continue;
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", $"{ColumnName(index)}{rowNumber}");
            writer.WriteAttributeString("s", (labelColumns.Contains(index) ? 5 : styleIndex).ToString(CultureInfo.InvariantCulture));
            if (value is int or long or decimal or double or float)
            {
                writer.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteElementString("t", value.ToString() ?? string.Empty);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static object?[] ApprovalCells(IReadOnlyList<BomWorkbookApprovalEntry> approvals, DateTimeOffset? publishedAt)
    {
        var values = new object?[12];
        foreach (var entry in approvals.Take(3).Select((approval, index) => (approval, index)))
        {
            values[entry.index * 3] = DisplayValue(entry.approval.StepName);
            values[entry.index * 3 + 1] = DisplayValue(entry.approval.People);
        }
        values[9] = "发布日期";
        values[10] = publishedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未发布";
        return values;
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

    private static void WriteBinary(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] LoadCompanyLogo()
    {
        var assembly = typeof(BomWorkbook).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(".Assets.upton-company-logo.png", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) throw new InvalidOperationException("未找到内嵌的公司LOGO资源。");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("无法读取内嵌的公司LOGO资源。");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string DisplayUnit(string value) => UnitNames.TryGetValue(value.Trim(), out var name) ? name : value.Trim();
    private static string DisplayValue(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
    private static string WorksheetName(string value)
    {
        var sanitized = string.Concat(value.Select(character => "[]:*?/\\".Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "BOM" : sanitized[..Math.Min(31, sanitized.Length)];
    }
    private static string SanitizeFileNameSegment(string value) => string.Concat((value ?? string.Empty).Trim().Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static string EscapeXml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private const string ExportStylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="3">
            <font><sz val="11"/><name val="Microsoft YaHei"/></font>
            <font><b/><sz val="18"/><name val="Microsoft YaHei"/></font>
            <font><b/><sz val="11"/><name val="Microsoft YaHei"/></font>
          </fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDCE6F1"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="2"><border/><border><left style="thin"><color rgb="FFB8C6D8"/></left><right style="thin"><color rgb="FFB8C6D8"/></right><top style="thin"><color rgb="FFB8C6D8"/></top><bottom style="thin"><color rgb="FFB8C6D8"/></bottom><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="6">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="2" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private const string ExportDrawingXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <xdr:oneCellAnchor>
            <xdr:from><xdr:col>10</xdr:col><xdr:colOff>76000</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>70000</xdr:rowOff></xdr:from>
            <xdr:ext cx="1466667" cy="188000"/>
            <xdr:pic>
              <xdr:nvPicPr><xdr:cNvPr id="1" name="UPTON 阿普顿 LOGO"/><xdr:cNvPicPr/></xdr:nvPicPr>
              <xdr:blipFill><a:blip r:embed="rId1"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>
              <xdr:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1466667" cy="188000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr>
            </xdr:pic>
            <xdr:clientData/>
          </xdr:oneCellAnchor>
        </xdr:wsDr>
        """;

    private static int FindHeaderColumn(IReadOnlyDictionary<int, string> values, string header) =>
        values.FirstOrDefault(pair => string.Equals(NormalizeHeader(pair.Value), NormalizeHeader(header), StringComparison.OrdinalIgnoreCase), new KeyValuePair<int, string>(-1, string.Empty)).Key;

    private static string NormalizeHeader(string value) => value.Trim() switch
    {
        "图号" => "物料编码",
        "名称" => "物料名称",
        "规格" => "型号",
        "父项料号" or "关联料号" => "上级物料编码",
        "描述" => "备注信息",
        "材料" => "材质",
        "是否完整" => "完整",
        var normalized => normalized
    };

    private static string KindLabel(BomKind kind) => kind switch
    {
        BomKind.Standard => "标准件",
        BomKind.NonStandard => "非标件",
        BomKind.Electrical => "电气件",
        _ => kind.ToString()
    };
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
