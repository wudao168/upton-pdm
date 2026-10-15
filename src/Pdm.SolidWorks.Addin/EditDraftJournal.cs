#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace Upton.Pdm.SolidWorks;

public sealed class LocalEditDraft
{
    public Guid DocumentId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SessionId { get; set; }
    public DateTime CheckedOutAt { get; set; }
    public string SourcePath { get; set; }
    public Guid? BaseVersionId { get; set; }
    public string BaseSha256 { get; set; }
    public string DraftPath { get; set; }
    public string DraftSha256 { get; set; }
    public string DiskSha256 { get; set; }
    public DateTime SavedAtUtc { get; set; }
    public DateTime NextSnapshotAttemptUtc { get; set; }
    public DateTime NextMaintenanceAttemptUtc { get; set; }
    public int MaintenanceFailureCount { get; set; }
    public string MaintenanceErrorKey { get; set; }
    public bool RecoveryRequired { get; set; }
    public bool ObservedOpen { get; set; }
    public bool UnsavedChanges { get; set; }
    public bool PendingClose { get; set; }
    public bool Closed { get; set; }
    public bool RestoreOnOpen { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartedUtcTicks { get; set; }
}

// Each account/server/company has its own journal. Drafts never enter formal version history.
internal sealed class EditDraftJournal
{
    private readonly string directory;
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(LocalEditDraft));

