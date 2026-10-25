using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.Domain;

public sealed record DocumentOperationRuleSettings(long Revision, IReadOnlyList<string> DisabledRuleIds)
{
    public static DocumentOperationRuleSettings Default { get; } = new(0, DocumentOperationRuleCatalog.NonBlockingIds);
    public bool IsEnabled(string id) => DocumentOperationRuleCatalog.CanBlock(id) && !DisabledRuleIds.Contains(id, StringComparer.Ordinal);
    public DocumentOperationRuleSettings Normalize() => this with { DisabledRuleIds = DisabledRuleIds
        .Concat(DocumentOperationRuleCatalog.NonBlockingIds).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray() };
}

public static class DocumentOperationRuleIds
{
    public static IReadOnlyList<string> All { get; } = DocumentOperationRuleCatalog.All.Select(rule => rule.Id).ToArray();
    public static bool Contains(string id) => All.Contains(id, StringComparer.Ordinal);
}
