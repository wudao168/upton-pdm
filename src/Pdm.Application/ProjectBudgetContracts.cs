using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectBudgetLine(string Category, decimal? BudgetAmount = null,
    decimal? ActualAmount = null, decimal? RemainingAmount = null,
    decimal? PlannedHours = null, decimal? HourlyRate = null,
    decimal? ActualHours = null, decimal? ActualHourlyRate = null, decimal? RemainingHours = null,
    string? Note = null);

public sealed record ProjectBudget(Guid ProjectId, IReadOnlyList<ProjectBudgetLine> Lines,
    long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null,
    IReadOnlyDictionary<string, string>? OrderCategories = null, bool ManualActualAmounts = false, IReadOnlyList<BudgetAssessmentGroup>? AssessmentGroups = null, string? AssessmentUpdatedBy = null, DateTimeOffset? AssessmentUpdatedAt = null, IReadOnlyDictionary<string, decimal?>? AssessmentLaborRates = null, IReadOnlyList<ProjectBudgetNote>? Notes = null, IReadOnlyList<ProjectSettlementLine>? SettlementLines = null, decimal BonusRate = .005m);
public sealed record ProjectSettlementLine(string Content, string? Unit = null, decimal? Quantity = null, decimal? UnitPrice = null, string? Note = null);
public sealed record SaveProjectSettlementCommand(IReadOnlyList<ProjectSettlementLine> Lines, long ExpectedRowVersion);
public sealed record SaveProjectBonusRateCommand(decimal Rate, long ExpectedRowVersion);
public sealed record ProjectBudgetNote(Guid Id, string Category, string Content, string? CreatedBy, DateTimeOffset? CreatedAt);
public sealed record AddProjectBudgetNoteCommand(string Category, string Content, long ExpectedRowVersion);
public sealed record SaveProjectBudgetCommand(IReadOnlyList<ProjectBudgetLine> Lines, long ExpectedRowVersion,
    IReadOnlyDictionary<string, string>? OrderCategories = null);
public sealed record ProjectBudgetOrder(string Key, string DocumentNumber, int LineNumber, string MaterialCode,
    string ItemName, decimal? Amount, string Category);
public sealed record ProjectBudgetRow(ProjectBudgetLine Input, decimal? BudgetAmount, decimal? ActualAmount,
    decimal? OrderAmount, decimal? EstimatedAmount, decimal? Variance, string Alert);
public sealed record ProjectBudgetView(Guid ProjectId, IReadOnlyList<ProjectBudgetRow> Rows,
    decimal? BudgetAmount, decimal? ActualAmount, decimal? EstimatedAmount, decimal? OverrunAmount,
    bool CanEdit, bool CanViewReserve, long RowVersion, string? UpdatedBy, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProjectBudgetOrder> Orders, bool CanEditBudget = false, bool CanEditActual = false, IReadOnlyList<ProjectBudgetNote>? Notes = null, IReadOnlyList<ProjectSettlementLine>? SettlementLines = null, decimal? SettlementAmount = null, decimal? BonusRate = null, decimal? BonusAmount = null, bool CanEditSettlement = false, bool CanEditBonus = false, bool CanViewSettlement = false);

public sealed record AssessmentLaborRateSettings(IReadOnlyDictionary<string, decimal?> Rates, long RowVersion = 0, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null);

public interface IProjectBudgetRepository
{
    Task<AssessmentLaborRateSettings?> FindLaborRatesAsync(CancellationToken cancellationToken);
    Task<AssessmentLaborRateSettings> SaveLaborRatesAsync(AssessmentLaborRateSettings settings, long expectedRowVersion, CancellationToken cancellationToken);
    Task<ProjectBudget?> FindAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ProjectBudget> SaveAsync(ProjectBudget budget, long expectedRowVersion, CancellationToken cancellationToken);
}

public static class ProjectBudgetCategories
{
    public const string Reserve = "RiskReserve";
    public static readonly string[] Material = ["Standard", "Nonstandard", "Equipment", Reserve, "Logistics", "Other"];
    public static readonly string[] Labor = ["MechanicalDesign", "ElectricalDesign", "InternalCommissioning", "SiteElectrical", "SiteCommissioning"];
    public static readonly string[] All = [.. Material, .. Labor];
}
