namespace Upton.Pdm.Domain;

public enum DrawingReviewPackageState
{
    InReview,
    ChangesRequested,
    WritingProperties,
    Approved,
    Stale
}

public enum DrawingReviewTarget
{
    Model3D,
    Drawing2D
}

public enum DrawingReviewTargetState
{
    Pending,
    ChangesRequested,
    Approved,
    Marked
}

public enum DrawingReviewDecision
{
    Approve,
    RequestChanges
}

public enum DrawingReviewMarkupSeverity
{
    Note,
    Blocking
}

public enum DrawingReviewMarkupState
{
    Open,
    Resolved
}

public sealed record DrawingReviewPackage
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Number { get; init; }

    public required DrawingReviewPackageState State { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }

    public IReadOnlyList<DrawingReviewItem> Items { get; init; } = [];

    public IReadOnlyList<DrawingReviewMarkup> Markups { get; init; } = [];
}

public sealed record DrawingReviewItem
{
    public required Guid Id { get; init; }

    public required Guid PackageId { get; init; }

    public required Guid BomItemId { get; init; }

    public required string DrawingNumber { get; init; }

    public required string Name { get; init; }

    public string? Configuration { get; init; }

    public required Guid ModelDocumentId { get; init; }

    public required Guid ModelVersionId { get; init; }

    public required string ModelRevision { get; init; }

    public required string ModelSha256 { get; init; }

    public required string ModelCreatedBy { get; init; }

    public required Guid DrawingDocumentId { get; init; }

    public required Guid DrawingVersionId { get; init; }

    public required string DrawingRevision { get; init; }

    public required string DrawingSha256 { get; init; }

    public required string DrawingCreatedBy { get; init; }

    public DrawingReviewTargetState ModelState { get; init; } = DrawingReviewTargetState.Pending;

    public string? ModelReviewer { get; init; }

    public string? ModelReviewerName { get; init; }

    public DateTimeOffset? ModelReviewedAt { get; init; }

    public string? ModelComment { get; init; }

    public DrawingReviewTargetState DrawingState { get; init; } = DrawingReviewTargetState.Pending;

    public string? DrawingReviewer { get; init; }

    public string? DrawingReviewerName { get; init; }

    public DateTimeOffset? DrawingReviewedAt { get; init; }

    public string? DrawingComment { get; init; }

    public Guid? ModelWritebackId { get; init; }

    public Guid? DrawingWritebackId { get; init; }

    public Guid? ModelResultVersionId { get; init; }

    public Guid? DrawingResultVersionId { get; init; }

    public Guid EffectiveModelVersionId => ModelResultVersionId ?? ModelVersionId;

    public Guid EffectiveDrawingVersionId => DrawingResultVersionId ?? DrawingVersionId;
}

public sealed record DrawingReviewMarkup
{
    public required Guid Id { get; init; }

    public required Guid PackageId { get; init; }

    public required Guid ItemId { get; init; }

    public required DrawingReviewTarget Target { get; init; }

    public string? ViewName { get; init; }

    public decimal? NormalizedX { get; init; }

    public decimal? NormalizedY { get; init; }

    public required string Text { get; init; }

    public required DrawingReviewMarkupSeverity Severity { get; init; }

    public required DrawingReviewMarkupState State { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? ResolvedBy { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }
}

public sealed record DrawingReviewWritebackRequest(
    Guid ItemId,
    CadPropertyWriteback Model,
    CadPropertyWriteback Drawing);
