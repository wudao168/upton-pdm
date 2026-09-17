using Upton.Pdm.SolidWorks;

namespace Pdm.Domain.Tests;

public sealed class DrawingQrLayoutRuleTests
{
    [Fact]
    public void CalculateOrigin_PlacesQrAboveTitleBlockAtLowerRight()
    {
        DrawingQrLayoutRule.CalculateOrigin(0.420d, 0.297d, 20, 5, out var x, out var y);

        Assert.Equal(0.395d, x, 6);
        Assert.Equal(0.050d, y, 6);
    }

    [Fact]
    public void CalculateOrigin_ClampsToSmallSheetBounds()
    {
        DrawingQrLayoutRule.CalculateOrigin(0.040d, 0.040d, 20, 5, out var x, out var y);

        Assert.Equal(0.015d, x, 6);
        Assert.Equal(0.015d, y, 6);
    }

    [Fact]
    public void CalculateOrigin_UsesCustomCoordinatesFromLowerRightAndKeepsQrOnSheet()
    {
        DrawingQrLayoutRule.CalculateOrigin(0.420d, 0.297d, 20d, 20d, 5d, true, 100d, 30d, out var x, out var y);

        Assert.Equal(0.300d, x, 6);
        Assert.Equal(0.030d, y, 6);
    }

    [Fact]
    public void CalculateOrigin_UsesConfiguredLengthAndWidth()
    {
        DrawingQrLayoutRule.CalculateOrigin(0.420d, 0.297d, 30d, 15d, 5d, true, 20d, 10d, out var x, out var y);

        Assert.Equal(0.370d, x, 6);
        Assert.Equal(0.010d, y, 6);
    }

    [Theory]
    [InlineData(1d, 2d, 2d)]
    [InlineData(2d, 1d, 0.5d)]
    [InlineData(0d, 2d, 1d)]
    public void PaperToSketchScale_CompensatesOnlyTheDrawingApiCoordinateSystem(
        double scaleNumerator,
        double scaleDenominator,
        double expected)
    {
        var result = DrawingQrLayoutRule.PaperToSketchScale(scaleNumerator, scaleDenominator);

        Assert.Equal(expected, result, 6);
    }

    [Fact]
    public void ToSketchDistance_PreservesRequestedPhysicalLengthAfterScaleCompensation()
    {
        Assert.Equal(0.06d, DrawingQrLayoutRule.ToSketchDistance(0.03d, 2d), 6);
    }
}
