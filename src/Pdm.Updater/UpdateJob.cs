using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.Updater;

internal sealed class UpdateJob
{
    private readonly string root;
    private readonly ClientUpdateState state;
    private readonly string statePath;
    private readonly string candidate;
    private readonly string backup;
    private readonly string rejected;
    private readonly string program;
    private readonly string startupEntry;
    private readonly Func<bool> hostInUse;

    public UpdateJob(string root, ClientUpdateState state, Func<bool> hostInUse = null)
    {
        this.root = root;
        this.state = state;
        state.Target = ClientUpdateState.NormalizeTarget(state.Target);
        if (!Guid.TryParseExact(state.Id, "N", out _) || !string.Equals(
            ClientUpdateState.StateRoot(state.Component, state.Target), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新记录与安装目录不匹配。");
        var parent = Path.GetDirectoryName(state.Target);
        if (parent == null) throw new InvalidDataException("安装目录无效。");
        AssertOrdinaryPath(parent);
        AssertOrdinaryPath(state.Target);
        program = Path.Combine(state.Target, ClientUpdateState.ProgramName(state.Component));
        var prefix = ".uplm-" + state.Component + "-" + state.Id;
        candidate = Path.Combine(parent, prefix + "-new");
        backup = Path.Combine(parent, prefix + "-backup");
        rejected = Path.Combine(parent, prefix + "-rejected");
        startupEntry = "UPLM.Update." + Path.GetFileName(root);
        statePath = Path.Combine(root, "state.json");
        this.hostInUse = hostInUse ?? HasActiveHost;
    }

    private bool IsDesktop => state.Component == ClientUpdateState.Desktop;

    public void Run(string mode)
    {
        try
        {
            // Retain a separate lock beside the installation across Windows sessions.
            using var installLock = ClientUpdateState.TryLock(Path.Combine(Path.GetDirectoryName(state.Target), ".uplm-" + state.Component + "-update.lock"));
            if (installLock == null) return;
            if (state.RecoveryRequired)
            {
                if (state.Phase == ClientUpdateState.Verifying)
                    VerifyStartup(IsDesktop && !hostInUse());
                else Restore(IsDesktop && (mode == "restart" || mode == "recover"));
                return;
            }
            if (mode == "recover" && state.Phase != ClientUpdateState.Waiting)
            {
                if (state.Phase == ClientUpdateState.Complete || state.Phase == ClientUpdateState.Restored)
                    SetRecoveryAtSignIn(false);
                return;
            }
            if (!File.Exists(program)) throw new IOException("找不到需要更新的 UPLM 程序。");
            var current = ClientUpdateState.ReadVersion(state.Target);
            if (ClientUpdateState.TryVersion(current, out var installed)
                && ClientUpdateState.TryVersion(state.Version, out var next) && next <= installed)
            {
                Save(ClientUpdateState.Complete, "当前程序已安装此版本或更新版本。");
                SetRecoveryAtSignIn(false);
                if (IsDesktop && mode == "restart") using (StartDesktop()) { }
                return;
            }
            UpdateDownload.VerifySignature(state);
            // Only a verified hexadecimal digest is used as a path segment.
            if (state.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("更新包校验值无效。");
            var package = Path.Combine(root, state.Sha256.ToUpperInvariant() + ".zip");
            if (!UpdateDownload.HasExpectedHash(package, state.Sha256))
            {
                Save(ClientUpdateState.Downloading, "正在下载并校验更新。");
                UpdateDownload.Fetch(state, package, progress =>
                {
                    state.Progress = progress;
                    ClientUpdateState.Write(statePath, state);
                });
            }
            state.Progress = 100;
            if (mode == "download" && !state.InstallAfterDownload)
            {
                Save(ClientUpdateState.Ready, "更新已下载，UPLM 正常退出后自动安装。");
                var request = ClientUpdateState.Read<DesktopUpdateExitRequest>(Path.Combine(root, "exit-request.json"));
                if (request?.Id != state.Id && hostInUse()) return;
            }

            PrepareCandidate(package);
            SetRecoveryAtSignIn(true);
            Save(ClientUpdateState.Waiting, IsDesktop ? "等待 UPLM 退出后安装。" : "等待 SolidWorks 正常退出后安装插件。");
            if (!WaitUntilIdle(IsDesktop ? TimeSpan.FromSeconds(35) : TimeSpan.FromMinutes(10)))
            {
                if (IsDesktop && mode == "restart" && !hostInUse()) using (StartDesktop()) { }
                return;
            }
            if (!IsDesktop && state.Automatic && !ClientUpdateState.PluginAutomaticUpdatesEnabled())
            {
                Save(ClientUpdateState.Ready, "插件更新已下载，请在客户端中手动检查并安装。");
                SetRecoveryAtSignIn(false);
                return;
            }
            if (!string.Equals(current, ClientUpdateState.ReadVersion(state.Target), StringComparison.Ordinal))
                throw new IOException("等待期间安装版本已变化，将重新检查更新。");
            Save(ClientUpdateState.Switching, "正在替换已校验的程序文件。");
            MoveWithRetry(state.Target, backup);
            Directory.Move(candidate, state.Target);
            if (!File.Exists(program) || ClientUpdateState.ProductVersion(program) != state.Version)
                throw new InvalidDataException("替换后的程序版本校验失败。");
            if (IsDesktop) MoveLegacyDesktopProfile(backup);
            state.InstalledUtcTicks = DateTime.UtcNow.Ticks;
            Save(ClientUpdateState.Verifying, IsDesktop
                ? mode == "restart" ? "安装完成，正在确认客户端启动。" : "安装完成，等待下次启动 UPLM 确认。"
                : "安装完成，等待下次打开 SolidWorks 确认插件加载。");
            VerifyStartup(IsDesktop && mode == "restart");
        }
        catch (Exception error)
        {
            ClientUpdateState.Log(root, error);
            if (state.Phase == ClientUpdateState.Switching || state.Phase == ClientUpdateState.Restoring)
            {
                try { Restore(IsDesktop && mode == "restart"); }
                catch (Exception restoreError) { ClientUpdateState.Log(root, restoreError); }
            }
            else if (!state.RecoveryRequired)
            {
                state.RetryUtcTicks = DateTime.UtcNow.AddMinutes(2).Ticks;
                Save(ClientUpdateState.Failed, error is InvalidDataException
                    ? error.Message : "更新暂未完成，请检查网络、磁盘空间或目录权限后重试。");
                DeleteStaging(candidate);
                SetRecoveryAtSignIn(false);
                if (IsDesktop && mode == "restart" && File.Exists(program)) StartDesktop();
            }
        }
    }

    private void PrepareCandidate(string package)
    {
        DeleteStaging(candidate);
        Expand(package, candidate);
        var nestedUi = Path.Combine(candidate, "uplm-ui.zip");
        var ui = Path.Combine(candidate, "ui");
        if (!IsDesktop && !Directory.Exists(ui) && File.Exists(nestedUi))
            Expand(nestedUi, ui);
        var next = Path.Combine(candidate, ClientUpdateState.ProgramName(state.Component));
        if (!File.Exists(next) || ClientUpdateState.ProductVersion(next) != state.Version
            || ClientUpdateState.ReadVersion(candidate) != state.Version
            || (IsDesktop && !File.Exists(Path.Combine(candidate, ClientUpdateState.UpdaterName))))
            throw new InvalidDataException("更新包内的程序、更新组件或版本标记不完整，请联系管理员重新发布。");
        foreach (var page in new[] { "index.html", "review-overlay.html", "server-settings.html", "client-bootstrap.json" })
            if (!File.Exists(Path.Combine(candidate, "ui", page)))
                throw new InvalidDataException("更新包缺少离线界面资源，请联系管理员重新发布。");

        var locator = Path.Combine(state.Target, "uplm-bootstrap.json");
        if (File.Exists(locator)) File.Copy(locator, Path.Combine(candidate, "uplm-bootstrap.json"), true);
    }

    private static void Expand(string package, string destination)
    {
        Directory.CreateDirectory(destination);
        var prefix = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        using var zip = ZipFile.OpenRead(package);
        foreach (var entry in zip.Entries)
        {
            var file = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(":"))
                throw new InvalidDataException("更新包包含无效文件路径。");
            total += entry.Length;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包解压大小超过限制。");
            if (entry.Name.Length == 0) Directory.CreateDirectory(file);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                entry.ExtractToFile(file, false);
            }
        }
    }

    private void VerifyStartup(bool startDesktop)
    {
        if (ClientUpdateState.ReadVersion(state.Target) != state.Version)
        {
            Save(ClientUpdateState.Failed, "安装版本已变化，请重新检查更新。");
            SetRecoveryAtSignIn(false);
            return;
        }
        var receiptPath = Path.Combine(root, "startup.json");
        Process launched = null;
        try { if (startDesktop) launched = StartDesktop(); }
        catch (Exception error)
        {
            ClientUpdateState.Log(root, error);
            Restore(true);
            return;
        }
        using var launchedProcess = launched;
        var until = DateTime.UtcNow.Add(IsDesktop ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(2));
        while (DateTime.UtcNow < until)
        {
            var receipt = ClientUpdateState.Read<ClientUpdateReceipt>(receiptPath);
            if (receipt?.Id == state.Id && receipt.Version == state.Version)
            {
                if (!receipt.Healthy) { Restore(startDesktop); return; }
                Save(ClientUpdateState.Complete, IsDesktop ? "客户端已更新并启动成功。" : "插件已更新并成功加载。");
                SetRecoveryAtSignIn(false);
                DeleteStaging(backup);
                return;
            }
            if (launched != null && launched.HasExited) break;
            if (!IsDesktop && ObservePluginHost()) { Restore(false); return; }
            Thread.Sleep(500);
        }
        if (startDesktop)
        {
            Save(ClientUpdateState.Restoring, "新版本未确认启动，退出 UPLM 后将恢复旧版。");
            Restore(true);
        }
    }

    private bool ObservePluginHost()
    {
        using var startup = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\SOLIDWORKS\AddInsStartup\" + ClientUpdateState.AddinGuid);
        if (!Equals(startup?.GetValue(null), 1)) return false;
        using var registration = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + ClientUpdateState.AddinGuid + @"\InprocServer32");
        var codeBase = registration?.GetValue("CodeBase") as string;
        if (!Uri.TryCreate(codeBase, UriKind.Absolute, out var location) || !location.IsFile
            || !string.Equals(location.LocalPath, program, StringComparison.OrdinalIgnoreCase)) return false;
        var processes = Process.GetProcessesByName("SLDWORKS");
        try
        {
            var host = processes.FirstOrDefault(p => !p.HasExited && p.StartTime.ToUniversalTime().Ticks >= state.InstalledUtcTicks);
            if (host == null) return state.ObservedHostId != 0;
            if (state.ObservedHostId != host.Id || state.ObservedHostStartTicks != host.StartTime.ToUniversalTime().Ticks)
            {
                state.ObservedHostId = host.Id;
                state.ObservedHostStartTicks = host.StartTime.ToUniversalTime().Ticks;
                ClientUpdateState.Write(statePath, state);
            }
            return false;
        }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void Restore(bool restartDesktop)
    {
        Save(ClientUpdateState.Restoring, "新版验证未通过，等待程序退出后恢复旧版。");
        if (!WaitUntilIdle(TimeSpan.FromSeconds(35))) return;
        if (Directory.Exists(backup))
        {
            if (Directory.Exists(state.Target)) Directory.Move(state.Target, rejected);
            Directory.Move(backup, state.Target);
            if (IsDesktop)
            {
                var legacy = Path.Combine(state.Target, "Upton.Pdm.Desktop.exe.WebView2");
                var profile = Path.Combine(root, "webview2");
                if (!Directory.Exists(legacy) && Directory.Exists(profile))
                {
                    try { Directory.Move(profile, legacy); }
                    catch (IOException) { }
                }
            }
        }
        if (!File.Exists(program)) throw new IOException("找不到可恢复的 UPLM 程序。");
        state.RetryUtcTicks = 0;
        Save(ClientUpdateState.Restored, "更新未完成，已恢复上一个可用版本。");
        SetRecoveryAtSignIn(false);
        DeleteStaging(candidate);
        DeleteStaging(rejected);
        if (restartDesktop) using (StartDesktop()) { }
    }

    private void MoveLegacyDesktopProfile(string previous)
    {
        var legacy = Path.Combine(previous, "Upton.Pdm.Desktop.exe.WebView2");
        var profile = Path.Combine(root, "webview2");
        if (!Directory.Exists(legacy) || Directory.Exists(profile)) return;
        AssertOrdinaryPath(legacy);
        Directory.Move(legacy, profile);
    }

    private Process StartDesktop() => Process.Start(new ProcessStartInfo(program)
    {
        WorkingDirectory = state.Target,
        UseShellExecute = false
    });

    private void SetRecoveryAtSignIn(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            using var process = Process.GetCurrentProcess();
            var command = "\"" + process.MainModule.FileName + "\" recover";
            run.SetValue(startupEntry, command);
        }
        else run.DeleteValue(startupEntry, false);
    }

    private bool WaitUntilIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        do
        {
            if (!hostInUse()) return true;
            Thread.Sleep(750);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private bool HasActiveHost()
    {
        var processes = Process.GetProcessesByName(IsDesktop ? "Upton.Pdm.Desktop" : "SLDWORKS");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    if (!IsDesktop || string.Equals(process.MainModule.FileName, program, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Win32Exception) { return true; }
                catch (InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void Save(string phase, string message)
    {
        var previousPhase = state.Phase;
        var previousMessage = state.Message;
        state.Phase = phase;
        state.Message = message;
        try { ClientUpdateState.Write(statePath, state); }
        catch
        {
            state.Phase = previousPhase;
            state.Message = previousMessage;
            throw;
        }
    }

    private static void AssertOrdinaryPath(string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新目录不能使用目录链接。");
    }

    private static void DeleteStaging(string path)
    {
        if (!Directory.Exists(path)) return;
        AssertOrdinaryPath(path);
        Directory.Delete(path, true);
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (attempt < 12) { Thread.Sleep(500); }
        }
    }
}
