using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Upton.Pdm.Desktop;

internal sealed class LightweightPreviewProvider
{
    private const int PreviewWidth = 960;
    private const int PreviewHeight = 540;
    private readonly int capacity;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> recency = new();
    private readonly object syncRoot = new();

    public LightweightPreviewProvider(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public string? TryCreateDataUrl(string filePath) => TryCreateDataUrl(filePath, PreviewWidth, PreviewHeight);

    public string? TryCreateDataUrl(string filePath, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var info = new FileInfo(filePath);
        var key = string.Concat(info.FullName, "|", info.Length, "|", info.LastWriteTimeUtc.Ticks, "|", width, "x", height);
        lock (syncRoot)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                recency.Remove(cached);
                recency.AddFirst(cached);
                return cached.Value.DataUrl;
            }
        }

        var dataUrl = ReadShellThumbnail(filePath, width, height);
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        lock (syncRoot)
        {
            if (entries.TryGetValue(key, out var existing)) recency.Remove(existing);
            var node = recency.AddFirst(new CacheEntry(key, dataUrl!));
            entries[key] = node;
            while (recency.Count > capacity)
            {
                var oldest = recency.Last;
                if (oldest == null) break;
                recency.RemoveLast();
                entries.Remove(oldest.Value.Key);
            }
        }
        return dataUrl;
    }

    private static string? ReadShellThumbnail(string filePath, int width, int height)
    {
        IShellItemImageFactory? imageFactory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref interfaceId, out imageFactory);
            if (result != 0 || imageFactory == null) return null;
            result = imageFactory.GetImage(
                new NativeSize { Width = width, Height = height },
                ShellItemImageFlags.ThumbnailOnly | ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero) return null;

            using var source = Image.FromHbitmap(bitmapHandle);
            using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.FromArgb(237, 243, 248));
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                var scale = Math.Min((double)width / source.Width, (double)height / source.Height);
                var scaledWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
                var scaledHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, (width - scaledWidth) / 2, (height - scaledHeight) / 2, scaledWidth, scaledHeight);
            }

            using var output = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 82L);
            canvas.Save(output, encoder, parameters);
            return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (imageFactory != null && Marshal.IsComObject(imageFactory)) Marshal.FinalReleaseComObject(imageFactory);
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string key, string dataUrl)
        {
            Key = key;
            DataUrl = dataUrl;
        }

        public string Key { get; }
        public string DataUrl { get; }
    }

    [Flags]
    private enum ShellItemImageFlags
    {
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
}
