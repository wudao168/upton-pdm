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
    public List<Guid> ReservedDocumentIds { get; set; } = new List<Guid>();
}

internal static class ArchiveBatchJournal
{
    private static string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM", "archive-batches");

    public static void Begin(Guid batchId, Guid sessionId, string scope, string actor)
    {
        using (var process = Process.GetCurrentProcess())
        {
            var state = new InterruptedArchiveBatch { BatchId = batchId, SessionId = sessionId, Scope = scope, Actor = actor,
                ProcessId = process.Id, ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            Save(state);
        }
    }

    public static void AddReservedDocument(Guid batchId, Guid documentId)
    {
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        var state = new JavaScriptSerializer().Deserialize<InterruptedArchiveBatch>(File.ReadAllText(path, Encoding.UTF8));
        if (state == null || state.BatchId != batchId) throw new InvalidDataException("存档恢复记录与当前批次不一致。");
        if (state.ReservedDocumentIds == null) state.ReservedDocumentIds = new List<Guid>();
        if (state.ReservedDocumentIds.Contains(documentId)) return;
        state.ReservedDocumentIds.Add(documentId);
        Save(state);
    }

    private static void Save(InterruptedArchiveBatch state)
    {
        var path = Path.Combine(directory, state.BatchId.ToString("N") + ".json");
        var temporary = path + ".tmp";
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(state));
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }

    public static IEnumerable<InterruptedArchiveBatch> Interrupted(string scope, string actor) => ReadRecoverable(scope, actor, false);

    // A failed submission can leave its journal in the current SolidWorks process.
    // Only the owner (after its operation ends) may reconcile that live record.
    public static IEnumerable<InterruptedArchiveBatch> Recoverable(string scope, string actor) => ReadRecoverable(scope, actor, true);

    private static IEnumerable<InterruptedArchiveBatch> ReadRecoverable(string scope, string actor, bool includeCurrentProcess)
    {
        if (!Directory.Exists(directory)) yield break;
        using (var current = Process.GetCurrentProcess())
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
            if (!running || includeCurrentProcess && state.ProcessId == current.Id
                && state.ProcessStartUtcTicks == current.StartTime.ToUniversalTime().Ticks) yield return state;
        }
    }

    public static void Complete(Guid batchId)
    {
        ArchiveUploadJournal.CompleteBatch(batchId);
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
