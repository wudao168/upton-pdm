using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Upton.Pdm.Application;
using ZXing;
using ZXing.Common;

namespace Upton.Pdm.Infrastructure;

internal static class PdfReleasePageEditor
{
    internal static readonly object Gate = new();
    private static bool initialized;
    internal const double Mm = 72d / 25.4;
    internal const double CodeSize = 18 * Mm * 29 / 37;
    internal const double Stroke = .45;
    internal const double FrameHeight = CodeSize + 2 * (Mm + Stroke / 2);
    internal sealed record Box(double Left, double Bottom, double Right, double Top)
    {
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public bool Overlaps(Box other) => Math.Max(Left, other.Left) + .5 < Math.Min(Right, other.Right)
            && Math.Max(Bottom, other.Bottom) + .5 < Math.Min(Top, other.Top);
        public Box Move(double dy) => this with { Bottom = Bottom + dy, Top = Top + dy };
    }
    internal sealed record Text(string Value, Box Bounds);
    internal sealed record Layout(double PageHeight, Box Frame);
    private sealed record Item(nint Handle, int Type, Box Bounds, string Value);
    private sealed record Line(double X1, double Y1, double X2, double Y2);

    internal static IReadOnlyList<Layout> Prepare(string input, string output, Guid drawingId, IReadOnlySet<string> cadCodes)
    {
        Init();
        var document = Native.FPDF_LoadDocument(input, null);
        if (document == 0) throw new PdmRuleException("PDF无法读取，请重新转换该图纸后重试标识。");
        try
        {
            var result = new List<Layout>();
            for (var index = 0; index < Native.FPDF_GetPageCount(document); index++)
            {
                var page = Native.FPDF_LoadPage(document, index);
                try
                {
                    if (Native.FPDFPage_GetRotation(page) != 0)
                        throw new PdmRuleException($"PDF第{index + 1}页带有旋转设置，请按图纸原始方向导出后重试标识。");
                    var items = ReadItems(page);
                    var lines = ReadLines(items);
                    var company = items.Where(item => item.Type == 1 && item.Value.Contains("UPTON", StringComparison.OrdinalIgnoreCase)
                        && item.Value.Contains("Automation Systems", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (company.Length != 1) throw new PdmRuleException($"PDF第{index + 1}页未唯一识别到UPTON标题栏，已保留原PDF，请核对图框后重试标识。");
                    var label = company[0].Bounds;
                    var verticals = lines.Where(line => Math.Abs(line.X1 - line.X2) < .05
                        && Math.Min(line.Y1, line.Y2) < label.Bottom && Math.Max(line.Y1, line.Y2) > label.Top).ToArray();
                    var left = verticals.Where(line => line.X1 <= label.Left).Select(line => line.X1).DefaultIfEmpty(double.NaN).Max();
                    var right = verticals.Where(line => line.X1 >= label.Right).Select(line => line.X1).DefaultIfEmpty(double.NaN).Min();
                    var top = lines.Where(line => Math.Abs(line.Y1 - line.Y2) < .05 && line.Y1 > label.Top
                        && Math.Min(line.X1, line.X2) <= left + 1 && Math.Max(line.X1, line.X2) >= right - 1)
                        .Select(line => line.Y1).DefaultIfEmpty(double.NaN).Min();
                    if (!double.IsFinite(left + right + top) || right - left < 175)
                        throw new PdmRuleException($"PDF第{index + 1}页的LOGO框边线无法可靠定位，请核对图框后重试标识。");
                    var frame = new Box(left, top, right, top + FrameHeight);
                    var oldNotes = items.Where(item => item.Type == 1 && Regex.IsMatch(item.Value.Trim(), @"^型号\s+.+[·]\s*正式版次\s+[A-Z]$"))
                        .Where(item => item.Bounds.Left >= left - 10 && item.Bounds.Bottom >= top - 30 && item.Bounds.Top <= top + 110).ToArray();
                    var oldModels = oldNotes.Select(item => Regex.Match(item.Value.Trim(), @"^型号\s+(.+?)\s*[·]\s*正式版次\s+[A-Z]$").Groups[1].Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var ownedImages = items.Where(item => item.Type == 3 && IsOwnedCode(item, drawingId, oldModels, cadCodes, frame)).ToArray();
                    if (oldNotes.Length > 0 && ownedImages.Length == 0)
                        throw new PdmRuleException("检测到旧版次文字但无法核对旧二维码归属，已保留原PDF，请核对后重试标识。");
                    var removed = ownedImages.Concat(oldNotes).Select(item => item.Handle).ToHashSet();
                    foreach (var item in items.Where(item => removed.Contains(item.Handle)))
                    {
                        if (Native.FPDFPage_RemoveObject(page, item.Handle) == 0) throw new PdmRuleException("无法清理PDF中的旧系统标识，请重新转换后重试。");
                        Native.FPDFPageObj_Destroy(item.Handle);
                    }
                    var headings = items.Where(item => !removed.Contains(item.Handle) && item.Type == 1
                        && item.Value.Trim().TrimEnd('：', ':') == "技术要求" && item.Bounds.Left >= left - 5 && item.Bounds.Right <= right).ToArray();
                    var requirements = headings.Length == 1
                        ? items.Where(item => !removed.Contains(item.Handle) && item.Type == 1
                            && Math.Abs(item.Bounds.Left - headings[0].Bounds.Left) < 5
                            && item.Bounds.Bottom >= top && item.Bounds.Top <= headings[0].Bounds.Top + .1).ToArray() : [];
                    var moving = requirements.Any(item => item.Bounds.Overlaps(frame));
                    var moved = moving ? requirements.Select(item => item.Handle).ToHashSet() : [];
                    if (moving)
                    {
                        var movement = Math.Max(68, frame.Top + Mm - requirements.Min(item => item.Bounds.Bottom));
                        foreach (var requirement in requirements)
                        {
                            var target = requirement.Bounds.Move(movement);
                            if (target.Top > Native.FPDF_GetPageHeightF(page) - 10 || Collides(target, items, lines, removed.Concat(moved).ToHashSet()))
                                throw new PdmRuleException($"PDF第{index + 1}页的技术要求上方空间不足，已保留原PDF，请调整预留区后重试标识。");
                        }
                        foreach (var item in requirements) Native.FPDFPageObj_Transform(item.Handle, 1, 0, 0, 1, 0, movement);
                    }
                    if (Collides(frame, items, lines, removed.Concat(moved).ToHashSet()))
                        throw new PdmRuleException($"PDF第{index + 1}页的标识位置存在图文内容，已保留原PDF，请核对预留区后重试标识。");
                    if (Native.FPDFPage_GenerateContent(page) == 0) throw new PdmRuleException("PDF页面准备失败，请重新转换后重试标识。");
                    result.Add(new(Native.FPDF_GetPageHeightF(page), frame));
                }
                finally { Native.FPDF_ClosePage(page); }
            }
            using var stream = File.Create(output);
            Native.WriteBlock callback = (_, data, size) =>
            {
                try { var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length); stream.Write(bytes); return 1; }
                catch { return 0; }
            };
            var writer = new Native.FileWriter { Version = 1, WriteBlock = callback };
            if (Native.FPDF_SaveAsCopy(document, ref writer, 2) == 0) throw new PdmRuleException("PDF准备结果保存失败，请检查存储目录后重试。");
            GC.KeepAlive(callback);
            return result;
        }
        finally { Native.FPDF_CloseDocument(document); }
    }

    private static bool Collides(Box area, IReadOnlyList<Item> items, IReadOnlyList<Line> lines, HashSet<nint> ignored)
    {
        if (items.Any(item => item.Type is 1 or 3 && !ignored.Contains(item.Handle) && area.Overlaps(item.Bounds))) return true;
        var inner = new Box(area.Left + 1, area.Bottom + 1, area.Right - 1, area.Top - 1);
        return lines.Any(line => Crosses(inner, line));
    }

    private static bool Crosses(Box box, Line line)
    {
        var t0 = 0d; var t1 = 1d;
        var dx = line.X2 - line.X1; var dy = line.Y2 - line.Y1;
        foreach (var (p, q) in new[] { (-dx, line.X1 - box.Left), (dx, box.Right - line.X1), (-dy, line.Y1 - box.Bottom), (dy, box.Top - line.Y1) })
        {
            if (Math.Abs(p) < .00001) { if (q < 0) return false; continue; }
            var t = q / p;
            if (p < 0) t0 = Math.Max(t0, t); else t1 = Math.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
    }

    private static List<Item> ReadItems(nint page)
    {
        var textPage = Native.FPDFText_LoadPage(page);
        try
        {
            var result = new List<Item>();
            for (var i = 0; i < Native.FPDFPage_CountObjects(page); i++)
            {
                var item = Native.FPDFPage_GetObject(page, i);
                if (Native.FPDFPageObj_GetBounds(item, out var left, out var bottom, out var right, out var top) == 0) continue;
                var type = Native.FPDFPageObj_GetType(item);
                var text = "";
                if (type == 1)
                {
                    var length = Native.FPDFTextObj_GetText(item, textPage, null, 0);
                    if (length > 0) { var buffer = new byte[length]; Native.FPDFTextObj_GetText(item, textPage, buffer, length); text = Encoding.Unicode.GetString(buffer).TrimEnd('\0'); }
                }
                result.Add(new(item, type, new(left, bottom, right, top), text));
            }
            return result;
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    private static List<Line> ReadLines(IEnumerable<Item> items)
    {
        var lines = new List<Line>();
        foreach (var item in items.Where(item => item.Type == 2))
        {
            if (Native.FPDFPageObj_GetMatrix(item.Handle, out var matrix) == 0) continue;
            (double X, double Y)? previous = null;
            for (var i = 0; i < Native.FPDFPath_CountSegments(item.Handle); i++)
            {
                var segment = Native.FPDFPath_GetPathSegment(item.Handle, i);
                if (Native.FPDFPathSegment_GetPoint(segment, out var x, out var y) == 0) continue;
                var current = (X: matrix.A * x + matrix.C * y + matrix.E, Y: matrix.B * x + matrix.D * y + matrix.F);
                if (previous is { } last && Native.FPDFPathSegment_GetType(segment) == 0) lines.Add(new(last.X, last.Y, current.X, current.Y));
                previous = current;
            }
        }
        return lines;
    }

    private static bool IsOwnedCode(Item image, Guid drawingId, HashSet<string> oldModels, IReadOnlySet<string> cadCodes, Box frame)
    {
        var bitmap = Native.FPDFImageObj_GetBitmap(image.Handle);
        if (bitmap == 0) return false;
        try
        {
            var value = Decode(bitmap);
            var parts = value?.Split('|');
            if (parts is { Length: 4 } && parts[0] == "UPLM-DRAWING" && Guid.TryParse(parts[3], out var id) && id == drawingId) return true;
            if (value is not null && cadCodes.Contains(value)) return true;
            // The independent CAD plug-in can replace the formal QR with a model-only
            // code on save. Require the matching legacy release note and its location.
            return value is not null && oldModels.Contains(value) && image.Bounds.Left >= frame.Left - 10
                && image.Bounds.Right <= frame.Right + 30 && image.Bounds.Bottom >= frame.Bottom - 30
                && image.Bounds.Top <= frame.Bottom + 110;
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }

    private static string? Decode(nint bitmap)
    {
        var width = Native.FPDFBitmap_GetWidth(bitmap); var height = Native.FPDFBitmap_GetHeight(bitmap);
        var stride = Native.FPDFBitmap_GetStride(bitmap); var format = Native.FPDFBitmap_GetFormat(bitmap);
        var channels = format == 1 ? 1 : format == 2 ? 3 : 4;
        var bytes = new byte[width * height * channels]; var pointer = Native.FPDFBitmap_GetBuffer(bitmap);
        for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * stride, bytes, row * width * channels, width * channels);
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var pixelFormat = format == 1 ? RGBLuminanceSource.BitmapFormat.Gray8
            : format == 2 ? RGBLuminanceSource.BitmapFormat.BGR24
            : format == 3 ? RGBLuminanceSource.BitmapFormat.BGR32 : RGBLuminanceSource.BitmapFormat.BGRA32;
        var decoded = reader.Decode(bytes, width, height, pixelFormat);
        if (decoded is not null) return decoded.Text;
        reader.Options.PureBarcode = true;
        return reader.Decode(bytes, width, height, pixelFormat)?.Text;
    }

    internal static IReadOnlyList<Text> Texts(byte[] pdf)
    {
        Init();
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        var document = Native.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdf.Length, null);
        if (document == 0) { pin.Free(); throw new PdmRuleException("PDF标识内容无法读取，请检查字体后重试。"); }
        var page = Native.FPDF_LoadPage(document, 0);
        try { return ReadItems(page).Where(item => item.Type == 1).Select(item => new Text(item.Value, item.Bounds)).ToArray(); }
        finally { Native.FPDF_ClosePage(page); Native.FPDF_CloseDocument(document); pin.Free(); }
    }

    internal static string? DecodePage(string path, int index, int dpi = 200)
    {
        Init();
        var document = Native.FPDF_LoadDocument(path, null);
        if (document == 0) throw new PdmRuleException("PDF无法读取。");
        var page = Native.FPDF_LoadPage(document, index);
        var width = (int)Math.Ceiling(Native.FPDF_GetPageWidthF(page) * dpi / 72);
        var height = (int)Math.Ceiling(Native.FPDF_GetPageHeightF(page) * dpi / 72);
        var bitmap = Native.FPDFBitmap_Create(width, height, 1);
        try { Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF); Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0); return Decode(bitmap); }
        finally { Native.FPDFBitmap_Destroy(bitmap); Native.FPDF_ClosePage(page); Native.FPDF_CloseDocument(document); }
    }

    private static void Init() { if (initialized) return; Native.FPDF_InitLibrary(); initialized = true; }

    private static class Native
    {
        private const string Dll = "pdfium";
        [StructLayout(LayoutKind.Sequential)] internal struct Matrix { public float A, B, C, D, E, F; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteBlock(nint writer, nint data, uint size);
        [StructLayout(LayoutKind.Sequential)] internal struct FileWriter { public int Version; public WriteBlock WriteBlock; }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(nint document);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDF_LoadPage(nint document, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageWidthF(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern float FPDF_GetPageHeightF(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_GetRotation(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_CountObjects(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFPage_GetObject(nint page, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetType(nint item);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetBounds(nint item, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetMatrix(nint item, out Matrix matrix);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFPageObj_Transform(nint item, double a, double b, double c, double d, double e, double f);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_RemoveObject(nint page, nint item);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFPageObj_Destroy(nint item);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPage_GenerateContent(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint FPDFTextObj_GetText(nint item, nint textPage, [Out] byte[]? buffer, uint length);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFText_ClosePage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPath_CountSegments(nint item);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFPath_GetPathSegment(nint item, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPathSegment_GetPoint(nint segment, out float x, out float y);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFPathSegment_GetType(nint segment);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFImageObj_GetBitmap(nint image);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBitmap_GetBuffer(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetWidth(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetHeight(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetFormat(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int left, int top, int width, int height, int rotation, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int FPDF_SaveAsCopy(nint document, ref FileWriter writer, uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(nint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(nint document);
    }
}
