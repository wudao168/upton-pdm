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

    public PdmSegmentedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Fixed;
        tabFont = new Font(Font, FontStyle.Bold);
        ApplyTheme("a");
    }

    internal int HeaderHeight => Math.Max(34, Font.Height + 12) + 10;
    public override Rectangle DisplayRectangle => new Rectangle(0, HeaderHeight + 5, ClientSize.Width, Math.Max(0, ClientSize.Height - HeaderHeight - 5));

    internal Rectangle GetSegmentBounds(int index)
    {
        if (TabCount == 0) return Rectangle.Empty;
        var step = Math.Max(0, ClientSize.Width - 8d) / TabCount;
        var left = 5 + (int)Math.Round(index * step);
        var right = 5 + (int)Math.Round((index + 1) * step) - 2;
        return new Rectangle(left, 5, Math.Max(0, right - left), HeaderHeight - 10);
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
        tabFont = new Font(Font, FontStyle.Bold);
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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = Rounded(new Rectangle(0, 0, Width - 1, HeaderHeight - 1), 7))
        using (var surface = new SolidBrush(colors["--pdm-surface"]))
        using (var border = new Pen(colors["--pdm-border"]))
        {
            e.Graphics.FillPath(surface, track);
            e.Graphics.DrawPath(border, track);
        }
        for (var index = 0; index < TabCount; index++)
        {
            var bounds = GetSegmentBounds(index);
            if (index == SelectedIndex)
            {
                var shadowBounds = bounds;
                shadowBounds.Offset(0, 1);
                using (var shadow = Rounded(shadowBounds, 10))
                using (var brush = new SolidBrush(Color.FromArgb(20, colors["--pdm-sidebar"]))) e.Graphics.FillPath(brush, shadow);
                using (var selected = Rounded(bounds, 10))
                using (var brush = new SolidBrush(colors["--pdm-theme-accent-soft-strong"])) e.Graphics.FillPath(brush, selected);
            }
            var textColor = index == SelectedIndex ? colors["--pdm-theme-accent-hover"]
                : index == hoveredIndex ? colors["--pdm-theme-accent"] : colors["--pdm-text-soft"];
            TextRenderer.DrawText(e.Graphics, TabPages[index].Text, tabFont, Rectangle.Inflate(bounds, -3, 0),
                TabPages[index].Enabled ? textColor : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (index == SelectedIndex && Focused && ShowFocusCues)
                using (var focus = Rounded(Rectangle.Inflate(bounds, -1, -1), 9))
                using (var pen = new Pen(colors["--pdm-theme-accent"])) e.Graphics.DrawPath(pen, focus);
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

    private static Dictionary<string, Color> ReadTheme(string theme)
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
