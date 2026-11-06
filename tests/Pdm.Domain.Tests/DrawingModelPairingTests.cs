using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class DrawingModelPairingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssemblyDrawingPublishesOnlyItsAssemblyWhileAnIndependentPartDrawingPublishesItsPart(bool independentPartDrawing)
    {
        var (repo, project, assembly, part, otherPart, drawing, partDrawing, rows) = await Prepare(independentPartDrawing);
        var pairs = await DrawingModelPairing.ReadAsync(repo, project.Id, default);
        Assert.Equal(assembly.Id, pairs.Single(pair => pair.DrawingDocumentId == drawing.Id).ModelDocumentId);
        Assert.Contains(otherPart.Id, DrawingModelPairing.ReferenceOnlyModelIds(pairs));
        Assert.Equal(!independentPartDrawing, DrawingModelPairing.ReferenceOnlyModelIds(pairs).Contains(part.Id));
        var workflow = new PdmWorkflowService(repo, null!, null!, TimeProvider.System);
        var candidates = await workflow.ListDrawingReviewCandidatesAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(drawing.Id, candidates.Single(item => item.ModelDocumentId == assembly.Id).DrawingDocumentId);
        Assert.True(candidates.Single(item => item.ModelDocumentId == otherPart.Id).IsReferenceOnly);
        if (partDrawing is not null) Assert.Equal(partDrawing.Id, candidates.Single(item => item.ModelDocumentId == part.Id).DrawingDocumentId);

        var select = typeof(PdmWorkflowService).GetMethod("SelectScopedReleaseItemsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var selected = await (Task<BomItem[]>)select.Invoke(workflow,
            [project.Id, ReleaseScope.NonStandardWithDrawing, BomKind.NonStandard, rows, rows.Select(item => item.Id).ToArray(), null, 1, CancellationToken.None])!;
        Assert.Equal(independentPartDrawing ? 2 : 1, selected.Length);
        var snapshot = (await repo.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "PAIRING-RELEASE", ReleasePackageState.Draft,
            snapshot.SnapshotId, "M", "E", [], DateTimeOffset.UtcNow, null, null) {
            Scope=ReleaseScope.NonStandardWithDrawing,RootDocumentId=assembly.Id,
            SelectedBomItemIds=selected.Select(item=>item.Id).ToArray(),NonStandardBomSnapshot=selected };
        var capture = typeof(PdmWorkflowService).GetMethod("CaptureReleaseVersionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        package = await (Task<ReleasePackage>)capture.Invoke(workflow, [package,CancellationToken.None])!;
        Assert.NotNull(package.VersionBindings);
        Assert.True(package.VersionBindings.Single(item => item.DocumentId == assembly.Id).Publish);
        Assert.Equal(independentPartDrawing, package.VersionBindings.Single(item => item.DocumentId == part.Id).Publish);
        var reference = package.VersionBindings.Single(item => item.DocumentId == otherPart.Id);
        Assert.False(reference.Publish);
        Assert.False(reference.RequiresPromotion);
        Assert.Equal(assembly.Id, package.VersionBindings.Single(item => item.DocumentId == drawing.Id).DrawingModelDocumentId);
        var source = (await repo.ListDocumentVersionsAsync(drawing.Id, default)).First();
        Assert.Equal([assembly.Id], ReleaseBoundSourceReader.ModelDocumentIds(package,drawing.Id,source.ReferenceSnapshot));
        package = await repo.CreateReleasePackageAsync(package with {State=ReleasePackageState.Publishing},default);
        var previews = await repo.ListReleasePreviewSourcesAsync(package.Id,default);
        Assert.Equal(independentPartDrawing ? 3 : 1,previews.Count);
        Assert.DoesNotContain(previews,item=>item.DocumentId==otherPart.Id);
        Assert.DoesNotContain(previews,item=>item.DocumentId==assembly.Id);
        Assert.Contains(await repo.ListReleaseReferenceSourcesAsync(package.Id,default),item=>item.DocumentId==otherPart.Id);
        Assert.Single(await repo.ListDocumentVersionsAsync(otherPart.Id,default));
    }

    [Fact]
    public async Task IndependentModelsInOneDrawingRequireConfirmationInsteadOfPublishingEveryReference()
    {
        var (repo, project, _, _, _, drawing, _, rows)=await Prepare(false,false);
        var pair=Assert.Single(await DrawingModelPairing.ReadAsync(repo,project.Id,default));
        Assert.Null(pair.ModelDocumentId);
        Assert.Contains("无法唯一确定发布对象",pair.Issue);
        Assert.Contains(drawing.FileName,pair.Issue);
        var workflow=new PdmWorkflowService(repo,null!,null!,TimeProvider.System);
        var select=typeof(PdmWorkflowService).GetMethod("SelectScopedReleaseItemsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await Assert.ThrowsAsync<PdmRuleException>(async ()=>await (Task<BomItem[]>)select.Invoke(workflow,
            [project.Id,ReleaseScope.NonStandardWithDrawing,BomKind.NonStandard,rows,rows.Select(item=>item.Id).ToArray(),null,1,CancellationToken.None])!);
    }

    private static async Task<(InMemoryPdmRepository,Project,PdmDocument,PdmDocument,PdmDocument,PdmDocument,PdmDocument?,BomItem[])> Prepare(bool independentPartDrawing,bool nested=true)
    {
        var repo=new InMemoryPdmRepository(TimeProvider.System);
        var project=await repo.CreateProjectAsync(new("PAIR-"+Guid.NewGuid().ToString("N"),"图模一一对应","admin","vault","release"),"admin",default);
        var session=Guid.NewGuid();
        var part=await ArchiveBatchTests.Register(repo,project,"ASM-01",DocumentKind.Part,session);
        var other=await ArchiveBatchTests.Register(repo,project,"ASM-02",DocumentKind.Part,session);
        var assembly=await ArchiveBatchTests.Register(repo,project,"ASM",DocumentKind.Assembly,session);
        async Task<PdmDocument> Drawing(string name) {
            var document=await repo.RegisterDocumentAsync(new(project.Id,name,name,name+".SLDDRW",DocumentKind.Drawing),"admin",default);
            return await repo.CheckoutAsync(document.Id,"admin",session,"QA",DateTimeOffset.UtcNow.AddMinutes(15),null,default);
        }
        var drawing=await Drawing("ASM");
        var partDrawing=independentPartDrawing?await Drawing("ASM-01"):null;
        var partNode=ArchiveBatchTests.Node(part);var otherNode=ArchiveBatchTests.Node(other);
        var assemblyNode=ArchiveBatchTests.Node(assembly) with {Children=nested?[partNode,otherNode]:[]};
        var files=new List<ArchiveBatchFile> {
            new(part.Id,ArchiveBatchTests.Commit(project,part,partNode,'A')),
            new(other.Id,ArchiveBatchTests.Commit(project,other,otherNode,'B')),
            new(assembly.Id,ArchiveBatchTests.Commit(project,assembly,assemblyNode,'C',true)),
            new(drawing.Id,ArchiveBatchTests.Commit(project,drawing,ArchiveBatchTests.Node(drawing) with {Children=[assemblyNode with {Children=[]},partNode,otherNode]},'D')) };
        if(partDrawing is not null)files.Add(new(partDrawing.Id,ArchiveBatchTests.Commit(project,partDrawing,ArchiveBatchTests.Node(partDrawing) with {Children=[partNode]},'E')));
        await repo.CheckInBatchVersionsAsync(Guid.NewGuid(),"pairing-test","admin",session,files,default);
        var rows=new[]{assembly,part,other}.Select((model,index)=>new BomItem(Guid.NewGuid(),project.Id,BomKind.NonStandard,index+1,model.DrawingNumber,model.Name,1,"件","钢",model.Name,"W1",true){SourceDocumentId=model.Id,SourceConfiguration="Default",Source="Auto"}).ToArray();
        await repo.ReplaceBomAsync(project.Id,BomKind.NonStandard,rows,default);
        return(repo,project,assembly,part,other,drawing,partDrawing,rows);
    }
}
