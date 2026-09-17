namespace Upton.Pdm.SolidWorks;

internal static class BatchCheckInDependencyRule
{
    public static bool BlocksParentAssembly(bool failedItemIsDrawing) =>
        !failedItemIsDrawing;
}
