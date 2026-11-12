using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveRecycleTests
{
    private static async Task<PdmDocument> Register(InMemoryPdmRepository repository, Project project, string name, DocumentKind kind, Guid session)
    {
        var extension = kind == DocumentKind.Drawing ? ".SLDDRW" : kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT";
        var document = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + extension, kind), "admin", default);
        return await repository.CheckoutAsync(document.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
    }
    private sealed record Fixture(InMemoryPdmRepository Repository, Project Project, Guid Batch, Guid Session, PdmDocument Part, PdmDocument Root, PdmDocument Drawing)
    { public ArchiveRecycleService Service => new(Repository, Repository, TimeProvider.System); }
    private static async Task<Fixture> Create(bool completeFollowup = true)
    {
        var repository = BusinessTestRepository.Create(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("REC-" + Guid.NewGuid().ToString("N"), "批次回收测试", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid(); var batch = Guid.NewGuid();
        var part = await Register(repository, project, "PART", DocumentKind.Part, session);
        var root = await Register(repository, project, "ROOT", DocumentKind.Assembly, session);
        var drawing = await Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        await repository.CheckInBatchVersionsAsync(batch, "first", "admin", session,
            [new(part.Id, ArchiveBatchTests.Commit(project, part, ArchiveBatchTests.Node(part), 'A')),
             new(root.Id, ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root) with { Children = [ArchiveBatchTests.Node(part)] }, 'B', true)),
             new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing) with { Children = [ArchiveBatchTests.Node(part)] }, 'C'))], default);
        if (completeFollowup) await repository.CompleteArchiveBatchFollowupAsync(batch, default);
        return new(repository, project, batch, session, part, root, drawing);
    }

    [Fact]
    public async Task WholeRootAndPairedDrawingRecycleAndRestoreWithoutDeletingVersions()
    {
        var f = await Create();
        var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        Assert.True(preview.CanRecycle, string.Join("；", preview.Blockers)); Assert.Equal(3, preview.Members.Count);
        var before = await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default);
        var set = await f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "存错项目", "admin", UserRole.Administrator, default);
        Assert.Null(await f.Repository.FindDocumentAsync(f.Root.Id, default)); Assert.Null(await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default));
        Assert.All(set.Members, member => Assert.Single(f.Repository.ListDocumentVersionsAsync(member.Document.Id, default).Result));
        var restored = await f.Service.RestoreAsync(f.Project.Id, set.Id, "admin", UserRole.Administrator, default);
        Assert.NotNull(restored.RestoredAt); Assert.NotNull(await f.Repository.FindDocumentAsync(f.Part.Id, default));
        Assert.Equal(before!.RootDocumentId, (await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default))!.RootDocumentId);
        Assert.Single(await f.Repository.ListDocumentRelationsAsync(f.Project.Id, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RestoreAsync(f.Project.Id, set.Id, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task PendingFollowupBlocksBeforeAnyChange()
    {
        var f = await Create(false); var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        Assert.False(preview.CanRecycle); Assert.Contains(preview.Blockers, text => text.Contains("BOM"));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "误操作", "admin", UserRole.Administrator, default));
        Assert.Equal(3, (await f.Repository.ListDocumentsAsync(f.Project.Id, default)).Count);
    }

    [Fact]
    public async Task GroupMembersCannotBeRestoredThroughSingleDocumentEntry()
    {
        var f = await Create(); var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        await f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "误操作", "admin", UserRole.Administrator, default);
        var document = await f.Repository.FindDocumentIncludingDeletedAsync(f.Part.Id, default);
        var single = new ControlledDocumentRecycleService(f.Repository, TimeProvider.System);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => single.RestoreAsync(f.Project.Id, f.Part.Id, document!.RowVersion, "admin", UserRole.Administrator, default));
        Assert.Contains("成套恢复", error.Message); Assert.Null(await f.Repository.FindDocumentAsync(f.Part.Id, default));
    }

    [Fact]
    public async Task IndependentExistingRootSurvivesRecycleAndCoexistsAfterRestore()
    {
        var f = await Create(); var other = await Register(f.Repository, f.Project, "KEEP", DocumentKind.Assembly, f.Session); var batch = Guid.NewGuid();
        await f.Repository.CheckInBatchVersionsAsync(batch, "keep", "admin", f.Session,
            [new(other.Id, ArchiveBatchTests.Commit(f.Project, other, ArchiveBatchTests.Node(other), 'E', true))], default);
        await f.Repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default); Assert.True(preview.CanRecycle);
        var set = await f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "清理错误批次", "admin", UserRole.Administrator, default);
        Assert.Equal(other.Id, (await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default))!.RootDocumentId);
        await f.Service.RestoreAsync(f.Project.Id, set.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(2, ProjectReferenceForest.Roots((await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default))!).Count);
        Assert.Single(await f.Repository.ListDocumentVersionsAsync(other.Id, default));
    }

    [Theory]
    [InlineData(DocumentKind.Assembly)]
    [InlineData(DocumentKind.Drawing)]
    public async Task OutsideAssemblyOrDrawingProtectsEntireBatch(DocumentKind kind)
    {
        var f = await Create(); var external = await Register(f.Repository, f.Project, "OUTSIDE", kind, f.Session);
        var batch = Guid.NewGuid(); await f.Repository.CheckInBatchVersionsAsync(batch, "outside", "admin", f.Session,
            [new(external.Id, ArchiveBatchTests.Commit(f.Project, external, ArchiveBatchTests.Node(external) with { Children = [ArchiveBatchTests.Node(f.Part)] }, 'D', true))], default);
        await f.Repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        Assert.False(preview.CanRecycle); Assert.Contains(preview.Blockers, text => text.Contains("范围之外"));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "误操作", "admin", UserRole.Administrator, default));
        Assert.Equal(4, (await f.Repository.ListDocumentsAsync(f.Project.Id, default)).Count);
    }

    [Fact]
    public async Task ChangedScopeAndCheckoutAreRejectedAtomically()
    {
        var f = await Create(); var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        await f.Repository.CheckoutAsync(f.Part.Id, "admin", f.Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "清理", "admin", UserRole.Administrator, default));
        Assert.NotNull(await f.Repository.FindDocumentAsync(f.Drawing.Id, default)); Assert.Empty(await f.Repository.ListArchiveRecycleSetsAsync(f.Project.Id, default));
    }

    [Fact]
    public async Task LegacyPartialDrawingRelationOutsideBatchStillBlocksRecycle()
    {
        var f = await Create(); var drawing = await Register(f.Repository, f.Project, "LEGACY", DocumentKind.Drawing, f.Session); var batch = Guid.NewGuid();
        await f.Repository.CheckInBatchVersionsAsync(batch, "legacy", "admin", f.Session,
            [new(drawing.Id, ArchiveBatchTests.Commit(f.Project, drawing, ArchiveBatchTests.Node(drawing) with { StructureIndexPending = true, Children = [ArchiveBatchTests.Node(f.Part)] }, 'E'))], default);
        await f.Repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var map = (System.Collections.IDictionary)typeof(InMemoryPdmRepository).GetField("documentRelations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Repository)!;
        foreach (var key in map.Keys.Cast<object>().Where(key => ((DocumentModelDrawingRelation)map[key]!).DrawingDocumentId == drawing.Id).ToArray()) map.Remove(key);
        var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        Assert.False(preview.CanRecycle); Assert.Contains(preview.Blockers, text => text.Contains("LEGACY.SLDDRW"));
        Assert.Equal(4, (await f.Repository.ListDocumentsAsync(f.Project.Id, default)).Count);
    }

    [Fact]
    public async Task WrongPreviewHashCannotRecycleAnything()
    {
        var f = await Create();
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.RecycleAsync(f.Project.Id, f.Batch, "other-preview", "清理", "admin", UserRole.Administrator, default));
        Assert.Equal(3, (await f.Repository.ListDocumentsAsync(f.Project.Id, default)).Count);
    }

    [Fact]
    public async Task LaterVersionsAreRetainedAndCannotBeRemovedByFirstArchiveBatch()
    {
        var f = await Create(); var partBase = Assert.Single(await f.Repository.ListDocumentVersionsAsync(f.Part.Id, default));
        var part = await f.Repository.CheckoutAsync(f.Part.Id, "admin", f.Session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var batch = Guid.NewGuid(); await f.Repository.CheckInBatchVersionsAsync(batch, "update", "admin", f.Session,
            [new(part.Id, ArchiveBatchTests.Commit(f.Project, part, ArchiveBatchTests.Node(part), 'D') with { ExpectedVersionId = partBase.Id })], default);
        await f.Repository.CompleteArchiveBatchFollowupAsync(batch, default);
        var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        Assert.DoesNotContain(preview.Members, item => item.Document.Id == part.Id);
        Assert.Contains(preview.RetainedFiles, text => text.Contains("PART") && text.Contains("其他版本"));
        Assert.False(preview.CanRecycle); Assert.Equal(2, (await f.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
    }

    [Fact]
    public async Task RestoreExpiredOrPartiallyRestoredSetDoesNotChangeOtherMembers()
    {
        var f = await Create(); var preview = await f.Service.PreviewAsync(f.Project.Id, f.Batch, "admin", UserRole.Administrator, default);
        var set = await f.Service.RecycleAsync(f.Project.Id, f.Batch, preview.ScopeHash, "误操作", "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Repository.RestoreArchiveSetAsync(f.Project.Id, set.Id, "admin", set.ExpiresAt, default));
        var root = await f.Repository.FindDocumentIncludingDeletedAsync(f.Root.Id, default);
        await f.Repository.SetDocumentDeletedAsync(root!.Id, false, root.RowVersion, "admin", null, DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.RestoreAsync(f.Project.Id, set.Id, "admin", UserRole.Administrator, default));
        Assert.Null(await f.Repository.FindDocumentAsync(f.Part.Id, default));
    }

    [Theory]
    [InlineData(UserRole.Engineer)]
    [InlineData(UserRole.PlanningManager)]
    public async Task AccountWithoutRecycleGrantCannotReadOrExecuteEvenWithForgedRole(UserRole role)
    {
        var f = await Create();
        await PermissionTestUsers.Ensure(f.Repository, ("without-recycle", role));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ListBatchesAsync(f.Project.Id, "without-recycle", UserRole.Administrator, default));
        Assert.Equal(3, (await f.Repository.ListDocumentsAsync(f.Project.Id, default)).Count);
    }
}
