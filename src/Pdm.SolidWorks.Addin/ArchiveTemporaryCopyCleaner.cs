using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchiveTemporaryCopyCleanupResult
{
    public int DeletedFiles { get; set; }
    public long FreedBytes { get; set; }
    public int SkippedItems { get; set; }
    public string Message => (DeletedFiles == 0 ? "没有清理到可删除的存档临时副本。" : "已清理" + DeletedFiles + "个临时文件，释放"
        + (FreedBytes / 1048576d).ToString("0.00", CultureInfo.InvariantCulture) + " MB。")
        + (SkippedItems == 0 ? string.Empty : "另有" + SkippedItems + "项被占用、只读或无法确认归属，已跳过。");
}

internal static class ArchiveTemporaryCopyCleaner
{
    private static string directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "checkin");
    internal static string DirectoryPath => Path.GetFullPath(directory);

    internal static ArchiveTemporaryCopyCleanupResult Clean(Func<bool> isBusy)
    {
        void RequireIdle()
        {
            if (isBusy == null || isBusy())
                throw new InvalidOperationException("当前有图档操作或其他SolidWorks实例运行，暂不清理。请待操作结束并退出其他实例后重试。");
        }
        RequireIdle();
        var result = new ArchiveTemporaryCopyCleanupResult();
        var root = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root)) return result;
        void CheckPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("临时文件路径超出存档副本目录，已停止清理。");
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("临时副本路径包含目录链接，已跳过，未沿链接清理其他位置。");
        }
        CheckPath(root);
        // Snapshot owned directories before the second activity check. Copies created by
        // a later operation are not in this list; never recurse into an unknown directory.
        var candidates = new List<string>();
        foreach (var document in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(document), "N", out _)) { result.SkippedItems++; continue; }
            try
            {
                CheckPath(document);
                foreach (var copy in Directory.GetDirectories(document))
                {
                    if (Guid.TryParseExact(Path.GetFileName(copy), "N", out _)) candidates.Add(copy);
                    else result.SkippedItems++;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
            { result.SkippedItems++; PluginDiagnosticLog.Record("清理存档临时副本", error.ToString()); }
        }
        RequireIdle();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (isBusy()) { result.SkippedItems += candidates.Count - index; break; }
            var copy = candidates[index];
            try
            {
                CheckPath(copy);
                var files = Directory.GetFiles(copy);
                if (Directory.GetDirectories(copy).Length > 0) { result.SkippedItems++; continue; }
                foreach (var file in files) CheckPath(file);
                foreach (var file in files)
                {
                    CheckPath(file);
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    result.DeletedFiles++;
                    result.FreedBytes += length;
                }
                CheckPath(copy);
                Directory.Delete(copy, false);
                var parent = Path.GetDirectoryName(copy);
                if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                { CheckPath(parent); Directory.Delete(parent, false); }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
            { result.SkippedItems++; PluginDiagnosticLog.Record("清理存档临时副本", error.ToString()); }
        }
        return result;
    }
}
