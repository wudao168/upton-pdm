using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal static class TopMostMessageBox
{
    public static DialogResult Show(IWin32Window owner, string message, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        var session = ArchiveSession.Current;
        if (session != null && !session.IsClosed) return session.Confirm(message, caption, buttons, defaultButton);
        var handle = owner?.Handle ?? IntPtr.Zero;
        var root = handle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(handle, 2);
        // Owned prompts stay above SolidWorks without entering the system-wide topmost band.
        return (DialogResult)MessageBox(root == IntPtr.Zero ? handle : root, message, caption,
            (uint)buttons | (uint)icon | (uint)defaultButton);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
