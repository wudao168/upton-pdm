using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ApprovalNodeRules
{
    public static ApprovalWorkflowTemplate ResolveRoute(ApprovalWorkflowTemplate template, UserAccount? submitter, decimal? amount = null, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (template.Layout is not null) return template with { Steps = ApprovalFlowGraph.Resolve(template,submitter,fields), Layout = null };
        if (!template.Steps.Any(step => step.ConditionRole is not null || step.AmountGreaterThan.HasValue || step.Conditions is not null)) return template;
        if (template.Steps.Any(step => step.ConditionRole is not null) && submitter is null)
            throw new PdmRuleException("提交人账号无效，无法匹配审批流程。");
        if (template.Steps.Any(step => step.AmountGreaterThan.HasValue) && !amount.HasValue)
            throw new PdmRuleException("请填写完整的预计金额后再提交审批。");
        var route = template.Steps.Where(step => (step.ConditionRole is null
            || submitter!.HasRole(step.ConditionRole) != step.UnlessRole)
            && (!step.AmountGreaterThan.HasValue || amount > step.AmountGreaterThan.Value)
            && ApprovalConditionRules.Matches(step.Conditions, template.Code, submitter, fields)).ToArray();
        if (route.Length < 2) throw new PdmRuleException("当前条件未匹配到后续审批节点，请联系管理员检查流程设置。");
        return template with { Steps = route };
    }

    public static async Task<ApprovalGroup?> ResolveGroupAsync(IPdmRepository repository, ApprovalWorkflowStepTemplate step,
        Project project, IReadOnlyList<UserAccount> users, CancellationToken ct, string flowCode = "")
    {
        if (step.AssigneeSource == ApprovalAssigneeSource.ParallelDepartments)
        {
            ValidateParallel(step.ParallelDepartments);
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var branches = new List<ApprovalDepartmentBranch>();
            foreach (var branch in step.ParallelDepartments!)
            {
                var group = await ResolveGroupAsync(repository, step with { AssigneeSource = ApprovalAssigneeSource.DepartmentMembers,
                    FixedUnitId = branch.UnitId, RequireAllApprovers = branch.RequireAll, EmptyFallbackUser = null, ParallelDepartments = null }, project, users, ct, flowCode);
                branches.Add(new(branch.UnitId, directory.Units.First(u => u.Id == branch.UnitId).Name, group!.Members, branch.RequireAll, []));
            }
            return new ApprovalGroup(branches.SelectMany(b => b.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), true, []) { Departments = branches, Policy = step.Policy };
        }
        if (step.AssigneeSource is not (ApprovalAssigneeSource.Role or ApprovalAssigneeSource.DepartmentMembers)) return null;
        var candidates = users.Where(user => user.IsActive && (project.OrganizationId is null || user.CompanyId is null || user.CompanyId == project.OrganizationId));
        if (step.AssigneeSource == ApprovalAssigneeSource.Role)
            candidates = candidates.Where(user => (step.RoleCodes ?? []).Any(user.HasRole));
        else
        {
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            if (!directory.Units.Any(unit => unit.Id == step.FixedUnitId && unit.IsActive && (project.OrganizationId is null || unit.OrganizationId == project.OrganizationId)))
                throw new PdmRuleException($"节点“{step.Name}”的部门无效，请检查审批设置。");
            var members = directory.Memberships.Where(member => member.UnitId == step.FixedUnitId)
                .Select(member => member.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidates = candidates.Where(user => members.Contains(user.Username));
        }
        var names = new List<string>();
        async Task<bool> CanHandle(UserAccount user) =>
            await repository.HasProjectContentReadAccessAsync(project.Id, user.Username, user.Role, ct)
            && (flowCode == "material-code-application" || await repository.HasUserPermissionAsync(user.Username, user.Role, PermissionCodes.ApprovalDecide, ct)
                || flowCode == "validation-plan" && await repository.HasUserPermissionAsync(user.Username, user.Role, PermissionCodes.ValidationPlanEdit, ct));
        foreach (var user in candidates)
            if (await CanHandle(user)) names.Add(user.Username);
        if (names.Count == 0 && step.EmptyFallbackUser is { } fallback)
        {
            var replacement = users.FirstOrDefault(user => user.IsActive && user.Username.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                && (project.OrganizationId is null || user.CompanyId is null || project.OrganizationId == user.CompanyId));
            if (replacement is not null && await CanHandle(replacement)) names.Add(replacement.Username);
        }
        if (names.Count == 0) throw new PdmRuleException($"节点“{step.Name}”没有能访问该项目的有效处理人，请检查角色、部门和项目权限。");
        return new ApprovalGroup(names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), step.RequireAllApprovers, []) { Policy = step.Policy };
    }

    public static ApprovalGroup? Freeze(ApprovalGroup? group, string assignee, ApprovalNodePolicy? policy, ApprovalCopyDelivery? copy)
    {
        if (group is null && policy is null && copy is null) return null;
        var snapshot = group ?? new ApprovalGroup([assignee], false, []);
        return snapshot with { Policy = policy, CopyOnApproval = copy };
    }

    public static ApprovalGroup? Sign(ApprovalGroup? group, string actor, ApprovalDecision decision, string? comment,
        DateTimeOffset now, bool emergency, out bool waiting, string? businessNote = null)
    {
        waiting = false;
        var notes = CheckInput(group?.Policy, group?.NoteHistory ?? [], actor, decision, comment, businessNote, now);
        if (group is null) return null;
        group = group with { NoteHistory = notes };
        if (group.Departments.Count > 0)
        {
            var branch = group.Departments.FirstOrDefault(b => b.CanSign(actor))
                ?? throw new PdmRuleException("您没有尚待处理的部门审批任务。");
            var updatedBranch = decision == ApprovalDecision.Approved
                ? branch with { Signatures = [.. branch.Signatures, new ApprovalSignature(actor, comment, now)] }
                : branch with { RejectedBy = actor, Rejection = comment, RejectedAt = now };
            var branches = group.Departments.Select(b => b.UnitId == branch.UnitId ? updatedBranch : b).ToArray();
            waiting = decision == ApprovalDecision.Approved && branches.Any(b => !b.Complete);
            return group with { Departments = branches, Signatures = branches.SelectMany(b => b.Signatures).ToArray() };
        }
        if (!group.CanSign(actor) && (!emergency || group.RequireAll && decision == ApprovalDecision.Approved))
            throw new PdmRuleException("只能由尚未处理该节点的审批人签署。");
        if (decision != ApprovalDecision.Approved) return group;
        var updated = group with { Signatures = [.. group.Signatures, new ApprovalSignature(actor, comment, now)] };
        waiting = updated.RequireAll && updated.Remaining().Any();
        return updated;
    }

    public static void ValidateParallel(IReadOnlyList<ApprovalDepartmentTemplate>? branches)
    {
        if (branches is null || branches.Count is < 2 or > 12 || branches.Any(b => b.UnitId == Guid.Empty)
            || branches.Select(b => b.UnitId).Distinct().Count() != branches.Count)
            throw new PdmRuleException("并行审批须配置2至12个不同部门，各部门全部通过后再进入下一节点。");
    }

    public static IReadOnlyList<ApprovalNoteRevision> CheckInput(ApprovalNodePolicy? policy, IReadOnlyList<ApprovalNoteRevision> history,
        string actor, ApprovalDecision decision, string? comment, string? businessNote, DateTimeOffset now)
    {
        if (decision == ApprovalDecision.Approved && policy?.RequireOpinion == true && string.IsNullOrWhiteSpace(comment))
            throw new PdmRuleException("该审批节点要求填写同意意见。");
        if (businessNote is null) return history;
        if (policy?.EditBusinessNote != true) throw new PdmRuleException("该审批节点未授权修改业务说明。");
        var text = businessNote.Trim();
        if (text.Length > 500) throw new PdmRuleException("业务说明最多可填写500个字符。");
        return [.. history, new ApprovalNoteRevision(actor, text, now)];
    }
}
