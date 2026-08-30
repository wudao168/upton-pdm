using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using Forms = System.Windows.Forms;

namespace Upton.Pdm.Desktop;

internal sealed class EDrawingsPreviewControl : Forms.UserControl
{
    private static readonly Color PrimaryText = Color.FromArgb(15, 23, 42);
    private static readonly Color SecondaryText = Color.FromArgb(71, 85, 105);
    private static readonly Color PreviewBackground = Color.FromArgb(151, 166, 184);
    private static readonly object DiagnosticLogSync = new();
    private static readonly string DiagnosticLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON",
        "PLM",
        "Logs",
        "client-edrawings.log");

    private readonly EDrawingsAxHost viewer = new();
    private readonly Forms.ToolStrip toolbar = new();
    private readonly Forms.TableLayoutPanel propertiesPanel = new();
    private readonly Forms.Timer repaintTimer = new();
    private readonly Dictionary<string, Forms.ToolStripButton> modeButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Forms.ToolStripButton measureButton;
    private object? markupControl;
    private string currentDocumentName = string.Empty;
    private int pendingRepaintAttempts;
    private bool documentOpen;
    private bool documentTransitioning;
    private bool disposed;
    private PreviewButtonTheme buttonTheme = PreviewButtonTheme.Resolve("a");

    internal event Action<string>? UserMessageRequested;

    internal void ApplyTheme(string theme)
    {
        buttonTheme = PreviewButtonTheme.Resolve(theme);
        foreach (Forms.ToolStripItem item in toolbar.Items)
        {
            if (item is not Forms.ToolStripButton button || button.Tag is not string command)
            {
                continue;
            }

            var previousImage = button.Image;
            button.Image = CreateCommandIcon(command, buttonTheme.IconColor);
            previousImage?.Dispose();
        }
        toolbar.Renderer = new TransparentToolStripRenderer(buttonTheme);
        toolbar.Invalidate();
    }

    internal EDrawingsPreviewControl()
    {
        SuspendLayout();
        BackColor = PreviewBackground;

        viewer.Dock = Forms.DockStyle.Fill;
        viewer.BackColor = PreviewBackground;
        viewer.BeginInit();
        Controls.Add(viewer);
        viewer.EndInit();

        ConfigureToolbar();
        AddModeButton("select", "选择").Checked = true;
        AddModeButton("pan", "平移");
        AddModeButton("rotate", "旋转");
        AddModeButton("zoom", "缩放");
        AddCommandButton("fit", "适合窗口");
        AddToolbarSpacer();
        AddCommandButton("front", "前视");
        AddCommandButton("top", "上视");
        AddCommandButton("right", "右视");
        AddCommandButton("isometric", "等轴测");
        AddToolbarSpacer();
        measureButton = AddModeButton("measure", "测量");

        ConfigurePropertiesPanel();
        Controls.Add(propertiesPanel);
        Controls.Add(toolbar);
        viewer.SendToBack();
        toolbar.BringToFront();
        propertiesPanel.BringToFront();

        repaintTimer.Interval = 220;
        repaintTimer.Tick += OnRepaintTimerTick;
        Resize += (_, _) =>
        {
            LayoutOverlays();
            ScheduleRefresh(2);
        };
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                LayoutOverlays();
                ScheduleRefresh(4);
            }
        };

        Dock = Forms.DockStyle.Fill;
        ResumeLayout(true);
        LayoutOverlays();
    }

    internal void OpenDocument(string path)
    {
        ThrowIfDisposed();
        CloseDocument();
        var documentName = Path.GetFileName(path);
        WriteDiagnostic("open-start", documentName);
        documentTransitioning = true;
        try
        {
            viewer.OpenDocument(path);
            documentOpen = true;
            currentDocumentName = documentName;
            WriteDiagnostic("open-complete", documentName);
        }
        catch (Exception exception)
        {
            WriteDiagnostic("open-failed", $"{documentName} | {exception.GetType().Name}: {exception.Message}");
            throw;
        }
        finally
        {
            documentTransitioning = false;
        }
        UpdateToolbarState();
        LayoutOverlays();
        ScheduleRefresh(3);
    }

    internal void FitDocument()
    {
        if (!CanUseDocument)
        {
            return;
        }

        try
        {
            dynamic control = viewer.ActiveControl;
            control.ZoomToFit();
        }
        catch
        {
            // Older eDrawings controls may not expose ZoomToFit.
        }

        ScheduleRefresh(2);
    }

    internal void ExecuteCommand(string command)
    {
        if (!CanUseDocument)
        {
            return;
        }

        if (TryGetMarkupOperator(command, out var markupOperator))
        {
            if (!TryActivateMarkup(markupOperator, out var message))
            {
                UserMessageRequested?.Invoke(message);
            }
            return;
        }

        try
        {
            dynamic control = viewer.ActiveControl;
            switch (command)
            {
                case "select":
                    control.ViewOperator = 0;
                    break;
                case "rotate":
                    control.ViewOperator = 1;
                    break;
                case "zoom":
                    control.ViewOperator = 2;
                    break;
                case "pan":
                    control.ViewOperator = 4;
                    break;
                case "fit":
                    control.ViewOrientation = 7;
                    break;
                case "front":
                    control.ViewOrientation = 0;
                    break;
                case "top":
                    control.ViewOrientation = 2;
                    break;
                case "right":
                    control.ViewOrientation = 5;
                    break;
                case "isometric":
                    control.ViewOrientation = 6;
                    break;
            }
        }
        catch
        {
            // Some older eDrawings controls do not expose every operator.
        }

        if (command is "fit" or "front" or "top" or "right" or "isometric")
        {
            ScheduleRefresh(2);
        }
    }

    internal bool IsMeasureAvailable
    {
        get
        {
            if (!CanUseDocument)
            {
                return false;
            }

            try
            {
                dynamic control = viewer.ActiveControl;
                return control.IsMeasureEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool TryActivateMeasure(out string message)
    {
        message = string.Empty;
        if (!IsMeasureAvailable)
        {
            message = "当前图档未启用测量，不能使用测量工具。";
            return false;
        }

        try
        {
            dynamic control = viewer.ActiveControl;
            markupControl = control.CoCreateInstance("{9FCFE7FE-2ED5-4720-94F9-6B712F7D11A2}");
            if (markupControl == null)
            {
                message = "当前 eDrawings 控件未提供测量组件。";
                return false;
            }

            dynamic markup = markupControl;
            markup.ViewOperator = 11;
            return true;
        }
        catch (Exception exception)
        {
            message = $"测量工具启动失败：{exception.Message}";
            return false;
        }
    }

    private bool TryActivateMarkup(int markupOperator, out string message)
    {
        message = string.Empty;
        try
        {
            dynamic control = viewer.ActiveControl;
            markupControl ??= control.CoCreateInstance("{9FCFE7FE-2ED5-4720-94F9-6B712F7D11A2}");
            if (markupControl == null)
            {
                message = "当前 eDrawings 控件未提供图形批注组件。";
                return false;
            }
            dynamic markup = markupControl;
            markup.ViewOperator = markupOperator;
            return true;
        }
        catch (Exception exception)
        {
            message = $"图形批注工具启动失败：{exception.Message}";
            return false;
        }
    }

    private static bool TryGetMarkupOperator(string command, out int markupOperator)
    {
        markupOperator = command switch
        {
            "markup-text-leader" => 0,
            "markup-text" => 1,
            "markup-cloud-leader" => 2,
            "markup-cloud-text" => 3,
            "markup-cloud" => 4,
            "markup-line" => 5,
            "markup-rectangle" => 6,
            "markup-circle" => 7,
            "markup-spline" => 9,
            "markup-stamp" => 14,
            _ => -1,
        };
        return markupOperator >= 0;
    }

    internal void UpdateProperties(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        if (disposed)
        {
            return;
        }

        propertiesPanel.SuspendLayout();
        propertiesPanel.Controls.Clear();
        propertiesPanel.RowStyles.Clear();
        propertiesPanel.RowCount = 0;

        foreach (var item in values)
        {
            var row = propertiesPanel.RowCount++;
            propertiesPanel.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.AutoSize));

            var label = new Forms.Label
            {
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular),
                ForeColor = SecondaryText,
                Margin = new Forms.Padding(0, 0, 10, 5),
                Text = item.Key,
            };
            var value = new Forms.Label
            {
                AutoEllipsis = false,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = PrimaryText,
                Margin = new Forms.Padding(0, 0, 0, 5),
                MaximumSize = new Size(420, 0),
                MinimumSize = new Size(70, 20),
                Text = item.Value,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            propertiesPanel.Controls.Add(label, 0, row);
            propertiesPanel.Controls.Add(value, 1, row);
        }

        propertiesPanel.Visible = values.Count > 0;
        propertiesPanel.ResumeLayout(true);
        LayoutOverlays();
    }

    internal void RefreshPreview()
    {
        ScheduleRefresh(3);
    }

    internal void CloseDocument()
    {
        if (disposed)
        {
            return;
        }

        repaintTimer.Stop();
        pendingRepaintAttempts = 0;
        markupControl = null;
        if (!documentOpen)
        {
            UpdateToolbarState();
            return;
        }

        documentTransitioning = true;
        documentOpen = false;
        WriteDiagnostic("close-start", currentDocumentName);
        try
        {
            viewer.CloseDocument();
            WriteDiagnostic("close-complete", currentDocumentName);
        }
        finally
        {
            currentDocumentName = string.Empty;
            documentTransitioning = false;
            UpdateToolbarState();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            CloseDocument();
            repaintTimer.Dispose();
            viewer.Dispose();
            disposed = true;
        }

        base.Dispose(disposing);
    }

    private void ConfigureToolbar()
    {
        toolbar.AutoSize = true;
        toolbar.BackColor = PreviewBackground;
        toolbar.CanOverflow = false;
        toolbar.Dock = Forms.DockStyle.None;
        toolbar.GripStyle = Forms.ToolStripGripStyle.Hidden;
        toolbar.ImageScalingSize = new Size(26, 26);
        toolbar.Padding = Forms.Padding.Empty;
        toolbar.Renderer = new TransparentToolStripRenderer(buttonTheme);
        toolbar.ShowItemToolTips = true;
        toolbar.TabStop = false;
    }

    private void ConfigurePropertiesPanel()
    {
        propertiesPanel.AutoSize = true;
        propertiesPanel.AutoSizeMode = Forms.AutoSizeMode.GrowAndShrink;
        propertiesPanel.BackColor = PreviewBackground;
        propertiesPanel.ColumnCount = 2;
        propertiesPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        propertiesPanel.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.AutoSize));
        propertiesPanel.Margin = Forms.Padding.Empty;
        propertiesPanel.MaximumSize = new Size(520, 0);
        propertiesPanel.Padding = new Forms.Padding(2, 2, 2, 0);
        propertiesPanel.Visible = false;
    }

    private Forms.ToolStripButton AddModeButton(string command, string toolTip)
    {
        var button = CreateButton(command, toolTip);
        button.CheckOnClick = true;
        button.Click += OnModeButtonClick;
        toolbar.Items.Add(button);
        modeButtons[command] = button;
        return button;
    }

    private Forms.ToolStripButton AddCommandButton(string command, string toolTip)
    {
        var button = CreateButton(command, toolTip);
        button.Click += (_, _) => ExecuteCommand(command);
        toolbar.Items.Add(button);
        return button;
    }

    private void AddToolbarSpacer()
    {
        toolbar.Items.Add(new Forms.ToolStripLabel
        {
            AutoSize = false,
            Enabled = false,
            Size = new Size(8, 40),
        });
    }

    private Forms.ToolStripButton CreateButton(string command, string toolTip)
    {
        return new Forms.ToolStripButton
        {
            AccessibleName = toolTip,
            AutoSize = false,
            DisplayStyle = Forms.ToolStripItemDisplayStyle.Image,
            Height = 40,
            Image = CreateCommandIcon(command, buttonTheme.IconColor),
            ImageScaling = Forms.ToolStripItemImageScaling.None,
            Margin = new Forms.Padding(3, 0, 3, 0),
            Tag = command,
            ToolTipText = toolTip,
            Width = 44,
        };
    }

    private static Bitmap CreateCommandIcon(string command, Color color)
    {
        var bitmap = new Bitmap(26, 26);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var pen = new Pen(color, 1.8F)
        {
            EndCap = LineCap.Round,
            StartCap = LineCap.Round,
        };
        using var brush = new SolidBrush(color);

        switch (command)
        {
            case "select":
                graphics.DrawPolygon(pen, new[]
                {
                    new PointF(6, 3), new PointF(19, 12), new PointF(13, 13),
                    new PointF(17, 21), new PointF(13, 23), new PointF(9, 15), new PointF(5, 19),
                });
                break;
            case "pan":
                graphics.DrawLine(pen, 13, 3, 13, 23);
                graphics.DrawLine(pen, 3, 13, 23, 13);
                DrawArrowHead(graphics, pen, new PointF(13, 3), new PointF(13, 8));
                DrawArrowHead(graphics, pen, new PointF(13, 23), new PointF(13, 18));
                DrawArrowHead(graphics, pen, new PointF(3, 13), new PointF(8, 13));
                DrawArrowHead(graphics, pen, new PointF(23, 13), new PointF(18, 13));
                break;
            case "rotate":
                graphics.DrawArc(pen, 4, 4, 18, 18, 45, 285);
                DrawArrowHead(graphics, pen, new PointF(20, 5), new PointF(16, 5));
                break;
            case "zoom":
                graphics.DrawEllipse(pen, 4, 4, 13, 13);
                graphics.DrawLine(pen, 15, 15, 22, 22);
                graphics.DrawLine(pen, 8, 10.5F, 13, 10.5F);
                graphics.DrawLine(pen, 10.5F, 8, 10.5F, 13);
                break;
            case "fit":
                graphics.DrawLines(pen, new[] { new Point(10, 4), new Point(4, 4), new Point(4, 10) });
                graphics.DrawLines(pen, new[] { new Point(16, 4), new Point(22, 4), new Point(22, 10) });
                graphics.DrawLines(pen, new[] { new Point(4, 16), new Point(4, 22), new Point(10, 22) });
                graphics.DrawLines(pen, new[] { new Point(22, 16), new Point(22, 22), new Point(16, 22) });
                break;
            case "front":
                DrawCube(graphics, pen, "front");
                break;
            case "top":
                DrawCube(graphics, pen, "top");
                break;
            case "right":
                DrawCube(graphics, pen, "right");
                break;
            case "isometric":
                DrawCube(graphics, pen, "isometric");
                break;
            case "measure":
                graphics.TranslateTransform(13, 13);
                graphics.RotateTransform(-38);
                graphics.DrawRectangle(pen, -10, -4, 20, 8);
                for (var x = -6; x <= 6; x += 4)
                {
                    graphics.DrawLine(pen, x, -4, x, -1);
                }
                graphics.ResetTransform();
                break;
            default:
                graphics.FillEllipse(brush, 9, 9, 8, 8);
                break;
        }

        return bitmap;
    }

    private static void DrawArrowHead(Graphics graphics, Pen pen, PointF tip, PointF toward)
    {
        var dx = toward.X - tip.X;
        var dy = toward.Y - tip.Y;
        var length = Math.Max(1F, (float)Math.Sqrt((dx * dx) + (dy * dy)));
        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        graphics.DrawLine(pen, tip, new PointF(tip.X + (ux * 4F) + (px * 3F), tip.Y + (uy * 4F) + (py * 3F)));
        graphics.DrawLine(pen, tip, new PointF(tip.X + (ux * 4F) - (px * 3F), tip.Y + (uy * 4F) - (py * 3F)));
    }

    private static void DrawCube(Graphics graphics, Pen pen, string highlightedFace)
    {
        var top = new[] { new PointF(13, 3), new PointF(22, 8), new PointF(13, 13), new PointF(4, 8) };
        var front = new[] { new PointF(4, 8), new PointF(13, 13), new PointF(13, 23), new PointF(4, 18) };
        var right = new[] { new PointF(13, 13), new PointF(22, 8), new PointF(22, 18), new PointF(13, 23) };
        using var faceBrush = new SolidBrush(Color.FromArgb(90, 59, 130, 246));
        using var isoBrush = new SolidBrush(Color.FromArgb(48, 59, 130, 246));

        if (highlightedFace == "isometric")
        {
            graphics.FillPolygon(isoBrush, top);
            graphics.FillPolygon(isoBrush, front);
            graphics.FillPolygon(isoBrush, right);
        }
        else if (highlightedFace == "top")
        {
            graphics.FillPolygon(faceBrush, top);
        }
        else if (highlightedFace == "front")
        {
            graphics.FillPolygon(faceBrush, front);
        }
        else if (highlightedFace == "right")
        {
            graphics.FillPolygon(faceBrush, right);
        }

        graphics.DrawPolygon(pen, top);
        graphics.DrawPolygon(pen, front);
        graphics.DrawPolygon(pen, right);
    }

    private void OnModeButtonClick(object? sender, EventArgs eventArgs)
    {
        if (sender is not Forms.ToolStripButton button || button.Tag is not string command)
        {
            return;
        }

        if (command == "measure")
        {
            if (!TryActivateMeasure(out var message))
            {
                button.Checked = false;
                UserMessageRequested?.Invoke(message);
                SelectMode("select");
                return;
            }
        }
        else
        {
            ExecuteCommand(command);
        }

        SelectMode(command);
    }

    private void SelectMode(string command)
    {
        foreach (var item in modeButtons)
        {
            item.Value.Checked = string.Equals(item.Key, command, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void LayoutOverlays()
    {
        if (disposed || ClientSize.Width <= 0)
        {
            return;
        }

        toolbar.PerformLayout();
        toolbar.Location = new Point(Math.Max(8, (ClientSize.Width - toolbar.Width) / 2), 10);
        var propertiesWidth = Math.Max(220, Math.Min(520, ClientSize.Width - 28));
        var valueWidth = Math.Max(120, propertiesWidth - 100);
        propertiesPanel.MaximumSize = new Size(propertiesWidth, 0);
        for (var row = 0; row < propertiesPanel.RowCount; row++)
        {
            if (propertiesPanel.GetControlFromPosition(1, row) is Forms.Label value)
            {
                value.MaximumSize = new Size(valueWidth, 0);
            }
        }
        propertiesPanel.PerformLayout();
        propertiesPanel.Location = new Point(14, 14);
        toolbar.BringToFront();
        propertiesPanel.BringToFront();
    }

    private void ScheduleRefresh(int attempts)
    {
        if (!CanUseDocument || !IsHandleCreated || !Visible)
        {
            return;
        }

        pendingRepaintAttempts = Math.Max(pendingRepaintAttempts, attempts);
        repaintTimer.Stop();
        repaintTimer.Start();
    }

    private void OnRepaintTimerTick(object? sender, EventArgs eventArgs)
    {
        repaintTimer.Stop();
        if (!CanUseDocument || !Visible)
        {
            pendingRepaintAttempts = 0;
            return;
        }

        RedrawNow();
        pendingRepaintAttempts--;
        if (pendingRepaintAttempts > 0)
        {
            repaintTimer.Start();
        }
    }

    private void RedrawNow()
    {
        if (!CanUseDocument)
        {
            return;
        }

        // OpenDoc performs its own asynchronous scene updates. Forcing UpdateScene/Refresh
        // while the native control is loading or closing can enter EModelView.dll reentrantly.
        viewer.Invalidate(true);
        LayoutOverlays();
    }

    private void UpdateToolbarState()
    {
        measureButton.Enabled = CanUseDocument;
        measureButton.ToolTipText = measureButton.Enabled ? "测量" : "请先打开图档";
    }

    private bool CanUseDocument => !disposed
        && documentOpen
        && !documentTransitioning
        && viewer.IsHandleCreated;

    private static void WriteDiagnostic(string action, string detail)
    {
        try
        {
            lock (DiagnosticLogSync)
            {
                var directory = Path.GetDirectoryName(DiagnosticLogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    DiagnosticLogPath,
                    $"{DateTimeOffset.Now:O} | thread={Environment.CurrentManagedThreadId} | {action} | {detail}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent document preview or client shutdown.
        }
    }

    private sealed class TransparentToolStripRenderer : Forms.ToolStripProfessionalRenderer
    {
        private readonly PreviewButtonTheme theme;

        internal TransparentToolStripRenderer(PreviewButtonTheme theme)
        {
            this.theme = theme;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs eventArgs)
        {
            // Keep the preview visible behind the toolbar; only individual hot/checked icons are highlighted.
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs eventArgs)
        {
        }

        protected override void OnRenderButtonBackground(Forms.ToolStripItemRenderEventArgs eventArgs)
        {
            if (eventArgs.Item is not Forms.ToolStripButton button)
            {
                return;
            }

            var bounds = new Rectangle(1, 1, Math.Max(1, eventArgs.Item.Width - 3), Math.Max(1, eventArgs.Item.Height - 3));
            using var path = CreateRoundedRectangle(bounds, 5);
            var backColor = button.Checked || button.Pressed
                ? theme.ActiveBackground
                : button.Selected
                    ? theme.HoverBackground
                    : Color.White;
            var borderColor = button.Checked || button.Pressed
                ? theme.ActiveBorder
                : theme.Border;
            using var shadowBrush = new SolidBrush(Color.FromArgb(35, 15, 23, 42));
            using var backBrush = new SolidBrush(backColor);
            using var borderPen = new Pen(borderColor);

            var shadowBounds = bounds;
            shadowBounds.Offset(0, 1);
            using var shadowPath = CreateRoundedRectangle(shadowBounds, 5);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            eventArgs.Graphics.FillPath(shadowBrush, shadowPath);
            eventArgs.Graphics.FillPath(backBrush, path);
            eventArgs.Graphics.DrawPath(borderPen, path);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs eventArgs)
        {
        }

        private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class PreviewButtonTheme
    {
        private PreviewButtonTheme(Color hoverBackground, Color activeBackground, Color border, Color activeBorder)
        {
            HoverBackground = hoverBackground;
            ActiveBackground = activeBackground;
            Border = border;
            ActiveBorder = activeBorder;
            IconColor = activeBorder;
        }

        internal Color HoverBackground { get; }
        internal Color ActiveBackground { get; }
        internal Color Border { get; }
        internal Color ActiveBorder { get; }
        internal Color IconColor { get; }

        internal static PreviewButtonTheme Resolve(string theme)
        {
            if (string.Equals(theme, "c", StringComparison.OrdinalIgnoreCase))
            {
                return new PreviewButtonTheme(
                    Color.FromArgb(233, 248, 246),
                    Color.FromArgb(208, 239, 236),
                    Color.FromArgb(157, 219, 213),
                    Color.FromArgb(19, 157, 147));
            }

            if (string.Equals(theme, "o", StringComparison.OrdinalIgnoreCase))
            {
                return new PreviewButtonTheme(
                    Color.FromArgb(255, 243, 231),
                    Color.FromArgb(255, 227, 196),
                    Color.FromArgb(246, 199, 143),
                    Color.FromArgb(230, 120, 23));
            }

            return new PreviewButtonTheme(
                Color.FromArgb(237, 245, 255),
                Color.FromArgb(217, 236, 255),
                Color.FromArgb(156, 199, 255),
                Color.FromArgb(64, 158, 255));
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(EDrawingsPreviewControl));
        }
    }

    private sealed class EDrawingsAxHost : Forms.AxHost
    {
        internal EDrawingsAxHost() : base("{C59EEF21-0223-4C39-A708-A3BE9008C67E}")
        {
        }

        internal dynamic ActiveControl
        {
            get
            {
                CreateControl();
                return GetOcx();
            }
        }

        internal void OpenDocument(string path)
        {
            dynamic control = ActiveControl;
            ShowCompleteUi(control);
            control.OpenDoc(path, false, false, true, string.Empty);
            ShowCompleteUi(control);
        }

        private static void ShowCompleteUi(dynamic control)
        {
            // eDrawings defines FullUI as an integer: -1 is complete UI and 0 is simple UI.
            control.FullUI = -1;
            control.ShowToolbar(true);
            try
            {
                control.BackgroundColor = ColorTranslator.ToOle(PreviewBackground);
                control.BackgroundColorGradient = false;
                control.BackgroundColorOverride = true;
            }
            catch
            {
                // Older controls may not expose background overrides; the host still uses the same fallback color.
            }
        }

        internal void CloseDocument()
        {
            try
            {
                if (IsHandleCreated)
                {
                    dynamic control = GetOcx();
                    control.CloseActiveDoc(string.Empty);
                }
            }
            catch
            {
                // The COM control can already be closing with its parent client window.
            }
        }
    }
}
