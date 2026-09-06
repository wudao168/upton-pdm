using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal static class DialogWindowSizing
{
    private const uint GetAncestorRoot = 2;

    public static void FitToOwner(Form dialog, IntPtr ownerWindowHandle, double fillRatio, int margin)
    {
        if (dialog == null)
        {
            throw new ArgumentNullException(nameof(dialog));
        }

        var rootWindowHandle = ownerWindowHandle == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(ownerWindowHandle, GetAncestorRoot);
        if (rootWindowHandle == IntPtr.Zero)
        {
            rootWindowHandle = ownerWindowHandle;
        }

        var workingArea = rootWindowHandle == IntPtr.Zero
            ? Screen.FromControl(dialog).WorkingArea
            : Screen.FromHandle(rootWindowHandle).WorkingArea;
        var ownerBounds = TryGetClientBounds(rootWindowHandle, out var clientBounds)
            ? clientBounds
            : workingArea;
        var targetBounds = CalculateBounds(ownerBounds, workingArea, dialog.MinimumSize, fillRatio, margin);

        dialog.MinimumSize = new Size(
            Math.Min(dialog.MinimumSize.Width, targetBounds.Width),
            Math.Min(dialog.MinimumSize.Height, targetBounds.Height));
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Bounds = targetBounds;
    }

    internal static Rectangle CalculateBounds(
        Rectangle ownerBounds,
        Rectangle workingArea,
        Size minimumSize,
        double fillRatio,
        int margin)
    {
        var available = Rectangle.Intersect(ownerBounds, workingArea);
        if (available.Width <= 0 || available.Height <= 0)
        {
            available = workingArea;
        }

        var ratio = Math.Max(0.1, Math.Min(1, fillRatio));
        var safeMargin = Math.Max(0, margin);
        var maximumWidth = Math.Max(1, available.Width - Math.Min(available.Width - 1, safeMargin * 2));
        var maximumHeight = Math.Max(1, available.Height - Math.Min(available.Height - 1, safeMargin * 2));
        var width = Math.Min(
            maximumWidth,
            Math.Max(Math.Min(minimumSize.Width, maximumWidth), (int)Math.Round(available.Width * ratio)));
        var height = Math.Min(
            maximumHeight,
            Math.Max(Math.Min(minimumSize.Height, maximumHeight), (int)Math.Round(available.Height * ratio)));
        var left = Math.Max(workingArea.Left, Math.Min(
            available.Left + (available.Width - width) / 2,
            workingArea.Right - width));
        var top = Math.Max(workingArea.Top, Math.Min(
            available.Top + (available.Height - height) / 2,
            workingArea.Bottom - height));

        return new Rectangle(left, top, width, height);
    }

    private static bool TryGetClientBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (windowHandle == IntPtr.Zero || !GetClientRect(windowHandle, out var clientRectangle))
        {
            return false;
        }

        var topLeft = new NativePoint { X = clientRectangle.Left, Y = clientRectangle.Top };
        var bottomRight = new NativePoint { X = clientRectangle.Right, Y = clientRectangle.Bottom };
        if (!ClientToScreen(windowHandle, ref topLeft) || !ClientToScreen(windowHandle, ref bottomRight))
        {
            return false;
        }

        bounds = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr windowHandle, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
