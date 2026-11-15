using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    private static async Task ConfirmMergedDrawingsAsync(InMemoryPdmRepository repository, ReleasePackage package, string? actor = null)
    {
        package=(await repository.FindReleasePackageAsync(package.Id,default))!;
        if (package.DrawingReview is null) return;
        var current = package.ApprovalTasks.OrderBy(t=>t.StepOrder).First(t=>t.Decision is null);
        foreach (var item in package.DrawingReview.Items.Where(i=>i.Required && ReleaseDrawingReviewService.StatusFor(package.DrawingReview,i,current.Id,actor ?? current.Assignee) != "Confirmed"))
        {
            await repository.AttachWorkVersionPreviewAsync(item.DocumentId,item.VersionId,
                new(DocumentPreviewFormat.Pdf,"review.pdf",10,"preview",item.Sha256),default);
            await new ReleaseDrawingReviewService(repository,TimeProvider.System).DecideAsync(package.Id,
                new(item.DocumentId,item.Token,"Confirmed","测试人员核对冻结BOM与图纸") { ApprovalTaskId=current.Id, RoundId=package.DrawingReview.RoundId },actor ?? current.Assignee,UserRole.Approver,default);
        }
    }
    [Theory]
    [InlineData(ReleaseScope.NonStandardWithDrawing)]
    [InlineData(ReleaseScope.NonStandardLongLead)]
    public async Task MergedRelease_SubmitsUnreviewedDrawingsAndCompletesOneApprovalFlow(ReleaseScope scope)
    {
        var repository=BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository,workflow,independentReview:false);
        Assert.Empty(await repository.ListDrawingReviewPackagesAsync(ProjectId,default));
        var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","","未指定",null,
            scope,CurrentIds(repository,BomKind.NonStandard),"admin",UserRole.Administrator,default);
        await repository.UpdateDraftReleasePackageAsync(package with { EmergencyRoleCodes = ["Administrator"] }, default);
        package=await workflow.SubmitReleasePackageAsync(package.Id,"admin",UserRole.Administrator,default);
        var item=Assert.Single(package.DrawingReview!.Items);
        var current=package.ApprovalTasks.OrderBy(t=>t.StepOrder).First(t=>t.Decision is null);
        await Assert.ThrowsAsync<PdmRuleException>(()=>workflow.DecideAsync(current.Id,current.Assignee,UserRole.Administrator,
            ApprovalDecision.Approved,"还未查看图纸",default,false));
        await repository.AttachWorkVersionPreviewAsync(item.DocumentId,item.VersionId,
            new(DocumentPreviewFormat.Pdf,"review.pdf",10,"preview",item.Sha256),default);
        var reviewService=new ReleaseDrawingReviewService(repository,TimeProvider.System);
        foreach(var task in package.ApprovalTasks.Where(t=>t.Decision is null).OrderBy(t=>t.StepOrder))
        {
            var before=package;
            var emergency = task.Assignee != "admin" && scope == ReleaseScope.NonStandardLongLead;
            package=await reviewService.DecideAndAdvanceAsync(package.Id,
                new(item.DocumentId,item.Token,"Confirmed","") { ApprovalTaskId=task.Id, RoundId=package.DrawingReview!.RoundId },
                emergency ? "admin" : task.Assignee, UserRole.Administrator, workflow, default, emergencySubstitute: emergency);
            var completed = package.ApprovalTasks.Single(t=>t.Id==task.Id);
            Assert.Equal(ApprovalDecision.Approved, completed.Decision);
            Assert.Equal(emergency, completed.IsEmergencySubstitute);
            Assert.Null(completed.EmergencyReason);
            await Assert.ThrowsAsync<PdmConflictException>(()=>reviewService.DecideAndAdvanceAsync(before.Id,
                new(item.DocumentId,item.Token,"Confirmed","重复提交") { ApprovalTaskId=task.Id },task.Assignee,UserRole.Administrator,workflow,default));
        }
        Assert.Equal(ReleasePackageState.Publishing,package.State);
        Assert.Empty(await repository.ListDrawingReviewPackagesAsync(ProjectId,default));
    }

    [Fact]
    public async Task DocumentNodeReview_GroupSignaturesStaySeparateAndResubmissionRejectsOldRound()
    {
        var repository=BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository,workflow,independentReview:false);
        var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","","未指定",null,
            ReleaseScope.NonStandardWithDrawing,CurrentIds(repository,BomKind.NonStandard),"admin",UserRole.Administrator,default);
        var members=new[]{"mechanical-supervisor","standardization-supervisor"};
        var group=new ApprovalGroup(members,true,[]) { Policy=new ApprovalNodePolicy(RequireOpinion:true,EditBusinessNote:true) };
        var first=package.ApprovalTasks[1] with { Participants=group, Assignee=members[0] };
        await repository.UpdateDraftReleasePackageAsync(package with { ApprovalTasks=[package.ApprovalTasks[0],first,..package.ApprovalTasks.Skip(2)] },default);
        package=await workflow.SubmitReleasePackageAsync(package.Id,"admin",UserRole.Administrator,default);
        var item=Assert.Single(package.DrawingReview!.Items);
        var round=package.DrawingReview.RoundId;
        await repository.AttachWorkVersionPreviewAsync(item.DocumentId,item.VersionId,new(DocumentPreviewFormat.Pdf,"review.pdf",10,"preview",item.Sha256),default);
        var service=new ReleaseDrawingReviewService(repository,TimeProvider.System);
        ReleaseDrawingReviewDecision Input(string comment) => new(item.DocumentId,item.Token,"Confirmed",comment) { ApprovalTaskId=first.Id,RoundId=round,BusinessNote="会签核对" };
        package=await service.DecideAndAdvanceAsync(package.Id,Input(""),members[0],UserRole.Approver,workflow,default);
        Assert.Null(package.ApprovalTasks.Single(t=>t.Id==first.Id).Decision);
        Assert.Equal("同意", Assert.Single(package.ApprovalTasks.Single(t=>t.Id==first.Id).Participants!.Signatures).Comment);
        Assert.Equal("", Assert.Single(package.DrawingReview!.Items[0].Notes).Comment);
        await Assert.ThrowsAsync<PdmRuleException>(()=>workflow.DecideAsync(first.Id,members[1],UserRole.Approver,ApprovalDecision.Approved,"未审图直接通过",default,false));
        package=await service.DecideAndAdvanceAsync(package.Id,Input("主设二复核"),members[1],UserRole.Approver,workflow,default);
        Assert.Equal(ApprovalDecision.Approved,package.ApprovalTasks.Single(t=>t.Id==first.Id).Decision);
        Assert.Equal(2,package.ApprovalTasks.Single(t=>t.Id==first.Id).Participants!.Signatures.Count);
        var next=package.ApprovalTasks.OrderBy(t=>t.StepOrder).First(t=>t.Decision is null);
        await Assert.ThrowsAsync<PdmRuleException>(()=>service.CompleteNodeAsync(package.Id,
            new(next.Id, ApprovalDecision.Rejected, "") { RoundId=round }, next.Assignee, UserRole.Approver, workflow, default));
        package=await service.CompleteNodeAsync(package.Id,new(next.Id,ApprovalDecision.Rejected,"修改后重审") {RoundId=round},next.Assignee,UserRole.Approver,workflow,default);
        package=await workflow.SubmitReleasePackageAsync(package.Id,"admin",UserRole.Administrator,default);
        Assert.NotEqual(round,package.DrawingReview!.RoundId);
        await Assert.ThrowsAsync<PdmConflictException>(()=>service.DecideAndAdvanceAsync(package.Id,Input("旧页面重复确认"),members[0],UserRole.Approver,workflow,default));
        await Assert.ThrowsAsync<PdmRuleException>(()=>workflow.DecideAsync(first.Id,members[0],UserRole.Approver,ApprovalDecision.Approved,"旧记录不能代替本轮",default,false));
        Assert.Equal(2,package.DrawingReview.Items[0].Notes.Count);
    }
}
