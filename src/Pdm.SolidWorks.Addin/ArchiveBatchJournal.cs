using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class InterruptedArchiveBatch
{
    public Guid BatchId { get; set; }
    public Guid SessionId { get; set; }
    public string Scope { get; set; }
    public string Actor { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
}

internal static class ArchiveBatchJournal
{
    private static string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM", "archive-batches");

    public static void Begin(Guid batchId, Guid sessionId, string scope, string actor)
    {
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        var temporary = path + ".tmp";
        using (var process = Process.GetCurrentProcess())
        {
            var state = new InterruptedArchiveBatch { BatchId = batchId, SessionId = sessionId, Scope = scope, Actor = actor,
                ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            Directory.CreateDirectory(directory);
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(state));
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }

    public static IEnumerable<InterruptedArchiveBatch> Interrupted(string scope, string actor)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            InterruptedArchiveBatch state;
            try { state = new JavaScriptSerializer().Deserialize<InterruptedArchiveBatch>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { PluginDiagnosticLog.Record("批次", "本机批次记录无法读取，已保留记录：" + Path.GetFileName(path)); continue; }
            if (state == null || state.BatchId == Guid.Empty || state.SessionId == Guid.Empty
                || !string.Equals(Path.GetFileNameWithoutExtension(path), state.BatchId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                || state.Scope != scope || !string.Equals(state.Actor, actor, StringComparison.OrdinalIgnoreCase)) continue;
            var running = false;
            try { using (var process = Process.GetProcessById(state.ProcessId)) running = !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == state.ProcessStartUtcTicks; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { running = true; }
            if (!running) yield return state;
        }
    }

    public static void Complete(Guid batchId)
    {
        ArchiveUploadJournal.CompleteBatch(batchId);
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
