using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class CadPropertyCardSnapshotTests
{
    [Fact]
    public void GlobalCardField_PreservesEmptyInsteadOfOldConfigurationValue()
    {
        var snapshot = new Dictionary<string, string?> { ["全局/材质"] = "", ["配置:默认/材质"] = "普通碳钢" };
        CadPropertyCardSnapshot.AddField(snapshot, "材质", false);
        Assert.Equal("", CadPropertyCardSnapshot.Read(snapshot, "默认", "材质"));
    }

    [Theory]
    [InlineData("A", "FESTO")]
    [InlineData("B", "")]
    [InlineData("Missing", "")]
    public void ConfigurationCardField_DoesNotFallBackToOtherConfigurationOrGlobal(string configuration, string expected)
    {
        var snapshot = new Dictionary<string, string?> { ["全局/品牌"] = "OLD", ["配置:A/品牌"] = "FESTO", ["配置:B/品牌"] = "" };
        CadPropertyCardSnapshot.AddField(snapshot, "品牌", true);
        Assert.Equal(expected, CadPropertyCardSnapshot.Read(snapshot, configuration, "品牌"));
    }

    [Fact]
    public void FieldsOutsideCard_AreNotUsedEvenWhenOldPropertiesExist()
    {
        var snapshot = new Dictionary<string, string?> { ["全局/NT"] = "旧名称", ["全局/物料名称"] = "" };
        CadPropertyCardSnapshot.AddField(snapshot, "物料名称", false);
        Assert.Null(CadPropertyCardSnapshot.Read(snapshot, "默认", "NT"));
        Assert.Equal("", CadPropertyCardSnapshot.Read(snapshot, "默认", "物料名称"));
    }

    [Fact]
    public void LegacySnapshot_KeepsScopePriorityUntilItsCardIsVerified()
    {
        var snapshot = new Dictionary<string, string?> { ["全局/型号"] = "GLOBAL", ["配置:A/型号"] = "CONFIG" };
        Assert.Equal("CONFIG", CadPropertyCardSnapshot.Read(snapshot, "A", "型号"));
    }

    [Fact]
    public void BothScopes_UsesPresentConfigurationEvenWhenEmpty()
    {
        var snapshot = new Dictionary<string, string?> { ["全局/品牌"] = "GLOBAL", ["配置:A/品牌"] = "" };
        CadPropertyCardSnapshot.AddField(snapshot, "品牌", false);
        CadPropertyCardSnapshot.AddField(snapshot, "品牌", true);
        Assert.Equal("", CadPropertyCardSnapshot.Read(snapshot, "A", "品牌"));
        Assert.Equal("GLOBAL", CadPropertyCardSnapshot.Read(snapshot, "B", "品牌"));
    }
}
