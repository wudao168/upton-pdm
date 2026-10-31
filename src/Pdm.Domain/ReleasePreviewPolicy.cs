namespace Upton.Pdm.Domain;

public static class ReleasePreviewPolicy
{
    public static bool RequiresConversion(ReleasePackage package) => package.LocksDocuments
        && package.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.LegacyCombined;
}
