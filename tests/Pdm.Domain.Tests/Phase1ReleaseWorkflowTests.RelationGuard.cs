using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnhandledRelationsBlockSubmitApprovalAndBackgroundPublication(bool pendingAtPublication)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var guard = new SwitchableMaterialRelationGuard();
        var publisher = new RecordingPublisher();
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), publisher, TimeProvider.System,
            materialRelationReleaseGuard: guard);
        var package = await workflow.CreateScopedReleasePackageAsync(ProjectId, null, "", "", "", "未指定", null,
            ReleaseScope.StandardFormal, CurrentIds(repository, BomKind.Standard), "admin", UserRole.Administrator, default);
        guard.Pending = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default));
        Assert.Equal(ReleasePackageState.Draft, (await repository.FindReleasePackageAsync(package.Id, default))!.State);
        guard.Pending = false;
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        var firstTask = package.ApprovalTasks.OrderBy(task => task.StepOrder).First(task => task.Decision is null);
        guard.Pending = true;
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideAsync(firstTask.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, null, default));
        Assert.Null((await repository.FindReleasePackageAsync(package.Id, default))!.ApprovalTasks.Single(task => task.Id == firstTask.Id).Decision);
        guard.Pending = false;
        for (var step = 0; step < 10 && package.State != ReleasePackageState.Publishing; step++)
        {
            var task = package.ApprovalTasks.OrderBy(item => item.StepOrder).First(item => item.Decision is null);
            package = await workflow.DecideAsync(task.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, null, default, publishImmediately: false);
        }
        Assert.Equal(ReleasePackageState.Publishing, package.State);
        guard.Pending = pendingAtPublication;
        if (pendingAtPublication)
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ResumePublishAsync(package.Id, default));
            Assert.Equal(ReleasePackageState.PublishFailed, (await repository.FindReleasePackageAsync(package.Id, default))!.State);
            Assert.Equal(0, publisher.PublishCalls);
        }
        else
        {
            var published = await workflow.ResumePublishAsync(package.Id, default);
            Assert.Equal(ReleasePackageState.Published, published.State);
            Assert.Equal(1, publisher.PublishCalls);
        }
    }

    private sealed class SwitchableMaterialRelationGuard : IMaterialRelationReleaseGuard
    {
        public bool Pending { get; set; }
        public Task EnsureCompleteAsync(Guid projectId, CancellationToken ct) => Pending
            ? throw new PdmRuleException("关联物料未处理，不能发布：MOTOR-1 电机 / 控制器。") : Task.CompletedTask;
    }
}
