using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PartialStructureBomTests
{
    [Fact]
    public async Task BatchQuantitySurvivesRefreshWithoutChangingCadSourceQuantities()
    {
        var f = await CreateAsync();
        var actor = await f.Repository.FindUserAsync("admin", default);
        if (actor is null)
        {
            actor = new(Guid.NewGuid(), "admin", "数量验收人员", "unused", UserRole.Administrator, true);
            await f.Repository.CreateUserAsync(actor, default);
        }
        var settings = await f.Repository.GetAuthorizationSettingsAsync(default);
        var permissions = new HashSet<string> { PermissionCodes.BomMechanicalEdit, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView };
        var pending = new Queue<string>(permissions);
        while (pending.TryDequeue(out var code))
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies)
                if (permissions.Add(dependency)) pending.Enqueue(dependency);
        await f.Repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(template => template.RoleCode == actor.EffectiveRoleCode
            ? template with { Grants = [.. template.Grants.Where(grant => !permissions.Contains(grant.Permission)), .. permissions.Select(code => new AuthorizationGrant(code, ["company"]))] }
            : template).ToArray() }, settings.Revision, default);
        var first = await PartAsync(f, "BATCH-ONE", new() { ["全局/物料分类"] = "标准件", ["全局/单位"] = "个" });
        var second = await PartAsync(f, "BATCH-TWO", new() { ["全局/物料分类"] = "标准件", ["全局/单位"] = "个" });
        var root = await ArchiveBatchTests.Register(f.Repository, f.Project, "BATCH-ROOT", DocumentKind.Assembly, f.Session);
        var tree = ArchiveBatchTests.Node(root) with { Children = [first with { Quantity = 1 }, second with { Quantity = 2 }] };
        await f.Repository.CheckInVersionAsync(root.Id, "admin", f.Session, ArchiveBatchTests.Commit(f.Project, root, tree, 'F', true), default);
        var generated = await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id, true, "admin", UserRole.Administrator, default);
        Assert.Equal(2, generated.StandardItems.Count);
        var ids = generated.StandardItems.Select(item => item.Id).ToArray();
        await f.Workflow.BatchUpdateBomItemsAsync(f.Project.Id, new(ids, ["quantity"], Quantity: 3), "admin", UserRole.Administrator, default);
        var refreshed = await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id, true, "admin", UserRole.Administrator, default);
        Assert.Equal(2, refreshed.StandardItems.Count);
        Assert.All(refreshed.StandardItems, item => { Assert.Equal(3, item.Quantity); Assert.True(item.IsSummaryQuantity); Assert.Contains(item.Id, ids); });
        var source = (await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default))!;
        Assert.Equal(new[] { 1, 2 }, source.Root.Children.Select(node => node.Quantity));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Workflow.BatchUpdateBomItemsAsync(f.Project.Id,
            new(ids, ["quantity"], Quantity: 1.5m), "admin", UserRole.Administrator, default));
        Assert.All(await f.Repository.GetBomAsync(f.Project.Id, BomKind.Standard, default), item => Assert.Equal(3, item.Quantity));
    }

    [Fact]
    public async Task ManuallyMaintainedFlatTotalSurvivesSourceRefreshAndDoesNotChangeTheArchivedStructure()
    {
        var f=await CreateAsync();
        var part=await PartAsync(f,"FLAT",new(){["全局/物料分类"]="标准件",["全局/物料编码"]="FLAT-CODE",["全局/物料名称"]="汇总维护物料"});
        var root=await ArchiveBatchTests.Register(f.Repository,f.Project,"ROOT",DocumentKind.Assembly,f.Session);
        var tree=ArchiveBatchTests.Node(root) with {Children=Enumerable.Range(0,4).Select(index=>part with {Quantity=1,InstancePath=$"ROOT/FLAT-{index}"}).ToArray()};
        await f.Repository.CheckInVersionAsync(root.Id,"admin",f.Session,ArchiveBatchTests.Commit(f.Project,root,tree,'F',true),default);
        var generated=await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id,true,"admin",UserRole.Administrator,default);
        var legacy=generated.StandardItems.Select(item=>item with {Quantity=0.5m}).ToArray();Assert.Equal(4,legacy.Length);
        await f.Repository.ReplaceBomAsync(f.Project.Id,BomKind.Standard,legacy,default);
        var summary=Assert.Single(BomSummaryProjection.Group(legacy));
        var edits=BomSummaryProjection.Expand(legacy,[new(summary.SummaryItemIds!,summary.SummaryFingerprint!,["quantity"],new(1,summary.DrawingNumber,summary.Name,6,summary.Unit,summary.Material,summary.Specification,summary.Revision,true))],[]);
        await f.Workflow.ReplaceBomAsync(f.Project.Id,BomKind.Standard,edits,"admin",UserRole.Administrator,default);
        var refreshed=await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id,true,"admin",UserRole.Administrator,default);
        var saved=Assert.Single(refreshed.StandardItems);Assert.Equal(6,saved.Quantity);Assert.True(saved.IsSummaryQuantity);Assert.Equal("Auto",saved.Source);
        Assert.False(saved.IsPendingRemoval);Assert.False(saved.IsManualUnmatched);
        await f.Repository.ReplaceBomAsync(f.Project.Id,BomKind.Standard,[],default);
        await f.Repository.ReplaceBomAsync(f.Project.Id,BomKind.NonStandard,[saved with {Kind=BomKind.NonStandard,IsManuallyOverridden=true}],default);
        var classified=await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id,true,"admin",UserRole.Administrator,default);
        Assert.Empty(classified.StandardItems);Assert.Equal(6,Assert.Single(classified.NonStandardItems).Quantity);
        var source=await f.Workflow.GetBomSourceDataAsync(f.Project.Id,"admin",UserRole.Administrator,default);
        Assert.Equal(4,source.Sum(item=>item.Quantity));
        var archive=(await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id,default))!;
        Assert.Equal(4,archive.Root.Children.Count);Assert.All(archive.Root.Children,node=>Assert.Equal(1,node.Quantity));
        var sibling=saved with {Id=Guid.NewGuid(),Kind=BomKind.NonStandard,Quantity=2,Source="Manual",SourceDocumentId=null,SourceInstancePath=null,IsSummaryQuantity=false,Sequence=2};
        await f.Repository.ReplaceBomAsync(f.Project.Id,BomKind.NonStandard,[classified.NonStandardItems.Single(),sibling],default);
        var withAdded=await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id,true,"admin",UserRole.Administrator,default);
        Assert.Equal(new decimal[]{2,6},withAdded.NonStandardItems.Select(item=>item.Quantity).Order().ToArray());
        var retained=withAdded.NonStandardItems.Single(item=>item.Id==saved.Id);
        await f.Repository.ReplaceBomAsync(f.Project.Id,BomKind.NonStandard,[retained],default);
        var only=Assert.Single(BomSummaryProjection.Group([retained]));
        var removed=BomSummaryProjection.Expand([retained],[new(only.SummaryItemIds!,only.SummaryFingerprint!,["quantity"],new(1,retained.DrawingNumber,retained.Name,0,retained.Unit,null,null,"W1",true))],[]);
        await f.Workflow.ReplaceBomAsync(f.Project.Id,BomKind.NonStandard,removed,"admin",UserRole.Administrator,default);
        var afterDelete=await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id,true,"admin",UserRole.Administrator,default);
        Assert.DoesNotContain(afterDelete.StandardItems,item=>!item.IsManuallyExcluded);
        Assert.DoesNotContain(afterDelete.NonStandardItems,item=>!item.IsManuallyExcluded);
    }
    [Fact]
    public async Task ArchiveFollowupAddsConfirmedPropertiesWithoutRequiringCompleteStructure()
    {
        var f = await CreateAsync();
        var part = await PartAsync(f, "PART", new Dictionary<string, string?>
        {
            ["全局/物料分类"] = "标准件", ["全局/物料编码"] = "CODE-1", ["全局/物料名称"] = "全局名称",
            ["配置:A/物料名称"] = "配置名称", ["全局/品牌"] = "测试品牌"
        });
        var blank = await PartAsync(f, "BLANK", new Dictionary<string, string?>());
        var root = await ArchiveBatchTests.Register(f.Repository, f.Project, "ROOT", DocumentKind.Assembly, f.Session);
        var commit = ArchiveBatchTests.Commit(f.Project, root, ArchiveBatchTests.Node(root) with
        {
            StructureIndexPending = true,
            Children = [part with { Configuration = "A", Quantity = 2, InstancePath = "ROOT/PART-1" }, blank]
        }, 'C', true);
        var receipt = await f.Workflow.CheckInBatchAsync(Guid.NewGuid(), "partial", "admin", UserRole.Administrator,
            f.Session, [new(root.Id, commit.File, commit.Properties, commit.ReferenceSnapshot, "部分结构", true, null)], default);
        await f.Workflow.CompleteArchiveBatchFollowupAsync(new(receipt, [root]), default);
        var item = Assert.Single(await f.Repository.GetBomAsync(f.Project.Id, BomKind.Standard, default));
        Assert.Equal("CODE-1", item.DrawingNumber);
        Assert.Equal("配置名称", item.Name);
        Assert.Equal("测试品牌", item.Brand);
        Assert.Equal(2, item.Quantity);
        var unknown = Assert.Single(await f.Repository.GetBomAsync(f.Project.Id, BomKind.Unclassified, default));
        Assert.Equal(blank.DocumentId, unknown.SourceDocumentId);
        Assert.True(unknown.IsPendingClassification);
        Assert.True((await f.Repository.GetLatestReferenceSnapshotAsync(f.Project.Id, default))!.Root.HasPendingStructureIndex);
        Assert.Single(await f.Repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.True(receipt.Results.Single().VersionCreated);
    }

    [Fact]
    public async Task PartialIndexPreservesAllExistingRowsAndDoesNotDuplicateChangedOccurrencePaths()
    {
        var f = await CreateAsync();
        var known = await PartAsync(f, "KNOWN", new Dictionary<string, string?> { ["全局/物料分类"] = "标准件" });
        var added = await PartAsync(f, "ADDED", new Dictionary<string, string?> { ["全局/物料分类"] = "非标件", ["全局/物料名称"] = "新增件" });
        var old = new BomItem(Guid.NewGuid(), f.Project.Id, BomKind.Standard, 17, "OLD", "保留名称", 12, "个", null, null, "W1", true)
        { Source = "Auto", SourceDocumentId = known.DocumentId, SourceConfiguration = "Default", SourceInstancePath = "ROOT/OLD", Remark = "保留备注" };
        var manual = new BomItem(Guid.NewGuid(), f.Project.Id, BomKind.NonStandard, 33, "MANUAL", "人工维护", 8, "个", null, null, "W1", true)
        { Source = "Manual", IsManuallyOverridden = true, IsManuallyRetained = true };
        await f.Repository.ApplyBomBatchAsync(f.Project.Id, [old], [manual], [], [], [], [], [], [], default);
        var root = await ArchiveBatchTests.Register(f.Repository, f.Project, "ROOT", DocumentKind.Assembly, f.Session);
        var tree = ArchiveBatchTests.Node(root) with { StructureIndexPending = true,
            Children = [known with { InstancePath = "ROOT/NEW", Quantity = 1 }, added with { InstancePath = "ROOT/ADDED" }] };
        await f.Repository.CheckInVersionAsync(root.Id, "admin", f.Session, ArchiveBatchTests.Commit(f.Project, root, tree, 'D', true), default);
        var result = await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id, true, "admin", UserRole.Administrator, default);
        Assert.Equal(BusinessValues(old), BusinessValues(Assert.Single(result.StandardItems)));
        Assert.Equal(BusinessValues(manual), BusinessValues(result.NonStandardItems.Single(item => item.Id == manual.Id)));
        var next = Assert.Single(result.NonStandardItems, item => item.SourceDocumentId == added.DocumentId);
        Assert.Equal(34, next.Sequence);
        Assert.False(next.IsPendingRemoval);
        Assert.Equal(0, result.PendingRemovalCount);
        Assert.Equal(0, result.ManualUnmatchedCount);
        var again = await f.Workflow.GenerateMechanicalBomAsync(f.Project.Id, true, "admin", UserRole.Administrator, default);
        Assert.Equal(result.StandardItems.Select(BusinessValues), again.StandardItems.Select(BusinessValues));
        Assert.Equal(result.NonStandardItems.Select(BusinessValues), again.NonStandardItems.Select(BusinessValues));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialIndexDoesNotGuessAnUnknownSourceVersion(bool absentRevision)
    {
        var f = await CreateAsync();
        var part = await PartAsync(f, "PART", new Dictionary<string, string?> { ["全局/物料分类"] = "标准件" });
        var root = await ArchiveBatchTests.Register(f.Repository, f.Project, "ROOT", DocumentKind.Assembly, f.Session);
        var tree = ArchiveBatchTests.Node(root) with { StructureIndexPending = true,
            Children = [part with { Revision = absentRevision ? null : new RevisionLabel(null, 999, false) }] };
        // Exercise derivation directly: new archive submissions separately validate
        // immutable source versions before they can reach this read-side fallback.
        var snapshot = ArchiveBatchTests.Commit(f.Project, root, tree, 'E', true).ReferenceSnapshot;
        var method = typeof(PdmWorkflowService).GetMethod("GenerateMechanicalBomFromSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<BomGenerationResult>)method.Invoke(f.Workflow, [f.Project.Id, snapshot, "admin", CancellationToken.None, false, true])!;
        Assert.Empty(result.StandardItems);
        Assert.Empty(result.UnclassifiedItems);
        Assert.False(result.Applied);
    }

    private static async Task<(InMemoryPdmRepository Repository, PdmWorkflowService Workflow, Project Project, Guid Session)> CreateAsync()
    {
        var repository = BusinessTestRepository.Create(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("PARTIAL-" + Guid.NewGuid().ToString("N"), "部分结构BOM", "admin", "vault", "release"), "admin", default);
        var workflow = new PdmWorkflowService(repository, DispatchProxy.Create<IFileStorage, ArchiveBatchReadReuseTests.Files>(), null!, TimeProvider.System);
        return (repository, workflow, project, Guid.NewGuid());
    }

    // Read responses decorate rows with fresh review tokens; those do not alter
    // the stored BOM's business fields or manual maintenance state.
    private static BomItem BusinessValues(BomItem item) => item with { ReleaseReviewToken = null, ReleaseReviewTokens = null };

    private static async Task<DocumentReferenceNode> PartAsync(
        (InMemoryPdmRepository Repository, PdmWorkflowService Workflow, Project Project, Guid Session) f, string name, Dictionary<string, string?> properties)
    {
        var part = await ArchiveBatchTests.Register(f.Repository, f.Project, name, DocumentKind.Part, f.Session);
        var saved = await f.Repository.CheckInVersionAsync(part.Id, "admin", f.Session,
            ArchiveBatchTests.Commit(f.Project, part, ArchiveBatchTests.Node(part), 'A') with { Properties = properties }, default);
        return ArchiveBatchTests.Node(saved.Document) with { Revision = saved.Version!.Revision };
    }
}
