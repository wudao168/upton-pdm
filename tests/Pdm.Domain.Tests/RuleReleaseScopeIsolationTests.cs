using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class RuleReleaseScopeIsolationTests
{
    [Theory]
    [InlineData(ReleaseScope.NonStandardLongLead)]
    [InlineData(ReleaseScope.NonStandardWithDrawing)]
    public async Task PendingIndexDoesNotAddFormalPromotionRulesToLongLeadBindings(ReleaseScope scope)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-INDEX-LONGLEAD-" + scope, "长交期范围", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var model = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var modelNode = ArchiveBatchTests.Node(model) with { StructureIndexPending = true };
        await repository.CheckInVersionAsync(model.Id, "admin", session,
            ArchiveBatchTests.Commit(project, model, modelNode, 'A', true), default);
        var drawing = await ArchiveBatchTests.Register(repository, project, "DRAWING", DocumentKind.Drawing, session);
        await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "longlead-drawing", "admin", session,
            [new(drawing.Id, ArchiveBatchTests.Commit(project, drawing, ArchiveBatchTests.Node(drawing) with { Children = [modelNode] }, 'B'))], default);
        Assert.Single(await repository.ListDocumentRelationsAsync(project.Id, default));
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "PART", "零件", 1, "个", "钢", "测试", "W1", true)
        { SourceDocumentId = model.Id, SourceConfiguration = modelNode.Configuration, SourceInstancePath = modelNode.InstancePath };
        await repository.ReplaceBomAsync(project.Id, BomKind.NonStandard, [item], default);
        var snapshot = (await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "QA-LONGLEAD", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null)
        { Scope = scope, RootDocumentId = model.Id, SelectedBomItemIds = [item.Id], NonStandardBomSnapshot = [item] };
        var workflow = new PdmWorkflowService(repository, new UnusedStorage(), new NoOpPublisher(), TimeProvider.System);
        var method = typeof(PdmWorkflowService).GetMethod("CaptureReleaseVersionsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<ReleasePackage> Capture() => (Task<ReleasePackage>)method.Invoke(workflow, [package, CancellationToken.None])!;
        if (scope == ReleaseScope.NonStandardWithDrawing)
            Assert.Contains("待补全", (await Assert.ThrowsAsync<PdmRuleException>(Capture)).Message);
        else
        {
            var captured = await Capture();
            Assert.Equal(2, captured.VersionBindings!.Count);
            Assert.All(captured.VersionBindings, binding => { Assert.False(binding.Publish); Assert.False(binding.RequiresPromotion); });
            Assert.False(captured.LocksDocuments);
            Assert.Equal(0, captured.DrawingDocumentCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConfirmedRemovalKeepsItsFrozenSourceRootProtection(bool removedRootPending, bool unknownRemovedId)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var project = (await repository.FindProjectAsync(projectId, default))!;
        var session = Guid.NewGuid();
        var complete = await ArchiveCompleteRoot(repository, project, session);
        var pending = await ArchiveBatchTests.Register(repository, project, "PENDING-REMOVAL-ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(pending.Id, "admin", session,
            ArchiveBatchTests.Commit(project, pending, ArchiveBatchTests.Node(pending) with { StructureIndexPending = true }, 'A', true), default);
        var removedRootId = removedRootPending ? pending.Id : complete.RootDocumentId;
        var removed = new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1, "REMOVED", "已删除组件", 1, "个", null, null, "W1", true)
        {
            SourceDocumentId = Guid.NewGuid(), SourceConfiguration = "Default",
            SourceInstancePath = $"roots/{removedRootId:N}/deleted-occurrence", Source = "Auto"
        };
        var baseline = await repository.SaveBomDraftAsync(projectId, BomKind.Standard, [removed], "admin", default);
        await repository.SetBomVersionStateAsync([baseline.Id], BomVersionState.Released, "admin", null, default);
        var next = await repository.SaveBomDraftAsync(projectId, BomKind.Standard, [], "admin", default);
        Assert.Equal(baseline.Id, next.BaseVersionId);
        // A later current-BOM row cannot replace the frozen deletion source.
        await repository.ReplaceBomAsync(projectId, BomKind.Standard,
            [removed with { SourceDocumentId = null, SourceInstancePath = null, Source = "Manual" }], default);
        var snapshot = (await repository.GetLatestReferenceSnapshotAsync(projectId, default))!;
        var package = new ReleasePackage(Guid.NewGuid(), projectId, "QA-REMOVE", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null)
        {
            Scope = ReleaseScope.StandardSupplement, StandardBomVersionId = next.Id,
            SelectedBomItemIds = [], StandardBomSnapshot = [],
            ConfirmedRemovedBomItemIds = [unknownRemovedId ? Guid.NewGuid() : removed.Id]
        };
        var workflow = new PdmWorkflowService(repository, new UnusedStorage(), new NoOpPublisher(), TimeProvider.System);
        var method = typeof(PdmWorkflowService).GetMethod("RequireCompletePackageReleaseIndexAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task Validate() => (Task)method.Invoke(workflow, [package, snapshot, CancellationToken.None])!;
        if (removedRootPending || unknownRemovedId)
        {
            var error = await Assert.ThrowsAsync<PdmRuleException>(Validate);
            Assert.Contains(unknownRemovedId ? "来源" : "待补全", error.Message);
        }
        else await Validate();
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("complete", false)]
    [InlineData("pending", true)]
    [InlineData("pending", false)]
    [InlineData("unknown", false)]
    [InlineData("ambiguous", false)]
    [InlineData("wrong-root", false)]
    [InlineData("manual", false)]
    [InlineData("wrong-configuration", false)]
    public async Task OnlyTheSelectedCadRootMayBlockScopedRelease(string source, bool explicitRoot)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var project = (await repository.FindProjectAsync(projectId, default))!;
        var session = Guid.NewGuid();
        var complete = await ArchiveCompleteRoot(repository, project, session);
        Assert.False(complete.Root.HasPendingStructureIndex);
        var unrelated = await ArchiveBatchTests.Register(repository, project, "UNRELATED-PENDING", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(unrelated.Id, "admin", session,
            ArchiveBatchTests.Commit(project, unrelated, ArchiveBatchTests.Node(unrelated) with {
                StructureIndexPending = true, Children = source == "ambiguous" ? [complete.Root] : [] }, 'A', true), default);
        var forest = (await repository.GetLatestReferenceSnapshotAsync(projectId, default))!;
        Assert.True(forest.Root.HasPendingStructureIndex);
        Assert.Contains(ProjectReferenceForest.Roots(forest), root => root.DocumentId == complete.RootDocumentId);
        Assert.False((await repository.GetProjectReferenceRootAsync(projectId, complete.RootDocumentId, default))!.Root.HasPendingStructureIndex);

        var selectedRoot = source == "pending"
            ? (await repository.GetProjectReferenceRootAsync(projectId, unrelated.Id, default))! : complete;
        var selected = (await repository.GetBomAsync(projectId, BomKind.Standard, default))
            .Select(item => item with {
                SourceDocumentId = source == "manual" ? null : source == "unknown" ? Guid.NewGuid() : selectedRoot.RootDocumentId,
                SourceConfiguration = source == "manual" ? null : source == "wrong-configuration" ? "UNKNOWN-CONFIGURATION" : selectedRoot.Root.Configuration,
                SourceInstancePath = source is "manual" or "unknown" or "ambiguous" ? null
                    : $"roots/{(source == "wrong-root" ? unrelated.Id : selectedRoot.RootDocumentId):N}/{selectedRoot.Root.InstancePath}",
                Source = source == "manual" ? "Manual" : "Auto"
            }).ToArray();
        await repository.ReplaceBomAsync(projectId, BomKind.Standard, selected, default);

        await ConfigureApprovers(repository, projectId);
        var workflow = new PdmWorkflowService(repository, new UnusedStorage(), new NoOpPublisher(), TimeProvider.System);
        var packageIdsBefore = (await repository.ListReleasePackagesAsync(projectId, default)).Select(package => package.Id).Order().ToArray();
        Task<ReleasePackage> Create() => workflow.CreateScopedReleasePackageAsync(projectId, null, "", "", "", "未指定", null,
            ReleaseScope.StandardFormal, selected.Select(item => item.Id).ToArray(), "admin", UserRole.Administrator, default,
            rootDocumentId: explicitRoot ? selectedRoot.RootDocumentId : null);
        if (source is "pending" or "unknown" or "ambiguous" or "wrong-root" or "wrong-configuration")
        {
            var error = await Assert.ThrowsAsync<PdmRuleException>(Create);
            Assert.Contains(source == "pending" ? "待补全" : "来源", error.Message);
            Assert.Equal(packageIdsBefore, (await repository.ListReleasePackagesAsync(projectId, default)).Select(package => package.Id).Order());
            return;
        }
        var package = await Create();
        Assert.Equal(selected.Select(item => item.Id).Order(), package.SelectedBomItemIds.Order());
    }

    private static async Task<CadReferenceSnapshot> ArchiveCompleteRoot(InMemoryPdmRepository repository, Project project, Guid session)
    {
        var root = await ArchiveBatchTests.Register(repository, project, "COMPLETE-ROOT", DocumentKind.Assembly, session);
        await repository.CheckInVersionAsync(root.Id, "admin", session,
            ArchiveBatchTests.Commit(project, root, ArchiveBatchTests.Node(root), 'C', true), default);
        return (await repository.GetProjectReferenceRootAsync(project.Id, root.Id, default))!;
    }

    private static async Task ConfigureApprovers(InMemoryPdmRepository repository, Guid projectId)
    {
        foreach (var user in new[] { "admin", "mechanical-supervisor", "standardization-supervisor" })
            if (await repository.FindUserAsync(user, default) is null)
                await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), user, user, "unused",
                    user == "admin" ? UserRole.Administrator : UserRole.Approver, true), default);
        var organization = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var division = await repository.SaveOrganizationUnitAsync(new SaveOrganizationUnitCommand(null, organization, null,
            "RULE-SCOPE-DIV", "规则测试事业部", OrganizationUnitKind.BusinessDivision, true, 0), default);
        var department = await repository.SaveOrganizationUnitAsync(new SaveOrganizationUnitCommand(null, organization, division.Id,
            "RULE-SCOPE-DEPT", "规则测试部门", OrganizationUnitKind.Department, true, 0), default);
        await repository.SetOrganizationMembershipsAsync("admin", [department.Id], department.Id, default);
        await repository.SetOrganizationUnitManagersAsync(department.Id, "mechanical-supervisor", [], default);
        await repository.SetOrganizationUnitManagersAsync(division.Id, "standardization-supervisor", [], default);
        await repository.SetMainProjectStaffingAsync(projectId, new SetMainProjectStaffingCommand("admin", [], ["admin"]), "admin", default);
    }

    private sealed class NoOpPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken token) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken token) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedStorage : IFileStorage
    {
        public Task<UploadSession> StartUploadAsync(Guid id, string name, long length, string hash, CancellationToken token, string? owner = null) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid id, int index, Stream content, CancellationToken token) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid id, string path, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken token) => Task.FromResult(true);
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string path, CancellationToken token) => throw new NotSupportedException();
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken token) => throw new NotSupportedException();
    }
}
