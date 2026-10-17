using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveReferencePath
{
    public static string Resolve(string original, string directory, string step, string owner,
        string instance = null, string configuration = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(original)) throw new ArgumentException("Empty reference path");
            if (!Path.IsPathRooted(original) && string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Relative reference has no owner directory");
            return Path.GetFullPath(Path.IsPathRooted(original) ? original : Path.Combine(directory ?? string.Empty, original));
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            var characters = InvalidCharacters(original);
            var message = "引用路径无法解析，已停止本次操作，文件内容已保留。\r\n"
                + "发生步骤：" + Visible(step) + "\r\n所属图档：" + Visible(owner)
                + (string.IsNullOrEmpty(instance) ? string.Empty : "\r\n组件实例：" + Visible(instance))
                + (string.IsNullOrEmpty(configuration) ? string.Empty : "\r\n引用配置：" + Visible(configuration))
                + "\r\n原始引用：" + Visible(original) + "\r\n解析基准目录：" + Visible(directory)
                + "\r\n原因：" + (characters.Count > 0 ? string.Join("；", characters)
                    : exception is PathTooLongException ? "路径长度超过当前系统支持范围。"
                    : string.IsNullOrWhiteSpace(original) ? "引用路径为空。"
                    : !Path.IsPathRooted(original) && string.IsNullOrWhiteSpace(directory) ? "相对引用缺少有效的所属图档目录。"
                    : "路径格式不符合Windows文件路径规则。")
                + "\r\n请在SolidWorks中核对该引用并保存后重试；所有引用都必须核对，系统不会跳过文件或按同名文件替换。";
            PluginDiagnosticLog.Record("路径诊断", message);
            throw new ArchiveReferencePathException(message, exception);
        }
    }

    private static List<string> InvalidCharacters(string path)
    {
        var result = new List<string>();
        if (path == null) return result;
        var invalid = Path.GetInvalidPathChars();
        for (var index = 0; index < path.Length; index++)
            if (invalid.Contains(path[index]))
                result.Add("第" + (index + 1) + "个字符「" + Visible(path[index].ToString()) + "」（U+" + ((int)path[index]).ToString("X4") + "）非法");
        return result;
    }

    internal static string Visible(string value)
    {
        if (value == null) return "（空）";
        var result = new StringBuilder();
        foreach (var character in value)
            if (char.IsControl(character)) result.Append("\\u").Append(((int)character).ToString("X4"));
            else result.Append(character);
        return result.ToString();
    }
}

internal sealed class ArchiveReferencePathException : InvalidOperationException
{
    public ArchiveReferencePathException(string message, Exception innerException) : base(message, innerException) { }
}
