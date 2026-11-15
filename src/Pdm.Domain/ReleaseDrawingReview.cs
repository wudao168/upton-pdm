namespace Upton.Pdm.Domain;

/// <summary>本发布包的图纸审核资料；旧在途包为空，继续原流程。</summary>
public sealed record ReleaseDrawingReview(int Revision, IReadOnlyList<ReleaseDrawingReviewItem> Items)
{
    public Guid RoundId { get; init; }
    public bool NodeDriven { get; init; } = true;
    public IReadOnlyList<ReleaseDrawingReviewItem> ArchivedItems { get; init; } = [];
    public static bool HasIssue(ReleaseDrawingReview review, ReleaseDrawingReviewItem item, Guid taskId) =>
        item.Notes.Where(note => note.ApprovalTaskId == taskId && note.RoundId == review.RoundId && note.Token == item.Token)
            .GroupBy(note => note.Actor, StringComparer.OrdinalIgnoreCase)
            .Any(notes => notes.Last().Status == "Issue");

    public static bool Supports(ReleaseScope scope) => scope is ReleaseScope.NonStandardWithDrawing
        or ReleaseScope.NonStandardLongLead or ReleaseScope.NonStandardSupplement;
    public static ApprovalTask? ReviewTask(ReleasePackage package) => package.ApprovalTasks.OrderBy(t => t.StepOrder)
        .FirstOrDefault(t => t.Stage == ApprovalStage.MainDesigner)
        ?? package.ApprovalTasks.OrderBy(t => t.StepOrder).FirstOrDefault(t => t.Stage != ApprovalStage.MechanicalEngineer)
        ?? package.ApprovalTasks.OrderBy(t => t.StepOrder).FirstOrDefault();
}

public sealed record ReleaseDrawingReviewItem(Guid DocumentId, Guid VersionId, string Sha256, string FileName,
    string Token, IReadOnlyList<Guid> BomItemIds, bool Required, string Status,
    IReadOnlyList<ReleaseDrawingReviewNote> Notes)
{
    public DocumentPreviewArtifact? Preview { get; init; }
}

public sealed record ReleaseDrawingReviewNote(string Status, string Comment, string Actor, string ActorName,
    DateTimeOffset At, Guid VersionId)
{
    public Guid ApprovalTaskId { get; init; }
    public Guid RoundId { get; init; }
    public string? Token { get; init; }
}

public sealed record ReleaseDrawingReviewDecision(Guid DocumentId, string Token, string Status, string Comment)
{
    public string? EmergencyReason { get; init; }
    public Guid? ApprovalTaskId { get; init; }
    public Guid? RoundId { get; init; }
    public string? BusinessNote { get; init; }
}

public sealed record ReleaseDrawingNodeDecision(Guid ApprovalTaskId, ApprovalDecision Decision, string Comment)
{
    public Guid? RoundId { get; init; }
    public string? EmergencyReason { get; init; }
    public string? BusinessNote { get; init; }
}
