using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class DocumentVersionTests
{
    [Fact]
    public async Task PendingDrawingRegistrationAcceptsOnlyItsOwnBatchModel()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-PENDING-SAFE", "同批模型登记", "admin", "vault", "release"), "admin", default);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var context = new Application.ArchiveRegistrationContext(Guid.NewGuid(), Guid.NewGuid(), "QA");
        var model = await workflow.RegisterDocumentAsync(new(project.Id, "PART", "模型", "PART.SLDPRT", DocumentKind.Part, SourceSha256: new string('A',64)), "admin", UserRole.Administrator, default, context);
        var command = new Application.RegisterDocumentCommand(project.Id, "DRAWING", "图纸", "DRAWING.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: model.Id, SourceSha256: new string('B',64));
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, default, context with { BatchId = Guid.NewGuid() }));
        var drawing = await workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, default, context);
        Assert.NotNull(await repository.FindPendingArchiveDocumentAsync(drawing.Id, default));
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }

    [Theory]
    [InlineData(DocumentKind.Drawing)]
    [InlineData(DocumentKind.Assembly)]
    public async Task HistoricalRestoreUsesAtomicReferencesReceiptAndDurableFollowup(DocumentKind kind)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var repository = new Infrastructure.InMemoryPdmRepository(clock);
        var project = await repository.CreateProjectAsync(new("QA-RESTORE-" + kind, "历史恢复", "admin", "vault", "release"), "admin", default);
        var a = await RegisterAndCheckInAsync(repository, project, "MODEL-A", new string('A',64));
        var b = await RegisterAndCheckInAsync(repository, project, "MODEL-B", new string('B',64));
        var document = await repository.RegisterDocumentAsync(new(project.Id, "ROOT", "根图档", kind == DocumentKind.Drawing ? "ROOT.SLDDRW" : "ROOT.SLDASM", kind), "engineer", default);
        var session = Guid.NewGuid();
        document = await repository.CheckoutAsync(document.Id, "engineer", session, "QA", clock.GetUtcNow().AddHours(1), default);
        var firstCommit = Commit(project, document, ReferenceRoot(document, "engineer") with { Children = [ReferenceRoot(a,"engineer")] }, new string('C',64), "首次", kind == DocumentKind.Assembly);
        var first = (await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "first", "engineer", session, [new(document.Id,firstCommit)], default)).Results.Single();
        document = await repository.CheckoutAsync(document.Id, "engineer", session, "QA", clock.GetUtcNow().AddHours(1), default);
        var secondCommit = Commit(project, document, ReferenceRoot(document, "engineer") with { Children = [ReferenceRoot(b,"engineer")] }, new string('D',64), "替换模型", kind == DocumentKind.Assembly) with { ExpectedVersionId = first.Version!.Id, RequireBaseVersion = true };
        var second = (await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "second", "engineer", session, [new(document.Id,secondCommit)], default)).Results.Single();
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), clock);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.RestoreVersionAsync(document.Id, first.Version.Id, "engineer", UserRole.Administrator, "恢复", default));
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RestoreVersionAsync(document.Id, first.Version.Id, "engineer", UserRole.Administrator, "恢复", default, Guid.NewGuid(), first.Version.Id));
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RestoreVersionAsync(document.Id, first.Version.Id, "engineer", UserRole.Administrator, "恢复", default, Guid.NewGuid(), second.Version!.Id, Guid.NewGuid()));
        var operation = Guid.NewGuid();
        var restored = await workflow.RestoreVersionAsync(document.Id, first.Version.Id, "engineer", UserRole.Administrator, "恢复", default, operation, second.Version!.Id);
        var replay = await workflow.RestoreVersionAsync(document.Id, first.Version.Id, "engineer", UserRole.Administrator, "恢复", default, operation, second.Version.Id);
        Assert.Equal(restored.Version.Id, replay.Version.Id);
        Assert.Equal(first.Version.StorageRelativePath, restored.Version.StorageRelativePath);
        Assert.Equal(a.Id, Assert.Single(restored.Version.ReferenceSnapshot.Children).DocumentId);
        Assert.Equal(3, (await repository.ListDocumentVersionsAsync(document.Id, default)).Count);
        Assert.Null(restored.Document.CheckoutSessionId);
        if (kind == DocumentKind.Drawing)
            Assert.Equal(a.Id, Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default)).ModelDocumentId);
        else
            Assert.Equal(a.Id, Assert.Single((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!.Root.Children).DocumentId);
        var work = (await repository.ListArchiveBatchFollowupsAsync(20, default)).Single(item => item.Receipt.BatchId == operation);
        await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.CompleteArchiveBatchFollowupAsync(work, new CancellationToken(true)));
        Assert.True((await repository.FindArchiveBatchAsync(operation, default))!.FollowupPending);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Contains(await repository.ListArchiveBatchFollowupsAsync(20, default), item => item.Receipt.BatchId == operation);
        await repository.CompleteArchiveBatchFollowupAsync(operation, default);
        Assert.False((await repository.FindArchiveBatchAsync(operation, default))!.FollowupPending);
        if (kind == DocumentKind.Drawing)
        {
            var reviewId = Guid.NewGuid();
            var modelVersion = (await repository.ListDocumentVersionsAsync(a.Id, default)).First();
            await repository.CreateDrawingReviewPackageAsync(new DrawingReviewPackage
            {
                Id=reviewId,ProjectId=project.Id,Number="QA-REVIEW",State=DrawingReviewPackageState.InReview,CreatedBy="engineer",CreatedAt=clock.GetUtcNow(),
                Items=[new DrawingReviewItem { Id=Guid.NewGuid(),PackageId=reviewId,BomItemId=Guid.NewGuid(),DrawingNumber=document.DrawingNumber,Name=document.Name,
                    ModelDocumentId=a.Id,ModelVersionId=modelVersion.Id,ModelRevision=modelVersion.Revision.Display,ModelSha256=modelVersion.Sha256,ModelCreatedBy="engineer",
                    DrawingDocumentId=document.Id,DrawingVersionId=restored.Version.Id,DrawingRevision=restored.Version.Revision.Display,DrawingSha256=restored.Version.Sha256,DrawingCreatedBy="engineer" }]
            },default);
            await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RestoreVersionAsync(document.Id,first.Version.Id,"engineer",UserRole.Administrator,"审核中恢复",default,Guid.NewGuid(),restored.Version.Id));
        }
    }

    [Theory]
    [InlineData("document.version.read")]
    [InlineData("document.version.download")]
    public async Task OriginalVersionBytesRequireDownloadEvenWithoutAttachmentDisposition(string action)
    {
        var repository=new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project=await repository.CreateProjectAsync(new("QA-BYTES-"+action,"下载权限","engineer","vault","release"),"engineer",default);
        await repository.EnsureProjectFolderTreeAsync(project.Id,default);
        var folder=(await repository.ListProjectFoldersAsync(project.Id,"admin",UserRole.Administrator,default)).First(item=>item.TemplateKey=="mechanical.project");
        var document=await repository.RegisterDocumentAsync(new(project.Id,"VIEW","只读","VIEW.SLDPRT",DocumentKind.Part,folder.Id),"engineer",default);
        await repository.SetProjectFolderPermissionsAsync(project.Id,folder.Id,[new(FolderPrincipalType.Role,nameof(UserRole.Engineer),FolderAccess.View)],"admin",UserRole.Administrator,default);
        var workflow=new Application.PdmWorkflowService(repository,new RecordingFileStorage(),new NoOpPublisher(),TimeProvider.System);
        await workflow.AuditVersionReadAsync(document.Id,Guid.Empty,"engineer",UserRole.Engineer,"document.version.list",default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>workflow.AuditVersionReadAsync(document.Id,Guid.Empty,"engineer",UserRole.Engineer,action,default));
        await repository.SetProjectFolderPermissionsAsync(project.Id,folder.Id,[new(FolderPrincipalType.Role,nameof(UserRole.Engineer),FolderAccess.View|FolderAccess.Download)],"admin",UserRole.Administrator,default);
        await workflow.AuditVersionReadAsync(document.Id,Guid.Empty,"engineer",UserRole.Engineer,action,default);
    }

    [Theory]
    [InlineData(DocumentLifecycleState.InReview)]
    [InlineData(DocumentLifecycleState.Obsolete)]
    public void RestoreCannotBypassLifecycleOrForeignSession(DocumentLifecycleState state)
    {
        var document = new PdmDocument(Guid.NewGuid(), Guid.NewGuid(), "P", "P", "P.SLDPRT", DocumentKind.Part, state, RevisionLabel.InitialWork(), null, DateTimeOffset.UtcNow);
        Assert.Throws<Application.PdmConflictException>(() => Application.ArchiveBatchRules.ValidateRestoreSession(document, "admin", null));
        var session = Guid.NewGuid();
        Assert.Throws<Application.PdmConflictException>(() => Application.ArchiveBatchRules.ValidateRestoreSession(document with { State = DocumentLifecycleState.Work, CheckedOutBy = "other", CheckoutSessionId = session }, "admin", session));
    }

    [Fact]
    public async Task MultiConfigurationReferencesShareFileVersionAndPreflightRejectsUnresolvedInstances()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var root = await repository.RegisterDocumentAsync(new(project.Id, "CONFIG-ROOT", "总装", "CONFIG-ROOT.SLDASM", DocumentKind.Assembly), "engineer", default);
        var part = await repository.RegisterDocumentAsync(new(project.Id, "CONFIG-PART", "多配置件", "CONFIG-PART.SLDPRT", DocumentKind.Part), "engineer", default);
        var session = Guid.NewGuid();
        root = await workflow.CheckoutAsync(root.Id, "engineer", UserRole.Administrator, session, "test", default);
        part = await workflow.CheckoutAsync(part.Id, "engineer", UserRole.Administrator, session, "test", default);
        var instances = new[] {
            ReferenceRoot(part, "engineer") with { InstancePath = "ROOT/PART-1", Configuration = "D13-V4-T4", Quantity = 1 },
            ReferenceRoot(part, "engineer") with { InstancePath = "ROOT/PART-2", Configuration = "D14-V6-T8", Quantity = 2 }
        };
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, root.Id, DateTimeOffset.UtcNow, "engineer",
            ReferenceRoot(root, "engineer") with { Children = instances }, new string('F', 64));
        Task Preflight(CadReferenceSnapshot tree, IReadOnlyCollection<Guid>? planned = null) => workflow.PreflightCheckInAsync(
            root.Id, "engineer", UserRole.Administrator, session, new Dictionary<string, string?>(), tree, true, default,
            requireBaseVersion: true, plannedDocumentIds: planned);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => Preflight(snapshot));
        await Preflight(snapshot, [part.Id]);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => Preflight(snapshot with {
            Root = snapshot.Root with { Children = [instances[0], instances[1] with { DocumentId = null }] }
        }, [part.Id]));
        await workflow.CheckoutAsync(part.Id, "engineer", UserRole.Administrator, Guid.NewGuid(), "test", default);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => Preflight(snapshot, [part.Id]));
        part = await workflow.CheckoutAsync(part.Id, "engineer", UserRole.Administrator, session, "test", default);
        var rootFile = new Application.StoredFile(".versions/config-root", 128, new string('B', 64), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.CheckInAsync(root.Id, "engineer", UserRole.Administrator,
            session, rootFile, "未提交子件", new Dictionary<string, string?>(), snapshot, true, false, default, requireBaseVersion: true));
        Assert.Empty(await repository.ListDocumentVersionsAsync(root.Id, default));

        var partTree = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, part.Id, DateTimeOffset.UtcNow, "engineer", instances[0], new string('F', 64));
        await workflow.CheckInAsync(part.Id, "engineer", UserRole.Administrator, session,
            new Application.StoredFile(".versions/config-part", 128, new string('A', 64), DateTimeOffset.UtcNow), "多配置件首次", new Dictionary<string, string?>(),
            partTree, false, false, default, requireBaseVersion: true);
        await Preflight(snapshot);
        var saved = await workflow.CheckInAsync(root.Id, "engineer", UserRole.Administrator, session, rootFile, "总装采用两种配置",
            new Dictionary<string, string?>(), snapshot, true, false, default, requireBaseVersion: true);
        Assert.True(saved.VersionCreated);
        Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default));
        Assert.Equal(instances.Select(node => node.Configuration), saved.Version!.ReferenceSnapshot.Children.Select(node => node.Configuration));
        Assert.Equal(new[] { 1, 2 }, saved.Version.ReferenceSnapshot.Children.Select(node => node.Quantity));
    }

    [Fact]
    public async Task SubassemblyCheckInKeepsItsIdentityAndParentPinUntilParentAdoptsStructure()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Application.CreateProjectCommand("QA-SUB-PIN", "子装配体版本引用", "admin", "vault", "release"), "admin", default);
        var part = await RegisterAndCheckInAsync(repository, project, "OLD-PART", new string('1', 64));
        var sub = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "SUB", "子装配", "SUB.SLDASM", DocumentKind.Assembly), "engineer", default);
        sub = await repository.CheckoutAsync(sub.Id, "engineer", default);
        var firstSubTree = ReferenceRoot(sub, "engineer") with { Children = [ReferenceRoot(part, "engineer")] };
        var firstSub = await repository.CheckInVersionAsync(sub.Id, "engineer", Commit(project, sub, firstSubTree, new string('2', 64), "子件首次"), default);
        var root = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "ROOT", "总装", "ROOT.SLDASM", DocumentKind.Assembly), "engineer", default);
        root = await repository.CheckoutAsync(root.Id, "engineer", default);
        var firstRootTree = ReferenceRoot(root, "engineer") with { Children = [firstSubTree with { Revision = firstSub.Document.Revision }] };
        await repository.CheckInVersionAsync(root.Id, "engineer", Commit(project, root, firstRootTree, new string('3', 64), "总装首次", true), default);
        var addedPart = await RegisterAndCheckInAsync(repository, project, "NEW-PART", new string('4', 64));
        sub = await repository.CheckoutAsync(sub.Id, "engineer", default);
        var secondSubTree = ReferenceRoot(sub, "engineer") with { Children = [ReferenceRoot(part, "engineer"), ReferenceRoot(addedPart, "engineer")] };
        var updatedSub = await repository.CheckInVersionAsync(sub.Id, "engineer", Commit(project, sub, secondSubTree, new string('5', 64), "补充子件"), default);
        var unchangedRoot = Assert.Single(ProjectReferenceForest.Roots((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!));
        Assert.Equal(root.Id, unchangedRoot.DocumentId);
        Assert.Equal(firstSub.Document.Revision, unchangedRoot.Children.Single().Revision);
        Assert.Single(unchangedRoot.Children.Single().Children);
        Assert.Equal(2, updatedSub.Version!.ReferenceSnapshot.Children.Count);
        root = await repository.CheckoutAsync(root.Id, "engineer", default);
        await repository.CheckInVersionAsync(root.Id, "engineer", Commit(project, root,
            ReferenceRoot(root, "engineer") with { Children = [secondSubTree with { Revision = updatedSub.Document.Revision }] },
            new string('6', 64), "总装采用新版", true), default);
        var adopted = Assert.Single(ProjectReferenceForest.Roots((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!));
        Assert.Equal(sub.Id, adopted.Children.Single().DocumentId);
        Assert.Equal(2, adopted.Children.Single().Children.Count);
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(sub.Id, default)).Count);
    }
    [Fact]
    public async Task OfflineReleaseUsesHeartbeatGraceAndRechecksReconnectionAtomically()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
        var repository = new Infrastructure.InMemoryPdmRepository(clock);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "AUTO-LOCK", "自动释放", "AUTO.SLDPRT", DocumentKind.Part), "admin", CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), clock);
        var session = Guid.NewGuid();
        await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "WS-A", CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(49));
        await workflow.HeartbeatEditSessionAsync(session, "admin", UserRole.Administrator, "WS-A", [document.Id], CancellationToken.None);
        Assert.False(Assert.Single(await workflow.ListEditLocksAsync("admin", UserRole.Administrator, CancellationToken.None), item => item.DocumentId == document.Id).CanForceRelease);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.ForceReleaseEditLockAsync(document.Id, "admin", UserRole.Administrator, "仍在线", CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(Assert.Single(await workflow.ListEditLocksAsync("admin", UserRole.Administrator, CancellationToken.None), item => item.DocumentId == document.Id).CanForceRelease);
        var cutoff = clock.GetUtcNow().AddMinutes(-60);
        await workflow.HeartbeatEditSessionAsync(session, "admin", UserRole.Administrator, "WS-A", [document.Id], CancellationToken.None);
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => repository.ForceReleaseOfflineCheckoutAsync(document.Id, session, cutoff, clock.GetUtcNow(), CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Null((await workflow.ForceReleaseEditLockAsync(document.Id, "admin", UserRole.Administrator, "离线接管", CancellationToken.None)).CheckedOutBy);
    }

    [Fact]
    public async Task DelayedAutomaticCloseCannotReleaseReopenedEditEvenWithSameProcessSession()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z"));
        var repository = new Infrastructure.InMemoryPdmRepository(clock);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "AUTO-CLOSE", "自动收尾", "AUTO.SLDPRT", DocumentKind.Part), "admin", CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), clock);
        var session = Guid.NewGuid();
        var first = await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "WS-A", CancellationToken.None);
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.CloseEditAsync(document.Id, "admin", UserRole.Administrator, session, first.CheckedOutAt!.Value, "changed", false, CancellationToken.None));
        var closed = await workflow.CloseEditAsync(document.Id, "admin", UserRole.Administrator, session, first.CheckedOutAt!.Value, "changed", true, CancellationToken.None);
        Assert.Null(closed.CheckedOutBy);
        Assert.Empty(await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None));
        Assert.Null((await workflow.CloseEditAsync(document.Id, "admin", UserRole.Administrator, session, first.CheckedOutAt.Value, "changed", true, CancellationToken.None)).CheckedOutBy);
        clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "WS-A", CancellationToken.None);
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.CloseEditAsync(document.Id, "admin", UserRole.Administrator, session, first.CheckedOutAt.Value, "changed", true, CancellationToken.None));
        Assert.Equal(session, (await repository.FindDocumentAsync(document.Id, CancellationToken.None))!.CheckoutSessionId);
    }

    [Fact]
    public async Task EditReleaseNotifiesAllWaitingUsersWithoutDuplicateRequests()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "AUTO-NOTIFY", "编辑通知", "AUTO.SLDPRT", DocumentKind.Part), "admin", CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var session = Guid.NewGuid();
        var checkout = await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "WS-A", CancellationToken.None);
        foreach (var actor in new[] { "waiter1", "waiter2", "waiter1" })
            await workflow.RequestEditLockReleaseAsync(document.Id, actor, UserRole.Administrator, "我需要编辑", CancellationToken.None);
        var heartbeat = await workflow.HeartbeatEditSessionAsync(session, "admin", UserRole.Administrator, "WS-A", [document.Id], CancellationToken.None);
        Assert.Contains(document.Id, heartbeat.ReleaseRequestedDocumentIds);
        await workflow.CloseEditAsync(document.Id, "admin", UserRole.Administrator, session, checkout.CheckedOutAt!.Value, "draft", true, CancellationToken.None);
        foreach (var actor in new[] { "waiter1", "waiter2" })
        {
            var notifications = await repository.ListUserNotificationsAsync(actor, 100, CancellationToken.None);
            Assert.Single(notifications, item => item.Category == "EditLockWaiting");
            Assert.Single(notifications, item => item.Category == "EditLockAvailable");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveSuppressedReferenceMustHaveArchivedVersion(bool archived)
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var part = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "SUP-P", "压缩件", "SUP-P.SLDPRT", DocumentKind.Part), "engineer", default);
        if (archived)
        {
            part = await repository.CheckoutAsync(part.Id, "engineer", default);
            part = (await repository.CheckInVersionAsync(part.Id, "engineer", Commit(project, part, ReferenceRoot(part, "engineer"), new string('A', 64), "part"), default)).Document;
        }
        var root = await repository.RegisterDocumentAsync(new Application.RegisterDocumentCommand(project.Id, "SUP-A", "装配", "SUP-A.SLDASM", DocumentKind.Assembly), "engineer", default);
        root = await repository.CheckoutAsync(root.Id, "engineer", default);
        var tree = ReferenceRoot(root, "engineer") with { Children = [ReferenceRoot(part, "engineer") with { Status = ReferenceNodeStatus.Suppressed }] };
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, root.Id, DateTimeOffset.UtcNow, "engineer", tree, new string('B', 64));
        async Task Save() => await workflow.CheckInAsync(root.Id, "engineer", UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('C', 64), DateTimeOffset.UtcNow), "root", new Dictionary<string, string?>(), snapshot, true, true, default);
        if (archived)
        {
            await Save();
            Assert.Equal(ReferenceNodeStatus.Suppressed, Assert.Single((await repository.ListDocumentVersionsAsync(root.Id, default)).Single().ReferenceSnapshot.Children).Status);
        }
        else
        {
            await Assert.ThrowsAsync<Application.PdmRuleException>(Save);
            Assert.Empty(await repository.ListDocumentVersionsAsync(root.Id, default));
        }
    }
    [Fact]
    public async Task IncrementalAdmission_AndIndependentProjectCopies_PreserveIdentityOnRetry()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var originalProject = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var copyProject = await repository.CreateProjectAsync(
            new Application.CreateProjectCommand("COPY-ADMISSION", "独立副本", "admin", @"D:\TestVault\Copy", @"D:\TestRelease\Copy"),
            "admin", CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var originalIds = new List<Guid>();
        var copiedIds = new List<Guid>();
        // First ten admitted documents, then ten new documents in that same project.
        for (var i = 0; i < 20; i++)
        {
            var command = new Application.RegisterDocumentCommand(originalProject.Id, $"PART-{i}", $"零件{i}", $"PART-{i}.SLDPRT",
                DocumentKind.Part, SourceSha256: (i + 1).ToString("X64"));
            var original = await workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, CancellationToken.None);
            originalIds.Add(original.Id);
            Assert.Equal(original.Id, (await workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, CancellationToken.None)).Id);

            var match = Assert.Single(await workflow.PreflightDocumentRegistrationAsync(copyProject.Id,
                [new("copy", command.FileName, command.Kind, command.SourceSha256!)], "admin", UserRole.Administrator, CancellationToken.None));
            Assert.Equal(DocumentRegistrationMatchKind.New, match.MatchKind);
            var copyCommand = command with { ProjectId = copyProject.Id };
            var copy = await workflow.RegisterDocumentAsync(copyCommand, "admin", UserRole.Administrator, CancellationToken.None);
            copiedIds.Add(copy.Id);
            Assert.NotEqual(original.Id, copy.Id);
            Assert.Equal(RevisionLabel.InitialWork(), copy.Revision);
            Assert.Equal(copy.Id, (await workflow.RegisterDocumentAsync(copyCommand, "admin", UserRole.Administrator, CancellationToken.None)).Id);
            Assert.Empty(await repository.ListDocumentVersionsAsync(copy.Id, CancellationToken.None));
        }
        var originals = await repository.ListDocumentsAsync(originalProject.Id, CancellationToken.None);
        var copies = await repository.ListDocumentsAsync(copyProject.Id, CancellationToken.None);
        Assert.All(originalIds, id => Assert.Single(originals, doc => doc.Id == id));
        Assert.Equal(20, copies.Count);
        Assert.Equal(40, originalIds.Concat(copiedIds).Distinct().Count());
        Assert.All(originals.Where(doc => originalIds.Contains(doc.Id)), doc =>
        {
            Assert.Equal(originalProject.Id, doc.ProjectId);
            Assert.Equal(RevisionLabel.InitialWork(), doc.Revision);
            Assert.Equal(0, doc.StoredVersionCount);
        });
    }

    [Fact]
    public async Task RegisterDocument_IsIdempotentWithinProjectByFileName()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var command = new Application.RegisterDocumentCommand(project.Id, "P-001", "Test Part", "P-001.SLDPRT", DocumentKind.Part);

        var first = await repository.RegisterDocumentAsync(command, "engineer", CancellationToken.None);
        var second = await repository.RegisterDocumentAsync(command, "engineer", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(RevisionLabel.InitialWork(), first.Revision);
    }

    [Fact]
    public async Task RegistrationPreflight_ClassifiesSameNameAndSameContentConflicts()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var sourceSha256 = new string('A', 64);
        var existing = await workflow.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "DUP-001", "Duplicate Source", "DUP-001.SLDPRT", DocumentKind.Part, SourceSha256: sourceSha256),
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        var matches = await workflow.PreflightDocumentRegistrationAsync(
            project.Id,
            [
                new("same", existing.FileName, DocumentKind.Part, sourceSha256),
                new("conflict", existing.FileName, DocumentKind.Part, new string('B', 64)),
                new("same-content-new-name", "DUP-ALIAS.SLDPRT", DocumentKind.Part, sourceSha256),
                new("new", "DUP-NEW.SLDPRT", DocumentKind.Part, new string('C', 64))
            ],
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        Assert.Equal(DocumentRegistrationMatchKind.SameNameSameContent, Assert.Single(matches, item => item.CandidateKey == "same").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, Assert.Single(matches, item => item.CandidateKey == "conflict").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.New, Assert.Single(matches, item => item.CandidateKey == "same-content-new-name").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.New, Assert.Single(matches, item => item.CandidateKey == "new").MatchKind);
    }

    [Fact]
    public async Task RegistrationPreflight_ResumesOnlyOwnedPendingFirstArchiveAndKeepsArchivedConflicts()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var pending = await workflow.RegisterDocumentAsync(new Application.RegisterDocumentCommand(
            project.Id, "PENDING", "未完成的首次存档", "PENDING.SLDASM", DocumentKind.Assembly, SourceSha256: new string('A', 64)),
            "admin", UserRole.Administrator, default);
        var changedFile = new Application.DocumentRegistrationCandidate("retry", pending.FileName, DocumentKind.Assembly, new string('B', 64));
        async Task<Application.DocumentRegistrationMatch> Match() => Assert.Single(await workflow.PreflightDocumentRegistrationAsync(
            project.Id, [changedFile], "admin", UserRole.Administrator, default));

        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, (await Match()).MatchKind);
        await repository.CheckoutAsync(pending.Id, "another-user", default);
        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, (await Match()).MatchKind);
        await repository.DiscardCheckoutAsync(pending.Id, "another-user", default);
        pending = await repository.CheckoutAsync(pending.Id, "admin", default);
        var retry = await Match();
        Assert.Equal(DocumentRegistrationMatchKind.PendingFirstArchive, retry.MatchKind);
        Assert.Equal(pending.Id, retry.ExistingDocumentId);
        Assert.Equal(project.Id, retry.ExistingProjectId);
        var wrongKind = Assert.Single(await workflow.PreflightDocumentRegistrationAsync(project.Id,
            [changedFile with { Kind = DocumentKind.Part }], "admin", UserRole.Administrator, default));
        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, wrongKind.MatchKind);
        Assert.Empty(await repository.ListDocumentVersionsAsync(pending.Id, default));

        await repository.CheckInVersionAsync(pending.Id, "admin", Commit(project, pending, ReferenceRoot(pending, "admin"),
            new string('B', 64), "重试首次存档", sourceFileSha256: new string('B', 64)), default);
        Assert.Single(await repository.ListDocumentVersionsAsync(pending.Id, default));
        Assert.Single(await repository.ListDocumentsAsync(project.Id, default), d => d.FileName == pending.FileName);
        await repository.CheckoutAsync(pending.Id, "admin", default);
        Assert.Equal(DocumentRegistrationMatchKind.SameNameSameContent, (await Match()).MatchKind);
        changedFile = changedFile with { SourceSha256 = new string('C', 64) };
        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, (await Match()).MatchKind);
    }

    [Fact]
    public async Task RegisterDocument_UsesContentFingerprintForSameNameSafetyButAllowsDifferentFileNames()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var sourceSha256 = new string('D', 64);
        var command = new Application.RegisterDocumentCommand(project.Id, "SAFE-001", "Safe Part", "SAFE-001.SLDPRT", DocumentKind.Part, SourceSha256: sourceSha256);

        var first = await workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, CancellationToken.None);
        var identical = await workflow.RegisterDocumentAsync(command, "admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(first.Id, identical.Id);

        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RegisterDocumentAsync(
            command with { SourceSha256 = new string('E', 64) },
            "admin",
            UserRole.Administrator,
            CancellationToken.None));
        var independent = await workflow.RegisterDocumentAsync(
            command with
            {
                DrawingNumber = "SAFE-ALIAS",
                FileName = "SAFE-ALIAS.SLDPRT"
            },
            "admin",
            UserRole.Administrator,
            CancellationToken.None);
        Assert.NotEqual(first.Id, independent.Id);
    }

    [Fact]
    public async Task RegisterDrawing_PersistsExplicitModelRelation()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var model = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-001", "Assembly", "A-001.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);

        var drawing = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-001", "Assembly Drawing", "A-001.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: model.Id),
            "engineer",
            CancellationToken.None);

        var relation = Assert.Single(
            await repository.ListDocumentRelationsAsync(project.Id, CancellationToken.None),
            item => item.DrawingDocumentId == drawing.Id);
        Assert.Equal(model.Id, relation.ModelDocumentId);
        Assert.Equal(drawing.Id, relation.DrawingDocumentId);
    }

    [Fact]
    public async Task RegisteredPart_FirstCheckInCreatesW1AndReleasesEditLock()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-002", "First Archive Part", "P-002.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        Assert.Equal(0, Assert.Single(await repository.ListDocumentsAsync(project.Id, CancellationToken.None), item => item.Id == document.Id).StoredVersionCount);
        var root = new DocumentReferenceNode(
            Guid.NewGuid(), document.Id, "P-002", document.FileName, document.Name, DocumentKind.Part,
            "Default", 1, ReferenceNodeStatus.Normal, null, "engineer", []);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", root, new string('B', 64));
        var commit = new Application.DocumentVersionCommit(
            new Application.StoredFile(".versions/P-002/W1/P-002.SLDPRT", 128, new string('A', 64), DateTimeOffset.UtcNow),
            "first archive", new Dictionary<string, string?>(), snapshot, [], []);

        var result = await repository.CheckInVersionAsync(document.Id, "engineer", commit, CancellationToken.None);

        var version = Assert.IsType<DocumentVersion>(result.Version);
        Assert.Equal("W1", version.Revision.Display);
        Assert.Equal("first archive", version.ChangeNote);
        Assert.Null(result.Document.CheckedOutBy);
        Assert.Equal(1, Assert.Single(await repository.ListDocumentsAsync(project.Id, CancellationToken.None), item => item.Id == document.Id).StoredVersionCount);
    }

    [Fact]
    public async Task CheckInWithSameFileHash_ReleasesEditLockWithoutCreatingVersion()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-003", new string('C', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var before = await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None);
        var root = ReferenceRoot(document, "engineer");
        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, root, new string('C', 64), "no changes"),
            CancellationToken.None);
        var after = await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None);

        Assert.False(result.VersionCreated);
        Assert.Null(result.Version);
        Assert.Equal(document.Revision, result.Document.Revision);
        Assert.Null(result.Document.CheckedOutBy);
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public async Task CheckInAfterFileRename_KeepsDocumentIdAndUpdatesCanonicalFileName()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-003-RENAME", new string('E', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        const string renamedFileName = "P-003-RENAME-NEW.SLDPRT";
        var renamedRoot = ReferenceRoot(document, "engineer") with { FileName = renamedFileName };

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(
                project,
                document,
                renamedRoot,
                new string('E', 64),
                "rename",
                forceVersion: true,
                fileName: renamedFileName),
            CancellationToken.None);

        Assert.Equal(document.Id, result.Document.Id);
        Assert.Equal(renamedFileName, result.Document.FileName);
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None)).Count);
        Assert.Single(await repository.ListDocumentsAsync(project.Id, CancellationToken.None), item => item.Id == document.Id);
    }

    [Fact]
    public async Task ProjectRootCheckInWithSameFileHash_DoesNotCreateVersion()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-003-ROOT", new string('R', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var before = await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None);

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(
                project,
                document,
                ReferenceRoot(document, "engineer"),
                new string('R', 64),
                "no root changes",
                isProjectRoot: true),
            CancellationToken.None);
        var after = await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None);

        Assert.False(result.VersionCreated);
        Assert.Null(result.Version);
        Assert.Equal("W1", result.Document.Revision.Display);
        Assert.Null(result.Document.CheckedOutBy);
        Assert.Equal(before.Count, after.Count);
    }

    [Fact]
    public async Task CompleteEditWithoutChanges_RejectsChangedFileAndDiscardCheckoutReleasesLock()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-004", new string('D', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);

        await Assert.ThrowsAsync<Application.PdmConflictException>(() =>
            repository.CompleteEditWithoutChangesAsync(document.Id, "engineer", new string('E', 64), CancellationToken.None));
        var discarded = await repository.DiscardCheckoutAsync(document.Id, "engineer", CancellationToken.None);

        Assert.Null(discarded.CheckedOutBy);
        Assert.Equal(document.Revision, discarded.Revision);
    }

    [Fact]
    public async Task CheckIn_InitialBlankChangeNoteIsAccepted()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-003", "Part 3", "P-003.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var root = new DocumentReferenceNode(
            Guid.NewGuid(), document.Id, "ROOT", document.FileName, document.DrawingNumber, document.Kind,
            "Default", 1, ReferenceNodeStatus.Normal, null, "engineer", []);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", root, new string('B', 64));
        var fileStorage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(
            repository,
            fileStorage,
            new Infrastructure.AtomicReleasePackagePublisher(TimeProvider.System),
            TimeProvider.System);

        var result = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('A', 64), DateTimeOffset.UtcNow),
            "  ",
            PreviewSourceProperties('1'),
            snapshot,
            false,
            false,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("W1", Assert.IsType<DocumentVersion>(result.Version).Revision.Display);
        Assert.Equal(string.Empty, result.Version.ChangeNote);
        Assert.Single(fileStorage.VerifiedFiles);
        Assert.Null(Assert.IsType<DocumentVersion>(result.Version).Preview);
    }

    [Fact]
    public async Task CheckIn_SubsequentBlankChangeNoteIsAccepted()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-004", new string('4', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var root = ReferenceRoot(document, "engineer");
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", root, new string('B', 64));
        var fileStorage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(
            repository,
            fileStorage,
            new Infrastructure.AtomicReleasePackagePublisher(TimeProvider.System),
            TimeProvider.System);

        var result = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('A', 64), DateTimeOffset.UtcNow),
            "  ",
            PreviewSourceProperties('2'),
            snapshot,
            false,
            false,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("W2", Assert.IsType<DocumentVersion>(result.Version).Revision.Display);
        Assert.Equal(string.Empty, result.Version.ChangeNote);
        Assert.Single(fileStorage.VerifiedFiles);
    }

    [Fact]
    public async Task WorkflowCheckIn_AfterFirstArchive_PreservesPlmPropertiesAndRefreshesFileMetadata()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-PLM-MASTER", "PLM master", "P-PLM-MASTER.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);

        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var first = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('A', 64), DateTimeOffset.UtcNow),
            "first archive",
            new Dictionary<string, string?>
            {
                ["全局/材质"] = "Q235",
                ["SourceFileSha256"] = new string('1', 64)
            },
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", ReferenceRoot(document, "engineer"), new string('B', 64)),
            false,
            false,
            CancellationToken.None);
        Assert.Equal("Q235", Assert.IsType<DocumentVersion>(first.Version).PropertySnapshot["全局/材质"]);

        document = await repository.CheckoutAsync(first.Document.Id, "engineer", CancellationToken.None);
        var second = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('C', 64), DateTimeOffset.UtcNow),
            "normal update",
            new Dictionary<string, string?>
            {
                ["全局/材质"] = "304",
                ["SourceFileSha256"] = new string('2', 64)
            },
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", ReferenceRoot(document, "engineer"), new string('D', 64)),
            false,
            false,
            CancellationToken.None,
            drawingNumber: "LOCAL-NUMBER",
            name: "Local name");

        var version = Assert.IsType<DocumentVersion>(second.Version);
        Assert.Equal("Q235", version.PropertySnapshot["全局/材质"]);
        Assert.Equal(new string('2', 64), version.PropertySnapshot["SourceFileSha256"]);
        Assert.Equal("P-PLM-MASTER", second.Document.DrawingNumber);
        Assert.Equal("PLM master", second.Document.Name);
    }

    [Fact]
    public async Task WorkflowCheckIn_FormalWriteback_MergesPlmPropertiesIntoNewVersion()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-WRITEBACK", "Writeback part", "P-WRITEBACK.SLDPRT", DocumentKind.Part),
            "admin",
            CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);

        document = await repository.CheckoutAsync(document.Id, "admin", CancellationToken.None);
        var first = await workflow.CheckInAsync(
            document.Id,
            "admin",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('A', 64), DateTimeOffset.UtcNow),
            "first archive",
            new Dictionary<string, string?>
            {
                ["配置:Default/材质"] = "Q235",
                ["SourceFileSha256"] = new string('1', 64)
            },
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "admin", ReferenceRoot(document, "admin"), new string('B', 64)),
            false,
            false,
            CancellationToken.None);
        var firstVersion = Assert.IsType<DocumentVersion>(first.Version);
        var writeback = new CadPropertyWriteback(
            Guid.NewGuid(),
            project.Id,
            Guid.NewGuid(),
            document.Id,
            "Default",
            firstVersion.Id,
            firstVersion.Revision.Display,
            new Dictionary<string, string?> { ["材质"] = "304" },
            CadPropertyWritebackStatus.Pending,
            "admin",
            DateTimeOffset.UtcNow);
        await repository.EnqueueCadPropertyWritebackAsync(writeback, CancellationToken.None);
        await workflow.StartCadPropertyWritebackAsync(writeback.Id, "admin", UserRole.Administrator, CancellationToken.None);

        var sessionId = Guid.NewGuid();
        document = await repository.CheckoutAsync(
            document.Id,
            "admin",
            sessionId,
            "TEST-WS",
            DateTimeOffset.UtcNow.AddMinutes(15),
            writeback.Id,
            CancellationToken.None);
        var result = await workflow.CheckInAsync(
            document.Id,
            "admin",
            UserRole.Administrator,
            sessionId,
            new Application.StoredFile("unused", 1, new string('C', 64), DateTimeOffset.UtcNow),
            "PLM writeback",
            new Dictionary<string, string?>
            {
                ["配置:Default/材质"] = "LOCAL",
                ["SourceFileSha256"] = new string('2', 64)
            },
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "admin", ReferenceRoot(document, "admin"), new string('D', 64)),
            false,
            false,
            CancellationToken.None,
            drawingReviewWritebackId: writeback.Id);

        var version = Assert.IsType<DocumentVersion>(result.Version);
        Assert.Equal("304", version.PropertySnapshot["配置:Default/材质"]);
        Assert.Equal(new string('2', 64), version.PropertySnapshot["SourceFileSha256"]);
    }

    [Fact]
    public async Task CheckIn_DoesNotRequireOrStorePreviewArtifact()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-PREVIEW-MISMATCH", "Preview mismatch", "P-PREVIEW-MISMATCH.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", ReferenceRoot(document, "engineer"), new string('F', 64));
        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);

        var result = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile(".versions/model.SLDPRT", 128, new string('A', 64), DateTimeOffset.UtcNow),
            "preview mismatch",
            PreviewSourceProperties('1'),
            snapshot,
            false,
            false,
            CancellationToken.None);

        Assert.Single(storage.VerifiedFiles);
        Assert.Null(Assert.IsType<DocumentVersion>(result.Version).Preview);
        Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CheckIn_DrawingStoresSourceOnly()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "D-PDF", "Drawing PDF", "D-PDF.SLDDRW", DocumentKind.Drawing),
            "engineer",
            CancellationToken.None);
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);

        var result = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile(".versions/D-PDF.SLDDRW", 128, new string('A', 64), DateTimeOffset.UtcNow),
            "drawing preview",
            PreviewSourceProperties('3'),
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", ReferenceRoot(document, "engineer"), new string('F', 64)),
            false,
            false,
            CancellationToken.None);

        Assert.Null(Assert.IsType<DocumentVersion>(result.Version).Preview);
        Assert.Single(storage.VerifiedFiles);
    }

    [Fact]
    public void Compare_ReportsPropertyReferenceQuantityAndBomChanges()
    {
        var documentId = Guid.NewGuid();
        var left = Version(documentId, "W1", "Q235", 1, 1, "Q235", "A");
        var right = Version(documentId, "W2", "304", 2, 3, "304", "B");

        var result = DocumentVersionDiff.Compare(left, right);

        Assert.Contains(result.PropertyChanges, change => change.Name == "Material" && change.Kind == SnapshotChangeKind.Modified);
        Assert.Contains(result.ReferenceChanges, change => change.Kind == ReferenceChangeKind.QuantityChanged);
        Assert.Contains(result.BomChanges, change => change.Kind == BomChangeKind.QuantityChanged);
        Assert.Contains(result.BomChanges, change => change.Kind == BomChangeKind.MaterialChanged);
        Assert.Contains(result.BomChanges, change => change.Kind == BomChangeKind.RevisionChanged);
    }

    [Fact]
    public void Compare_ToleratesDuplicateDrawingNumbersInBomSnapshot()
    {
        var documentId = Guid.NewGuid();
        var leftBom = new[]
        {
            new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Mechanical, 1, "P-001", "Part", 2, "件", "Q235", "10", "A", true),
            new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Mechanical, 2, "P-001", "Part", 3, "件", "Q235", "10", "A", true),
        };
        var rightBom = new[]
        {
            new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Mechanical, 1, "P-001", "Part", 4, "件", "Q235", "10", "A", true),
            new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Mechanical, 2, "P-001", "Part", 3, "件", "Q235", "10", "A", true),
        };
        var left = Version(documentId, "A-W1", "Q235", 1, 2, "Q235", "A") with { MechanicalBomSnapshot = leftBom };
        var right = Version(documentId, "B", "Q235", 1, 4, "Q235", "A") with { MechanicalBomSnapshot = rightBom };

        var result = DocumentVersionDiff.Compare(left, right);

        var quantityChange = Assert.Single(result.BomChanges, change => change.Kind == BomChangeKind.QuantityChanged);
        Assert.Equal("P-001", quantityChange.DrawingNumber);
        Assert.Equal("5", quantityChange.PreviousValue);
        Assert.Equal("7", quantityChange.CurrentValue);
    }

    [Fact]
    public async Task CheckInWithSameFileHash_ReferenceChangeCreatesNextWorkVersion()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-006", new string('6', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer") with { Configuration = "Changed" }, new string('6', 64), "reference changed", forceVersion: true),
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("W2", Assert.IsType<DocumentVersion>(result.Version).Revision.Display);
    }

    [Fact]
    public async Task CheckInVersion_WithBatchPropertyIdentity_UpdatesPdmDrawingNumberAndName()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-PROPERTY-OLD", new string('A', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(
                project,
                document,
                ReferenceRoot(document, "engineer"),
                new string('B', 64),
                "batch property update",
                drawingNumber: "P-PROPERTY-NEW",
                name: "Updated fixture"),
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("P-PROPERTY-NEW", result.Document.DrawingNumber);
        Assert.Equal("Updated fixture", result.Document.Name);
        Assert.Null(result.Document.CheckedOutBy);
    }

    [Fact]
    public async Task WorkflowCheckIn_AfterFirstArchive_IgnoresLocalIdentityChanges()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-AUDIT-OLD", new string('C', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(),
            project.Id,
            document.Id,
            DateTimeOffset.UtcNow,
            "engineer",
            ReferenceRoot(document, "engineer"),
            new string('D', 64));
        var workflow = new Application.PdmWorkflowService(
            repository,
            new RecordingFileStorage(),
            new Infrastructure.AtomicReleasePackagePublisher(TimeProvider.System),
            TimeProvider.System);

        var result = await workflow.CheckInAsync(
            document.Id,
            "engineer",
            UserRole.Administrator,
            document.CheckoutSessionId!.Value,
            new Application.StoredFile("unused", 1, new string('D', 64), DateTimeOffset.UtcNow),
            "batch property update",
            PreviewSourceProperties('3'),
            snapshot,
            false,
            true,
            CancellationToken.None,
            "P-AUDIT-NEW",
            "Updated audit name");

        Assert.Equal("P-AUDIT-OLD", result.Document.DrawingNumber);
        Assert.Equal("P-AUDIT-OLD", result.Document.Name);
        var audits = await repository.ListAuditAsync("engineer", UserRole.Administrator, 100, CancellationToken.None);
        Assert.DoesNotContain(audits, entry => entry.Action == "document.identity.update");
    }

    [Fact]
    public async Task ProjectRootAndChildCheckIn_RefreshSourceSnapshotAndMechanicalBom()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var projectRoot = await repository.GetReferenceTreeAsync(project.Id, CancellationToken.None);
        Assert.NotNull(projectRoot);
        var child = projectRoot.Children.First(node => node.DocumentId.HasValue);
        var childId = child.DocumentId!.Value;
        var sessionId = Guid.NewGuid();
        await repository.CheckoutAsync(childId, "admin", sessionId, "test-machine", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(), project.Id, childId, DateTimeOffset.UtcNow, "admin", child, new string('B', 64));
        var workflow = new Application.PdmWorkflowService(
            repository,
            new RecordingFileStorage(),
            new Infrastructure.AtomicReleasePackagePublisher(TimeProvider.System),
            TimeProvider.System);

        var result = await workflow.CheckInAsync(
            childId,
            "admin",
            UserRole.Administrator,
            sessionId,
            new Application.StoredFile("unused", 1, new string('C', 64), DateTimeOffset.UtcNow),
            "更新子件属性",
            new Dictionary<string, string?>
            {
                ["SourceFileSha256"] = new string('4', 64),
                ["物料分类"] = "标准件",
                ["物料编码"] = "AUTO-BOM-001",
                ["物料名称"] = "自动更新标准组件",
                ["型号"] = "M12",
                ["单位"] = "个"
            },
            snapshot,
            false,
            true,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Null(result.BomUpdateError);
        var childBomUpdate = Assert.IsType<Application.BomGenerationResult>(result.BomUpdate);
        Assert.True(childBomUpdate.Applied);
        Assert.Contains(childBomUpdate.StandardItems, item => item.SourceDocumentId == childId && item.DrawingNumber == "AUTO-BOM-001");

        var mechanicalBeforeRoot = (await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.Unclassified, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.Virtual, CancellationToken.None))
            .ToArray();

        var rootId = Assert.IsType<Guid>(projectRoot.DocumentId);
        var rootDocument = Assert.Single(
            await repository.ListDocumentsAsync(project.Id, CancellationToken.None),
            document => document.Id == rootId);
        if (!string.IsNullOrWhiteSpace(rootDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(rootId, "admin", "测试根BOM统一刷新", CancellationToken.None);
        const string rootActor = "admin";
        var rootSessionId = Guid.NewGuid();
        await repository.CheckoutAsync(rootId, rootActor, rootSessionId, "test-machine", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var rootResult = await workflow.CheckInAsync(
            rootId,
            rootActor,
            UserRole.Administrator,
            rootSessionId,
            new Application.StoredFile("unused-root", 1, new string('D', 64), DateTimeOffset.UtcNow),
            "根装配体统一刷新BOM",
            PreviewSourceProperties('5'),
            new CadReferenceSnapshot(
                Guid.NewGuid(), project.Id, rootId, DateTimeOffset.UtcNow, rootActor, NormalizeReferenceStatus(projectRoot), new string('E', 64)),
            true,
            true,
            CancellationToken.None);

        Assert.True(rootResult.VersionCreated);
        Assert.Null(rootResult.BomUpdateError);
        Assert.True(Assert.IsType<Application.BomGenerationResult>(rootResult.BomUpdate).Applied);
        var mechanicalAfterRoot = (await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.Unclassified, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.Virtual, CancellationToken.None))
            .ToArray();
        Assert.Equal(
            mechanicalBeforeRoot.Select(item => (item.Id, item.Kind, item.DrawingNumber, item.Quantity)),
            mechanicalAfterRoot.Select(item => (item.Id, item.Kind, item.DrawingNumber, item.Quantity)));

        var refreshed = Assert.Single(
            await workflow.GetBomSourceDataAsync(project.Id, rootActor, UserRole.Administrator, CancellationToken.None),
            item => item.SourceDocumentId == childId);
        Assert.Equal("AUTO-BOM-001", refreshed.DrawingNumber);
        Assert.Equal("自动更新标准组件", refreshed.Name);
        Assert.NotNull(refreshed.ReconciliationStatus);
        Assert.NotNull(refreshed.ReconciliationNote);
    }

    [Fact]
    public async Task CheckIn_DuringReleaseApproval_PreservesLockedBomAndReportsRefreshError()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var projectRoot = Assert.IsType<DocumentReferenceNode>(await repository.GetReferenceTreeAsync(project.Id, CancellationToken.None));
        var child = projectRoot.Children.First(node => node.DocumentId.HasValue);
        var childId = child.DocumentId!.Value;
        var before = (await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None))
            .Select(item => (item.Id, item.Kind, item.DrawingNumber, item.Quantity))
            .ToArray();
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), project.Id, "RP-CHECKIN-LOCK", ReleasePackageState.ProcessReview,
            Guid.NewGuid(), "W1", "W1", [], DateTimeOffset.UtcNow, null, null)
        {
            Scope = ReleaseScope.StandardSupplement
        }, CancellationToken.None);
        var sessionId = Guid.NewGuid();
        await repository.CheckoutAsync(
            childId, "admin", sessionId, "test-machine", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(
            repository,
            new RecordingFileStorage(),
            new Infrastructure.AtomicReleasePackagePublisher(TimeProvider.System),
            TimeProvider.System);

        var result = await workflow.CheckInAsync(
            childId,
            "admin",
            UserRole.Administrator,
            sessionId,
            new Application.StoredFile("unused", 1, new string('F', 64), DateTimeOffset.UtcNow),
            "审批期间更新图档",
            new Dictionary<string, string?>
            {
                ["SourceFileSha256"] = new string('6', 64),
                ["物料分类"] = "标准件",
                ["物料编码"] = "LOCKED-BOM-001",
                ["物料名称"] = "审批锁定测试组件",
                ["型号"] = "M16",
                ["单位"] = "个"
            },
            new CadReferenceSnapshot(
                Guid.NewGuid(), project.Id, childId, DateTimeOffset.UtcNow, "admin", child, new string('E', 64)),
            false,
            true,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Null(result.BomUpdate);
        Assert.Contains("标准件BOM已锁定", result.BomUpdateError);
        var after = (await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None))
            .Concat(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None))
            .Select(item => (item.Id, item.Kind, item.DrawingNumber, item.Quantity))
            .ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task CheckInWithSameSourceHash_DoesNotCreateVersionWhenArchiveCopyHashDiffers()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-007", "Source Hash Part", "P-007.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        document = (await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer"), new string('A', 64), "first", sourceFileSha256: new string('S', 64)),
            CancellationToken.None)).Document;
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer"), new string('B', 64), "unchanged", sourceFileSha256: new string('S', 64)),
            CancellationToken.None);

        Assert.False(result.VersionCreated);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task ChildCheckIn_DoesNotReplaceExplicitProjectRoot()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-ROOT", "Root Assembly", "A-ROOT.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var part = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-CHILD", "Child Part", "P-CHILD.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        part = await repository.CheckoutAsync(part.Id, "engineer", CancellationToken.None);
        var child = ReferenceRoot(part, "engineer") with { InstancePath = "A-ROOT/P-CHILD" };
        var root = ReferenceRoot(assembly, "engineer") with { Children = [child] };

        await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, root, new string('7', 64), "root archive", isProjectRoot: true),
            CancellationToken.None);
        await repository.CheckInVersionAsync(
            part.Id,
            "engineer",
            Commit(project, part, child, new string('8', 64), "child archive"),
            CancellationToken.None);

        var projectTree = await repository.GetReferenceTreeAsync(project.Id, CancellationToken.None);
        Assert.NotNull(projectTree);
        Assert.Equal(assembly.Id, projectTree.DocumentId);
        Assert.Single(projectTree.Children);
        var currentSnapshot = await repository.GetLatestReferenceSnapshotAsync(project.Id, CancellationToken.None);
        Assert.Equal(assembly.Id, currentSnapshot?.RootDocumentId);
    }

    [Fact]
    public async Task SubassemblyCheckIn_OnlyCreatesSubassemblyVersionAndKeepsCompleteProjectTree()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var rootAssembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-COMPLETE", "Complete Assembly", "A-COMPLETE.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var subassembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-SUB", "Subassembly", "A-SUB.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var sibling = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "P-SIBLING", "Sibling Part", "P-SIBLING.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);

        subassembly = await repository.CheckoutAsync(subassembly.Id, "engineer", CancellationToken.None);
        subassembly = (await repository.CheckInVersionAsync(
            subassembly.Id,
            "engineer",
            Commit(project, subassembly, ReferenceRoot(subassembly, "engineer"), new string('1', 64), "subassembly W1"),
            CancellationToken.None)).Document;
        sibling = await repository.CheckoutAsync(sibling.Id, "engineer", CancellationToken.None);
        sibling = (await repository.CheckInVersionAsync(
            sibling.Id,
            "engineer",
            Commit(project, sibling, ReferenceRoot(sibling, "engineer"), new string('2', 64), "sibling W1"),
            CancellationToken.None)).Document;
        rootAssembly = await repository.CheckoutAsync(rootAssembly.Id, "engineer", CancellationToken.None);
        var subassemblyReference = ReferenceRoot(subassembly, "engineer") with { InstancePath = "A-COMPLETE/A-SUB" };
        var siblingReference = ReferenceRoot(sibling, "engineer") with { InstancePath = "A-COMPLETE/P-SIBLING" };
        var completeRoot = ReferenceRoot(rootAssembly, "engineer") with { Children = [subassemblyReference, siblingReference] };
        rootAssembly = (await repository.CheckInVersionAsync(
            rootAssembly.Id,
            "engineer",
            Commit(project, rootAssembly, completeRoot, new string('3', 64), "complete root", isProjectRoot: true),
            CancellationToken.None)).Document;

        subassembly = await repository.CheckoutAsync(subassembly.Id, "engineer", CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var result = await workflow.CheckInAsync(
            subassembly.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('4', 64), DateTimeOffset.UtcNow),
            "subassembly W2",
            PreviewSourceProperties('5'),
            new CadReferenceSnapshot(
                Guid.NewGuid(),
                project.Id,
                subassembly.Id,
                DateTimeOffset.UtcNow,
                "engineer",
                ReferenceRoot(subassembly, "engineer"),
                new string('5', 64)),
            false,
            true,
            CancellationToken.None);

        Assert.Equal("W2", Assert.IsType<DocumentVersion>(result.Version).Revision.Display);
        var projectTree = Assert.IsType<DocumentReferenceNode>(
            await repository.GetReferenceTreeAsync(project.Id, CancellationToken.None));
        Assert.Equal(rootAssembly.Id, projectTree.DocumentId);
        Assert.Equal(2, projectTree.Children.Count);
        Assert.Contains(projectTree.Children, node => node.DocumentId == subassembly.Id);
        Assert.Contains(projectTree.Children, node => node.DocumentId == sibling.Id);
        var currentSnapshot = await repository.GetLatestReferenceSnapshotAsync(project.Id, CancellationToken.None);
        Assert.Equal(rootAssembly.Id, currentSnapshot?.RootDocumentId);
    }

    [Fact]
    public async Task WorkflowCheckIn_CanAdmitSubassemblyAsIndependentRootWithoutReplacingExistingRoot()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var rootAssembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-GUARD", "Guard Root", "A-GUARD.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var subassembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-GUARD-SUB", "Guard Subassembly", "A-GUARD-SUB.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        rootAssembly = await repository.CheckoutAsync(rootAssembly.Id, "engineer", CancellationToken.None);
        var root = ReferenceRoot(rootAssembly, "engineer") with
        {
            Children = [ReferenceRoot(subassembly, "engineer") with { InstancePath = "A-GUARD/A-GUARD-SUB" }]
        };
        await repository.CheckInVersionAsync(
            rootAssembly.Id,
            "engineer",
            Commit(project, rootAssembly, root, new string('6', 64), "guard root", isProjectRoot: true),
            CancellationToken.None);

        subassembly = await repository.CheckoutAsync(subassembly.Id, "engineer", CancellationToken.None);
        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var result = await workflow.CheckInAsync(
            subassembly.Id,
            "engineer",
            UserRole.Administrator,
            new Application.StoredFile("unused", 1, new string('7', 64), DateTimeOffset.UtcNow),
            "must not replace root",
            new Dictionary<string, string?>(),
            new CadReferenceSnapshot(
                Guid.NewGuid(),
                project.Id,
                subassembly.Id,
                DateTimeOffset.UtcNow,
                "engineer",
                ReferenceRoot(subassembly, "engineer"),
                new string('8', 64)),
            true,
            true,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Single(storage.VerifiedFiles);
        var currentSnapshot = await repository.GetLatestReferenceSnapshotAsync(project.Id, CancellationToken.None);
        Assert.Contains(ProjectReferenceForest.Roots(currentSnapshot!), node => node.DocumentId == rootAssembly.Id);
        Assert.Contains(ProjectReferenceForest.Roots(currentSnapshot!), node => node.DocumentId == subassembly.Id);
    }

    [Fact]
    public async Task ControlledOpenManifest_UsesExactReferencedVersionsAndValidatesEveryFileMetadata()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var part = await RegisterAndCheckInAsync(repository, project, "P-MANIFEST", new string('1', 64));
        part = await repository.CheckoutAsync(part.Id, "engineer", CancellationToken.None);
        part = (await repository.CheckInVersionAsync(
            part.Id,
            "engineer",
            Commit(project, part, ReferenceRoot(part, "engineer"), new string('2', 64), "part W2"),
            CancellationToken.None)).Document;

        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-MANIFEST", "Manifest Assembly", "A-MANIFEST.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        var child = ReferenceRoot(part, "engineer") with
        {
            InstancePath = "A-MANIFEST/P-MANIFEST",
            Revision = RevisionLabel.Parse("W1")
        };
        var root = new DocumentReferenceNode(
            Guid.NewGuid(), assembly.Id, "A-MANIFEST", assembly.FileName, assembly.Name, DocumentKind.Assembly,
            "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "engineer", [child]);
        assembly = (await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, root, new string('3', 64), "assembly W1", isProjectRoot: true),
            CancellationToken.None)).Document;

        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal(project.Id, manifest.ProjectId);
        Assert.Equal("W1", manifest.RootRevision);
        Assert.Equal("A-MANIFEST.SLDASM", manifest.RootRelativePath);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Contains(manifest.Files, file => file.IsRoot && file.Revision == "W1" && file.Sha256 == new string('3', 64));
        Assert.Contains(manifest.Files, file => file.DocumentId == part.Id && file.Revision == "W1" && file.Sha256 == new string('1', 64));
        Assert.Equal(2, storage.MetadataValidatedFiles.Count);
        Assert.Empty(storage.VerifiedFiles);
    }

    [Fact]
    public async Task ControlledOpenManifest_RequiresConfirmationForLegacyConflictsAndHistoricalStaysStrict()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var part = await RegisterAndCheckInAsync(repository, project, "P-CONFLICT", new string('1', 64));
        part = await repository.CheckoutAsync(part.Id, "engineer", CancellationToken.None);
        part = (await repository.CheckInVersionAsync(
            part.Id,
            "engineer",
            Commit(project, part, ReferenceRoot(part, "engineer"), new string('2', 64), "part W2"),
            CancellationToken.None)).Document;

        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-CONFLICT", "Conflict Assembly", "A-CONFLICT.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        var firstInstance = ReferenceRoot(part, "engineer") with
        {
            NodeId = Guid.NewGuid(),
            InstancePath = "A-CONFLICT/P-CONFLICT-1",
            Revision = RevisionLabel.Parse("W1")
        };
        var secondInstance = firstInstance with
        {
            NodeId = Guid.NewGuid(),
            InstancePath = "A-CONFLICT/P-CONFLICT-2",
            Revision = RevisionLabel.Parse("W2")
        };
        var root = new DocumentReferenceNode(
            Guid.NewGuid(), assembly.Id, "A-CONFLICT", assembly.FileName, assembly.Name, DocumentKind.Assembly,
            "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "engineer", [firstInstance, secondInstance]);
        assembly = (await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, root, new string('3', 64), "assembly W1", isProjectRoot: true),
            CancellationToken.None)).Document;

        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None));
        var currentManifest = await workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None, true);

        Assert.Equal(2, currentManifest.Files.Count);
        Assert.Contains(currentManifest.Files, file => file.DocumentId == part.Id && file.Revision == "W2" && file.Sha256 == new string('2', 64));
        Assert.Contains(currentManifest.Warnings, warning =>
            warning.Contains("P-CONFLICT.SLDPRT", StringComparison.Ordinal)
            && warning.Contains("最新受控版本W2", StringComparison.Ordinal));

        var assemblyVersion = Assert.Single(await repository.ListDocumentVersionsAsync(assembly.Id, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.CreateControlledOpenManifestAsync(
            assembly.Id, assemblyVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("快照中引用了不同版本", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlledOpenManifest_CurrentLatestUsesLatestReferencedVersionWhenLegacySnapshotRevisionMissing()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var part = await RegisterAndCheckInAsync(repository, project, "P-LEGACY", new string('4', 64));
        part = await repository.CheckoutAsync(part.Id, "engineer", CancellationToken.None);
        part = (await repository.CheckInVersionAsync(
            part.Id,
            "engineer",
            Commit(project, part, ReferenceRoot(part, "engineer"), new string('5', 64), "part W2"),
            CancellationToken.None)).Document;

        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "A-LEGACY", "Legacy Assembly", "A-LEGACY.SLDASM", DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        var child = ReferenceRoot(part, "engineer") with
        {
            InstancePath = "A-LEGACY/P-LEGACY",
            Revision = null
        };
        var childWithoutDocumentId = child with
        {
            NodeId = Guid.NewGuid(),
            DocumentId = null,
            InstancePath = "A-LEGACY/P-LEGACY-2"
        };
        var root = new DocumentReferenceNode(
            Guid.NewGuid(), assembly.Id, "A-LEGACY", assembly.FileName, assembly.Name, DocumentKind.Assembly,
            "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "engineer", [child, childWithoutDocumentId]);
        assembly = (await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, root, new string('6', 64), "legacy assembly", isProjectRoot: true),
            CancellationToken.None)).Document;

        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var currentManifest = await workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        Assert.Contains(currentManifest.Files, file => file.DocumentId == part.Id && file.Revision == "W2" && file.Sha256 == new string('5', 64));
        Assert.Equal(2, currentManifest.Files.Count);

        var assemblyVersion = Assert.Single(await repository.ListDocumentVersionsAsync(assembly.Id, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.CreateControlledOpenManifestAsync(
            assembly.Id, assemblyVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("快照未记录版本", exception.Message);
    }

    [Fact]
    public async Task ControlledOpenManifest_CurrentLatestIncludesRegisteredDrawingOmittedFromSnapshot()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var part = await RegisterAndCheckInAsync(repository, project, "P-WITH-DRAWING", new string('7', 64));
        var drawing = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "P-WITH-DRAWING",
                "Part Drawing",
                "P-WITH-DRAWING.SLDDRW",
                DocumentKind.Drawing,
                RelatedModelDocumentId: part.Id),
            "engineer",
            CancellationToken.None);
        drawing = await repository.CheckoutAsync(drawing.Id, "engineer", CancellationToken.None);
        var drawingRoot = new DocumentReferenceNode(
            Guid.NewGuid(), drawing.Id, "P-WITH-DRAWING-DRAWING", drawing.FileName, drawing.Name,
            DocumentKind.Drawing, "图纸", 1, ReferenceNodeStatus.Normal, drawing.Revision, "engineer", []);
        drawing = (await repository.CheckInVersionAsync(
            drawing.Id,
            "engineer",
            Commit(project, drawing, drawingRoot, new string('8', 64), "drawing W1"),
            CancellationToken.None)).Document;

        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var currentManifest = await workflow.CreateControlledOpenManifestAsync(
            part.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal(2, currentManifest.Files.Count);
        Assert.Contains(currentManifest.Files, file => file.DocumentId == drawing.Id && file.Revision == "W1" && file.Sha256 == new string('8', 64));
        Assert.Equal(2, storage.MetadataValidatedFiles.Count);

        var partVersion = Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, CancellationToken.None));
        var historicalManifest = await workflow.CreateControlledOpenManifestAsync(
            part.Id, partVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None);
        Assert.Single(historicalManifest.Files);
    }

    [Fact]
    public async Task ControlledOpenManifest_HistoricalVersionIncludesDrawingRecordedInSnapshot()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var part = await RegisterAndCheckInAsync(repository, project, "P-HISTORICAL-DRAWING", new string('9', 64));
        var drawing = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "P-HISTORICAL-DRAWING",
                "Historical Drawing",
                "P-HISTORICAL-DRAWING.SLDDRW",
                DocumentKind.Drawing,
                RelatedModelDocumentId: part.Id),
            "engineer",
            CancellationToken.None);
        drawing = await repository.CheckoutAsync(drawing.Id, "engineer", CancellationToken.None);
        var drawingRoot = new DocumentReferenceNode(
            Guid.NewGuid(), drawing.Id, "P-HISTORICAL-DRAWING-DRAWING", drawing.FileName, drawing.Name,
            DocumentKind.Drawing, "Drawing", 1, ReferenceNodeStatus.Normal, drawing.Revision, "engineer", []);
        drawing = (await repository.CheckInVersionAsync(
            drawing.Id,
            "engineer",
            Commit(project, drawing, drawingRoot, new string('A', 64), "drawing W1"),
            CancellationToken.None)).Document;

        part = await repository.CheckoutAsync(part.Id, "engineer", CancellationToken.None);
        var drawingChild = drawingRoot with { InstancePath = "P-HISTORICAL-DRAWING/P-HISTORICAL-DRAWING.SLDDRW" };
        var partRoot = ReferenceRoot(part, "engineer") with { Children = [drawingChild] };
        part = (await repository.CheckInVersionAsync(
            part.Id,
            "engineer",
            Commit(project, part, partRoot, new string('B', 64), "part W2"),
            CancellationToken.None)).Document;

        var partVersion = Assert.Single(
            await repository.ListDocumentVersionsAsync(part.Id, CancellationToken.None),
            version => version.Revision.Display == "W2");
        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(
            part.Id, partVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal(2, manifest.Files.Count);
        Assert.Contains(manifest.Files, file => file.DocumentId == drawing.Id && file.Revision == "W1");
        Assert.Equal(2, storage.MetadataValidatedFiles.Count);
    }

    [Fact]
    public async Task ControlledOpenManifest_CurrentLatestSkipsUnarchivedDrawingButHistoricalStaysStrict()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "A-UNARCHIVED-DRAWING",
                "Assembly With Unarchived Drawing",
                "A-UNARCHIVED-DRAWING.SLDASM",
                DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var drawing = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "D-UNARCHIVED",
                "Unarchived Drawing",
                "顶玻璃.SLDDRW",
                DocumentKind.Drawing,
                RelatedModelDocumentId: assembly.Id),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        var drawingChild = new DocumentReferenceNode(
            Guid.NewGuid(), drawing.Id, "A-UNARCHIVED-DRAWING/D-UNARCHIVED", drawing.FileName, drawing.Name,
            DocumentKind.Drawing, "图纸", 1, ReferenceNodeStatus.Normal, drawing.Revision, "engineer", []);
        var assemblyRoot = new DocumentReferenceNode(
            Guid.NewGuid(), assembly.Id, assembly.DrawingNumber, assembly.FileName, assembly.Name,
            DocumentKind.Assembly, "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "engineer", [drawingChild]);
        assembly = (await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, assemblyRoot, new string('C', 64), "assembly W1", isProjectRoot: true),
            CancellationToken.None)).Document;

        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var currentManifest = await workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        var currentFile = Assert.Single(currentManifest.Files);
        Assert.True(currentFile.IsRoot);
        Assert.Equal(assembly.Id, currentFile.DocumentId);
        Assert.Single(storage.MetadataValidatedFiles);
        Assert.Contains(currentManifest.Warnings, warning => warning.Contains("顶玻璃.SLDDRW", StringComparison.Ordinal));

        var assemblyVersion = Assert.Single(await repository.ListDocumentVersionsAsync(assembly.Id, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<Application.PdmNotFoundException>(() => workflow.CreateControlledOpenManifestAsync(
            assembly.Id, assemblyVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("顶玻璃.SLDDRW", exception.Message);
        Assert.Contains("W1", exception.Message);
    }

    [Fact]
    public async Task ControlledOpenManifest_CurrentLatestSkipsUnarchivedPartButHistoricalStaysStrict()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var assembly = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "A-UNARCHIVED-PART",
                "Assembly With Unarchived Part",
                "A-UNARCHIVED-PART.SLDASM",
                DocumentKind.Assembly),
            "engineer",
            CancellationToken.None);
        var part = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(
                project.Id,
                "P-UNARCHIVED",
                "Unarchived Part",
                "标签机钣金.SLDPRT",
                DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        assembly = await repository.CheckoutAsync(assembly.Id, "engineer", CancellationToken.None);
        var partChild = new DocumentReferenceNode(
            Guid.NewGuid(), part.Id, "A-UNARCHIVED-PART/P-UNARCHIVED", part.FileName, part.Name,
            DocumentKind.Part, "Default", 1, ReferenceNodeStatus.Normal, part.Revision, "engineer", []);
        var assemblyRoot = new DocumentReferenceNode(
            Guid.NewGuid(), assembly.Id, assembly.DrawingNumber, assembly.FileName, assembly.Name,
            DocumentKind.Assembly, "Default", 1, ReferenceNodeStatus.Normal, assembly.Revision, "engineer", [partChild]);
        assembly = (await repository.CheckInVersionAsync(
            assembly.Id,
            "engineer",
            Commit(project, assembly, assemblyRoot, new string('D', 64), "assembly W1", isProjectRoot: true),
            CancellationToken.None)).Document;

        var storage = new RecordingFileStorage();
        var workflow = new Application.PdmWorkflowService(repository, storage, new NoOpPublisher(), TimeProvider.System);
        var currentManifest = await workflow.CreateControlledOpenManifestAsync(
            assembly.Id, null, false, false, "engineer", UserRole.Administrator, CancellationToken.None);

        var currentFile = Assert.Single(currentManifest.Files);
        Assert.True(currentFile.IsRoot);
        Assert.Equal(assembly.Id, currentFile.DocumentId);
        Assert.Contains(currentManifest.Warnings, warning => warning.Contains("标签机钣金.SLDPRT", StringComparison.Ordinal));

        var assemblyVersion = Assert.Single(await repository.ListDocumentVersionsAsync(assembly.Id, CancellationToken.None));
        var exception = await Assert.ThrowsAsync<Application.PdmNotFoundException>(() => workflow.CreateControlledOpenManifestAsync(
            assembly.Id, assemblyVersion.Id, false, false, "engineer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("标签机钣金.SLDPRT", exception.Message);
        Assert.Contains("W1", exception.Message);
    }

    private static DocumentVersion Version(Guid documentId, string revision, string propertyMaterial, int referenceQuantity, decimal bomQuantity, string bomMaterial, string bomRevision)
    {
        var root = new DocumentReferenceNode(Guid.NewGuid(), documentId, "ROOT", "ROOT.SLDASM", "ROOT", DocumentKind.Assembly, "Default", referenceQuantity, ReferenceNodeStatus.Normal, RevisionLabel.Parse(revision), null, []);
        var bom = new BomItem(Guid.NewGuid(), Guid.NewGuid(), BomKind.Mechanical, 1, "P-001", "Part", bomQuantity, "件", bomMaterial, "10", bomRevision, true);
        return new DocumentVersion(Guid.NewGuid(), documentId, RevisionLabel.Parse(revision), DocumentVersionStatus.Work, "version/file", 10, new string('A', 64), "engineer", DateTimeOffset.UtcNow, revision, new Dictionary<string, string?> { ["Material"] = propertyMaterial }, root, [bom], [], null, null, null, null);
    }

    [Fact]
    public async Task CheckoutSession_RejectsSecondSessionAndHeartbeatReportsReleasedLock()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "LOCK-SESSION", "Session Part", "LOCK-SESSION.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        document = await repository.CheckoutAsync(document.Id, "engineer", firstSession, "WS-A", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);

        await Assert.ThrowsAsync<Application.PdmConflictException>(() =>
            repository.CheckoutAsync(document.Id, "engineer", secondSession, "WS-B", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));
        var active = await repository.HeartbeatCheckoutSessionAsync(firstSession, "engineer", "WS-A", [document.Id], DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);
        Assert.Contains(document.Id, active);

        await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "超时释放", CancellationToken.None);
        await repository.CheckoutAsync(document.Id, "engineer", secondSession, "WS-B", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);
        var staleHeartbeat = await repository.HeartbeatCheckoutSessionAsync(firstSession, "engineer", "WS-A", [document.Id], DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);

        Assert.DoesNotContain(document.Id, staleHeartbeat);
        await Assert.ThrowsAsync<Application.PdmConflictException>(() =>
            repository.DiscardCheckoutAsync(document.Id, "engineer", firstSession, CancellationToken.None));
    }

    [Fact]
    public async Task CheckoutSession_ReclaimsSessionFromSameUserAndMachine()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        var repository = new Infrastructure.InMemoryPdmRepository(clock);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "LOCK-RECLAIM", "Reclaim Part", "LOCK-RECLAIM.SLDPRT", DocumentKind.Part),
            "admin",
            CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), clock);
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();

        var firstCheckout = await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, firstSession, "WS-A", CancellationToken.None);
        var reclaimed = await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, secondSession, "ws-a", CancellationToken.None);
        await Assert.ThrowsAsync<Application.PdmConflictException>(() =>
            workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, secondSession, "WS-B", CancellationToken.None));
        await Assert.ThrowsAsync<Application.PdmConflictException>(() =>
            workflow.CheckoutAsync(document.Id, "other-admin", UserRole.Administrator, secondSession, "WS-A", CancellationToken.None));

        Assert.Equal(secondSession, reclaimed.CheckoutSessionId);
        Assert.Equal("ws-a", reclaimed.CheckoutMachine);
        Assert.Equal(firstCheckout.CheckedOutAt, reclaimed.CheckedOutAt);
        var staleHeartbeat = await repository.HeartbeatCheckoutSessionAsync(firstSession, "admin", "WS-A", [document.Id], clock.GetUtcNow().AddMinutes(15), CancellationToken.None);
        Assert.DoesNotContain(document.Id, staleHeartbeat);
        var audits = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, CancellationToken.None);
        Assert.Contains(audits, entry =>
            entry.Action == "document.checkout.reclaim-local-session"
            && entry.Detail.Contains(firstSession.ToString(), StringComparison.OrdinalIgnoreCase)
            && entry.Detail.Contains(secondSession.ToString(), StringComparison.OrdinalIgnoreCase));

        var released = await workflow.DiscardCheckoutAsync(
            document.Id,
            "admin",
            UserRole.Administrator,
            secondSession,
            CancellationToken.None);
        Assert.Null(released.CheckedOutBy);
        Assert.Null(released.CheckoutSessionId);
        Assert.Null(released.CheckoutMachine);
    }

    [Fact]
    public async Task ForceRelease_RequiresConfiguredAgeAndAuditsInvalidatedSession()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        var repository = new Infrastructure.InMemoryPdmRepository(clock);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "LOCK-OVERDUE", "Overdue Part", "LOCK-OVERDUE.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        var sessionId = Guid.NewGuid();
        await repository.CheckoutAsync(document.Id, "engineer", sessionId, "WS-A", clock.GetUtcNow().AddMinutes(15), CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), clock);

        await Assert.ThrowsAsync<Application.PdmRuleException>(() =>
            workflow.ForceReleaseEditLockAsync(document.Id, "admin", UserRole.Administrator, "尚未超时", CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(49));
        var released = await workflow.ForceReleaseEditLockAsync(document.Id, "admin", UserRole.Administrator, "编辑人长期未处理", CancellationToken.None);

        Assert.Null(released.CheckedOutBy);
        Assert.Null(released.CheckoutSessionId);
        var audits = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, CancellationToken.None);
        Assert.Contains(audits, entry => entry.Action == "document.checkout.force-release" && entry.Detail.Contains(sessionId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckInBasedOnHistoricalPartVersion_CreatesNextLatestAndPreservesHistory()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var w1Sha256 = new string('1', 64);
        var w2Sha256 = new string('2', 64);
        var document = await RegisterAndCheckInAsync(repository, project, "P-HISTORY-EDIT", w1Sha256);

        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        document = (await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer"), w2Sha256, "W2 change"),
            CancellationToken.None)).Document;

        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var historicalEdit = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer"), w1Sha256, "based on W1"),
            CancellationToken.None);
        var versions = await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None);

        Assert.True(historicalEdit.VersionCreated);
        Assert.Equal("W3", Assert.IsType<DocumentVersion>(historicalEdit.Version).Revision.Display);
        Assert.Equal(["W3", "W2", "W1"], versions.Select(version => version.Revision.Display).ToArray());
        Assert.Equal(w1Sha256, Assert.Single(versions, version => version.Revision.Display == "W1").Sha256);
        Assert.Equal(w2Sha256, Assert.Single(versions, version => version.Revision.Display == "W2").Sha256);
    }

    [Fact]
    public async Task DrawingCheckIn_DoesNotBlockWhenQrCodeDoesNotMatchModelSpecification()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var settings = await repository.GetSystemSettingsAsync(CancellationToken.None);
        await repository.UpdateSystemSettingsAsync(settings with { DrawingQrPolicy = DrawingQrPolicy.Default }, CancellationToken.None);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var model = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "QR-001", "QR Model", "QR-001.SLDPRT", DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        model = await repository.CheckoutAsync(model.Id, "engineer", CancellationToken.None);
        var modelCommit = Commit(project, model, ReferenceRoot(model, "engineer"), new string('1', 64), "model");
        modelCommit = modelCommit with { Properties = new Dictionary<string, string?> { ["全局/型号"] = "MODEL-QR-001" } };
        await repository.CheckInVersionAsync(model.Id, "engineer", modelCommit, CancellationToken.None);

        var drawing = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "QR-001", "QR Drawing", "QR-001.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: model.Id),
            "engineer",
            CancellationToken.None);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var sessionId = Guid.NewGuid();
        drawing = await workflow.CheckoutAsync(drawing.Id, "engineer", UserRole.Administrator, sessionId, "test", CancellationToken.None);
        var snapshot = new CadReferenceSnapshot(
            Guid.NewGuid(),
            project.Id,
            drawing.Id,
            DateTimeOffset.UtcNow,
            "engineer",
            ReferenceRoot(drawing, "engineer"),
            new string('F', 64));
        var file = new Application.StoredFile(".versions/QR-001/W1/QR-001.SLDDRW", 128, new string('2', 64), DateTimeOffset.UtcNow);
        var invalid = new Dictionary<string, string?>
        {
            ["全局/UPLM_QR_CONTENT"] = "WRONG",
            ["全局/UPLM_QR_RULE_VERSION"] = "1",
            ["全局/UPLM_QR_SOURCE_PROPERTY"] = "型号"
        };

        await workflow.PreflightCheckInAsync(
            drawing.Id, "engineer", UserRole.Administrator, sessionId, invalid, snapshot, false,
            CancellationToken.None);
        Assert.Empty(await repository.ListDocumentVersionsAsync(drawing.Id, CancellationToken.None));
        Assert.Equal("engineer", (await repository.FindDocumentAsync(drawing.Id, CancellationToken.None))?.CheckedOutBy);

        var result = await workflow.CheckInAsync(
            drawing.Id, "engineer", UserRole.Administrator, sessionId, file, "drawing", invalid, snapshot, false, false,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("WRONG", Assert.IsType<DocumentVersion>(result.Version).PropertySnapshot["全局/UPLM_QR_CONTENT"]);
    }

    [Fact]
    public async Task ReplayingCommittedCheckIn_ReturnsSameVersionAfterResponseWasLost()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, "RETRY-001", "Retry", "RETRY-001.SLDPRT", DocumentKind.Part),
            "engineer", CancellationToken.None);
        var sessionId = Guid.NewGuid();
        document = await workflow.CheckoutAsync(document.Id, "engineer", UserRole.Administrator, sessionId, "test", CancellationToken.None);
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow,
            "engineer", ReferenceRoot(document, "engineer"), new string('F', 64));
        var file = new Application.StoredFile(".versions/RETRY-001/W1/RETRY-001.SLDPRT", 128, new string('2', 64), DateTimeOffset.UtcNow);

        var first = await workflow.CheckInAsync(document.Id, "engineer", UserRole.Administrator, sessionId,
            file, "first", new Dictionary<string, string?>(), snapshot, false, false, CancellationToken.None);
        var replay = await workflow.CheckInAsync(document.Id, "engineer", UserRole.Administrator, sessionId,
            file, "first", new Dictionary<string, string?>(), snapshot, false, false, CancellationToken.None);

        Assert.Equal(first.Version?.Id, replay.Version?.Id);
        Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, CancellationToken.None));
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.CheckInAsync(
            document.Id, "engineer", UserRole.Administrator, sessionId,
            file with { RelativePath = ".versions/RETRY-001/W2/RETRY-001.SLDPRT" },
            "different upload", new Dictionary<string, string?>(), snapshot, false, false, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveSafety_StaleSourceAndMissingBaseCannotOverwriteLatest()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Application.CreateProjectCommand("QA-BASE", "存档基准验收", "admin", "vault", "release"), "admin", default);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var document = await repository.RegisterDocumentAsync(new(project.Id, "QA-P", "零件", "QA-P.SLDPRT", DocumentKind.Part), "admin", default);
        var session = Guid.NewGuid();
        await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "QA-PC", default);
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "admin", ReferenceRoot(document, "admin"), "snapshot");
        var first = await workflow.CheckInAsync(document.Id, "admin", UserRole.Administrator, session,
            new("first", 1, new string('A', 64), DateTimeOffset.UtcNow), "首次", new Dictionary<string, string?>(), snapshot, false, false, default,
            expectedVersionId: null, requireBaseVersion: true);
        await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "QA-PC", default);
        var second = await workflow.CheckInAsync(document.Id, "admin", UserRole.Administrator, session,
            new("second", 1, new string('B', 64), DateTimeOffset.UtcNow), "修改", new Dictionary<string, string?>(), snapshot with { SnapshotId = Guid.NewGuid() }, false, false, default,
            expectedVersionId: first.Version!.Id, requireBaseVersion: true);
        await workflow.CheckoutAsync(document.Id, "admin", UserRole.Administrator, session, "QA-PC", default);
        foreach (var invalidBase in new Guid?[] { null, first.Version.Id, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.CheckInAsync(document.Id,
                "admin", UserRole.Administrator, session, new("old-source", 1, new string('C', 64), DateTimeOffset.UtcNow),
                "旧原目录修改", new Dictionary<string, string?>(), snapshot, false, false, default,
                expectedVersionId: invalidBase, requireBaseVersion: true));
            Assert.Contains("保留本地修改", error.Message);
            Assert.Equal(second.Version!.Id, (await repository.ListDocumentVersionsAsync(document.Id, default))[0].Id);
        }
        // Also validate at the atomic repository boundary, after workflow preflight could have raced.
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => repository.CheckInVersionAsync(document.Id, "admin", session,
            Commit(project, document, snapshot.Root, new string('C', 64), "事务基准校验") with
            { ExpectedVersionId = first.Version.Id, RequireBaseVersion = true }, default));
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(document.Id, default)).Count);
        Assert.Equal(session, (await repository.FindDocumentAsync(document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task ArchiveSafety_OpeningAssemblyDrawingExpandsPinnedModelDependencies()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Application.CreateProjectCommand("QA-DRAW-ASSEMBLY", "装配工程图依赖验收", "admin", "vault", "release"), "admin", default);
        var part = await repository.RegisterDocumentAsync(new(project.Id, "DRAW-PART", "零件", "DRAW-PART.SLDPRT", DocumentKind.Part), "admin", default);
        part = await repository.CheckoutAsync(part.Id, "admin", default);
        var partFirst = await repository.CheckInVersionAsync(part.Id, "admin", Commit(project, part, ReferenceRoot(part, "admin"), new string('A', 64), "零件首次"), default);
        var assembly = await repository.RegisterDocumentAsync(new(project.Id, "DRAW-ASM", "装配体", "DRAW-ASM.SLDASM", DocumentKind.Assembly), "admin", default);
        assembly = await repository.CheckoutAsync(assembly.Id, "admin", default);
        var modelRoot = ReferenceRoot(assembly, "admin") with { Children = [ReferenceRoot(partFirst.Document, "admin")] };
        var assemblyFirst = await repository.CheckInVersionAsync(assembly.Id, "admin", Commit(project, assembly, modelRoot, new string('B', 64), "装配首次"), default);
        var drawing = await repository.RegisterDocumentAsync(new(project.Id, "DRAW-2D", "工程图", "DRAW-2D.SLDDRW", DocumentKind.Drawing), "admin", default);
        drawing = await repository.CheckoutAsync(drawing.Id, "admin", default);
        var drawingRoot = ReferenceRoot(drawing, "admin") with { Children = [ReferenceRoot(assemblyFirst.Document, "admin")] };
        var drawingFirst = await repository.CheckInVersionAsync(drawing.Id, "admin", Commit(project, drawing, drawingRoot, new string('C', 64), "工程图首次"), default);
        // Current files move forward while the drawing still confirms its original model version.
        part = await repository.CheckoutAsync(part.Id, "admin", default);
        var partNext = await repository.CheckInVersionAsync(part.Id, "admin", Commit(project, part, ReferenceRoot(part, "admin"), new string('D', 64), "零件新版"), default);
        assembly = await repository.CheckoutAsync(assembly.Id, "admin", default);
        await repository.CheckInVersionAsync(assembly.Id, "admin", Commit(project, assembly, modelRoot with { Children = [ReferenceRoot(partNext.Document, "admin")] }, new string('E', 64), "装配新版"), default);
        var workflow = new Application.PdmWorkflowService(repository, new RecordingFileStorage(), new NoOpPublisher(), TimeProvider.System);
        var manifest = await workflow.CreateControlledOpenManifestAsync(drawing.Id, null, false, false, "admin", UserRole.Administrator, default);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal(drawingFirst.Version!.Id, manifest.RootVersionId);
        Assert.Equal(assemblyFirst.Version!.Id, manifest.Files.Single(file => file.DocumentId == assembly.Id).VersionId);
        Assert.Equal(partFirst.Version!.Id, manifest.Files.Single(file => file.DocumentId == part.Id).VersionId);
    }

    [Fact]
    public async Task ArchiveSafety_MultiRootReferencesRemainIndependentAndRemovalRetainsDocument()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Application.CreateProjectCommand("QA-ROOTS", "独立根验收", "admin", "vault", "release"), "admin", default);
        var part = await repository.RegisterDocumentAsync(new(project.Id, "SHARED", "共用件", "SHARED.SLDPRT", DocumentKind.Part), "engineer", default);
        part = await repository.CheckoutAsync(part.Id, "engineer", default);
        var partFirst = await repository.CheckInVersionAsync(part.Id, "engineer", Commit(project, part, ReferenceRoot(part, "engineer"), new string('A', 64), "首次"), default);
        var originalChild = ReferenceRoot(partFirst.Document, "engineer") with { InstancePath = "child", Quantity = 3 };
        var roots = new List<PdmDocument>();
        foreach (var name in new[] { "ROOT-A", "ROOT-B" })
        {
            var root = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + ".SLDASM", DocumentKind.Assembly), "engineer", default);
            root = await repository.CheckoutAsync(root.Id, "engineer", default);
            var stored = await repository.CheckInVersionAsync(root.Id, "engineer", Commit(project, root,
                ReferenceRoot(root, "engineer") with { Children = [originalChild] }, new string('B', 64), "首次总装", true), default);
            roots.Add(stored.Document);
        }
        var initialForest = (await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        Assert.Equal(2, ProjectReferenceForest.Roots(initialForest).Count);
        Assert.Equal(2, initialForest.Root.Children.Select(node => node.Children[0].InstancePath).Distinct().Count());
        await repository.CheckoutAsync(part.Id, "engineer", default);
        var partSecond = await repository.CheckInVersionAsync(part.Id, "engineer", Commit(project, partFirst.Document,
            ReferenceRoot(partFirst.Document, "engineer"), new string('C', 64), "更新共用件"), default);
        Assert.All(ProjectReferenceForest.Roots((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!),
            node => Assert.Equal(partFirst.Document.Revision, node.Children[0].Revision));
        await repository.CheckoutAsync(roots[0].Id, "engineer", default);
        var adopt = Commit(project, roots[0], ReferenceRoot(roots[0], "engineer") with
            { Children = [originalChild with { Revision = partSecond.Document.Revision }] }, new string('B', 64), "确认采用新版", true);
        var adopted = await repository.CheckInVersionAsync(roots[0].Id, "engineer", adopt, default);
        Assert.True(adopted.VersionCreated); // References changed even though native bytes did not.
        var forest = ProjectReferenceForest.Roots((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!);
        Assert.Equal(partSecond.Document.Revision, forest.Single(node => node.DocumentId == roots[0].Id).Children[0].Revision);
        Assert.Equal(partFirst.Document.Revision, forest.Single(node => node.DocumentId == roots[1].Id).Children[0].Revision);
        await repository.CheckoutAsync(roots[0].Id, "engineer", default);
        await repository.CheckInVersionAsync(roots[0].Id, "engineer", adopt with
            { ReferenceSnapshot = adopt.ReferenceSnapshot with { SnapshotId = Guid.NewGuid(), Root = adopt.ReferenceSnapshot.Root with { Children = [] } } }, default);
        Assert.NotNull(await repository.FindDocumentAsync(part.Id, default));
        Assert.Equal(2, (await repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        Assert.Single(ProjectReferenceForest.Roots((await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!)
            .Single(node => node.DocumentId == roots[1].Id).Children);
    }

    [Fact]
    public async Task ArchiveSafety_ForceFlagDoesNotVersionUnchangedFileOrDrawingSupplement()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new Application.CreateProjectCommand("QA-NOCHANGE", "无变化验收", "admin", "vault", "release"), "admin", default);
        var document = await repository.RegisterDocumentAsync(new(project.Id, "ASM", "装配体", "ASM.SLDASM", DocumentKind.Assembly), "engineer", default);
        document = await repository.CheckoutAsync(document.Id, "engineer", default);
        var commit = Commit(project, document, ReferenceRoot(document, "engineer"), new string('A', 64), "首次", true);
        var first = await repository.CheckInVersionAsync(document.Id, "engineer", commit, default);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            await repository.CheckoutAsync(document.Id, "engineer", default);
            var drawing = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "drawing", "DIFFERENT-NAME.SLDDRW", "补充工程图",
                DocumentKind.Drawing, "图纸", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
            var noChange = await repository.CheckInVersionAsync(document.Id, "engineer", commit with
            {
                ForceVersion = true, ExpectedVersionId = first.Version!.Id, RequireBaseVersion = true,
                ReferenceSnapshot = commit.ReferenceSnapshot with { SnapshotId = Guid.NewGuid(), Root = commit.ReferenceSnapshot.Root with { Children = [drawing] } }
            }, default);
            Assert.False(noChange.VersionCreated);
            Assert.Null(noChange.Document.CheckedOutBy);
        }
        Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, default));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("quantity")]
    [InlineData("suppressed")]
    [InlineData("replacement")]
    [InlineData("revision")]
    [InlineData("addition")]
    [InlineData("removal")]
    public void ArchiveSafety_DetectsStructureChanges(string operation)
    {
        var child = new DocumentReferenceNode(Guid.NewGuid(), Guid.NewGuid(), "child", "PART.SLDPRT", "零件", DocumentKind.Part,
            "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var root = child with { DocumentId = Guid.NewGuid(), Kind = DocumentKind.Assembly, FileName = "ASM.SLDASM", Children = [child] };
        var changed = operation switch
        {
            "configuration" => child with { Configuration = "Other" },
            "quantity" => child with { Quantity = 2 },
            "suppressed" => child with { Status = ReferenceNodeStatus.Suppressed },
            "replacement" => child with { DocumentId = Guid.NewGuid() },
            "revision" => child with { Revision = RevisionLabel.InitialWork().NextWork() },
            _ => child
        };
        var current = root with { Children = operation == "removal" ? [] : operation == "addition" ? [child, child with { InstancePath = "another" }] : [changed] };
        Assert.False(ArchiveSafety.SameReferences(root, current));
        Assert.True(ArchiveSafety.SameReferences(root, root with { NodeId = Guid.NewGuid(), Revision = RevisionLabel.InitialWork().NextWork(), CheckedOutBy = "another" }));
    }

    private static async Task<PdmDocument> RegisterAndCheckInAsync(Infrastructure.InMemoryPdmRepository repository, Project project, string drawingNumber, string sha256)
    {
        var document = await repository.RegisterDocumentAsync(
            new Application.RegisterDocumentCommand(project.Id, drawingNumber, drawingNumber, string.Concat(drawingNumber, ".SLDPRT"), DocumentKind.Part),
            "engineer",
            CancellationToken.None);
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);
        var root = ReferenceRoot(document, "engineer");
        var result = await repository.CheckInVersionAsync(document.Id, "engineer", Commit(project, document, root, sha256, "first archive"), CancellationToken.None);
        return result.Document;
    }

    private static DocumentReferenceNode ReferenceRoot(PdmDocument document, string actor) =>
        new(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name, document.Kind, "Default", 1, ReferenceNodeStatus.Normal, document.Revision, actor, []);

    private static DocumentReferenceNode NormalizeReferenceStatus(DocumentReferenceNode node) =>
        node with
        {
            Status = ReferenceNodeStatus.Normal,
            Children = node.Children.Select(NormalizeReferenceStatus).ToArray()
        };

    private static IReadOnlyDictionary<string, string?> PreviewSourceProperties(char sourceHashCharacter) =>
        new Dictionary<string, string?> { ["SourceFileSha256"] = new string(sourceHashCharacter, 64) };

    private static Application.DocumentVersionCommit Commit(
        Project project,
        PdmDocument document,
        DocumentReferenceNode root,
        string sha256,
        string note,
        bool isProjectRoot = false,
        bool forceVersion = false,
        string? sourceFileSha256 = null,
        string? drawingNumber = null,
        string? name = null,
        string? fileName = null) =>
        new(
            new Application.StoredFile(string.Concat(".versions/", document.DrawingNumber, "/", document.FileName), 128, sha256, DateTimeOffset.UtcNow),
            note,
            string.IsNullOrWhiteSpace(sourceFileSha256)
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["SourceFileSha256"] = sourceFileSha256 },
            new CadReferenceSnapshot(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "engineer", root, new string('F', 64)),
            [],
            [],
            IsProjectRoot: isProjectRoot,
            ForceVersion: forceVersion,
            DrawingNumber: drawingNumber,
            Name: name,
            FileName: fileName);

    private sealed class RecordingFileStorage : Application.IFileStorage
    {
        public List<Application.StoredFile> VerifiedFiles { get; } = [];
        public List<Application.StoredFile> MetadataValidatedFiles { get; } = [];
        public Task ValidateStoredFileMetadataAsync(Project project, Application.StoredFile file, CancellationToken cancellationToken)
        {
            MetadataValidatedFiles.Add(file);
            return Task.CompletedTask;
        }
        public Task VerifyStoredFileAsync(Project project, Application.StoredFile file, CancellationToken cancellationToken)
        {
            VerifiedFiles.Add(file);
            return Task.CompletedTask;
        }
        public Task<Application.UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken, string? owner = null) => throw new NotSupportedException();
        public Task<Application.UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Application.UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Application.StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Application.StoredFile> CopyVersionAsync(Project project, Application.StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoOpPublisher : Application.IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Application.ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<Application.ReleasePreviewSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(new Application.ReleasePublication(string.Empty, PreviewArtifacts(sources)));

        private static IReadOnlyDictionary<Guid, DocumentPreviewArtifact> PreviewArtifacts(IReadOnlyList<Application.ReleasePreviewSource> sources) =>
            sources.ToDictionary(
                source => source.DocumentId,
                source => new DocumentPreviewArtifact(
                    source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    $".release-previews/{source.DocumentId:N}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}",
                    1,
                    new string('A', 64),
                    source.SourceSha256));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
