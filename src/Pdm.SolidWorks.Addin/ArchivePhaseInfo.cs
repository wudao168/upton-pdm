using System;

namespace Upton.Pdm.SolidWorks;

internal static class ArchivePhaseInfo
{
    private static readonly string[] Hints = {
        "读取项目、设计树和引用，确认图档范围并分析变更；需要选择或确认时请在当前窗口操作。",
        "核对编辑权限，准备原生文件、工程图及上传副本；本阶段尚未产生存档版本。",
        "逐项校验文件、引用及服务器提交条件；全部通过后才开始上传。",
        "上传文件、等待服务器整批确认，再同步本地状态并清理临时读取；最终以存档结果为准。"
    };

    public static string Title(int phase) => ArchiveStepInfo.Title(phase == 1 ? 1 : phase == 2 ? 4 : phase == 3 ? 7 : 8);
    public static string Hint(int phase) => Title(phase) + "。提示：" + Hints[phase - 1];
    public static string Format(int phase, string message)
    {
        message = message ?? string.Empty;
        // The enclosing operation owns the phase. Nested reference checks can run
        // in more than one phase, so their legacy prefix must not move it backwards.
        for (var index = 1; index <= 4; index++)
        {
            var prefix = "阶段" + index + "/4：";
            if (message.StartsWith(prefix, StringComparison.Ordinal)) { message = message.Substring(prefix.Length); break; }
        }
        return Title(phase) + "｜" + message;
    }
}
