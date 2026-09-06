using Upton.Pdm.SolidWorks;
using Xunit;

namespace Pdm.Domain.Tests;

public sealed class BatchPropertyNameModelAutoFillRuleTests
{
    [Fact]
    public void PureChinese_FillsMaterialNameOnly()
    {
        Assert.Equal(["物料名称"], BatchPropertyNameModelAutoFillRule.TargetPropertyNames("透明管"));
    }

    [Fact]
    public void PureEnglishAndDigits_FillsModelOnly()
    {
        Assert.Equal(["型号"], BatchPropertyNameModelAutoFillRule.TargetPropertyNames("HG-10-16MM"));
    }

    [Fact]
    public void ChineseWithEnglishOrDigits_FillsMaterialNameAndModel()
    {
        Assert.Equal(
            ["物料名称", "型号"],
            BatchPropertyNameModelAutoFillRule.TargetPropertyNames("PC透明管外径15X9"));
    }

    [Fact]
    public void ChineseWithPunctuation_DoesNotCountAsMixedContent()
    {
        Assert.Equal(["物料名称"], BatchPropertyNameModelAutoFillRule.TargetPropertyNames("透明管-外径"));
    }
}
