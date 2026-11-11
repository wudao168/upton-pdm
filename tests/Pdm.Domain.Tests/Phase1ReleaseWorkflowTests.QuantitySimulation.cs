using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    [Fact]
    public async Task SelectedSummaryReplacesBothOldOccurrencesInDraftResubmissionAndApproval()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var first = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "01020003769", "带导杆气缸", 1, "001", null, "MGPM12-20Z", "W1", true);
        var second = first with { Id = Guid.NewGuid(), Sequence = 2 };
        var deferred = first with { Id = Guid.NewGuid(), Sequence = 3, DrawingNumber = "DEFER" };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [first, second, deferred], default);
        var formal = await workflow.CreateScopedReleasePackageAsync(ProjectId, null, "", "", "", "未指定", null,
            ReleaseScope.StandardFormal, [first.Id, second.Id, deferred.Id], "admin", UserRole.Administrator, default);
        formal = await PublishThroughAllStepsAsync(workflow, formal);
        var total = second with { Quantity = 5, IsSummaryQuantity = true, IsManuallyRetained = true };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [total], default);
        var draft = await workflow.CreateScopedReleasePackageAsync(ProjectId, null, "", "", "设计问题 / 设计变更", "未指定", null,
            ReleaseScope.StandardSupplement, [total.Id], "admin", UserRole.Administrator, default);
        void Check(ReleasePackage package)
        {
            Assert.Equal(5, Assert.Single(package.StandardBomSnapshot, item => item.DrawingNumber == first.DrawingNumber).Quantity);
            Assert.Contains(package.StandardBomSnapshot, item => item.Id == deferred.Id);
        }
        Check(draft);
        var submitted = await workflow.SubmitReleasePackageAsync(draft.Id, "admin", UserRole.Administrator, default);
        Check(submitted);
        await workflow.WithdrawReleasePackageAsync(draft.Id, "admin", UserRole.Administrator, "重核数量", default);
        Check(await workflow.UpdateReleasePackageDraftAsync(draft.Id, draft.ChangeReason, [total.Id], null, "admin", UserRole.Administrator, default));
        Check(await PublishThroughAllStepsAsync(workflow, draft));
        var history = await repository.FindReleasePackageAsync(formal.Id, default);
        Assert.Equal(2, history!.StandardBomSnapshot.Where(item => item.DrawingNumber == first.DrawingNumber).Sum(item => item.Quantity));
    }

    [Theory]
    [InlineData(BomKind.Standard,ReleaseScope.StandardFormal,ReleaseScope.StandardSupplement)]
    [InlineData(BomKind.Electrical,ReleaseScope.ElectricalFormal,ReleaseScope.ElectricalSupplement)]
    public async Task QuantitySimulation_AddIncreaseDecreaseDeleteDeferRestoreAndMultiply(BomKind kind, ReleaseScope formalScope, ReleaseScope supplementScope)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        BomItem Item(string code,decimal quantity,string unit="001",Guid? kit=null)
        {
            var id=Guid.NewGuid();
            return new(id,ProjectId,kind,1,code,"仿真物料"+code,quantity,unit,null,"SIM-M1","W1",true)
            {ReleaseTrackingId=id,IsManuallyOverridden=true,EngineeringKitReferenceId=kit,Source=kit.HasValue?"EngineeringKit":"Manual"};
        }
        var a=Item("SIM-A",1,kit:kind==BomKind.Standard?Guid.NewGuid():null);
        var b=Item("SIM-B",4);
        var c=Item("SIM-C",2);
        var current=new List<BomItem>{a,b,c};
        var history=new Dictionary<Guid,string>();
        ReleasePackage? previous=null;
        var multiplier=1;
        async Task Publish(string stage,Guid[] ids,Dictionary<string,decimal> expected,Guid[]? removed=null,bool formal=false)
        {
            await repository.ReplaceBomAsync(ProjectId,kind,current,default);
            var before=previous is null?new Dictionary<string,decimal>():SimulationTotals(previous,kind);
            var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","",formal?"":"设计问题 / 设计变更","未指定",null,
                formal?formalScope:supplementScope,ids,"admin",UserRole.Administrator,default,
                wholeSetMultiplier:multiplier,confirmedRemovedBomItemIds:removed);
            Assert.Equal(ids.Order(),package.SelectedBomItemIds.Order());
            if(!formal)package=await workflow.UpdateReleasePackageDraftAsync(package.Id,"设计问题 / 设计变更",ids,null,
                "admin",UserRole.Administrator,default,wholeSetMultiplier:multiplier,confirmedRemovedBomItemIds:removed);
            package=await PublishThroughAllStepsAsync(workflow,package);
            Assert.Equal(ReleasePackageState.Published,package.State);
            var actual=SimulationTotals(package,kind);
            Assert.Equal(expected.OrderBy(pair=>pair.Key).ToArray(),actual.OrderBy(pair=>pair.Key).ToArray());
            foreach(var frozen in history)
                Assert.Equal(frozen.Value,SimulationFingerprint((await repository.FindReleasePackageAsync(frozen.Key,default))!,kind));
            history[package.Id]=SimulationFingerprint(package,kind);
            var net=before.Keys.Union(actual.Keys).ToDictionary(key=>key,key=>actual.GetValueOrDefault(key)-before.GetValueOrDefault(key));
            var comparison=ReleaseQuantityComparison.Build(package,kind,await repository.ListReleasePackagesAsync(ProjectId,default));
            foreach(var line in comparison) Assert.Equal(net.GetValueOrDefault(line.MaterialKey),line.DemandChange);
            var demand=BomReleaseAggregation.BuildForHistory(package,await repository.ListReleasePackagesAsync(ProjectId,default));
            Assert.Equal(net.Where(pair=>pair.Value>0).OrderBy(pair=>pair.Key).ToArray(),
                demand.PurchaseDemand.ToDictionary(line=>line.MaterialCode+"|"+line.UnitCode,line=>line.Quantity).OrderBy(pair=>pair.Key).ToArray());
            simulationOutput.WriteLine("SIMULATION "+JsonSerializer.Serialize(new{kind=kind.ToString(),stage,
                selected=ids.Length,removed=removed?.Length??0,multiplier,beforeByMaterial=before,afterByMaterial=actual,
                netByMaterial=net,targetsByUnit=SimulationUnits(actual),
                addedDemandByUnit=SimulationUnits(net.ToDictionary(pair=>pair.Key,pair=>Math.Max(0,pair.Value))),
                reducedDemandByUnit=SimulationUnits(net.ToDictionary(pair=>pair.Key,pair=>Math.Max(0,-pair.Value))),historyUnchanged=true}));
            previous=package;
            current=(await repository.GetBomAsync(ProjectId,kind,default)).Where(item=>!item.IsManuallyExcluded&&!item.IsPendingRemoval).ToList();
        }
        Dictionary<string,decimal> Expected(params (string Code,decimal Quantity)[] values)=>values.ToDictionary(value=>value.Code,value=>value.Quantity);
        void SetQuantity(Guid id,decimal quantity)=>current=current.Select(item=>item.Id==id?item with{Quantity=quantity}:item).ToList();

        await Publish("初次正式发布",[a.Id,b.Id,c.Id],Expected(("SIM-A|001",1),("SIM-B|001",4),("SIM-C|001",2)),formal:true);
        var added=Item("SIM-A",5,kit:kind==BomKind.Standard?Guid.NewGuid():null);
        current.Insert(0,added);
        await Publish("新增同料号实例，仅选择新增5",[added.Id],Expected(("SIM-A|001",6),("SIM-B|001",4),("SIM-C|001",2)));
        SetQuantity(b.Id,7);
        await Publish("增加已有物料4到7",[b.Id],Expected(("SIM-A|001",6),("SIM-B|001",7),("SIM-C|001",2)));
        SetQuantity(b.Id,3);
        await Publish("减少已有物料7到3",[b.Id],Expected(("SIM-A|001",6),("SIM-B|001",3),("SIM-C|001",2)));
        current.RemoveAll(item=>item.Id==a.Id);
        await Publish("仅删除旧实例1，保留同料号新实例5",[],Expected(("SIM-A|001",5),("SIM-B|001",3),("SIM-C|001",2)),[a.Id]);
        current.RemoveAll(item=>item.Id==c.Id);
        var d=Item("SIM-D",2);current.Add(d);
        await Publish("删除2并新增另一物料2，合计不变仍有增减",[d.Id],Expected(("SIM-A|001",5),("SIM-B|001",3),("SIM-D|001",2)),[c.Id]);
        SetQuantity(b.Id,9);
        var e=Item("SIM-E",4);current.Add(e);
        await Publish("顺延已有物料3到9，仅发布新物料4",[e.Id],Expected(("SIM-A|001",5),("SIM-B|001",3),("SIM-D|001",2),("SIM-E|001",4)));
        Assert.Equal(9,current.Single(item=>item.Id==b.Id).Quantity);
        await Publish("下一次补发顺延变更3到9",[b.Id],Expected(("SIM-A|001",5),("SIM-B|001",9),("SIM-D|001",2),("SIM-E|001",4)));
        await workflow.UpdateBomMultiplierAsync(ProjectId,kind,2,1,"admin",UserRole.Administrator,default);
        multiplier=2;
        var count=(await repository.ListReleasePackagesAsync(ProjectId,default)).Count;
        await Assert.ThrowsAsync<PdmRuleException>(()=>workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","设计问题 / 设计变更","未指定",null,
            supplementScope,[b.Id],"admin",UserRole.Administrator,default,wholeSetMultiplier:2));
        Assert.Equal(count,(await repository.ListReleasePackagesAsync(ProjectId,default)).Count);
        await Publish("倍率变化确认全部影响项",current.Select(item=>item.Id).ToArray(),Expected(("SIM-A|001",10),("SIM-B|001",18),("SIM-D|001",4),("SIM-E|001",8)));
        var boxed=Item("SIM-BOX",1,"004");current.Add(boxed);
        await Publish("新增盒单位，按单位合计而不混加件数",[boxed.Id],Expected(("SIM-A|001",10),("SIM-B|001",18),("SIM-D|001",4),("SIM-E|001",8),("SIM-BOX|004",2)));
    }

    [Fact]
    public async Task QuantitySimulation_NonStandardApprovedDrawingIncreaseDecreaseDeleteAndRestore()
    {
        var repository=new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        foreach(var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id,"admin","仿真准备",default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository,workflow);
        var part=(await repository.GetBomAsync(ProjectId,BomKind.NonStandard,default)).First() with{Quantity=4,IsManuallyOverridden=true};
        var current=new List<BomItem>{part};
        var history=new Dictionary<Guid,string>();
        var multiplier=1;
        async Task Publish(string stage,decimal expected,Guid[] selected,Guid[]? removed=null,bool formal=false)
        {
            await repository.ReplaceBomAsync(ProjectId,BomKind.NonStandard,current,default);
            var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","",formal?"":"设计问题 / 设计变更","未指定",null,
                formal?ReleaseScope.NonStandardWithDrawing:ReleaseScope.NonStandardSupplement,selected,"admin",UserRole.Administrator,default,
                wholeSetMultiplier:multiplier,confirmedRemovedBomItemIds:removed);
            package=await PublishThroughAllStepsAsync(workflow,package);
            Assert.Equal(ReleasePackageState.Published,package.State);
            Assert.Equal(expected,package.NonStandardBomSnapshot.Sum(item=>item.Quantity)*package.WholeSetMultiplier);
            foreach(var frozen in history)Assert.Equal(frozen.Value,SimulationFingerprint((await repository.FindReleasePackageAsync(frozen.Key,default))!,BomKind.NonStandard));
            history[package.Id]=SimulationFingerprint(package,BomKind.NonStandard);
            simulationOutput.WriteLine("SIMULATION "+JsonSerializer.Serialize(new{kind="NonStandard",stage,selected=selected.Length,removed=removed?.Length??0,multiplier,
                targetsByUnit=SimulationUnits(SimulationTotals(package,BomKind.NonStandard)),historyUnchanged=true}));
            current=(await repository.GetBomAsync(ProjectId,BomKind.NonStandard,default)).Where(item=>!item.IsManuallyExcluded&&!item.IsPendingRemoval).ToList();
        }
        await Publish("已审图非标初次正式4",4,[part.Id],formal:true);
        current=current.Select(item=>item with{Quantity=6}).ToList();
        await Publish("已审非标增加4到6",6,[part.Id]);
        current=current.Select(item=>item with{Quantity=2}).ToList();
        await Publish("已审非标减少6到2",2,[part.Id]);
        var restore=current.Single();current.Clear();
        await Publish("删除最后一个非标项形成空基线",0,[],[part.Id]);
        current=[restore with{Quantity=3}];
        await Publish("恢复旧物料后发布3，删除记录不被改写",3,[part.Id]);
        await workflow.UpdateBomMultiplierAsync(ProjectId,BomKind.NonStandard,2,1,"admin",UserRole.Administrator,default);multiplier=2;
        await Publish("非标倍率2形成目标6，源单套仍为3",6,[part.Id]);
        Assert.Equal(3,current.Single().Quantity);
    }

    private static IReadOnlyList<BomItem> SimulationSnapshot(ReleasePackage package,BomKind kind)=>kind switch
    {BomKind.Standard=>package.StandardBomSnapshot,BomKind.NonStandard=>package.NonStandardBomSnapshot,_=>package.ElectricalBomSnapshot};
    private static Dictionary<string,decimal> SimulationTotals(ReleasePackage package,BomKind kind)=>SimulationSnapshot(package,kind)
        .Where(item=>!item.IsManuallyExcluded&&!item.IsReleaseExcluded&&!item.IsPendingRemoval)
        .GroupBy(item=>item.DrawingNumber.Trim().ToUpperInvariant()+"|"+U9UnitCatalog.NormalizeBomUnit(item.Unit))
        .ToDictionary(group=>group.Key,group=>group.Sum(item=>item.Quantity)*package.WholeSetMultiplier);
    private static Dictionary<string,decimal> SimulationUnits(IReadOnlyDictionary<string,decimal> materials)=>materials
        .GroupBy(pair=>pair.Key[(pair.Key.LastIndexOf('|')+1)..]).ToDictionary(group=>group.Key,group=>group.Sum(pair=>pair.Value));
    private static string SimulationFingerprint(ReleasePackage package,BomKind kind)=>JsonSerializer.Serialize(SimulationSnapshot(package,kind)
        .OrderBy(item=>item.Id).Select(item=>new{item.Id,item.DrawingNumber,item.Unit,item.Quantity}));
}
