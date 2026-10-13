using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectTeamPermissionTests
{
    [Fact]
    public async Task MissingOrganizationMembershipsReturnsValidationError()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SetOrganizationMembershipsAsync(
            "admin", null!, Guid.Empty, "admin", UserRole.Administrator, default));
        Assert.Contains("人员至少需要一个所属组织", error.Message);
    }

    [Fact]
    public async Task OnlyProjectAndExecutionDivisionManagersCanMaintainTeamOwners()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "团队权限验证", null, new(2026, 10, 3), 1, "admin", "D:/test", "D:/release"), default);
        var division = await repository.SaveOrganizationUnitAsync(new(null, root.OrganizationId!.Value, null, "TEAM-TEST", "团队测试事业部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        foreach (var actor in new[] { "pm", "division-manager", "lead", "supervisor", "collaborator" })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), actor, actor, "unused", UserRole.Engineer, true), default);
            await repository.SetOrganizationMembershipsAsync(actor, [division.Id], division.Id, default);
        }
        await repository.SetProjectExecutionUnitAsync(root.Id, division.Id, "admin", default);
        await repository.SetMainProjectStaffingAsync(root.Id, new("pm", ["collaborator"], ["lead"]), "admin", default);
        await repository.SetOrganizationUnitManagersAsync(division.Id, "division-manager", [], default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectDesignerAssign, PermissionCodes.ProjectStaffingManage], default);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        Assert.True((await repository.ListProjectsForUserAsync("pm", UserRole.Engineer, default)).Single(project => project.Id == root.Id).CanManageMainStaffing);
        var multipleOwners = await workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string> { ["Acceptance"] = "lead、collaborator、lead" }), "pm", UserRole.Engineer, default);
        Assert.Equal("lead、collaborator", multipleOwners.PhaseOwners["Acceptance"]);
        var hardwareOwners = await workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string> { ["ElectricalHardware"] = "lead" }), "pm", UserRole.Engineer, default);
        Assert.Equal("lead", hardwareOwners.PhaseOwners["ElectricalHardware"]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetMainProjectStaffingAsync(root.Id, new("collaborator", [], ["lead"]), "pm", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string> { ["Acceptance"] = "lead、missing-user" }), "pm", UserRole.Engineer, default));
        foreach (var actor in new[] { "pm", "division-manager" })
        {
            await workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string> { ["Acceptance"] = "pm" }), actor, UserRole.Engineer, default);
            await workflow.SetMainProjectStaffingAsync(root.Id, new("pm", ["collaborator"], ["lead"]), actor, UserRole.Engineer, default);
        }
        foreach (var actor in new[] { "lead", "supervisor", "collaborator" })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string>()), actor, UserRole.Engineer, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetMainProjectStaffingAsync(root.Id, new("pm", [], ["lead"]), actor, UserRole.Engineer, default));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetProjectPhaseOwnersAsync(root.Id, new(new Dictionary<string, string>()), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetMainProjectStaffingAsync(root.Id, new("pm", [], ["lead"]), "admin", UserRole.Administrator, default));
    }
}
