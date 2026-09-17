using Xunit;

namespace Upton.Pdm.Tests;

public sealed class BatchCheckInDependencyRuleTests
{
    [Fact]
    public void DrawingFailure_DoesNotBlockParentAssembly()
    {
        Assert.False(Upton.Pdm.SolidWorks.BatchCheckInDependencyRule.BlocksParentAssembly(
            failedItemIsDrawing: true));
    }

    [Fact]
    public void ModelFailure_BlocksParentAssembly()
    {
        Assert.True(Upton.Pdm.SolidWorks.BatchCheckInDependencyRule.BlocksParentAssembly(
            failedItemIsDrawing: false));
    }
}
