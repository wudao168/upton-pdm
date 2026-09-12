using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveProjectPlanTemplateCommand(
    string Name,
    string? ProjectTypeCode,
    bool IsActive,
    IReadOnlyList<ProjectPlanTemplateTask> Tasks,
    long? ExpectedRowVersion,
    IReadOnlyList<ProjectPlanStageDefinition>? Stages = null);

public sealed record GenerateProjectPlanCommand(
    Guid TemplateId,
    DateOnly StartDate,
    int TotalDurationDays,
    bool ReplaceExisting,
    string? ChangeReason,
    IReadOnlyList<ProjectPlanStageSchedule>? IndependentStages = null,
    IReadOnlyList<string>? DeferredStages = null);

public sealed record SaveProjectPlanCommand(
    IReadOnlyList<ProjectPlanTask> Tasks,
    string ChangeReason,
    long ExpectedRowVersion,
    IReadOnlyList<ProjectPlanStageDefinition>? Stages = null,
    bool CreateMissingFollowers = false);

public sealed record UpdateProjectPlanTaskProgressCommand(
    int CompletionPercent,
    DateOnly? ActualStart,
    DateOnly? ActualFinish,
    long ExpectedRowVersion);

public sealed record SubmitProjectPlanChangeCommand(IReadOnlyList<ProjectPlanScheduleChange> Tasks, string Reason, long ExpectedRowVersion);

public sealed record CompleteProjectPlanChangeCommand(long ExpectedRowVersion);

public sealed record SetProjectPlanStageCommand(
    string? Stage,
    string Reason,
    long ExpectedRowVersion);

public sealed record ReuseProjectPlanCommand(
    Guid SourceProjectId,
    IReadOnlyList<Guid> TargetProjectIds,
    bool ReplaceExisting,
    string ChangeReason);

public sealed record ProjectPlanWrite(ProjectPlan Plan, long? ExpectedRowVersion, ProjectPlanVersion? Version);

public interface IProjectPlanningRepository
{
    Task<IReadOnlyList<ProjectPlanTemplate>> ListTemplatesAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<ProjectPlanTemplate?> FindTemplateAsync(Guid templateId, CancellationToken cancellationToken);
    Task<ProjectPlanTemplate> SaveTemplateAsync(ProjectPlanTemplate template, long? expectedRowVersion, CancellationToken cancellationToken);
    Task<ProjectPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken, bool includeDeleted = false);
    Task<IReadOnlyList<ProjectPlan>> ListPlansAsync(IReadOnlyCollection<Guid>? projectIds, CancellationToken cancellationToken);
    Task<ProjectPlan> SavePlanAsync(ProjectPlan plan, long? expectedRowVersion, ProjectPlanVersion? version, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectPlan>> SavePlansAsync(IReadOnlyList<ProjectPlanWrite> writes, CancellationToken cancellationToken);
    Task DeletePlansAsync(IReadOnlyDictionary<Guid, long> expectedRowVersions, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectPlanVersion>> ListVersionsAsync(Guid planId, CancellationToken cancellationToken);
}
