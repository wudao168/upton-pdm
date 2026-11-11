using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public sealed class MaterialCodeApprovalWorkflow(IPdmRepository repository, TimeProvider timeProvider)
{
    public async Task<MaterialCodeApprovalSnapshot> CreateAsync(Guid projectId, string actor, ApprovalWorkflowTemplate template, CancellationToken ct, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var users = (await repository.ListUsersAsync(ct)).Where(user => user.IsActive).ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        template = ApprovalNodeRules.ResolveRoute(template, users.FirstOrDefault(user => user.Username.Equals(actor, StringComparison.OrdinalIgnoreCase)),
            fields: ApprovalConditionRules.Context(project, actor, directory, fields));
        var primaryId = directory.Memberships.FirstOrDefault(item => item.IsPrimary && item.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))?.UnitId;
        var primary = directory.Units.FirstOrDefault(unit => unit.Id == primaryId && unit.IsActive);
        string? Manager(Guid? id) => directory.Units.Any(unit => unit.Id == id && unit.IsActive)
            ? directory.Managers.FirstOrDefault(item => item.UnitId == id)?.PrimaryManager : null;
        var nodes = new List<MaterialCodeApprovalNode>();
        foreach (var (step, index) in template.Steps.Select((step, index) => (step, index)))
        {
            var person = step.AssigneeSource switch {
                ApprovalAssigneeSource.Submitter => actor,
                ApprovalAssigneeSource.ProjectDesignLead => project.DesignLead,
                ApprovalAssigneeSource.FixedUser => step.FixedAssignee,
                ApprovalAssigneeSource.FixedUnitManager => Manager(step.FixedUnitId),
                ApprovalAssigneeSource.PrimaryUnitManager => Manager(primary?.Id),
                ApprovalAssigneeSource.ParentUnitManager => Manager(primary?.ParentUnitId),
                _ => null
            };
            var group = await ApprovalNodeRules.ResolveGroupAsync(repository, step, project, users, ct, template.Code);
            var assignees = group?.Members.ToList() ?? [];
            if (group is null && users.FirstOrDefault(user => user.Username.Equals(person, StringComparison.OrdinalIgnoreCase)) is { } candidate
                && await repository.HasProjectContentReadAccessAsync(projectId, candidate.Username, candidate.Role, ct)) assignees.Add(candidate.Username);
            if (assignees.Count == 0) throw new PdmRuleException($"料号申请审批的“{step.Name}”没有可访问此项目的有效审批人，请检查人员、角色和项目权限。");
            var self = index == 0 && step.AssigneeSource == ApprovalAssigneeSource.Submitter;
            nodes.Add(new MaterialCodeApprovalNode(Guid.NewGuid(), index + 1, step.Stage, step.Name, assignees,
                self ? ApprovalDecision.Approved : null, self ? actor : null, self ? "提交申请即完成申请人自检" : null, self ? timeProvider.GetUtcNow() : null)
                { Departments = group?.Departments ?? [], RequireAllApprovers = group?.RequireAll ?? step.RequireAllApprovers, Policy = step.Policy, CopyOnApproval = await ApprovalNodeCopies.ResolveAsync(repository, step, project, actor, users, ct) });
        }
        return new(template.Code, template.Version, nodes, template.EmergencyRoleCodes ?? []);
    }
}
