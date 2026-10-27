using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

internal static partial class PdfReleasePageEditor
{
    private sealed record FillCell(int Page, double PageHeight, string Name, string Value, Box Bounds);

    internal static void FillTitleBlock(string input, string output, IReadOnlyDictionary<string, string> values)
    {
        Init();
        // 保留SolidWorks跨内容流共享的字体、线型与绘图状态。
        using var joined = new MemoryStream();
        using (var original = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            foreach (var page in original.Pages)
                if (page.Contents.Elements.Count > 1)
                {
                    var content = page.Contents.CreateSingleContent().Stream.Value;
                    page.Contents.Elements.Clear();
                    page.Contents.AppendContent().CreateStream(content);
                }
            original.Save(joined, false);
        }
        var bytes = joined.ToArray();
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var document = Native.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)bytes.Length, null);
        if (document == 0) { pin.Free(); throw new PdmRuleException("PDF标题栏无法读取，请重新转换后重试。"); }
        var cells = new List<FillCell>();
        try
        {
            for (var index = 0; index < Native.FPDF_GetPageCount(document); index++)
            {
                var page = Native.FPDF_LoadPage(document, index);
                try
                {
                    if (Native.FPDFPage_GetRotation(page) != 0) throw new PdmRuleException($"PDF第{index + 1}页存在旋转设置，无法可靠填写标题栏。");
                    var items = ReadItems(page);
                    var lines = ReadLines(items);
                    var height = Native.FPDF_GetPageHeightF(page);
                    var labels = LabelTexts(items.Where(item => item.Type == 1));
                    var logo = items.Where(item => item.Type == 1 && item.Value.Contains("UPTON", StringComparison.OrdinalIgnoreCase)
                        && item.Value.Contains("Automation Systems", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (logo.Length != 1) throw new PdmRuleException($"PDF第{index + 1}页未唯一识别到UPTON图框，无法填写属性。");
                    var remove = new HashSet<nint>();
                    foreach (var (key, value) in values)
                    {
                        var field = PdfTitleBlockFields.Get(key);
                        var matches = labels.Where(text => text.Bounds.Top <= logo[0].Bounds.Top + 45
                            && text.Bounds.Left >= Native.FPDF_GetPageWidthF(page) * .45
                            && field.Labels.Any(label => CleanLabel(text.Value).Equals(CleanLabel(label), StringComparison.OrdinalIgnoreCase))).ToArray();
                        if (matches.Length != 1) throw new PdmRuleException($"PDF第{index + 1}页未唯一找到“{field.Name}”图框格（标签：{string.Join("／", field.Labels)}），请补齐对应标签后重新转图。");
                        Box cell;
                        try
                        {
                            var label = matches[0].Bounds;
                            var labelBox = CellAround(label, lines);
                            // 部分旧UPTON图框的重量标签与数值共用一格。
                            cell = labelBox.Width >= 85 && label.Left - labelBox.Left < 12 && labelBox.Right - label.Right > 28
                                ? new Box(label.Right + 4, labelBox.Bottom, labelBox.Right, labelBox.Top)
                                : CellAround(new(labelBox.Right + 2, labelBox.Bottom + 2, labelBox.Right + 3, labelBox.Top - 2), lines);
                        }
                        catch (PdmRuleException)
                        {
                            throw new PdmRuleException($"PDF第{index + 1}页“{field.Name}”格线无法闭合定位，请检查图框。");
                        }
                        if (cell.Width < 8 || cell.Height < 8 || cells.Any(c => c.Page == index && c.Bounds.Overlaps(cell)))
                            throw new PdmRuleException($"PDF第{index + 1}页“{field.Name}”填写区域不明确，请检查图框格线。");
                        foreach (var item in items.Where(item => item.Type is 1 or 3 && item.Bounds.Overlaps(cell)))
                        {
                            if (item.Type != 1 || !Inside(item.Bounds, cell) || labels.Any(text => text.Bounds == item.Bounds
                                && PdfTitleBlockFields.All.Any(f => f.Labels.Any(l => CleanLabel(text.Value) == CleanLabel(l)))))
                                throw new PdmRuleException($"PDF第{index + 1}页“{field.Name}”区域包含其他图文，已停止填写，请检查图框。");
                            remove.Add(item.Handle);
                        }
                        if (lines.Any(line => Crosses(new(cell.Left + 1, cell.Bottom + 1, cell.Right - 1, cell.Top - 1), line)))
                            throw new PdmRuleException($"PDF第{index + 1}页“{field.Name}”区域存在内部格线，无法安全填写。");
                        cells.Add(new(index, height, field.Name, value, cell));
                    }
                    foreach (var item in items.Where(item => remove.Contains(item.Handle)))
                    {
                        if (Native.FPDFPage_RemoveObject(page, item.Handle) == 0) throw new PdmRuleException("PDF旧属性无法清理，请重新转换后重试。");
                        Native.FPDFPageObj_Destroy(item.Handle);
                    }
                    if (Native.FPDFPage_GenerateContent(page) == 0) throw new PdmRuleException("PDF标题栏整理失败，请重新转换后重试。");
                }
                finally { Native.FPDF_ClosePage(page); }
            }
            using var stream = File.Create(output);
            Native.WriteBlock callback = (_, data, size) =>
            {
                try { var buffer = new byte[size]; Marshal.Copy(data, buffer, 0, buffer.Length); stream.Write(buffer); return 1; }
                catch { return 0; }
            };
            var writer = new Native.FileWriter { Version = 1, WriteBlock = callback };
            if (Native.FPDF_SaveAsCopy(document, ref writer, 2) == 0) throw new PdmRuleException("PDF标题栏保存失败，请检查存储权限后重试。");
            GC.KeepAlive(callback);
        }
        finally { Native.FPDF_CloseDocument(document); pin.Free(); }

        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Modify);
        foreach (var pageCells in cells.GroupBy(cell => cell.Page))
        {
            using var graphics = XGraphics.FromPdfPage(pdf.Pages[pageCells.Key], XGraphicsPdfPageOptions.Append);
            foreach (var cell in pageCells) DrawCell(graphics, cell);
        }
        pdf.Save(output);
    }

    private static void DrawCell(XGraphics graphics, FillCell cell)
    {
        for (var size = 8.5; size >= 5.5; size -= .5)
        {
            var font = new XFont("UPLM-YaHei", size);
            var rows = new List<string>();
            var row = "";
            foreach (var character in cell.Value.Replace("\r", ""))
            {
                if (character == '\n') { rows.Add(row); row = ""; continue; }
                if (graphics.MeasureString(row + character, font).Width > cell.Bounds.Width - 5)
                { rows.Add(row); row = ""; }
                row += character;
            }
            rows.Add(row);
            if (rows.Any(text => graphics.MeasureString(text, font).Width > cell.Bounds.Width - 5)
                || rows.Count * size * 1.35 > cell.Bounds.Height - 3) continue;
            var first = cell.PageHeight - cell.Bounds.Top + (cell.Bounds.Height - rows.Count * size * 1.35) / 2;
            for (var index = 0; index < rows.Count; index++)
                graphics.DrawString(rows[index], font, XBrushes.Black,
                    new XRect(cell.Bounds.Left + 2.5, first + index * size * 1.35, cell.Bounds.Width - 5, size * 1.35), XStringFormats.TopCenter);
            return;
        }
        throw new PdmRuleException($"PDF第{cell.Page + 1}页“{cell.Name}”内容超出图框格，请扩大对应格后重试；系统不会截断内容。");
    }

    private static string CleanLabel(string value) => Regex.Replace(value, @"[\s：:]", "");
    private static bool Inside(Box inner, Box outer) => inner.Left >= outer.Left - .6 && inner.Right <= outer.Right + .6
        && inner.Bottom >= outer.Bottom - .6 && inner.Top <= outer.Top + .6;

    private static List<Text> LabelTexts(IEnumerable<Item> source)
    {
        var items = source.OrderByDescending(item => item.Bounds.Bottom).ThenBy(item => item.Bounds.Left).ToArray();
        var result = items.Select(item => new Text(item.Value, item.Bounds)).ToList();
        foreach (var item in items)
        {
            var next = items.Where(other => other.Handle != item.Handle && Math.Abs(other.Bounds.Bottom - item.Bounds.Bottom) < 1.5
                && other.Bounds.Left > item.Bounds.Left).OrderBy(other => other.Bounds.Left).ToArray();
            var text = item.Value; var bounds = item.Bounds;
            foreach (var other in next)
            {
                if (other.Bounds.Left - bounds.Right > 3) break;
                text += other.Value;
                bounds = new(bounds.Left, Math.Min(bounds.Bottom, other.Bounds.Bottom), other.Bounds.Right, Math.Max(bounds.Top, other.Bounds.Top));
                result.Add(new(text, bounds));
            }
        }
        return result.Distinct().ToList();
    }

    private static Box CellAround(Box text, IReadOnlyList<Line> lines)
    {
        var x = (text.Left + text.Right) / 2; var y = (text.Bottom + text.Top) / 2;
        var verticals = lines.Where(line => Math.Abs(line.X1 - line.X2) < .1
            && Math.Min(line.Y1, line.Y2) < y && Math.Max(line.Y1, line.Y2) > y).ToArray();
        var horizontals = lines.Where(line => Math.Abs(line.Y1 - line.Y2) < .1
            && Math.Min(line.X1, line.X2) < x && Math.Max(line.X1, line.X2) > x).ToArray();
        var left = verticals.Where(l => l.X1 < text.Left).Select(l => l.X1).DefaultIfEmpty(double.NaN).Max();
        var right = verticals.Where(l => l.X1 > text.Right).Select(l => l.X1).DefaultIfEmpty(double.NaN).Min();
        var bottom = horizontals.Where(l => l.Y1 < text.Bottom).Select(l => l.Y1).DefaultIfEmpty(double.NaN).Max();
        var top = horizontals.Where(l => l.Y1 > text.Top).Select(l => l.Y1).DefaultIfEmpty(double.NaN).Min();
        if (!double.IsFinite(left + right + bottom + top)) throw new PdmRuleException("PDF标题栏格线无法闭合定位，请检查属性标签周围的图框格线。");
        return new(left, bottom, right, top);
    }
}
