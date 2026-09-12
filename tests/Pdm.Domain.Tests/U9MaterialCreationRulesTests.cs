using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class U9MaterialCreationRulesTests
{
    [Theory]
    [InlineData("0101", MaterialKind.Electrical, MaterialSupplyMode.Purchase, null, 9, 1, true, true)]
    [InlineData("0102", MaterialKind.Standard, MaterialSupplyMode.Purchase, null, 9, 1, true, true)]
    [InlineData("0301", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Master, 10, 0, false, true)]
    [InlineData("0302", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Master, 10, 0, false, true)]
    [InlineData("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Standard, 6, 0, true, false)]
    [InlineData("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Electrical, 6, 0, true, false)]
    [InlineData("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.NonStandard, 6, 0, true, false)]
    public void Templates_DistinguishPurchasedMasterAndVirtualBom(string category, MaterialKind kind,
        MaterialSupplyMode supply, ProjectBomHeaderKind? header, int form, int planning, bool request, bool outsource)
    {
        var material = Material(category, kind, supply);
        var payload = Payload(material, header);
        using var document = JsonDocument.Parse(payload);
        var row = document.RootElement[0];
        Assert.Equal(form, row.GetProperty("ItemFormAttribute").GetInt32());
        Assert.Equal(planning, row.GetProperty("MrpInfo").GetProperty("MRPPlanningType").GetInt32());
        Assert.Equal(request, row.GetProperty("PurchaseInfo").GetProperty("IsNeedRequest").GetBoolean());
        Assert.Equal(outsource, row.GetProperty("IsOutsideOperationEnable").GetBoolean());
        Assert.Equal("C001", row.GetProperty("CostCurrency").GetProperty("Code").GetString());
        Assert.True(row.GetProperty("InventoryInfo").GetProperty("IsBalanceByProject").GetBoolean());
        Assert.Equal(2, row.GetProperty("InventoryInfo").GetProperty("LotControlMode").GetInt32());
        Assert.True(row.GetProperty("MrpInfo").GetProperty("IsControlByDC").GetBoolean());
        Assert.Equal(1, row.GetProperty("MrpInfo").GetProperty("ForecastContorlType").GetInt32());
        Assert.True(row.GetProperty("MrpInfo").GetProperty("IsTraceRequirement").GetBoolean());
        Assert.Equal(1, row.GetProperty("MfgInfo").GetProperty("DesignationRule").GetInt32());
        foreach (var attribute in new[] { "IsPurchaseEnable", "IsSalesEnable", "IsBuildEnable", "IsMRPEnable", "IsBOMEnable", "IsInventoryEnable", "IsVarRatio" })
            Assert.True(row.GetProperty(attribute).GetBoolean());
        Assert.Equal("true", row.GetProperty("Effective").GetProperty("IsEffective").GetString());
        Assert.True(row.GetProperty("InventoryInfo").GetProperty("IsInvCalculateBySeiban").GetBoolean());
        Assert.True(row.GetProperty("PurchaseInfo").GetProperty("ReceiptModeAllowModify").GetBoolean());
        Assert.True(row.GetProperty("SaleInfo").GetProperty("IsReturnable").GetBoolean());
        Assert.True(row.GetProperty("SaleInfo").GetProperty("IsRMAAllowModify").GetBoolean());
        Assert.True(row.GetProperty("MfgInfo").GetProperty("IsExpandByOrder").GetBoolean());
        Assert.Equal(1, row.GetProperty("MfgInfo").GetProperty("BuildShrinkageRate").GetInt32());
        Assert.True(row.GetProperty("PurchaseInfo").GetProperty("IsPUTradePathModify").GetBoolean());
        Assert.True(row.GetProperty("PurchaseInfo").GetProperty("IsPURtnTradePathModify").GetBoolean());
        Assert.True(row.GetProperty("SaleInfo").GetProperty("IsSDTradePathModify").GetBoolean());
        Assert.True(row.GetProperty("SaleInfo").GetProperty("IsSDRtnTradePathModify").GetBoolean());
    }

    [Fact]
    public void OrdinaryFixture_IsNotMisclassifiedAsVirtualBomByNameOrCategory()
    {
        var material = Material("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture) with { Name = "标准件BOM" };
        using var document = JsonDocument.Parse(Payload(material));
        Assert.Equal(10, document.RootElement[0].GetProperty("ItemFormAttribute").GetInt32());
        Assert.False(document.RootElement[0].TryGetProperty("MfgInfo", out _));
        Assert.Equal(1, document.RootElement[0].GetProperty("MrpInfo").GetProperty("MRPPlanningType").GetInt32());
    }

    [Fact]
    public void SpecialCategory_RetainsLegacySettings()
    {
        using var document = JsonDocument.Parse(Payload(Material("010402", MaterialKind.Standard, MaterialSupplyMode.Purchase)));
        Assert.False(document.RootElement[0].TryGetProperty("CostCurrency", out _));
        Assert.False(document.RootElement[0].GetProperty("IsBuildEnable").GetBoolean());
        Assert.Equal(1, document.RootElement[0].GetProperty("MrpInfo").GetProperty("MRPPlanningType").GetInt32());
    }

    [Theory]
    [InlineData("0102", MaterialKind.Standard, MaterialSupplyMode.Purchase, null, "1", "0")]
    [InlineData("0302", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Master, "0", "1")]
    [InlineData("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Standard, "0", "1")]
    public void Readback_ValidatesPlanningMethodForMaterialAndBomHeaders(string category, MaterialKind kind,
        MaterialSupplyMode supply, ProjectBomHeaderKind? header, string expected, string wrong)
    {
        var payload = Payload(Material(category, kind, supply), header);
        using var document = JsonDocument.Parse(payload);
        var actual = U9MaterialCreationRules.ReadAttributes(document.RootElement[0]).ToDictionary();
        Assert.Equal(expected, actual["MrpInfo.MRPPlanningType"]);
        Assert.Empty(U9MaterialCreationRules.Compare(payload, actual));
        actual["MrpInfo.MRPPlanningType"] = wrong;
        Assert.Contains(U9MaterialCreationRules.Compare(payload, actual), error => error.Contains("MRPPlanningType"));
    }

    [Fact]
    public void Readback_NormalizesInternalNamesAndDetectsMissingOrWrongAttributes()
    {
        using var response = JsonDocument.Parse("""
            {"m_costCurrency":{"m_code":"C001"},"m_inventoryInfo":{"m_turnOverRate":0.00},
             "m_mrpInfo":{"m_isControlByDC":true,"m_mRPPlanningType":0}}
            """);
        var actual = U9MaterialCreationRules.ReadAttributes(response.RootElement);
        Assert.Equal("C001", actual["CostCurrency.Code"]);
        Assert.Equal("0", actual["InventoryInfo.TurnOverRate"]);
        Assert.Equal("true", actual["MrpInfo.IsControlByDC"]);
        var differences = U9MaterialCreationRules.Compare(Payload(Material("0102", MaterialKind.Standard, MaterialSupplyMode.Purchase)), actual);
        Assert.Contains(differences, error => error.Contains("MRPPlanningType"));
        Assert.Contains(differences, error => error.Contains("LotControlMode") && error.Contains("缺失"));
        Assert.DoesNotContain(differences, error => error.Contains("CostCurrency"));
    }

    [Fact]
    public void LegacyVirtualBom_ReconciliationOnlyCorrectsOutsourceExpectation()
    {
        var payload = Payload(Material("0201", MaterialKind.Product, MaterialSupplyMode.Manufacture), ProjectBomHeaderKind.Standard);
        using var document = JsonDocument.Parse(payload);
        var actual = U9MaterialCreationRules.ReadAttributes(document.RootElement[0]).ToDictionary();
        var legacy = payload.Replace("\"IsOutsideOperationEnable\": false", "\"IsOutsideOperationEnable\": true");
        Assert.Single(U9MaterialCreationRules.Compare(legacy, actual));
        Assert.Empty(U9MaterialCreationRules.Compare(legacy, actual, reconcileLegacyVirtualBom: true));
        actual["InventoryInfo.LotControlMode"] = "0";
        Assert.Contains(U9MaterialCreationRules.Compare(legacy, actual, true), error => error.Contains("LotControlMode"));
        actual["IsOutsideOperationEnable"] = null;
        Assert.Contains(U9MaterialCreationRules.Compare(legacy, actual, true), error => error.Contains("IsOutsideOperationEnable"));
        actual["IsOutsideOperationEnable"] = "true";
        Assert.Contains(U9MaterialCreationRules.Compare(legacy, actual, true), error => error.Contains("IsOutsideOperationEnable"));
    }

    [Theory]
    [InlineData("0102", MaterialSupplyMode.Purchase, null)]
    [InlineData("0302", MaterialSupplyMode.Manufacture, ProjectBomHeaderKind.Master)]
    public void LegacyReconciliation_DoesNotRelaxOtherTemplates(string category, MaterialSupplyMode supply, ProjectBomHeaderKind? header)
    {
        var payload = Payload(Material(category, MaterialKind.Product, supply), header);
        using var document = JsonDocument.Parse(payload);
        var actual = U9MaterialCreationRules.ReadAttributes(document.RootElement[0]).ToDictionary();
        actual["IsOutsideOperationEnable"] = "false";
        Assert.Contains(U9MaterialCreationRules.Compare(payload, actual, true), error => error.Contains("IsOutsideOperationEnable"));
    }

    private static string Payload(PdmMaterial material, ProjectBomHeaderKind? header = null) =>
        U9MaterialPayloadFactory.CreatePayload(material,
            new(material.Kind, material.CategoryCode!, "测试分类", material.SupplyMode, true, "test", DateTimeOffset.UtcNow),
            "7", "test-only", bomHeaderKind: header);

    private static PdmMaterial Material(string category, MaterialKind kind, MaterialSupplyMode supply) => new(
        Guid.NewGuid(), "TEST-NOT-WRITTEN", "测试", kind, supply, "001", null, null, null, null, null, null, null, null,
        MaterialApprovalStatus.Draft, null, null, category, null, null, MaterialSyncStatus.NotQueued,
        "test", DateTimeOffset.UtcNow, "test", DateTimeOffset.UtcNow, 1, category);
}
