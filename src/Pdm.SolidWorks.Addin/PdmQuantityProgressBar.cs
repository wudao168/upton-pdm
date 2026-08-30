using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmQuantityProgressBar : Control
{
    private const int BorderThickness = 3;
    private int completed;
    private int total;

    public PdmQuantityProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        BackColor = Color.Black;
        ForeColor = Color.FromArgb(237, 125, 49);
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "文件处理进度";
    }

    public void SetProgress(int completedCount, int totalCount)
    {
        total = Math.Max(0, totalCount);
        completed = total > 0
            ? Math.Max(0, Math.Min(completedCount, total))
            : 0;
        AccessibleDescription = total > 0
            ? string.Concat(completed, " / ", total)
            : "准备中";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);

        Rectangle bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        eventArgs.Graphics.Clear(Color.Black);
        using (var borderPen = new Pen(Color.White, BorderThickness))
        {
            int offset = BorderThickness / 2;
            eventArgs.Graphics.DrawRectangle(
                borderPen,
                offset,
                offset,
                Math.Max(0, bounds.Width - BorderThickness),
                Math.Max(0, bounds.Height - BorderThickness));
        }

        var contentBounds = new Rectangle(
            BorderThickness,
            BorderThickness,
            Math.Max(0, bounds.Width - (BorderThickness * 2)),
            Math.Max(0, bounds.Height - (BorderThickness * 2)));
        int filledWidth = total > 0
            ? (int)Math.Round(contentBounds.Width * (double)completed / total)
            : 0;
        var filledBounds = new Rectangle(contentBounds.X, contentBounds.Y, filledWidth, contentBounds.Height);
        using (var fillBrush = new SolidBrush(Color.White))
        {
            eventArgs.Graphics.FillRectangle(fillBrush, filledBounds);
        }

        string progressText = FormatProgressText();
        TextFormatFlags textFlags = TextFormatFlags.HorizontalCenter |
                                    TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.SingleLine |
                                    TextFormatFlags.NoPadding;
        using (Region previousClip = eventArgs.Graphics.Clip)
        {
            if (filledWidth > 0)
            {
                eventArgs.Graphics.SetClip(filledBounds);
                TextRenderer.DrawText(eventArgs.Graphics, progressText, Font, bounds, ForeColor, textFlags);
            }

            int remainingWidth = Math.Max(0, contentBounds.Width - filledWidth);
            if (remainingWidth > 0)
            {
                var remainingBounds = new Rectangle(
                    contentBounds.X + filledWidth,
                    contentBounds.Y,
                    remainingWidth,
                    contentBounds.Height);
                eventArgs.Graphics.Clip = previousClip;
                eventArgs.Graphics.SetClip(remainingBounds);
                TextRenderer.DrawText(eventArgs.Graphics, progressText, Font, bounds, ForeColor, textFlags);
            }

            eventArgs.Graphics.Clip = previousClip;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font.Dispose();
        }

        base.Dispose(disposing);
    }

    private string FormatProgressText()
    {
        if (total <= 0)
        {
            return "准备中";
        }

        int digits = Math.Max(4, total.ToString().Length);
        return string.Concat(
            completed.ToString().PadLeft(digits, '0'),
            " / ",
            total.ToString().PadLeft(digits, '0'));
    }
}
