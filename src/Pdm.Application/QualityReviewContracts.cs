using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record QualityIssueProfile(Guid IssueId, string Domain, string Category, string Severity,
    DateOnly? OccurredOn, Guid? ThemeId, Guid? DuplicateOfIssueId, Guid? RecurrenceOfIssueId,
    string ImmediateAction, string RootCause, string CorrectiveAction, string VerificationResult,
    string EvidenceNote, IReadOnlyList<Guid> EvidenceAttachmentIds, string UpdatedBy, DateTimeOffset UpdatedAt)
{
    public string? VerifiedBy { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public long? VerifiedIssueRowVersion { get; init; }
    public string? CorrectiveActionBy { get; init; }
}
public sealed record QualityTheme(Guid Id, string Name, string Description, bool IsActive);
public sealed record QualityIssueRow(OplIssue Issue, QualityIssueProfile? Profile, Guid RootProjectId,
    string ProjectCode, string ProjectName, string OwnerName, string CreatedByName, bool CanOpenProject);
public sealed record QualityThemeMetric(Guid ThemeId, string Name, int EventCount, int ProjectCount,
    int MajorCount, bool IsCandidate, Guid? ActiveImprovementId, int UnknownDateCount);
public sealed record QualityPerson(string Username, string DisplayName);
public sealed record QualityProject(Guid Id, string Code, string Name, Guid RootProjectId);
public sealed record QualityCapabilities(bool CanManageIssues, bool CanManageThemes, bool CanManageImprovements,
    bool CanVerifyImprovements, bool CanPublishExperience, bool CanReviewSummaries)
{
    public bool CanApproveImprovements { get; init; }
    public bool CanExecuteImprovements { get; init; }
    public bool CanCloseImprovements { get; init; }
    public bool CanViewDetails { get; init; }
    public bool CanManageSettings { get; init; }
}
public sealed record QualityIssueSnapshot(Guid IssueId, int Number, Guid ProjectId, string ProjectCode,
    string Description, string Status, string OwnerName, string Solution, QualityIssueProfile? Profile, long SourceRowVersion);
public sealed record ProjectRetrospective(Guid Id, Guid ProjectId, int Revision, string Kind, string State,
    string Title, string Achievements, string Lessons, string RemainingPlan, string CreatedBy,
    DateTimeOffset CreatedAt, string? ReviewedBy, DateTimeOffset? PublishedAt,
    IReadOnlyList<QualityIssueSnapshot> Issues)
{
    public DateOnly? CoverageFrom { get; init; }
    public DateOnly? CoverageTo { get; init; }
    public string TargetResults { get; init; } = "";
}
public sealed record QualityAction(Guid Id, string Title, string Owner, DateOnly DueOn,
    bool Completed, string Evidence, string? CompletedBy = null, DateTimeOffset? CompletedAt = null);
public sealed record QualityObservation(Guid ProjectId, DateOnly AppliedOn, DateOnly ObservedOn,
    bool Passed, string Evidence, string RecordedBy, DateTimeOffset RecordedAt);
public sealed record QualityImprovementEvent(string Action, string Actor, string ActorName,
    DateTimeOffset At, string Note);
public sealed record QualityImprovement(Guid Id, Guid ThemeId, string Title, string State, string Owner,
    string RootCause, string Goal, DateOnly DueOn, int MinimumSamples, int ObservationDays,
    int MaximumFailures, IReadOnlyList<QualityAction> Actions, IReadOnlyList<QualityObservation> Observations,
    DateTimeOffset? ObservationStartedAt, string CreatedBy, DateTimeOffset CreatedAt,
    IReadOnlyList<QualityImprovementEvent> History, string? VerifiedBy = null, DateTimeOffset? ClosedAt = null)
{
    public IReadOnlyList<QualityImprovementCycle> PreviousCycles { get; init; } = [];
}
public sealed record QualityImprovementCycle(DateTimeOffset EndedAt, string Reason, string State,
    IReadOnlyList<QualityAction> Actions, IReadOnlyList<QualityObservation> Observations,
    DateTimeOffset? ObservationStartedAt, string? VerifiedBy, DateTimeOffset? ClosedAt,
    int MinimumSamples, int ObservationDays, int MaximumFailures);
public sealed record QualityExperience(Guid Id, Guid ImprovementId, string Title, string Content,
    string Applicability, string PublishedBy, DateTimeOffset PublishedAt, int Revision)
{
    public IReadOnlyList<string> ProjectTags { get; init; } = [];
    public IReadOnlyList<Guid> RecommendedProjectIds { get; init; } = [];
}
public sealed record QualityAdoption(Guid Id, Guid ExperienceId, Guid ProjectId, string Owner,
    DateOnly DueOn, string State, string Evidence, string? ConfirmedBy = null, DateTimeOffset? ConfirmedAt = null);
public sealed record QualityReviewCatalog(Guid CompanyId, long RowVersion,
    IReadOnlyList<QualityIssueProfile> Profiles, IReadOnlyList<QualityTheme> Themes,
    IReadOnlyList<ProjectRetrospective> Summaries, IReadOnlyList<QualityImprovement> Improvements,
    IReadOnlyList<QualityExperience> Experiences, IReadOnlyList<QualityAdoption> Adoptions)
{
    public QualityReviewSettings Settings { get; init; } = new(90, 3, 2);
}
public sealed record QualityReviewSettings(int WindowDays, int EventThreshold, int ProjectThreshold);
public sealed record SaveQualityReviewSettings(long ExpectedRowVersion, int WindowDays, int EventThreshold, int ProjectThreshold);
public sealed record QualitySettingsWorkspace(long RowVersion, QualityReviewSettings Settings);
public sealed record QualityReviewWorkspace(QualityReviewCatalog Catalog, IReadOnlyList<QualityIssueRow> Issues,
    IReadOnlyList<QualityThemeMetric> Metrics, IReadOnlyList<QualityProject> Projects,
    IReadOnlyList<QualityPerson> People, QualityCapabilities Capabilities, DateOnly WindowFrom, DateOnly WindowTo);
public sealed record QualityIssueWorkspace(long RowVersion, QualityIssueRow Row,
    IReadOnlyList<QualityTheme> Themes, IReadOnlyList<QualityPerson> People,
    bool CanManageIssues, bool CanVerifyIssues, bool CanCloseIssues, bool CanReopenIssues, bool CanVoidIssues)
{
    // Kept for older readers. Daily issue entry no longer enumerates or edits historical issue relationships.
    public IReadOnlyList<QualityIssueRow> RelatedIssues { get; init; } = [];
}
public sealed record RetrospectiveSource(string ProjectCode, bool CanEdit, bool CanReview, bool CanPublish);
public sealed record ProjectRetrospectiveWorkspace(Guid ProjectId, long RowVersion,
    IReadOnlyList<ProjectRetrospective> Summaries, IReadOnlyList<QualityIssueRow> Issues,
    IReadOnlyList<QualityExperience> Experiences, IReadOnlyList<QualityAdoption> Adoptions,
    bool CanEdit, bool CanReview, bool CanAdopt)
{
    public IReadOnlyDictionary<Guid, RetrospectiveSource> SummarySources { get; init; } = new Dictionary<Guid, RetrospectiveSource>();
    public IReadOnlyList<QualityTheme> Themes { get; init; } = [];
    public IReadOnlyList<QualityPerson> People { get; init; } = [];
    public bool CanPublish { get; init; }
    public bool CanViewIssues { get; init; }
    public bool CanManageIssues { get; init; }
    public bool CanVerifyIssues { get; init; }
    public bool CanCloseIssues { get; init; }
    public bool CanReopenIssues { get; init; }
    public bool CanVoidIssues { get; init; }
}
// Category and the two relationship identifiers are accepted only for wire compatibility.
// Existing stored values remain unchanged; newly classified issues start without those legacy fields.
public sealed record SaveQualityIssueProfile(long ExpectedRowVersion, string Domain, string Category,
    string Severity, DateOnly? OccurredOn, Guid? ThemeId, Guid? DuplicateOfIssueId, Guid? RecurrenceOfIssueId,
    string ImmediateAction, string RootCause, string CorrectiveAction, string VerificationResult,
    string EvidenceNote, IReadOnlyList<Guid> EvidenceAttachmentIds);
public sealed record SaveQualityTheme(long ExpectedRowVersion, string Name, string Description, bool IsActive);
public sealed record SaveProjectRetrospective(long ExpectedRowVersion, string Kind, string Title,
    string Achievements, string Lessons, string RemainingPlan)
{
    public DateOnly? CoverageFrom { get; init; }
    public DateOnly? CoverageTo { get; init; }
    public string TargetResults { get; init; } = "";
}
public sealed record QualityTransition(long ExpectedRowVersion, string Action, string Note = "");
public sealed record SaveQualityImprovement(long ExpectedRowVersion, Guid ThemeId, string Title, string Owner,
    string RootCause, string Goal, DateOnly DueOn, int MinimumSamples, int ObservationDays, int MaximumFailures,
    IReadOnlyList<QualityAction> Actions);
public sealed record SaveQualityObservation(long ExpectedRowVersion, Guid ProjectId, DateOnly AppliedOn,
    DateOnly ObservedOn, bool Passed, string Evidence);
public sealed record PublishQualityExperience(long ExpectedRowVersion, Guid ImprovementId,
    string Title, string Content, string Applicability)
{
    public IReadOnlyList<string> ProjectTags { get; init; } = [];
}
public sealed record AssignQualityAdoption(long ExpectedRowVersion, Guid ExperienceId,
    IReadOnlyList<Guid> ProjectIds, string Owner, DateOnly DueOn);
public sealed record ConfirmQualityAdoption(long ExpectedRowVersion, string State, string Evidence);
public interface IQualityReviewStore
{
    Task<QualityReviewCatalog> ReadAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveAsync(QualityReviewCatalog catalog, long expectedVersion, CancellationToken ct);
}