    public EditDraftJournal(string root, string scope)
    {
        using (var hash = SHA256.Create())
            directory = Path.Combine(root, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(scope))).Replace("-", "").Substring(0, 32));
    }

    private string RecordPath(LocalEditDraft entry) => Path.Combine(directory, entry.SessionId.ToString("N") + "-" + entry.DocumentId.ToString("N") + ".xml");

    public IReadOnlyList<LocalEditDraft> ReadAll()
    {
        if (!Directory.Exists(directory)) return Array.Empty<LocalEditDraft>();
        return Directory.GetFiles(directory, "*.xml").Select(path =>
        {
            using (var stream = File.OpenRead(path)) return (LocalEditDraft)Serializer.Deserialize(stream);
        }).ToArray();
    }

    public void Save(LocalEditDraft entry)
    {
        Directory.CreateDirectory(directory);
        var target = RecordPath(entry);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Serializer.Serialize(stream, entry);
                stream.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Hash(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }

    public bool DelayMaintenance(LocalEditDraft entry, string errorKey, bool recoveryRequired, DateTime now)
    {
        var changed = !string.Equals(entry.MaintenanceErrorKey, errorKey, StringComparison.Ordinal);
        entry.MaintenanceFailureCount = changed ? 1 : Math.Min(entry.MaintenanceFailureCount + 1, 5);
        entry.MaintenanceErrorKey = errorKey;
        entry.RecoveryRequired = recoveryRequired;
        entry.NextMaintenanceAttemptUtc = now.AddSeconds(recoveryRequired ? 300 : Math.Min(300, 30 * Math.Pow(2, entry.MaintenanceFailureCount - 1)));
        Save(entry);
        return changed;
    }

    public static void ClearMaintenanceFailure(LocalEditDraft entry)
    {
        entry.NextMaintenanceAttemptUtc = DateTime.MinValue;
        entry.MaintenanceFailureCount = 0;
        entry.MaintenanceErrorKey = null;
        entry.RecoveryRequired = false;
    }

    public void Preserve(LocalEditDraft entry, string snapshotPath, bool restoreOnOpen)
    {
        var previousDraft = entry.DraftPath;
        var sourceHash = Hash(snapshotPath);
        var diskHash = Hash(entry.SourcePath);
        if (string.Equals(entry.DraftSha256, sourceHash, StringComparison.OrdinalIgnoreCase) && HasVerifiedDraft(entry))
        {
            entry.DiskSha256 = diskHash;
            entry.SavedAtUtc = DateTime.UtcNow;
            entry.RestoreOnOpen = restoreOnOpen;
            Save(entry);
            return;
        }
        var folder = Path.Combine(directory, entry.DocumentId.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "draft" + Path.GetExtension(entry.SourcePath));
        using (var source = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(destination);
            destination.Flush(true);
        }
        if (!string.Equals(sourceHash, Hash(target), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(diskHash, Hash(entry.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("保存草稿期间图档发生变化，已保留编辑状态，请稍后重试。");
        entry.DraftPath = target;
        entry.DraftSha256 = sourceHash;
        entry.DiskSha256 = diskHash;
        entry.SavedAtUtc = DateTime.UtcNow;
        entry.RestoreOnOpen = restoreOnOpen;
        Save(entry);
        // Only the live owning process can retire its own superseded snapshot. Historical
        // sessions and the last referenced recovery copy remain available for recovery.
        using (var process = Process.GetCurrentProcess())
            if (entry.ProcessId == process.Id && OwnerProcessRunning(entry))
                TryDeleteUnreferencedSnapshot(entry.DocumentId, previousDraft);
    }

    private void TryDeleteUnreferencedSnapshot(Guid documentId, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var snapshotDirectory = Path.GetDirectoryName(fullPath);
            var documentDirectory = Path.Combine(directory, documentId.ToString("N"));
            if (!string.Equals(Path.GetDirectoryName(snapshotDirectory), documentDirectory, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(snapshotDirectory), "N", out _)
                || !string.Equals(Path.GetFileNameWithoutExtension(fullPath), "draft", StringComparison.OrdinalIgnoreCase)
                || ReadAll().Any(record => !string.IsNullOrWhiteSpace(record.DraftPath)
                    && string.Equals(Path.GetFullPath(record.DraftPath), fullPath, StringComparison.OrdinalIgnoreCase))) return;
            File.Delete(fullPath);
            if (!Directory.EnumerateFileSystemEntries(snapshotDirectory).Any()) Directory.Delete(snapshotDirectory);
        }
        catch (IOException) { } // A reader or unavailable journal is not evidence that a draft can be removed.
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { } // Corrupt journal: preserve all snapshots.
        catch (ArgumentException) { }
    }

    public bool HasVerifiedDraft(LocalEditDraft entry) => !string.IsNullOrWhiteSpace(entry.DraftPath)
        && File.Exists(entry.DraftPath) && string.Equals(Hash(entry.DraftPath), entry.DraftSha256, StringComparison.OrdinalIgnoreCase);

    public bool Restore(LocalEditDraft entry, string target, Guid? latestVersionId)
    {
        if (!entry.RestoreOnOpen) return false;
        if (entry.BaseVersionId != latestVersionId)
            throw new InvalidOperationException("服务器已有新版本，本机草稿已保留，不能直接覆盖。请核对并合并修改。草稿位置：" + Path.GetDirectoryName(entry.DraftPath));
        if (!HasVerifiedDraft(entry)) throw new IOException("本机草稿校验失败，已停止恢复。请检查草稿文件及磁盘状态。");
        if (!string.Equals(Path.GetFullPath(entry.SourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("工作区路径已变化，草稿已保留，请在原工作区恢复：" + entry.SourcePath);
        var current = Hash(target);
        if (string.Equals(current, entry.DraftSha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(current, entry.DiskSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地图档已有其他修改，已保留图档和草稿，请核对后恢复。草稿位置：" + Path.GetDirectoryName(entry.DraftPath));
        // Retain both files. Never replace an unrecognized local revision.
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".draft";
        try
        {
            File.Copy(entry.DraftPath, temporary);
            File.SetAttributes(temporary, FileAttributes.Normal);
            if (!string.Equals(Hash(temporary), entry.DraftSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复副本校验失败。");
            if (!string.Equals(Hash(target), current, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复期间本地图档发生变化，已停止恢复。");
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            File.Replace(temporary, target, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    public static bool OwnerProcessRunning(LocalEditDraft entry)
    {
        try
        {
            using (var process = Process.GetProcessById(entry.ProcessId))
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.ProcessStartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // An inaccessible process is not proof of death.
    }
}
