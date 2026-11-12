using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

// Templates are editable data. An application stores a deep, versioned snapshot at submission.
public sealed record BonusDepartment(string Key, string Name, Guid? UnitId = null, IReadOnlyList<string>? Reviewers = null);
public sealed record BonusScoringDepartment(string DepartmentKey, decimal Weight = 100);
public sealed record BonusIndicator(string Id, string Name, decimal MaxScore, decimal Weight, string Guidance,
    string Evidence, IReadOnlyList<BonusScoringDepartment> Departments);
public sealed record BonusRoleTemplate(string Id, string Name, string Phase, decimal SuggestedWeight, IReadOnlyList<BonusIndicator> Indicators);
public sealed record BonusApprovalNode(string Id, string Name, IReadOnlyList<string> DepartmentKeys, bool Parallel = false);
public sealed record ProjectBonusSettings(long Version, IReadOnlyList<BonusDepartment> Departments,
    IReadOnlyList<BonusRoleTemplate> Roles, IReadOnlyList<BonusIndicator> ProjectIndicators,
    IReadOnlyList<BonusApprovalNode> ApprovalNodes, string PaymentDepartmentKey = "finance", bool Enabled = false);
public sealed record BonusPerson(string Id, string Username, string RoleId, decimal Weight, string Reason);
public sealed record BonusEvidence(string IndicatorId, string PersonId, string Text);
public sealed record BonusScore(string IndicatorId, string PersonId, string DepartmentKey, decimal? Score,
    string Comment, string? Actor = null, DateTimeOffset? At = null);
public sealed record BonusScoringTask(string IndicatorId, string PersonId, string DepartmentKey, IReadOnlyList<string> Members);
public sealed record BonusAward(string PersonId, string Username, decimal Share, decimal RoleScore, decimal Amount);
public sealed record BonusAudit(string Actor, string Action, string Comment, DateTimeOffset At);
public sealed record BonusPayment(decimal Amount, DateOnly Date, string Voucher, string Actor, DateTimeOffset At);
public sealed record BonusApplication(string Phase, string Status, string Applicant, IReadOnlyList<BonusPerson> People,
    IReadOnlyList<BonusEvidence> Evidence, string MilestoneEvidence, ProjectBonusSettings? Template,
    IReadOnlyList<BonusScoringTask> ScoringTasks, IReadOnlyList<BonusScore> Scores, IReadOnlyList<ExpenseApprovalStep> Steps,
    IReadOnlyList<BonusAward> Awards, decimal? ProjectScore, IReadOnlyList<BonusAudit> Audit, BonusPayment? Payment = null);
public sealed record ProjectBonusBook(Guid ProjectId, long RowVersion, decimal? FrozenPool, IReadOnlyList<BonusApplication> Applications);
public sealed record BonusApplicationView(BonusApplication Application, bool CanEdit, bool CanSubmit, bool CanApprove,
    bool CanPay, IReadOnlyList<BonusScoringTask> MyScoringTasks);
public sealed record ProjectBonusView(Guid ProjectId, string ProjectCode, string ProjectName, long RowVersion,
    decimal? Pool, bool Frozen, bool CanCreate, string Actor, IReadOnlyList<BonusApplicationView> Applications,
    ProjectBonusSettings? Settings, IReadOnlyList<BonusUserOption> Users, bool CanViewHistory = false);
public sealed record BonusUserOption(string Username, string Name);
public sealed record SaveBonusDraft(long ExpectedRowVersion, IReadOnlyList<BonusPerson> People,
    IReadOnlyList<BonusEvidence> Evidence, string MilestoneEvidence, bool StartScoring);
public sealed record BonusAction(long ExpectedRowVersion, string Comment = "");
public sealed record SaveBonusScores(long ExpectedRowVersion, IReadOnlyList<BonusScore> Scores);
public sealed record BonusPaymentCommand(long ExpectedRowVersion, decimal Amount, DateOnly Date, string Voucher);
public sealed record BonusSettingsView(ProjectBonusSettings Settings, bool CanEdit, IReadOnlyList<BonusUserOption> Users,
    IReadOnlyList<ProjectBudgetDepartment> Departments);
public interface IProjectBonusRepository
{
    Task<ProjectBonusSettings?> GetSettingsAsync(Guid companyId, CancellationToken ct);
    Task<ProjectBonusSettings> SaveSettingsAsync(Guid companyId, ProjectBonusSettings settings, long expectedVersion, CancellationToken ct);
    Task<ProjectBonusBook?> FindAsync(Guid projectId, CancellationToken ct);
    Task<ProjectBonusBook> SaveAsync(ProjectBonusBook book, long expectedVersion, IReadOnlyList<UserNotification> notifications, CancellationToken ct);
    Task<IReadOnlyList<ProjectBonusBook>> HistoryAsync(Guid projectId, CancellationToken ct);
}
