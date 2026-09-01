#nullable enable
using System;
using System.Collections.Generic;

namespace Upton.Pdm.Domain;

// Shared with the native add-in: retain the card's scope independently of old CAD properties.
public static class CadPropertyCardSnapshot
{
    public const string SchemaKey = "UPLM.PropertyCardSchema";
    public const string ScopePrefix = "UPLM.PropertyCardScope/";

    public static void AddField(IDictionary<string, string?> snapshot, string name, bool configurationSpecific)
    {
        snapshot[SchemaKey] = "1";
        var key = ScopePrefix + name.Trim();
        var scope = configurationSpecific ? "Configuration" : "Global";
        snapshot[key] = snapshot.TryGetValue(key, out var previous) && previous != scope ? "Both" : scope;
    }

    public static string? Read(IReadOnlyDictionary<string, string?>? snapshot, string configuration, string propertyName)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(propertyName)) return null;
        var name = propertyName.Trim();
        if (snapshot.ContainsKey(SchemaKey))
        {
            if (!snapshot.TryGetValue(ScopePrefix + name, out var scope)) return null;
            var key = scope == "Global" ? "全局/" + name : "配置:" + configuration + "/" + name;
            if (snapshot.TryGetValue(key, out var value)) return value?.Trim() ?? string.Empty;
            if (scope == "Both" && snapshot.TryGetValue("全局/" + name, out value)) return value?.Trim() ?? string.Empty;
            return string.Empty;
        }

        // Old snapshots have no card scope. Keep legacy scope priority until explicitly annotated;
        // do not infer a card from a filename or change other projects' historical property scopes.
        foreach (var key in new[] { "配置:" + configuration + "/" + name, "全局/" + name, name })
            if (snapshot.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value!.Trim();
        return null;
    }
}
