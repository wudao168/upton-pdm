using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal enum BatchRenameTextOperation
{
    Replace,
    Prefix,
    Suffix,
    Remove
}

internal enum BatchRenameHierarchyKind
{
    Assembly,
    Part
}

internal sealed class BatchRenameHierarchyItem
{
    internal BatchRenameHierarchyItem(
        string key,
        string parentKey,
        BatchRenameHierarchyKind kind,
        bool isRoot = false,
        string existingNumber = "")
    {
        Key = key ?? string.Empty;
        ParentKey = parentKey ?? string.Empty;
        Kind = kind;
        IsRoot = isRoot;
        ExistingNumber = existingNumber?.Trim() ?? string.Empty;
    }

    internal string Key { get; }
    internal string ParentKey { get; }
    internal BatchRenameHierarchyKind Kind { get; }
    internal bool IsRoot { get; }
    internal string ExistingNumber { get; }
}

internal static class BatchRenameRule
{
    internal const string ComponentDrawingScope = "部件图";
    internal const string NonStandardScope = "非标件";

    internal static bool IsInHierarchyScope(
        BatchRenameHierarchyKind kind,
        string classification,
        bool includeComponentDrawings,
        bool includeNonStandardParts)
    {
        var normalizedClassification = (classification ?? string.Empty).Trim();
        var isComponentDrawing = kind == BatchRenameHierarchyKind.Assembly
            || string.Equals(normalizedClassification, ComponentDrawingScope, StringComparison.OrdinalIgnoreCase);
        var isNonStandard = string.Equals(normalizedClassification, NonStandardScope, StringComparison.OrdinalIgnoreCase);
        return includeComponentDrawings && isComponentDrawing
            || includeNonStandardParts && isNonStandard;
    }

    internal static bool CanGenerateHierarchyRename(
        bool selected,
        BatchRenameHierarchyKind kind,
        string classification,
        bool includeComponentDrawings,
        bool includeNonStandardParts) =>
        selected && IsInHierarchyScope(
            kind,
            classification,
            includeComponentDrawings,
            includeNonStandardParts);

    internal static IReadOnlyDictionary<string, string> BuildHierarchyNumbers(IEnumerable<BatchRenameHierarchyItem> source)
    {
        var items = (source ?? Array.Empty<BatchRenameHierarchyItem>())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var siblingStates = new Dictionary<string, HierarchySiblingNumberState>(StringComparer.OrdinalIgnoreCase);
        var root = items.FirstOrDefault(item => item.IsRoot && item.Kind == BatchRenameHierarchyKind.Assembly);
        if (root == null)
        {
            throw new InvalidOperationException("当前结构缺少根装配体，不能生成层级编号。");
        }
        result[root.Key] = "00";

        foreach (var key in BuildHierarchyOrder(items).Skip(1))
        {
            var item = items.First(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(item.ParentKey) || !result.TryGetValue(item.ParentKey, out var parentNumber))
            {
                throw new InvalidOperationException("图档层级关系不完整，不能生成层级编号。");
            }

            if (item.Kind == BatchRenameHierarchyKind.Assembly)
            {
                var index = NextHierarchyIndex(item, items, parentNumber, root.Key, siblingStates);
                result[item.Key] = string.Equals(item.ParentKey, root.Key, StringComparison.OrdinalIgnoreCase)
                    ? index.ToString("00")
                    : string.Concat(parentNumber, ".", index.ToString("00"));
            }
            else
            {
                var index = NextHierarchyIndex(item, items, parentNumber, root.Key, siblingStates);
                result[item.Key] = string.Concat(parentNumber, "-", index.ToString("00"));
            }
        }
        return result;
    }

    internal static IReadOnlyList<string> BuildHierarchyOrder(IEnumerable<BatchRenameHierarchyItem> source)
    {
        var items = (source ?? Array.Empty<BatchRenameHierarchyItem>())
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var root = items.FirstOrDefault(item => item.IsRoot && item.Kind == BatchRenameHierarchyKind.Assembly);
        if (root == null)
        {
            throw new InvalidOperationException("当前结构缺少根装配体，不能生成层级编号。");
        }

        var result = new List<string> { root.Key };
        var visited = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
        AppendHierarchyChildren(root.Key, items, result, visited);
        if (visited.Count != items.Length)
        {
            throw new InvalidOperationException("图档层级关系不完整，不能生成层级编号。");
        }
        return result;
    }

