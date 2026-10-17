using System.Text.RegularExpressions;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public static class DingTalkOplMessageTemplate
{
    public const string Default = "所属项目：{所属项目}\n子项目：{子项目}\n问题描述：{问题描述}\n优先级：{优先级}\n实施状态：{实施状态}\n负责人：{负责人}\n提交人：{提交人}\n变更内容：{变更内容}\n附件：{附件}";
    public static readonly string[] Fields = ["所属项目", "子项目", "问题编号", "问题描述", "优先级", "实施状态", "负责人", "提交人", "要求完成日期", "解决措施", "提交日期", "完成日期", "更新人", "变更内容", "附件"];
    private static readonly Regex Placeholder = new(@"\{([^{}\r\n]+)\}");

    public static string Normalize(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return Default;
        if (template.Length > 6000) throw new PdmRuleException("消息模板最多6000个字符。");
        foreach (Match match in Placeholder.Matches(template))
            if (!Fields.Contains(match.Groups[1].Value)) throw new PdmRuleException($"消息模板包含不支持的字段：{match.Value}，请使用下方列出的字段。");
        if (Placeholder.Replace(template, "").IndexOfAny(['{', '}']) >= 0) throw new PdmRuleException("消息模板的字段括号不完整，请按{问题描述}的形式填写。");
        return template.Replace("\r\n", "\n").Trim();
    }

    public static IReadOnlyList<string> Render(string template, IReadOnlyDictionary<string, string> values) =>
        Normalize(template).Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !Placeholder.IsMatch(line) || Placeholder.Matches(line).Cast<Match>().Any(match => !string.IsNullOrWhiteSpace(values.GetValueOrDefault(match.Groups[1].Value))))
            .Select(line => Placeholder.Replace(line, match => values.GetValueOrDefault(match.Groups[1].Value) ?? "")).ToArray();
}
