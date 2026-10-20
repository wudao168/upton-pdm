using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record WorkHourInput(DateOnly Date, string Location, decimal Hours, Guid ProjectId);
public sealed record WorkHourEntry(Guid Id, DateOnly Date, string Location, decimal Hours, Guid ProjectId,
    string ProjectCode, string ProjectName, Guid RootProjectId, string RootProjectCode, string RootProjectName);
public sealed record WorkHourRevision(Guid Id, DateTimeOffset At, string Actor, IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<WorkHourEntry> Before, IReadOnlyList<WorkHourEntry> After);
public sealed record WorkHoursMonth(Guid CompanyId, string Username, string Month, long RowVersion,
    IReadOnlyList<WorkHourEntry> Entries, IReadOnlyList<WorkHourRevision> History);
public sealed record WorkHoursVersion(string Month, long RowVersion);
public sealed record SaveWorkHours(DateOnly From, DateOnly To, IReadOnlyList<WorkHourInput> Entries, IReadOnlyList<WorkHoursVersion> Versions);
public sealed record WorkHoursPerson(string Username, string DisplayName, string Department, bool IsActive);
public sealed record WorkHoursProject(Guid Id, string Code, string Name, Guid RootProjectId, string RootProjectCode, string RootProjectName);
public sealed record WorkHoursView(DateOnly Today, DateOnly StartDate, string Username, string DisplayName, string Scope,
    IReadOnlyList<WorkHoursPerson> People, IReadOnlyList<WorkHoursProject> Projects, IReadOnlyList<WorkHoursMonth> Sheets);

public interface IWorkHoursStore
{
    Task<DateOnly> StartDateAsync(Guid companyId, DateOnly initialDate, CancellationToken ct);
    Task<IReadOnlyList<WorkHoursMonth>> ReadAsync(Guid companyId, string fromMonth, string toMonth, CancellationToken ct);
    Task<bool> SaveAsync(IReadOnlyList<WorkHoursMonth> sheets, CancellationToken ct);
}

