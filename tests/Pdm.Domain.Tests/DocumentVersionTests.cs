using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class DocumentVersionTests
{
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
                new("duplicate", "DUP-ALIAS.SLDPRT", DocumentKind.Part, sourceSha256),
                new("new", "DUP-NEW.SLDPRT", DocumentKind.Part, new string('C', 64))
            ],
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        Assert.Equal(DocumentRegistrationMatchKind.SameNameSameContent, Assert.Single(matches, item => item.CandidateKey == "same").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.SameNameDifferentContent, Assert.Single(matches, item => item.CandidateKey == "conflict").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.SameContentDifferentName, Assert.Single(matches, item => item.CandidateKey == "duplicate").MatchKind);
        Assert.Equal(DocumentRegistrationMatchKind.New, Assert.Single(matches, item => item.CandidateKey == "new").MatchKind);
    }

    [Fact]
    public async Task RegisterDocument_UsesContentFingerprintToPreventUnsafeReuse()
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
        await Assert.ThrowsAsync<Application.PdmConflictException>(() => workflow.RegisterDocumentAsync(
            command with { DrawingNumber = "SAFE-ALIAS", FileName = "SAFE-ALIAS.SLDPRT" },
            "admin",
            UserRole.Administrator,
            CancellationToken.None));

        var independent = await workflow.RegisterDocumentAsync(
            command with
            {
                DrawingNumber = "SAFE-ALIAS",
                FileName = "SAFE-ALIAS.SLDPRT",
                AllowDuplicateContent = true,
                DuplicateReason = "客户要求使用独立图号"
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
            new Dictionary<string, string?>(),
            snapshot,
            false,
            false,
            CancellationToken.None);

        Assert.True(result.VersionCreated);
        Assert.Equal("W1", Assert.IsType<DocumentVersion>(result.Version).Revision.Display);
        Assert.Equal(string.Empty, result.Version.ChangeNote);
        Assert.Single(fileStorage.VerifiedFiles);
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
            new Dictionary<string, string?>(),
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
    public async Task CheckInWithSameFileHash_ForceVersionCreatesNextWorkVersion()
    {
        var repository = new Infrastructure.InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await RegisterAndCheckInAsync(repository, project, "P-006", new string('6', 64));
        document = await repository.CheckoutAsync(document.Id, "engineer", CancellationToken.None);

        var result = await repository.CheckInVersionAsync(
            document.Id,
            "engineer",
            Commit(project, document, ReferenceRoot(document, "engineer"), new string('6', 64), "reference changed", forceVersion: true),
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
    public async Task WorkflowCheckIn_WithBatchPropertyIdentity_WritesIdentityAudit()
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
            new Dictionary<string, string?>(),
            snapshot,
            false,
            true,
            CancellationToken.None,
            "P-AUDIT-NEW",
            "Updated audit name");

        Assert.Equal("P-AUDIT-NEW", result.Document.DrawingNumber);
        var audits = await repository.ListAuditAsync("engineer", UserRole.Administrator, 100, CancellationToken.None);
        Assert.Contains(audits, entry => entry.Action == "document.identity.update" && entry.Detail.Contains("P-AUDIT-NEW", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildDocumentCheckIn_AutomaticallyReconcilesProjectBomAndReportsMismatches()
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
        Assert.NotNull(result.BomUpdate);
        Assert.True(result.BomUpdate.Applied);
        Assert.True(result.BomUpdate.UnclassifiedCount + result.BomUpdate.PendingRemovalCount + result.BomUpdate.ManualUnmatchedCount > 0);
        var refreshed = Assert.Single(
            await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None),
            item => item.SourceDocumentId == childId);
        Assert.Equal("AUTO-BOM-001", refreshed.DrawingNumber);
        Assert.Equal("自动更新标准组件", refreshed.Name);
        Assert.NotNull(refreshed.ReconciliationStatus);
        Assert.Contains("图档源数据", refreshed.ReconciliationNote);
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
            new Dictionary<string, string?>(),
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
    public async Task WorkflowCheckIn_RejectsSubassemblyMarkedAsProjectRoot()
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
        var exception = await Assert.ThrowsAsync<Application.PdmRuleException>(() => workflow.CheckInAsync(
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
            CancellationToken.None));

        Assert.Contains("不能替换项目完整结构", exception.Message, StringComparison.Ordinal);
        Assert.Empty(storage.VerifiedFiles);
        var currentSnapshot = await repository.GetLatestReferenceSnapshotAsync(project.Id, CancellationToken.None);
        Assert.Equal(rootAssembly.Id, currentSnapshot?.RootDocumentId);
    }

    [Fact]
    public async Task ControlledOpenManifest_UsesExactReferencedVersionsAndVerifiesEveryFile()
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
        Assert.Equal(2, storage.VerifiedFiles.Count);
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
        Assert.Equal(2, storage.VerifiedFiles.Count);

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
        Assert.Equal(2, storage.VerifiedFiles.Count);
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
        Assert.Single(storage.VerifiedFiles);
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
        public Task VerifyStoredFileAsync(Project project, Application.StoredFile file, CancellationToken cancellationToken)
        {
            VerifiedFiles.Add(file);
            return Task.CompletedTask;
        }
        public Task<Application.UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task<string> PublishAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
