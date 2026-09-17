using System;

namespace Upton.Pdm.SolidWorks;

internal static class DrawingQrLayoutRule
{
    private const double TitleBlockClearanceMillimeters = 45d;

    public static void CalculateOrigin(
        double sheetWidth,
        double sheetHeight,
        int sizeMillimeters,
        int marginMillimeters,
        out double x,
        out double y)
    {
        CalculateOrigin(
            sheetWidth,
            sheetHeight,
            sizeMillimeters,
            sizeMillimeters,
            marginMillimeters,
            false,
            0d,
            0d,
            out x,
            out y);
    }

    public static void CalculateOrigin(
        double sheetWidth,
        double sheetHeight,
        double lengthMillimeters,
        double widthMillimeters,
        double marginMillimeters,
        bool useCustomPosition,
        double xMillimeters,
        double yMillimeters,
        out double x,
        out double y)
    {
        var length = Math.Max(8d, lengthMillimeters) / 1000d;
        var width = Math.Max(8d, widthMillimeters) / 1000d;
        var margin = Math.Max(0, marginMillimeters) / 1000d;
        if (useCustomPosition)
        {
            x = Clamp(sheetWidth - length - xMillimeters / 1000d, 0d, Math.Max(0d, sheetWidth - length));
            y = Clamp(yMillimeters / 1000d, 0d, Math.Max(0d, sheetHeight - width));
            return;
        }
        x = Math.Max(margin, sheetWidth - margin - length);
        y = Math.Max(
            margin,
            Math.Min(sheetHeight - margin - width, TitleBlockClearanceMillimeters / 1000d + margin));
    }

    public static double PaperToSketchScale(double scaleNumerator, double scaleDenominator)
    {
        if (scaleNumerator <= 0d
            || scaleDenominator <= 0d
            || double.IsNaN(scaleNumerator)
            || double.IsInfinity(scaleNumerator)
            || double.IsNaN(scaleDenominator)
            || double.IsInfinity(scaleDenominator)) return 1d;
        return scaleDenominator / scaleNumerator;
    }

    public static double ToSketchDistance(double paperDistance, double paperToSketchScale) =>
        paperDistance * paperToSketchScale;

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsNaN(value) || double.IsInfinity(value) ? minimum : Math.Max(minimum, Math.Min(maximum, value));
}
