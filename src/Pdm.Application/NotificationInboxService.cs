using System.Globalization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record NotificationCleanupDecision(UserNotification Notification, bool Resolved, string Reason);

public sealed class NotificationInboxService(IPdmRepository repository, IProjectPlanningRepository plans,
    IProjectApplicationRepository applications, IValidationPlanRepository validations, TimeProvider clock)
{
    private static readonly HashSet<string> ReleaseCategories = ["ReleasePreviewPending", "ReleasePreviewResult", "ReleaseDeliveryResult", "ReleaseApprovalPending", "ReleaseApprovalRejected", "ReleaseManagementRisk"];

    public async Task<int> ReconcileAsync(string? recipient, CancellationToken ct)
    {
        var notices = await repository.ListUnreadUserNotificationsAsync(recipient, ct);
        var decisions = await ReviewAsync(notices, ct);
        var now = clock.GetUtcNow();
        foreach (var decision in decisions)
            if (decision.Resolved) await repository.ResolveUserNotificationAsync(decision.Notification.Id, decision.Notification.Recipient, now, ct);
            else await repository.MarkUserNotificationReadAsync(decision.Notification.Id, decision.Notification.Recipient, now, ct);
        foreach (var id in notices.Where(item => ReleaseCategories.Contains(item.Category) && item.ReleasePackageId is not null).Select(item => item.ReleasePackageId!.Value).Distinct())
            await ReleaseManagementNotifications.ReconcileRiskAsync(repository, id, now, ct);
        return decisions.Count;
    }

    public async Task<IReadOnlyList<NotificationCleanupDecision>> ReviewAsync(IReadOnlyList<UserNotification> notices, CancellationToken ct)
    {
        if (notices.Count == 0) return [];
        var decisions = (await ReleaseManagementNotifications.ReviewLegacyAsync(repository,
            notices.Where(item => ReleaseCategories.Contains(item.Category) && item.ReleasePackageId is not null).ToArray(), ct)).ToDictionary(item => item.Notification.Id);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var drawingCache = new Dictionary<Guid, DrawingReviewPackage?>();
        var planCache = new Dictionary<Guid, ProjectPlan?>();
        var applicationCache = new Dictionary<Guid, ProjectApplication?>();
        var userCache = new Dictionary<string, UserAccount?>(StringComparer.OrdinalIgnoreCase);
        var documentCache = new Dictionary<Guid, PdmDocument?>();
        IReadOnlyList<EditHandoff>? handoffs = null;
        foreach (var notice in notices)
        {
            if (decisions.ContainsKey(notice.Id)) continue;
            if (notice.IsSenderReceipt) { decisions[notice.Id] = new(notice, false, "发送回执属于发起记录"); continue; }
            if (!userCache.TryGetValue(notice.Recipient, out var user)) userCache[notice.Recipient] = user = await repository.FindUserAsync(notice.Recipient, ct);
            if (user?.IsActive == false || NotificationRecipientPolicy.SystemOnly(user) && !NotificationRecipientPolicy.IsSystemMessage(notice))
            { decisions[notice.Id] = new(notice, false, "账号已停用或仅承担系统管理职责"); continue; }

            var key = notice.SourceKey.Split(':');
            if (notice.Category.StartsWith("DrawingReview", StringComparison.Ordinal) && TryId(key, 1, out var drawingId))
            {
                if (!drawingCache.TryGetValue(drawingId, out var drawing)) drawingCache[drawingId] = drawing = await repository.FindDrawingReviewPackageAsync(drawingId, ct);
                if (drawing is null)
                {
                    if (notice.ProjectId is Guid id && await repository.FindProjectAsync(id, ct) is not null) decisions[notice.Id] = new(notice, true, "原审核单已不存在");
                    continue;
                }
                var waitingForReview = notice.Category is "DrawingReviewAssigned" or "DrawingReviewResubmitted";
                if (waitingForReview)
                {
                    if (drawing.State != DrawingReviewPackageState.InReview) decisions[notice.Id] = new(notice, true, "本轮审图已结束或已进入后续节点");
                    else if (!drawing.AllowsReviewer(notice.Recipient)) decisions[notice.Id] = new(notice, false, "当前审核任务已由其他人受理");
                    else if (drawing.AssignedReviewers.Count == 0 && user is not null)
                    {
                        var project = await repository.FindProjectAsync(drawing.ProjectId, ct);
                        var isLead = project is not null && (string.Equals(project.DesignLead, user.Username, StringComparison.OrdinalIgnoreCase) || project.DesignLeads.Contains(user.Username, StringComparer.OrdinalIgnoreCase));
                        if (!await NotificationRecipientPolicy.InBusinessCompanyAsync(repository, user, project?.OrganizationId, ct)
                            || !isLead && !await NotificationRecipientPolicy.HasBusinessPermissionAsync(repository, user.EffectiveRoleCodes, PermissionCodes.DrawingReviewDecide, ct))
                            decisions[notice.Id] = new(notice, false, "不属于当前公共审图受理岗位");
                    }
                }
                else if (notice.Category == "DrawingReviewSupervisorApproval")
                {
                    if (drawing.State != DrawingReviewPackageState.PendingSupervisorApproval) decisions[notice.Id] = new(notice, true, "本轮主管批准已结束");
                    else if (!string.Equals(drawing.Supervisor, notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "不是当前指定主管");
                    else if (drawing.Items.Max(item => item.DrawingReviewedAt) is { } reviewedAt && notice.CreatedAt < reviewedAt)
                        decisions[notice.Id] = new(notice, true, "上一轮批准提醒已由本轮提醒替代");
                }
                else if (notice.Category == "DrawingReviewChangesRequested")
                {
                    var changesRemain = drawing.State == DrawingReviewPackageState.ChangesRequested || drawing.State == DrawingReviewPackageState.InReview
                        && drawing.Items.Any(item => item.DrawingState == DrawingReviewTargetState.ChangesRequested || item.ModelState == DrawingReviewTargetState.ChangesRequested);
                    if (!changesRemain) decisions[notice.Id] = new(notice, true, "退回事项已重新处理或审核单已结束");
                    else if (!drawing.CreatedBy.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "修改事项由本轮提交人办理");
                }
                else if (notice.Category is "DrawingReviewWritebackAbandoned" or "DrawingReviewWritebackTimedOut"
                    && !drawing.CreatedBy.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "审核标记结果仅反馈本轮提交人");
            }
            else if (notice.Category is "project-plan" or "project-plan-approval" && notice.ProjectId is Guid projectId)
            {
                if (!planCache.TryGetValue(projectId, out var plan)) planCache[projectId] = plan = await plans.FindPlanAsync(projectId, ct, includeDeleted: true);
                if (plan is null)
                {
                    if (await repository.FindProjectAsync(projectId, ct) is not null) decisions[notice.Id] = new(notice, true, "本轮项目计划已不存在");
                    continue;
                }
                if (notice.SourceKey.Contains(":overdue:", StringComparison.Ordinal)) { decisions[notice.Id] = new(notice, true, "计划逾期在项目工作台集中展示"); continue; }
                if (key[0] == "project-plan" && TryId(key, 1, out var taskId))
                {
                    var task = (plan.ChangeDraftSource ?? plan).Tasks.FirstOrDefault(item => item.Id == taskId);
                    var outdatedDate = key.Length >= 4 && key[2].StartsWith('d') && int.TryParse(key[2].AsSpan(1), out var days)
                        && DateOnly.TryParseExact(key[3], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdOn)
                        && task is not null && task.PlannedFinish != createdOn.AddDays(days);
                    if (plan.IsDeleted || plan.ApprovalStatus != ProjectPlanApprovalStatus.Approved || task is null || task.Status == ProjectPlanTaskStatus.Completed || outdatedDate)
                        decisions[notice.Id] = new(notice, true, "任务已完成、排期已变更或本轮计划已结束");
                    else if (!string.Equals(task.Assignee, notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "不是当前任务责任人");
                }
                else if (key[0] == "project-plan-approval")
                {
                    if (plan.IsDeleted || plan.ApprovalStatus != ProjectPlanApprovalStatus.Pending || !TryId(key, 1, out var planId) || plan.Id != planId)
                        decisions[notice.Id] = new(notice, true, "本轮首版计划审批已结束");
                    else if (!string.Equals(plan.ApprovalAssignee, notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "不是当前计划审批人");
                }
                else if (key[0] == "project-plan-change-approval")
                {
                    if (plan.IsDeleted || plan.ChangeRequest?.Status != ProjectPlanApprovalStatus.Pending || !TryId(key, 1, out var requestId) || plan.ChangeRequest.Id != requestId)
                        decisions[notice.Id] = new(notice, true, "本轮计划变更审批已结束");
                    else if (!plan.ChangeRequest.ApprovalAssignee.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "不是当前计划变更审批人");
                }
                else if (key[0] is "project-plan-change-decision" or "project-plan-change-complete" or "project-plan-change-stage" && TryId(key, 1, out var changeId))
                {
                    if (plan.IsDeleted || plan.ChangeRequest?.Id != changeId || key[0] == "project-plan-change-decision"
                        && plan.ChangeRequest.Status == ProjectPlanApprovalStatus.Approved && plan.ChangeDraftSource is null)
                        decisions[notice.Id] = new(notice, true, "本轮计划变更事项已结束或被新基线替代");
                    else if (key[0] == "project-plan-change-stage" && key.Length > 2
                        && !(plan.ChangeDraftSource ?? plan).Tasks.Any(task => task.Stage.Equals(key[2], StringComparison.OrdinalIgnoreCase) && string.Equals(task.Assignee, notice.Recipient, StringComparison.OrdinalIgnoreCase)))
                        decisions[notice.Id] = new(notice, false, "不再负责该阶段任务");
                }
            }
            else if (notice.Category == "project-application" && TryId(key, 1, out var applicationId))
            {
                if (!applicationCache.TryGetValue(applicationId, out var application))
                {
                    foreach (var company in directory.Organizations)
                    {
                        application = await applications.FindApplicationAsync(applicationId, company.Id, ct);
                        if (application is not null) break;
                    }
                    applicationCache[applicationId] = application;
                }
                if (application is null || key.Length < 3 || !long.TryParse(key[2], out var version)) continue;
                if (version != application.RowVersion || application.Status is ProjectApplicationService.Draft or ProjectApplicationService.Withdrawn)
                    decisions[notice.Id] = new(notice, true, "本轮立项事项已被处理或被新一轮替代");
                else if (application.Status == ProjectApplicationService.Pending)
                {
                    if (user is not null && !await NotificationRecipientPolicy.HasBusinessPermissionAsync(repository, user.EffectiveRoleCodes, PermissionCodes.ProjectApplicationApprove, ct))
                        decisions[notice.Id] = new(notice, false, "不属于立项审批业务受理岗位");
                }
                else if (!application.Applicant.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase)) decisions[notice.Id] = new(notice, false, "申请结果仅反馈本人");
            }
            else if (notice.Category == "WorkflowValidationPlan" && TryId(key, 2, out var validationId))
            {
                var validation = await validations.FindPlanByIdAsync(validationId, ct);
                if (validation is not null && validation.State is ProjectValidationPlanState.Superseded or ProjectValidationPlanState.Rejected)
                    decisions[notice.Id] = new(notice, true, "本轮验证计划已结束或被新版本替代");
            }
            else if (notice.Category == "EditLockWaiting") decisions[notice.Id] = new(notice, false, "编辑申请回执保留在跟踪记录，不作为新的办理事项");
            else if (notice.Category is "EditLockRequested" or "EditLockEscalated" or "EditLockHandoff")
            {
                var index = key[0] == "edit-request" ? 2 : 1;
                if (!TryId(key, index, out var documentId)) continue;
                if (!documentCache.TryGetValue(documentId, out var document)) documentCache[documentId] = document = await repository.FindDocumentAsync(documentId, ct);
                if (document is null) continue;
                var sessionIndex = index + 1;
                var oldSession = TryId(key, sessionIndex, out var session) && document.CheckoutSessionId != session;
                if (notice.Category == "EditLockHandoff")
                {
                    handoffs ??= await repository.ListEditHandoffsAsync(ct);
                    if (!handoffs.Any(item => item.DocumentId == documentId && item.ToUser.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase) && item.ExpiresAt > clock.GetUtcNow()))
                        decisions[notice.Id] = new(notice, true, "交接预留已接收或已到期");
                }
                else if (document.CheckedOutBy is null || oldSession) decisions[notice.Id] = new(notice, true, "本轮编辑占用已结束或已交接");
                else if (notice.Category == "EditLockRequested" && !document.CheckedOutBy.Equals(notice.Recipient, StringComparison.OrdinalIgnoreCase))
                    decisions[notice.Id] = new(notice, false, "不是当前编辑占用人");
            }
        }
        // 同一审图包的多张图重新提交，合并成一个当前待审提醒。
        foreach (var group in notices.Where(item => !decisions.ContainsKey(item.Id) && item.Category is "DrawingReviewAssigned" or "DrawingReviewResubmitted")
                     .GroupBy(item => $"{item.SourceKey.Split(':').ElementAtOrDefault(1)}:{item.Recipient.ToLowerInvariant()}"))
            foreach (var old in group.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Skip(1))
                decisions[old.Id] = new(old, true, "已合并到本轮最新待审提醒");
        return decisions.Values.ToArray();
    }

    private static bool TryId(string[] key, int index, out Guid id) { id = default; return index < key.Length && Guid.TryParse(key[index], out id); }
}
