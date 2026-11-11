using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed partial class U9ProcurementServiceTests
{
    [Theory]
    [InlineData(BomKind.Standard, false)]
    [InlineData(BomKind.Standard, true)]
    [InlineData(BomKind.Electrical, false)]
    [InlineData(BomKind.Electrical, true)]
    public async Task PublishedRemovalRetainsCanceledDemandAndOriginalProcurementHistory(BomKind kind, bool removeAll)
    {
        var clock=TimeProvider.System;var repository=new InMemoryPdmRepository(clock);
        var procurement=new InMemoryU9ProcurementRepository();var project=Assert.Single(await repository.ListProjectsAsync(default));var now=clock.GetUtcNow();
        var file=new BomFileReference(Guid.NewGuid(),Guid.NewGuid(),"Drawing2D","已发布采购图.pdf",1,10,new string('A',64),"标准化",now,"标准物料","图纸");
        var removed=new BomItem(Guid.NewGuid(),project.Id,kind,1,"REMOVED","取消购买物料",2,"001",null,"MODEL","W1",true){Files=[file]};
        var kept=removed with {Id=Guid.NewGuid(),DrawingNumber="KEPT",Name="继续购买物料",Quantity=5,Sequence=2};
        async Task Release(string number,IReadOnlyList<BomItem> rows,ReleasePackageState state,int minutes)
        {
            await repository.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,number,state,null,"","",[],now.AddMinutes(minutes-1),state==ReleasePackageState.Published?now.AddMinutes(minutes):null,null)
            {
                Scope=kind==BomKind.Standard?ReleaseScope.StandardFormal:ReleaseScope.ElectricalFormal,
                StandardBomSnapshot=kind==BomKind.Standard?rows:[],ElectricalBomSnapshot=kind==BomKind.Electrical?rows:[],
                ConfirmedRemovedBomItemIds=number=="RP-CANCEL"?[removed.Id]:[]
            },default);
        }
        await Release("RP-ORIGINAL",[removed,kept],ReleasePackageState.Published,-6);
        var current=removeAll?Array.Empty<BomItem>():[kept];
        await repository.ReplaceBomAsync(project.Id,kind,current,default);
        await Release("RP-DRAFT",current,ReleasePackageState.Draft,-4);
        await procurement.ReplaceSnapshotAsync(Guid.NewGuid(),[
            new("001","PR","1",null,"PR-ORIGINAL",1,2,false,now,removed.DrawingNumber,removed.Name,removed.Specification,"UPTON",project.Code,null,null,2,2,0,0,null,now,null),
            new("001","PO","2","1","PO-ORIGINAL",1,2,false,now,removed.DrawingNumber,removed.Name,removed.Specification,"UPTON",project.Code,null,null,0,0,2,1,"原采购备注",now.AddDays(1),null)
        ],now,default);
        var service=new U9ProcurementService(procurement,new InMemoryMaterialRepository(clock),repository,new InMemoryProjectPlanningRepository(),null!,null!,null!,clock);
        var draftRow=Assert.Single((await service.ListProjectAsync(project.Id,"admin",UserRole.Administrator,default)).Items,row=>row.MaterialCode=="REMOVED");
        Assert.Equal(2,draftRow.Quantity);Assert.False(draftRow.IsDemandCanceled);
        await Release("RP-CANCEL",current,ReleasePackageState.Published,-2);
        var result=await service.ListProjectAsync(project.Id,"admin",UserRole.Administrator,default);
        var canceled=Assert.Single(result.Items,row=>row.MaterialCode=="REMOVED");
        Assert.True(canceled.IsDemandCanceled);Assert.Equal(0,canceled.Quantity);Assert.Equal("已取消",canceled.PurchaseRequisitionStatus);
        Assert.Equal("RP-CANCEL",canceled.ReleasePackageNumber);Assert.Equal(2,canceled.RequestedQuantity);Assert.Equal(2,canceled.PurchaseQuantity);Assert.Equal(1,canceled.ArrivedQuantity);
        Assert.False(canceled.HasDeliveryDelay);Assert.Contains("PR-ORIGINAL",canceled.PurchaseRequisitionNumbers);Assert.Contains("PO-ORIGINAL",canceled.PurchaseOrderNumbers);
        var originalPr=Assert.Single(canceled.Details,row=>row.Kind=="请购");Assert.False(originalPr.IsCanceled);Assert.Equal("已核准",originalPr.LineStatus);
        Assert.Equal(file,Assert.Single(canceled.Files));Assert.Equal(file,Assert.Single(await service.ListPublishedFilesAsync(project.Id,"REMOVED","admin",UserRole.Administrator,default)));
        var keptRow=Assert.Single(result.Items,row=>row.MaterialCode=="KEPT");Assert.Equal(removeAll?0:5,keptRow.Quantity);Assert.Equal(removeAll,keptRow.IsDemandCanceled);
        var original=(await repository.ListReleasePackagesAsync(project.Id,default)).Single(package=>package.Number=="RP-ORIGINAL");
        Assert.Equal(2,Assert.Single(original.StandardBomSnapshot.Concat(original.ElectricalBomSnapshot),row=>row.Id==removed.Id).Quantity);
        var restored=removed with {Id=Guid.NewGuid(),Quantity=3};
        await Release("RP-REBUY",[restored,..current],ReleasePackageState.Published,0);
        var active=(await service.ListProjectAsync(project.Id,"admin",UserRole.Administrator,default)).Items.Where(row=>row.MaterialCode=="REMOVED").ToArray();
        Assert.All(active,row=>Assert.False(row.IsDemandCanceled));
        Assert.Equal(3,active.Sum(row=>row.Quantity));
        Assert.Equal(2,active.Sum(row=>row.RequestedQuantity));
        Assert.Equal(2,active.Sum(row=>row.PurchaseQuantity));
        Assert.Equal(1,active.Sum(row=>row.ArrivedQuantity));
        Assert.Equal(2,Assert.Single(active,row=>row.PurchaseRequisitionStatus=="已核准").Quantity);
        Assert.Equal(1,Assert.Single(active,row=>row.PurchaseRequisitionStatus=="未请购").Quantity);
    }

    [Fact]
    public async Task ReplacingMaterialCodeOnTheSameBomRowRetainsTheOldMaterialAsCanceled()
    {
        var clock=TimeProvider.System;var repository=new InMemoryPdmRepository(clock);var project=Assert.Single(await repository.ListProjectsAsync(default));var now=clock.GetUtcNow();
        var old=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,"OLD","原物料",2,"001",null,null,"W1",true);
        foreach(var (number,item,at) in new[]{("RP-OLD",old,now.AddMinutes(-2)),("RP-NEW",old with {DrawingNumber="NEW",Name="替代物料",Quantity=1},now)})
            await repository.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,number,ReleasePackageState.Published,null,"","",[],at.AddMinutes(-1),at,null){Scope=ReleaseScope.StandardFormal,StandardBomSnapshot=[item]},default);
        var service=new U9ProcurementService(new InMemoryU9ProcurementRepository(),new InMemoryMaterialRepository(clock),repository,new InMemoryProjectPlanningRepository(),null!,null!,null!,clock);
        var result=await service.ListProjectAsync(project.Id,"admin",UserRole.Administrator,default);
        Assert.Equal(0,Assert.Single(result.Items,row=>row.MaterialCode=="OLD" && row.IsDemandCanceled).Quantity);
        Assert.Equal(1,Assert.Single(result.Items,row=>row.MaterialCode=="NEW" && !row.IsDemandCanceled).Quantity);
    }
}
