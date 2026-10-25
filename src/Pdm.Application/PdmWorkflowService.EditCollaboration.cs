using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    public async Task ValidateEditDispositionAsync(Guid documentId, string actor, bool keepEditing, string? handoffTo, Guid? writebackId, CancellationToken ct)
    {
        if (keepEditing && !string.IsNullOrWhiteSpace(handoffTo)) throw new PdmRuleException("继续编辑和交接给他人不能同时选择。");
        if (writebackId.HasValue && (keepEditing || !string.IsNullOrWhiteSpace(handoffTo))) throw new PdmRuleException("审核或属性回写完成后必须结束专用编辑会话。");
        if (string.IsNullOrWhiteSpace(handoffTo)) return;
        if (actor.Equals(handoffTo.Trim(), StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("不能交接给自己；继续工作请选择“存档并继续编辑”。");
        var recipient = await repository.FindUserAsync(handoffTo.Trim(), ct);
        if (recipient is null || !recipient.IsActive) throw new PdmRuleException("接手账号不存在或已停用。");
        var document = await RequireDocumentAsync(documentId, ct);
        var project = await repository.FindProjectAsync(document.ProjectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var scope = await repository.GetUserCompanyScopeAsync(recipient.Username, ct) ?? throw new PdmRuleException("接手人尚未配置公司范围。");
        if (scope.PrimaryCompanyId is null) throw new PdmRuleException("接手人尚未配置所属公司。");
        var companyId = project.OrganizationId ?? scope.PrimaryCompanyId ?? Guid.Empty;
        var platform = recipient.HasRole("platform_admin") || recipient.HasRole("developer") || recipient.Username.Equals("admin", StringComparison.OrdinalIgnoreCase);
        if (companyId != scope.PrimaryCompanyId && !platform && (!scope.CrossCompanyView || !scope.AccessibleCompanyIds.Contains(companyId)))
            throw new PdmRuleException("接手人无权访问该图档所属公司。");
        var originalTenant = TenantContext.Current;
        try
        {
            // 必须按接手人的身份判断，不能沿用发起管理员的跨公司和部门权限。
            TenantContext.Set(new(recipient.Id, companyId, scope.PrimaryCompanyId ?? companyId, recipient.Username, recipient.EffectiveRoleCode, platform || scope.CrossCompanyView, new HashSet<string>(), recipient.EffectiveRoleCodes));
            var permissions = await repository.GetUserPermissionsAsync(recipient.Username, recipient.Role, ct);
            TenantContext.Set(TenantContext.Current! with { Permissions = permissions });
            if (!permissions.Contains(PermissionCodes.DocumentEdit)
                || !await repository.HasDocumentAccessAsync(documentId, recipient.Username, recipient.Role, FolderAccess.View | FolderAccess.Edit, ct))
                throw new PdmRuleException("接手人没有该图档的编辑权限，请先由项目负责人授权。");
            await RequireProjectSubmissionAccessAsync(document.ProjectId, recipient.Username, recipient.Role, "接收图档交接", ct);
        }
        finally { if (originalTenant is null) TenantContext.Clear(); else TenantContext.Set(originalTenant); }
    }

    public async Task<PdmDocument> EmergencyReleaseEditLockAsync(Guid documentId, Guid sessionId, DateTimeOffset checkedOutAt, string actor, UserRole role, string reason, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentLockEmergencyRelease, ct);
        var current = await RequireDocumentAsync(documentId, ct);
        var project = await repository.FindProjectAsync(current.ProjectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!IsProjectLockManager(project, actor, role)) throw new UnauthorizedAccessException("仅授权项目负责人或系统管理员可以紧急接管。");
        reason = RequiredReason(reason);
        var updated = await repository.ReclaimCheckoutAsync(documentId, sessionId, checkedOutAt, null, timeProvider.GetUtcNow(), ct);
        await AuditAsync(actor, "document.checkout.emergency-release", nameof(PdmDocument), documentId.ToString(),
            $"原编辑人：{current.CheckedOutBy}；原会话：{sessionId}；原电脑：{current.CheckoutMachine}；原因：{reason}；未上传修改由原电脑恢复并比对", ct);
        await NotifyEditReleasedAsync(current, ct);
        await repository.CreateUserNotificationsAsync([new(Guid.NewGuid(), current.CheckedOutBy!, "EditLockReleased", "图档已被紧急接管",
            $"{current.FileName}已由{actor}紧急解除占用。原会话不能提交，请保留本地修改并与服务器最新版比对。原因：{reason}", current.ProjectId, null, $"edit-emergency:{documentId:N}:{sessionId:N}:{checkedOutAt.UtcTicks}", timeProvider.GetUtcNow(), null)], ct);
        return updated;
    }

    public async Task<PdmDocument> AutoReleaseUnchangedEditAsync(Guid documentId, Guid sessionId, DateTimeOffset checkedOutAt, Guid baseVersionId, string sha256, string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, ct);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, ct);
        var settings = await repository.GetSystemSettingsAsync(ct);
        if (!settings.CheckoutAutoReleaseEnabled) throw new PdmRuleException("自动释放已关闭，当前编辑权限保留。");
        var current = await RequireDocumentAsync(documentId, ct);
        if (current.CheckoutSessionId != sessionId || !string.Equals(current.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("编辑会话已变化，已取消自动释放，请重新核对图档。");
        if (current.State is DocumentLifecycleState.InReview or DocumentLifecycleState.Obsolete) throw new PdmConflictException("审核、审批或作废图档不能自动释放编辑权限。");
        await EnsureDrawingReviewEditAllowedAsync(documentId, null, "自动结束编辑", ct);
        var latest = (await repository.ListDocumentVersionsAsync(documentId, ct)).FirstOrDefault();
        if (latest is null || latest.Id != baseVersionId || !string.Equals(latest.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("服务器基准版本已经变化，已保留编辑权限，请先核对图档。");
        var updated = await repository.ReclaimCheckoutAsync(documentId, sessionId, checkedOutAt, null, timeProvider.GetUtcNow(), ct);
        await AuditAsync(actor, "document.checkout.auto-idle-release", nameof(PdmDocument), documentId.ToString(), "插件确认无未提交修改，闲置倒计时结束；服务端再次核对会话、审核和存档上传状态", ct);
        await NotifyEditReleasedAsync(current, ct);
        return updated;
    }

    // 后台任务只回收确认长期离线的旧一代会话，仓储事务内再次核对心跳、审核和上传状态。
    public async Task<int> MaintainEditCollaborationAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await repository.GetSystemSettingsAsync(ct);
        var count = 0;
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(ct))
        {
            if (document.CheckoutSessionId is not Guid sessionId || document.CheckedOutAt is not DateTimeOffset started || string.IsNullOrWhiteSpace(document.CheckedOutBy)) continue;
            var project = await repository.FindProjectAsync(document.ProjectId, ct);
            if (project is null) continue;
            var managers = new[] { project.PrimaryProjectManager, project.DesignLead }.Concat(project.CollaborativeProjectManagers).Concat(project.DesignLeads)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var key = $"{document.Id:N}:{sessionId:N}:{started.UtcTicks}";
            if (document.CheckoutReleaseRequestedAt is DateTimeOffset requested)
            {
                if (now - requested >= TimeSpan.FromMinutes(5))
                    await EditNoticeAsync([document.CheckedOutBy], "EditLockReminder", "请处理图档交接申请", $"{document.FileName}已有人等待编辑超过5分钟，请存档交接或说明继续编辑原因。", document.ProjectId, "edit-wait-reminder:" + EditHandoff.WaitingKey(document), ct);
                if (now - requested >= TimeSpan.FromMinutes(10))
                    await EditNoticeAsync(managers, "EditLockEscalated", "图档交接等待负责人处理", $"{document.FileName}的交接申请超过10分钟，请协调{document.CheckedOutBy}或按授权接管。", document.ProjectId, "edit-wait-escalated:" + EditHandoff.WaitingKey(document), ct);
            }
            if (now - started >= TimeSpan.FromHours(24))
                await EditNoticeAsync(managers.Append(document.CheckedOutBy), "EditLockOverdue", "图档长期占用待处理", $"{document.FileName}已占用超过24小时，请核对未提交修改；在线图档不会仅因占用时长被自动释放。", document.ProjectId, "edit-long-held:" + key, ct);
            var heartbeat = document.CheckoutLastHeartbeatAt ?? started;
            if (now - started >= TimeSpan.FromHours(48) && document.CheckoutLeaseExpiresAt > now)
                await EditNoticeAsync(managers, "EditLockEscalated", "在线图档长期未交接", $"{document.FileName}在线占用已超过48小时，请负责人核对未提交内容，必要时按授权紧急接管。在线修改不自动释放。", document.ProjectId, "edit-online-48h:" + key, ct);
            if (!settings.CheckoutAutoReleaseEnabled || now - heartbeat < TimeSpan.FromHours(settings.CheckoutForceReleaseHours) || document.CheckoutLeaseExpiresAt >= now) continue;
            try
            {
                await repository.ReclaimCheckoutAsync(document.Id, sessionId, started, now.AddHours(-settings.CheckoutForceReleaseHours), now, ct);
            }
            catch (PdmConflictException) { continue; }
            count++;
            await AuditAsync("system", "document.checkout.auto-offline-release", nameof(PdmDocument), document.Id.ToString(),
                $"持续离线超过{settings.CheckoutForceReleaseHours}小时；原编辑人：{document.CheckedOutBy}；原会话：{sessionId}；电脑：{document.CheckoutMachine}；未上传修改须从原电脑恢复", ct);
            await NotifyEditReleasedAsync(document, ct);
            await EditNoticeAsync(managers.Append(document.CheckedOutBy), "EditLockReleased", "长期离线图档已自动回收",
                $"{document.FileName}持续离线已达到回收时限。旧会话已失效，接手人使用服务器最后存档版本；原电脑的未上传修改须另存并人工比对。", document.ProjectId, "edit-auto-released:" + key, ct);
        }
        foreach (var handoff in await repository.ListEditHandoffsAsync(ct))
        {
            if (handoff.ExpiresAt > now || !await repository.ExpireEditHandoffAsync(handoff, now, ct)) continue;
            var document = await repository.FindDocumentAsync(handoff.DocumentId, ct);
            if (document is null) continue;
            var waiting = handoff.WaitSourceKey is null ? [] : await repository.ListNotificationRecipientsAsync(handoff.WaitSourceKey, ct);
            await EditNoticeAsync(waiting.Concat([handoff.FromUser, handoff.ToUser]), "EditLockAvailable", "图档交接预留已到期",
                $"{document.FileName}的5分钟接手预留已到期，现在可按权限重新申请编辑。", handoff.ProjectId, $"edit-handoff-expired:{handoff.DocumentId:N}:{handoff.ExpiresAt.UtcTicks}", ct);
        }
        return count;
    }

    private Task EditNoticeAsync(IEnumerable<string> recipients, string kind, string title, string message, Guid projectId, string key, CancellationToken ct) =>
        repository.CreateUserNotificationsAsync(recipients.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new UserNotification(Guid.NewGuid(), name, kind, title, message, projectId, null, key, timeProvider.GetUtcNow(), null)).ToArray(), ct);
}
