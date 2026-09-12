using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public static class ValidationPlanWorkbook
{
    private static readonly double[] ColumnWidths = [8, 16, 17, 17, 17, 17, 17, 17, 17, 17];

    public static byte[] Write(ValidationPlanExportData export)
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
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
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
                  <sheets><sheet name="项目验证计划" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteText(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            WriteText(archive, "xl/styles.xml", StylesXml);
            WriteWorksheet(archive, export);
        }
        return output.ToArray();
    }

    public static string FileName(ValidationPlanExportData export) =>
        $"{Sanitize(export.Project.Code)}_验证计划_{export.ExportedAt:yyyyMMdd_HHmmss}.xlsx";

    private static void WriteWorksheet(ZipArchive archive, ValidationPlanExportData export)
    {
        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true });
        var merges = new List<string> { "A1:J1", "A2:J2", "B3:C3", "E3:F3", "H3:I3", "B4:D4", "F4:H4", "B5:E5" };

        writer.WriteStartDocument(true);
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", "5");
        writer.WriteAttributeString("topLeftCell", "A6");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("cols");
        for (var index = 0; index < ColumnWidths.Length; index++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (index + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", ColumnWidths[index].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("sheetData");
        WriteRow(writer, 1, 34, 1, ["项目验证计划"]);
        WriteRow(writer, 2, 48, 2, ["说明：本计划由项目成员线上维护；需要数据支撑的验证项应填写实际数据。导出文件中的评审与会签栏用于打印签字。"]);
        WriteRow(writer, 3, 28, 3, ["编制", export.Plan.PreparedBy ?? export.Plan.UpdatedBy, null, "校对", null, null, "审核", null, null, "批准"]);
        WriteRow(writer, 4, 28, 3, ["项目号", export.Project.Code, null, null, "项目名称", export.Project.Name, null, null, "验证日期", Date(export.Plan.ValidationDate)]);
        WriteRow(writer, 5, 34, 4, ["序号", "验证内容", null, null, null, "信息来源", "验证日期", "结果（如有数据需填入）", "责任人", "备注"]);

        var rowNumber = 6;
        var sequence = 1;
        foreach (var category in export.Plan.Items.OrderBy(item => item.SortOrder).GroupBy(item => item.CategoryName))
        {
            WriteRow(writer, rowNumber, 24, 6, [$"分类：{category.Key}"]);
            merges.Add($"A{rowNumber}:J{rowNumber}");
            rowNumber++;
            foreach (var item in category)
            {
                WriteRow(writer, rowNumber, 36, 5,
                [
                    sequence++, item.ValidationContent, null, null, null, item.InformationSource,
                    Date(item.ValidationDate), item.Result, item.ResponsiblePerson, item.Remark
                ]);
                merges.Add($"B{rowNumber}:E{rowNumber}");
                rowNumber++;
            }
        }

        for (var index = 0; index < 5; index++)
        {
            WriteRow(writer, rowNumber, 36, 5, [sequence++, "", "", "", "", "", "", "", "", ""]);
            merges.Add($"B{rowNumber}:E{rowNumber}");
            rowNumber++;
        }

        WriteRow(writer, rowNumber, 34, 7, ["评审结果", "□ 本次项目验证计划通过", null, null, null, null, "□ 本次项目验证计划不通过"]);
        merges.Add($"B{rowNumber}:F{rowNumber}");
        merges.Add($"G{rowNumber}:J{rowNumber}");
        rowNumber++;
        WriteRow(writer, rowNumber, 28, 3, ["会签", null, "项目经理", "机械负责人", "电气负责人", "装配钳工", "调试工程师", "生产经理", "技术经理", "质量经理"]);
        merges.Add($"A{rowNumber}:B{rowNumber + 1}");
        rowNumber++;
        WriteRow(writer, rowNumber, 42, 5, []);
        rowNumber++;
        WriteRow(writer, rowNumber, 30, 2, ["签字确认（责任确权）需对验证内容及结果进行检查；技术经理、质量经理负责监督和确认。"]);
        merges.Add($"A{rowNumber}:J{rowNumber}");
        writer.WriteEndElement();

        writer.WriteStartElement("mergeCells");
        writer.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var merge in merges)
        {
            writer.WriteStartElement("mergeCell");
            writer.WriteAttributeString("ref", merge);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteStartElement("pageMargins");
        writer.WriteAttributeString("left", "0.3");
        writer.WriteAttributeString("right", "0.3");
        writer.WriteAttributeString("top", "0.5");
        writer.WriteAttributeString("bottom", "0.5");
        writer.WriteAttributeString("header", "0.2");
        writer.WriteAttributeString("footer", "0.2");
        writer.WriteEndElement();
        writer.WriteStartElement("pageSetup");
        writer.WriteAttributeString("orientation", "portrait");
        writer.WriteAttributeString("fitToWidth", "1");
        writer.WriteAttributeString("fitToHeight", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter writer, int rowNumber, double height, int style, IReadOnlyList<object?> values)
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
            writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            if (value is int number)
            {
                writer.WriteElementString("v", number.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteStartElement("t");
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(value.ToString() ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
    private static string ColumnName(int index) => index < 26 ? ((char)('A' + index)).ToString() : throw new ArgumentOutOfRangeException(nameof(index));
    private static void WriteText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="5">
            <font><sz val="10"/><name val="Microsoft YaHei"/></font>
            <font><b/><sz val="20"/><name val="Microsoft YaHei"/></font>
            <font><sz val="10"/><name val="Microsoft YaHei"/><color rgb="FF4B5563"/></font>
            <font><b/><sz val="10"/><name val="Microsoft YaHei"/></font>
            <font><b/><sz val="10"/><name val="Microsoft YaHei"/><color rgb="FFFFFFFF"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF0F766E"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFDFF5F1"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FF9CA3AF"/></left><right style="thin"><color rgb="FF9CA3AF"/></right><top style="thin"><color rgb="FF9CA3AF"/></top><bottom style="thin"><color rgb="FF9CA3AF"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="8">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="4" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="3" fillId="3" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
