using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public sealed class MaterialCodeApprovalWorkflow(IPdmRepository repository, TimeProvider timeProvider)
{
    public async Task<MaterialCodeApprovalSnapshot> CreateAsync(Guid projectId, string actor, ApprovalWorkflowTemplate template, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var users = (await repository.ListUsersAsync(ct)).Where(user => user.IsActive).ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
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
            var candidates = step.AssigneeSource == ApprovalAssigneeSource.Role
                ? users.Where(user => (step.RoleCodes ?? []).Any(user.HasRole)).ToArray()
                : users.Where(user => string.Equals(user.Username, person, StringComparison.OrdinalIgnoreCase)).ToArray();
            var assignees = new List<string>();
            foreach (var user in candidates)
                if (await repository.HasProjectContentReadAccessAsync(projectId, user.Username, user.Role, ct)) assignees.Add(user.Username);
            if (assignees.Count == 0) throw new PdmRuleException($"料号申请审批的“{step.Name}”没有可访问此项目的有效审批人，请检查人员、角色和项目权限。");
            var self = index == 0 && step.AssigneeSource == ApprovalAssigneeSource.Submitter;
            nodes.Add(new MaterialCodeApprovalNode(Guid.NewGuid(), index + 1, step.Stage, step.Name, assignees,
                self ? ApprovalDecision.Approved : null, self ? actor : null, self ? "提交申请即完成申请人自检" : null, self ? timeProvider.GetUtcNow() : null));
        }
        return new(template.Code, template.Version, nodes, template.EmergencyRoleCodes ?? []);
    }
}
