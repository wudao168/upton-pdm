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
        var platform = AuthorizationPolicy.GlobalAllows(await repository.GetAuthorizationSettingsAsync(ct), recipient, scope.PrimaryCompanyId, AuthorizationActions.PlatformManage);
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
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View, ct);
        var current = await RequireDocumentAsync(documentId, ct);
        var project = await repository.FindProjectAsync(current.ProjectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await AuthorizationPolicy.CanAsync(repository, project.Id, actor, PermissionCodes.DocumentLockEmergencyRelease, ct)) throw new UnauthorizedAccessException("当前账号没有此项目的紧急接管权限。");
        reason = RequiredReason(reason);
        var updated = await repository.ReclaimCheckoutAsync(documentId, sessionId, checkedOutAt, null, timeProvider.GetUtcNow(), ct);
        await AuditAsync(actor, "document.checkout.emergency-release", nameof(PdmDocument), documentId.ToString(),
            $"原编辑人：{current.CheckedOutBy}；原会话：{sessionId}；原电脑：{current.CheckoutMachine}；原因：{reason}；未上传修改由原电脑恢复并比对", ct);
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
        return updated;
    }

    // 按本轮实际发出的提醒计时，仓储事务内再次核对编辑代次、审核和上传状态。
    public async Task<int> MaintainEditCollaborationAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await repository.GetSystemSettingsAsync(ct);
        var count = 0;
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(ct))
        {
            if (document.CheckoutSessionId is not Guid sessionId || document.CheckedOutAt is not DateTimeOffset started || string.IsNullOrWhiteSpace(document.CheckedOutBy)) continue;
            if (now - started < TimeSpan.FromHours(settings.CheckoutOverdueHours)) continue;
            var key = EditLockOverdueReminder.SourceKey(document);
            var sentAt = await repository.FindNotificationCreatedAtAsync(document.CheckedOutBy, key, ct);
            if (sentAt is null)
            {
                var deadline = settings.CheckoutAutoReleaseEnabled
                    ? $"提醒发出后{settings.CheckoutOverdueReleaseGraceHours}小时仍未处理，系统将自动释放编辑权限。请及时存档未提交修改。"
                    : string.Empty;
                await EditNoticeAsync([document.CheckedOutBy], "EditLockOverdue", "图档占用超时提醒",
                    $"{document.FileName}已占用满{settings.CheckoutOverdueHours}小时，请存档或释放编辑权限。{deadline}", document.ProjectId, key, ct);
                continue;
            }
            if (!settings.CheckoutAutoReleaseEnabled || now - sentAt.Value < TimeSpan.FromHours(settings.CheckoutOverdueReleaseGraceHours)) continue;
            try
            {
                await repository.ReclaimCheckoutAsync(document.Id, sessionId, started, null, now, ct);
            }
            catch (PdmConflictException) { continue; }
            count++;
            await AuditAsync("system", "document.checkout.auto-overdue-release", nameof(PdmDocument), document.Id.ToString(),
                $"占用提醒后{settings.CheckoutOverdueReleaseGraceHours}小时未处理；原编辑人：{document.CheckedOutBy}；原会话：{sessionId}；电脑：{document.CheckoutMachine}；未上传修改须从原电脑恢复", ct);
            await repository.ResolveUserNotificationsAsync([key], now, ct);
        }
        foreach (var handoff in await repository.ListEditHandoffsAsync(ct))
        {
            if (handoff.ExpiresAt <= now) await repository.ExpireEditHandoffAsync(handoff, now, ct);
        }
        return count;
    }

    private Task EditNoticeAsync(IEnumerable<string> recipients, string kind, string title, string message, Guid projectId, string key, CancellationToken ct) =>
        repository.CreateUserNotificationsAsync(recipients.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new UserNotification(Guid.NewGuid(), name, kind, title, message, projectId, null, key, timeProvider.GetUtcNow(), null)).ToArray(), ct);
}

internal static class EditLockOverdueReminder
{
    public static string SourceKey(PdmDocument document) =>
        $"edit-overdue-owner:{document.Id:N}:{document.CheckoutSessionId:N}:{document.CheckedOutAt?.UtcTicks}";
}
