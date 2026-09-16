using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class BatchPropertyFilterRule
{
    internal static bool MatchesQuery(IEnumerable<string> values, string query, bool excludeMatches)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return true;
        }

        var contains = (values ?? Array.Empty<string>())
            .Any(value => !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(normalizedQuery, StringComparison.CurrentCultureIgnoreCase) >= 0);
        return excludeMatches ? !contains : contains;
    }
}
