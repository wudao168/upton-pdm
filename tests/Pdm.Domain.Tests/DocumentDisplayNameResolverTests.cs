using Upton.Pdm.Domain;

namespace Pdm.Domain.Tests;

public sealed class DocumentDisplayNameResolverTests
{
    [Fact]
    public void ResolvePrefersConfiguredSolidWorksProperty()
    {
        var properties = new Dictionary<string, string?>
        {
            ["全局/物料名称"] = "配置名称",
            ["全局/NT"] = "旧名称"
        };

        var result = DocumentDisplayNameResolver.Resolve("装配体-1/零件-1", "R70000050.02-01", properties, "物料名称");

        Assert.Equal("配置名称", result);
    }

    [Fact]
    public void ResolveUsesLegacyNtAndRejectsStoredInstancePath()
    {
        var properties = new Dictionary<string, string?>
        {
            ["全局/NT"] = "真空箱泵组安装架"
        };

        var result = DocumentDisplayNameResolver.Resolve("R70000050.02-1/R70000050.02-01-1", "R70000050.02-01", properties, "物料名称");

        Assert.Equal("真空箱泵组安装架", result);
    }

    [Fact]
    public void ResolveFallsBackToDrawingNumberInsteadOfInstancePath()
    {
        var result = DocumentDisplayNameResolver.Resolve("R70000050.02-1/R70000050.02-01-1", "R70000050.02-01", null, "物料名称");

        Assert.Equal("R70000050.02-01", result);
    }
}