    internal static string HierarchyFileBaseName(string serialNumber, string hierarchyNumber)
    {
        var serial = (serialNumber ?? string.Empty).Trim();
        var number = (hierarchyNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(serial))
        {
            throw new InvalidOperationException("项目序列号不能为空。");
        }
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new InvalidOperationException("层级编号不能为空。");
        }
        return NormalizeFileBaseName(string.Concat(serial, ".", number), string.Empty);
    }

    internal static string ExtractExistingHierarchyNumber(string currentFileBaseName)
    {
        var name = (currentFileBaseName ?? string.Empty).Trim();
        var separator = name.IndexOf('.');
        if (separator <= 0 || separator >= name.Length - 1)
        {
            return string.Empty;
        }
        var candidate = name.Substring(separator + 1);
        return candidate.All(character => char.IsDigit(character) || character == '.' || character == '-')
            ? candidate
            : string.Empty;
    }

    internal static string Apply(
        string currentValue,
        BatchRenameTextOperation operation,
        string searchText,
        string replacementText,
        bool caseSensitive)
    {
        var current = currentValue ?? string.Empty;
        var search = searchText ?? string.Empty;
        var replacement = replacementText ?? string.Empty;
        switch (operation)
        {
            case BatchRenameTextOperation.Prefix:
                return string.Concat(replacement, current);
            case BatchRenameTextOperation.Suffix:
                return string.Concat(current, replacement);
            case BatchRenameTextOperation.Remove:
                return ReplaceLiteral(current, RequiredSearch(search), string.Empty, caseSensitive);
            default:
                return ReplaceLiteral(current, RequiredSearch(search), replacement, caseSensitive);
        }
    }

    internal static string NormalizeFileBaseName(string requestedName, string extension)
    {
        var name = (requestedName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(extension) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - extension.Length).Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("新文件名不能为空。");
        }
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("新文件名包含Windows不允许的字符，或以句点、空格结尾。");
        }
        if (name.Length + (extension?.Length ?? 0) > 240)
        {
            throw new InvalidOperationException("新文件名过长。");
        }

        var deviceName = name.Split('.')[0].ToUpperInvariant();
        var reserved = deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX" || deviceName == "NUL"
            || (deviceName.Length == 4
                && (deviceName.StartsWith("COM", StringComparison.Ordinal) || deviceName.StartsWith("LPT", StringComparison.Ordinal))
                && deviceName[3] >= '1' && deviceName[3] <= '9');
        if (reserved)
        {
            throw new InvalidOperationException("该名称是Windows保留名称，请使用其他名称。");
        }
        return name;
    }

    private static string RequiredSearch(string search)
    {
        if (string.IsNullOrEmpty(search))
        {
            throw new InvalidOperationException("查找文字不能为空。");
        }
        return search;
    }

    private static int NextHierarchyIndex(
        BatchRenameHierarchyItem item,
        IReadOnlyList<BatchRenameHierarchyItem> items,
        string parentNumber,
        string rootKey,
        IDictionary<string, HierarchySiblingNumberState> states)
    {
        var stateKey = string.Concat((int)item.Kind, "\0", item.ParentKey ?? string.Empty);
        if (!states.TryGetValue(stateKey, out var state))
        {
            state = new HierarchySiblingNumberState();
            foreach (var sibling in items.Where(candidate => candidate.Kind == item.Kind
                && string.Equals(candidate.ParentKey, item.ParentKey, StringComparison.OrdinalIgnoreCase)))
            {
                if (!TryGetExistingSiblingIndex(sibling, parentNumber, rootKey, out var index)
                    || !state.Used.Add(index))
                {
                    continue;
                }
                state.ExistingByKey[sibling.Key] = index;
                state.Next = Math.Max(state.Next, index + 1);
            }
            states[stateKey] = state;
        }

        if (state.ExistingByKey.TryGetValue(item.Key, out var existing))
        {
            return existing;
        }
        while (state.Used.Contains(state.Next))
        {
            state.Next++;
        }
        var next = state.Next++;
        state.Used.Add(next);
        return next;
    }

    private static bool TryGetExistingSiblingIndex(
        BatchRenameHierarchyItem item,
        string parentNumber,
        string rootKey,
        out int index)
    {
        index = 0;
        var existing = item.ExistingNumber ?? string.Empty;
        string leaf;
        if (item.Kind == BatchRenameHierarchyKind.Assembly)
        {
            if (string.Equals(item.ParentKey, rootKey, StringComparison.OrdinalIgnoreCase))
            {
                leaf = existing;
            }
            else
            {
                var prefix = string.Concat(parentNumber, ".");
                if (!existing.StartsWith(prefix, StringComparison.Ordinal)) return false;
                leaf = existing.Substring(prefix.Length);
            }
        }
        else
        {
            var prefix = string.Concat(parentNumber, "-");
            if (!existing.StartsWith(prefix, StringComparison.Ordinal)) return false;
            leaf = existing.Substring(prefix.Length);
        }

        return leaf.Length >= 2
            && leaf.All(char.IsDigit)
            && int.TryParse(leaf, out index)
            && index > 0;
    }

    private sealed class HierarchySiblingNumberState
    {
        internal Dictionary<string, int> ExistingByKey { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal HashSet<int> Used { get; } = new HashSet<int>();
        internal int Next { get; set; } = 1;
    }

    private static void AppendHierarchyChildren(
        string parentKey,
        IReadOnlyList<BatchRenameHierarchyItem> items,
        ICollection<string> result,
        ISet<string> visited)
    {
        var children = items
            .Where(item => !item.IsRoot && string.Equals(item.ParentKey, parentKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var part in children.Where(item => item.Kind == BatchRenameHierarchyKind.Part))
        {
            if (visited.Add(part.Key)) result.Add(part.Key);
        }
        foreach (var assembly in children.Where(item => item.Kind == BatchRenameHierarchyKind.Assembly))
        {
            if (!visited.Add(assembly.Key)) continue;
            result.Add(assembly.Key);
            AppendHierarchyChildren(assembly.Key, items, result, visited);
        }
    }

    private static string ReplaceLiteral(
        string current,
        string search,
        string replacement,
        bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matchIndex = current.IndexOf(search, 0, comparison);
        if (matchIndex < 0)
        {
            return current;
        }

        var result = new StringBuilder(current.Length);
        var position = 0;
        while (matchIndex >= 0)
        {
            result.Append(current, position, matchIndex - position);
            result.Append(replacement);
            position = matchIndex + search.Length;
            matchIndex = current.IndexOf(search, position, comparison);
        }
        result.Append(current, position, current.Length - position);
        return result.ToString();
    }
}
