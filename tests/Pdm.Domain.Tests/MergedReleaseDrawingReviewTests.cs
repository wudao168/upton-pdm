using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class MergedReleaseDrawingReviewTests
{
    [Fact]
    public async Task NewReleaseDeduplicatesDrawingAndNeedsNoIndependentReview()
    {
        var f = await Fixture.Create();
        var review = await ReleaseDrawingReviewService.CaptureAsync(f.Repo,f.Package,default);
        var item = Assert.Single(review.Items);
        Assert.Equal(2,item.BomItemIds.Count);
        Assert.True(item.Required);
        Assert.Equal("Pending",item.Status);
        Assert.Empty(await f.Repo.ListDrawingReviewPackagesAsync(f.Package.ProjectId,default));
        var settings = (await f.Repo.GetSystemSettingsAsync(default)) with { PdfTitleBlockFill = new() {
            EnabledFields=["designer","drafter","designedOn","reviewer","reviewedOn","approver","approvedOn","quantity"] } };
        var snapshot = await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repo,f.Package with {DrawingReview=review},
            (await f.Repo.FindProjectAsync(f.Package.ProjectId,default))!,settings,default);
        Assert.True(snapshot.UseReleaseApproval);
        Assert.False(snapshot.QuantitiesFrozen);
        var values=Assert.Single(snapshot.Drawings).Value.Values;
        Assert.Equal("设计工程师",values["designer"]);
        Assert.Equal(values["designer"],values["drafter"]);
        Assert.False(values.ContainsKey("reviewer"));
        Assert.False(values.ContainsKey("approver"));
    }

    [Fact]
    public async Task MandatoryConfirmationAndIssuesAreEnforcedInsideApprovalRepository()
    {
        var f=await Fixture.Create(); await f.Start();
        await Assert.ThrowsAsync<PdmRuleException>(()=>f.Repo.DecideApprovalAsync(f.First.Id,"admin",ApprovalDecision.Approved,"同意",false,null,default));
        var updated=await f.Decide("Issue","孔距不一致");
        Assert.Single(updated.DrawingReview!.Items[0].Notes);
        await Assert.ThrowsAsync<PdmRuleException>(()=>f.Repo.DecideApprovalAsync(f.First.Id,"admin",ApprovalDecision.Approved,"同意",false,null,default));
        Assert.Equal(ReleasePackageState.ProcessReview, updated.State);
        await Assert.ThrowsAsync<PdmRuleException>(()=>f.Decide("Confirmed","旧版本不能直接确认"));
        await f.Decide("Pending","");
        updated=await f.Decide("Confirmed","复核正确");
        Assert.Equal("主设姓名",updated.DrawingReview!.Items[0].Notes.Last().ActorName);
        var next=await f.Repo.DecideApprovalAsync(f.First.Id,"admin",ApprovalDecision.Approved,"同意",false,null,default);
        Assert.Equal(ReleasePackageState.Approval,next.State);
        await Assert.ThrowsAsync<PdmRuleException>(()=>f.Repo.DecideApprovalAsync(f.Last.Id,"supervisor",ApprovalDecision.Approved,"未复核",false,null,default));
        await f.Decide("Confirmed","主管复核","supervisor");
        var finished=await f.Repo.DecideApprovalAsync(f.Last.Id,"supervisor",ApprovalDecision.Approved,"批准",false,null,default);
        Assert.Equal(ReleasePackageState.Publishing,finished.State);
        await Assert.ThrowsAsync<PdmConflictException>(()=>f.Decide("Pending","审批后不能改"));
    }

    [Fact]
    public async Task ReturnedDrawingIsReboundOnArchiveAndCanFinishOriginalFlow()
    {
        var f = await Fixture.Create(false);
        await f.Start();
        var returned = await f.Decide("Issue", "孔距需调整");
        var oldToken = returned.DrawingReview!.Items[0].Token;
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await f.Repo.CheckoutAsync(f.Drawing.Id, "engineer", session, "TEST", now.AddMinutes(15), default);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Decide("Pending", ""));
        var source = f.Version;
        var commit = new DocumentVersionCommit(new("merged/revised.slddrw", 12, new string('B', 64), now),
            "修改孔距", source.PropertySnapshot,
            new(Guid.NewGuid(), f.Package.ProjectId, f.Drawing.Id, now, "engineer", source.ReferenceSnapshot, "new-root"), [], []);
        var saved = await f.Repo.CheckInVersionAsync(f.Drawing.Id, "engineer", session, commit, default);
        var updated = (await f.Repo.FindReleasePackageAsync(f.Package.Id, default))!;
        Assert.Equal(returned.State, updated.State);
        Assert.Equal(returned.DrawingReview.RoundId, updated.DrawingReview!.RoundId);
        Assert.Equal(saved.Version!.Id, updated.DrawingReview.Items[0].VersionId);
        Assert.Equal(saved.Version.Id, updated.VersionBindings!.Single().VersionId);
        Assert.Equal("Pending", updated.DrawingReview.Items[0].Status);
        Assert.Equal("孔距需调整", Assert.Single(updated.DrawingReview.Items[0].Notes).Comment);
        Assert.Equal(DocumentLifecycleState.InReview, saved.Document.State);
        await Assert.ThrowsAsync<PdmConflictException>(() => new ReleaseDrawingReviewService(f.Repo, TimeProvider.System).DecideAsync(
            f.Package.Id, new(f.Drawing.Id, oldToken, "Confirmed", "") { ApprovalTaskId=f.First.Id, RoundId=updated.DrawingReview.RoundId },
            "admin", UserRole.Administrator, default));
        await f.Decide("Confirmed", "");
        var next = await f.Repo.DecideApprovalAsync(f.First.Id, "admin", ApprovalDecision.Approved, "同意", false, null, default);
        Assert.Equal(ReleasePackageState.Approval, next.State);
        await f.Decide("Confirmed", "", "supervisor");
        var finished = await f.Repo.DecideApprovalAsync(f.Last.Id, "supervisor", ApprovalDecision.Approved, "同意", false, null, default);
        Assert.Equal(ReleasePackageState.Publishing, finished.State);
    }

    [Fact]
    public async Task NativeDrawingCanBeConfirmedWithoutPdfAndReviewConversionIsRejected()
    {
        var f=await Fixture.Create(false); await f.Start();
        var updated=await f.Decide("Confirmed","原图核对通过");
        Assert.Null(updated.DrawingReview!.Items[0].Preview);
        Assert.Equal("Confirmed",updated.DrawingReview.Items[0].Status);
        var error=await Assert.ThrowsAsync<PdmRuleException>(()=>new ReleaseDrawingReviewService(f.Repo,TimeProvider.System)
            .GeneratePreviewAsync(f.Package.Id,f.Drawing.Id,"admin",UserRole.Administrator,default));
        Assert.Contains("最终发布阶段",error.Message);
        Assert.Equal(DocumentVersionStatus.Work,(await f.Repo.FindDocumentVersionAsync(f.Drawing.Id,f.Version.Id,default))!.Status);
    }

    [Fact]
    public async Task ChangedBomInvalidatesConfirmationButRetainsHistoryAndRejectsStaleToken()
    {
        var f=await Fixture.Create(); await f.Start();
        var confirmed=await f.Decide("Confirmed","原稿复核");
        var same=await ReleaseDrawingReviewService.CaptureAsync(f.Repo,confirmed,default);
        Assert.Equal("Confirmed",same.Items[0].Status);
        var changed=await ReleaseDrawingReviewService.CaptureAsync(f.Repo,confirmed with {
            NonStandardBomSnapshot=confirmed.NonStandardBomSnapshot.Select(i=>i with {Quantity=i.Quantity+1}).ToArray() },default);
        Assert.Equal("Pending",changed.Items[0].Status);
        Assert.Single(changed.Items[0].Notes);
        Assert.NotEqual(same.Items[0].Token,changed.Items[0].Token);
        var removed=await ReleaseDrawingReviewService.CaptureAsync(f.Repo,confirmed with { NonStandardBomSnapshot=[] },default);
        Assert.Empty(removed.Items);
        Assert.Single(Assert.Single(removed.ArchivedItems).Notes);
        var restored=await ReleaseDrawingReviewService.CaptureAsync(f.Repo,confirmed with { DrawingReview=removed },default);
        Assert.Single(restored.Items[0].Notes);
        Assert.Empty(restored.ArchivedItems);
        await Assert.ThrowsAsync<PdmConflictException>(()=>new ReleaseDrawingReviewService(f.Repo,TimeProvider.System).DecideAsync(f.Package.Id,
            new(f.Drawing.Id,"stale","Confirmed",""),"admin",UserRole.Administrator,default));
        await Assert.ThrowsAsync<PdmConflictException>(()=>f.Repo.SaveReleaseDrawingReviewAsync(f.Package.Id,changed,0,f.First.Id,default));
    }

    [Fact]
    public async Task AuthorizationRevocationNonCurrentAndCrossProjectAreRejected()
    {
        var f=await Fixture.Create(); await f.Start();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Decide("Confirmed","","supervisor"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Decide("Confirmed","","engineer"));
        await PermissionTestUsers.Revoke(f.Repo,"Administrator",PermissionCodes.ApprovalDecide);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Decide("Confirmed",""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ReleaseDrawingReviewService(f.Repo,TimeProvider.System)
            .ReadAsync(f.Package.Id,f.Drawing.Id,"nonexistent",UserRole.Administrator,default));
        await Assert.ThrowsAsync<PdmConflictException>(()=>ReleaseDrawingReviewService.CaptureAsync(f.Repo,f.Package with {ProjectId=Guid.NewGuid()},default));
        await PermissionTestUsers.Grant(f.Repo,"Administrator",PermissionCodes.ApprovalDecide);
        await f.Decide("Confirmed","");
    }

    [Fact]
    public async Task ActualReleaseSignersFreezeAfterApprovalAndIgnoreDrawingReviewAndAssignedNames()
    {
        var f=await Fixture.Create(); await f.Start();
        var package=(await f.Repo.FindReleasePackageAsync(f.Package.Id,default))!;
        var settings=(await f.Repo.GetSystemSettingsAsync(default)) with { PdfTitleBlockFill=new() {
            EnabledFields=["reviewer","reviewedOn","approver","approvedOn"] } };
        var snapshot=await PdfTitleBlockSnapshotBuilder.BuildAsync(f.Repo,package,(await f.Repo.FindProjectAsync(package.ProjectId,default))!,settings,default);
        await Assert.ThrowsAsync<PdmConflictException>(()=>PdfTitleBlockSnapshotBuilder.FreezeForPublicationAsync(f.Repo,package with {PdfTitleBlockSnapshot=snapshot},[],default));
        await f.Decide("Confirmed","");
        await f.Repo.TransferApprovalAsync(f.First.Id,"admin","supervisor",default);
        await f.Decide("Confirmed","转交后复核","supervisor");
        await f.Repo.DecideApprovalAsync(f.First.Id,"supervisor",ApprovalDecision.Approved,"实际转交审核",false,null,default);
        var current=(await f.Repo.FindReleasePackageAsync(f.Package.Id,default))!;
        await new ReleaseDrawingReviewService(f.Repo,TimeProvider.System).DecideAsync(f.Package.Id,
            new(f.Drawing.Id,current.DrawingReview!.Items[0].Token,"Confirmed","代批复核") { ApprovalTaskId=f.Last.Id, RoundId=current.DrawingReview.RoundId, EmergencyReason="主管出差" },"admin",UserRole.Administrator,default);
        package=await f.Repo.DecideApprovalAsync(f.Last.Id,"admin",ApprovalDecision.Approved,"紧急代批",true,"主管出差",default);
        var frozen=await PdfTitleBlockSnapshotBuilder.FreezeForPublicationAsync(f.Repo,package with {PdfTitleBlockSnapshot=snapshot},[],default);
        Assert.True(frozen.QuantitiesFrozen);
        Assert.Equal("主管姓名",frozen.Drawings[f.Drawing.Id].Values["reviewer"]);
        Assert.Equal("主设姓名",frozen.Drawings[f.Drawing.Id].Values["approver"]);
        Assert.NotEmpty(frozen.Drawings[f.Drawing.Id].Values["approvedOn"]);
        Assert.Same(frozen,await PdfTitleBlockSnapshotBuilder.FreezeForPublicationAsync(f.Repo,package with {PdfTitleBlockSnapshot=frozen,ApprovalTasks=[]},[],default));
    }

    [Fact]
    public async Task LegacyInFlightPackageKeepsItsOriginalGate()
    {
        var f=await Fixture.Create();
        await f.Repo.CreateReleasePackageAsync(f.Package with {State=ReleasePackageState.ProcessReview,DrawingReview=null},default);
        var next=await f.Repo.DecideApprovalAsync(f.First.Id,"admin",ApprovalDecision.Approved,"原流程",false,null,default);
        Assert.Null(next.DrawingReview);
    }

    private sealed class ReviewConverter : IServerPreviewConverter
    {
        public bool ReviewCalled;
        public Task<IReadOnlyDictionary<Guid,DocumentPreviewArtifact>> GenerateReviewAsync(ReleasePackage p,Project project,IReadOnlyList<ReleasePreviewSource> sources,string directory,CancellationToken ct)
        { ReviewCalled=true; return Task.FromResult<IReadOnlyDictionary<Guid,DocumentPreviewArtifact>>(sources.ToDictionary(s=>s.DocumentId,s=>new DocumentPreviewArtifact(DocumentPreviewFormat.Pdf,"review.pdf",10,"preview",s.Sha256))); }
        public Task<IReadOnlyDictionary<Guid,DocumentPreviewArtifact>> GenerateAsync(ReleasePackage p,Project project,IReadOnlyList<ReleasePreviewSource> sources,string directory,CancellationToken ct) => throw new Exception("审核不能调用正式发布转换");
    }

    private sealed class Fixture
    {
        public InMemoryPdmRepository Repo=null!;
        public ReleasePackage Package=null!;
        public PdmDocument Drawing=null!;
        public DocumentVersion Version=null!;
        public ApprovalTask First=>Package.ApprovalTasks[0];
        public ApprovalTask Last=>Package.ApprovalTasks[1];
        public async Task Start()
        {
            var review=await ReleaseDrawingReviewService.CaptureAsync(Repo,Package,default);
            await Repo.CreateReleasePackageAsync(Package with {State=ReleasePackageState.ProcessReview,DrawingReview=review},default);
        }
        public async Task<ReleasePackage> Decide(string status,string comment,string actor="admin")
        {
            var p=(await Repo.FindReleasePackageAsync(Package.Id,default))!;
            return await new ReleaseDrawingReviewService(Repo,TimeProvider.System).DecideAsync(Package.Id,
                new(Drawing.Id,p.DrawingReview!.Items[0].Token,status,comment) { RoundId=p.DrawingReview.RoundId, ApprovalTaskId=p.ApprovalTasks.OrderBy(t=>t.StepOrder).FirstOrDefault(t=>t.Decision is null)?.Id },actor,UserRole.Administrator,default);
        }
        public static async Task<Fixture> Create(bool preview=true)
        {
            var repo=BusinessTestRepository.Create(TimeProvider.System);
            await PermissionTestUsers.Ensure(repo,("supervisor",UserRole.Administrator));
            await repo.UpdateUserAsync("admin","主设姓名",UserRole.Administrator,"Administrator",["Administrator"],true,default);
            await repo.UpdateUserAsync("engineer","设计工程师",UserRole.Engineer,"Engineer",["Engineer"],true,default);
            await repo.UpdateUserAsync("supervisor","主管姓名",UserRole.Administrator,"Administrator",["Administrator"],true,default);
            var projectId=Guid.Parse("11111111-1111-1111-1111-111111111111");
            var model=await repo.RegisterDocumentAsync(new(projectId,"MERGED","审核零件","MERGED.SLDPRT",DocumentKind.Part),"engineer",default);
            var drawing=await repo.RegisterDocumentAsync(new(projectId,"MERGED","审核图纸","MERGED.SLDDRW",DocumentKind.Drawing,RelatedModelDocumentId:model.Id),"engineer",default);
            var node=new DocumentReferenceNode(Guid.NewGuid(),model.Id,"MERGED",model.FileName,model.Name,model.Kind,"默认",1,ReferenceNodeStatus.Normal,model.Revision,null,[]);
            var root=node with {DocumentId=drawing.Id,Kind=DocumentKind.Drawing,FileName=drawing.FileName,Children=[node]};
            var session=Guid.NewGuid(); var now=DateTimeOffset.UtcNow;
            await repo.CheckoutAsync(drawing.Id,"engineer",session,"TEST",now.AddMinutes(15),null,default);
            var result=await repo.CheckInVersionAsync(drawing.Id,"engineer",session,new(new("merged/drawing.slddrw",10,new string('A',64),now),"存档",
                new Dictionary<string,string?>(),new(Guid.NewGuid(),projectId,drawing.Id,now,"engineer",root,"root"),[],[]),default);
            var version=result.Version!;
            if(preview) await repo.AttachWorkVersionPreviewAsync(drawing.Id,version.Id,new(DocumentPreviewFormat.Pdf,"preview.pdf",10,"pdf",version.Sha256),default);
            var id=Guid.NewGuid();
            var bom=new BomItem(Guid.NewGuid(),projectId,BomKind.NonStandard,1,"MERGED","审核零件",2,"件","钢","M1","W1",true){SourceDocumentId=model.Id};
            var package=new ReleasePackage(id,projectId,"RP-MERGED",ReleasePackageState.Draft,null,"","",[
                new(Guid.NewGuid(),id,ApprovalStage.MainDesigner,"admin",null,null,null,null){StepOrder=1},
                new(Guid.NewGuid(),id,ApprovalStage.MechanicalSupervisor,"supervisor",null,null,null,null){StepOrder=2}],now,null,null)
            { Scope=ReleaseScope.NonStandardWithDrawing,NonStandardBomSnapshot=[bom,bom with {Id=Guid.NewGuid(),Sequence=2}],
                VersionBindings=[new(drawing.Id,version.Id,version.Revision.Display,version.Sha256,true,drawing.FileName,true,DrawingModelDocumentId:model.Id)] };
            return new(){Repo=repo,Package=package,Drawing=drawing,Version=version};
        }
    }
}
