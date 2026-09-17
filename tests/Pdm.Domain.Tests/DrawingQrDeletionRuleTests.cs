using Xunit;

namespace Upton.Pdm.Tests;

public sealed class DrawingQrDeletionRuleTests
{
    [Fact]
    public void TryDelete_FallsBackToPlainDelete()
    {
        var plainCalls = 0;
        var editCalls = 0;

        var result = Upton.Pdm.SolidWorks.DrawingQrDeletionRule.TryDelete(
            () => false,
            () => { plainCalls++; return true; },
            () => { editCalls++; return true; });

        Assert.True(result);
        Assert.Equal(1, plainCalls);
        Assert.Equal(0, editCalls);
    }

    [Fact]
    public void TryDelete_FallsBackToEditDelete()
    {
        var result = Upton.Pdm.SolidWorks.DrawingQrDeletionRule.TryDelete(
            () => false,
            () => false,
            () => true);

        Assert.True(result);
    }
}
