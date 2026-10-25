#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace Upton.Pdm.Domain;

public sealed class DocumentOperationRuleMessage
{
    public string Kind { get; set; }
    public string Text { get; set; }
}

public sealed class DocumentOperationRuleDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string Behavior { get; set; }
    public DocumentOperationRuleMessage[] Messages { get; set; }
    public string MessageNote { get; set; }
    public bool CanBlock { get; set; } = true;
}

public static class DocumentOperationRuleCatalog
{
    public static IReadOnlyList<DocumentOperationRuleDefinition> All { get; } = Read();
    public static bool CanBlock(string id) => All.FirstOrDefault(rule => rule.Id == id)?.CanBlock ?? true;
    public static IReadOnlyList<string> NonBlockingIds => All.Where(rule => !rule.CanBlock).Select(rule => rule.Id).ToArray();
    private static DocumentOperationRuleDefinition[] Read()
    {
        using (var stream = typeof(DocumentOperationRuleCatalog).Assembly.GetManifestResourceStream("Upton.Pdm.DocumentOperationRules.json"))
        using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("图档规则目录缺失。")))
        {
#if NETFRAMEWORK
            return new JavaScriptSerializer().Deserialize<DocumentOperationRuleDefinition[]>(reader.ReadToEnd());
#else
            return JsonSerializer.Deserialize<DocumentOperationRuleDefinition[]>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
#endif
        }
    }

    public static bool MessageMatches(string template, string message)
    {
        var text = Regex.Replace(message ?? "", @"\s+", "").ToLowerInvariant();
        var fragments = Regex.Split(Regex.Replace(template ?? "", @"\s+", "").ToLowerInvariant(), @"\{[^}]+\}").Where(value => value.Length > 0).ToArray();
        if (fragments.Sum(value => value.Length) < 4) return false;
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var index = text.IndexOf(fragment, offset, StringComparison.Ordinal);
            if (index < 0) return false;
            offset = index + fragment.Length;
        }
        return true;
    }
}
