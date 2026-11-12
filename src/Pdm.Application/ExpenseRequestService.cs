using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ExpenseRequestService(IPdmRepository repository, IExpenseRequestRepository store, TimeProvider timeProvider, WorkflowNotificationService? workflowNotifications = null)
{
    public async Task<ExpenseDirectory> GetAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, actor, role, ct);
        var settings = (await repository.GetSystemSettingsAsync(ct)).ApprovalWorkflows;
        var canCreate = await CanCreateAsync(project.Id, actor, ct);
        var canApprove = await CanApproveAsync(project.Id, actor, ct);
        var requests = await store.ListAsync(projectId, ct);
        var context = await ProjectFinancialAccessPolicy.CreateContextAsync(repository, actor, ct);
        var access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, actor, role, ct, context);
        bool VisibleAmount(ExpenseRequest request) => access.ActualFor(ExpenseDepartment(request, project, context.Directory));
        return new(projectId, project.Code, project.Name, canCreate,
            requests.Where(request => VisibleAmount(request) || request.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase)
                || request.Steps.Any(step => step.Participants.Members.Contains(actor, StringComparer.OrdinalIgnoreCase)
                    || step.Participants.CopyOnApproval?.Recipients.Contains(actor, StringComparer.OrdinalIgnoreCase) == true))
                .Select(request => View(request, actor, canCreate, canApprove, VisibleAmount(request))).ToArray(),
            [new("Procurement", "采购部", settings.Expense is not null || settings.ExpenseProcurement is not null),
             new("SupplyChain", "供应链", settings.Expense is not null || settings.ExpenseSupplyChain is not null)]);
    }

    public async Task<ExpenseRoutePreview> PreviewAsync(Guid projectId, SaveExpenseRequest input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, actor, role, ct);
        if (!await CanCreateAsync(project.Id, actor, ct)) throw new UnauthorizedAccessException("当前账号没有本项目的费用申请发起权限。");
        var lines = NormalizeLines(input.Lines, true);
        var amount = lines.Sum(line => line.Amount!.Value);
        var workflow = await WorkflowAsync(input.Department, ct);
        var steps = await BuildStepsAsync(workflow, project, actor, input with { Lines = lines }, amount, ct);
        var users = await repository.ListUsersAsync(ct);
        return new(steps.Select(step => $"{step.Name}（{string.Join("、", step.Participants.Members.Select(member => users.First(user => user.Username.Equals(member, StringComparison.OrdinalIgnoreCase)).DisplayName))}）").ToArray(), amount);
    }

    public async Task<ExpenseRequestView> SaveAsync(Guid projectId, Guid id, SaveExpenseRequest input, string actor, UserRole role, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new PdmRuleException("申请标识无效，请重新打开申请单。");
        var project = await RequireProjectAsync(projectId, actor, role, ct);
        var canCreate = await CanCreateAsync(project.Id, actor, ct);
        if (!canCreate) throw new UnauthorizedAccessException("当前账号没有本项目的费用申请发起权限。");
        var current = await store.FindAsync(id, ct);
        if (current is not null && (current.ProjectId != projectId || !current.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("只能修改本人在当前项目发起的费用申请。");
        if (current is not null && current.Status is not ("Draft" or "Rejected" or "Withdrawn"))
            throw new PdmRuleException("当前申请已提交审批，请刷新查看处理结果。");
        if (input.Purpose is not ("SitePurchase" or "RepairSupplement")) throw new PdmRuleException("请选择现场采购或维修补订。");
        if (input.Department is not ("Procurement" or "SupplyChain")) throw new PdmRuleException("请选择采购部或供应链审批。");
        var reason = Text(input.Reason, 2000, "申请原因", input.Submit);
        if (input.Submit && (!input.RequiredOn.HasValue || input.RequiredOn == DateOnly.MinValue)) throw new PdmRuleException("请填写需求日期。");
        var lines = NormalizeLines(input.Lines, input.Submit);
        ApprovalWorkflowTemplate? workflow = null;
        IReadOnlyList<ExpenseApprovalStep> steps = [];
        if (input.Submit)
        {
            workflow = await WorkflowAsync(input.Department, ct);
            steps = await BuildStepsAsync(workflow, project, actor, input with { Lines = lines }, lines.Sum(line => line.Amount!.Value), ct);
        }
        var request = new ExpenseRequest(id, projectId, project.Code, project.Name, actor, input.Purpose,
            input.Department, input.RequiredOn, reason, lines, input.Submit ? "Pending" : "Draft",
            workflow, steps, input.ExpectedRowVersion, timeProvider.GetUtcNow());
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var workflows = (await repository.GetSystemSettingsAsync(ct)).ApprovalWorkflows;
        var departmentWorkflow = workflow ?? ExpenseWorkflowDefaults.Unified(workflows);
        request = request with { DepartmentId = ExpenseDepartment(request with { Workflow = departmentWorkflow }, project, directory) };
        if (input.Submit && request.DepartmentId is null) {
            var actorUser = (await repository.ListUsersAsync(ct)).FirstOrDefault(user => user.Username.Equals(actor, StringComparison.OrdinalIgnoreCase));
            var route = ResolveRoute(workflow!, project, actor, actorUser, directory, input with { Lines = lines }, lines.Sum(line => line.Amount!.Value));
            request = request with { DepartmentId = ExpenseDepartment(request with { Workflow = route with { ExpenseDepartmentIds = null } }, project, directory) };
        }
        var saved = await store.SaveAsync(request, input.ExpectedRowVersion, await NotificationsAsync(request, null, ct), ct);
        if (input.Submit && workflowNotifications is not null) await workflowNotifications.CaptureExpenseAsync(saved,ct);
        return View(saved, actor, canCreate, false);
    }

    public async Task<ExpenseRequestView> WithdrawAsync(Guid projectId, Guid id, WithdrawExpenseRequest input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, actor, role, ct);
        var current = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("费用申请不存在。");
        if (current.ProjectId != projectId) throw new PdmNotFoundException("当前项目中没有这份费用申请。");
        if (!current.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase) || !await CanCreateAsync(project.Id, actor, ct))
            throw new UnauthorizedAccessException("撤回费用申请需要本人提交记录及本项目的发起权限。");
        if (current.Status != "Pending") throw new PdmConflictException("仅审批中的费用申请可以撤回，请刷新后查看最新状态。");
        var changed = current with { Status = "Withdrawn", UpdatedAt = timeProvider.GetUtcNow() };
        var saved = await store.SaveAsync(changed, input.ExpectedRowVersion, await NotificationsAsync(changed, null, ct), ct);
        return View(saved, actor, true, false);
    }

    public async Task<ExpenseRequestView> DecideAsync(Guid projectId, Guid id, DecideExpenseRequest input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await RequireProjectAsync(projectId, actor, role, ct);
        var current = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("费用申请不存在。");
        if (current.ProjectId != projectId) throw new PdmNotFoundException("当前项目中没有这份费用申请。");
        var canApprove = await CanApproveAsync(project.Id, actor, ct);
        var step = current.CurrentStep ?? throw new PdmConflictException("该申请当前没有待处理的审批节点，请刷新。");
        if (!canApprove || !step.Participants.CanSign(actor)) throw new UnauthorizedAccessException("您不是当前节点的待审批人员。");
        var comment = Text(input.Comment, 1000, "审批意见", !input.Approve);
        if (input.BusinessNote is not null) Text(input.BusinessNote, 500, "申请原因", true);
        var now = timeProvider.GetUtcNow();
        var participants = ApprovalNodeRules.Sign(step.Participants, actor,
            input.Approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected, comment, now, false, out var waiting, input.BusinessNote)!;
        var decided = step with { Participants = participants, Decision = waiting ? null : input.Approve ? "Approved" : "Rejected",
            Actor = waiting ? null : actor, Comment = waiting ? null : comment, DecidedAt = waiting ? null : now };
        var steps = current.Steps.Select(item => item.Id == step.Id ? decided : item).ToArray();
        var changed = current with { Steps = steps, UpdatedAt = now,
            Reason = input.BusinessNote is null ? current.Reason : input.BusinessNote.Trim(),
            Status = !input.Approve ? "Rejected" : steps.All(item => item.Decision == "Approved") ? "Approved" : "Pending" };
        var saved = await store.SaveAsync(changed, input.ExpectedRowVersion,
            await NotificationsAsync(changed, decided.Decision == "Approved" ? decided : null, ct), ct);
        var context = await ProjectFinancialAccessPolicy.CreateContextAsync(repository, actor, ct);
        var access = await ProjectFinancialAccessPolicy.ReadAsync(repository, project, actor, role, ct, context);
        return View(saved, actor, await CanCreateAsync(project.Id, actor, ct), canApprove,
            access.ActualFor(ExpenseDepartment(saved, project, context.Directory)));
    }

    private async Task<ApprovalWorkflowTemplate> WorkflowAsync(string department, CancellationToken ct)
    {
        var settings = (await repository.GetSystemSettingsAsync(ct)).ApprovalWorkflows;
        if (department is not ("Procurement" or "SupplyChain")) throw new PdmRuleException("请选择采购部或供应链审批。");
        return ExpenseWorkflowDefaults.Unified(settings)
            ?? throw new PdmRuleException("尚未配置费用审批流程，请联系管理员在统一审批设置中配置部门分支及审批人员。");
    }

    private async Task<IReadOnlyList<ExpenseApprovalStep>> BuildStepsAsync(ApprovalWorkflowTemplate workflow, Project project,
        string actor, SaveExpenseRequest input, decimal amount, CancellationToken ct)
    {
        var users = await repository.ListUsersAsync(ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var route = ResolveRoute(workflow, project, actor, users.FirstOrDefault(user => user.Username.Equals(actor, StringComparison.OrdinalIgnoreCase)), directory, input, amount);
        var primaryId = directory.Memberships.FirstOrDefault(item => item.IsPrimary && item.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))?.UnitId;
        var primary = directory.Units.FirstOrDefault(unit => unit.Id == primaryId && unit.IsActive && (project.OrganizationId is null || unit.OrganizationId == project.OrganizationId));
        var result = new List<ExpenseApprovalStep>();
        foreach (var step in route.Steps.Skip(1))
        {
            var group = await ApprovalNodeRules.ResolveGroupAsync(repository, step, project, users, ct, workflow.Code);
            if (group is null)
            {
                var unitId = step.AssigneeSource switch { ApprovalAssigneeSource.FixedUnitManager => step.FixedUnitId,
                    ApprovalAssigneeSource.PrimaryUnitManager => primary?.Id, ApprovalAssigneeSource.ParentUnitManager => primary?.ParentUnitId, _ => null };
                var organizational = step.AssigneeSource is ApprovalAssigneeSource.FixedUnitManager or ApprovalAssigneeSource.PrimaryUnitManager or ApprovalAssigneeSource.ParentUnitManager;
                var assignee = organizational ? directory.Managers.FirstOrDefault(manager => manager.UnitId == unitId)?.PrimaryManager
                    : step.AssigneeSource == ApprovalAssigneeSource.ProjectDesignLead ? project.DesignLead : step.FixedAssignee;
                if (organizational
                    && !directory.Units.Any(unit => unit.Id == unitId && unit.IsActive && (project.OrganizationId is null || unit.OrganizationId == project.OrganizationId)))
                    throw new PdmRuleException($"“{step.Name}”的部门已失效，请联系管理员检查审批设置。");
                var user = users.FirstOrDefault(user => user.IsActive && user.Username.Equals(assignee, StringComparison.OrdinalIgnoreCase));
                if (user is null || project.OrganizationId is not null && user.CompanyId is not null && user.CompanyId != project.OrganizationId
                    || !await CanApproveAsync(project.Id, user.Username, ct))
                    throw new PdmRuleException($"“{step.Name}”尚未配置可审批本项目的有效人员，请联系管理员检查指定人员及权限。");
                group = new([user.Username], false, []);
            }
            else
                foreach (var member in group.Members)
                    if (!await CanApproveAsync(project.Id, member, ct))
                        throw new PdmRuleException($"“{step.Name}”有人员缺少本项目的费用申请查看或审批权限，请检查审批设置和权限中心。");
            var copy = await ApprovalNodeCopies.ResolveAsync(repository, step, project, actor, users, ct);
            result.Add(new(Guid.NewGuid(), step.Name, ApprovalNodeRules.Freeze(group, group.Members[0], step.Policy, copy)!));
        }
        return result;
    }

    private async Task<IReadOnlyList<UserNotification>> NotificationsAsync(ExpenseRequest request, ExpenseApprovalStep? approved, CancellationToken ct)
    {
        var result = new List<UserNotification>();
        var sourceKey = $"expense-request:{request.Id:N}:{request.RowVersion + 1}";
        var purpose = request.Purpose == "SitePurchase" ? "现场采购" : "维修补订";
        var content = $"项目 {request.ProjectCode} · {purpose} · 需求日期 {request.RequiredOn:yyyy-MM-dd}。请进入费用申请查看并处理，金额按当前权限显示。";
        foreach (var recipient in request.CurrentStep?.Participants.Remaining() ?? [])
            result.Add(new(Guid.NewGuid(), recipient, "ExpenseApprovalPending", $"费用申请待审批 · {request.CurrentStep!.Name}", content,
                request.ProjectId, null, sourceKey, request.UpdatedAt, null));
        if (request.Status is "Approved" or "Rejected" or "Withdrawn")
            result.Add(new(Guid.NewGuid(), request.Applicant, "ExpenseApprovalResult", request.Status == "Approved" ? "费用申请已通过" : request.Status == "Withdrawn" ? "费用申请已撤回" : "费用申请已退回",
                content, request.ProjectId, null, sourceKey, request.UpdatedAt, null));
        if (approved?.Participants.CopyOnApproval is { } copy)
            foreach (var recipient in copy.Recipients)
                result.Add(new(Guid.NewGuid(), recipient, "ExpenseApprovalCopy", $"费用审批抄送 · {approved.Name}", copy.Message + "\n" + content,
                    request.ProjectId, null, sourceKey + ":copy", request.UpdatedAt, null));
        return await NotificationRecipientPolicy.FilterAsync(repository, result, ct);
    }

    private static ExpenseRequestView View(ExpenseRequest request, string actor, bool canCreate, bool canApprove, bool canViewAmount = true) => new(
        canViewAmount ? request : request with { Lines = request.Lines.Select(line => line with { UnitPrice = null }).ToArray() },
        canCreate && request.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase) && request.Status is "Draft" or "Rejected" or "Withdrawn",
        canApprove && request.CurrentStep?.Participants.CanSign(actor) == true, canViewAmount,
        canCreate && request.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase) && request.Status == "Pending");

    private static Guid? ExpenseDepartment(ExpenseRequest request, Project project, OrganizationDirectory directory)
    {
        var current = request.DepartmentId ?? (request.Workflow?.ExpenseDepartmentIds is { } departments
            ? departments.TryGetValue(request.Department, out var unitId) ? unitId : (Guid?)null
            : request.Workflow?.ExpenseDepartmentId ?? request.Workflow?.Steps.Skip(1).Select(step => step.FixedUnitId).FirstOrDefault(id => id.HasValue));
        var seen = new HashSet<Guid>();
        while (current.HasValue && seen.Add(current.Value))
        {
            var unit = directory.Units.FirstOrDefault(unit => unit.Id == current && unit.IsActive && unit.OrganizationId == project.OrganizationId);
            if (unit is null) return null;
            if (unit.Kind == OrganizationUnitKind.Department) return unit.Id;
            current = unit.ParentUnitId;
        }
        return null;
    }

    private static ApprovalWorkflowTemplate ResolveRoute(ApprovalWorkflowTemplate workflow, Project project, string actor, UserAccount? user,
        OrganizationDirectory directory, SaveExpenseRequest input, decimal amount) => ApprovalNodeRules.ResolveRoute(workflow, user, amount,
            ApprovalConditionRules.Context(project, actor, directory, new Dictionary<string, object?> {
                ["TotalAmount"] = amount, ["Purpose"] = input.Purpose, ["Department"] = input.Department, ["RequiredOn"] = input.RequiredOn,
                ["Reason"] = input.Reason, ["LineCount"] = input.Lines.Count, ["ItemKind"] = input.Lines.Select(line => line.Kind).Distinct().ToArray()
            }));

    private async Task<Project> RequireProjectAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(id, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await AuthorizationPolicy.CanAsync(repository, id, actor, PermissionCodes.ProjectExpenseView, ct))
            throw new UnauthorizedAccessException("当前账号没有本项目的费用申请查看权限。");
        return project;
    }

    private Task<bool> CanCreateAsync(Guid projectId, string actor, CancellationToken ct) =>
        AuthorizationPolicy.CanAsync(repository, projectId, actor, PermissionCodes.ProjectExpenseSubmit, ct);

    private Task<bool> CanApproveAsync(Guid projectId, string actor, CancellationToken ct) =>
        AuthorizationPolicy.CanAsync(repository, projectId, actor, PermissionCodes.ProjectExpenseApprove, ct);

    internal static IReadOnlyList<ExpenseLine> NormalizeLines(IReadOnlyList<ExpenseLine> input, bool submit)
    {
        if (input is null || input.Count > 500 || submit && input.Count == 0) throw new PdmRuleException("请填写1至500行采购明细。");
        return input.Select((line, index) =>
        {
            var label = $"第{index + 1}行";
            if (line.Kind is not ("Material" or "Service")) throw new PdmRuleException($"{label}请选择物料或服务。");
            if (line.Quantity is <= 0 or > 100000000 || line.UnitPrice is < 0 or > 100000000
                || submit && (!line.Quantity.HasValue || !line.UnitPrice.HasValue))
                throw new PdmRuleException($"{label}请填写大于0的数量和不小于0的预计单价，单项数值最多一亿。");
            if (line.Quantity.HasValue) U9UnitCatalog.RequireWholeQuantity(line.Quantity.Value, line.Unit, label + "数量");
            if (line.UnitPrice.HasValue && decimal.Round(line.UnitPrice.Value, 2) != line.UnitPrice.Value)
                throw new PdmRuleException($"{label}预计单价最多保留两位小数。");
            return line with { Name = Text(line.Name, 200, label + "名称", submit), Unit = Text(line.Unit, 30, label + "单位", submit),
                MaterialCode = Text(line.MaterialCode, 120, label + "物料编码"), Specification = Text(line.Specification, 1000, label + "规格或服务内容"),
                Brand = Text(line.Brand, 100, label + "品牌"), Note = Text(line.Note, 500, label + "备注") };
        }).ToArray();
    }

    private static string Text(string? value, int maximum, string label, bool required = false)
    {
        var text = value?.Trim() ?? "";
        if (required && text.Length == 0) throw new PdmRuleException($"请填写{label}。");
        if (text.Length > maximum) throw new PdmRuleException($"{label}最多填写{maximum}个字符。");
        return text;
    }
}
