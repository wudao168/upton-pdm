using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class Phase1ReleaseWorkflowTests
{
    [Theory]
    [InlineData("EngineeringKit", null)]
    [InlineData("EngineeringKit", "旧品牌")]
    [InlineData("MaterialRelation", null)]
    public async Task CurrentBomAndReleaseUseUpdatedMaterialMaster(string source, string? oldBrand)
    {
        var clock = TimeProvider.System;
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(clock);
        await ConfigureApprovalWorkflowsAsync(repository);
        var materials = new InMemoryMaterialRepository(clock);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), clock,
            materialRepository: materials);
        await AddApprovedStandardMaterialAsync(materials, "02030000319", "R722.S1.00-21", "卡拉五金");
        var material = (await materials.FindMaterialByCodeAsync("02030000319", default))!;
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, material.MaterialCode,
            "旧名称", 3, "001", null, "旧型号", "W1", false)
        {
            Brand = oldBrand, Source = source, Remark = "关联物料：UKIT-000004",
            EngineeringKitReferenceId = source == "EngineeringKit" ? Guid.NewGuid() : null,
            EngineeringKitRevisionId = source == "EngineeringKit" ? Guid.NewGuid() : null,
            RelationAllocations = [new(Guid.NewGuid(), "组合A", 3, Guid.NewGuid(), 1, [])]
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row], default);

        var refreshed = Assert.Single(await workflow.GetBomAsync(ProjectId, BomKind.Standard, "admin", UserRole.Administrator, default));
        Assert.Equal("卡拉五金", refreshed.Brand);
        Assert.Equal(material.Name, refreshed.Name);
        Assert.Equal(material.Specification, refreshed.Specification);
        Assert.True(refreshed.IsComplete);
        Assert.Equal(row.Quantity, refreshed.Quantity);
        Assert.Equal(row.Unit, refreshed.Unit);
        Assert.Equal(row.Remark, refreshed.Remark);
        Assert.Equal(row.EngineeringKitRevisionId, refreshed.EngineeringKitRevisionId);
        Assert.Equal(row.RelationAllocations, refreshed.RelationAllocations);

        var materialService = new MaterialService(materials, repository, null!, null!, clock);
        var resolution = Assert.Single(await materialService.ResolveStandardBomMaterialsAsync(new(ProjectId, [row.Id]),
            "admin", UserRole.Administrator, default));
        Assert.Equal(MaterialCodeResolutionStatus.Verified, resolution.Status);
        Assert.Empty(resolution.Issues);

        var package = await workflow.CreateScopedReleasePackageAsync(ProjectId, null, "", "", "", "未指定", null,
            ReleaseScope.StandardFormal, [row.Id], "admin", UserRole.Administrator, default);
        Assert.Equal("卡拉五金", Assert.Single(package.StandardBomSnapshot).Brand);

        await materials.UpdateMaterialAsync(material with { Brand = "后续品牌", Name = "后续名称" }, material.RowVersion, default);
        Assert.Equal("后续品牌", Assert.Single(await workflow.GetBomAsync(ProjectId, BomKind.Standard, "admin", UserRole.Administrator, default)).Brand);
        Assert.Equal("卡拉五金", Assert.Single((await repository.FindReleasePackageAsync(package.Id, default))!.StandardBomSnapshot).Brand);
    }

    [Fact]
    public async Task CurrentBomDoesNotReplaceCadIdentityOrUseUnapprovedMaterial()
    {
        var clock = TimeProvider.System;
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(clock);
        var materials = new InMemoryMaterialRepository(clock);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), clock,
            materialRepository: materials);
        await AddApprovedStandardMaterialAsync(materials, "02030000319", "R722.S1.00-21", "卡拉五金");
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "02030000319", "图档名称", 3, "001", null, "图档型号", "W1", false)
            { SourceDocumentId = Guid.NewGuid(), Brand = "图档品牌", Source = "Auto" };
        var draft = row with { Id = Guid.NewGuid(), SourceDocumentId = null, Source = "EngineeringKit", Brand = "原品牌" };
        var manual = row with { Id = Guid.NewGuid(), SourceDocumentId = null, Source = "Manual", Brand = "人工品牌" };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row, draft, manual], default);
        var approved = await workflow.GetBomAsync(ProjectId, BomKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal("图档品牌", approved.Single(item => item.Id == row.Id).Brand);
        Assert.Equal("图档型号", approved.Single(item => item.Id == row.Id).Specification);
        Assert.Equal("卡拉五金", approved.Single(item => item.Id == draft.Id).Brand);
        Assert.Equal("人工品牌", approved.Single(item => item.Id == manual.Id).Brand);
        var material = (await materials.FindMaterialByCodeAsync(row.DrawingNumber, default))!;
        await materials.UpdateMaterialAsync(material with { ApprovalStatus = MaterialApprovalStatus.Draft, Brand = "未批准品牌" }, material.RowVersion, default);

        var current = await workflow.GetBomAsync(ProjectId, BomKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal("图档品牌", current.Single(item => item.Id == row.Id).Brand);
        Assert.Equal("图档型号", current.Single(item => item.Id == row.Id).Specification);
        Assert.Equal("原品牌", current.Single(item => item.Id == draft.Id).Brand);
    }
}
