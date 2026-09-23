using System;

namespace Upton.Pdm.SolidWorks;

internal static class BatchPropertyNameModelAutoFillRule
{
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
