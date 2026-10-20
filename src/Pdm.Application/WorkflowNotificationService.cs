using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class WorkflowNotificationService(IPdmRepository repository, IWorkflowNotificationStore store,
    IMaterialRepository materials, IValidationPlanRepository validationPlans, TimeProvider clock)
{
    private async Task<Guid> RequireAdminAsync(string actor, UserRole role, CancellationToken ct)
    {
        var allowed = await repository.HasUserPermissionAsync(actor, role,
            PermissionCodes.StorageSettingsManage, ct);
        if (!allowed) throw new UnauthorizedAccessException("没有维护审批通知的权限。");
        return TenantContext.CompanyId ?? throw new PdmRuleException("请先选择当前公司。");
    }

    public async Task<WorkflowNotificationCatalog> ReadAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, ct))
            throw new UnauthorizedAccessException("没有查看审批通知配置的权限。");
        return await store.ReadAsync(TenantContext.CompanyId ?? throw new PdmRuleException("请先选择当前公司。"), ct);
    }

    public async Task<WorkflowNotificationCatalog> SaveFlowAsync(string flowCode, SaveWorkflowNotification input, string actor, UserRole role, CancellationToken ct)
    {
        var company = await RequireAdminAsync(actor, role, ct);
        if (input.CompanyId != company) throw new PdmConflictException("当前公司已变化，请刷新通知设置后重新保存。");
        if (!WorkflowNotificationFlows.Names.ContainsKey(flowCode)) throw new PdmRuleException("通知流程不存在，请刷新页面。");
        var catalog = await store.ReadAsync(company, ct);
        if (input.Recipients is null || input.Recipients.Count > 100 || input.Enabled && input.Recipients.Count == 0)
            throw new PdmRuleException("启用通知时，请至少选择一个接收角色，最多100组；部门可以不选。");
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var units = ActiveUnits(directory, company);
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles;
        var recipients = new List<WorkflowNotificationRecipient>();
        foreach (var recipient in input.Recipients)
        {
            if (recipient.DepartmentSource is not ("Fixed" or "SubmitterDepartment" or "CompanyRole")
                || recipient.DepartmentSource == "Fixed" && (!recipient.DepartmentId.HasValue || !units.Contains(recipient.DepartmentId.Value))
                || recipient.DepartmentSource != "Fixed" && recipient.DepartmentId.HasValue)
                throw new PdmRuleException("请选择当前公司的部门、“提交人部门”或“不限部门”。");
            var selectedRole = roles.FirstOrDefault(item => item.Role.Equals(recipient.RoleCode?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedRole is null || recipient.PositionId != Guid.Empty)
                throw new PdmRuleException("请选择有效的接收角色；原接收配置请重新选择角色后保存。");
            recipients.Add(recipient with { RoleCode = selectedRole.Role });
        }
        var rule = new WorkflowNotificationRule(flowCode, input.Enabled, recipients.Distinct().ToArray());
        return await SaveAsync(catalog with { Flows = catalog.Flows.Where(item => item.FlowCode != flowCode).Append(rule).ToArray() },
            input.ExpectedRowVersion, actor, flowCode, ct);
    }

    private async Task<WorkflowNotificationCatalog> SaveAsync(WorkflowNotificationCatalog catalog, long version, string actor, string target, CancellationToken ct)
    {
        if (!await store.SaveAsync(catalog, version, ct)) throw new PdmConflictException("通知设置已被其他人修改，请刷新后重新保存。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "workflow-notification.configure", "WorkflowNotification", target, "更新通知接收配置；审批流程保持原配置"), ct);
        return catalog with { RowVersion = version + 1 };
    }

    public async Task<IReadOnlyList<WorkflowNoticeJob>> RecordsAsync(string flowCode, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await ReadAsync(actor, role, ct);
        return await store.ListJobsAsync(catalog.CompanyId, flowCode, ct);
    }

    public async Task CaptureReleaseAsync(ReleasePackage package, CancellationToken ct, bool restart = false)
    {
        var first = package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault();
        if (first is null) return;
        var kind = ReleaseDeliveryService.Kind(package.Scope);
        var flows = kind == "All" ? new[] { "standard-release", "nonstandard-release", "electrical-release" }
            : new[] { kind == "Standard" ? "standard-release" : kind == "Electrical" ? "electrical-release" : "nonstandard-release" };
        foreach (var flow in flows) await CaptureAsync("Release", package.Id, package.ProjectId, first.Assignee, first.Id.ToString("N"), flow, ct, restart);
    }

    public Task CaptureValidationAsync(ProjectValidationPlan plan, CancellationToken ct) =>
        CaptureAsync("ValidationPlan", plan.Id, plan.ProjectId, plan.SubmittedBy ?? plan.CreatedBy,
            plan.ApprovalTasks.OrderBy(task => task.StepOrder).First().Id.ToString("N"), "validation-plan", ct);

    public Task CaptureMaterialAsync(MaterialCodeApplication application, CancellationToken ct) =>
        CaptureAsync("MaterialCode", application.Id, application.ProjectId, application.RequestedBy,
            application.RequestedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "material-code-application", ct);

    private async Task CaptureAsync(string kind, Guid id, Guid projectId, string submitter, string token, string flow, CancellationToken ct, bool restart = false)
    {
        var project = await repository.FindProjectAsync(projectId, ct);
        if (project?.OrganizationId is not Guid company) return;
        var catalog = await store.ReadAsync(company, ct);
        var rule = catalog.Flows.FirstOrDefault(item => item.FlowCode == flow) ?? new(flow, false, []);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        // 与现有审批节点使用同一主部门关系，在审批发起时固定，后续不随操作人或人员调动改变。
        var membership = directory.Memberships.FirstOrDefault(item => item.IsPrimary && item.Username.Equals(submitter, StringComparison.OrdinalIgnoreCase));
        var department = directory.Units.FirstOrDefault(unit => unit.Id == membership?.UnitId && unit.OrganizationId == company && unit.IsActive);
        var now = clock.GetUtcNow();
        var job = new WorkflowNoticeJob
        {
            Id = Guid.NewGuid(), CompanyId = company, ProjectId = projectId, BusinessId = id, BusinessKind = kind,
            BusinessToken = token, Submitter = submitter, SubmitterDepartmentId = department?.Id, Rule = rule,
            SourceKey = $"workflow-notice:{flow}:{id:N}:{token}", CreatedAt = now, NextCheckAt = now
        };
        var previous = await store.FindSourceAsync(company, job.SourceKey, ct);
        if (previous is not null && restart && previous.State != "Sent")
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (previous.State == "Sent") return;
                if (await store.SaveJobAsync(job with { Id = previous.Id, SubmitterDepartmentId = previous.SubmitterDepartmentId ?? job.SubmitterDepartmentId }, previous.RowVersion, ct)) return;
                previous = await store.FindSourceAsync(company, job.SourceKey, ct) ?? previous;
            }
            throw new PdmConflictException("通知记录正在更新，请刷新后重试。");
        }
        else await store.CaptureAsync(job, ct);
    }

    public async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var job = await store.FindJobAsync(id, ct);
        if (job is null || job.State is "Sent" or "Cancelled") return;
        var previousTenant = TenantContext.Current;
        TenantContext.Set(new(Guid.Empty, job.CompanyId, job.CompanyId, "workflow-notification", "System", false, new HashSet<string>()));
        try
        {
            if (job.State != "Ready")
            {
                var prepared = await PrepareAsync(job, ct);
                if (!await store.SaveJobAsync(prepared, job.RowVersion, ct)) return;
                job = prepared with { RowVersion = job.RowVersion + 1 };
                if (job.State != "Ready") return;
            }
            var messages = new List<UserNotification>();
            var skipped = false;
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            foreach (var message in job.Messages)
            {
                var user = await repository.FindUserAsync(message.Username, ct);
                if (user is null || !CompanyUser(directory, user.Username, job.CompanyId)) { skipped = true; continue; }
                var canRead = await repository.HasProjectContentReadAccessAsync(job.ProjectId, user.Username, user.Role, ct);
                if (!canRead && !message.RoleRecipient) { skipped = true; continue; }
                messages.Add(new(Guid.NewGuid(), user.Username, job.BusinessKind == "Release" ? "ReleaseDepartment" : job.BusinessKind == "ValidationPlan" ? "WorkflowValidationPlan" : "WorkflowMaterialCode",
                    message.Title, canRead ? message.Content : RoleSummary, job.ProjectId, job.BusinessKind == "Release" ? job.BusinessId : null,
                    job.NotificationSourceKey!, clock.GetUtcNow(), null));
            }
            await repository.CreateUserNotificationsAsync(messages, ct);
            await store.SaveJobAsync(job with { State = job.Incomplete || skipped ? "Attention" : "Sent",
                Message = skipped ? "部分接收人已停用、公司已变化或无项目查看权限，请检查账号与权限" : job.Incomplete ? job.Message : $"已生成{messages.Count}条站内通知" }, job.RowVersion, ct);
        }
        finally { if (previousTenant is null) TenantContext.Clear(); else TenantContext.Set(previousTenant); }
    }

    private async Task<WorkflowNoticeJob> PrepareAsync(WorkflowNoticeJob job, CancellationToken ct)
    {
        var next = job with { NextCheckAt = clock.GetUtcNow().AddSeconds(30) };
        var project = await repository.FindProjectAsync(job.ProjectId, ct);
        if (project?.OrganizationId != job.CompanyId) return next with { State = "Cancelled", Message = "关联项目不存在或公司已变化" };
        string title, content, sourceKey = job.SourceKey;
        ProjectValidationPlan? plan = null;
        if (job.BusinessKind == "Release")
        {
            var package = await repository.FindReleasePackageAsync(job.BusinessId, ct);
            if (package is null || package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Id.ToString("N") != job.BusinessToken)
                return next with { State = "Cancelled", Message = "原审批已取消或被新一轮审批替代" };
            if (package.State == ReleasePackageState.Rejected) return next with { State = "Cancelled", Message = "本轮审批已退回" };
            if (package.State != ReleasePackageState.Published) return next;
            var changed = package.Scope is ReleaseScope.StandardSupplement or ReleaseScope.NonStandardSupplement or ReleaseScope.ElectricalSupplement;
            title = changed ? "BOM增补/变更已发布" : "BOM已正式发布";
            content = $"{project.Code} · {package.Number} · {WorkflowNotificationFlows.Names[job.Rule.FlowCode]}已发布，请查看发布内容并处理后续U9C操作。";
            sourceKey = WorkflowNotificationFlows.ReleaseSourceKey(package.Id, changed ? "Changed" : "Published");
        }
        else if (job.BusinessKind == "ValidationPlan")
        {
            plan = await validationPlans.FindPlanByIdAsync(job.BusinessId, ct);
            if (plan is null || plan.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Id.ToString("N") != job.BusinessToken
                || plan.State is ProjectValidationPlanState.Rejected or ProjectValidationPlanState.Superseded)
                return next with { State = "Cancelled", Message = "验证计划审批已取消、退回或被替代" };
            if (plan.State != ProjectValidationPlanState.Effective) return next;
            title = "验证计划审批已通过";
            content = $"{project.Code} · 验证计划R{plan.RevisionNumber}已生效，请查看负责的验证事项。";
        }
        else
        {
            var application = await materials.FindMaterialCodeApplicationAsync(job.BusinessId, ct);
            if (application is null || application.Status is MaterialCodeApplicationStatus.Rejected or MaterialCodeApplicationStatus.Withdrawn)
                return next with { State = "Cancelled", Message = "料号申请已退回或撤回" };
            if (application.Status != MaterialCodeApplicationStatus.Approved || string.IsNullOrWhiteSpace(application.MaterialCode)
                || application.MaterialCode.StartsWith("PDM-PENDING-", StringComparison.OrdinalIgnoreCase)) return next;
            title = "申请的正式料号已生成";
            content = $"{project.Code} · {application.ApplicationName ?? application.BomItemName ?? "料号申请"} · 正式料号：{application.MaterialCode}。请查看申请结果。";
        }
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var catalog = await store.ReadAsync(job.CompanyId, ct);
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles;
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roleRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var units = ActiveUnits(directory, job.CompanyId);
        if (job.Rule.Enabled) foreach (var recipient in job.Rule.Recipients)
        {
            var unitId = recipient.DepartmentSource == "SubmitterDepartment" ? job.SubmitterDepartmentId : recipient.DepartmentId;
            if (recipient.RoleCode is not null)
            {
                var selectedRole = roles.FirstOrDefault(item => item.Role.Equals(recipient.RoleCode, StringComparison.OrdinalIgnoreCase));
                if (selectedRole is null || recipient.DepartmentSource is not ("Fixed" or "SubmitterDepartment" or "CompanyRole")
                    || recipient.DepartmentSource != "CompanyRole" && (unitId is null || !units.Contains(unitId.Value)))
                { warnings.Add("接收部门或角色失效，或提交人未配置有效主部门"); continue; }
                var members = directory.Memberships.Where(item => item.UnitId == unitId).Select(item => item.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matching = directory.Users.Where(user => CompanyUser(directory, user.Username, job.CompanyId) && user.EffectiveRoleCodes.Contains(selectedRole.Role, StringComparer.OrdinalIgnoreCase)
                    && (recipient.DepartmentSource == "CompanyRole" || members.Contains(user.Username))).Select(user => user.Username).ToArray();
                if (matching.Length == 0) warnings.Add($"{(recipient.DepartmentSource == "CompanyRole" ? "当前公司" : directory.Units.First(item => item.Id == unitId).Name)}／{selectedRole.Name}没有有效人员");
                recipients.UnionWith(matching);
                roleRecipients.UnionWith(matching);
                continue;
            }
            // 已发起流程继续使用原接收快照；新配置统一按系统角色匹配。
            var position = catalog.Positions.FirstOrDefault(item => item.Id == recipient.PositionId && item.IsActive);
            if (unitId is not Guid unit || !units.Contains(unit) || position is null)
            { warnings.Add("原接收配置失效，或提交人未配置有效主部门"); continue; }
            var assignment = catalog.Assignments.FirstOrDefault(item => item.DepartmentId == unit && item.PositionId == position.Id);
            var assigned = assignment?.Usernames ?? [];
            var eligible = assigned.Where(name => CompanyUser(directory, name, job.CompanyId)).ToArray();
            if (eligible.Length == 0) warnings.Add($"{directory.Units.First(item => item.Id == unit).Name}／{position.Name}未配置有效接收人员");
            recipients.UnionWith(eligible);
        }
        if (job.BusinessKind == "MaterialCode") recipients.Add(job.Submitter);
        var responsibleItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (plan is not null) foreach (var item in plan.Items)
        {
            foreach (var name in (item.ResponsiblePerson ?? "").Split([',', '，', ';', '；', '、', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var matches = directory.Users.Where(user => CompanyUser(directory, user.Username, job.CompanyId)
                    && (user.Username.Equals(name, StringComparison.OrdinalIgnoreCase) || user.DisplayName == name)).ToArray();
                if (matches.Length != 1) { warnings.Add($"验证责任人“{name}”无法唯一匹配有效账号"); continue; }
                var username = matches[0].Username;
                recipients.Add(username);
                if (!responsibleItems.TryGetValue(username, out var items)) responsibleItems[username] = items = [];
                items.Add(item.ValidationContent);
            }
        }
        var messages = new List<WorkflowNoticeMessage>();
        foreach (var recipient in recipients)
        {
            var user = directory.Users.FirstOrDefault(user => user.Username.Equals(recipient, StringComparison.OrdinalIgnoreCase));
            if (user is null || !CompanyUser(directory, recipient, job.CompanyId))
            { warnings.Add($"接收人{recipient}已停用或不属于当前公司"); continue; }
            var canRead = await repository.HasProjectContentReadAccessAsync(job.ProjectId, user.Username, user.Role, ct);
            var roleRecipient = roleRecipients.Contains(recipient);
            if (!canRead && !roleRecipient)
            { warnings.Add($"接收人{recipient}已停用或没有项目查看权限"); continue; }
            var detail = canRead ? content : RoleSummary;
            if (canRead && responsibleItems.TryGetValue(user.Username, out var items))
            {
                detail += " 您负责：" + string.Join("；", items.Take(5).Select(item => item.Length > 120 ? item[..120] + "…" : item));
                if (items.Count > 5) detail += $"；另有{items.Count - 5}项，请打开计划查看。";
            }
            if (detail.Length > 1000) detail = detail[..960] + "…请打开业务详情查看完整内容。";
            messages.Add(new(user.Username, title, detail, roleRecipient));
        }
        // 缺少角色人员时保留待处理记录，其他接收人仍正常收到通知。
        return next with { State = warnings.Count > 0 && messages.Count == 0 ? "Attention" : "Ready",
            Messages = messages, NotificationSourceKey = sourceKey, Incomplete = warnings.Count > 0,
            Message = warnings.Count > 0 ? string.Join("；", warnings.Distinct()) : $"待生成{messages.Count}条通知" };
    }

    private const string RoleSummary = "您所属角色有一条业务完成通知，请联系业务负责人处理后续工作；详情按项目权限开放。";

    private static HashSet<Guid> ActiveUnits(OrganizationDirectory directory, Guid company)
    {
        var result = new HashSet<Guid>();
        for (var previous = -1; previous != result.Count;)
        {
            previous = result.Count;
            foreach (var unit in directory.Units.Where(unit => unit.OrganizationId == company && unit.IsActive
                && (unit.ParentUnitId is null || result.Contains(unit.ParentUnitId.Value)))) result.Add(unit.Id);
        }
        return result;
    }
    private static bool CompanyUser(OrganizationDirectory directory, string username, Guid company) =>
        directory.Users.Any(user => user.IsActive && user.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
            && (user.CompanyId == company || user.CompanyId is null && directory.Memberships.Any(member => member.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                && directory.Units.Any(unit => unit.Id == member.UnitId && unit.OrganizationId == company && unit.IsActive))));
}
