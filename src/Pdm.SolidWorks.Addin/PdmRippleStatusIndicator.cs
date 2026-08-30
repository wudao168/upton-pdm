using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmRippleStatusIndicator : Control
{
    private const int CellSize = 7;
    private const int CellSpacing = 1;
    private const int AnimationDurationMilliseconds = 1500;
    private static readonly int[] AnimationDelays =
    {
        0, 100, 200,
        100, 200, 300,
        200, 300, 400
    };
    private static readonly Color[] CellColors =
    {
        Color.FromArgb(0x00, 0xFF, 0x87),
        Color.FromArgb(0x0C, 0xFD, 0x95),
        Color.FromArgb(0x17, 0xFB, 0xA2),
        Color.FromArgb(0x23, 0xF9, 0xB2),
        Color.FromArgb(0x30, 0xF7, 0xC3),
        Color.FromArgb(0x3D, 0xF5, 0xD4),
        Color.FromArgb(0x45, 0xF4, 0xDE),
        Color.FromArgb(0x53, 0xF1, 0xF0),
        Color.FromArgb(0x60, 0xEF, 0xFF)
    };

    private readonly Timer animationTimer = new Timer { Interval = 30 };
    private DateTime animationStartedAt;
    private bool active;
    private bool online;

    public PdmRippleStatusIndicator()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);

        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Graphic;
        animationTimer.Tick += (_, __) => Invalidate();
    }

    public bool Active
    {
        get => active;
        set
        {
            if (active == value)
            {
                return;
            }

            active = value;
            animationStartedAt = DateTime.UtcNow;
            UpdateAnimationState();
            Invalidate();
        }
    }

    public bool Online
    {
        get => online;
        set
        {
            if (online == value)
            {
                return;
            }

            online = value;
            Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        UpdateAnimationState();
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        base.OnVisibleChanged(eventArgs);
        UpdateAnimationState();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        int gridSize = (CellSize + (CellSpacing * 2)) * 3;
        int left = (ClientSize.Width - gridSize) / 2;
        int top = (ClientSize.Height - gridSize) / 2;
        double elapsed = (DateTime.UtcNow - animationStartedAt).TotalMilliseconds;

        for (int index = 0; index < CellColors.Length; index++)
        {
            int row = index / 3;
            int column = index % 3;
            float opacity = active ? GetRippleOpacity(elapsed, AnimationDelays[index]) : 1F;
            Color cellColor = active || online
                ? CellColors[index]
                : Color.FromArgb(255, 184, 86);
            int alpha = Math.Max(0, Math.Min(255, (int)Math.Round(opacity * 255F)));
            if (alpha == 0)
            {
                continue;
            }

            var cellBounds = new RectangleF(
                left + column * (CellSize + (CellSpacing * 2)) + CellSpacing,
                top + row * (CellSize + (CellSpacing * 2)) + CellSpacing,
                CellSize,
                CellSize);
            using (var cellBrush = new SolidBrush(Color.FromArgb(alpha, cellColor)))
            using (var cellPath = CreateRoundedRectangle(cellBounds, 2F))
            {
                eventArgs.Graphics.FillPath(cellBrush, cellPath);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animationTimer.Stop();
            animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static float GetRippleOpacity(double elapsedMilliseconds, int delayMilliseconds)
    {
        double delayedElapsed = elapsedMilliseconds - delayMilliseconds;
        if (delayedElapsed < 0D)
        {
            return 0F;
        }

        double progress = (delayedElapsed % AnimationDurationMilliseconds) / AnimationDurationMilliseconds;
        if (progress >= 0.6D)
        {
            return 0F;
        }

        double phase = progress < 0.3D ? progress / 0.3D : (0.6D - progress) / 0.3D;
        return (float)(phase * phase * (3D - (2D * phase)));
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2F;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
        path.CloseFigure();
        return path;
    }

    private void UpdateAnimationState()
    {
        if (active && Visible && IsHandleCreated)
        {
            if (!animationTimer.Enabled)
            {
                animationStartedAt = DateTime.UtcNow;
                animationTimer.Start();
            }

            return;
        }

        animationTimer.Stop();
    }
}
