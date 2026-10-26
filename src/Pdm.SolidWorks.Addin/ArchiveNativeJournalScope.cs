using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Upton.Pdm.SolidWorks;

// SOLIDWORKS reads this documented preference during startup. Give only the
// private reader a unique journal, then restore the engineer's preference.
internal sealed class ArchiveNativeJournalScope : IDisposable
{
    private const string ValueName = "SolidWorks Journal Folders";
    private const string RecordName = "native-journal-setting.json";
    private readonly string job;
    private readonly Mutex gate = new Mutex(false, @"Local\UPLM.NativeArchiveJournalStartup");
    private bool entered;

    private ArchiveNativeJournalScope(string job) { this.job = job; }

    internal static ArchiveNativeJournalScope Begin(string job, string executable)
    {
        if (!ArchiveNativeProcessWorkspace.IsJob(job)) throw new InvalidOperationException("专用日志目录不属于本次存档，尚未上传。");
        var scope = new ArchiveNativeJournalScope(job);
        try
        {
            scope.Enter();
            var path = SettingsPath(executable);
            using (var key = Registry.CurrentUser.OpenSubKey(path, true))
            {
                if (key == null) throw new InvalidOperationException("无法读取 SolidWorks 日志目录设置，原工作会话已保留，尚未上传。");
                var current = NormalizeDirectory(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                // A terminated worker may have missed its finally block.
                if (!string.IsNullOrWhiteSpace(current) && Path.GetFileName(current) == "journal")
                {
                    var previous = Path.GetDirectoryName(current);
                    if (ArchiveNativeProcessWorkspace.IsJob(previous)) Restore(previous, path);
                }
                var original = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = original == null ? RegistryValueKind.String : key.GetValueKind(ValueName);
                if (original != null && (original is not string || kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                    throw new InvalidOperationException("SolidWorks 日志目录设置类型异常，未修改该设置，尚未上传。");
                var directory = Path.Combine(job, "journal"); Directory.CreateDirectory(directory);
                var record = new Setting { RegistryPath = path, Original = original as string, Existed = original != null, Kind = kind, Directory = directory };
                File.WriteAllText(Path.Combine(job, RecordName), Json().Serialize(record));
                key.SetValue(ValueName, directory, RegistryValueKind.String);
                if (!Equals(key.GetValue(ValueName), directory)) throw new IOException("专用日志目录设置未生效，尚未上传。");
            }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    internal static void Recover(string job)
    {
        if (!ArchiveNativeProcessWorkspace.IsJob(job) || !File.Exists(Path.Combine(job, RecordName))) return;
        using (var scope = new ArchiveNativeJournalScope(job)) scope.Enter();
    }

    private void Enter()
    {
        try { entered = gate.WaitOne(TimeSpan.FromMinutes(2)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new InvalidOperationException("其他专用会话仍在启动，请稍后重试；原图档和窗口已保留。");
    }

    public void Dispose()
    {
        try
        {
            if (entered && File.Exists(Path.Combine(job, RecordName)))
            {
                var request = Json().Deserialize<ArchiveNativeProcessWorkspace.Request>(File.ReadAllText(Path.Combine(job, "native-isolation-request.json")));
                Restore(job, SettingsPath(request.SolidWorksExecutable));
            }
        }
        finally { if (entered) { gate.ReleaseMutex(); entered = false; } gate.Dispose(); }
    }

    private static string SettingsPath(string executable)
    {
        var major = FileVersionInfo.GetVersionInfo(executable).FileMajorPart;
        if (major < 20 || major > 100) throw new InvalidOperationException("无法确认 SolidWorks 日志设置所属版本，尚未上传。");
        return @"Software\SolidWorks\SOLIDWORKS " + (major + 1992) + @"\ExtReferences";
    }

    private static void Restore(string job, string expectedPath)
    {
        var recordPath = Path.Combine(job, RecordName);
        if (!File.Exists(recordPath)) return;
        var record = Json().Deserialize<Setting>(File.ReadAllText(recordPath));
        if (record.RegistryPath != expectedPath || record.Directory != Path.Combine(job, "journal")
            || record.Kind != RegistryValueKind.String && record.Kind != RegistryValueKind.ExpandString)
            throw new InvalidOperationException("专用日志恢复记录不一致，未改写其他设置。");
        using (var key = Registry.CurrentUser.OpenSubKey(expectedPath, true))
        {
            if (key == null) throw new IOException("无法恢复 SolidWorks 日志目录，请查看本次操作日志。");
            var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (SameDirectory(current as string, record.Directory))
            {
                if (record.Existed) key.SetValue(ValueName, record.Original, record.Kind);
                else key.DeleteValue(ValueName, false);
                current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!Equals(current, record.Original) || record.Existed && key.GetValueKind(ValueName) != record.Kind)
                    throw new IOException("SolidWorks 原日志目录尚未恢复，未继续提交存档。");
            }
            // If someone changed the preference concurrently, preserve their value.
            File.WriteAllText(Path.Combine(job, "native-journal-restored.json"), Json().Serialize(new
            { Restored = Equals(current, record.Original), ConcurrentPreferenceKept = !Equals(current, record.Original) }));
        }
    }

    private static bool SameDirectory(string left, string right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        value = value.Replace('/', '\\').TrimEnd('\\');
        var profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        var symbolic = Path.Combine(Path.GetDirectoryName(profile), "CURRENT_USER");
        if (value.StartsWith(symbolic + "\\", StringComparison.OrdinalIgnoreCase)) value = profile + value.Substring(symbolic.Length);
        return value;
    }

    private static JavaScriptSerializer Json() => new JavaScriptSerializer();
    private sealed class Setting
    {
        public string RegistryPath { get; set; }
        public string Original { get; set; }
        public bool Existed { get; set; }
        public RegistryValueKind Kind { get; set; }
        public string Directory { get; set; }
    }
}
