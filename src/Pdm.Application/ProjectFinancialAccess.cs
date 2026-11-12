using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectFinancialAccess(bool Budget, bool Actual, IReadOnlySet<Guid> Departments,
    bool? BudgetMaintenance = null, bool? ActualMaintenance = null, bool CanMaintainAssessment = false)
{
    public bool CanMaintainBudget => BudgetMaintenance ?? Budget;
    public bool CanMaintainActual => ActualMaintenance ?? Actual;
    public bool BudgetFor(Guid? department) => Budget || department.HasValue && Departments.Contains(department.Value);
    public bool ActualFor(Guid? department) => Actual || department.HasValue && Departments.Contains(department.Value);

    public ProjectBudgetView Filter(ProjectBudgetView value)
    {
        var rows = value.Rows.Where(row => BudgetFor(row.Input.DepartmentId) || ActualFor(row.Input.DepartmentId)).Select(row =>
        {
            var budget = BudgetFor(row.Input.DepartmentId);
            var actual = ActualFor(row.Input.DepartmentId);
            return row with {
                Input = row.Input with {
                    BudgetAmount = budget ? row.Input.BudgetAmount : null,
                    PlannedHours = budget ? row.Input.PlannedHours : null,
                    HourlyRate = budget ? row.Input.HourlyRate : null,
                    ActualAmount = actual ? row.Input.ActualAmount : null,
                    ActualHours = actual ? row.Input.ActualHours : null,
                    ActualHourlyRate = actual ? row.Input.ActualHourlyRate : null,
                    RemainingAmount = actual ? row.Input.RemainingAmount : null,
                    RemainingHours = actual ? row.Input.RemainingHours : null,
                    Note = budget && actual ? row.Input.Note : null
                },
                BudgetAmount = budget ? row.BudgetAmount : null,
                ActualAmount = actual ? row.ActualAmount : null,
                OrderAmount = actual ? row.OrderAmount : null,
                EstimatedAmount = actual ? row.EstimatedAmount : null,
                Variance = budget && actual ? row.Variance : null,
                Alert = budget && actual ? row.Alert : "Incomplete"
            };
        }).ToArray();
        var actualCategories = rows.Where(row => ActualFor(row.Input.DepartmentId)).Select(row => row.Input.Category).ToHashSet();
        return value with {
            Rows = rows,
            BudgetAmount = Sum(rows.Select(row => row.BudgetAmount)),
            ActualAmount = Sum(rows.Select(row => row.ActualAmount)),
            EstimatedAmount = Sum(rows.Select(row => row.EstimatedAmount)),
            OverrunAmount = Budget && Actual ? value.OverrunAmount : null,
            Orders = value.Orders.Where(order => actualCategories.Contains(order.Category)).ToArray(),
            Notes = Budget && Actual ? value.Notes : [],
            CanEditBudget = value.CanEditBudget && CanMaintainBudget,
            CanEditActual = value.CanEditActual && CanMaintainActual,
            CanEdit = value.CanEditBudget && CanMaintainBudget || value.CanEditActual && CanMaintainActual,
            CanViewBudget = Budget || Departments.Count > 0,
            CanViewActual = Actual || Departments.Count > 0,
            IsDepartmentScoped = !Budget && !Actual && Departments.Count > 0,
            Departments = Budget ? value.Departments : []
        };
    }

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        var items = values.ToArray();
        return items.Length > 0 && items.All(value => value.HasValue) ? items.Sum(value => value!.Value) : null;
    }
}

public static class ProjectFinancialAccessPolicy
{
    public sealed record Context(UserAccount? User, OrganizationDirectory Directory);
    public static async Task<Context> CreateContextAsync(IPdmRepository repository, string actor, CancellationToken ct) =>
        new(await repository.FindUserAsync(actor, ct), await repository.GetOrganizationDirectoryAsync(ct));

    public static async Task<ProjectFinancialAccess> ReadAsync(IPdmRepository repository, Project project, string actor, UserRole role, CancellationToken ct, Context? context = null)
    {
        var denied = new ProjectFinancialAccess(false, false, new HashSet<Guid>());
        if (!await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct)) return denied;
        var user = context is null ? await repository.FindUserAsync(actor, ct) : context.User;
        if (user is null || !user.IsActive) return denied;
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct);
        if (root is null || root.OrganizationId != project.OrganizationId) return denied;
        var directory = context?.Directory ?? await repository.GetOrganizationDirectoryAsync(ct);
        var companyUnits = directory.Units.Where(unit => unit.IsActive && unit.OrganizationId == project.OrganizationId).ToDictionary(unit => unit.Id);
        var authorization = await AuthorizationPolicy.CreateContextAsync(repository, project.Id, actor, ct);
        if (authorization is null) return denied;
        var settings = await repository.GetAuthorizationSettingsAsync(ct);
        var allowed = AuthorizationPolicy.Decisions(settings, authorization).Where(decision => decision.Allowed).Select(decision => decision.Permission).ToHashSet();
        bool Allows(string code) => allowed.Contains(code);
        var departmentRole = Allows(AuthorizationActions.FinancialDepartmentRead);
        var departments = new HashSet<Guid>();
        if (departmentRole)
            foreach (var membership in directory.Memberships.Where(member => Same(member.Username, actor)))
            {
                Guid? current = membership.UnitId;
                var visited = new HashSet<Guid>();
                while (current.HasValue && visited.Add(current.Value) && companyUnits.TryGetValue(current.Value, out var unit))
                {
                    if (unit.Kind == OrganizationUnitKind.Department) { departments.Add(unit.Id); break; }
                    current = unit.ParentUnitId;
                }
            }
        return new(Allows(AuthorizationActions.FinancialBudgetRead), Allows(AuthorizationActions.FinancialActualRead), departments,
            Allows(AuthorizationActions.FinancialBudgetMaintain), Allows(AuthorizationActions.FinancialActualMaintain), Allows(PermissionCodes.ProjectBudgetAssessmentEdit));
    }

    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
