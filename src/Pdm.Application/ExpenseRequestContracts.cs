using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ExpenseLine(string Kind, string? MaterialCode, string Name, string? Specification,
    string? Brand, string Unit, decimal? Quantity, decimal? UnitPrice, string? Note)
{
    public decimal? Amount => Quantity.HasValue && UnitPrice.HasValue
        ? decimal.Round(Quantity.Value * UnitPrice.Value, 2, MidpointRounding.AwayFromZero) : null;
}

public sealed record ExpenseApprovalStep(Guid Id, string Name, ApprovalGroup Participants,
    string? Decision = null, string? Actor = null, string? Comment = null, DateTimeOffset? DecidedAt = null);

public sealed record ExpenseRequest(Guid Id, Guid ProjectId, string ProjectCode, string ProjectName,
    string Applicant, string Purpose, string Department, DateOnly? RequiredOn, string Reason,
    IReadOnlyList<ExpenseLine> Lines, string Status, ApprovalWorkflowTemplate? Workflow,
    IReadOnlyList<ExpenseApprovalStep> Steps, long RowVersion, DateTimeOffset UpdatedAt, Guid? DepartmentId = null)
{
    public decimal? TotalAmount => Lines.Count > 0 && Lines.All(line => line.Amount.HasValue)
        ? Lines.Sum(line => line.Amount!.Value) : null;
    public ExpenseApprovalStep? CurrentStep => Status == "Pending" ? Steps.FirstOrDefault(step => step.Decision is null) : null;
}

public sealed record SaveExpenseRequest(string Purpose, string Department, DateOnly? RequiredOn, string Reason,
    IReadOnlyList<ExpenseLine> Lines, long ExpectedRowVersion, bool Submit);
public sealed record DecideExpenseRequest(long ExpectedRowVersion, bool Approve, string? Comment, string? BusinessNote = null);
public sealed record WithdrawExpenseRequest(long ExpectedRowVersion);
public sealed record ExpenseRequestView(ExpenseRequest Request, bool CanEdit, bool CanApprove, bool CanViewAmount = true, bool CanWithdraw = false);
public sealed record ExpenseDirectory(Guid ProjectId, string ProjectCode, string ProjectName, bool CanCreate,
    IReadOnlyList<ExpenseRequestView> Requests, IReadOnlyList<ExpenseDepartment> Departments);
public sealed record ExpenseDepartment(string Code, string Name, bool Configured);
public sealed record ExpenseRoutePreview(IReadOnlyList<string> Steps, decimal TotalAmount);

public interface IExpenseRequestRepository
{
    Task<ExpenseRequest?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ExpenseRequest>> ListAsync(Guid projectId, CancellationToken ct);
    Task<ExpenseRequest> SaveAsync(ExpenseRequest request, long expectedRowVersion,
        IReadOnlyList<UserNotification> notifications, CancellationToken ct);
}
