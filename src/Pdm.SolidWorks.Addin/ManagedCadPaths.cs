#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Upton.Pdm.SolidWorks;

// SolidWorks and its in-process .NET Framework host need ordinary paths.
// Keep the CAD leaf and choose a shallower managed directory before using IO/COM.
internal static class ManagedCadPaths
{
    private const int FilePathLimit = 260;
    private const int DirectoryPathLimit = 248;
    private const int ProjectTokenLength = 10;
    private const int EntryTokenLength = 16;
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    internal static string CompactWorkspaceRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "Cad");

    internal static string CompactContainer(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        return string.Equals(root.TrimEnd('\\', '/'), CompactWorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.Combine(root, ".uplm", "c");
    }

    internal static string CompactProjectDirectory(string workspaceRoot, string projectCode) =>
        Path.Combine(CompactContainer(workspaceRoot), Token((projectCode ?? string.Empty).ToUpperInvariant(), ProjectTokenLength));

    internal static string CompactDirectory(string workspaceRoot, string kind, string identity, string projectCode = null) =>
        Path.Combine(projectCode == null ? CompactContainer(workspaceRoot) : CompactProjectDirectory(workspaceRoot, projectCode),
            CompactEntryName(kind, identity));

    internal static string CompactEntryName(string kind, string identity) => kind + Token(identity, EntryTokenLength);

    internal static bool IsCompactProjectEntry(string workspaceRoot, string path)
    {
        var container = CompactContainer(workspaceRoot);
        if (!IsWithin(path, container)) return false;
        var entry = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        return parent != null && string.Equals(Path.GetDirectoryName(parent), container, StringComparison.OrdinalIgnoreCase)
            && IsToken(Path.GetFileName(parent), ProjectTokenLength)
            && entry.Length == EntryTokenLength + 1 && "wlrdba".Contains(entry[0])
            && IsToken(entry.Substring(1), EntryTokenLength);
    }

    internal static bool IsWithin(string path, string directory) => !string.IsNullOrWhiteSpace(path) &&
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsToken(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    private static string Token(string value, int length)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, length);
    }

    internal static bool Fits(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var directoryLength = directory.TrimEnd('\\', '/').Length;
        var lastSeparator = Math.Max(relativePath.LastIndexOf('\\'), relativePath.LastIndexOf('/'));
        var parentLength = lastSeparator < 0 ? directoryLength : directoryLength + 1 + lastSeparator;
        return parentLength < DirectoryPathLimit && directoryLength + 1 + relativePath.Length < FilePathLimit;
    }

    internal static bool Fits(string directory, IEnumerable<string> relativePaths) =>
        !string.IsNullOrWhiteSpace(directory) && relativePaths.All(path => Fits(directory, path));

    internal static string ChooseDirectory(string preferred, IEnumerable<string> alternatives, IEnumerable<string> relativePaths)
    {
        var files = relativePaths.ToArray();
        foreach (var directory in new[] { preferred }.Concat(alternatives).Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Fits(directory, files)) continue;
            // Also check relative path safety before callers create directories or transfer files.
            foreach (var file in files) Resolve(directory, file);
            return directory;
        }

        var longest = files.OrderByDescending(path => path?.Length ?? 0).FirstOrDefault();
        throw new InvalidOperationException("当前计算机的受控目录无法容纳图档原路径「" + longest
            + "」。请在插件设置中选择更短的本地工作区后重试。");
    }

    internal static string ChooseStagingDirectory(string preferred, string identity, IEnumerable<string> relativePaths) =>
        ChooseDirectory(preferred, new[] { CompactDirectory(CompactWorkspaceRoot, "s", identity) }, relativePaths);

    internal static string Resolve(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Split('\\', '/').Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment.IndexOfAny(InvalidNameCharacters) >= 0
                || segment[segment.Length - 1] == '.' || segment[segment.Length - 1] == ' '))
            throw new InvalidDataException("PLM打开清单包含无效或越界的文件路径。");

        if (!Fits(directory, relativePath))
            throw new PathTooLongException("当前本地目录无法容纳图档原路径「" + relativePath + "」。");
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!IsWithin(path, directory))
            throw new InvalidDataException("PLM打开清单包含无效或越界的文件路径。");
        return path;
    }

    internal static string TemporarySibling(string path)
    {
        var directory = Path.GetDirectoryName(path);
        // Do not add a suffix to the CAD leaf. The sibling only contains bytes.
        var name = ".p" + Token(Guid.NewGuid().ToString("N"), 9);
        return Resolve(directory, name);
    }
}