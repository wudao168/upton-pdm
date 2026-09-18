using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomU9AutomationRetryQueueTests
{
    [Fact]
    public void SchedulesWaitingItemsWithBackoffUntilCleared()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        BomU9AutomationRetryQueue.Schedule(projectId, ProjectBomHeaderKind.Standard, "BOM表头料号尚未完成自动审批", now);
        try
        {
            Assert.DoesNotContain(TakeDue(now), retry => retry.ProjectId == projectId);

            var first = TakeDue(now.AddMinutes(1)).Single(retry => retry.ProjectId == projectId);
            Assert.Equal(1, first.Attempt);
            Assert.Contains("表头料号", first.Message);

            Assert.DoesNotContain(TakeDue(now.AddMinutes(2)), retry => retry.ProjectId == projectId);

            var second = TakeDue(now.AddMinutes(7)).Single(retry => retry.ProjectId == projectId);
            Assert.Equal(2, second.Attempt);

            BomU9AutomationRetryQueue.Clear(projectId, ProjectBomHeaderKind.Standard);
            Assert.DoesNotContain(TakeDue(now.AddHours(4)), retry => retry.ProjectId == projectId);
        }
        finally
        {
            BomU9AutomationRetryQueue.Clear(projectId, ProjectBomHeaderKind.Standard);
        }
    }

    [Fact]
    public void ImmediateScheduleIsDueRightAway()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        BomU9AutomationRetryQueue.Schedule(projectId, ProjectBomHeaderKind.Master, "启动自检：重新评估U9C BOM自动创建", now, immediate: true);
        try
        {
            Assert.Single(TakeDue(now), retry => retry.ProjectId == projectId);
        }
        finally
        {
            BomU9AutomationRetryQueue.Clear(projectId, ProjectBomHeaderKind.Master);
        }
    }

    private static IReadOnlyList<BomU9AutomationRetry> TakeDue(DateTimeOffset now) =>
        BomU9AutomationRetryQueue.TakeDue(now, 50);
}
