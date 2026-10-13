using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchiveUploadState
{
    public Guid SessionId { get; set; }
    public string RelativePath { get; set; }
    public long AcknowledgedLength { get; set; }
}

internal static class ArchiveUploadJournal
{
    private static string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM", "archive-transfers");

    public static string PathFor(Guid projectId, Guid documentId, string sourcePath, string sha256, long length)
    {
        var key = projectId + "|" + documentId + "|" + Path.GetFullPath(sourcePath).ToUpperInvariant() + "|" + sha256 + "|" + length;
        using (var hash = SHA256.Create())
            return Path.Combine(directory, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".json");
    }

    public static ArchiveUploadState Read(string path)
    {
        try { return File.Exists(path) ? new JavaScriptSerializer().Deserialize<ArchiveUploadState>(File.ReadAllText(path, Encoding.UTF8)) : null; }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
        { return null; }
    }

    public static void Save(string path, ArchiveUploadState state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(state), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        { throw new InvalidOperationException("无法保存本机续传记录，已保留图档。请检查本机磁盘空间及UPLM缓存目录权限后重试。", exception); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
