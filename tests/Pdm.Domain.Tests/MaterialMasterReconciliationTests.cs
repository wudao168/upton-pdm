using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class MaterialMasterReconciliationTests
{
    private static PdmMaterial Baseline() => new(Guid.NewGuid(), "01020001234", "PLM名称", MaterialKind.Standard,
        MaterialSupplyMode.Purchase, "001", "PLM型号", "铝", "PLM备注", "PLM品牌", null, 20, "kg", null,
        MaterialApprovalStatus.Approved, "reviewer", DateTimeOffset.UtcNow, "0102", "u9-original", "01020001234",
        MaterialSyncStatus.Failed, "creator", DateTimeOffset.UtcNow.AddDays(-5), "editor", DateTimeOffset.UtcNow, 4, "0102",
        IsArchived: true, ArchivedBy: "editor", U9SyncConfirmed: true, SourceSystem: MaterialDataSource.U9C,
        MasterOwner: MaterialMasterOwner.U9C, SelectionAdvice: "项目建议", ReferencePrice: 123);

    [Theory]
    [InlineData(MaterialDataSource.Pdm)]
    [InlineData(MaterialDataSource.U9C)]
    public void ReconciliationPreservesPlmTechnicalFieldsApprovalAvailabilityAndProvenance(MaterialDataSource source)
    {
        var baseline = Baseline() with { SourceSystem = source };
        var incoming = baseline with { Id = Guid.NewGuid(), Name = "ERP名称", Specification = "ERP型号", Brand = "ERP品牌",
            UnitCode = "002", IsArchived = false, U9IsEffective = true, LastU9SyncedAt = DateTimeOffset.UtcNow };
        var result = MaterialMasterReconciliation.Merge(baseline, incoming);
        Assert.Equal(baseline.Id, result.Id);
        Assert.Equal(baseline.Name, result.Name);
        Assert.Equal(baseline.Specification, result.Specification);
        Assert.Equal(baseline.UnitCode, result.UnitCode);
        Assert.Equal(baseline.Brand, result.Brand);
        Assert.Equal(baseline.ApprovedBy, result.ApprovedBy);
        Assert.Equal(baseline.CreatedAt, result.CreatedAt);
        Assert.Equal(baseline.SelectionAdvice, result.SelectionAdvice);
        Assert.Equal(baseline.ReferencePrice, result.ReferencePrice);
        Assert.Equal(MaterialSyncStatus.Failed, result.SyncStatus);
        Assert.True(result.IsArchived);
        Assert.True(result.U9IsEffective);
        Assert.Equal(source, result.SourceSystem);
        Assert.Equal(MaterialMasterOwner.Pdm, result.MasterOwner);
        Assert.Contains("ERP名称", result.U9ReconciliationMessage);
        Assert.Contains("启停", result.U9ReconciliationMessage);
    }

    [Fact]
    public void UnconfirmedSameCodeCollisionIsReportedWithoutTakingOverU9Identity()
    {
        var local = Baseline() with { SourceSystem = MaterialDataSource.Pdm, MasterOwner = MaterialMasterOwner.Pdm,
            U9SyncConfirmed = false, U9ItemId = null, U9ItemCode = null };
        var incoming = local with { Name = "另一个ERP物料", U9ItemId = "foreign", U9ItemCode = local.MaterialCode,
            U9SyncConfirmed = true, U9IsEffective = true, SourceSystem = MaterialDataSource.U9C };
        var result = MaterialMasterReconciliation.Merge(local, incoming);
        Assert.False(result.U9SyncConfirmed);
        Assert.Null(result.U9ItemId);
        Assert.NotNull(result.U9ReconciliationMessage);
    }

    [Fact]
    public void ExistingU9IdentityIsNeverReboundToDifferentRecord()
    {
        var baseline = Baseline();
        var result = MaterialMasterReconciliation.Merge(baseline, baseline with { U9ItemId = "different", U9IsEffective = false });
        Assert.Equal("u9-original", result.U9ItemId);
        Assert.Contains("内部标识", result.U9ReconciliationMessage);
    }

    [Fact]
    public void NewU9MaterialKeepsSourceAndInitialStateButBecomesPlmControlled()
    {
        var incoming = Baseline() with { U9IsEffective = false };
        var result = MaterialMasterReconciliation.Merge(null, incoming);
        Assert.True(result.IsArchived);
        Assert.Equal(MaterialDataSource.U9C, result.SourceSystem);
        Assert.Equal(MaterialMasterOwner.Pdm, result.MasterOwner);
        Assert.Equal(incoming.MaterialCode, result.MaterialCode);
    }
}
