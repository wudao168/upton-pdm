using System;
using Xunit;

namespace Upton.Pdm.Tests;

public sealed class BatchProjectRootRuleTests
{
    [Fact]
    public void ProjectWithoutRoot_AllowsFirstRoot()
    {
        Assert.Equal(
            string.Empty,
            Upton.Pdm.SolidWorks.BatchProjectRootRule.IncompatibilityReason(
                null,
                string.Empty,
                null,
                "80龟缸.SLDASM"));
    }

    [Fact]
    public void SameDocument_AllowsCheckIn()
    {
        var id = Guid.NewGuid();

        Assert.Equal(string.Empty, Upton.Pdm.SolidWorks.BatchProjectRootRule.IncompatibilityReason(id, "old.SLDASM", id, "renamed.SLDASM"));
    }

    [Fact]
    public void DifferentDocument_AllowsIndependentRoot()
    {
        var reason = Upton.Pdm.SolidWorks.BatchProjectRootRule.IncompatibilityReason(
            Guid.NewGuid(),
            "CW004-3.SLDASM",
            Guid.NewGuid(),
            "80龟缸.SLDASM");

        Assert.Equal(string.Empty, reason);
    }
}
