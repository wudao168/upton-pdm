namespace Upton.Pdm.Domain;
public static class MaterialCodeWorkflowDefaults
{
    public static ApprovalWorkflowTemplate Template(IReadOnlyList<string>? roleCodes = null, int version = 1) => new(
        "material-code-application", "料号申请审批", version,
        [new(ApprovalStage.MechanicalEngineer, "申请人自检", ApprovalAssigneeSource.Submitter),
         new(ApprovalStage.StandardizationSupervisor, "标准化审核", ApprovalAssigneeSource.Role,
            RoleCodes: roleCodes ?? MaterialCodeApprovalSettings.Default.ApproverRoleCodes)]);
}
