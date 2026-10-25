using System;
using System.Text.RegularExpressions;

namespace Upton.Pdm.Domain;

// Business labels never replace immutable archive revision identifiers.
public static class DrawingRevision
{
    public static string Letter(string revision)
    {
        var value = (revision ?? string.Empty).Trim().ToUpperInvariant();
        var work = Regex.Match(value, @"^(?:([A-Z])-)?W([1-9][0-9]*)$", RegexOptions.CultureInvariant);
        if (!work.Success) return value;
        if (!work.Groups[1].Success) return "A";
        var previous = work.Groups[1].Value[0];
        return previous == 'Z' ? string.Empty : ((char)(previous + 1)).ToString();
    }

    public static bool IsWork(string revision) => Regex.IsMatch((revision ?? string.Empty).Trim(),
        @"^(?:[A-Z]-)?W[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Display(string revision)
    {
        var value = (revision ?? string.Empty).Trim();
        if (value.Length == 0) return "—";
        var dirty = value.EndsWith("*", StringComparison.Ordinal);
        var original = value;
        value = value.TrimEnd('*').ToUpperInvariant();
        if (!IsWork(value)) return Regex.IsMatch(value, "^[A-Z]$") ? value + (dirty ? "*" : string.Empty) : original;
        var letter = Letter(value);
        var label = letter.Length == 0 ? "版次已达 Z（待处理）" : letter + "-D";
        return label + (dirty ? "*" : string.Empty);
    }
}
