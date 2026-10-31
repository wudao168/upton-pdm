using System;

namespace Upton.Pdm.SolidWorks;

// Presentation steps do not change the four transactional workflow phases.
internal static class ArchiveStepInfo
{
    private static readonly string[] Names = { "读取文件清单", "确认提交范围", "核对引用与配置", "核对版本与权限",
        "预留图档身份", "准备上传副本", "固定快照与最终校验", "上传文件", "整批提交", "核对结果与刷新状态" };
    private static readonly string[] Hints = { "正在读取本次图档与结构清单。", "请选择图档并确认归属项目。", "核对本次图档及必要引用。",
        "核对文件内容、编辑权限和版本。", "准备本批图档身份，此时尚未生成存档版本。", "准备已确认图档的上传副本。",
        "固定引用版本并检查提交条件，全部通过后才上传。", "文件上传后仍需等待整批提交确认。", "正在等待服务器整批确认，最终以存档结果为准。",
        "根据服务器确认结果更新本地图档状态。" };
    internal static string Title(int step)
    { step = Math.Max(1, Math.Min(10, step)); return "步骤" + step + "/10：" + Names[step - 1]; }
    internal static string Hint(int step) => Title(step) + "。提示：" + Hints[Math.Max(1, Math.Min(10, step)) - 1];
    internal static string Bytes(long value) => value >= 1024L * 1024 * 1024 ? (value / (1024d * 1024 * 1024)).ToString("0.00") + " GB"
        : value >= 1024 * 1024 ? (value / (1024d * 1024)).ToString("0.0") + " MB"
        : value >= 1024 ? (value / 1024d).ToString("0.0") + " KB" : value + " B";
    internal static string Format(int step, string message)
    {
        message = message ?? string.Empty;
        for (var phase = 1; phase <= 4; phase++)
        {
            var prefix = "阶段" + phase + "/4：";
            if (message.StartsWith(prefix, StringComparison.Ordinal)) { message = message.Substring(prefix.Length); break; }
        }
        return Title(step) + "｜" + message;
    }
}