public sealed class WorkHoursService(IPdmRepository repository, IWorkHoursStore store, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
    private async Task<Guid> Company(CancellationToken ct) => TenantContext.CompanyId
        ?? (await repository.GetProjectNumberingOptionsAsync(ct)).Organizations.First().Id;
    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Month(DateOnly date) => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
    private static string Key(DateOnly date, Guid project, string location) => $"{date:yyyy-MM-dd}/{project}/{location}";
    private static void Range(DateOnly from, DateOnly to, int maximum)
    {
        if (from.Year < 2000 || to < from || to.DayNumber - from.DayNumber >= maximum)
            throw new PdmRuleException($"请选择有效日期范围，单次最多{maximum}天。");
    }
    private async Task<(Guid Company, OrganizationDirectory Directory, OrganizationDirectoryUser User)> Context(string actor, CancellationToken ct)
    {
        var company = await Company(ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var user = directory.Users.FirstOrDefault(x => Same(x.Username, actor) && x.IsActive
            && (x.CompanyId == company || x.CompanyId is null || x.CrossCompanyView || x.AccessibleCompanyIds?.Contains(company) == true));
        if (user is null) throw new UnauthorizedAccessException("当前账号已停用或没有当前公司的工时权限，请联系管理员。");
        return (company, directory, user);
    }
    private static WorkHoursProject ProjectOption(Project project, IReadOnlyList<Project> projects)
    {
        var rootId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
        var root = projects.FirstOrDefault(x => x.Id == rootId) ?? project;
        return new(project.Id, project.Code, project.Name, rootId, root.Code, root.Name);
    }
    public async Task<WorkHoursView> ReadAsync(DateOnly from, DateOnly to, bool report, string actor, UserRole role, CancellationToken ct)
    {
        Range(from, to, 366);
        var (company, directory, user) = await Context(actor, ct);
        var allCompany = report && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.WorkHoursReportView, ct);
        var units = directory.Units.Where(x => x.OrganizationId == company).ToArray();
        var managed = report ? directory.Managers.Where(x => Same(x.PrimaryManager, actor) || x.CollaborativeManagers.Any(n => Same(n, actor)))
            .Select(x => x.UnitId).Where(id => units.Any(u => u.Id == id)).ToHashSet() : [];
        for (var count = -1; count != managed.Count;)
        {
            count = managed.Count;
            foreach (var unit in units.Where(x => x.ParentUnitId.HasValue && managed.Contains(x.ParentUnitId.Value))) managed.Add(unit.Id);
        }
        var authorized = directory.Memberships.Where(x => managed.Contains(x.UnitId)).Select(x => x.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        authorized.Add(actor);
        var people = directory.Users.Where(x => (x.CompanyId == company || x.CompanyId is null || x.AccessibleCompanyIds?.Contains(company) == true)
            && (allCompany || authorized.Contains(x.Username)))
            .Select(x => new WorkHoursPerson(x.Username, x.DisplayName,
                units.FirstOrDefault(u => directory.Memberships.Any(m => m.UnitId == u.Id && Same(m.Username, x.Username) && m.IsPrimary))?.Name
                ?? units.FirstOrDefault(u => directory.Memberships.Any(m => m.UnitId == u.Id && Same(m.Username, x.Username)))?.Name ?? "未分配", x.IsActive))
            .ToList();
        if (!people.Any(x => Same(x.Username, actor))) people.Add(new(actor, user.DisplayName, "未分配", true));
        var names = people.Select(x => x.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sheets = (await store.ReadAsync(company, Month(from), Month(to), ct)).Where(x => names.Contains(x.Username)
            && (report || Same(x.Username, actor))).Select(x => x with { History = [] }).ToList();
        if (!report)
            for (var date = new DateOnly(from.Year, from.Month, 1); date <= to; date = date.AddMonths(1))
                if (!sheets.Any(x => x.Month == Month(date))) sheets.Add(new(company, actor, Month(date), 0, [], []));
        var projects = (await repository.ListProjectsForUserAsync(actor, role, ct)).Where(x => x.IsActive && x.OrganizationId == company).ToArray();
        return new(Today, await store.StartDateAsync(company, Today, ct), actor, user.DisplayName,
            allCompany ? "当前公司" : report && managed.Count > 0 ? "本人及管理部门" : "仅本人",
            people, projects.Select(x => ProjectOption(x, projects)).OrderBy(x => x.Code).ToArray(), sheets);
    }
    public async Task<WorkHoursView> SaveAsync(SaveWorkHours command, string actor, UserRole role, CancellationToken ct)
    {
        Range(command.From, command.To, 31);
        if (command.Entries is null || command.Versions is null || command.Entries.Count > 500)
            throw new PdmRuleException("工时资料不完整或条数过多，请刷新后重试。");
        var (company, _, _) = await Context(actor, ct);
        if (command.To > Today) throw new PdmRuleException("实际工时不能填写未来日期。");
        if (command.Entries.Any(x => x.Date < command.From || x.Date > command.To || x.Hours <= 0 || x.Hours > 24 || x.Hours * 2 != decimal.Truncate(x.Hours * 2)
            || x.Location is not ("厂内" or "厂外") || x.ProjectId == Guid.Empty))
            throw new PdmRuleException("请选择范围内日期、项目和厂内／厂外；工时须大于0，按0.5小时填写且每天合计不超过24小时。");
        if (command.Entries.GroupBy(x => x.Date).Any(g => g.Sum(x => x.Hours) > 24)) throw new PdmRuleException("同一天所有项目的工时合计不能超过24小时。");
        if (command.Entries.GroupBy(x => Key(x.Date, x.ProjectId, x.Location)).Any(g => g.Count() > 1))
            throw new PdmRuleException("同一天、同一项目、同一所在地只能填写一条，请合并工时。");
        var sheets = (await store.ReadAsync(company, Month(command.From), Month(command.To), ct)).Where(x => Same(x.Username, actor)).ToArray();
        var visible = (await repository.ListProjectsForUserAsync(actor, role, ct)).Where(x => x.OrganizationId == company).ToArray();
        var entries = new List<WorkHourEntry>();
        foreach (var item in command.Entries)
        {
            var existing = sheets.SelectMany(x => x.Entries).FirstOrDefault(x => x.Date == item.Date && x.ProjectId == item.ProjectId && x.Location == item.Location);
            var project = visible.FirstOrDefault(x => x.Id == item.ProjectId && x.IsActive);
            // 历史项目即使已停用，也允许保留原有记录；不能新增到停用或无权访问的项目。
            if (project is null && existing is null) throw new UnauthorizedAccessException("所选项目已停用、已删除或不在可填报范围内，请重新选择。");
            if (existing is not null) entries.Add(existing with { Hours = item.Hours });
            else
            {
                var option = ProjectOption(project!, visible);
                entries.Add(new(Guid.NewGuid(), item.Date, item.Location, item.Hours, option.Id, option.Code, option.Name,
                    option.RootProjectId, option.RootProjectCode, option.RootProjectName));
            }
        }
        var changed = new List<WorkHoursMonth>();
        for (var date = new DateOnly(command.From.Year, command.From.Month, 1); date <= command.To; date = date.AddMonths(1))
        {
            var month = Month(date);
            var sheet = sheets.FirstOrDefault(x => x.Month == month) ?? new(company, actor, month, 0, [], []);
            var versions = command.Versions.Where(x => x.Month == month).ToArray();
            if (versions.Length != 1 || versions[0].RowVersion != sheet.RowVersion) throw new PdmConflictException("工时已在其他页面修改，请刷新后重新填写；本次没有覆盖任何记录。");
            var before = sheet.Entries.Where(x => x.Date >= command.From && x.Date <= command.To).OrderBy(x => Key(x.Date, x.ProjectId, x.Location)).ToArray();
            var after = entries.Where(x => Month(x.Date) == month).OrderBy(x => Key(x.Date, x.ProjectId, x.Location)).ToArray();
            if (before.SequenceEqual(after)) continue;
            var dates = before.Concat(after).Select(x => x.Date).Distinct().Order().ToArray();
            var revision = new WorkHourRevision(Guid.NewGuid(), clock.GetUtcNow(), actor, dates, before, after);
            changed.Add(sheet with { Entries = sheet.Entries.Where(x => x.Date < command.From || x.Date > command.To).Concat(after).OrderBy(x => x.Date).ToArray(), History = sheet.History.Append(revision).ToArray() });
        }
        if (changed.Count > 0 && !await store.SaveAsync(changed, ct)) throw new PdmConflictException("工时已在其他页面修改，请刷新后重试；本次没有覆盖任何记录。");
        return await ReadAsync(command.From, command.To, false, actor, role, ct);
    }
    public async Task<IReadOnlyList<WorkHourRevision>> HistoryAsync(string month, string actor, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new PdmRuleException("请选择有效月份。");
        var (company, _, _) = await Context(actor, ct);
        return (await store.ReadAsync(company, month, month, ct)).FirstOrDefault(x => Same(x.Username, actor))?.History ?? [];
    }
}
