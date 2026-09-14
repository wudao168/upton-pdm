using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ThumbnailPreviewDialog : Form
{
    private const double ZoomStep = 1.15;
    private const double MinimumZoom = 0.10;
    private const double MaximumZoom = 8.00;

    private readonly Image image;
    private readonly Panel viewport = new Panel();
    private readonly PictureBox picture = new PictureBox();
    private double zoom = 1;
    private bool fitMode = true;
    private bool dragging;
    private Point dragStart;
    private Point scrollStart;

    internal ThumbnailPreviewDialog(Image source, string fileName, Rectangle workingArea)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        image = new Bitmap(source);
        Text = string.Concat(string.IsNullOrWhiteSpace(fileName) ? "图档" : fileName, " - 缩略图");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        KeyPreview = true;
        BackColor = Color.FromArgb(34, 39, 46);

        var maximumWidth = Math.Max(360, (int)Math.Round(workingArea.Width * 0.5));
        var maximumHeight = Math.Max(280, (int)Math.Round(workingArea.Height * 0.5));
        MaximumSize = new Size(maximumWidth, maximumHeight);
        MinimumSize = new Size(Math.Min(420, maximumWidth), Math.Min(320, maximumHeight));
        Size = MaximumSize;

        var header = BuildHeader();
        viewport.Dock = DockStyle.Fill;
        viewport.AutoScroll = true;
        viewport.BackColor = Color.FromArgb(34, 39, 46);
        viewport.Cursor = Cursors.SizeAll;

        picture.Image = image;
        picture.SizeMode = PictureBoxSizeMode.StretchImage;
        picture.BackColor = Color.Transparent;
        picture.Cursor = Cursors.SizeAll;

        viewport.Controls.Add(picture);
        Controls.Add(viewport);
        Controls.Add(header);

        Shown += (_, _) => FitImage();
        viewport.Resize += (_, _) =>
        {
            if (fitMode)
            {
                FitImage();
            }
            else
            {
                PositionPicture();
            }
        };
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Close();
            }
        };

        RegisterImageInteraction(this);
        RegisterImageInteraction(viewport);
        RegisterImageInteraction(picture);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(12, 5, 8, 5),
            BackColor = Color.FromArgb(244, 247, 251)
        };
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "滚轮缩放 · 拖动查看 · 双击适合窗口 · Esc关闭",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(73, 88, 108)
        };
        var close = new Button
        {
            Dock = DockStyle.Right,
            Width = 72,
            Text = "关闭",
            FlatStyle = FlatStyle.System
        };
        close.Click += (_, _) => Close();
        header.Controls.Add(hint);
        header.Controls.Add(close);
        return header;
    }

    private void RegisterImageInteraction(Control control)
    {
        control.MouseWheel += (_, eventArgs) => ZoomAtCursor(eventArgs.Delta);
        control.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                FitImage();
            }
        };
        control.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left)
            {
                return;
            }
            dragging = true;
            dragStart = Cursor.Position;
            scrollStart = new Point(-viewport.AutoScrollPosition.X, -viewport.AutoScrollPosition.Y);
            control.Capture = true;
        };
        control.MouseMove += (_, _) =>
        {
            if (!dragging)
            {
                return;
            }
            var current = Cursor.Position;
            viewport.AutoScrollPosition = new Point(
                Math.Max(0, scrollStart.X - (current.X - dragStart.X)),
                Math.Max(0, scrollStart.Y - (current.Y - dragStart.Y)));
        };
        control.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                dragging = false;
                control.Capture = false;
            }
        };
    }

    private void FitImage()
    {
        if (image.Width <= 0 || image.Height <= 0 || viewport.ClientSize.Width <= 0 || viewport.ClientSize.Height <= 0)
        {
            return;
        }

        fitMode = true;
        zoom = Math.Min(
            (viewport.ClientSize.Width - 12D) / image.Width,
            (viewport.ClientSize.Height - 12D) / image.Height);
        zoom = Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom));
        viewport.AutoScrollPosition = Point.Empty;
        ResizePicture();
    }

    private void ZoomAtCursor(int delta)
    {
        if (delta == 0 || image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        fitMode = false;
        zoom = delta > 0 ? zoom * ZoomStep : zoom / ZoomStep;
        zoom = Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom));
        ResizePicture();
    }

    private void ResizePicture()
    {
        picture.Size = new Size(
            Math.Max(1, (int)Math.Round(image.Width * zoom)),
            Math.Max(1, (int)Math.Round(image.Height * zoom)));
        viewport.AutoScrollMinSize = picture.Size;
        PositionPicture();
    }

    private void PositionPicture()
    {
        picture.Location = new Point(
            picture.Width < viewport.ClientSize.Width ? (viewport.ClientSize.Width - picture.Width) / 2 : 0,
            picture.Height < viewport.ClientSize.Height ? (viewport.ClientSize.Height - picture.Height) / 2 : 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            picture.Image = null;
            image.Dispose();
        }
        base.Dispose(disposing);
    }
}
