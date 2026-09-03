using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ReleaseItemCommentTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task CurrentApprover_CanAppendMaterialComments_ThatRemainReadableAfterApproval()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 3, 6, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var service = new ReleaseItemCommentService(repository, clock);
        var packageId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var firstItem = Item(Guid.NewGuid(), 1, "根装配/支架-1");
        var secondItem = Item(Guid.NewGuid(), 2, "根装配/支架-2");
        var package = new ReleasePackage(
            packageId, ProjectId, "RP-COMMENT-001", ReleasePackageState.Approval, Guid.NewGuid(), "W1", "W1",
            [new ApprovalTask(taskId, packageId, ApprovalStage.Approval, "approver", null, null, null, null) { StepOrder = 1 }],
            clock.GetUtcNow(), null, null)
        {
            Scope = ReleaseScope.StandardFormal,
            StandardBomSnapshot = [firstItem, secondItem]
        };
        await repository.CreateReleasePackageAsync(package, default);

        var first = await service.AddAsync(packageId, firstItem.Id, "检查安装方向", "approver", UserRole.Engineer, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await service.AddAsync(packageId, secondItem.Id, "同型号第二实例已复核", "approver", UserRole.Engineer, default);

        Assert.Equal(first.MaterialKey, second.MaterialKey);
        var activeComments = await service.ListAsync(packageId, "approver", UserRole.Engineer, default);
        Assert.Equal(new[] { "检查安装方向", "同型号第二实例已复核" }, activeComments.Select(item => item.Comment));

        await repository.DecideApprovalAsync(taskId, "approver", ApprovalDecision.Approved, "同意", false, null, default);
        var historicalComments = await service.ListAsync(packageId, "approver", UserRole.Engineer, default);
        Assert.Equal(2, historicalComments.Count);
    }

    [Fact]
    public async Task NonCurrentApprover_CannotAddMaterialComment()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 3, 6, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var service = new ReleaseItemCommentService(repository, clock);
        var packageId = Guid.NewGuid();
        var item = Item(Guid.NewGuid(), 1, "根装配/传感器-1");
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            packageId, ProjectId, "RP-COMMENT-002", ReleasePackageState.ProcessReview, Guid.NewGuid(), "W1", "W1",
            [new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.ProcessReview, "assigned-user", null, null, null, null) { StepOrder = 1 }],
            clock.GetUtcNow(), null, null)
        {
            Scope = ReleaseScope.StandardFormal,
            StandardBomSnapshot = [item]
        }, default);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AddAsync(packageId, item.Id, "不应写入", "other-user", UserRole.Engineer, default));

        Assert.Contains("当前审批人", exception.Message);
        Assert.Empty(await repository.ListReleaseItemCommentsAsync(packageId, default));
    }

    private static BomItem Item(Guid id, int sequence, string path) => new(
        id, ProjectId, BomKind.Standard, sequence, "STD-001", "支架", 1, "个", null, "M8", "W1", true)
    {
        Brand = "UPTON",
        SourceInstancePath = path,
        ParentDrawingNumber = "ROOT-001"
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
