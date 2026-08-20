using System;
using Forms = System.Windows.Forms;

namespace Upton.Pdm.Desktop;

internal sealed class EDrawingsPreviewControl : Forms.UserControl
{
    private readonly EDrawingsAxHost viewer = new();
    private bool disposed;

    internal EDrawingsPreviewControl()
    {
        viewer.Dock = Forms.DockStyle.Fill;
        viewer.BeginInit();
        Controls.Add(viewer);
        viewer.EndInit();
        Dock = Forms.DockStyle.Fill;
    }

    internal void OpenDocument(string path)
    {
        ThrowIfDisposed();
        CloseDocument();
        viewer.OpenDocument(path);
    }

    internal void FitDocument()
    {
        if (disposed || !viewer.IsHandleCreated)
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
            // Older eDrawings controls may not expose ZoomToFit; their own toolbar remains available.
        }
    }

    internal void ExecuteCommand(string command)
    {
        if (disposed || !viewer.IsHandleCreated)
        {
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
    }

    internal void CloseDocument()
    {
        if (disposed)
        {
            return;
        }

        viewer.CloseDocument();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            CloseDocument();
            viewer.Dispose();
            disposed = true;
        }

        base.Dispose(disposing);
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
