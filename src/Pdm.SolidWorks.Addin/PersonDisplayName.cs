using System;
using System.Collections.Generic;

namespace Upton.Pdm.SolidWorks;

internal static class PersonDisplayName
{
    public static string Format(string username, string displayName)
    {
        var name = displayName?.Trim();
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, username?.Trim(), StringComparison.OrdinalIgnoreCase))
            return name;
        return string.IsNullOrWhiteSpace(username) ? string.Empty : "姓名待补充";
    }

    public static string Resolve(IReadOnlyDictionary<string, string> names, string username)
    {
        names.TryGetValue(username?.Trim() ?? string.Empty, out var name);
        return Format(username, name);
    }
}
