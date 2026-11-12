using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardIndependentRelease_PendingCadDoesNotBlockFormalOrSupplementLifecycle(bool unclassifiedSource)
    {
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var project = (await repository.FindProjectAsync(ProjectId, default))!;
        var session = Guid.NewGuid();
        var assembly = await ArchiveBatchTests.Register(repository, project, "PENDING-STANDARD", DocumentKind.Assembly, session);
        var node = ArchiveBatchTests.Node(assembly) with { StructureIndexPending = true };
        await repository.CheckInVersionAsync(assembly.Id, "admin", session,
            ArchiveBatchTests.Commit(project, assembly, node, 'A', true), default);
        var items = (await repository.GetBomAsync(ProjectId, BomKind.Standard, default)).Select(item => item with {
            SourceDocumentId = assembly.Id, SourceInstancePath = $"roots/{assembly.Id:N}/{node.InstancePath}",
            SourceConfiguration = node.Configuration, Source = "Auto"
        }).ToArray();
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, items, default);
        if (unclassifiedSource)
            await repository.ReplaceBomAsync(ProjectId, BomKind.Unclassified,
                [new BomItem(Guid.NewGuid(), ProjectId, BomKind.Unclassified, 1, "UNKNOWN-SOURCE", "待分类图档来源", 1, "个", null, null, "W1", false)
                    { SourceDocumentId = assembly.Id, IsPendingClassification = true }], default);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        foreach (var scope in new[] { ReleaseScope.StandardFormal, ReleaseScope.StandardSupplement })
        {
            if (scope == ReleaseScope.StandardSupplement)
            {
                items = items.Select(item => item with { Quantity = item.Quantity + 1 }).ToArray();
                await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, items, default);
            }
            var package = await workflow.CreateScopedReleasePackageAsync(ProjectId, null, "", "", "设计问题 / 设计变更", "未指定", null,
                scope, items.Select(item => item.Id).ToArray(), "admin", UserRole.Administrator, default);
            package = await workflow.UpdateReleasePackageDraftAsync(package.Id, package.ChangeReason, package.SelectedBomItemIds, null,
                "admin", UserRole.Administrator, default);
            package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
            foreach (var task in package.ApprovalTasks.Where(task => task.Decision is null).OrderBy(task => task.StepOrder))
                package = await workflow.DecideAsync(task.Id, task.Assignee, UserRole.Approver, ApprovalDecision.Approved, "同意", default);
            Assert.Equal(ReleasePackageState.Published, package.State);
            Assert.False(package.LocksDocuments);
            Assert.Empty(package.VersionBindings!);
            Assert.Equal(items.Select(item => item.Quantity), package.StandardBomSnapshot.Select(item => item.Quantity));
            Assert.True((await repository.GetLatestReferenceSnapshotAsync(ProjectId, default))!.Root.HasPendingStructureIndex);
        }
    }

    [Fact]
    public async Task StandardIndependentRelease_ManualBomCanCompleteLifecycleWithoutCadSnapshot()
    {
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var project = await repository.CreateProjectAsync(new("STANDARD-NO-CAD", "标准件独立发布", "admin", "vault", "release"), "admin", default);
        await repository.SetMainProjectStaffingAsync(project.Id, new SetMainProjectStaffingCommand("admin", [], ["admin"]), "admin", default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, "STANDARD-001", "手工标准件", 2, "个", null, "MODEL", "W1", true);
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [item], default);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var package = await workflow.CreateScopedReleasePackageAsync(project.Id, null, "", "", "", "未指定", null,
            ReleaseScope.StandardFormal, [item.Id], "admin", UserRole.Administrator, default);
        package = await workflow.UpdateReleasePackageDraftAsync(package.Id, "", [item.Id], null, "admin", UserRole.Administrator, default);
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        foreach (var task in package.ApprovalTasks.Where(task => task.Decision is null).OrderBy(task => task.StepOrder))
            package = await workflow.DecideAsync(task.Id, task.Assignee, UserRole.Approver, ApprovalDecision.Approved, "同意", default);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Null(package.ReferenceSnapshotId);
        Assert.Equal(2, Assert.Single(package.StandardBomSnapshot).Quantity);
    }
}
