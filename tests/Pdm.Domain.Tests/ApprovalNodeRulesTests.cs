using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ApprovalNodeRulesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationPlan_CandidatesReceivePendingTaskAndOnlyRequiredVotesAdvance(bool all)
    {
        var repo = new InMemoryValidationPlanRepository();
        var now = DateTimeOffset.UtcNow;
        var plan = new ProjectValidationPlan(Guid.NewGuid(), Guid.NewGuid(), 1, ProjectValidationPlanState.Draft, "author",
            DateOnly.FromDateTime(now.UtcDateTime), [], [], [], "author", now, "author", now, 1);
        var draft = await repo.SavePlanAsync(plan, null, default);
        var node = new ValidationPlanApprovalTask(Guid.NewGuid(), draft.Id, 1, ApprovalStage.MainDesigner, "审核", "first", null, null, null, now, null)
            { Participants = new ApprovalGroup(["first","second"], all, []) { Policy = new ApprovalNodePolicy(EditBusinessNote: true) } };
        await repo.SubmitAsync(draft.Id, draft.RowVersion, "validation-plan", 1, [node], "author", now, default);
        Assert.Single(await repo.ListPendingApprovalTasksAsync("second", false, default));
        var result = await repo.DecideAsync(node.Id, "second", ApprovalDecision.Approved, "通过", now, default, businessNote: "已核对依据");
        Assert.Empty(await repo.ListPendingApprovalTasksAsync("second", false, default));
        Assert.Equal("已核对依据", Assert.Single(result.ApprovalTasks[0].Participants!.NoteHistory).Text);
        if (all)
        {
            Assert.Equal(ProjectValidationPlanState.PendingApproval, result.State);
            result = await repo.DecideAsync(node.Id, "first", ApprovalDecision.Approved, "同意", now, default);
        }
        Assert.Equal(ProjectValidationPlanState.Effective, result.State);
    }
    [Fact]
    public void RouteMatchesOneBranchAndRequiresAnApprovalAfterSelfCheck()
    {
        var fixture = ReleaseApprovalSettings.Default.Mechanical;
        var user = new UserAccount(Guid.NewGuid(), "engineer", "工程师", "", UserRole.Engineer, true);
        var route = fixture with { Steps = [fixture.Steps[0], fixture.Steps[1] with { ConditionRole = "Engineer" },
            fixture.Steps[2] with { ConditionRole = "Engineer", UnlessRole = true }] };
        Assert.Equal([fixture.Steps[0].Stage, fixture.Steps[1].Stage], ApprovalNodeRules.ResolveRoute(route, user).Steps.Select(step => step.Stage));
        var empty = route with { Steps = [route.Steps[0], route.Steps[2]] };
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.ResolveRoute(empty, user));
    }

    [Fact]
    public void ReadOnlyNodeRejectsNoteAndOnlyOutstandingMembersCanSign()
    {
        var now = DateTimeOffset.UtcNow;
        var group = new ApprovalGroup(["first","second"], true, []);
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.Sign(group, "first", ApprovalDecision.Approved, "同意", now, false, out _, "越权修改"));
        var signed = ApprovalNodeRules.Sign(group, "first", ApprovalDecision.Approved, "同意", now, false, out var waiting);
        Assert.True(waiting);
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.Sign(signed, "FIRST", ApprovalDecision.Approved, "重复", now, false, out _));
        Assert.Throws<PdmRuleException>(() => ApprovalNodeRules.Sign(signed, "admin", ApprovalDecision.Approved, "代签", now, true, out _));
        ApprovalNodeRules.Sign(signed, "second", ApprovalDecision.Approved, "同意", now, false, out waiting);
        Assert.False(waiting);
    }
}
