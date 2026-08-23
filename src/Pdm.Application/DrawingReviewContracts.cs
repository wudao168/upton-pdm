using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record AddDrawingReviewMarkupCommand(
    Guid ItemId,
    DrawingReviewTarget Target,
    string? ViewName,
    decimal? NormalizedX,
    decimal? NormalizedY,
    string Text,
    DrawingReviewMarkupSeverity Severity);

public sealed record DecideDrawingReviewTargetCommand(
    DrawingReviewTarget Target,
    DrawingReviewDecision Decision,
    string? Comment);
