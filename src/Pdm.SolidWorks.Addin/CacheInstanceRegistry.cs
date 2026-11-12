using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

// Cooperation is opt-in: an old or inaccessible CAD instance is never assumed idle.
internal sealed class CacheInstanceState
{
    public int ProcessId { get; set; }
    public long StartedUtcTicks { get; set; }
    public DateTime RecordedUtc { get; set; }
    public bool Busy { get; set; }
    public string[] Paths { get; set; } = Array.Empty<string>();
    public Guid[] CheckedOut { get; set; } = Array.Empty<Guid>();
}

internal sealed class CacheInstanceProtection
{
    public List<string> Paths { get; } = new List<string>();
    public List<Guid> CheckedOut { get; } = new List<Guid>();
    public List<int> UnconfirmedProcesses { get; } = new List<int>();
    public string Revision { get; set; }
    public string Notice => UnconfirmedProcesses.Count == 0 ? string.Empty
        : "另有 SolidWorks 实例的图档占用状态尚未确认（进程 " + string.Join("、", UnconfirmedProcesses)
        + "），相关缓存已保留；请在这些实例更新插件或正常退出后再次清理。";
}

internal static class CacheInstanceRegistry
{
    private static string Folder(string root) => Path.Combine(root, ".uplm", "CacheSessions");
    private static string GateName(string root)
    {
        string key;
        using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
        return @"Global\UPLM.CacheMaintenance." + key;
    }
    internal static T WithGate<T>(string root, Func<T> action)
    {
        using (var mutex = new Mutex(false, GateName(root)))
        {
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try { return action(); } finally { mutex.ReleaseMutex(); }
        }
    }
    internal static void Publish(string root, bool busy, string[] paths, Guid[] checkedOut) =>
        WithGate(root, () => { PublishCore(root, busy, paths, checkedOut); return true; });
    // Idle notifications run on the CAD UI thread. A heartbeat must never wait
    // behind a potentially long hash/deletion pass; stale state remains protected.
    internal static bool TryPublish(string root, bool busy, string[] paths, Guid[] checkedOut)
    {
        using (var mutex = new Mutex(false, GateName(root)))
        {
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return false;
                PublishCore(root, busy, paths, checkedOut);
                return true;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }
    private static void PublishCore(string root, bool busy, string[] paths, Guid[] checkedOut)
    {
        using (var process = Process.GetCurrentProcess())
        {
            var directory = Folder(root);
            ControlledCacheMaintenance.RequireOrdinaryPath(directory, root);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, process.Id + ".json");
            var temporary = file + ".tmp";
            var value = new CacheInstanceState { ProcessId = process.Id, StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                RecordedUtc = DateTime.UtcNow, Busy = busy, Paths = paths ?? Array.Empty<string>(), CheckedOut = checkedOut ?? Array.Empty<Guid>() };
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
        }
    }
    internal static CacheInstanceProtection Read(string root)
    {
        var result = new CacheInstanceProtection();
        var revisions = new List<string>();
        using (var current = Process.GetCurrentProcess())
        foreach (var process in Process.GetProcessesByName("SLDWORKS"))
        using (process)
        {
            if (process.Id == current.Id) continue;
            try
            {
                var file = Path.Combine(Folder(root), process.Id + ".json");
                ControlledCacheMaintenance.RequireOrdinaryPath(file, root);
                var text = File.ReadAllText(file);
                var state = new JavaScriptSerializer().Deserialize<CacheInstanceState>(text);
                if (!Matches(state, process.Id, process.StartTime.ToUniversalTime().Ticks, DateTime.UtcNow))
                    throw new InvalidDataException("实例保护状态尚未就绪。");
                result.Paths.AddRange(state.Paths ?? Array.Empty<string>());
                result.CheckedOut.AddRange(state.CheckedOut ?? Array.Empty<Guid>());
                // Heartbeats alone do not invalidate an otherwise unchanged protection set.
                revisions.Add(process.Id + ":" + string.Join("|", state.Paths ?? Array.Empty<string>()) + ":" + string.Join("|", state.CheckedOut ?? Array.Empty<Guid>()));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException
                || error is System.ComponentModel.Win32Exception || error is ArgumentException)
            {
                try { if (process.HasExited) continue; } catch { }
                result.UnconfirmedProcesses.Add(process.Id);
                revisions.Add(process.Id + ":unconfirmed");
            }
        }
        result.Revision = string.Join("\n", revisions.OrderBy(value => value, StringComparer.Ordinal));
        return result;
    }
    internal static bool Matches(CacheInstanceState state, int processId, long started, DateTime now) =>
        state != null && state.ProcessId == processId && state.StartedUtcTicks == started && !state.Busy
        && state.RecordedUtc <= now.AddSeconds(2) && state.RecordedUtc >= now.AddSeconds(-10);
}
