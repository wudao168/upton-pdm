using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

// A copy is leased from its creation through the last upload/recovery use. The
// OS releases the lease on a crash, so abandoned copies can still be reclaimed.
internal static class ArchiveTemporaryCopyLease
{
    internal const string FileName = ".uplm-upload-active";
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, FileStream> Active = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
    internal const string CompactDirectoryPrefix = "c-";
    private const string NativeCompactKind = "t";

    internal static string RootDirectory => Path.Combine(Path.GetTempPath(), "UPTON-PDM", "checkin");

    internal static string CreateCopyPath(string sourcePath, Guid documentId, bool preserveFileName)
    {
        var fileName = Path.GetFileName(sourcePath);
        var root = RootDirectory;
        var copyId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, documentId.ToString("N"), copyId);
        if (!FitsCopy(directory, fileName))
            directory = Path.Combine(root, CompactDirectoryPrefix + copyId);
        // Streamed copies are read as bytes and keep the original name in the upload request.
        var copyName = !preserveFileName && !FitsCopy(directory, fileName)
            ? "upload" + Path.GetExtension(fileName) : fileName;
        if (!FitsCopy(directory, copyName))
        {
            directory = ManagedCadPaths.CompactDirectory(ManagedCadPaths.CompactWorkspaceRoot, NativeCompactKind, copyId);
            if (FitsCopy(directory, fileName)) copyName = fileName;
        }
        if (!FitsCopy(directory, copyName))
            throw new InvalidOperationException("本机受控目录无法容纳原文件名，未生成存档副本。请联系管理员核对本机工作目录：" + fileName);

        var copyPath = ManagedCadPaths.Resolve(directory, copyName);
        Directory.CreateDirectory(directory);
        Start(directory);
        return copyPath;
    }

    internal static bool IsNativeCompactDirectoryName(string name) =>
        name.Length == NativeCompactKind.Length + 16 && name.StartsWith(NativeCompactKind, StringComparison.Ordinal)
        && name.Skip(NativeCompactKind.Length).All(Uri.IsHexDigit);

    private static bool FitsCopy(string directory, string fileName) =>
        ManagedCadPaths.Fits(directory, fileName) && ManagedCadPaths.Fits(directory, FileName);

    internal static void Start(string directory)
    {
        lock (Sync) Active.Add(Path.GetFullPath(directory), new FileStream(Path.Combine(directory, FileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
    }
    internal static void Release(string directory)
    {
        lock (Sync)
        {
            var key = Path.GetFullPath(directory);
            if (!Active.TryGetValue(key, out var lease)) return;
            Active.Remove(key); lease.Dispose();
        }
    }
}
