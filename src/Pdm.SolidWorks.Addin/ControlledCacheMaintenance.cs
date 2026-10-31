using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class ControlledCacheUsage
{
    public long UsedBytes { get; set; }
    public long ReclaimableBytes { get; set; }
    public long FreedBytes { get; set; }
    public int SkippedItems { get; set; }
    public string Message => "缓存占用 " + (UsedBytes / 1073741824d).ToString("0.00") + " GB，可清理约 "
        + (ReclaimableBytes / 1073741824d).ToString("0.00") + " GB。"
        + (FreedBytes == 0 ? "" : "已释放 " + (FreedBytes / 1048576d).ToString("0.00") + " MB。")
        + (SkippedItems == 0 ? "" : "有 " + SkippedItems + " 项正在使用、含修改或归属未确认，已保留。");
}

internal sealed class ControlledCacheEntry
{
    public string Directory { get; set; }
    public string Kind { get; set; }
    public DateTime LastUsedUtc { get; set; }
    public ControlledOpenManifestDto Manifest { get; set; }
}

// Only verified, indexed managed contexts can be removed. Recovery and original
// source directories never enter this index. A read-only flag alone is not proof.
internal static class ControlledCacheMaintenance
{
    internal static void RequireOrdinaryPath(string path, string root) => Ordinary(path, root);
    private static readonly object Sync = new object();
    private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    private static string IndexRoot(string root) => Path.Combine(root, ".uplm", "CacheIndex");
    private static string Key(string directory)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
            Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
    }
    internal static void Record(string root, string directory, ControlledOpenManifestDto manifest, string kind)
    {
        if (!Owned(root, directory)) return;
        lock (Sync)
        {
            var folder = IndexRoot(root); Ordinary(folder, root); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Key(directory) + ".json"); var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, Json().Serialize(new ControlledCacheEntry { Directory = Path.GetFullPath(directory),
                    Kind = kind, LastUsedUtc = DateTime.UtcNow, Manifest = manifest }), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal static string[] FindDirectories(string root, Guid project, Guid document)
    {
        lock (Sync) return Entries(root).Where(entry => entry.Manifest.ProjectId == project
            && (document == Guid.Empty || entry.Manifest.Files.Any(file => file.DocumentId == document))).OrderByDescending(entry => entry.LastUsedUtc)
            .Select(entry => entry.Directory).ToArray();
    }
    internal static void RegisterLegacySnapshots(string root, ControlledOpenManifestDto manifest)
    {
        var parent = Path.Combine(root, ".uplm", "Snapshots", manifest.ProjectCode, manifest.RootDocumentId.ToString("N"));
        if (!Directory.Exists(parent)) return;
        try
        {
            Ordinary(parent, root);
            foreach (var kind in Directory.GetDirectories(parent))
            {
                Ordinary(kind, root);
                foreach (var directory in Directory.GetDirectories(kind))
                {
                    var paths = Files(directory, root).ToArray();
                    if (paths.Length != manifest.Files.Count || !manifest.Files.All(file =>
                        ControlledReferenceMaterializer.Matches(ControlledReferenceMaterializer.LocalPath(directory, file), file))) continue;
                    Record(root, directory, manifest, "snapshot");
                }
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
        { PluginDiagnosticLog.Record("旧缓存核对", "旧副本未能确认，已保留：" + error); }
    }
    internal static ControlledCacheUsage Inspect(string root, IReadOnlyCollection<string> protectedPaths, IReadOnlyCollection<Guid> checkedOut)
    {
        lock (Sync)
        {
            var usage = new ControlledCacheUsage();
            foreach (var directory in Directories(root))
            {
                try { usage.UsedBytes += Files(directory, root).Sum(path => new FileInfo(path).Length); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { usage.SkippedItems++; }
            }
            foreach (var entry in Entries(root))
            {
                if (CanRemove(root, entry, protectedPaths, checkedOut))
                    usage.ReclaimableBytes += Files(entry.Directory, root).Sum(path => new FileInfo(path).Length);
                else usage.SkippedItems++;
            }
            return usage;
        }
    }
    internal static ControlledCacheUsage Clean(string root, IReadOnlyCollection<string> protectedPaths,
        IReadOnlyCollection<Guid> checkedOut, long limitBytes, bool manual, Func<bool> isBusy)
    {
        lock (Sync)
        {
            if (isBusy == null || isBusy()) throw new InvalidOperationException("当前有图档操作或其他SolidWorks实例运行，暂不清理缓存。");
            var entries = Entries(root).OrderBy(entry => entry.LastUsedUtc).ToArray();
            var usage = Inspect(root, protectedPaths, checkedOut); long freed = 0;
            foreach (var entry in entries)
            {
                if (isBusy()) break;
                var obsolete = entry.Kind == "snapshot" && entries.Any(other => other.Kind == "working"
                    && other.Manifest.ProjectId == entry.Manifest.ProjectId && other.Manifest.RootDocumentId == entry.Manifest.RootDocumentId);
                if (!manual && !obsolete && usage.UsedBytes - freed <= limitBytes) continue;
                if (!CanRemove(root, entry, protectedPaths, checkedOut)) continue;
                var handles = new List<FileStream>();
                try
                {
                    var paths = Files(entry.Directory, root).ToArray();
                    // Hold every main stream against concurrent CAD loading/writing until removal.
                    foreach (var path in paths) handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete));
                    if (isBusy() || !CanRemove(root, entry, protectedPaths, checkedOut, handles)) continue;
                    foreach (var path in paths)
                    {
                        Ordinary(path, root); var length = new FileInfo(path).Length;
                        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                        try { File.Delete(path); freed += length; }
                        catch { if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); throw; }
                    }
                    foreach (var handle in handles) handle.Dispose(); handles.Clear();
                    RemoveEmpty(entry.Directory, root);
                    File.Delete(Path.Combine(IndexRoot(root), Key(entry.Directory) + ".json"));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { PluginDiagnosticLog.Record("安全清理缓存", "占用或归属变化，停止处理该副本：" + error); }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }
            var result = Inspect(root, protectedPaths, checkedOut); result.FreedBytes = freed;
            return result;
        }
    }
    private static bool CanRemove(string root, ControlledCacheEntry entry, IReadOnlyCollection<string> protectedPaths,
        IReadOnlyCollection<Guid> checkedOut, List<FileStream> held = null)
    {
        try
        {
            if (!Owned(root, entry.Directory) || !Directory.Exists(entry.Directory)
                || (protectedPaths ?? Array.Empty<string>()).Any(path => Within(path, entry.Directory))) return false;
            var expected = entry.Manifest.Files.ToDictionary(file => ControlledReferenceMaterializer.LocalPath(entry.Directory, file), StringComparer.OrdinalIgnoreCase);
            var paths = Files(entry.Directory, root).ToArray();
            if (paths.Length != expected.Count || paths.Any(path => !expected.ContainsKey(path))) return false;
            foreach (var path in paths)
            {
                var file = expected[path];
                if ((checkedOut ?? Array.Empty<Guid>()).Contains(file.DocumentId) || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) return false;
                if (held != null)
                {
                    var stream = held.Single(handle => string.Equals(handle.Name, path, StringComparison.OrdinalIgnoreCase)); stream.Position = 0;
                    string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!string.Equals(hash, file.NativeIsolation?.Sha256 ?? file.Sha256, StringComparison.OrdinalIgnoreCase)
                        && !(PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata) && metadata.DocumentId == file.DocumentId
                            && metadata.VersionId == file.VersionId && string.Equals(metadata.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(metadata.ResolvedReferenceSha256, hash, StringComparison.OrdinalIgnoreCase))) return false;
                }
            }
            return true;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException) { return false; }
    }
    private static ControlledCacheEntry[] Entries(string root)
    {
        var folder = IndexRoot(root); if (!Directory.Exists(folder)) return Array.Empty<ControlledCacheEntry>();
        Ordinary(folder, root); var entries = new List<ControlledCacheEntry>();
        foreach (var path in Directory.GetFiles(folder, "*.json"))
            try
            {
                Ordinary(path, root); var entry = Json().Deserialize<ControlledCacheEntry>(File.ReadAllText(path));
                if (entry?.Manifest?.Files != null && Owned(root, entry.Directory) && Directory.Exists(entry.Directory)
                    && Path.GetFileNameWithoutExtension(path) == Key(entry.Directory)) entries.Add(entry);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)
            { PluginDiagnosticLog.Record("缓存索引", "索引无法确认，保留原文件：" + error); }
        return entries.ToArray();
    }
    private static string[] Directories(string root)
    {
        var candidates = Entries(root).Select(entry => entry.Directory).ToList();
        var snapshot = Path.Combine(root, ".uplm", "Snapshots");
        if (Directory.Exists(snapshot)) { Ordinary(snapshot, root); candidates.Add(snapshot); }
        var view = Path.Combine(root, "View");
        if (Directory.Exists(view))
        {
            Ordinary(view, root);
            foreach (var project in Directory.GetDirectories(view))
            {
                Ordinary(project, root);
                foreach (var name in new[] { "装配工作区", "图档缓存" })
                { var folder = Path.Combine(project, name); if (Directory.Exists(folder)) candidates.Add(folder); }
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !candidates.Any(parent =>
            !string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) && Within(path, parent))).ToArray();
    }
    private static bool Owned(string root, string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Within(directory, root)) return false;
        var relative = Path.GetFullPath(directory).Substring(Path.GetFullPath(root).TrimEnd('\\', '/').Length + 1).Split('\\', '/');
        return relative.Length >= 4 && (relative[0] == ".uplm" && relative[1] == "Snapshots"
            || relative[0] == "View" && (relative[2] == "装配工作区" || relative[2] == "图档缓存"));
    }
    private static bool Within(string path, string root) => !string.IsNullOrWhiteSpace(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void Ordinary(string path, string root)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && !Within(path, root))
            throw new IOException("缓存路径超出受控目录，已停止处理。");
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("缓存路径包含目录链接，已保留文件。");
            if (string.Equals(current.TrimEnd('\\', '/'), Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private static IEnumerable<string> Files(string directory, string root)
    {
        Ordinary(directory, root);
        foreach (var path in Directory.GetFiles(directory)) { Ordinary(path, root); yield return Path.GetFullPath(path); }
        foreach (var child in Directory.GetDirectories(directory)) foreach (var path in Files(child, root)) yield return path;
    }
    private static void RemoveEmpty(string directory, string root)
    {
        Ordinary(directory, root);
        foreach (var child in Directory.GetDirectories(directory)) RemoveEmpty(child, root);
        if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
    }
}
