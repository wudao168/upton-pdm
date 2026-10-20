using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record DepartmentPosition(Guid Id, string Name, bool IsActive = true);
public sealed record DepartmentPositionAssignment(Guid DepartmentId, Guid PositionId, IReadOnlyList<string> Usernames);
public sealed record WorkflowNotificationRecipient(string DepartmentSource, Guid? DepartmentId, Guid PositionId = default, string? RoleCode = null);
public sealed record WorkflowNotificationRule(string FlowCode, bool Enabled, IReadOnlyList<WorkflowNotificationRecipient> Recipients);
public sealed record WorkflowNotificationCatalog(Guid CompanyId, long RowVersion,
    IReadOnlyList<DepartmentPosition> Positions, IReadOnlyList<DepartmentPositionAssignment> Assignments,
    IReadOnlyList<WorkflowNotificationRule> Flows);

public sealed record SaveWorkflowNotification(bool Enabled, IReadOnlyList<WorkflowNotificationRecipient> Recipients, long ExpectedRowVersion, Guid CompanyId);
public sealed record WorkflowNoticeMessage(string Username, string Title, string Content, bool RoleRecipient = false);

public sealed record WorkflowNoticeJob
{
    public required Guid Id { get; init; }
    public required string SourceKey { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid BusinessId { get; init; }
    public required string BusinessKind { get; init; }
    public required string BusinessToken { get; init; }
    public required string Submitter { get; init; }
    public Guid? SubmitterDepartmentId { get; init; }
    public required WorkflowNotificationRule Rule { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset NextCheckAt { get; init; }
    public string State { get; init; } = "Waiting";
    public string Message { get; init; } = "等待业务完成";
    public string? NotificationSourceKey { get; init; }
    public IReadOnlyList<WorkflowNoticeMessage> Messages { get; init; } = [];
    public bool Incomplete { get; init; }
    public long RowVersion { get; init; }
}

public interface IWorkflowNotificationStore
{
    Task<WorkflowNotificationCatalog> ReadAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveAsync(WorkflowNotificationCatalog catalog, long expectedVersion, CancellationToken ct);
    Task CaptureAsync(WorkflowNoticeJob job, CancellationToken ct);
    Task<WorkflowNoticeJob?> FindJobAsync(Guid id, CancellationToken ct);
    Task<WorkflowNoticeJob?> FindSourceAsync(Guid companyId, string sourceKey, CancellationToken ct);
    Task<bool> SaveJobAsync(WorkflowNoticeJob job, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<WorkflowNoticeJob>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task<IReadOnlyList<WorkflowNoticeJob>> ListJobsAsync(Guid companyId, string flowCode, CancellationToken ct);
}

public static class WorkflowNotificationFlows
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["standard-release"] = "标准件BOM", ["nonstandard-release"] = "非标件BOM",
        ["electrical-release"] = "电气件BOM", ["validation-plan"] = "验证计划",
        ["material-code-application"] = "料号申请"
    };
    public static string ReleaseSourceKey(Guid id, string eventName) => $"release-notice:{id:N}:{eventName}";
}
