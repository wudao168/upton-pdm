namespace Upton.Pdm.Domain;

public static class ReleasePreviewPolicy
{
    public static bool RequiresConversion(ReleasePackage package) =>
        ReleaseDrawingReview.Supports(package.Scope) || package.Scope == ReleaseScope.LegacyCombined && package.LocksDocuments;

    /// <summary>只有具备完整审批记录和冻结转图来源的失败包可以恢复发布。</summary>
    public static bool CanResumeFailedPackage(ReleasePackage package) =>
        package.State == ReleasePackageState.PublishFailed && package.PublishedAt is null
        && string.IsNullOrWhiteSpace(package.PublishedPath) && RequiresConversion(package)
        && package.ApprovalTasks.Count > 0 && package.ApprovalTasks.All(task =>
            task.ReleasePackageId == package.Id && task.Decision == ApprovalDecision.Approved && task.DecidedAt.HasValue)
        && package.VersionBindings?.Any(binding => binding.Publish || DeliversFrozenDrawings(package)) == true
        && (package.DrawingReview?.Items.Count > 0 || package.Scope == ReleaseScope.LegacyCombined && package.DrawingDocumentCount > 0)
        && (DeliversFrozenDrawings(package) || package.NonStandardBomVersionId.HasValue);

    public static bool ConflictsWithFailedRetry(ReleasePackage package, ReleaseScope scope, ReleasePackageState state,
        Guid? rootDocumentId, DateTimeOffset? publishedAt)
    {
        if (package.CreatesManufacturingBaseline && (scope is ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement
            or ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement))
            return state is ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing
                || state == ReleasePackageState.Published && publishedAt > package.ApprovalTasks.Max(task => task.DecidedAt);
        if (package.RootDocumentId.HasValue && rootDocumentId.HasValue && package.RootDocumentId != rootDocumentId) return false;
        var formalDrawingScope = scope is ReleaseScope.LegacyCombined or ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement;
        if (state == ReleasePackageState.Published && formalDrawingScope)
            return package.Scope is ReleaseScope.NonStandardLongLead or ReleaseScope.NonStandardWithDrawing
                || publishedAt > package.ApprovalTasks.Max(task => task.DecidedAt);
        return (formalDrawingScope || !DeliversFrozenDrawings(package) && scope == ReleaseScope.NonStandardLongLead)
            && state is ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing;
    }

    // 长交期的交付件按发布包绑定，源图仍可用于后续正式发布。
    public static bool DeliversFrozenDrawings(ReleasePackage package) => package.Scope == ReleaseScope.NonStandardLongLead;

    public static bool NeedsInitialBackfill(ReleasePackage package) =>
        DeliversFrozenDrawings(package) && package.PreviewState == ReleasePreviewState.None
        && package.VersionBindings is { Count: > 0 } && package.DrawingReview?.Items.Count > 0;
}
