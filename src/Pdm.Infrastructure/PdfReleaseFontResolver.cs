using System.Buffers.Binary;
using PdfSharp.Fonts;

namespace Upton.Pdm.Infrastructure;

internal sealed class PdfReleaseFontResolver : IFontResolver
{
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "UPLM-YaHei-Bold" : "UPLM-YaHei");

    public byte[] GetFont(string faceName)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var file = Path.Combine(fonts, faceName.EndsWith("Bold", StringComparison.Ordinal) ? "msyhbd.ttc" : "msyh.ttc");
        if (!File.Exists(file)) throw new InvalidOperationException("服务器缺少微软雅黑字体，请安装中文字体后重试PDF标识。");
        return FirstFace(File.ReadAllBytes(file));
    }

    // PDFsharp reads individual TrueType faces; Windows YaHei is a shared-table TTC.
    private static byte[] FirstFace(byte[] collection)
    {
        uint Read32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(offset, 4));
        if (Read32(0) != 0x74746366) return collection;
        var start = checked((int)Read32(12));
        var count = BinaryPrimitives.ReadUInt16BigEndian(collection.AsSpan(start + 4, 2));
        var directoryLength = 12 + count * 16;
        var length = (directoryLength + 3) & ~3;
        for (var i = 0; i < count; i++) length += (checked((int)Read32(start + 12 + i * 16 + 12)) + 3) & ~3;
        var result = new byte[length];
        collection.AsSpan(start, directoryLength).CopyTo(result);
        var destination = (directoryLength + 3) & ~3;
        var head = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + i * 16;
            var source = checked((int)Read32(start + entry + 8));
            var size = checked((int)Read32(start + entry + 12));
            collection.AsSpan(source, size).CopyTo(result.AsSpan(destination));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(entry + 8, 4), (uint)destination);
            if (Read32(start + entry) == 0x68656164) head = destination;
            destination += (size + 3) & ~3;
        }
        if (head >= 0)
        {
            result.AsSpan(head + 8, 4).Clear();
            uint sum = 0;
            for (var i = 0; i < result.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(i, 4)));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(head + 8, 4), unchecked(0xB1B0AFBA - sum));
        }
        return result;
    }
}
