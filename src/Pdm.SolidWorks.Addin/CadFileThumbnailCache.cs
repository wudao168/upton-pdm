using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal static class CadFileThumbnailCache
{
    [Flags]
    private enum ShellItemImageFlags
    {
        ResizeToFit = 0x00000000,
        BiggerSizeOk = 0x00000001,
        ThumbnailOnly = 0x00000008
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory imageFactory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    internal static Image Load(string filePath, int size)
    {
        if (string.IsNullOrWhiteSpace(filePath) || size <= 0 || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var source = new FileInfo(filePath);
            var cachePath = CachePath(filePath, size);
            if (File.Exists(cachePath)
                && new FileInfo(cachePath).Length > 0
                && File.GetLastWriteTimeUtc(cachePath) >= source.LastWriteTimeUtc)
            {
                var cached = LoadImageCopy(cachePath);
                if (cached != null)
                {
                    return cached;
                }
            }

            var thumbnail = ReadShellThumbnail(filePath, size);
            if (thumbnail == null)
            {
                return null;
            }

            SaveCacheCopy(cachePath, thumbnail);
            return thumbnail;
        }
        catch
        {
            return null;
        }
    }

    private static Image ReadShellThumbnail(string filePath, int size)
    {
        IShellItemImageFactory imageFactory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref interfaceId, out imageFactory);
            if (result != 0 || imageFactory == null)
            {
                return null;
            }

            result = imageFactory.GetImage(
                new NativeSize { Width = size, Height = size },
                ShellItemImageFlags.ThumbnailOnly | ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            using (var bitmap = Image.FromHbitmap(bitmapHandle))
            {
                return new Bitmap(bitmap);
            }
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }
            if (imageFactory != null && Marshal.IsComObject(imageFactory))
            {
                Marshal.FinalReleaseComObject(imageFactory);
            }
        }
    }

    private static Image LoadImageCopy(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCacheCopy(string cachePath, Image image)
    {
        var temporaryPath = string.Concat(cachePath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            image.Save(temporaryPath, ImageFormat.Png);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
            File.Move(temporaryPath, cachePath);
        }
        catch
        {
            TryDelete(temporaryPath);
        }
    }

    private static string CachePath(string filePath, int size)
    {
        var normalizedPath = Path.GetFullPath(filePath).Trim().ToUpperInvariant();
        byte[] hash;
        using (var sha256 = SHA256.Create())
        {
            hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
        }
        var fileName = string.Concat(
            BitConverter.ToString(hash).Replace("-", string.Empty),
            "-",
            size,
            ".png");
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UPTON PDM",
            "ThumbnailCache",
            fileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
