using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class EngineeringKitServiceTests
{
    [Fact]
    public async Task Model_IsGeneratedFromStandardCategoryCode_OrKeptWhenManual()
    {
        var time = TimeProvider.System;
        var kits = new InMemoryEngineeringKitRepository();
        var materials = new InMemoryMaterialRepository(time);
        var pdm = new InMemoryPdmRepository(time);
        var service = new EngineeringKitService(kits, materials, pdm, time, Library());
        var required = Material("0102000001", "必选螺栓");
        await materials.UpsertU9MaterialAsync(required, CancellationToken.None);

        // 自动生成：型号 = 标准分类代码-序列号
        var autoDraft = await service.SaveDraftAsync(null, new(
            "自动型号套件", "UPTON", "备注信息", "首次建立",
            [new(required.Id, 1, false, 1)],
            ModelMode: EngineeringKitModelMode.Auto, StandardLibraryCategoryId: CategoryId),
            "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("备注信息", autoDraft.Description);
        Assert.Null(autoDraft.Model);
        var autoReleased = await service.PublishAsync(autoDraft.Id, autoDraft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("UTJ-01-0001", autoReleased.Model);

        // 手动填写：发布后保留手动型号
        var manualDraft = await service.SaveDraftAsync(null, new(
            "手动型号套件", "UPTON", null, null,
            [new(required.Id, 1, false, 1)],
            ModelMode: EngineeringKitModelMode.Manual, Model: "KIT-MANUAL-01", StandardLibraryCategoryId: CategoryId),
            "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("KIT-MANUAL-01", manualDraft.Model);
        var manualReleased = await service.PublishAsync(manualDraft.Id, manualDraft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("KIT-MANUAL-01", manualReleased.Model);

        // 自动模式缺少代码时拒绝保存
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null, new(
            "缺代码套件", "UPTON", null, null,
            [new(required.Id, 1, false, 1)]),
            "developer", UserRole.Administrator, CancellationToken.None));
    }

    [Fact]
    public async Task Publish_AssignsIndependentUkitCode_AndExpansionUsesOnlyRealMaterialCodes()
    {
        var time = TimeProvider.System;
        var kits = new InMemoryEngineeringKitRepository();
        var materials = new InMemoryMaterialRepository(time);
        var pdm = new InMemoryPdmRepository(time);
        var service = new EngineeringKitService(kits, materials, pdm, time, Library());
        var required = Material("0102000001", "必选螺栓");
        var second = Material("0102000002", "配套垫圈");
        await materials.UpsertU9MaterialAsync(required, CancellationToken.None);
        await materials.UpsertU9MaterialAsync(second, CancellationToken.None);

        var draft = await service.SaveDraftAsync(null, new(
            "安装附件套件", "UPTON", null, "首次建立",
            [new(required.Id, 5, false, 1), new(second.Id, 2, false, 2)],
            StandardLibraryCategoryId: CategoryId),
            "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Null(draft.Code);
        Assert.Null(draft.Model);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);
        Assert.Equal("UKIT-000001", released.Code);
        Assert.Equal("UTJ-01-0001", released.Model);
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
        var service = new EngineeringKitService(kits, materials, pdm, time, Library());
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, CancellationToken.None);
        var draft = await service.SaveDraftAsync(null, new("定位套件", "UPTON", null, null, [new(material.Id, 1, false, 1)], StandardLibraryCategoryId: CategoryId), "developer", UserRole.Administrator, CancellationToken.None);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "developer", UserRole.Administrator, CancellationToken.None);

        var changed = await service.SaveDraftAsync(released.Id,
            new("定位套件", "UPTON", null, "数量调整", [new(material.Id, 2, false, 1)], released.RowVersion, StandardLibraryCategoryId: CategoryId),
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
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), new InMemoryMaterialRepository(time), new InMemoryPdmRepository(time), time, Library());

        var allOptional = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("不允许可选件", "UPTON", null, null, [new(Guid.NewGuid(), 1, true, 1)], StandardLibraryCategoryId: CategoryId),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不支持可选子料", allOptional.Message);

        var unknown = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null,
            new("嵌套尝试", "UPTON", null, null, [new(Guid.NewGuid(), 1, false, 1)], StandardLibraryCategoryId: CategoryId),
            "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不能引用另一个套件", unknown.Message);
    }

    private static readonly Guid CategoryId = Guid.Parse("d3c89f93-8370-42b5-a8fe-a0bb6edf39ac");

    [Fact]
    public async Task PublishedCategoryMovesAndNewRevisionsKeepModelAndEngineerCannotReadDraft()
    {
        var library = Library();
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), materials, pdm, TimeProvider.System, library);
        var material = Material("0102000003", "定位销") with { Specification = "PIN-01", Brand = "UPTON" };
        await materials.UpsertU9MaterialAsync(material, default);
        var draft = await service.SaveDraftAsync(null, new("套件", "UPTON", null, null, [new(material.Id, 1, false, 1)], StandardLibraryCategoryId: CategoryId, CategoryCode: "伪造代码"), "admin", UserRole.Administrator, default);
        Assert.Empty(await service.ListLibraryAsync(CategoryId, null, null, "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.GetAsync(draft.Id, "engineer", UserRole.Engineer, default));
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-01-0001", released.Model);
        Assert.Single(await service.ListLibraryAsync(CategoryId, null, null, "engineer", UserRole.Engineer, default));
        Assert.Empty(await service.ListLibraryAsync(StandardLibraryDefaults.UncategorizedId, null, null, "engineer", UserRole.Engineer, default));
        var now = DateTimeOffset.UtcNow;
        var target = await library.CreateCategoryAsync(new(Guid.NewGuid(), "配套件", CategoryId, 1, true, "admin", now, "admin", now, 1, "PT"), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveCategoryAsync(released.Id, target.Id, released.RowVersion, "engineer", UserRole.Engineer, default));
        var moved = await service.MoveCategoryAsync(released.Id, target.Id, released.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(released.Model, moved.Model);
        Assert.Single(await service.ListLibraryAsync(CategoryId, null, null, "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.MoveCategoryAsync(released.Id, target.Id, released.RowVersion, "admin", UserRole.Administrator, default));
        var edited = await service.SaveDraftAsync(moved.Id, new("套件", "UPTON", null, null, [new(material.Id, 2, false, 1)], moved.RowVersion, ModelMode: EngineeringKitModelMode.Manual, Model: "尝试改号", StandardLibraryCategoryId: target.Id), "admin", UserRole.Administrator, default);
        Assert.Equal(released.Model, edited.Model);
        var engineerView = await service.GetAsync(edited.Id, "engineer", UserRole.Engineer, default);
        Assert.Single(engineerView.Revisions);
        Assert.Equal(1, engineerView.Revisions[0].Components[0].Quantity);
        Assert.Equal("PIN-01", engineerView.Revisions[0].Components[0].Specification);
        var next = await service.PublishAsync(edited.Id, edited.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(released.Model, next.Model);
        Assert.Equal(target.Id, next.StandardLibraryCategoryId);
        Assert.Equal(2, next.CurrentReleasedRevision?.VersionNumber);
    }

    [Fact]
    public async Task CategoryIsRequiredAndRootCategoryNumberIsAutomatic()
    {
        var library = Library();
        var now = DateTimeOffset.UtcNow;
        var uncoded = await library.CreateCategoryAsync(new(Guid.NewGuid(), "无代码分类", null, 1, true, "admin", now, "admin", now, 1), default);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, default);
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), materials, new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System, library);
        SaveEngineeringKitDraftCommand input = new("套件", "UPTON", null, null, [new(material.Id, 1, false, 1)]);
        foreach (var id in new[] { (Guid?)null, Guid.NewGuid(), StandardLibraryDefaults.UncategorizedId })
            await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveDraftAsync(null, input with { StandardLibraryCategoryId = id }, "admin", UserRole.Administrator, default));
        var auto = await service.SaveDraftAsync(null, input with { StandardLibraryCategoryId = uncoded.Id }, "admin", UserRole.Administrator, default);
        var autoReleased = await service.PublishAsync(auto.Id, auto.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-02-0001", autoReleased.Model);
        var manual = await service.SaveDraftAsync(null, input with { StandardLibraryCategoryId = uncoded.Id, ModelMode = EngineeringKitModelMode.Manual, Model = "MANUAL" }, "admin", UserRole.Administrator, default);
        var released = await service.PublishAsync(manual.Id, manual.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(uncoded.Id, released.StandardLibraryCategoryId);
        Assert.Equal("MANUAL", released.Model);
    }

    private static InMemoryStandardLibraryRepository Library()
    {
        var library = new InMemoryStandardLibraryRepository();
        var now = DateTimeOffset.UtcNow;
        library.CreateCategoryAsync(new(CategoryId, "结构件", null, 1, true, "admin", now, "admin", now, 1), default).GetAwaiter().GetResult();
        return library;
    }

    [Fact]
    public async Task ModelCountersAreIndependentOfMaterialCodesAndSharedByDescendants()
    {
        var library = Library();
        var now = DateTimeOffset.UtcNow;
        var child = await library.CreateCategoryAsync(new(Guid.NewGuid(), "子分类", CategoryId, 1, true, "admin", now, "admin", now, 1), default);
        var other = await library.CreateCategoryAsync(new(Guid.NewGuid(), "第二类", null, 2, true, "admin", now, "admin", now, 1), default);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, default);
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), materials, new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System, library);
        async Task<EngineeringKit> Publish(Guid categoryId, string? manual = null)
        {
            var draft = await service.SaveDraftAsync(null, new("套件", "UPTON", null, null, [new(material.Id, 1, false, 1)],
                ModelMode: manual is null ? EngineeringKitModelMode.Auto : EngineeringKitModelMode.Manual,
                Model: manual, StandardLibraryCategoryId: categoryId), "admin", UserRole.Administrator, default);
            return await service.PublishAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        }
        Assert.Equal("MANUAL", (await Publish(CategoryId, "MANUAL")).Model);
        var first = await Publish(child.Id);
        Assert.Equal("UKIT-000002", first.Code);
        Assert.Equal("UTJ-01-0001", first.Model);
        Assert.Equal("UTJ-02-0001", (await Publish(other.Id)).Model);
        Assert.Equal("UTJ-01-0002", (await Publish(CategoryId)).Model);

        var updated = await service.SaveDraftAsync(first.Id, new("套件", "UPTON", null, null, [new(material.Id, 2, false, 1)],
            first.RowVersion, StandardLibraryCategoryId: other.Id), "admin", UserRole.Administrator, default);
        var released = await service.PublishAsync(updated.Id, updated.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(first.Model, released.Model);
        Assert.Equal("UTJ-02-0002", (await Publish(other.Id)).Model);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Publish(child.Id)));
        Assert.Equal(12, concurrent.Select(item => item.Model).Distinct().Count());
        Assert.Equal(Enumerable.Range(3, 12).Select(number => $"UTJ-01-{number:D4}"), concurrent.Select(item => item.Model).Order());
    }

    [Fact]
    public async Task FirstPublishUsesCurrentRootAfterCategoryReparenting()
    {
        var library = Library();
        var now = DateTimeOffset.UtcNow;
        var other = await library.CreateCategoryAsync(new(Guid.NewGuid(), "第二类", null, 2, true, "admin", now, "admin", now, 1), default);
        var child = await library.CreateCategoryAsync(new(Guid.NewGuid(), "子分类", CategoryId, 1, true, "admin", now, "admin", now, 1), default);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, default);
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(), materials, new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System, library);
        var draft = await service.SaveDraftAsync(null, new("套件", "UPTON", null, null, [new(material.Id, 1, false, 1)], StandardLibraryCategoryId: child.Id), "admin", UserRole.Administrator, default);
        await library.UpdateCategoryAsync(child with { ParentId = other.Id }, child.RowVersion, default);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal("UTJ-02-0001", released.Model);
        Assert.Equal("UTJ-02", released.CategoryCode);
    }

    [Fact]
    public async Task DeactivationPreservesVersionsAndBlocksNewExpansionUntilReactivated()
    {
        var time = TimeProvider.System;
        var materials = new InMemoryMaterialRepository(time);
        var pdm = new InMemoryPdmRepository(time);
        var service = new EngineeringKitService(new InMemoryEngineeringKitRepository(pdm), materials, pdm, time, Library());
        var material = Material("0102000003", "定位销");
        await materials.UpsertU9MaterialAsync(material, default);
        var draft = await service.SaveDraftAsync(null, new("套件", "UPTON", null, null, [new(material.Id, 2, false, 1)], StandardLibraryCategoryId: CategoryId), "admin", UserRole.Administrator, default);
        Assert.True(draft.IsActive);
        var released = await service.PublishAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        var expansion = await service.ExpandAsync(released.Id, new(null, 3, []), "engineer", UserRole.Engineer, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetActiveAsync(released.Id, false, released.RowVersion, "engineer", UserRole.Engineer, default));
        var stopped = await service.SetActiveAsync(released.Id, false, released.RowVersion, "admin", UserRole.Administrator, default);
        Assert.False(stopped.IsActive);
        Assert.Equal(released.Code, stopped.Code);
        Assert.Equal(released.Model, stopped.Model);
        Assert.Equal(released.CurrentReleasedRevisionId, stopped.CurrentReleasedRevisionId);
        Assert.Equal(released.Revisions, stopped.Revisions);
        Assert.Equal(released.ReferenceCount, stopped.ReferenceCount);
        Assert.False((await service.GetAsync(released.Id, "engineer", UserRole.Engineer, default)).IsActive);
        Assert.False(Assert.Single(await service.ListLibraryAsync(CategoryId, null, null, "engineer", UserRole.Engineer, default)).IsActive);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ExpandAsync(released.Id, new(released.CurrentReleasedRevisionId, 1, []), "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SetActiveAsync(released.Id, true, released.RowVersion, "admin", UserRole.Administrator, default));
        var edited = await service.SaveDraftAsync(released.Id, new("套件", "UPTON", null, null, [new(material.Id, 3, false, 1)], stopped.RowVersion, StandardLibraryCategoryId: CategoryId), "admin", UserRole.Administrator, default);
        Assert.False(edited.IsActive);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.PublishAsync(edited.Id, edited.RowVersion, "admin", UserRole.Administrator, default));
        var enabled = await service.SetActiveAsync(edited.Id, true, edited.RowVersion, "admin", UserRole.Administrator, default);
        Assert.True(enabled.IsActive);
        var restoredExpansion = await service.ExpandAsync(enabled.Id, new(released.CurrentReleasedRevisionId, 3, []), "engineer", UserRole.Engineer, default);
        Assert.Equal(expansion.Lines, restoredExpansion.Lines);
        var next = await service.PublishAsync(enabled.Id, enabled.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(2, next.CurrentReleasedRevision?.VersionNumber);
        Assert.Equal(released.Model, next.Model);
    }

    [Theory]
    [InlineData(1, "UTJ-01-0001")]
    [InlineData(9999, "UTJ-01-9999")]
    public void ModelUsesExactlyFourSequenceDigits(long sequence, string expected) =>
        Assert.Equal(expected, EngineeringKitNumbering.Model("UTJ-01", sequence));

    [Fact]
    public void ModelRejectsOverflowInsteadOfProducingFiveDigits() =>
        Assert.Throws<PdmRuleException>(() => EngineeringKitNumbering.Model("UTJ-01", 10000));

    private static PdmMaterial Material(string code, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new PdmMaterial(Guid.NewGuid(), code, name, MaterialKind.Standard, MaterialSupplyMode.Purchase, "001", null, null, null, null, null,
            null, null, null, MaterialApprovalStatus.Approved, "tester", now, "0102", code, code, MaterialSyncStatus.Succeeded,
            "tester", now, "tester", now, 1, "0102", false, null, null, true, MaterialDataSource.U9C, MaterialMasterOwner.U9C, now);
    }
}
