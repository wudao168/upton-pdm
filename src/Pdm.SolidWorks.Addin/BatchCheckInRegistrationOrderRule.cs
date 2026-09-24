namespace Upton.Pdm.SolidWorks;

internal static class BatchCheckInRegistrationOrderRule
{
    public static int Priority(bool isDrawing) => isDrawing ? 1 : 0;
}
