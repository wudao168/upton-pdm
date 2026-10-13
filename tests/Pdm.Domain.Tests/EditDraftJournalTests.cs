using Upton.Pdm.SolidWorks;

namespace Upton.Pdm.Domain.Tests;

public sealed class EditDraftJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-edit-draft-tests", Guid.NewGuid().ToString("N"));

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

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
