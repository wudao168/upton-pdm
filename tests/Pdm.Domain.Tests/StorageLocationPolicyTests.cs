using Upton.Pdm.Application;

namespace Upton.Pdm.Domain.Tests;

public sealed class StorageLocationPolicyTests
{
    [Fact]
    public void ResolveUnder_KeepsProjectInsideConfiguredVault()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upton-pdm-vault"));

        var result = StorageLocationPolicy.ResolveUnder(root, "PRJ-2026-018");

        Assert.StartsWith(root, result, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("PRJ-2026-018", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\OUTSIDE")]
    [InlineData("C:\\OUTSIDE")]
    public void ResolveUnder_RejectsPathOutsideVault(string relativePath)
    {
        Assert.Throws<PdmRuleException>(() => StorageLocationPolicy.ResolveUnder("D:\\PDM\\Vault", relativePath));
    }
}
