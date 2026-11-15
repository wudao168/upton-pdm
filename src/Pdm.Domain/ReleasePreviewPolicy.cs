namespace Upton.Pdm.Domain;

public static class ReleasePreviewPolicy
{
    public static bool RequiresConversion(ReleasePackage package) =>
        ReleaseDrawingReview.Supports(package.Scope) || package.Scope == ReleaseScope.LegacyCombined && package.LocksDocuments;

    // 长交期的交付件按发布包绑定，源图仍可用于后续正式发布。
    public static bool DeliversFrozenDrawings(ReleasePackage package) => package.Scope == ReleaseScope.NonStandardLongLead;

    public static bool NeedsInitialBackfill(ReleasePackage package) =>
        DeliversFrozenDrawings(package) && package.PreviewState == ReleasePreviewState.None
        && package.VersionBindings is { Count: > 0 } && package.DrawingReview?.Items.Count > 0;
}
