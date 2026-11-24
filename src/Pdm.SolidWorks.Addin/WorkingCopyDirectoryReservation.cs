using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Upton.Pdm.SolidWorks;

// A cache heartbeat records native windows after they open. The file handle
// reserves the physical directory before staging or changing file attributes,
// including when two processes choose a previously unused directory at once.
internal static class WorkingCopyDirectoryReservation
{
    internal static readonly string SessionKey = Guid.NewGuid().ToString("N");
    private static readonly Dictionary<string, FileStream> Handles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Private = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static string Select(string directory, Func<string> privateDirectory, Func<string, string> managedRoot,
        CacheInstanceProtection other, bool preserveDirectory)
    {
        lock (Handles)
        {
            var path = DirectoryKey(directory);
            bool Occupied(string target)
            {
                var root = managedRoot(target);
                return other.Paths.Any(file =>
                {
                    if (string.IsNullOrWhiteSpace(file)) return false;
                    if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase) || ManagedCadPaths.IsWithin(file, target)) return true;
                    var parent = Path.GetDirectoryName(file);
                    // A flat legacy project can contain newer workspaces. A raw CAD
                    // file outside the managed cache does not reserve its whole drive.
                    return ManagedCadPaths.IsWithin(parent, root) && ManagedCadPaths.IsWithin(target, parent);
                });
            }
            // Uncooperative, busy or unreadable instances can still have an open
            // document. Only our unguessable private directory can be reused then.
            if (!Occupied(path) && (other.UnconfirmedProcesses.Count == 0 || Private.Contains(path))
                && TryReserve(path, managedRoot(path))) return path;
            if (preserveDirectory)
                throw new InvalidOperationException("另一 SolidWorks 实例正在使用此工作目录，或暂时无法核实其占用状态。已保留当前窗口、编辑权限及草稿。请在原实例继续操作，或在其正常关闭图档后重试。目录：" + path);
            var isolated = DirectoryKey(privateDirectory());
            if (Occupied(isolated) || Directory.Exists(isolated) && !Handles.ContainsKey(isolated)
                || !TryReserve(isolated, managedRoot(isolated)))
                throw new IOException("未能取得独立的受控工作目录，已保留现有文件。请核实其他 SolidWorks 实例的工作状态后重试。");
            Private.Add(isolated);
            return isolated;
        }
    }

    private static string DirectoryKey(string directory)
    {
        // Manifests can include an ending separator while native document parents
        // omit it. Both spellings must reuse the handle for the same lock file.
        var full = Path.GetFullPath(directory);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == root.Length ? root + Path.DirectorySeparatorChar : trimmed;
    }

    private static bool TryReserve(string directory, string root)
    {
        if (Handles.ContainsKey(directory)) return true;
        string name;
        using (var sha = SHA256.Create())
            name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
        var folder = Path.Combine(root, ".uplm", "CacheSessions", "Working");
        var marker = Path.Combine(folder, name + ".lck");
        ControlledCacheMaintenance.RequireOrdinaryPath(marker, root);
        Directory.CreateDirectory(folder);
        try
        {
            // OpenOrCreate leaves an empty reusable marker. Windows releases the
            // exclusive writer when its process exits, even after an unclean exit.
            Handles.Add(directory, new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read));
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) { return false; }
    }
}
