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
            control.FullUI = true;
            control.ShowToolbar(true);
            control.OpenDoc(path, false, false, true, string.Empty);
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
