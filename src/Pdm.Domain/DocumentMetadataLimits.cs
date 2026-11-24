#nullable enable
using System;

namespace Upton.Pdm.Domain;

public static class DocumentMetadataLimits
{
    private const int MaximumDrawingNumberLength = 255;
    private const int MaximumDocumentNameLength = 300;
    private const int MaximumArchiveFileNameLength = 260;

    public static string? GetIssue(string? drawingNumber, string? name)
    {
        var drawingNumberLength = CountCharacters(drawingNumber?.Trim());
        var nameLength = CountCharacters(name?.Trim());
        string? issue = null;
        if (drawingNumberLength > MaximumDrawingNumberLength)
            issue = $"图号不能超过{MaximumDrawingNumberLength}个字符，当前为{drawingNumberLength}个字符。";
        if (nameLength > MaximumDocumentNameLength)
            issue += $"图档名称不能超过{MaximumDocumentNameLength}个字符，当前为{nameLength}个字符。";
        return issue;
    }

    public static string? GetArchiveFileNameIssue(string? fileName)
    {
        if (IsWindowsDeviceName(fileName)) return "文件名使用 Windows 保留名称，请修改后再存档。";
        var length = fileName?.Trim().Length ?? 0;
        return length > MaximumArchiveFileNameLength
            ? $"文件名长度上限为{MaximumArchiveFileNameLength}，当前长度为{length}。"
            : null;
    }

    public static bool IsWindowsDeviceName(string? name)
    {
        if (name is null || name.Length == 0) return false;
        var end = name.IndexOf('.');
        var stem = (end < 0 ? name : name.Substring(0, end)).TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    // MySQL VARCHAR counts a supplementary character once. The add-in runs on .NET Framework.
    private static int CountCharacters(string? value)
    {
        if (value is null) return 0;

        var count = 0;
        for (var index = 0; index < value.Length; index++, count++)
        {
            if (char.IsSurrogatePair(value, index)) index++;
        }
        return count;
    }
}
