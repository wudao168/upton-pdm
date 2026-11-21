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
        var length = fileName?.Trim().Length ?? 0;
        return length > MaximumArchiveFileNameLength
            ? $"文件名长度上限为{MaximumArchiveFileNameLength}，当前长度为{length}。"
            : null;
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
