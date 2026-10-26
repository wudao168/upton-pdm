using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchiveProgressBar : Control
{
    private bool determinate;
    private bool running = true;
    private int animation;
    private string hint;
    public int Maximum { get; private set; } = 100;
    public int Value { get; private set; }
    public bool IsIndeterminate => running && !determinate;
    public string Hint { get => hint; set { hint = value; Invalidate(); } }

    public ArchiveProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = "处理中…";
    }

    public void Report(int completed, int total)
    {
        running = true; determinate = total > 0;
        if (determinate)
        {
            Maximum = total; Value = Math.Max(0, Math.Min(total, completed));
            Text = ProgressText();
        }
        else Text = "处理中…";
        Invalidate();
    }

    public void Finish(bool failed)
    {
        running = false; hint = null;
        if (failed) Text = determinate ? ProgressText() + "（已停止）" : "已停止";
        else
        {
            var counted = determinate;
            if (!counted) Maximum = 100;
            Value = Maximum; determinate = true;
            Text = counted ? ProgressText() : "100%";
        }
        Invalidate();
    }

    public void Animate()
    {
        if (!IsIndeterminate || hint != null) return;
        animation = (animation + 1) % 16; Invalidate();
    }

    private string ProgressText() => (int)((long)Value * 100 / Maximum) + "%　" + Value + " / " + Maximum;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.FromArgb(233, 239, 245));
        if (hint == null)
        {
            var width = Math.Max(0, ClientSize.Width - 2);
            var fillWidth = determinate ? (int)((long)width * Value / Maximum) : width / 4;
            var left = determinate ? 1 : 1 + (width - fillWidth) * animation / 15;
            if (fillWidth > 0 && (determinate || running))
                using (var brush = new SolidBrush(Color.FromArgb(140, 188, 232)))
                    e.Graphics.FillRectangle(brush, left, 1, fillWidth, Math.Max(0, Height - 2));
        }
        using (var border = new Pen(Color.FromArgb(188, 197, 206)))
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        TextRenderer.DrawText(e.Graphics, hint ?? Text, Font, ClientRectangle, Color.FromArgb(33, 78, 121),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
