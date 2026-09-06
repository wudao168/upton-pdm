using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class BatchPropertyNameModelAutoFillRule
{
    private static readonly IReadOnlyList<string> MaterialNameOnly = new[] { "物料名称" };
    private static readonly IReadOnlyList<string> ModelOnly = new[] { "型号" };
    private static readonly IReadOnlyList<string> MaterialNameAndModel = new[] { "物料名称", "型号" };

    internal static IReadOnlyList<string> TargetPropertyNames(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var containsChinese = normalized.Any(IsChinese);
        if (!containsChinese)
        {
            return ModelOnly;
        }

        return normalized.Any(IsEnglishLetterOrDigit)
            ? MaterialNameAndModel
            : MaterialNameOnly;
    }

    private static bool IsChinese(char character) =>
        character >= '\u3400' && character <= '\u4DBF'
        || character >= '\u4E00' && character <= '\u9FFF'
        || character >= '\uF900' && character <= '\uFAFF';

    private static bool IsEnglishLetterOrDigit(char character) =>
        character >= 'A' && character <= 'Z'
        || character >= 'a' && character <= 'z'
        || character >= '0' && character <= '9';
}
