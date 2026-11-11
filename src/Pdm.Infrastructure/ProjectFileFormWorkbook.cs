using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class ProjectFileFormWorkbook : IProjectFileFormWorkbook
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace Art = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public byte[] Build(string kind, ProjectFileForm form, Project project, Project root, OrganizationDirectory directory,
        string actor, DateTimeOffset now, string? evidenceFileName)
    {
        var title = ProjectFileFormPermissions.Title(kind);
        var rows = new XElement(Sheet + "sheetData");
        var merges = new XElement(Sheet + "mergeCells");
        var images = new List<(int Row, ProjectFormAttachment File)>();
        var rowNumber = 0;

        void AddRow(int height, int style, params object?[] values)
        {
            rowNumber++;
            var row = new XElement(Sheet + "row", new XAttribute("r", rowNumber), new XAttribute("ht", height), new XAttribute("customHeight", 1));
            for (var index = 0; index < 8; index++)
            {
                var cell = new XElement(Sheet + "c", new XAttribute("r", $"{(char)('A' + index)}{rowNumber}"), new XAttribute("s", style));
                var value = values.ElementAtOrDefault(index);
                if (value is int or decimal) cell.Add(new XElement(Sheet + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
                else if (value is not null)
                {
                    cell.SetAttributeValue("t", "inlineStr");
                    cell.Add(new XElement(Sheet + "is", new XElement(Sheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value.ToString())));
                }
                row.Add(cell);
            }
            rows.Add(row);
        }
        void Merge(char first, char last) => merges.Add(new XElement(Sheet + "mergeCell", new XAttribute("ref", $"{first}{rowNumber}:{last}{rowNumber}")));
        void Pairs(string left, string? value, string right, string? second)
        {
            AddRow(30, 1, left, value, null, null, right, second); Merge('B', 'D'); Merge('F', 'H');
        }
        void Section(string label, string? content)
        {
            AddRow(23, 4, label); Merge('A', 'H');
            var text = content ?? "";
            do
            {
                var count = Math.Min(text.Length, 650);
                if (count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1])) count--;
                var part = text[..count];
                var lines = part.Split('\n').Sum(line => Math.Max(1, (line.Length + 67) / 68));
                AddRow(Math.Clamp(20 + 16 * lines, 48, 390), 3, part); Merge('A', 'H');
                text = text[count..];
            } while (text.Length > 0);
        }

        var data = form.Data;
        int headerRow;
        double[] widths;
        if (kind == ProjectFileFormPermissions.Sample)
        {
            widths = [8, 29, 12, 10, 24, 17, 19, 30];
            Pairs("收货人", data.Recipient, "联系方式", data.Contact);
            AddRow(36, 1, "收货地址", data.Address); Merge('B', 'H');
            AddRow(27, 2, "序号", "物料名称", "数量", "单位", "图片", "需求日期", "适用阶段", "备注");
            headerRow = rowNumber;
            var sequence = 0;
            foreach (var item in data.Items ?? [])
            {
                AddRow(item.Image is null ? 45 : 80, 1, ++sequence, item.MaterialName, item.Quantity, item.Unit,
                    item.Image is null ? "—" : null, Date(item.RequiredOn), item.Phase, item.Note);
                if (item.Image is not null) images.Add((rowNumber, item.Image));
            }
        }
        else if (ProjectFileFormPermissions.IsAcceptance(kind))
        {
            var acceptance = data.Acceptance ?? new ProjectAcceptanceDetails();
            widths = [14, 19, 19, 19, 14, 19, 19, 19];
            var pre = kind == ProjectFileFormPermissions.PreAcceptance;
            Pairs("验收类别", pre ? "预验收（PAT）" : "终验收（FAT）", "文档编号", form.Summary.Number);
            Pairs("合同编号", acceptance.ContractNumber, "项目号", project.Code);
            Pairs("项目名称", project.Name, "验收时间", Date(data.ChangedOn));
            Pairs("供方", data.Supplier, "需方", data.Customer);
            Pairs("验收地点", acceptance.Location, "验收人员", acceptance.Attendees);
            headerRow = 0;
            Section("验收标准", acceptance.Standard);
            Section("验收说明", acceptance.Explanation);
            if (pre) Section("整改要求", acceptance.Corrections);
            AddRow(28, 1, "验收结果", acceptance.Passed switch
            {
                true => "☑ 验收合格　□ 验收不合格",
                false => "□ 验收合格　☑ 验收不合格",
                null => "□ 验收合格　□ 验收不合格",
            }); Merge('B', 'H');
            Section(pre ? "验收人意见" : "验收人意见或建议", acceptance.Comments);
            AddRow(50, 1, "需方确认", null, null, null, "供方确认"); Merge('B', 'D'); Merge('F', 'H');
            Pairs("验收日期", null, "验收日期", null);
            if (!string.IsNullOrWhiteSpace(acceptance.Terms)) Section("验收约定", acceptance.Terms);
        }
        else
        {
            widths = [14, 19, 19, 19, 14, 19, 19, 19];
            Pairs("文件编号", form.Summary.Number, "变更日期", Date(data.ChangedOn));
            Pairs("供方", data.Supplier, "需方", data.Customer);
            headerRow = 2;
            Section("变更原因", data.Reason);
            Section("变更前内容", data.Before);
            Section("变更后内容", data.After);
            AddRow(28, 1, "是否付费", data.Paid == true ? "是" : "否"); Merge('B', 'H');
            Section("备注", data.Note);
            if (data.Evidence is { Count: > 0 })
                Section("客户确认附件", $"随单据存档：{evidenceFileName}\n" + string.Join("、", data.Evidence.Select(file => file.FileName)));
            AddRow(50, 1, "需方签字", null, null, null, "供方签字"); Merge('B', 'D'); Merge('F', 'H');
            Pairs("日期", null, "日期", null);
        }

        merges.SetAttributeValue("count", merges.Elements().Count());
        var cols = new XElement(Sheet + "cols", widths.Select((width, index) => new XElement(Sheet + "col",
            new XAttribute("min", index + 1), new XAttribute("max", index + 1), new XAttribute("width", width), new XAttribute("customWidth", 1))));
        var sheet = new XElement(Sheet + "worksheet", cols, rows, merges);
        var version = form.Summary.FileVersion == 0 ? "草稿" : $"V{form.Summary.FileVersion}";
        var metadata = ProjectWorkbookMetadata.ForProject(project, root, directory, title, "版本", version, now, actor);
        var bytes = ProjectWorkbookHeader.Apply(Package(title, sheet), metadata, originalHeaderRow: headerRow);
        return images.Count == 0 ? bytes : EmbedImages(bytes, images);
    }

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static byte[] Package(string title, XElement sheet)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            const string mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
            XElement Override(string part, string type) => new(Types + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
            XElement Default(string extension, string type) => new(Types + "Default", new XAttribute("Extension", extension), new XAttribute("ContentType", type));
            Write(zip, "[Content_Types].xml", new XElement(Types + "Types", Default("rels", "application/vnd.openxmlformats-package.relationships+xml"),
                Default("xml", "application/xml"), Override("/xl/workbook.xml", mime + "sheet.main+xml"),
                Override("/xl/worksheets/sheet1.xml", mime + "worksheet+xml"), Override("/xl/styles.xml", mime + "styles+xml")));
            Write(zip, "_rels/.rels", new XElement(Rel + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
            Write(zip, "xl/workbook.xml", new XElement(Sheet + "workbook", new XElement(Sheet + "bookViews",
                new XElement(Sheet + "workbookView")), new XElement(Sheet + "sheets",
                new XElement(Sheet + "sheet", new XAttribute("name", title), new XAttribute("sheetId", 1), new XAttribute(Office + "id", "rId1")))));
            Write(zip, "xl/_rels/workbook.xml.rels", new XElement(Rel + "Relationships", Relationship("rId1", "worksheet", "worksheets/sheet1.xml"),
                Relationship("rId2", "styles", "styles.xml")));
            Write(zip, "xl/styles.xml", XElement.Parse(Styles));
            Write(zip, "xl/worksheets/sheet1.xml", sheet);
        }
        return output.ToArray();
    }

    private static byte[] EmbedImages(byte[] workbook, IReadOnlyList<(int Row, ProjectFormAttachment File)> images)
    {
        using var output = new MemoryStream();
        output.Write(workbook); output.Position = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            const string drawingPath = "xl/drawings/uplm-header1.xml";
            const string relPath = "xl/drawings/_rels/uplm-header1.xml.rels";
            var drawing = Read(zip, drawingPath);
            var relations = Read(zip, relPath);
            var types = Read(zip, "[Content_Types].xml");
            var logo = drawing.Element(Drawing + "oneCellAnchor")!;
            for (var index = 0; index < images.Count; index++)
            {
                var (row, file) = images[index];
                var png = Path.GetExtension(file.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase);
                var (width, height) = Dimensions(file.Content!, png);
                var scale = Math.Min(152d / width, 86d / height);
                const int emuPerPixel = 9525;
                var extension = png ? "png" : "jpeg";
                var media = $"sample-picture-{index + 1}.{extension}";
                var relation = $"samplePicture{index + 1}";
                using (var image = zip.CreateEntry("xl/media/" + media).Open()) image.Write(file.Content!);
                if (!types.Elements(Types + "Default").Any(element => (string?)element.Attribute("Extension") == extension))
                    types.AddFirst(new XElement(Types + "Default", new XAttribute("Extension", extension), new XAttribute("ContentType", "image/" + extension)));
                relations.Add(Relationship(relation, "image", "../media/" + media));
                var anchor = new XElement(logo);
                var from = anchor.Element(Drawing + "from")!;
                from.SetElementValue(Drawing + "col", 4);
                from.SetElementValue(Drawing + "row", row + 5); // The shared template prepends six worksheet rows.
                from.SetElementValue(Drawing + "colOff", (int)((168 - width * scale) / 2 * emuPerPixel));
                from.SetElementValue(Drawing + "rowOff", (int)((106 - height * scale) / 2 * emuPerPixel));
                anchor.Element(Drawing + "ext")!.SetAttributeValue("cx", (int)(width * scale * emuPerPixel));
                anchor.Element(Drawing + "ext")!.SetAttributeValue("cy", (int)(height * scale * emuPerPixel));
                var name = anchor.Descendants(Drawing + "cNvPr").Single();
                name.SetAttributeValue("id", index + 2); name.SetAttributeValue("name", file.FileName);
                anchor.Descendants(Art + "blip").Single().SetAttributeValue(Office + "embed", relation);
                drawing.Add(anchor);
            }
            Write(zip, drawingPath, drawing); Write(zip, relPath, relations); Write(zip, "[Content_Types].xml", types);
        }
        return output.ToArray();
    }

    private static (int Width, int Height) Dimensions(ReadOnlySpan<byte> content, bool png)
    {
        static (int, int) Valid(int width, int height) => width > 0 && height > 0 && (long)width * height <= 20_000_000
            ? (width, height) : throw new PdmRuleException("样件图片尺寸无效或超过2000万像素，请缩小图片后重试。");
        if (png && content.Length >= 24 && content.Slice(12, 4).SequenceEqual("IHDR"u8))
            return Valid(BinaryPrimitives.ReadInt32BigEndian(content[16..]), BinaryPrimitives.ReadInt32BigEndian(content[20..]));
        if (!png)
        {
            var index = 2;
            while (index + 4 <= content.Length && content[index] == 255)
            {
                var marker = content[index + 1];
                if (marker == 255) { index++; continue; }
                var segment = BinaryPrimitives.ReadUInt16BigEndian(content[(index + 2)..]);
                if (segment < 2 || index + 2 + segment > content.Length) break;
                if (segment >= 8 && marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                    return Valid(BinaryPrimitives.ReadUInt16BigEndian(content[(index + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(content[(index + 5)..]));
                index += 2 + segment;
            }
        }
        throw new PdmRuleException("无法读取样件图片尺寸，请重新选择有效的 PNG 或 JPEG 图片。");
    }

    private static XElement Relationship(string id, string kind, string target) => new(Rel + "Relationship", new XAttribute("Id", id),
        new XAttribute("Type", Office.NamespaceName + "/" + kind), new XAttribute("Target", target));
    private static XElement Read(ZipArchive zip, string name) { using var stream = zip.GetEntry(name)!.Open(); return XElement.Load(stream); }
    private static void Write(ZipArchive zip, string name, XElement element)
    {
        if (zip.Mode == ZipArchiveMode.Update) zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        element.Save(stream);
    }

    private const string Styles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2"><font><sz val="11"/><color rgb="FF203149"/><name val="Microsoft YaHei"/></font><font><b/><sz val="11"/><color rgb="FF203149"/><name val="Microsoft YaHei"/></font></fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF1F5FA"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="2"><border/><border><left style="thin"><color rgb="FFBBC9DA"/></left><right style="thin"><color rgb="FFBBC9DA"/></right><top style="thin"><color rgb="FFBBC9DA"/></top><bottom style="thin"><color rgb="FFBBC9DA"/></bottom></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1" applyBorder="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1" applyBorder="1" applyFill="1" applyFont="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1" applyBorder="1"><alignment horizontal="left" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1" applyBorder="1" applyFill="1" applyFont="1"><alignment horizontal="left" vertical="center" wrapText="1"/></xf>
          </cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
