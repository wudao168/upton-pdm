using Upton.Pdm.Application;
using System.IO.Compression;
using System.Xml.Linq;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Infrastructure;

/// <summary>从本次冻结的发布工作簿生成采购清单，保持原有提前抵扣及净变更列。</summary>
public static class BomPurchaseListPdf
{
    public const string Suffix = "-采购清单.pdf";
    public static byte[] Write(byte[] workbook, Project project, ReleasePackage package, string title)
    {
        using var archive = new ZipArchive(new MemoryStream(workbook));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string[] shared = [];
        if (archive.GetEntry("xl/sharedStrings.xml") is { } strings)
        { using var input = strings.Open(); shared = XDocument.Load(input).Descendants(ns + "si").Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToArray(); }
        using var sheet = (archive.GetEntry("xl/worksheets/sheet1.xml") ?? throw new PdmRuleException("发布清单缺少数据页，无法生成采购PDF。")).Open();
        var rows = XDocument.Load(sheet).Descendants(ns + "row").Select(row => row.Elements(ns + "c").Select(cell =>
        {
            var name = (string?)cell.Attribute("r") ?? "A1";
            var index = 0; foreach (var letter in name.TakeWhile(char.IsLetter)) index = index * 26 + letter - 'A' + 1;
            var value = (string?)cell.Element(ns + "v") ?? string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
            if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var number) && number >= 0 && number < shared.Length) value = shared[number];
            return (Index: index - 1, Value: value);
        }).ToArray()).ToArray();
        var header = Array.FindIndex(rows, row => row.Any(cell => cell.Value == "物料编码") && row.Any(cell => cell.Value == "数量" || cell.Value.Contains("BOM总量") || cell.Value.Contains("BOM目标总量")));
        if (header < 0) throw new PdmRuleException("发布清单表头不完整，无法生成采购PDF。");
        var count = rows[header].Max(cell => cell.Index) + 1;
        var table = rows.Skip(header).Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell.Value))).Select(row => Enumerable.Range(0,count).Select(i => row.FirstOrDefault(cell => cell.Index == i).Value ?? "").ToArray()).ToArray();
        lock (PdfReleasePageEditor.Gate)
        {
            GlobalFontSettings.FontResolver ??= new PdfReleaseFontResolver();
            using var pdf = new PdfDocument();
            pdf.Info.Title = $"{project.Code} {title} 采购清单";
            pdf.Info.Subject = package.Number;
            var font = new XFont("UPLM-YaHei", 8, XFontStyleEx.Regular);
            var bold = new XFont("UPLM-YaHei", 8, XFontStyleEx.Bold);
            var heading = new XFont("UPLM-YaHei", 14, XFontStyleEx.Bold);
            var weights = table[0].Select(text => text.Contains("名称") || text.Contains("型号") || text.Contains("备注") ? 3d : text.Contains("编码") ? 2d : 1d).ToArray();
            XGraphics? graphics = null; double y = 0; double[] widths = []; double bottom = 0;
            string[] Wrap(string value, double width)
            {
                var lines = new List<string>(); var line = "";
                foreach (var ch in value.Replace("\r", ""))
                {
                    if (ch == '\n' || line.Length > 0 && graphics!.MeasureString(line + ch, font).Width > width - 8) { lines.Add(line); line = ""; }
                    if (ch != '\n') line += ch;
                }
                lines.Add(line); return lines.ToArray();
            }
            void Draw(string[][] cells, int offset, int take, bool isHeader)
            {
                var height = take * 12 + 8d; var x = 28d;
                for (var col = 0; col < count; col++)
                {
                    if (isHeader) graphics!.DrawRectangle(new XSolidBrush(XColor.FromArgb(239,242,241)),x,y,widths[col],height);
                    graphics!.DrawRectangle(new XPen(XColors.LightGray,.5),x,y,widths[col],height);
                    for (var line=offset;line<Math.Min(cells[col].Length,offset+take);line++)
                        graphics.DrawString(cells[col][line],isHeader?bold:font,XBrushes.Black,new XRect(x+4,y+4+(line-offset)*12,widths[col]-8,12),XStringFormats.TopLeft);
                    x+=widths[col];
                }
                y+=height;
            }
            void Page()
            {
                graphics?.Dispose(); var page=pdf.AddPage(); page.Size=PageSize.A3; page.Orientation=PageOrientation.Landscape;
                graphics=XGraphics.FromPdfPage(page); bottom=page.Height.Point-35;
                widths=weights.Select(w=>(page.Width.Point-56)*w/weights.Sum()).ToArray();
                graphics.DrawString($"{title} · 采购清单",heading,XBrushes.Black,new XRect(28,20,page.Width.Point-56,22),XStringFormats.TopLeft);
                graphics.DrawString($"项目：{project.Code} · {project.Name}    发布包：{package.Number}",font,XBrushes.Black,new XRect(28,47,page.Width.Point-56,16),XStringFormats.TopLeft);
                graphics.DrawString("本清单取自本次发布冻结BOM，数量和净变更以各列含义为准。",font,XBrushes.Gray,new XRect(28,65,page.Width.Point-56,14),XStringFormats.TopLeft);
                graphics.DrawString($"第 {pdf.PageCount} 页",font,XBrushes.Gray,new XRect(28,page.Height.Point-24,page.Width.Point-56,12),XStringFormats.TopLeft);
                y=86; var cells=table[0].Select((v,i)=>Wrap(v,widths[i])).ToArray();Draw(cells,0,cells.Max(c=>c.Length),true);
            }
            Page();
            foreach(var row in table.Skip(1))
            {
                var cells=row.Select((v,i)=>Wrap(v,widths[i])).ToArray();var lines=cells.Max(c=>c.Length);var offset=0;
                while(offset<lines)
                {
                    var capacity=(int)((bottom-y-8)/12);
                    if(capacity<1 || offset==0 && lines<=45 && lines>capacity) { Page();capacity=(int)((bottom-y-8)/12); }
                    var take=Math.Min(capacity,lines-offset);Draw(cells,offset,take,false);offset+=take;
                }
            }
            graphics?.Dispose(); using var output=new MemoryStream();pdf.Save(output,false);return output.ToArray();
        }
    }
}
