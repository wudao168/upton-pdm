using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Upton.Pdm.SolidWorks.PreviewWorker;

// SOLIDWORKS reads the journal preference at startup. Restore it once this private instance is ready.
internal sealed class PreviewJournalScope : IDisposable
{
    private const string ValueName = "SolidWorks Journal Folders";
    private const string RecordName = "journal-setting.json";
    private readonly Mutex gate = new Mutex(false, @"Local\UPLM.NativeArchiveJournalStartup");
    private readonly string registryPath;
    private readonly string root;
    private bool entered;
    private Setting setting;
    public string WorkingDirectory { get; private set; }
    public string JournalDirectory => setting?.Directory;

    private PreviewJournalScope(string registryPath, string root) { this.registryPath = registryPath; this.root = root; }

    public static PreviewJournalScope Begin(string executable)
    {
        var major = FileVersionInfo.GetVersionInfo(executable).FileMajorPart;
        if (major < 20 || major > 100) throw new InvalidOperationException("无法确认SolidWorks日志设置所属版本，请检查程序安装。");
        return BeginAt(@"Software\SolidWorks\SOLIDWORKS " + (major + 1992) + @"\ExtReferences",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "preview-agent", "solidworks-sessions"));
    }

    private static PreviewJournalScope BeginAt(string registryPath, string root)
    {
        var scope = new PreviewJournalScope(registryPath, Path.GetFullPath(root));
        try
        {
            try { scope.entered = scope.gate.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { scope.entered = true; }
            if (!scope.entered) throw new InvalidOperationException("其他SolidWorks专用会话仍在启动，请稍后重试。");
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
            {
                var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                // A killed worker cannot execute finally. Recover only a record owned by this component.
                if (scope.IsPrivateDirectory(current as string))
                {
                    var previous = Path.Combine(Path.GetDirectoryName((string)current), RecordName);
                    if (!File.Exists(previous)) throw new InvalidOperationException("专用日志目录的恢复记录缺失，请检查转图代理诊断日志。");
                    scope.Restore(key, new JavaScriptSerializer().Deserialize<Setting>(File.ReadAllText(previous, Encoding.UTF8)));
                }
                var original = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = original == null ? RegistryValueKind.String : key.GetValueKind(ValueName);
                if (original != null && (!(original is string) || kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                    throw new InvalidOperationException("SolidWorks日志目录设置类型异常，已停止启动，请检查转图代理诊断日志。");
                scope.WorkingDirectory = Path.Combine(scope.root, Guid.NewGuid().ToString("N"));
                var directory = Path.Combine(scope.WorkingDirectory, "journal");
                Directory.CreateDirectory(directory);
                using (var probe = new FileStream(Path.Combine(directory, "write-check.tmp"), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1, FileOptions.DeleteOnClose)) { probe.WriteByte(0); probe.Flush(true); }
                scope.setting = new Setting { RegistryPath = registryPath, Original = original as string, Existed = original != null, Kind = kind, Directory = directory };
                File.WriteAllText(Path.Combine(scope.WorkingDirectory, RecordName), new JavaScriptSerializer().Serialize(scope.setting), new UTF8Encoding(false));
                key.SetValue(ValueName, directory, RegistryValueKind.String);
                if (!Equals(key.GetValue(ValueName), directory)) throw new IOException("SolidWorks专用日志目录设置未生效。");
                Console.Error.WriteLine("SolidWorks日志目录已隔离；原目录=" + (original ?? "默认") + "；专用目录=" + directory + "；写入检查=通过");
            }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private bool IsPrivateDirectory(string value) => !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(value) == "journal" && Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(value)), "N", out _);

    private void Restore(RegistryKey key, Setting record)
    {
        if (record == null || record.RegistryPath != registryPath || !IsPrivateDirectory(record.Directory)
            || record.Kind != RegistryValueKind.String && record.Kind != RegistryValueKind.ExpandString)
            throw new InvalidOperationException("SolidWorks专用日志恢复记录不一致，未改写其他设置。");
        var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (!string.Equals(current as string, record.Directory, StringComparison.OrdinalIgnoreCase)) return;
        if (record.Existed) key.SetValue(ValueName, record.Original, record.Kind); else key.DeleteValue(ValueName, false);
        if (!Equals(key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), record.Original)
            || record.Existed && key.GetValueKind(ValueName) != record.Kind) throw new IOException("SolidWorks原日志目录尚未恢复，请检查代理诊断日志。");
        Console.Error.WriteLine("SolidWorks原日志目录已恢复。专用会话继续使用独立日志。");
    }

    public void Dispose()
    {
        try { if (entered && setting != null) using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) Restore(key, setting); }
        finally { if (entered) { gate.ReleaseMutex(); entered = false; } gate.Dispose(); }
    }

    private sealed class Setting
    {
        public string RegistryPath { get; set; }
        public string Original { get; set; }
        public bool Existed { get; set; }
        public RegistryValueKind Kind { get; set; }
        public string Directory { get; set; }
    }
}
