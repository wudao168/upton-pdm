namespace Upton.Pdm.Domain;

public static class DocumentDisplayNameResolver
{
    private static readonly string[] LegacyNameProperties = ["NT", "零件名称", "名称"];

    public static string Resolve(
        string? storedName,
        string drawingNumber,
        IReadOnlyDictionary<string, string?>? properties,
        string configuredNameProperty)
    {
        var propertyNames = new[] { configuredNameProperty }
            .Concat(LegacyNameProperties)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var propertyName in propertyNames)
        {
            var value = FindProperty(properties, propertyName.Trim());
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        var normalizedStoredName = storedName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedStoredName)
            && !normalizedStoredName.Contains('/')
            && !normalizedStoredName.Contains('\\'))
        {
            return normalizedStoredName;
        }

        return drawingNumber.Trim();
    }

    private static string? FindProperty(IReadOnlyDictionary<string, string?>? properties, string propertyName)
    {
        if (properties is null) return null;

        var globalKey = string.Concat("全局/", propertyName);
        var global = properties.FirstOrDefault(pair => string.Equals(pair.Key, globalKey, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(global.Value)) return global.Value;

        var suffix = string.Concat("/", propertyName);
        var scoped = properties.FirstOrDefault(pair =>
            string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase)
            || pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return scoped.Value;
    }
}
