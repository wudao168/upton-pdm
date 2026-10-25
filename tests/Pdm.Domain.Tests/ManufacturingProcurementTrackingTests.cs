using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ManufacturingProcurementTrackingTests
{
    [Fact]
    public async Task NonstandardReadsManufacturingWithoutAnyU9OrMaterialLookup()
    {
        var f = await Fixture();
        var service = new U9ProcurementService(DispatchProxy.Create<IU9ProcurementRepository, RejectReads>(),
            DispatchProxy.Create<IMaterialRepository, RejectReads>(), f.Repository, new InMemoryProjectPlanningRepository(), null!, null!, null!, TimeProvider.System, f.Production);
        var result = await service.ListProjectAsync(f.Project.Id, "admin", UserRole.Administrator, default);
        var row = Assert.Single(result.Items);
        Assert.Equal(10, row.Quantity); Assert.Equal("冻结的加工件", row.MaterialName);
        Assert.Empty(row.PurchaseOrderNumbers); Assert.Empty(row.PurchaseRequisitionNumbers);
        Assert.Empty(row.WarehouseMovements); Assert.Null(result.LastSuccessfulRefreshAt); Assert.NotNull(result.ManufacturingReadAt);
        var m = Assert.IsType<ManufacturingTracking>(row.Manufacturing);
        Assert.Equal(10, m.AssignedQuantity); Assert.Equal(8, m.SubmittedQuantity);
        Assert.Equal(5, m.QualifiedQuantity); Assert.Equal(2, m.IssuedQuantity);
        Assert.Equal("外协厂", m.Suppliers); Assert.Contains("自制负责人", m.Responsible!);
        Assert.Equal(2, m.Batches.Count); Assert.Equal("A", m.Revision);
    }

    [Fact]
    public async Task SameMaterialCodeKeepsU9PurchaseAndManufacturingAndVersionsSeparate()
    {
        var f = await Fixture(); var now = DateTimeOffset.UtcNow;
        var item = f.Item with { Id = Guid.NewGuid(), Kind = BomKind.Standard, Name = "采购标准件", Quantity = 4 };
        await f.Repository.CreateReleasePackageAsync(f.Package with { Id = Guid.NewGuid(), Number = "RP-STD", Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = [item], NonStandardBomSnapshot = [] }, default);
        var procurement = new InMemoryU9ProcurementRepository();
        await procurement.ReplaceSnapshotAsync(Guid.NewGuid(), [new("001","PO","po",null,"PO-999",1,3,false,now,
            item.DrawingNumber,"ERP名称",null,null,f.Project.Code,null,null,0,0,999,900,null,now,now)], now, default);
        var record = (await f.Store.FindAsync(f.Package.Id, default))!;
        var oldLine = record.State.Lines[0];
        var nextLine = oldLine with { Id = Guid.NewGuid(), Revision = "B", RequiredQuantity = 2 };
        await f.Store.SaveAsync(record with { State = record.State with { Lines = [oldLine, nextLine] } }, record.RowVersion, default);
        var service = new U9ProcurementService(procurement,new InMemoryMaterialRepository(TimeProvider.System),f.Repository,
            new InMemoryProjectPlanningRepository(),null!,null!,null!,TimeProvider.System,f.Production);
        var result = await service.ListProjectAsync(f.Project.Id,"admin",UserRole.Administrator,default);
        Assert.Equal(3,result.Items.Count);
        Assert.Equal(999,Assert.Single(result.Items,r=>r.Manufacturing is null).PurchaseQuantity);
        var manufacturing = result.Items.Where(r=>r.Manufacturing is not null).ToArray();
        Assert.Equal(new[]{"A","B"},manufacturing.Select(r=>r.Manufacturing!.Revision));
        Assert.Equal(5,manufacturing[0].Manufacturing!.QualifiedQuantity);
        Assert.Equal(0,manufacturing[1].Manufacturing!.QualifiedQuantity);
        Assert.All(manufacturing,r=>Assert.Empty(r.PurchaseOrderNumbers));
    }

    [Fact]
    public async Task MissingManufacturingRecordShowsPendingReceptionWithoutU9Fallback()
    {
        var f = await Fixture();
        var service = new U9ProcurementService(DispatchProxy.Create<IU9ProcurementRepository,RejectReads>(),
            DispatchProxy.Create<IMaterialRepository,RejectReads>(),f.Repository,new InMemoryProjectPlanningRepository(),null!,null!,null!,TimeProvider.System);
        var row = Assert.Single((await service.ListProjectAsync(f.Project.Id,"admin",UserRole.Administrator,default)).Items);
        Assert.Equal(10,row.Quantity); Assert.Equal("待生产接收",row.Manufacturing!.Status);
        Assert.Empty(row.Manufacturing.Batches); Assert.Null(row.Manufacturing.ForecastOn);
    }

    [Fact]
    public async Task CancellationTransferAndFlowResetUseEffectiveQuantitiesWhileKeepingHistory()
    {
        var f = await Fixture(); var state = (await f.Store.FindAsync(f.Package.Id,default))!.State;
        var lot = state.Lots[0]; var now=DateTimeOffset.UtcNow;
        ProductionMovement Move(string kind,decimal quantity,bool reset=false)=>new(Guid.NewGuid(),Guid.NewGuid(),lot.Id,kind,quantity,"admin",now){ResetsFlow=reset};
        state = state with {Movements=state.Movements.Concat(new[]{Move("Cancelled",1),Move("Transferred",1),Move("ProductionReceived",4,true),Move("Pass",2)}).ToArray()};
        var row=ManufacturingProcurementTracking.Row(f.Project,f.Project,f.Package,state.Lines[0],state,f.Item,null,null);
        // Reset starts a new effective flow; prior operations remain visible only as history.
        Assert.Equal(10,row.Quantity); Assert.Equal(3,row.Manufacturing!.QualifiedQuantity);
        Assert.Contains(row.Manufacturing.Batches[0].History,m=>m.Kind=="Cancelled");
        Assert.Contains(row.Manufacturing.Batches[0].History,m=>m.ResetsFlow);
        state=state with {Movements=state.Movements.Where(m=>!m.ResetsFlow&&m.Kind!="Pass").ToArray()};
        row=ManufacturingProcurementTracking.Row(f.Project,f.Project,f.Package,state.Lines[0],state,f.Item,null,null);
        Assert.Equal(8,row.Quantity); Assert.Equal(8,row.Manufacturing!.AssignedQuantity);
    }

    [Fact]
    public async Task ForeignProjectRecordsCannotLeakIntoProjectProcurement()
    {
        var f=await Fixture();var state=(await f.Store.FindAsync(f.Package.Id,default))!.State;
        await f.Store.CreateAsync(state with {ReleasePackageId=Guid.NewGuid(),ProjectId=Guid.NewGuid(),PackageNumber="FOREIGN"},default);
        var service=new U9ProcurementService(DispatchProxy.Create<IU9ProcurementRepository,RejectReads>(),new InMemoryMaterialRepository(TimeProvider.System),f.Repository,new InMemoryProjectPlanningRepository(),null!,null!,null!,TimeProvider.System,f.Production);
        var row=Assert.Single((await service.ListProjectAsync(f.Project.Id,"admin",UserRole.Administrator,default)).Items);
        Assert.Equal(f.Package.Number,row.ReleasePackageNumber);
    }

    private static async Task<FixtureData> Fixture()
    {
        var time=TimeProvider.System;var repository=new InMemoryPdmRepository(time);var project=Assert.Single(await repository.ListProjectsAsync(default));
        var item=new BomItem(Guid.NewGuid(),project.Id,BomKind.NonStandard,1,"02040001","冻结的加工件",10,"个","钢","CUSTOM-42","A",true){ImpactStage=ProjectPlanStage.Assembly};
        var now=time.GetUtcNow();var package=new ReleasePackage(Guid.NewGuid(),project.Id,"RP-MFG",ReleasePackageState.Published,null,"","",[],now.AddMinutes(-1),now,"D:/Release")
        {Scope=ReleaseScope.NonStandardWithDrawing,NonStandardBomSnapshot=[item],DrawingRequiredOn=DateOnly.FromDateTime(now.DateTime).AddDays(3)};
        await repository.CreateReleasePackageAsync(package,default);
        var store=new InMemoryProductionTrackingStore(DateTimeOffset.MinValue);var production=new ProductionTrackingService(repository,store,time,new(repository));
        await production.PublishedAsync(package.Id,default);var record=(await store.FindAsync(package.Id,default))!;var line=Assert.Single(record.State.Lines);
        ProductionLot Lot(string route,decimal qty,string person)=>new(Guid.NewGuid(),line.Id,route,qty,route=="External"?"外协厂":null,person,DateOnly.FromDateTime(now.DateTime).AddDays(4),null,null,0);
        var self=Lot("Self",6,"自制负责人");var external=Lot("External",4,"外协负责人");
        ProductionMovement Move(ProductionLot lot,string kind,decimal qty)=>new(Guid.NewGuid(),Guid.NewGuid(),lot.Id,kind,qty,"admin",now);
        await store.SaveAsync(record with {State=record.State with {ReceivedAt=now,ReceivedBy="admin",Lots=[self,external],Movements=[Move(self,"QualityReceived",6),Move(self,"Pass",4),Move(self,"Issue",2),Move(external,"ExternalDelivered",4),Move(external,"QualityReceived",2),Move(external,"Pass",1)]}},record.RowVersion,default);
        return new(repository,project,item,package,store,production);
    }
    private sealed record FixtureData(InMemoryPdmRepository Repository,Project Project,BomItem Item,ReleasePackage Package,InMemoryProductionTrackingStore Store,ProductionTrackingService Production);
    public class RejectReads:DispatchProxy { protected override object? Invoke(MethodInfo? method,object?[]? args)=>throw new InvalidOperationException("非标件不应访问U9C："+method!.Name); }
}
