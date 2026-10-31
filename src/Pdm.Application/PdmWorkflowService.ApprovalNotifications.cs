using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    private async Task NotifyPendingReleaseApprovalAsync(ReleasePackage package, bool resubmitted, CancellationToken ct)
    {
        if (package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval)) return;
        var task = package.ApprovalTasks.OrderBy(item => item.StepOrder).FirstOrDefault(item => item.Decision is null);
        if (task is null || string.IsNullOrWhiteSpace(task.Assignee)) return;
        var project = await repository.FindProjectAsync(package.ProjectId, ct)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var title = resubmitted ? "BOM发布已重新提交审批" : "BOM发布待审批";
        // 退回重提会复用任务ID；成功流转使用独立事件键，保留旧消息并生成新一轮未读提醒。
        await repository.CreateUserNotificationsAsync([new UserNotification(
            Guid.NewGuid(), task.Assignee, "ReleaseApprovalPending", title,
            $"{project.Code} · {package.Number} 待您审批，请查看发布包并处理。",
            project.Id, package.Id, $"release-package:{package.Id:N}:approval:{task.Id:N}:{Guid.NewGuid():N}",
            timeProvider.GetUtcNow(), null)], ct);
    }
}
