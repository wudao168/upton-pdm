using System;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class BatchPropertyNameModelAutoFillRule
{
    internal static string DocumentNameOrFileName(string documentName, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(documentName)) return documentName.Trim();
        var name = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        var extension = Path.GetExtension(name);
        return new[] { ".sldprt", ".sldasm", ".slddrw" }.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name) : name;
    }

    internal static bool TryResolveValue(string sourceValue, string currentValue, bool overwriteMismatch, out string value)
    {
        value = sourceValue?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return false;
        }

        return overwriteMismatch
            ? !string.Equals(value, currentValue?.Trim() ?? string.Empty, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(currentValue);
    }
}
