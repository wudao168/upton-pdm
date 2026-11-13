#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace Upton.Pdm.ClientShared;

internal static class ClientPackageUpdater
{
    public static string GetInstalledVersion(string directory) => ClientUpdateState.ReadVersion(directory);

    public static string GetLoadedVersion(Assembly assembly) =>
        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];

    public static bool IsUpdateAvailable(string installed, string available) =>
        ClientUpdateState.TryVersion(available, out var proposed)
        && (string.IsNullOrWhiteSpace(installed) || (ClientUpdateState.TryVersion(installed, out var current) && proposed > current));

    public static ClientUpdateState GetStatus(string component, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var root = ClientUpdateState.StateRoot(component, target);
        var state = ClientUpdateState.Read<ClientUpdateState>(Path.Combine(root, "state.json"));
        if (state?.Phase == ClientUpdateState.Downloading && !IsRunning(root))
        {
            state.Phase = ClientUpdateState.Failed;
            state.Message = "下载已中断，下次检查时将继续下载。";
        }
        return state;
    }

    private static bool IsRunning(string root)
    {
        if (!Directory.Exists(root)) return false;
        using var lease = ClientUpdateState.TryLock(Path.Combine(root, "worker.lock"));
        return lease == null;
    }

    public static bool QueueDownload(string component, ClientPackageConfiguration package, string target, bool automatic)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.PackageUrl)
            || !IsUpdateAvailable(GetInstalledVersion(target), package.Version))
            return false;
        var install = component == ClientUpdateState.Addin;
        return Request(component, target, "download", (state, canonical) =>
        {
            if (state?.RecoveryRequired == true) return null;
            if (state?.Version == package.Version && state.Sha256 == package.Sha256 && state.Signature == package.Signature)
            {
                if (state.Phase == ClientUpdateState.Ready || state.Phase == ClientUpdateState.Waiting)
                    return install ? PrepareInstall(state, automatic) : null;
                if ((automatic && state.Phase == ClientUpdateState.Restored) || (automatic && state.RetryUtcTicks > DateTime.UtcNow.Ticks))
                    return null;
                if (!install && state.RetryUtcTicks > DateTime.UtcNow.Ticks) return null;
            }
            return new ClientUpdateState
            {
                Id = Guid.NewGuid().ToString("N"), Component = component, Target = canonical,
                Version = package.Version, PackageUrl = package.PackageUrl, Sha256 = package.Sha256,
                Signature = package.Signature, InstallAfterDownload = install, Automatic = automatic,
                Phase = ClientUpdateState.Downloading, Message = "正在下载并校验更新。"
            };
        });
    }

    private static ClientUpdateState PrepareInstall(ClientUpdateState state, bool automatic)
    {
        if (state?.Phase != ClientUpdateState.Ready && state?.Phase != ClientUpdateState.Waiting) return null;
        state.Phase = ClientUpdateState.Waiting;
        state.InstallAfterDownload = true;
        state.Automatic = automatic;
        state.Message = "更新已就绪，等待程序退出后安装。";
        return state;
    }

    public static bool TryLaunchDesktopUpdate(string target, bool restart = true)
    {
        return Request(ClientUpdateState.Desktop, target, restart ? "restart" : "exit", (state, _) =>
        {
            if (state?.Phase == ClientUpdateState.Switching || state?.Phase == ClientUpdateState.Restoring)
                return state;
            return PrepareInstall(state, false);
        });
    }

    public static void ScheduleDesktopUpdateOnExit(string target)
    {
        var root = ClientUpdateState.StateRoot(ClientUpdateState.Desktop, target);
        try
        {
            var state = ClientUpdateState.Read<ClientUpdateState>(Path.Combine(root, "state.json"));
            if (state?.Phase == ClientUpdateState.Downloading || state?.Phase == ClientUpdateState.Ready
                || state?.Phase == ClientUpdateState.Waiting)
            {
                // A download already in progress can finish and install after this process exits.
                ClientUpdateState.Write(Path.Combine(root, "exit-request.json"), new DesktopUpdateExitRequest { Id = state.Id });
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (TryLaunchDesktopUpdate(target, restart: false)) return;
                if (GetStatus(ClientUpdateState.Desktop, target)?.Phase != ClientUpdateState.Ready) return;
                Thread.Sleep(50);
            }
        }
        catch (Exception error) { ClientUpdateState.Log(root, error); }
    }

    public static void ResumeRecovery(string component, string target)
    {
        if (!string.IsNullOrWhiteSpace(target))
            Request(component, target, "recover", (state, _) => state?.RecoveryRequired == true ? state : null);
    }

    public static bool DesktopInstallationInProgress(string target)
    {
        var state = GetStatus(ClientUpdateState.Desktop, target);
        return (state?.Phase == ClientUpdateState.Waiting || state?.Phase == ClientUpdateState.Switching
                || state?.Phase == ClientUpdateState.Restoring)
            && IsRunning(ClientUpdateState.StateRoot(ClientUpdateState.Desktop, target));
    }

    private static bool Request(string component, string target, string mode,
        Func<ClientUpdateState, string, ClientUpdateState> prepare)
    {
        var canonical = ClientUpdateState.NormalizeTarget(target);
        var root = ClientUpdateState.StateRoot(component, canonical);
        var path = Path.Combine(root, "state.json");
        try
        {
            Directory.CreateDirectory(root);
            using var request = ClientUpdateState.TryLock(Path.Combine(root, "request.lock"));
            if (request == null || IsRunning(root)) return false;
            var state = prepare(ClientUpdateState.Read<ClientUpdateState>(path), canonical);
            if (state == null) return false;
            ClientUpdateState.Write(path, state);
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ClientUpdateState.UpdaterName);
            if (!File.Exists(source))
                throw new FileNotFoundException("缺少更新组件，请使用 UPLM 安装修复。", source);
            var executable = CacheUpdater(source, root);
            var token = Guid.NewGuid().ToString("N");
            using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\UPLM.Update." + token);
            using var parent = Process.GetCurrentProcess();
            var arguments = string.Join(" ", mode, Quote(root), token, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            using var worker = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            });
            if (worker == null) throw new InvalidOperationException("无法启动 UPLM 更新组件。");
            // The worker acquires its own lock before acknowledging the request.
            request.Dispose();
            return signal.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (Exception error)
        {
            ClientUpdateState.Log(root, error);
            return false;
        }
    }

    internal static string Quote(string path) => "\"" + path + "\"";

    private static string CacheUpdater(string source, string root)
    {
        using var input = File.OpenRead(source);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        var folder = Path.Combine(root, "worker-" + hash);
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, ClientUpdateState.UpdaterName);
        if (File.Exists(executable))
        {
            using var cached = File.OpenRead(executable);
            if (BitConverter.ToString(sha.ComputeHash(cached)).Replace("-", "") == hash) return executable;
        }
        input.Position = 0;
        var temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temporary)) { input.CopyTo(output); output.Flush(true); }
            if (File.Exists(executable)) File.Replace(temporary, executable, null);
            else File.Move(temporary, executable);
            foreach (var previous in Directory.EnumerateDirectories(root, "worker-*"))
            {
                if (string.Equals(previous, folder, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(Path.Combine(previous, ClientUpdateState.UpdaterName)); Directory.Delete(previous); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return executable;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ReportStartup(string component, string target, string loadedVersion, bool healthy)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var root = ClientUpdateState.StateRoot(component, target);
        try
        {
            var state = ClientUpdateState.Read<ClientUpdateState>(Path.Combine(root, "state.json"));
            if (state?.Phase == ClientUpdateState.Verifying && string.Equals(state.Version, loadedVersion, StringComparison.OrdinalIgnoreCase))
                ClientUpdateState.Write(Path.Combine(root, "startup.json"),
                    new ClientUpdateReceipt { Id = state.Id, Version = loadedVersion, Healthy = healthy });
        }
        catch (Exception error) { ClientUpdateState.Log(root, error); }
    }
}
