#nullable disable
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveFileFingerprint
{
    public static string Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            try
            {
                using (var source = Open(path)) return ReadOpenedFile(path, source, cancellationToken);
            }
            catch (FileIdentityUnavailableException)
            {
                // Older shares may not expose a file ID. Only read them while Windows can
                // exclude every writer and replacement; never downgrade to unverified shared reads.
                return ReadProtected(path, cancellationToken);
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is Win32Exception)
        {
            throw new IOException("无法稳定读取图档文件，已停止存档。请等待文件保存完成，检查文件共享连接后重试：" + path, error);
        }
    }

    private static string ReadOpenedFile(string path, FileStream source, CancellationToken cancellationToken)
    {
        var before = State(source);
        var firstHash = Hash(source, before.Length, cancellationToken);
        RequireSame(before, State(source), path);
        // Separate handles avoid FileStream's read buffer returning bytes from the first pass.
        // Last-write timestamps can remain unchanged while SolidWorks holds its writable handle.
        using (var verification = Open(path))
        {
            RequireSame(before, State(verification), path);
            var secondHash = Hash(verification, before.Length, cancellationToken);
            RequireSame(before, State(source), path);
            RequireSame(before, State(verification), path);
            if (!string.Equals(firstHash, secondHash, StringComparison.Ordinal)) throw Changed(path);
            // A save can replace the pathname while both opened handles still refer to the old file.
            using (var current = Open(path)) RequireSame(before, State(current), path);
            return firstHash;
        }
    }

    private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    private static string ReadProtected(string path, CancellationToken cancellationToken)
    {
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan))
            return Hash(source, source.Length, cancellationToken);
    }

    private static string Hash(FileStream source, long length, CancellationToken cancellationToken)
    {
        using (var hash = SHA256.Create())
        {
            var buffer = new byte[64 * 1024];
            for (long remaining = length; remaining > 0;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw Changed(source.Name);
                hash.TransformBlock(buffer, 0, count, null, 0);
                remaining -= count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadByte() != -1) throw Changed(source.Name);
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hash.Hash).Replace("-", "");
        }
    }

    private static FileInformation State(FileStream source)
    {
        if (!GetFileInformationByHandle(source.SafeFileHandle, out var information))
            throw new FileIdentityUnavailableException("无法确认图档文件标识。", new Win32Exception(Marshal.GetLastWin32Error()));
        if (information.FileIndexHigh == 0 && information.FileIndexLow == 0)
            throw new FileIdentityUnavailableException("文件共享未提供有效文件标识，无法确认读取期间是否替换了图档。");
        return information;
    }

    private static void RequireSame(FileInformation expected, FileInformation actual, string path)
    {
        if (expected.VolumeSerialNumber != actual.VolumeSerialNumber || expected.FileIndexHigh != actual.FileIndexHigh
            || expected.FileIndexLow != actual.FileIndexLow || expected.Length != actual.Length
            || expected.LastWriteTime.dwHighDateTime != actual.LastWriteTime.dwHighDateTime
            || expected.LastWriteTime.dwLowDateTime != actual.LastWriteTime.dwLowDateTime)
            throw Changed(path);
    }

    private static IOException Changed(string path) => new IOException("图档在读取期间发生变化，请保存完成后重新存档：" + path);

    private sealed class FileIdentityUnavailableException : IOException
    {
        public FileIdentityUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        public long Length => ((long)FileSizeHigh << 32) | FileSizeLow;
    }
}
