using System.Text.Json.Serialization;

namespace Upton.Pdm.Domain;

public static class ProjectPlanStage
{
    public const string Design = "Design";
    public const string MaterialPreparation = "MaterialPreparation";
    public const string Assembly = "Assembly";
    public const string Commissioning = "Commissioning";
    public const string ClientCommissioning = "ClientCommissioning";
    public const string AcceptanceProgress = "AcceptanceProgress";
    public const string FinalAcceptance = "FinalAcceptance";
    public const string Paused = "Paused";
    public const string Cancelled = "Cancelled";
    public const string Terminated = "Terminated";
    public static IReadOnlyList<ProjectPlanStageDefinition> Defaults { get; } =
    [
        new(Design, "设计"), new(MaterialPreparation, "备料"), new(Assembly, "装配"),
        new(Commissioning, "调试"), new(ClientCommissioning, "客户端调试"),
        new(AcceptanceProgress, "验收推进"), new(FinalAcceptance, "终验收")
    ];
}

public sealed record ProjectPlanStageDefinition(string Code, string Name)
{
    // null identifies legacy snapshots; their scheduling and progress remain unchanged.
    public bool? ParticipatesInDelivery { get; init; }
    public decimal DurationRatio { get; init; }
    public decimal ProgressRatio { get; init; }
    public int IndependentDurationDays { get; init; }
}

public sealed record ProjectPlanStageSchedule(string Stage, DateOnly StartDate, int DurationDays);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectPlanTaskStatus
{
    NotStarted,
    InProgress,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectPlanApprovalStatus { Draft, Pending, Rejected, Approved }

public sealed record ProjectPlanTemplateTask(
    Guid Id,
    string Name,
    string Stage,
    decimal DurationRatio,
    IReadOnlyList<int> PredecessorSortOrders,
    string? DefaultAssigneeRole,
    decimal Weight,
    bool IsMilestone,
    bool IsRequired,
    int SortOrder)
{
    public int StartOffsetDays { get; init; }
    public int? FixedDurationDays { get; init; }
}

public sealed record ProjectPlanTemplate(
    Guid Id,
    string Name,
    string? ProjectTypeCode,
    bool IsActive,
    IReadOnlyList<ProjectPlanTemplateTask> Tasks,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    public IReadOnlyList<ProjectPlanStageDefinition> Stages { get; init; } = ProjectPlanStage.Defaults;
}

public sealed record ProjectPlanTask(
    Guid Id,
    string Name,
    string Stage,
    string? Assignee,
    int DurationDays,
    DateOnly PlannedStart,
    DateOnly PlannedFinish,
    DateOnly? BaselineStart,
    DateOnly? BaselineFinish,
    DateOnly? ActualStart,
    DateOnly? ActualFinish,
    int CompletionPercent,
    ProjectPlanTaskStatus Status,
    IReadOnlyList<Guid> PredecessorTaskIds,
    decimal Weight,
    bool IsMilestone,
    bool IsRequired,
    int SortOrder)
{
    public Guid? TemplateTaskId { get; init; }
    public Guid? SourceTaskId { get; init; }
}

public sealed record ProjectPlanSyncResult(Guid ProjectId, string ProjectCode, string Result, IReadOnlyList<string> Differences);

public sealed record ProjectPlanScheduleChange(Guid TaskId, DateOnly PlannedStart, DateOnly PlannedFinish, string? Assignee);
public sealed record ProjectPlanChangeRequest(Guid Id, IReadOnlyList<ProjectPlanScheduleChange> Tasks, string Reason,
    string SubmittedBy, DateTimeOffset SubmittedAt, string ApprovalAssignee, ProjectPlanApprovalStatus Status,
    string? DecidedBy = null, DateTimeOffset? DecidedAt = null, string? Comment = null);

public sealed record ProjectPlan(
    Guid Id,
    Guid ProjectId,
    Guid TemplateId,
    string TemplateName,
    string CurrentStage,
    string? ManualStage,
    string? ManualStageReason,
    DateOnly PlannedStart,
    DateOnly PlannedFinish,
    DateOnly ForecastFinish,
    int BaselineVersion,
    IReadOnlyList<ProjectPlanTask> Tasks,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    public bool FollowsParentPlan { get; init; }
    public ProjectPlanChangeRequest? ChangeRequest { get; init; }
    // Present only while an approved change permission is being used. The nested
    // snapshot remains the effective plan until the draft is explicitly completed.
    public ProjectPlan? ChangeDraftSource { get; init; }
    public Guid? ParentPlanId { get; init; }
    public long? ParentPlanRowVersion { get; init; }
    public IReadOnlyList<ProjectPlanSyncResult> ChildSyncResults { get; init; } = [];
    public IReadOnlyList<ProjectPlanStageSchedule> StageSchedules { get; init; } = [];
    public IReadOnlyList<ProjectPlanStageDefinition> Stages { get; init; } = ProjectPlanStage.Defaults;
    public ProjectPlanApprovalStatus ApprovalStatus { get; init; } = ProjectPlanApprovalStatus.Draft;
    public string? ApprovalAssignee { get; init; }
    public string? SubmittedBy { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovalComment { get; init; }
    public bool IsDeleted { get; init; }
}

public sealed record ProjectPlanVersion(
    Guid Id,
    Guid PlanId,
    int VersionNumber,
    string ChangeReason,
    ProjectPlan Snapshot,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record ProjectPlanPortfolioItem(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    bool IsRoot,
    bool HasPlan,
    string? CurrentStage,
    int CompletionPercent,
    DateOnly? PlannedStart,
    DateOnly? PlannedFinish,
    DateOnly? ForecastFinish,
    bool IsLagging,
    bool IsAtRisk,
    ProjectPlan? Plan);

public sealed record ProjectPlanPortfolio(
    Guid RootProjectId,
    string CurrentStage,
    int CompletionPercent,
    int LaggingProjectCount,
    int RiskProjectCount,
    DateOnly? PlannedStart,
    DateOnly? PlannedFinish,
    IReadOnlyList<ProjectPlanPortfolioItem> Projects);
