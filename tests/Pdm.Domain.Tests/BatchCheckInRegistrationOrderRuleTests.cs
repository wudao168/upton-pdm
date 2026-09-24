using Xunit;

namespace Upton.Pdm.Tests;

public sealed class BatchCheckInRegistrationOrderRuleTests
{
    [Fact]
    public void Model_RegistersBeforeDrawing()
    {
        Assert.True(Upton.Pdm.SolidWorks.BatchCheckInRegistrationOrderRule.Priority(false)
            < Upton.Pdm.SolidWorks.BatchCheckInRegistrationOrderRule.Priority(true));
    }
}
