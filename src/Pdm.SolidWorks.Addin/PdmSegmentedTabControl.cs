using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmSegmentedTabControl : TabControl
{
    private Font tabFont;
    private string theme = "a";
    private Dictionary<string, Color> colors;
    private int hoveredIndex = -1;
    private TableLayoutPanel alignedToolbar;

    internal void AlignWith(TableLayoutPanel toolbar)
    {
        alignedToolbar = toolbar;
        toolbar.Layout += (_, _) => Invalidate();
    }

    public PdmSegmentedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        tabFont = new Font(Font, FontStyle.Regular);
        ApplyTheme("a");
    }

    private float horizontalScale = 1F;
    private float verticalScale = 1F;
    internal int HeaderHeight => Math.Max(1, (int)Math.Round(30 * verticalScale));
    internal int HeaderGap => Math.Max(1, (int)Math.Round(5 * verticalScale));
    public override Rectangle DisplayRectangle => new Rectangle(0, HeaderHeight + HeaderGap, ClientSize.Width, Math.Max(0, ClientSize.Height - HeaderHeight - HeaderGap));

    internal Rectangle GetSegmentBounds(int index)
    {
        if (TabCount == 0) return Rectangle.Empty;
        if (alignedToolbar != null && alignedToolbar.ColumnCount == TabCount * 2 - 1)
        {
            // 直接复用实际按钮的屏幕位置，涵盖高 DPI 下原生 TabPage 的边缘偏移。
            var action = alignedToolbar.GetControlFromPosition(index * 2, 0);
            if (action != null && alignedToolbar.IsHandleCreated && IsHandleCreated)
            {
                var leftEdge = PointToClient(action.PointToScreen(Point.Empty)).X;
                return new Rectangle(leftEdge, 0, action.Width, HeaderHeight);
            }

        }
        var gap = Math.Max(1, (int)Math.Round(6 * horizontalScale));
        var available = Math.Max(0, ClientSize.Width - gap * (TabCount - 1));
        // TableLayoutPanel 将整除余量交给最后一列，与下方四个操作按钮一致。
        var width = available / TabCount;
        var left = index * (width + gap);
        return new Rectangle(left, 0, index == TabCount - 1 ? ClientSize.Width - left : width, HeaderHeight);
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        horizontalScale *= factor.Width;
        verticalScale *= factor.Height;
        base.ScaleControl(factor, specified);
    }

    internal void ApplyTheme(string value)
    {
        var next = value == "c" || value == "p" || value == "o" || value == "g" ? value : "a";
        if (colors != null && next == theme) return;
        theme = next;
        colors = ReadTheme(next);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        tabFont?.Dispose();
        tabFont = new Font(Font, FontStyle.Regular);
        PerformLayout();
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var next = HitTest(e.Location);
        if (next == hoveredIndex) return;
        hoveredIndex = next;
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoveredIndex = -1;
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0201)
        {
            var point = new Point((short)(message.LParam.ToInt64() & 0xffff), (short)((message.LParam.ToInt64() >> 16) & 0xffff));
            if (point.Y < HeaderHeight)
            {
                var index = HitTest(point);
                if (index >= 0 && TabPages[index].Enabled) SelectedIndex = index;
                Focus();
                return;
            }
        }
        base.WndProc(ref message);
    }

    private int HitTest(Point point)
    {
        for (var index = 0; index < TabCount; index++)
            if (GetSegmentBounds(index).Contains(point)) return index;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        if (colors == null || Width < 2) return;
        for (var index = 0; index < TabCount; index++)
        {
            var bounds = GetSegmentBounds(index);
            var enabled = TabPages[index].Enabled;
            var background = !enabled ? Color.FromArgb(224, 228, 233)
                : index == SelectedIndex ? Color.FromArgb(21, 126, 77) : Color.FromArgb(72, 84, 99);
            if (enabled && index == hoveredIndex) background = ControlPaint.Light(background, 0.12F);
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, bounds);
            var caption = TabPages[index].Text;
            var fittedSize = tabFont.Size;
            while (fittedSize > 6.5F)
            {
                using (var candidate = new Font(tabFont.FontFamily, fittedSize, FontStyle.Regular))
                    if (TextRenderer.MeasureText(e.Graphics, caption, candidate, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width <= bounds.Width - 2) break;
                fittedSize -= 0.25F;
            }
            using (var fittedFont = new Font(tabFont.FontFamily, fittedSize, FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, caption, fittedFont, Rectangle.Inflate(bounds, -1, 0),
                    enabled ? Color.White : Color.FromArgb(145, 151, 159),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (index == SelectedIndex && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3), Color.White, background);
        }
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Dictionary<string, Color> ReadTheme(string theme)
    {
        // 与网页使用同一份主题变量，避免原生页签另存一套近似颜色。
        using (var stream = typeof(PdmSegmentedTabControl).Assembly.GetManifestResourceStream("Upton.Pdm.SolidWorks.Assets.Themes.css"))
        using (var reader = new StreamReader(stream))
        {
            var css = reader.ReadToEnd();
            var values = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (Match block in Regex.Matches(css, @":root\s*\{([^}]+)\}")) ReadColors(block.Groups[1].Value, values);
            var selected = Regex.Match(css, ":root\\[data-pdm-theme=\"" + theme + "\"\\]\\s*\\{([^}]+)\\}");
            if (selected.Success) ReadColors(selected.Groups[1].Value, values);
            return values;
        }
    }

    private static void ReadColors(string css, Dictionary<string, Color> values)
    {
        foreach (Match value in Regex.Matches(css, @"(--[\w-]+)\s*:\s*(#[\da-fA-F]{3,8})\s*;"))
            values[value.Groups[1].Value] = ColorTranslator.FromHtml(value.Groups[2].Value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tabFont?.Dispose();
        base.Dispose(disposing);
    }
}
