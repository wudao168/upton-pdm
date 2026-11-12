using System.Reflection;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    private static T ReloadSnapshot<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static BomItem WithSnapshotConfiguration(BomItem item)
    {
        var optionId=Guid.NewGuid();
        var group=new MaterialRelationAllocationGroup(Guid.NewGuid(),"配套组",[optionId])
        {
            Options=[new(optionId,"CONFIG-PART","配件","MODEL","UPTON","001",MaterialRelationQuantityMode.PerMainQuantity,1,1)]
        };
        var allocation=new MaterialRelationAllocation(Guid.NewGuid(),"组合A",1,Guid.NewGuid(),1,[group]);
        return item with{RelationAllocations=[allocation],EngineeringKitRelationAllocations=[allocation]};
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotComparison_ReloadedConfigurationsDoNotBlockUnchangedFormalSubmission(bool configured)
    {
        var repository=global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        var items=(await repository.GetBomAsync(ProjectId,BomKind.Standard,default)).Select(item=>configured?WithSnapshotConfiguration(item):item).ToArray();
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,items,default);
        var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","","未指定",null,
            ReleaseScope.StandardFormal,items.Select(item=>item.Id).ToArray(),"admin",UserRole.Administrator,default);
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,ReloadSnapshot(items),default);
        var submitted=await workflow.SubmitReleasePackageAsync(package.Id,"admin",UserRole.Administrator,default);
        Assert.NotEqual(ReleasePackageState.Draft,submitted.State);
        Assert.Equal(items.Sum(item=>item.Quantity),submitted.StandardBomSnapshot.Sum(item=>item.Quantity));
    }

    [Fact]
    public async Task SnapshotComparison_ReloadedDeletionOnlySupplementKeepsItsExistingDraftAndQuantities()
    {
        var repository=global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        var items=(await repository.GetBomAsync(ProjectId,BomKind.Standard,default)).Select(WithSnapshotConfiguration).ToArray();
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,items,default);
        var formal=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","","未指定",null,
            ReleaseScope.StandardFormal,items.Select(item=>item.Id).ToArray(),"admin",UserRole.Administrator,default);
        formal=await PublishThroughAllStepsAsync(workflow,formal);
        var frozen=JsonSerializer.Serialize(formal.StandardBomSnapshot);
        var removed=items[0];
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,ReloadSnapshot(items.Skip(1).ToArray()),default);
        var change=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","设计问题 / 设计变更","未指定",null,
            ReleaseScope.StandardSupplement,[],"admin",UserRole.Administrator,default,confirmedRemovedBomItemIds:[removed.Id]);
        var version=(await repository.FindBomVersionAsync(ProjectId,change.StandardBomVersionId!.Value,default))!;
        var comparison=typeof(PdmWorkflowService).GetMethod("BomSnapshotsEqual",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.True((bool)comparison.Invoke(null,[version.Items,ReloadSnapshot(version.Items)])!);
        var reloaded=await repository.SaveBomDraftAsync(ProjectId,BomKind.Standard,ReloadSnapshot(version.Items),"admin",default);
        Assert.Equal(version.Id,reloaded.Id);
        var submitted=await workflow.SubmitReleasePackageAsync(change.Id,"admin",UserRole.Administrator,default);
        Assert.Equal(change.Id,submitted.Id);
        Assert.Equal(change.StandardBomVersionId,submitted.StandardBomVersionId);
        Assert.Equal([removed.Id],submitted.ConfirmedRemovedBomItemIds);
        Assert.Equal(formal.StandardBomSnapshot.Sum(item=>item.Quantity)-removed.Quantity,submitted.StandardBomSnapshot.Sum(item=>item.Quantity));
        Assert.Equal(frozen,JsonSerializer.Serialize((await repository.FindReleasePackageAsync(formal.Id,default))!.StandardBomSnapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotComparison_StillRejectsChangedConfigurationContents(bool kit)
    {
        var repository=global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow=new PdmWorkflowService(repository,new UnusedFileStorage(),new RecordingPublisher(),TimeProvider.System);
        var items=(await repository.GetBomAsync(ProjectId,BomKind.Standard,default)).Select(WithSnapshotConfiguration).ToArray();
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,items,default);
        var package=await workflow.CreateScopedReleasePackageAsync(ProjectId,null,"","","","未指定",null,
            ReleaseScope.StandardFormal,items.Select(item=>item.Id).ToArray(),"admin",UserRole.Administrator,default);
        var current=ReloadSnapshot(items);
        var allocation=(kit?current[0].EngineeringKitRelationAllocations:current[0].RelationAllocations)[0];
        var group=allocation.Groups[0];
        var changed=allocation with{Groups=[group with{Options=[group.Options[0] with{QuantityPerSet=2,Quantity=2}]}]};
        current[0]=kit?current[0] with{EngineeringKitRelationAllocations=[changed]}:current[0] with{RelationAllocations=[changed]};
        await repository.ReplaceBomAsync(ProjectId,BomKind.Standard,current,default);
        var conflict=await Assert.ThrowsAsync<PdmConflictException>(()=>workflow.SubmitReleasePackageAsync(package.Id,"admin",UserRole.Administrator,default));
        Assert.Contains("在发布包创建后已变化",conflict.Message);
    }
}
