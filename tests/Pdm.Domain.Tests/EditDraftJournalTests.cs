using Upton.Pdm.SolidWorks;

namespace Upton.Pdm.Domain.Tests;

public sealed class EditDraftJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-edit-draft-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitDiscardSurvivesRestartRestoresServerBytesAndPreservesManualRecovery()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        File.WriteAllText(entry.SourcePath, "saved changes being discarded");
        journal.MarkDiscarded(entry);
        var recovered = Assert.Single(new EditDraftJournal(root, "server|company|user").ReadAll());
        Assert.True(recovered.DiscardPending);
        Assert.False(recovered.RestoreOnOpen);
        Assert.False(journal.Restore(recovered, entry.SourcePath, entry.BaseVersionId));
        var archived = Path.Combine(root, "server.SLDPRT");
        File.WriteAllText(archived, "latest archived version");
        journal.RestoreDiscardedFile(recovered, archived, EditDraftJournal.Hash(archived));
        Assert.Equal("latest archived version", File.ReadAllText(entry.SourcePath));
        Assert.Equal("saved changes being discarded", File.ReadAllText(recovered.DiscardBackupPath));
        Assert.True(journal.HasVerifiedDraft(recovered));
        Assert.True(recovered.DiscardPending); // Binding completion is a separate durable step.
        Assert.True((File.GetAttributes(entry.SourcePath) & FileAttributes.ReadOnly) != 0);
        var backup = recovered.DiscardBackupPath;
        journal.RestoreDiscardedFile(recovered, archived, EditDraftJournal.Hash(archived));
        Assert.Equal(backup, recovered.DiscardBackupPath);
    }

    [Fact]
    public void FailedDiscardDownloadOrNewLocalEditCannotOverwriteTheWorkingFile()
    {
        var (journal, entry, _) = Create();
        journal.MarkDiscarded(entry);
        Assert.Throws<FileNotFoundException>(() => journal.RestoreDiscardedFile(entry, Path.Combine(root, "missing"), "HASH"));
        var archived = Path.Combine(root, "server.SLDPRT");
        File.WriteAllText(archived, "server");
        Assert.Throws<IOException>(() => journal.RestoreDiscardedFile(entry, archived, "wrong-hash"));
        Assert.Equal("base", File.ReadAllText(entry.SourcePath));
        File.WriteAllText(entry.SourcePath, "subsequent unrelated edit");
        Assert.Throws<IOException>(() => journal.RestoreDiscardedFile(entry, archived, EditDraftJournal.Hash(archived)));
        Assert.Equal("subsequent unrelated edit", File.ReadAllText(entry.SourcePath));
        Assert.True(Assert.Single(journal.ReadAll()).DiscardPending);
    }

    private (EditDraftJournal Journal, LocalEditDraft Entry, string Snapshot) Create()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "part.SLDPRT");
        var snapshot = Path.Combine(root, "snapshot.SLDPRT");
        File.WriteAllText(source, "base");
        File.WriteAllText(snapshot, "unsaved changes");
        return (new EditDraftJournal(root, "server|company|user"), new LocalEditDraft
        {
            DocumentId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
            SourcePath = source, BaseVersionId = Guid.NewGuid(), BaseSha256 = EditDraftJournal.Hash(source)
        }, snapshot);
    }

    [Fact]
    public void ExplicitDiscardCanRecreateADeletedWorkingFileWithoutRevivingItsDraft()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        journal.MarkDiscarded(entry);
        File.Delete(entry.SourcePath);
        var archived = Path.Combine(root, "server.SLDPRT");
        File.WriteAllText(archived, "verified server bytes");
        journal.RestoreDiscardedFile(entry, archived, EditDraftJournal.Hash(archived));
        Assert.Equal("verified server bytes", File.ReadAllText(entry.SourcePath));
        Assert.True(journal.HasVerifiedDraft(entry));
        Assert.False(entry.RestoreOnOpen);
        Assert.True(entry.DiscardPending);
    }

    [Fact]
    public void AlreadyRestoredDiscardStillProtectsTheWorkingCopyAsReadOnly()
    {
        var (journal, entry, _) = Create();
        journal.MarkDiscarded(entry);
        var archived = Path.Combine(root, "server.SLDPRT");
        File.Copy(entry.SourcePath, archived);
        journal.RestoreDiscardedFile(entry, archived, EditDraftJournal.Hash(archived));
        Assert.True((File.GetAttributes(entry.SourcePath) & FileAttributes.ReadOnly) != 0);
        Assert.Null(entry.DiscardBackupPath);
    }

    [Fact]
    public void VerifiedRelocatedBaseAllowsDraftRestoreButDoesNotAllowSubsequentLocalChanges()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        File.WriteAllText(entry.SourcePath, "verified relocated base");
        var verified = EditDraftJournal.Hash(entry.SourcePath);
        Assert.Throws<InvalidOperationException>(() => journal.Restore(entry, entry.SourcePath, entry.BaseVersionId, entry.BaseSha256));
        Assert.True(journal.RestoreWithVerifiedBase(entry, entry.SourcePath, entry.BaseVersionId, entry.BaseSha256, verified));
        Assert.Equal("unsaved changes", File.ReadAllText(entry.SourcePath));
        File.WriteAllText(entry.SourcePath, "new unrelated edits");
        Assert.Throws<InvalidOperationException>(() => journal.RestoreWithVerifiedBase(entry, entry.SourcePath,
            entry.BaseVersionId, entry.BaseSha256, verified));
        Assert.Equal("new unrelated edits", File.ReadAllText(entry.SourcePath));
    }

    [Fact]
    public void RenameReplacementRetainsOriginalAndRejectsAnUnrecognizedTarget()
    {
        var (_, entry, _) = Create();
        var temporary = entry.SourcePath + ".restore";
        File.WriteAllText(temporary, "server bytes");
        var previous = EditDraftJournal.Hash(entry.SourcePath);
        var replacement = EditDraftJournal.Hash(temporary);
        File.WriteAllText(entry.SourcePath, "new unrelated edits");
        var error = Assert.Throws<IOException>(() => EditDraftJournal.ReplaceByRenaming(temporary, entry.SourcePath, previous, replacement));
        Assert.Contains(entry.SourcePath, error.Message);
        Assert.Equal("new unrelated edits", File.ReadAllText(entry.SourcePath));
        Assert.Equal("server bytes", File.ReadAllText(temporary));
        EditDraftJournal.ReplaceByRenaming(temporary, entry.SourcePath, EditDraftJournal.Hash(entry.SourcePath), replacement);
        Assert.Equal("server bytes", File.ReadAllText(entry.SourcePath));
        Assert.Equal("new unrelated edits", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.discard-backup-*"))));
    }

    [Fact]
    public void DurableDraftRestoresWithoutDeletingTheBackupOrAdvancingVersion()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        var recovered = Assert.Single(new EditDraftJournal(root, "server|company|user").ReadAll());
        Assert.True(journal.Restore(recovered, entry.SourcePath, entry.BaseVersionId));
        Assert.Equal("unsaved changes", File.ReadAllText(entry.SourcePath));
        Assert.True(journal.HasVerifiedDraft(recovered));
        Assert.Equal(entry.BaseVersionId, recovered.BaseVersionId);
        Assert.False(journal.Restore(recovered, entry.SourcePath, entry.BaseVersionId));
    }

    [Fact]
    public void NewServerVersionAndUnrecognizedLocalChangesAreNeverOverwritten()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        Assert.Throws<InvalidOperationException>(() => journal.Restore(entry, entry.SourcePath, Guid.NewGuid()));
        Assert.Equal("base", File.ReadAllText(entry.SourcePath));
        File.WriteAllText(entry.SourcePath, "other edits");
        Assert.Throws<InvalidOperationException>(() => journal.Restore(entry, entry.SourcePath, entry.BaseVersionId));
        Assert.Equal("other edits", File.ReadAllText(entry.SourcePath));
        Assert.True(journal.HasVerifiedDraft(entry));
    }

    [Fact]
    public void RetainedDraftCanResumeAfterWorkingDirectoryWasReplacedWithTheVerifiedBase()
    {
        var (journal, entry, snapshot) = Create();
        File.WriteAllText(entry.SourcePath, "previous disk edit");
        journal.Preserve(entry, snapshot, true);
        File.WriteAllText(entry.SourcePath, "base"); // Clean download after clearing/recreating the working directory.
        Assert.True(journal.Restore(entry, entry.SourcePath, entry.BaseVersionId, entry.BaseSha256));
        Assert.Equal("unsaved changes", File.ReadAllText(entry.SourcePath));
        Assert.True(journal.HasVerifiedDraft(entry));
    }

    [Fact]
    public void CorruptDraftIsRejectedAndAccountsAreIsolated()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        Assert.Empty(new EditDraftJournal(root, "server|other-company|user").ReadAll());
        Assert.Empty(new EditDraftJournal(root, "server|company|other-user").ReadAll());
        File.WriteAllText(entry.DraftPath, "corrupt");
        Assert.False(journal.HasVerifiedDraft(entry));
        Assert.Throws<IOException>(() => journal.Restore(entry, entry.SourcePath, entry.BaseVersionId));
        Assert.Equal("base", File.ReadAllText(entry.SourcePath));
    }

    [Fact]
    public void FailedBackupDoesNotCreateAReleasableRecord()
    {
        var (journal, entry, _) = Create();
        Assert.Throws<FileNotFoundException>(() => journal.Preserve(entry, Path.Combine(root, "missing"), true));
        Assert.Empty(journal.ReadAll());
        Assert.False(entry.PendingClose);
    }

    [Fact]
    public void MissingSourceWaitsForRecoveryAcrossRestartWithoutDiscardingTheDraft()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        File.Delete(entry.SourcePath);
        var now = DateTime.UtcNow;
        Assert.True(journal.DelayMaintenance(entry, "missing-source", true, now));
        var recovered = Assert.Single(new EditDraftJournal(root, "server|company|user").ReadAll());
        Assert.True(recovered.RecoveryRequired);
        Assert.Equal(now.AddMinutes(5), recovered.NextMaintenanceAttemptUtc);
        Assert.False(recovered.Closed);
        Assert.False(recovered.PendingClose);
        Assert.True(journal.HasVerifiedDraft(recovered));
        Assert.False(journal.DelayMaintenance(recovered, "missing-source", true, now.AddMinutes(5)));
        Assert.Equal(now.AddMinutes(10), Assert.Single(journal.ReadAll()).NextMaintenanceAttemptUtc);
    }

    [Fact]
    public void TransientFailuresBackOffAndRecoveryClearsThePersistentError()
    {
        var (journal, entry, _) = Create();
        var now = DateTime.UtcNow;
        var delays = new[] { 30, 60, 120, 240, 300, 300 };
        for (var index = 0; index < delays.Length; index++)
        {
            Assert.Equal(index == 0, journal.DelayMaintenance(entry, "offline", false, now));
            entry = Assert.Single(journal.ReadAll());
            Assert.Equal(now.AddSeconds(delays[index]), entry.NextMaintenanceAttemptUtc);
            Assert.False(entry.RecoveryRequired);
        }
        Assert.True(journal.DelayMaintenance(entry, "permission-changed", false, now));
        Assert.Equal(now.AddSeconds(30), entry.NextMaintenanceAttemptUtc);
        EditDraftJournal.ClearMaintenanceFailure(entry);
        journal.Save(entry);
        var recovered = Assert.Single(journal.ReadAll());
        Assert.Null(recovered.MaintenanceErrorKey);
        Assert.Equal(DateTime.MinValue, recovered.NextMaintenanceAttemptUtc);
        Assert.Equal(0, recovered.MaintenanceFailureCount);
    }

    [Fact]
    public void ReplacingOwnedDraftRemovesOnlyTheUnreferencedSupersededSnapshot()
    {
        var (journal, entry, snapshot) = Create();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        entry.ProcessId = process.Id;
        entry.ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        journal.Preserve(entry, snapshot, true);
        var previous = entry.DraftPath;
        File.WriteAllText(snapshot, "newer changes");
        journal.Preserve(entry, snapshot, true);
        Assert.False(File.Exists(previous));
        Assert.True(journal.HasVerifiedDraft(entry));
        Assert.Equal("newer changes", File.ReadAllText(entry.DraftPath));

        var shared = Assert.Single(journal.ReadAll());
        shared.SessionId = Guid.NewGuid();
        journal.Save(shared);
        previous = entry.DraftPath;
        File.WriteAllText(snapshot, "latest changes");
        journal.Preserve(entry, snapshot, true);
        Assert.True(File.Exists(previous));
        Assert.True(journal.HasVerifiedDraft(shared));
        Assert.True(journal.HasVerifiedDraft(entry));
    }

    [Fact]
    public void RecoveredSessionCannotDeleteItsPreviousRecoverySnapshot()
    {
        var (journal, entry, snapshot) = Create();
        journal.Preserve(entry, snapshot, true);
        var previous = entry.DraftPath;
        File.WriteAllText(snapshot, "newer changes");
        journal.Preserve(entry, snapshot, true);
        Assert.True(File.Exists(previous));
        Assert.True(journal.HasVerifiedDraft(entry));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
