using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Upton.Pdm.SolidWorks;

internal sealed class NativePropertyCardTemplate
{
    private NativePropertyCardTemplate(
        string filePath,
        IReadOnlyList<NativePropertyCardField> fields,
        bool isAvailable)
    {
        FilePath = filePath ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(FilePath)
            ? "未发现属性卡"
            : Path.GetFileName(FilePath);
        Fields = fields ?? Array.Empty<NativePropertyCardField>();
        IsAvailable = isAvailable;
    }

    public string FilePath { get; }

    public string DisplayName { get; }

    public IReadOnlyList<NativePropertyCardField> Fields { get; }

    public bool IsAvailable { get; }

    public static NativePropertyCardTemplate Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return Missing();
        }

        var document = XDocument.Load(filePath, LoadOptions.None);
        var fields = document
            .Descendants("Control")
            .Select(control => new NativePropertyCardField(
                (string)control.Attribute("PropName"),
                EditorPropertyName((string)control.Attribute("PropName"), (string)control.Attribute("Label")),
                string.Equals((string)control.Attribute("ApplyTo"), "Config", StringComparison.OrdinalIgnoreCase)))
            .Where(field => !string.IsNullOrWhiteSpace(field.PropertyName))
            .GroupBy(
                field => string.Concat(field.ConfigurationSpecific ? "配置|" : "全局|", field.PropertyName),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (fields.Length == 0)
        {
            throw new InvalidDataException(string.Concat(Path.GetFileName(filePath), "中没有可应用的SolidWorks属性字段。"));
        }

        return new NativePropertyCardTemplate(Path.GetFullPath(filePath), fields, true);
    }

    public static NativePropertyCardTemplate Missing() =>
        new NativePropertyCardTemplate(string.Empty, Array.Empty<NativePropertyCardField>(), false);

    public override string ToString() => DisplayName;

    private static string EditorPropertyName(string propertyName, string label)
    {
        var candidate = propertyName?.Trim() ?? string.Empty;
        if (string.Equals(candidate, "物料分类", StringComparison.OrdinalIgnoreCase))
        {
            return "分类";
        }
        if (string.Equals(candidate, "材质", StringComparison.OrdinalIgnoreCase))
        {
            return "材料";
        }
        if (BatchPropertyEditItem.EditablePropertyNames.Any(name =>
            string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return candidate;
        }

        candidate = label?.Trim() ?? string.Empty;
        return BatchPropertyEditItem.EditablePropertyNames.FirstOrDefault(name =>
            string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }
}

internal sealed class NativePropertyCardField
{
    public NativePropertyCardField(string propertyName, string editorPropertyName, bool configurationSpecific)
    {
        PropertyName = propertyName?.Trim() ?? string.Empty;
        EditorPropertyName = editorPropertyName?.Trim() ?? string.Empty;
        ConfigurationSpecific = configurationSpecific;
    }

    public string PropertyName { get; }

    public string EditorPropertyName { get; }

    public bool ConfigurationSpecific { get; }
}
