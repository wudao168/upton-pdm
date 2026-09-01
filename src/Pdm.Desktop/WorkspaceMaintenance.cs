using System;
using System.IO;

namespace Upton.Pdm.Desktop;

internal sealed class WorkspaceUsage
{
    public string WorkspaceRoot { get; set; } = string.Empty;
    public int WorkingFiles { get; set; }
    public long WorkingBytes { get; set; }
    public int SnapshotFiles { get; set; }
    public long SnapshotBytes { get; set; }
    public int RecoveryFiles { get; set; }
    public long RecoveryBytes { get; set; }
}

internal static class WorkspaceMaintenance
{
    public static WorkspaceUsage ReadUsage(string workspaceRoot)
    {
        var root = NormalizeRoot(workspaceRoot);
        var result = new WorkspaceUsage { WorkspaceRoot = root };
        ReadDirectory(Path.Combine(root, "View"), out var workingFiles, out var workingBytes);
        ReadDirectory(Path.Combine(root, ".uplm", "Snapshots"), out var snapshotFiles, out var snapshotBytes);
        ReadDirectory(Path.Combine(root, ".uplm", "Recovery"), out var recoveryFiles, out var recoveryBytes);
        result.WorkingFiles = workingFiles;
        result.WorkingBytes = workingBytes;
        result.SnapshotFiles = snapshotFiles;
        result.SnapshotBytes = snapshotBytes;
        result.RecoveryFiles = recoveryFiles;
        result.RecoveryBytes = recoveryBytes;
        return result;
    }

    public static WorkspaceUsage ClearReusableCache(string workspaceRoot)
    {
        var root = NormalizeRoot(workspaceRoot);
        DeleteUnlockedFiles(Path.Combine(root, ".uplm", "Snapshots"));
        DeleteUnlockedFiles(Path.Combine(root, ".uplm", "Staging"));
        return ReadUsage(root);
    }

    private static string NormalizeRoot(string workspaceRoot)
    {
        var fullPath = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("工作区不能设置为磁盘根目录。");
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ReadDirectory(string path, out int files, out long bytes)
    {
        files = 0;
        bytes = 0;
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    files++;
                    bytes += info.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteUnlockedFiles(string path)
    {
        if (!Directory.Exists(path)) return;
        DeleteUnlockedFiles(new DirectoryInfo(path), true);
    }

    private static void DeleteUnlockedFiles(DirectoryInfo directory, bool keepRoot)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
        FileInfo[] files;
        DirectoryInfo[] directories;
        try
        {
            files = directory.GetFiles();
            directories = directory.GetDirectories();
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var child in directories) DeleteUnlockedFiles(child, false);
        foreach (var file in files)
        {
            try
            {
                using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None)) { }
                file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (keepRoot) return;
        try
        {
            if (directory.GetFileSystemInfos().Length == 0) directory.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
