using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal static class PluginDiagnosticLog
{
    private static readonly object Sync = new object();
    private static readonly Queue<string> Entries = new Queue<string>();
    private static string lastMessage;
    private static int revision;
    public static int Revision { get { lock (Sync) return revision; } }

    public static void Record(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Upton.Pdm.ClientShared.ClientDiagnostics.Record(category == "失败" || category == "异常" ? "error" : "runtime", category, message);
        var safe = Redact(message);
        if (safe.Length > 4000) safe = safe.Substring(0, 4000) + "\r\n……详细内容请导出诊断日志。";
        lock (Sync)
        {
            var content = "【" + category + "】" + safe;
            if (content == lastMessage) return;
            lastMessage = content;
            Entries.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + content);
            while (Entries.Count > 200) Entries.Dequeue();
            revision++;
        }
    }

    public static string Snapshot()
    {
        lock (Sync) return string.Join(Environment.NewLine + Environment.NewLine, Entries);
    }

    internal static string Redact(string text) => Upton.Pdm.ClientShared.ClientLogRedaction.Clean(text, false);

    public static string BuildReport(string version, string directory)
    {
        var report = new StringBuilder();
        report.AppendLine("UPLM SolidWorks 插件诊断日志");
        report.AppendLine("导出时间：" + DateTimeOffset.Now.ToString("O"));
        report.AppendLine("插件版本：" + version);
        report.AppendLine("实际加载的插件文件：" + typeof(PdmAddin).Assembly.Location);
        report.AppendLine("运行程序集标识：" + typeof(PdmAddin).Assembly.ManifestModule.ModuleVersionId.ToString("D"));
        report.AppendLine("系统：" + Environment.OSVersion + "；进程位数：" + (Environment.Is64BitProcess ? "64" : "32"));
        report.AppendLine("请补充：发生时间、项目号、图档名、操作步骤和实际现象。");
        report.AppendLine("\r\n===== 本次运行的操作记录 =====\r\n" + Snapshot());
        foreach (var name in new[] { "addin-checkin.log", "addin-errors.log", "addin-operations.log", "addin-scan.log" })
        {
            foreach (var suffix in new[] { ".1", "" })
            {
                var path = Path.Combine(directory, name + suffix);
                if (!File.Exists(path)) continue;
                report.AppendLine("\r\n===== " + name + suffix + "（最多末尾 1 MB） =====");
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var remaining = Math.Min(stream.Length, 1024 * 1024);
                        stream.Position = stream.Length - remaining;
                        var truncated = stream.Position > 0;
                        var bytes = new byte[(int)remaining];
                        var count = 0;
                        while (count < bytes.Length)
                        {
                            var read = stream.Read(bytes, count, bytes.Length - count);
                            if (read == 0) break;
                            count += read;
                        }
                        var text = Encoding.UTF8.GetString(bytes, 0, count);
                        if (truncated)
                        {
                            var newline = text.IndexOf('\n');
                            text = newline >= 0 ? text.Substring(newline + 1) : string.Empty;
                        }
                        report.AppendLine(Redact(text));
                    }
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    report.AppendLine("该日志暂时无法读取，其余记录已保留。请稍后重新导出。");
                }
            }
        }
        return report.ToString();
    }
}
