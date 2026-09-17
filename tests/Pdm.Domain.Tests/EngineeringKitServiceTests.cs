using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class EngineeringKitServiceTests
{
    [Fact]
    public async Task Publish_AssignsIndependentUkitCode_AndExpansionUsesOnlyRealMaterialCodes()
    {
        var time = TimeProvider.System;
        var kits = new InMemoryEngineeringKitRepository();
        var materials = new InMemoryMaterialRepository(time);
        var pdm = new InMemoryPdmRepository(time);
        var service = new EngineeringKitService(kits, materials, pdm, time);
        var required = Material("0102000001", "必选螺栓");
        var second = Material("0102000002", "配套垫圈");
        await materials.UpsertU9MaterialAsync(required, CancellationToken.None);
        await materials.UpsertU9MaterialAsync(second, CancellationToken.None);

        var draft = await service.SaveDraftAsync(null, new(
            "安装附件套件", "UPTON", null, "首次建立",
            [new(required.Id, 5, false, 1), new(second.Id, 2, false, 2)]),
            "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Null(draft.Code);
        Assert.Null(draft.Model);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("UKIT-000001", released.Code);
        Assert.Equal(released.Code, released.Model);
        Assert.Equal("UPTON", released.Brand);
        Assert.Equal(1, released.CurrentReleasedRevision?.VersionNumber);

        var expansion = await service.ExpandAsync(released.Id, new(null, 3, []), "engineer", UserRole.Engineer, CancellationToken.None);
        Assert.Equal(2, expansion.Lines.Count);
        Assert.Equal(15, expansion.Lines.Single(item => item.MaterialCode == "0102000001").Quantity);
        Assert.Equal(6, expansion.Lines.Single(item => item.MaterialCode == "0102000002").Quantity);
        Assert.DoesNotContain(expansion.Lines, item => item.MaterialCode.StartsWith("UKIT-", StringComparison.Ordinal));
        Assert.All(expansion.Lines, item => Assert.False(item.IsOptional));
    }

    [Fact]
    public async Task EditingReleasedKit_CreatesNextDraftWithoutChangingPinnedRelease()
    {
        var time = TimeProvider.System;
        var kits = new InMemoryEngineeringKitRepository();
        var materials = new InMemoryMaterialRepository(time);
        var pdm = new InMemoryPdmRepository(time);
        var service = new EngineeringKitService(kits, materials, pdm, time);
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, CancellationToken.None);
        var draft = await service.SaveDraftAsync(null, new("定位套件", "UPTON", null, null, [new(material.Id, 1, false, 1)]), "developer", UserRole.Administrator, CancellationToken.None);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);

        var changed = await service.SaveDraftAsync(released.Id,
            new("定位套件", "UPTON", null, "数量调整", [new(material.Id, 2, false, 1)], released.RowVersion),
            "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal("UKIT-000001", changed.Code);
        Assert.Equal(1, changed.CurrentReleasedRevision?.VersionNumber);
        Assert.Equal(1, changed.CurrentReleasedRevision?.Components.Single().Quantity);
        Assert.Equal(2, changed.DraftRevision?.VersionNumber);
        Assert.Equal(2, changed.DraftRevision?.Components.Single().Quantity);
    }

    [Fact]
    public async Task SaveDraft_RejectsOptionalOrUnknownComponents()
    {
        var time = TimeProvider.System;
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), new InMemoryMaterialRepository(time), new InMemoryPdmRepository(time), time);

        var allOptional = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("不允许可选件", "UPTON", null, null, [new(Guid.NewGuid(), 1, true, 1)]),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不支持可选子料", allOptional.Message);

        var unknown = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("嵌套尝试", "UPTON", null, null, [new(Guid.NewGuid(), 1, false, 1)]),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不能引用另一个套件", unknown.Message);
    }

    private static PdmMaterial Material(string code, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new PdmMaterial(Guid.NewGuid(), code, name, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", null, null, null, null, null,
            null, null, null, MaterialApprovalStatus.Approved, "tester", now, "0102", code, code, MaterialSyncStatus.Succeeded,
            "tester", now, "tester", now, 1, "0102", false, null, null, true, MaterialDataSource.U9C, MaterialMasterOwner.U9C, now);
    }
}
