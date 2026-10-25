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
    public bool DiscardPending { get; set; }
    public string DiscardBackupPath { get; set; }
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

    public static bool MatchesBase(LocalEditDraft entry, Guid? latestVersionId, string latestSha256) =>
        latestVersionId.HasValue && (entry.BaseVersionId == latestVersionId
            || !string.IsNullOrWhiteSpace(entry.BaseSha256) && !string.IsNullOrWhiteSpace(latestSha256)
                && string.Equals(entry.BaseSha256, latestSha256, StringComparison.OrdinalIgnoreCase));

    public bool Restore(LocalEditDraft entry, string target, Guid? latestVersionId, string latestSha256 = null)
        => RestoreWithVerifiedBase(entry, target, latestVersionId, latestSha256, null);

    public bool RestoreWithVerifiedBase(LocalEditDraft entry, string target, Guid? latestVersionId, string latestSha256,
        string verifiedCurrentSha256)
    {
        if (!entry.RestoreOnOpen || entry.DiscardPending) return false;
        if (!MatchesBase(entry, latestVersionId, latestSha256))
            throw new InvalidOperationException("服务器已有新版本，本机草稿已保留，不能直接覆盖。请核对并合并修改。草稿位置：" + Path.GetDirectoryName(entry.DraftPath));
        if (!HasVerifiedDraft(entry)) throw new IOException("本机草稿校验失败，已停止恢复。请检查草稿文件及磁盘状态。");
        var samePath = string.Equals(Path.GetFullPath(entry.SourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        if (!samePath && File.Exists(entry.SourcePath)
            && !string.Equals(Hash(entry.SourcePath), entry.DiskSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原工作文件还有更新的本地修改，两份文件及草稿均已保留，请核对后恢复：" + entry.SourcePath);
        var current = Hash(target);
        if (string.Equals(current, entry.DraftSha256, StringComparison.OrdinalIgnoreCase)) return false;
        var expectedDisk = samePath ? entry.DiskSha256 : latestSha256;
        if ((string.IsNullOrWhiteSpace(expectedDisk) || !string.Equals(current, expectedDisk, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(latestSha256) || !string.Equals(current, latestSha256, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(verifiedCurrentSha256) || !string.Equals(current, verifiedCurrentSha256, StringComparison.OrdinalIgnoreCase)))
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
            ReplaceVerifiedWorkingFile(temporary, target, current, entry.DraftSha256);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    // Explicit discard is durable until both the controlled bytes and their binding
    // have been restored. A failed download or locked CAD file must not revive a draft.
    public void MarkDiscarded(LocalEditDraft entry)
    {
        entry.DiskSha256 = File.Exists(entry.SourcePath) ? Hash(entry.SourcePath) : null;
        entry.Closed = true;
        entry.PendingClose = false;
        entry.RestoreOnOpen = false;
        entry.DiscardPending = true;
        ClearMaintenanceFailure(entry);
        Save(entry);
    }

    public string ValidateDiscardedFile(LocalEditDraft entry, string controlledPath, string controlledHash)
    {
        if (!entry.DiscardPending) throw new InvalidOperationException("该图档没有待完成的放弃编辑记录。");
        if (!string.Equals(Hash(controlledPath), controlledHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("服务器版本副本校验失败，未恢复工作文件。");
        var target = Path.GetFullPath(entry.SourcePath);
        var current = File.Exists(target) ? Hash(target) : null;
        if (current != null && !string.Equals(current, controlledHash, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current, entry.DiskSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("放弃编辑后工作文件又发生变化，已保留两份文件，请核对后重试。文件：" + target);
        return current;
    }

    public void RestoreDiscardedFile(LocalEditDraft entry, string controlledPath, string controlledHash)
    {
        var current = ValidateDiscardedFile(entry, controlledPath, controlledHash);
        var target = Path.GetFullPath(entry.SourcePath);
        if (string.Equals(current, controlledHash, StringComparison.OrdinalIgnoreCase))
        {
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
            return;
        }
        if (current != null)
        {
            var backup = Path.Combine(directory, entry.DocumentId.ToString("N"), Guid.NewGuid().ToString("N"), Path.GetFileName(target));
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.Copy(target, backup);
            if (!string.Equals(Hash(backup), current, StringComparison.OrdinalIgnoreCase))
                throw new IOException("放弃编辑的恢复备份校验失败，原工作文件未替换。");
            entry.DiscardBackupPath = backup;
            Save(entry);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".restore";
        try
        {
            File.Copy(controlledPath, temporary);
            File.SetAttributes(temporary, FileAttributes.Normal);
            if (!string.Equals(Hash(temporary), controlledHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(File.Exists(target) ? Hash(target) : null, current, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复期间文件发生变化，已停止替换并保留备份。");
            if (File.Exists(target))
            {
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                ReplaceVerifiedWorkingFile(temporary, target, current, controlledHash);
            }
            else File.Move(temporary, target);
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ReplaceVerifiedWorkingFile(string temporary, string target, string previousHash, string replacementHash)
    {
        try
        {
            // Working copies have their PLM binding recorded separately. An inability
            // to merge old Windows metadata must not reject otherwise permitted writes.
            File.Replace(temporary, target, null, true);
        }
        catch (UnauthorizedAccessException)
        {
            // A rename still requires Windows to permit replacing this file. This
            // changes no ACL and keeps the original until the verified copy is installed.
            ReplaceByRenaming(temporary, target, previousHash, replacementHash);
        }
    }

    internal static void ReplaceByRenaming(string temporary, string target, string previousHash, string replacementHash)
    {
        var retained = target + ".discard-backup-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!string.Equals(Hash(target), previousHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(temporary), replacementHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复期间文件发生变化，已停止替换并保留备份。");
            File.Move(target, retained);
            try { File.Move(temporary, target); }
            catch
            {
                if (!File.Exists(target)) File.Move(retained, target);
                throw;
            }
            // Keep the renamed original as an additional recovery copy. Do not turn
            // a successful restoration into a failure because cleanup was denied.
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            throw new IOException("本地恢复文件替换失败：" + Path.GetFileName(target) + "；路径：" + target
                + "。原文件及恢复副本已保留，请检查文件占用和目录写入权限后重试。", error);
        }
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
