using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
    public PreparedArchiveBatch Prepared { get; set; }
}

internal sealed class PreparedArchiveBatch
{
    public Guid ProjectId { get; set; }
    public string ProjectCode { get; set; }
    public string RootPath { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public string PayloadHash { get; set; }
    public PreparedArchiveSource[] Files { get; set; }
    public string[] ConfirmedSourcePaths { get; set; }
}

internal sealed class PreparedArchiveSource
{
    public Guid DocumentId { get; set; }
    public string SourcePath { get; set; }
    public string SourceHash { get; set; }
    public bool MemorySnapshot { get; set; }
    public bool SyncIdentity { get; set; }
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
        var state = ReadState(batchId);
        if (state.ReservedDocumentIds == null) state.ReservedDocumentIds = new List<Guid>();
        if (state.ReservedDocumentIds.Contains(documentId)) return;
        state.ReservedDocumentIds.Add(documentId);
        Save(state);
    }

    public static void Prepare(Guid batchId, PreparedArchiveBatch prepared, string json)
    {
        var state = ReadState(batchId);
        var payload = Encoding.UTF8.GetBytes(json);
        prepared.PayloadHash = Hash(payload);
        prepared.PreparedAtUtc = DateTime.UtcNow;
        // Publish the small journal only after the complete request is durable.
        WriteAtomically(PayloadPath(batchId), payload);
        state.Prepared = prepared;
        Save(state);
    }

    public static string ReadPayload(InterruptedArchiveBatch state)
    {
        var bytes = File.ReadAllBytes(PayloadPath(state.BatchId));
        if (!string.Equals(state.Prepared?.PayloadHash, Hash(bytes), StringComparison.Ordinal))
            throw new InvalidDataException("原批次提交清单校验未通过。");
        return Encoding.UTF8.GetString(bytes);
    }

    public static FileStream TryLock(Guid batchId)
    {
        try { return new FileStream(Path.Combine(directory, batchId.ToString("N") + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { return null; }
    }

    private static string PayloadPath(Guid batchId) => Path.Combine(directory, batchId.ToString("N") + ".payload");

    private static string Hash(byte[] payload)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(payload));
    }

    private static InterruptedArchiveBatch ReadState(Guid batchId)
    {
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        var state = CreateSerializer().Deserialize<InterruptedArchiveBatch>(File.ReadAllText(path, Encoding.UTF8));
        if (state == null || state.BatchId != batchId) throw new InvalidDataException("存档恢复记录与当前批次不一致。");
        return state;
    }

    private static void Save(InterruptedArchiveBatch state)
    {
        var path = Path.Combine(directory, state.BatchId.ToString("N") + ".json");
        WriteAtomically(path, Encoding.UTF8.GetBytes(CreateSerializer().Serialize(state)));
    }

    private static JavaScriptSerializer CreateSerializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        Directory.CreateDirectory(directory);
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
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
            try { state = CreateSerializer().Deserialize<InterruptedArchiveBatch>(File.ReadAllText(path, Encoding.UTF8)); }
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
        var payload = PayloadPath(batchId);
        if (File.Exists(payload)) File.Delete(payload);
        var path = Path.Combine(directory, batchId.ToString("N") + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
