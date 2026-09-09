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
        var optional = Material("0102000002", "可选垫圈");
        await materials.UpsertU9MaterialAsync(required, CancellationToken.None);
        await materials.UpsertU9MaterialAsync(optional, CancellationToken.None);

        var draft = await service.SaveDraftAsync(null, new(
            "安装附件套件", "仅供PDM工程引用", "首次建立",
            [new(required.Id, 5, false, 1), new(optional.Id, 2, true, 2)]),
            "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Null(draft.Code);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("UKIT-000001", released.Code);
        Assert.Equal(1, released.CurrentReleasedRevision?.VersionNumber);

        var requiredOnly = await service.ExpandAsync(released.Id, new(null, 3, []), "engineer", UserRole.Engineer, CancellationToken.None);
        Assert.Single(requiredOnly.Lines);
        Assert.Equal("0102000001", requiredOnly.Lines[0].MaterialCode);
        Assert.Equal(15, requiredOnly.Lines[0].Quantity);
        Assert.DoesNotContain(requiredOnly.Lines, item => item.MaterialCode.StartsWith("UKIT-", StringComparison.Ordinal));

        var withOptional = await service.ExpandAsync(released.Id, new(null, 2, [optionalComponent(released)]), "engineer", UserRole.Engineer, CancellationToken.None);
        Assert.Equal(2, withOptional.Lines.Count);
        Assert.Equal(4, withOptional.Lines.Single(item => item.IsOptional).Quantity);
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
        var draft = await service.SaveDraftAsync(null, new("定位套件", null, null, [new(material.Id, 1, false, 1)]), "developer", UserRole.Administrator, CancellationToken.None);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);

        var changed = await service.SaveDraftAsync(released.Id,
            new("定位套件", null, "数量调整", [new(material.Id, 2, false, 1)], released.RowVersion),
            "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal("UKIT-000001", changed.Code);
        Assert.Equal(1, changed.CurrentReleasedRevision?.VersionNumber);
        Assert.Equal(1, changed.CurrentReleasedRevision?.Components.Single().Quantity);
        Assert.Equal(2, changed.DraftRevision?.VersionNumber);
        Assert.Equal(2, changed.DraftRevision?.Components.Single().Quantity);
    }

    [Fact]
    public async Task SaveDraft_RejectsAllOptionalOrUnknownComponents()
    {
        var time = TimeProvider.System;
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), new InMemoryMaterialRepository(time), new InMemoryPdmRepository(time), time);

        var allOptional = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("无必选套件", null, null, [new(Guid.NewGuid(), 1, true, 1)]),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("至少需要一个必选子料", allOptional.Message);

        var unknown = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("嵌套尝试", null, null, [new(Guid.NewGuid(), 1, false, 1)]),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不能引用另一个套件", unknown.Message);
    }

    private static Guid optionalComponent(EngineeringKit kit) =>
        kit.CurrentReleasedRevision!.Components.Single(item => item.IsOptional).Id;

    private static PdmMaterial Material(string code, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new PdmMaterial(Guid.NewGuid(), code, name, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", null, null, null, null, null,
            null, null, null, MaterialApprovalStatus.Approved, "tester", now, "0102", code, code, MaterialSyncStatus.Succeeded,
            "tester", now, "tester", now, 1, "0102", false, null, null, true, MaterialDataSource.U9C, MaterialMasterOwner.U9C, now);
    }
}
