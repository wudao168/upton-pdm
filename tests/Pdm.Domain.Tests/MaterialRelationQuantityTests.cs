using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed partial class MaterialRelationServiceTests
{
    [Fact]
    public async Task QuantityPreviewDoesNotWriteAndConfirmationResizesAccessoriesButPreservesFixedAndPublishedSnapshot()
    {
        var clock=TimeProvider.System;var pdm=new InMemoryPdmRepository(clock);var materials=new InMemoryMaterialRepository(clock);var relations=new InMemoryMaterialRelationRepository();
        var service=new MaterialRelationService(relations,materials,pdm,clock);
        var project=await pdm.CreateProjectAsync(new("QTY-"+Guid.NewGuid().ToString("N"),"配套数量确认","admin","vault","release"),"admin",default);
        var motor=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","MOTOR-Q","主料");
        var variable=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","VARIABLE-Q","随主料配件");
        var fixedPart=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","FIXED-Q","固定配件");
        var draft=await service.SaveDraftAsync(null,new(motor.Id,"配套确认","初版",null,[
            new("随动",true,MaterialRelationSelectionMode.Single,1,1,false,1,[new(variable.Id,MaterialRelationQuantityMode.PerMainQuantity,2,false,1)]),
            new("固定",true,MaterialRelationSelectionMode.Single,1,1,false,2,[new(fixedPart.Id,MaterialRelationQuantityMode.Fixed,3,false,1)])]),"admin",UserRole.Administrator,default);
        var published=await service.PublishAsync(draft.Id,draft.DraftRevision!.Id,draft.DraftRevision.RowVersion,"admin",UserRole.Administrator,default);
        var main=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,motor.MaterialCode,motor.Name,2,"001",null,null,"W1",true);
        await pdm.ReplaceBomAsync(project.Id,BomKind.Standard,[main],default);
        await service.ApplyAsync(project.Id,[new(main.Id,published.PublishedRevision!.Groups.Select(group=>new MaterialRelationChoice(group.Id,[group.Options[0].Id])).ToArray())],"admin",UserRole.Administrator,default);
        var customized=(await pdm.GetBomAsync(project.Id,BomKind.Standard,default)).Select(row=>row.DrawingNumber==variable.MaterialCode
            ?row with {Remark="保留工程师备注",IsWearPart=true,ImpactStage="调试",Brand="保留品牌"}:row).ToArray();
        await pdm.ReplaceBomAsync(project.Id,BomKind.Standard,customized,default);
        var old=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);
        var package=await pdm.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,"RP-FROZEN",ReleasePackageState.Published,null,"","",[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null)
            {Scope=ReleaseScope.StandardFormal,StandardBomSnapshot=old,StandardBomRevision="S-Q-B01"},default);
        var allocation=old.Single(row=>row.Id==main.Id).RelationAllocations.Single();
        var change=new BomConfigurationQuantityChange([new(main.Id,allocation.Id)],4,2);
        var preview=await service.ChangeQuantitiesAsync(project.Id,[change],"admin",UserRole.Administrator,default,previewOnly:true);
        Assert.Contains(preview.Changes,row=>row.MaterialCode==variable.MaterialCode && row.Before==4 && row.After==8);
        Assert.DoesNotContain(preview.Changes,row=>row.MaterialCode==fixedPart.MaterialCode);
        Assert.Equal(2,(await pdm.GetBomAsync(project.Id,BomKind.Standard,default)).Single(row=>row.Id==main.Id).Quantity);
        await Assert.ThrowsAsync<PdmRuleException>(()=>service.ChangeQuantitiesAsync(project.Id,[change],"admin",UserRole.Administrator,default));
        await service.ChangeQuantitiesAsync(project.Id,[change with {Confirmed=true,ConfirmationToken=preview.ConfirmationToken}],"admin",UserRole.Administrator,default,validateConfirmation:true);
        var saved=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);
        Assert.Equal(4,saved.Single(row=>row.Id==main.Id).Quantity);
        Assert.Equal(8,saved.Single(row=>row.DrawingNumber==variable.MaterialCode).Quantity);
        var retained=saved.Single(row=>row.DrawingNumber==variable.MaterialCode);
        Assert.Equal("保留工程师备注",retained.Remark);Assert.True(retained.IsWearPart);Assert.Equal("调试",retained.ImpactStage);Assert.Equal("保留品牌",retained.Brand);
        Assert.Equal(3,saved.Single(row=>row.DrawingNumber==fixedPart.MaterialCode).Quantity);
        Assert.True((await service.GetCompletenessAsync(project.Id,"admin",UserRole.Administrator,default)).IsComplete);
        Assert.Equal(old,(await pdm.FindReleasePackageAsync(package.Id,default))!.StandardBomSnapshot);
        await Assert.ThrowsAsync<PdmConflictException>(()=>service.ChangeQuantitiesAsync(project.Id,[change with {Confirmed=true}],"admin",UserRole.Administrator,default));
    }

    [Fact]
    public async Task ResizingOneAllocationLeavesTheOtherCombinationAndItsAccessoriesUnchanged()
    {
        var clock=TimeProvider.System;var pdm=new InMemoryPdmRepository(clock);var materials=new InMemoryMaterialRepository(clock);var relations=new InMemoryMaterialRelationRepository();var service=new MaterialRelationService(relations,materials,pdm,clock);
        var project=await pdm.CreateProjectAsync(new("COMBO-Q-"+Guid.NewGuid().ToString("N"),"组合数量","admin","vault","release"),"admin",default);
        var motor=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","MOTOR-C","主料");var a=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","ACC-A","配件A");var b=await AddApprovedMaterialAsync(materials,MaterialKind.Standard,"0102","ACC-B","配件B");
        var draft=await service.SaveDraftAsync(null,new(motor.Id,"多组合","初版",null,[new("配件",true,MaterialRelationSelectionMode.Single,1,1,false,1,[new(a.Id,MaterialRelationQuantityMode.PerMainQuantity,1,false,1),new(b.Id,MaterialRelationQuantityMode.PerMainQuantity,1,false,2)])]),"admin",UserRole.Administrator,default);
        var template=await service.PublishAsync(draft.Id,draft.DraftRevision!.Id,draft.DraftRevision.RowVersion,"admin",UserRole.Administrator,default);var group=template.PublishedRevision!.Groups[0];
        var main=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,motor.MaterialCode,motor.Name,2,"001",null,null,"W1",true);await pdm.ReplaceBomAsync(project.Id,BomKind.Standard,[main],default);
        var first=Guid.NewGuid();var second=Guid.NewGuid();
        await service.ApplyAsync(project.Id,[new(main.Id,[],[new(first,"组合A",1,[new(group.Id,[group.Options[0].Id])]),new(second,"组合B",1,[new(group.Id,[group.Options[1].Id])])])],"admin",UserRole.Administrator,default);
        var change=new BomConfigurationQuantityChange([new(main.Id,first)],3,1,true);
        var preview=await service.ChangeQuantitiesAsync(project.Id,[change],"admin",UserRole.Administrator,default,previewOnly:true);
        var corrupted=(await pdm.GetBomAsync(project.Id,BomKind.Standard,default)).Select(row=>row.DrawingNumber==a.MaterialCode?row with {Quantity=9}:row).ToArray();
        await pdm.ReplaceBomAsync(project.Id,BomKind.Standard,corrupted,default);
        await Assert.ThrowsAsync<PdmConflictException>(()=>service.ChangeQuantitiesAsync(project.Id,[change with {ConfirmationToken=preview.ConfirmationToken}],"admin",UserRole.Administrator,default,validateConfirmation:true));
        Assert.Equal(2,(await pdm.GetBomAsync(project.Id,BomKind.Standard,default)).Single(row=>row.Id==main.Id).Quantity);
        var fresh=await service.ChangeQuantitiesAsync(project.Id,[change],"admin",UserRole.Administrator,default,previewOnly:true);
        await service.ChangeQuantitiesAsync(project.Id,[change with {ConfirmationToken=fresh.ConfirmationToken}],"admin",UserRole.Administrator,default,validateConfirmation:true);
        var saved=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);var updated=saved.Single(row=>row.Id==main.Id);
        Assert.Equal(4,updated.Quantity);Assert.Equal(3,updated.RelationAllocations.Single(item=>item.Id==first).Quantity);Assert.Equal(1,updated.RelationAllocations.Single(item=>item.Id==second).Quantity);
        Assert.Equal(3,saved.Single(row=>row.DrawingNumber==a.MaterialCode).Quantity);Assert.Equal(1,saved.Single(row=>row.DrawingNumber==b.MaterialCode).Quantity);
        await pdm.ReplaceBomAsync(project.Id,BomKind.Standard,saved.Where(row=>row.DrawingNumber!=a.MaterialCode).ToArray(),default);
        var restore=new BomConfigurationQuantityChange([new(main.Id,first)],2,3,true);
        var restorePreview=await service.ChangeQuantitiesAsync(project.Id,[restore],"admin",UserRole.Administrator,default,previewOnly:true);
        Assert.Contains(restorePreview.Changes,row=>row.MaterialCode==a.MaterialCode && row.Before==0 && row.After==2);
        await service.ChangeQuantitiesAsync(project.Id,[restore with {ConfirmationToken=restorePreview.ConfirmationToken}],"admin",UserRole.Administrator,default,validateConfirmation:true);
        Assert.Equal(2,(await pdm.GetBomAsync(project.Id,BomKind.Standard,default)).Single(row=>row.DrawingNumber==a.MaterialCode).Quantity);
        var remove=new BomConfigurationQuantityChange([new(main.Id,first)],0,2,true);
        var removal=await service.ChangeQuantitiesAsync(project.Id,[remove],"admin",UserRole.Administrator,default,previewOnly:true);
        Assert.Contains(removal.Changes,row=>row.MaterialCode==a.MaterialCode && row.After==0);
        await service.ChangeQuantitiesAsync(project.Id,[remove with {ConfirmationToken=removal.ConfirmationToken}],"admin",UserRole.Administrator,default,validateConfirmation:true);
        var remaining=await pdm.GetBomAsync(project.Id,BomKind.Standard,default);
        Assert.Equal(1,remaining.Single(row=>row.Id==main.Id).Quantity);Assert.DoesNotContain(remaining,row=>row.DrawingNumber==a.MaterialCode);
        Assert.Equal(1,remaining.Single(row=>row.DrawingNumber==b.MaterialCode).Quantity);
    }
}
