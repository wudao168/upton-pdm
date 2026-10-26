using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveEmbeddedPackageInspection
{
    internal static bool IsSelfContained(ISwOLEObject item)
    {
        if (IsSolidWorksExportNote(item)) return true;
        if (!item.IsLinked && string.Equals(item.Clsid, "{00000316-0000-0000-C000-000000000046}", StringComparison.OrdinalIgnoreCase))
        {
            // CLSID_StaticDib is an OLE_CANTLINKINSIDE static representation.
            // Validate the complete stored bitmap, without activating any server.
            var bitmap = ReadStoredStream(item, "CONTENTS", 64 * 1024 * 1024);
            return bitmap != null && IsBitmapData(bitmap);
        }
        var package = ReadWorkbook(item);
        return package != null && IsWorkbookData(package);
    }

    private static bool IsBitmapData(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M' || BitConverter.ToUInt32(data, 2) != data.Length
            || BitConverter.ToUInt32(data, 6) != 0 || BitConverter.ToUInt32(data, 14) != 40) return false;
        var width = BitConverter.ToInt32(data, 18); var height = BitConverter.ToInt32(data, 22);
        var bits = BitConverter.ToUInt16(data, 28); var compression = BitConverter.ToUInt32(data, 30);
        if (width <= 0 || width > 32768 || height == 0 || height < -32768 || height > 32768 || BitConverter.ToUInt16(data, 26) != 1
            || !new[] { 1, 4, 8, 16, 24, 32 }.Contains((int)bits) || compression != 0 && compression != 3
            || compression == 3 && bits != 16 && bits != 32) return false;
        var colors = BitConverter.ToUInt32(data, 46);
        if (colors == 0 && bits <= 8) colors = (uint)(1 << bits);
        if (colors > 256) return false;
        var offset = 54L + colors * 4L + (compression == 3 ? 12 : 0);
        var pixels = ((width * (long)bits + 31) / 32) * 4 * Math.Abs((long)height);
        var imageSize = BitConverter.ToUInt32(data, 34);
        return BitConverter.ToUInt32(data, 10) == offset && offset + pixels == data.Length && (imageSize == 0 || imageSize == pixels);
    }

    internal static byte[] ReadWorkbook(ISwOLEObject item)
        => !item.IsLinked && string.Equals(item.Clsid, "{00020830-0000-0000-C000-000000000046}", StringComparison.OrdinalIgnoreCase)
            ? ReadStoredStream(item, "Package", 32 * 1024 * 1024) : null;

    internal static byte[] RemoveWorkbookLinks(byte[] data)
    {
        using (var input = new MemoryStream(data))
        using (var source = new ZipArchive(input, ZipArchiveMode.Read))
        using (var output = new MemoryStream())
        {
            if (source.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024) throw new IOException("内嵌设计表内容过大，未开始改写图档。");
            using (var result = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var entry in source.Entries)
                {
                    if (entry.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)) continue;
                    XDocument xml = null;
                    if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = entry.Open())
                        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) xml = XDocument.Load(reader);
                        var elements = xml.Descendants().ToArray();
                        if (elements.Any(element => element.Name.LocalName == "definedName" && Regex.IsMatch(element.Value, @"\[[0-9]+\]")))
                            throw new IOException("内嵌设计表含有无法从已保存结果固定的外部名称，未改变图档。");
                        var externalShared = elements.Where(element => element.Name.LocalName == "f" && Regex.IsMatch(element.Value, @"\[[0-9]+\]"))
                            .Where(element => (string)element.Attribute("t") == "shared").Select(element => (string)element.Attribute("si")).ToArray();
                        foreach (var formula in elements.Where(element => element.Name.LocalName == "f"
                            && (Regex.IsMatch(element.Value, @"\[[0-9]+\]") || (string)element.Attribute("t") == "shared" && externalShared.Contains((string)element.Attribute("si")))))
                        {
                            var cell = formula.Parent;
                            var value = cell?.Elements().SingleOrDefault(element => element.Name.LocalName == "v");
                            if (value == null || string.IsNullOrEmpty(value.Value) && (string)cell.Attribute("t") != "str" || (string)cell.Attribute("t") == "e")
                                throw new IOException("内嵌设计表的外部公式缺少有效的已保存结果，未改变图档：" + (string)cell?.Attribute("r"));
                            formula.Remove(); // Same semantics as Excel Break Link: retain its saved value.
                        }
                        foreach (var element in elements.Where(element => element.Name.LocalName == "externalReferences" || element.Name.LocalName == "hyperlinks"
                            || element.Name.LocalName == "Override" && ((string)element.Attribute("PartName") ?? "").StartsWith("/xl/externalLinks/", StringComparison.OrdinalIgnoreCase)
                            || element.Name.LocalName == "Relationship" && new[] { "externalLink", "hyperlink" }.Contains(((string)element.Attribute("Type") ?? "").Split('/').Last(), StringComparer.Ordinal))) element.Remove();
                    }
                    using (var target = result.CreateEntry(entry.FullName).Open())
                        if (xml != null) xml.Save(target); else using (var original = entry.Open()) original.CopyTo(target);
                }
            var clean = output.ToArray();
            if (!IsWorkbookData(clean)) throw new IOException("内嵌设计表仍有尚未安全处理的外部内容，未改变图档。");
            return clean;
        }
    }

    internal static void RequireUnavailableLocalWorkbookSources(byte[] data)
    {
        using (var input = new MemoryStream(data))
        using (var package = new ZipArchive(input, ZipArchiveMode.Read))
            foreach (var entry in package.Entries.Where(entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    foreach (var link in XDocument.Load(reader).Descendants().Where(element => element.Name.LocalName == "Relationship"))
                    {
                        var target = (string)link.Attribute("Target");
                        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || File.Exists(uri.LocalPath))
                            throw new IOException("内嵌设计表的来源仍可用或属于网络来源，不能在未经完整核对的情况下更新或固定其结果，已停止存档。");
                    }
    }

    private static bool IsInertExtension(string path, XElement list)
    {
        foreach (var extension in list.Elements())
        {
            if (extension.Name.LocalName != "ext" || extension.Elements().Count() != 1
                || extension.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name != "uri")) return false;
            var child = extension.Elements().Single(); var uri = (string)extension.Attribute("uri");
            var expected = path == "xl/styles.xml" && string.Equals(uri, "{EB79DEF2-80B8-43e5-95BD-54CBDDF9020C}", StringComparison.OrdinalIgnoreCase)
                ? new[] { "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main", "slicerStyles", "defaultSlicerStyle" }
                : path == "xl/styles.xml" && string.Equals(uri, "{9260A510-F301-46a8-8635-F512D64BE5F5}", StringComparison.OrdinalIgnoreCase)
                ? new[] { "http://schemas.microsoft.com/office/spreadsheetml/2010/11/main", "timelineStyles", "defaultTimelineStyle" }
                : path == "xl/workbook.xml" && string.Equals(uri, "{140A7094-0E35-4892-8432-C4D2E57EDEB5}", StringComparison.OrdinalIgnoreCase)
                ? new[] { "http://schemas.microsoft.com/office/spreadsheetml/2010/11/main", "workbookPr", "chartTrackingRefBase" }
                : path.StartsWith("xl/theme/", StringComparison.Ordinal) && string.Equals(uri, "{05A4C25C-085E-4340-85A3-A5531E510DB2}", StringComparison.OrdinalIgnoreCase)
                ? new[] { "http://schemas.microsoft.com/office/thememl/2012/main", "themeFamily", "name", "id", "vid" } : null;
            if (expected == null || child.Name != XName.Get(expected[1], expected[0]) || child.HasElements
                || child.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && !expected.Skip(2).Contains(attribute.Name.ToString()))) return false;
        }
        return true;
    }

    // Inspect the stored bytes only. Never activate an OLE server or consult the
    // historical source paths in a package. Unknown embedded content stays blocked.
    internal static bool IsSolidWorksExportNote(ISwOLEObject item)
    {
        if (item.IsLinked || !string.Equals(item.Clsid, "{0003000C-0000-0000-C000-000000000046}", StringComparison.OrdinalIgnoreCase)) return false;
        var data = ReadStoredStream(item, "\u0001Ole10Native", 65536);
        return data != null && IsExportNoteData(data);
    }

    private static byte[] ReadStoredStream(ISwOLEObject item, string name, int maximum)
    {
        var bytes = (item.Buffer as Array)?.Cast<object>().Select(value => unchecked((byte)Convert.ToInt32(value))).ToArray();
        if (bytes == null || bytes.Length != item.BufferSize || bytes.Length > maximum + 1024 * 1024) return null;
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "ole-inspection");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
        IStorage storage = null; IStream stream = null;
        try
        {
            File.WriteAllBytes(path, bytes);
            if (StgOpenStorage(path, IntPtr.Zero, 0x10, IntPtr.Zero, 0, out storage) != 0) return null;
            storage.OpenStream(name, IntPtr.Zero, 0x10, 0, out stream);
            stream.Stat(out var stat, 1);
            if (stat.cbSize < 16 || stat.cbSize > maximum) return null;
            var data = new byte[(int)stat.cbSize]; var read = Marshal.AllocHGlobal(4);
            try
            {
                stream.Read(data, data.Length, read);
                if (Marshal.ReadInt32(read) != data.Length) return null;
            }
            finally { Marshal.FreeHGlobal(read); }
            return data;
        }
        catch (IOException) { return null; }
        catch (COMException) { return null; }
        finally
        {
            if (stream != null) Marshal.ReleaseComObject(stream);
            if (storage != null) Marshal.ReleaseComObject(storage);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException error) { PluginDiagnosticLog.Record("嵌入说明核对", error.ToString()); }
        }
    }

    private static bool IsWorkbookData(byte[] data)
    {
        // Embedded design tables remain editable. Inspect their actual OOXML bytes
        // without starting Excel, refreshing data or rewriting any configuration.
        try
        {
            using (var input = new MemoryStream(data))
            using (var package = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var names = package.Entries.Select(entry => entry.FullName).ToArray();
                if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
                    || !names.Contains("xl/workbook.xml") || !names.Contains("[Content_Types].xml")
                    || package.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024) return false;
                foreach (var entry in package.Entries)
                {
                    if (!Regex.IsMatch(entry.FullName, @"^(\[Content_Types\]\.xml|_rels/\.rels|docProps/(core|app)\.xml|xl/(workbook|styles|sharedStrings)\.xml|xl/_rels/workbook\.xml\.rels|xl/worksheets/(sheet[0-9]+\.xml|_rels/sheet[0-9]+\.xml\.rels)|xl/theme/theme[0-9]+\.xml|xl/printerSettings/printerSettings[0-9]+\.bin)$", RegexOptions.CultureInvariant)) return false;
                    if (entry.FullName.EndsWith(".bin", StringComparison.Ordinal)) continue;
                    using (var stream = entry.Open())
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    {
                        var xml = XDocument.Load(reader);
                        foreach (var element in xml.Descendants())
                        {
                            var name = element.Name.LocalName;
                            if (name == "extLst" && !IsInertExtension(entry.FullName, element)) return false;
                            if (name == "externalReferences" || name == "oleObjects" || name == "hyperlinks") return false;
                            if (name == "Relationship")
                            {
                                var target = (string)element.Attribute("Target") ?? "";
                                var type = (string)element.Attribute("Type") ?? "";
                                if ((string)element.Attribute("TargetMode") == "External" || target.Contains(":") || target.StartsWith("/") || target.Contains("\\")
                                    || !new[] { "officeDocument", "core-properties", "extended-properties", "worksheet", "styles", "theme", "sharedStrings", "printerSettings" }
                                        .Contains(type.Substring(type.LastIndexOf('/') + 1), StringComparer.Ordinal)) return false;
                                var owner = entry.FullName == "_rels/.rels" ? "" : entry.FullName.Replace("/_rels/", "/").Substring(0, entry.FullName.Replace("/_rels/", "/").Length - 5);
                                var destination = new Uri(new Uri("https://pdm.invalid/" + owner), target);
                                if (destination.Host != "pdm.invalid" || !names.Contains(Uri.UnescapeDataString(destination.AbsolutePath.TrimStart('/')))) return false;
                            }
                            if (name != "f" && name != "definedName") continue;
                            var formula = element.Value;
                            if (formula.IndexOfAny(new[] { '[', ']', '\\', '|' }) >= 0 || formula.Contains("://")) return false;
                            foreach (Match function in Regex.Matches(formula, @"([A-Za-z_][A-Za-z0-9_.]*)\s*\("))
                                if (!new[] { "MID", "FIND", "RIGHT", "LEFT", "LEN", "IF", "IFERROR", "AND", "OR", "NOT", "SUM", "MIN", "MAX", "ABS", "ROUND", "ROUNDUP", "ROUNDDOWN", "INT", "MOD", "VALUE", "TEXT", "CONCATENATE", "SUBSTITUTE", "TRIM", "UPPER", "LOWER", "VLOOKUP", "HLOOKUP", "INDEX", "MATCH", "COUNT", "COUNTA", "AVERAGE" }
                                    .Contains(function.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) return false;
                        }
                    }
                }
                return true;
            }
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (XmlException) { return false; }
        catch (UriFormatException) { return false; }
    }

    private static bool IsExportNoteData(byte[] data)
    {
        try
        {
            using (var input = new MemoryStream(data))
            using (var reader = new BinaryReader(input))
            {
                if (reader.ReadUInt32() != data.Length - 4 || reader.ReadUInt16() != 2) return false;
                string ReadTerminated()
                {
                    var start = input.Position;
                    while (input.Position - start <= 4096)
                        if (reader.ReadByte() == 0)
                            return Encoding.ASCII.GetString(data, (int)start, (int)(input.Position - start - 1));
                    throw new IOException("嵌入说明字段过长。");
                }
                if (!string.Equals(ReadTerminated(), "Save as Part.txt", StringComparison.OrdinalIgnoreCase)) return false;
                ReadTerminated(); // Historical source name; not a live link.
                reader.ReadUInt32();
                var temporaryLength = reader.ReadUInt32(); var start = input.Position;
                ReadTerminated();
                if (input.Position - start != temporaryLength) return false;
                var length = reader.ReadUInt32();
                if (length < 11 || length > 32 || length > input.Length - input.Position) return false;
                if (!Regex.IsMatch(Encoding.ASCII.GetString(reader.ReadBytes((int)length)), "^SOLIDWORKS [0-9]{0,4}$", RegexOptions.CultureInvariant)) return false;
                // Some package versions append the three names as UTF-16 fields.
                if (input.Position != input.Length)
                    for (var index = 0; index < 3; index++)
                    {
                        var characters = reader.ReadUInt32();
                        if (characters > 4096 || characters * 2 > input.Length - input.Position) return false;
                        reader.ReadBytes((int)characters * 2);
                    }
                return input.Position == input.Length;
            }
        }
        catch (IOException) { return false; }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorage(string name, IntPtr priority, uint mode, IntPtr exclude, uint reserved, out IStorage storage);

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IStream stream);
    }
}
