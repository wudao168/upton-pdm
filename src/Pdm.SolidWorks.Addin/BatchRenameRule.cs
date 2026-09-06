using System;
using System.IO;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal enum BatchRenameTextOperation
{
    Replace,
    Prefix,
    Suffix,
    Remove
}

internal static class BatchRenameRule
{
    internal static string Apply(
        string currentValue,
        BatchRenameTextOperation operation,
        string searchText,
        string replacementText,
        bool caseSensitive)
    {
        var current = currentValue ?? string.Empty;
        var search = searchText ?? string.Empty;
        var replacement = replacementText ?? string.Empty;
        switch (operation)
        {
            case BatchRenameTextOperation.Prefix:
                return string.Concat(replacement, current);
            case BatchRenameTextOperation.Suffix:
                return string.Concat(current, replacement);
            case BatchRenameTextOperation.Remove:
                return ReplaceLiteral(current, RequiredSearch(search), string.Empty, caseSensitive);
            default:
                return ReplaceLiteral(current, RequiredSearch(search), replacement, caseSensitive);
        }
    }

    internal static string NormalizeFileBaseName(string requestedName, string extension)
    {
        var name = (requestedName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(extension) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - extension.Length).Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("新文件名不能为空。");
        }
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("新文件名包含Windows不允许的字符，或以句点、空格结尾。");
        }
        if (name.Length + (extension?.Length ?? 0) > 240)
        {
            throw new InvalidOperationException("新文件名过长。");
        }

        var deviceName = name.Split('.')[0].ToUpperInvariant();
        var reserved = deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX" || deviceName == "NUL"
            || (deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.Ordinal) || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                && deviceName[3] >= '1' && deviceName[3] <= '9');
        if (reserved)
        {
            throw new InvalidOperationException("该名称是Windows保留名称，请使用其他名称。");
        }
        return name;
    }

    private static string RequiredSearch(string search)
    {
        if (string.IsNullOrEmpty(search))
        {
            throw new InvalidOperationException("查找文字不能为空。");
        }
        return search;
    }

    private static string ReplaceLiteral(
        string current,
        string search,
        string replacement,
        bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matchIndex = current.IndexOf(search, 0, comparison);
        if (matchIndex < 0)
        {
            return current;
        }

        var result = new StringBuilder(current.Length);
        var position = 0;
        while (matchIndex >= 0)
        {
            result.Append(current, position, matchIndex - position);
            result.Append(replacement);
            position = matchIndex + search.Length;
            matchIndex = current.IndexOf(search, position, comparison);
        }
        result.Append(current, position, current.Length - position);
        return result.ToString();
    }
}
