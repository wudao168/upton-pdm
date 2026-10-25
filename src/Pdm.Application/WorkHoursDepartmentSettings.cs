using Upton.Pdm.Domain;
using System.Globalization;

namespace Upton.Pdm.Application;

public sealed record WorkHoursDepartmentPeriod(Guid UnitId, DateOnly From, DateOnly? To);
public sealed record WorkHoursDepartmentPerson(string Username, string DisplayName, string Department, bool IsActive, bool Required, bool CanEdit);
public sealed record WorkHoursDepartmentGroup(Guid Id, string Name, bool Participating, IReadOnlyList<WorkHoursDepartmentPerson> People);
public sealed record WorkHoursDepartmentChange(Guid UnitId, bool Participating);
public sealed record WorkHoursPersonChange(string Username, bool Required);
public sealed record SaveWorkHoursGroupSettings(Guid CompanyId, long RowVersion,
    IReadOnlyList<WorkHoursDepartmentChange> Departments, IReadOnlyList<WorkHoursPersonChange> People);

public sealed partial class WorkHoursService
{
    private static Guid RootUnit(Guid id, IReadOnlyList<OrganizationUnit> units)
    {
        var seen = new HashSet<Guid> { id };
        while (units.FirstOrDefault(x => x.Id == id)?.ParentUnitId is Guid parent && units.Any(x => x.Id == parent) && seen.Add(parent)) id = parent;
        return id;
    }
    private static OrganizationUnit? PrimaryUnit(string username, OrganizationDirectory directory, Guid company)
        => directory.Memberships.Where(x => Same(x.Username, username)).OrderByDescending(x => x.IsPrimary)
            .Select(x => directory.Units.FirstOrDefault(u => u.Id == x.UnitId && u.OrganizationId == company)).FirstOrDefault(x => x is not null);
    private static Guid? PersonRoot(string username, OrganizationDirectory directory, Guid company)
        => PrimaryUnit(username, directory, company) is { } unit ? RootUnit(unit.Id, directory.Units.Where(x => x.OrganizationId == company).ToArray()) : null;
    private static bool RequiredNow(WorkHoursCatalog catalog, string username, DateOnly date)
        => catalog.Requirements.Any(x => Same(x.Username, username) && x.From <= date && (x.To is null || x.To >= date));
    private static bool DepartmentParticipates(WorkHoursCatalog catalog, Guid? unit, DateOnly date)
        => catalog.DepartmentScopeFrom is null || date < catalog.DepartmentScopeFrom
            || unit is Guid id && catalog.Departments.Any(x => x.UnitId == id && x.From <= date && (x.To is null || x.To >= date));
    private bool DepartmentEnabled(WorkHoursCatalog catalog, Guid? unit, OrganizationDirectory directory)
    {
        if (unit is not Guid id) return false;
        return catalog.DepartmentScopeFrom is not null
            ? catalog.Departments.Any(x => x.UnitId == id && x.From <= Today && (x.To is null || x.To >= Today))
            : catalog.Requirements.Any(x => RequiredNow(catalog, x.Username, Today) && PersonRoot(x.Username, directory, catalog.CompanyId) == id);
    }
    private IReadOnlyList<OrganizationUnit> DepartmentRoots(OrganizationDirectory directory, Guid company)
    {
        var units = directory.Units.Where(x => x.OrganizationId == company && x.IsActive).ToArray();
        return units.Where(x => x.ParentUnitId is null || !units.Any(u => u.Id == x.ParentUnitId))
            .OrderBy(x => x.Code, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), false)).ToArray();
    }
    private IReadOnlyList<WorkHoursDepartmentGroup> DepartmentGroups(OrganizationDirectory directory, WorkHoursCatalog catalog, ManagementAccess personnel)
    {
        var users = directory.Users.Where(x => InCompany(x, catalog.CompanyId) && x.IsActive).ToArray();
        var roots = users.ToDictionary(x => x.Username, x => PersonRoot(x.Username, directory, catalog.CompanyId), StringComparer.OrdinalIgnoreCase);
        return DepartmentRoots(directory, catalog.CompanyId).Select(unit => new WorkHoursDepartmentGroup(unit.Id, unit.Name,
            DepartmentEnabled(catalog, unit.Id, directory), users.Where(x => roots[x.Username] == unit.Id)
                .Select(x => new WorkHoursDepartmentPerson(x.Username, x.DisplayName, Person(x, directory, catalog.CompanyId).Department,
                    x.IsActive, RequiredNow(catalog, x.Username, Today), personnel.Names.Contains(x.Username)))
                .OrderBy(x => x.Department).ThenBy(x => x.DisplayName).ToArray())).ToArray();
    }
    private WorkHoursCatalog InitializeDepartmentScope(WorkHoursCatalog catalog, OrganizationDirectory directory)
        => catalog.DepartmentScopeFrom is not null ? catalog : catalog with
        {
            DepartmentScopeFrom = Today,
            Departments = DepartmentRoots(directory, catalog.CompanyId).Where(x => DepartmentEnabled(catalog, x.Id, directory))
                .Select(x => new WorkHoursDepartmentPeriod(x.Id, Today, null)).ToArray()
        };
    private IReadOnlyList<WorkHoursRequirement> ChangeRequired(IReadOnlyList<WorkHoursRequirement> source, string username, bool required)
    {
        var result = source.ToList();
        foreach (var existing in result.Where(x => Same(x.Username, username) && (x.To is null || x.To >= Today)).ToArray())
        {
            result.Remove(existing);
            if (existing.From < Today) result.Add(existing with { To = Today.AddDays(-1) });
        }
        if (required) result.Add(new(Guid.NewGuid(), username, Today, null, 8));
        return result;
    }
    public async Task<WorkHoursManagementView> SaveGroupSettingsAsync(SaveWorkHoursGroupSettings input, string actor, CancellationToken ct)
    {
        var (company, directory, catalog, access) = await Management(actor, ct, true);
        if (input.CompanyId != company) throw new PdmConflictException("当前公司已切换，请刷新工时设置后重新保存。");
        if (input.Departments is null || input.People is null || input.Departments.Count > 500 || input.People.Count > 2000
            || input.Departments.Select(x => x.UnitId).Distinct().Count() != input.Departments.Count
            || input.People.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.People.Count)
            throw new PdmRuleException("填报范围资料不完整或存在重复，请刷新后重新勾选。");
        if (input.RowVersion != catalog.RowVersion) throw new PdmConflictException("工时设置已被其他人更新，请刷新后重新勾选；本次没有覆盖原设置。");
        if (input.Departments.Count > 0 && !access.Admin) throw new UnauthorizedAccessException("只有管理员可以修改参与工时填报的部门。");
        var personnel = ManagementScope(company, directory, directory.Users.First(x => Same(x.Username, actor)), catalog, true);
        var roots = DepartmentRoots(directory, company);
        if (input.Departments.Any(x => !roots.Any(u => u.Id == x.UnitId))) throw new PdmRuleException("所选部门已停用或不属于当前公司，请刷新后重试。");
        foreach (var change in input.People)
        {
            CheckTarget(change.Username, personnel);
            if (!directory.Users.Any(x => Same(x.Username, change.Username) && x.IsActive && InCompany(x, company)))
                throw new PdmRuleException("所选人员已停用或不属于当前公司，请刷新人员名单。");
            if (PersonRoot(change.Username, directory, company) is not Guid root || !roots.Any(x => x.Id == root))
                throw new PdmRuleException("所选人员尚未分配有效部门，请先在组织架构中分配部门。");
        }
        if (input.Departments.Count + input.People.Count == 0) return await ReadManagementAsync(actor, ct);
        var after = InitializeDepartmentScope(catalog, directory);
        var periods = after.Departments.ToList();
        foreach (var change in input.Departments)
        {
            var enabled = periods.Any(x => x.UnitId == change.UnitId && x.From <= Today && (x.To is null || x.To >= Today));
            if (enabled == change.Participating) continue;
            foreach (var previous in periods.Where(x => x.UnitId == change.UnitId && (x.To is null || x.To >= Today)).ToArray())
            {
                periods.Remove(previous);
                if (previous.From < Today) periods.Add(previous with { To = Today.AddDays(-1) });
            }
            if (change.Participating) periods.Add(new(change.UnitId, Today, null));
        }
        after = after with { Departments = periods };
        foreach (var change in input.People)
        {
            if (!DepartmentEnabled(after, PersonRoot(change.Username, directory, company), directory))
                throw new PdmRuleException("该人员所在部门尚未参与工时填报，请先由管理员启用部门。");
            if (RequiredNow(after, change.Username, Today) != change.Required)
                after = after with { Requirements = ChangeRequired(after.Requirements, change.Username, change.Required) };
        }
        await SaveCatalog(catalog, after, input.RowVersion, actor, "调整部门及应填人员", "按部门分组勾选填报范围，保存当日起生效。", ct);
        foreach (var user in directory.Users.Where(x => InCompany(x, company) && (!RequiredNow(after, x.Username, Today) || !DepartmentEnabled(after, PersonRoot(x.Username, directory, company), directory))))
            await repository.ResolveWorkHoursNotificationsAsync(user.Username, company, clock.GetUtcNow(), ct);
        return await ReadManagementAsync(actor, ct);
    }
}
