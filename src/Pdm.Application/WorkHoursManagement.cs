using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public interface IWorkHoursCalendar { bool IsWorkingDay(DateOnly date); }
public sealed record WorkHoursRequirement(Guid Id, string Username, DateOnly From, DateOnly? To, decimal Hours);
public sealed record WorkHoursException(Guid Id, string Username, DateOnly From, DateOnly To, string Kind, decimal? Hours, string Reason);
public sealed record WorkHoursGrant(string Username, IReadOnlyList<Guid> UnitIds);
public sealed record WorkHoursSettingRevision(Guid Id, DateTimeOffset At, string Actor, string Action, string Reason,
    IReadOnlyList<WorkHoursRequirement> BeforeRequirements, IReadOnlyList<WorkHoursRequirement> AfterRequirements,
    IReadOnlyList<WorkHoursException> BeforeExceptions, IReadOnlyList<WorkHoursException> AfterExceptions,
    IReadOnlyList<WorkHoursGrant> BeforeGrants, IReadOnlyList<WorkHoursGrant> AfterGrants)
{
    public IReadOnlyList<WorkHoursDepartmentPeriod> BeforeDepartments { get; init; } = [];
    public IReadOnlyList<WorkHoursDepartmentPeriod> AfterDepartments { get; init; } = [];
}
public sealed record WorkHoursCatalog(Guid CompanyId, long RowVersion, IReadOnlyList<WorkHoursRequirement> Requirements,
    IReadOnlyList<WorkHoursException> Exceptions, IReadOnlyList<WorkHoursGrant> Grants, IReadOnlyList<WorkHoursSettingRevision> History)
{
    public DateOnly? DepartmentScopeFrom { get; init; }
    public IReadOnlyList<WorkHoursDepartmentPeriod> Departments { get; init; } = [];
}
public sealed record WorkHoursDayCheck(DateOnly Date, decimal Expected, decimal Actual, string Status, bool Muted, string Reason);
public sealed record WorkHoursCompliance(string Username, IReadOnlyList<WorkHoursDayCheck> Days)
{
    public bool ReminderEnabled { get; init; } = true;
}
public sealed record WorkHoursManagementView(DateOnly Today, long RowVersion, bool CanAuthorize, string Scope,
    IReadOnlyList<WorkHoursPerson> People, IReadOnlyList<OrganizationUnit> Units, IReadOnlyList<WorkHoursPerson> Assistants,
    IReadOnlyList<WorkHoursRequirement> Requirements, IReadOnlyList<WorkHoursException> Exceptions,
    IReadOnlyList<WorkHoursGrant> Grants, IReadOnlyList<WorkHoursSettingRevision> History)
{
    public IReadOnlyList<WorkHoursDepartmentGroup> DepartmentGroups { get; init; } = [];
    public int UnassignedPeopleCount { get; init; }
    public bool CanAdjust { get; init; }
}
public sealed record SetWorkHoursRequirement(IReadOnlyList<string> Usernames, DateOnly From, DateOnly? To, decimal Hours, bool Required, string Reason, long RowVersion);
public sealed record SetWorkHoursException(string Username, DateOnly From, DateOnly To, string Kind, decimal? Hours, string Reason, long RowVersion);
public sealed record RemoveWorkHoursException(Guid Id, string Reason, long RowVersion);
public sealed record SetWorkHoursGrant(string Username, IReadOnlyList<Guid> UnitIds, string Reason, long RowVersion);
public sealed record CorrectWorkHours(string Username, SaveWorkHours Sheet, string Reason);
public sealed record WorkHoursCorrectionView(WorkHoursMonth Sheet, IReadOnlyList<WorkHoursProject> Projects);
public sealed record RemindWorkHours(IReadOnlyList<string> Usernames);
public sealed record WorkHoursReminderOutcome(string Username, string DisplayName, bool Sent, string Reason);
public sealed record WorkHoursReminderResult(int Sent, int Skipped)
{
    public IReadOnlyList<WorkHoursReminderOutcome> Results { get; init; } = [];
}

public sealed partial class WorkHoursService
{
    private sealed record ManagementAccess(bool Admin, HashSet<Guid> Units, HashSet<string> Names);
    private static bool InCompany(OrganizationDirectoryUser user, Guid company) => user.CompanyId == company || user.CompanyId is null || user.AccessibleCompanyIds?.Contains(company) == true;
    private static bool Assistant(OrganizationDirectoryUser user) => user.EffectiveRoleCodes.Any(x => Same(x, "TechnicalAssistant"));
    private static bool RosterAssistant(OrganizationDirectoryUser user) => user.EffectiveRoleCodes.Any(x => Same(x, "TechnicalAssistant") || Same(x, "ProductionAssistant") || Same(x, "SalesAssistant"));
    private static WorkHoursPerson Person(OrganizationDirectoryUser user, OrganizationDirectory directory, Guid company)
    {
        var unit = PrimaryUnit(user.Username, directory, company);
        return new(user.Username, user.DisplayName, unit?.Name ?? "未分配", user.IsActive) { DepartmentId = unit?.Id };
    }
    private static ManagementAccess ManagementScope(Guid company, OrganizationDirectory directory, OrganizationDirectoryUser user, WorkHoursCatalog catalog, bool personnelOnly = false)
    {
        var admin = !personnelOnly && (user.Role is UserRole.Administrator or UserRole.PlatformAdministrator || user.EffectiveRoleCodes.Any(x => x is "platform_admin" or "developer"));
        var eligible = personnelOnly ? RosterAssistant(user) : Assistant(user) || user.Role == UserRole.BusinessUnitManager || user.EffectiveRoleCodes.Contains("BusinessUnitManager", StringComparer.OrdinalIgnoreCase);
        var units = directory.Units.Where(x => x.OrganizationId == company && x.IsActive).ToArray();
        var selected = admin ? units.Select(x => x.Id).ToHashSet() : eligible
            ? directory.Managers.Where(x => Same(x.PrimaryManager, user.Username) || x.CollaborativeManagers.Any(n => Same(n, user.Username))).Select(x => x.UnitId)
                .Concat(directory.Memberships.Where(x => Same(x.Username, user.Username)).Select(x => RosterAssistant(user) ? RootUnit(x.UnitId, units) : x.UnitId))
                .Concat(catalog.Grants.Where(x => Same(x.Username, user.Username)).SelectMany(x => x.UnitIds)).Where(id => units.Any(u => u.Id == id)).ToHashSet()
            : [];
        for (var count = -1; count != selected.Count;)
        {
            count = selected.Count;
            foreach (var unit in units.Where(x => x.ParentUnitId.HasValue && selected.Contains(x.ParentUnitId.Value))) selected.Add(unit.Id);
        }
        var names = directory.Users.Where(x => InCompany(x, company) && (admin || directory.Memberships.Any(m => Same(m.Username, x.Username) && selected.Contains(m.UnitId))))
            .Select(x => x.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(admin, selected, names);
    }
    private async Task<(Guid Company, OrganizationDirectory Directory, WorkHoursCatalog Catalog, ManagementAccess Access)> Management(string actor, CancellationToken ct, bool settings = false)
    {
        var (company, directory, user) = await Context(actor, ct);
        var catalog = await store.ReadCatalogAsync(company, ct);
        var access = ManagementScope(company, directory, user, catalog);
        if (settings && !access.Admin && access.Units.Count == 0) access = ManagementScope(company, directory, user, catalog, true);
        if (!access.Admin && access.Units.Count == 0) throw new UnauthorizedAccessException("当前账号没有该公司的工时管理范围，请联系管理员配置角色及所属部门。");
        return (company, directory, catalog, access);
    }
    private static void CheckTarget(string username, ManagementAccess access)
    {
        if (!access.Names.Contains(username)) throw new UnauthorizedAccessException("所选人员不在当前授权部门范围内，不能查看或修改其工时资料。");
    }
    private static string Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw new PdmRuleException("请填写处理原因，最多500字。");
        return reason.Trim();
    }
    public async Task<WorkHoursManagementView> ReadManagementAsync(string actor, CancellationToken ct)
    {
        var (_, directory, catalog, access) = await Management(actor, ct, true);
        var user = directory.Users.First(x => Same(x.Username, actor));
        var personnel = ManagementScope(catalog.CompanyId, directory, user, catalog, true);
        var adjustment = ManagementScope(catalog.CompanyId, directory, user, catalog);
        var people = directory.Users.Where(x => access.Names.Contains(x.Username)).Select(x => Person(x, directory, catalog.CompanyId)).OrderBy(x => x.Department).ThenBy(x => x.DisplayName).ToArray();
        // 修订按人员裁剪，部门管理者不能通过设置历史读到其他部门的信息。
        return new(Today, catalog.RowVersion, access.Admin, access.Admin ? "当前公司" : "授权部门及下级部门", people,
            directory.Units.Where(x => access.Units.Contains(x.Id)).ToArray(),
            access.Admin ? directory.Users.Where(x => InCompany(x, catalog.CompanyId) && x.IsActive && (Assistant(x) || x.Role == UserRole.BusinessUnitManager)).Select(x => Person(x, directory, catalog.CompanyId)).ToArray() : [],
            catalog.Requirements.Where(x => access.Names.Contains(x.Username)).ToArray(), catalog.Exceptions.Where(x => access.Names.Contains(x.Username)).ToArray(),
            access.Admin ? catalog.Grants : [], catalog.History.Select(x => x with
            {
                BeforeRequirements = x.BeforeRequirements.Where(r => access.Names.Contains(r.Username)).ToArray(), AfterRequirements = x.AfterRequirements.Where(r => access.Names.Contains(r.Username)).ToArray(),
                BeforeExceptions = x.BeforeExceptions.Where(r => access.Names.Contains(r.Username)).ToArray(), AfterExceptions = x.AfterExceptions.Where(r => access.Names.Contains(r.Username)).ToArray(),
                BeforeGrants = access.Admin ? x.BeforeGrants : [], AfterGrants = access.Admin ? x.AfterGrants : [],
                BeforeDepartments = access.Admin ? x.BeforeDepartments : x.BeforeDepartments.Where(d => access.Units.Contains(d.UnitId)).ToArray(),
                AfterDepartments = access.Admin ? x.AfterDepartments : x.AfterDepartments.Where(d => access.Units.Contains(d.UnitId)).ToArray()
            }).Where(x => x.BeforeRequirements.Count + x.AfterRequirements.Count + x.BeforeExceptions.Count + x.AfterExceptions.Count + x.BeforeGrants.Count + x.AfterGrants.Count + x.BeforeDepartments.Count + x.AfterDepartments.Count > 0).ToArray())
        {
            CanAdjust = adjustment.Admin || adjustment.Units.Count > 0,
            DepartmentGroups = DepartmentGroups(directory, catalog, personnel),
            UnassignedPeopleCount = directory.Users.Count(x => x.IsActive && InCompany(x, catalog.CompanyId) && PrimaryUnit(x.Username, directory, catalog.CompanyId) is null)
        };
    }
    private async Task SaveCatalog(WorkHoursCatalog before, WorkHoursCatalog after, long version, string actor, string action, string reason, CancellationToken ct)
    {
        if (before.RowVersion != version) throw new PdmConflictException("工时设置已被其他人更新，请刷新后重新操作；本次没有覆盖原设置。");
        var revision = new WorkHoursSettingRevision(Guid.NewGuid(), clock.GetUtcNow(), actor, action, Reason(reason),
            before.Requirements.Except(after.Requirements).ToArray(), after.Requirements.Except(before.Requirements).ToArray(),
            before.Exceptions.Except(after.Exceptions).ToArray(), after.Exceptions.Except(before.Exceptions).ToArray(),
            before.Grants.Except(after.Grants).ToArray(), after.Grants.Except(before.Grants).ToArray())
        {
            BeforeDepartments = before.Departments.Except(after.Departments).ToArray(), AfterDepartments = after.Departments.Except(before.Departments).ToArray()
        };
        if (!await store.SaveCatalogAsync(after with { History = before.History.Append(revision).ToArray() }, ct))
            throw new PdmConflictException("工时设置已被其他人更新，请刷新后重新操作；本次没有覆盖原设置。");
    }
    public async Task<WorkHoursManagementView> SetRequirementAsync(SetWorkHoursRequirement input, string actor, CancellationToken ct)
    {
        var (_, directory, catalog, access) = await Management(actor, ct, true);
        access = ManagementScope(catalog.CompanyId, directory, directory.Users.First(x => Same(x.Username, actor)), catalog, true);
        if (input.Usernames is null || input.Usernames.Count is < 1 or > 500 || input.From < Today || input.To < input.From || input.From.Year > Today.Year + 5
            || input.Required && (input.Hours <= 0 || input.Hours > 24 || input.Hours * 2 != decimal.Truncate(input.Hours * 2)))
            throw new PdmRuleException("请选择人员及从今天起的生效日期；每日应填工时按0.5小时设置，须大于0且不超过24小时。");
        var requirements = catalog.Requirements.ToList();
        foreach (var name in input.Usernames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CheckTarget(name, access);
            var target = directory.Users.First(x => Same(x.Username, name));
            if (input.Required && !DepartmentEnabled(catalog, PersonRoot(name, directory, catalog.CompanyId), directory))
                throw new PdmRuleException("该人员所在部门尚未参与工时填报，请先由管理员启用部门。");
            if (input.Required && !target.IsActive) throw new PdmRuleException("停用人员不能加入应填名单，请先核对人员状态。");
            foreach (var existing in requirements.Where(x => Same(x.Username, name) && (x.To is null || x.To >= input.From)).ToArray())
            {
                requirements.Remove(existing);
                if (existing.From < input.From) requirements.Add(existing with { To = input.From.AddDays(-1) });
            }
            if (input.Required) requirements.Add(new(Guid.NewGuid(), target.Username, input.From, input.To, input.Hours));
        }
        await SaveCatalog(catalog, InitializeDepartmentScope(catalog, directory) with { Requirements = requirements }, input.RowVersion, actor, input.Required ? "设置应填人员" : "结束应填要求", input.Reason, ct);
        return await ReadManagementAsync(actor, ct);
    }
    public async Task<WorkHoursManagementView> SetExceptionAsync(SetWorkHoursException input, string actor, CancellationToken ct)
    {
        var (company, _, catalog, access) = await Management(actor, ct);
        CheckTarget(input.Username, access); Range(input.From, input.To, 366);
        if (input.Kind == "mute" && input.To < Today) throw new PdmRuleException("暂停提醒的结束日期不能早于今天；到期后未补齐的历史工时会恢复提醒。");
        if (input.Kind is not ("target" or "mute") || input.Kind == "target" && (input.Hours is null || input.Hours < 0 || input.Hours > 24 || input.Hours * 2 != decimal.Truncate(input.Hours.Value * 2)))
            throw new PdmRuleException("请选择调整应填工时或暂停提醒；工时按0.5小时设置，0表示豁免填报。");
        if (!catalog.Requirements.Any(x => Same(x.Username, input.Username) && x.From <= input.To && (x.To is null || x.To >= input.From)))
            throw new PdmRuleException("该日期范围没有应填要求，请先核对人员名单及生效日期。");
        var item = new WorkHoursException(Guid.NewGuid(), input.Username, input.From, input.To, input.Kind, input.Kind == "target" ? input.Hours : null, Reason(input.Reason));
        await SaveCatalog(catalog, catalog with { Exceptions = catalog.Exceptions.Append(item).ToArray() }, input.RowVersion, actor, input.Kind == "mute" ? "暂停提醒" : "调整应填要求", input.Reason, ct);
        await ResolveRemindersAsync(company, input.Username, ct);
        return await ReadManagementAsync(actor, ct);
    }
    public async Task<WorkHoursManagementView> RemoveExceptionAsync(RemoveWorkHoursException input, string actor, CancellationToken ct)
    {
        var (_, _, catalog, access) = await Management(actor, ct);
        var item = catalog.Exceptions.FirstOrDefault(x => x.Id == input.Id) ?? throw new PdmRuleException("该特殊处理记录不存在，请刷新后重试。");
        CheckTarget(item.Username, access);
        await SaveCatalog(catalog, catalog with { Exceptions = catalog.Exceptions.Where(x => x.Id != input.Id).ToArray() }, input.RowVersion, actor, "撤销特殊处理", input.Reason, ct);
        return await ReadManagementAsync(actor, ct);
    }
    public async Task<WorkHoursManagementView> SetGrantAsync(SetWorkHoursGrant input, string actor, CancellationToken ct)
    {
        var (company, directory, catalog, access) = await Management(actor, ct);
        if (!access.Admin) throw new UnauthorizedAccessException("只有系统管理员可以设置工时管理的授权部门。");
        var target = directory.Users.FirstOrDefault(x => InCompany(x, company) && Same(x.Username, input.Username) && x.IsActive && (Assistant(x) || x.Role == UserRole.BusinessUnitManager));
        if (target is null || input.UnitIds is null || input.UnitIds.Any(id => !directory.Units.Any(x => x.Id == id && x.OrganizationId == company && x.IsActive)))
            throw new PdmRuleException("请选择当前公司的事业部负责人、技术助理及有效部门。");
        var grants = catalog.Grants.Where(x => !Same(x.Username, target.Username)).ToList();
        if (input.UnitIds.Count > 0) grants.Add(new(target.Username, input.UnitIds.Distinct().ToArray()));
        await SaveCatalog(catalog, catalog with { Grants = grants }, input.RowVersion, actor, "调整管理授权部门", input.Reason, ct);
        return await ReadManagementAsync(actor, ct);
    }
    private IReadOnlyList<WorkHoursCompliance> BuildCompliance(WorkHoursCatalog catalog, IReadOnlyList<WorkHoursPerson> people,
        IReadOnlyList<WorkHoursMonth> sheets, DateOnly from, DateOnly to, DateOnly start, OrganizationDirectory directory)
    {
        var result = new List<WorkHoursCompliance>();
        foreach (var person in people)
        {
            var requirements = catalog.Requirements.Where(x => Same(x.Username, person.Username)).ToArray();
            var exceptions = catalog.Exceptions.Where(x => Same(x.Username, person.Username)).ToArray();
            var hours = sheets.Where(x => Same(x.Username, person.Username)).SelectMany(x => x.Entries).GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.Sum(e => e.Hours));
            var days = new List<WorkHoursDayCheck>();
            var unit = PersonRoot(person.Username, directory, catalog.CompanyId);
            for (var date = from > start ? from : start; date <= to && date < Today; date = date.AddDays(1))
            {
                var requirement = requirements.LastOrDefault(x => x.From <= date && (x.To is null || x.To >= date));
                if (requirement is null || !DepartmentParticipates(catalog, unit, date) || !(calendar?.IsWorkingDay(date) ?? date.DayOfWeek != DayOfWeek.Sunday)) continue;
                var change = exceptions.LastOrDefault(x => x.From <= date && x.To >= date && x.Kind == "target");
                var mute = exceptions.LastOrDefault(x => x.From <= date && x.To >= date && x.Kind == "mute" && x.To >= Today);
                var expected = change?.Hours ?? requirement.Hours;
                var actual = hours.GetValueOrDefault(date);
                days.Add(new(date, expected, actual, expected == 0 ? "已豁免" : actual == 0 ? "未填写" : actual < expected ? "填写不完整" : "完整", mute is not null,
                    string.Join("；", new[] { change?.Reason, mute is null ? null : $"暂停提醒：{mute.Reason}" }.Where(x => x is not null))));
            }
            if (days.Count > 0) result.Add(new(person.Username, days)
            { ReminderEnabled = person.IsActive && RequiredNow(catalog, person.Username, Today) && DepartmentEnabled(catalog, unit, directory) });
        }
        return result;
    }
    public async Task<WorkHoursCorrectionView> ReadCorrectionAsync(string username, string month, string actor, CancellationToken ct)
    {
        var (company, directory, _, access) = await Management(actor, ct); CheckTarget(username, access);
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new PdmRuleException("请选择有效月份。");
        var sheet = (await store.ReadAsync(company, month, month, ct)).FirstOrDefault(x => Same(x.Username, username)) ?? new(company, username, month, 0, [], []);
        var user = directory.Users.First(x => Same(x.Username, username));
        var projects = (await repository.ListProjectsForUserAsync(username, user.Role, ct)).Where(x => x.OrganizationId == company && x.IsActive).ToArray();
        return new(sheet, projects.Select(x => ProjectOption(x, projects)).ToArray());
    }
    public async Task CorrectAsync(CorrectWorkHours input, string actor, UserRole role, CancellationToken ct)
    {
        var (_, _, _, access) = await Management(actor, ct); CheckTarget(input.Username, access);
        if (input.Sheet is null) throw new PdmRuleException("请填写需要更正的工时资料。");
        await SaveEntriesAsync(input.Sheet, input.Username, actor, role, Reason(input.Reason), ct);
    }
    private async Task<IReadOnlyList<WorkHoursCompliance>> CheckHistory(Guid company, IReadOnlyList<string> usernames, CancellationToken ct)
    {
        var catalog = await store.ReadCatalogAsync(company, ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var names = usernames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var from = catalog.Requirements.Where(x => names.Contains(x.Username)).Select(x => x.From).DefaultIfEmpty(Today).Min();
        // 按年度分段读取，既保留历史欠填，也不产生逐日数据库查询。
        var checks = new Dictionary<string, List<WorkHoursDayCheck>>(StringComparer.OrdinalIgnoreCase);
        var people = usernames.Select(x => new WorkHoursPerson(x, x, "", true)).ToArray();
        var start = await store.StartDateAsync(company, Today, ct);
        for (var date = from > start ? from : start; date < Today; date = date.AddDays(366))
        {
            var to = date.AddDays(365) < Today ? date.AddDays(365) : Today.AddDays(-1);
            var sheets = await store.ReadAsync(company, Month(date), Month(to), ct);
            foreach (var check in BuildCompliance(catalog, people, sheets, date, to, start, directory))
            {
                if (!checks.TryGetValue(check.Username, out var days)) checks[check.Username] = days = [];
                days.AddRange(check.Days);
            }
        }
        return checks.Select(x => new WorkHoursCompliance(x.Key, x.Value)).ToArray();
    }
    private async Task ResolveRemindersAsync(Guid company, string username, CancellationToken ct)
    {
        if (!(await CheckHistory(company, [username], ct)).SelectMany(x => x.Days).Any(x => x.Actual < x.Expected && !x.Muted))
            await repository.ResolveWorkHoursNotificationsAsync(username, company, clock.GetUtcNow(), ct);
    }
    public async Task<WorkHoursReminderResult> RemindAsync(RemindWorkHours input, string actor, CancellationToken ct)
    {
        var (company, directory, _, access) = await Management(actor, ct);
        if (input.Usernames is null || input.Usernames.Count is < 1 or > 500) throw new PdmRuleException("请选择需要提醒的人员。");
        foreach (var name in input.Usernames) CheckTarget(name, access);
        var sender = directory.Users.First(x => Same(x.Username, actor)).DisplayName;
        return await SendReminders(company, input.Usernames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ct, sender);
    }
    private async Task<WorkHoursReminderResult> SendReminders(Guid company, IReadOnlyList<string> names, CancellationToken ct, string? manualSender = null)
    {
        var sent = 0;
        if (names.Count == 0) return new(0, 0);
        var results = new List<WorkHoursReminderOutcome>();
        // Automatic reminders share a daily key. Each explicit manual action has its own key.
        var source = $"work-hours:{company:N}:{Today:yyyyMMdd}" + (manualSender is null ? "" : $":manual:{Guid.NewGuid():N}");
        var checks = (await CheckHistory(company, names, ct)).ToDictionary(x => x.Username, StringComparer.OrdinalIgnoreCase);
        var catalog = await store.ReadCatalogAsync(company, ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        foreach (var name in names)
        {
            var person = directory.Users.First(x => Same(x.Username, name));
            string? reason = !person.IsActive ? "人员已停用"
                : !RequiredNow(catalog, name, Today) ? "当前不在已生效的应填名单中"
                : !DepartmentEnabled(catalog, PersonRoot(name, directory, company), directory) ? "所在部门未参与工时填报" : null;
            var days = checks.GetValueOrDefault(name)?.Days ?? [];
            var pending = days.Where(x => x.Actual < x.Expected && !x.Muted).ToArray();
            if (reason is null && pending.Length == 0)
                reason = days.Any(x => x.Actual < x.Expected && x.Muted) ? "缺填日期已暂停提醒" : "已无未填写或填写不足的已过工作日";
            if (reason is not null)
            {
                await repository.ResolveWorkHoursNotificationsAsync(name, company, clock.GetUtcNow(), ct);
                results.Add(new(person.Username, person.DisplayName, false, reason));
                continue;
            }
            if (manualSender is null && (await repository.ListNotificationRecipientsAsync(source, ct)).Any(x => Same(x, name)))
            { results.Add(new(person.Username, person.DisplayName, false, "今日已自动提醒")); continue; }
            await repository.CreateUserNotificationsAsync([new(Guid.NewGuid(), name, "work-hours", "请核对并补全工时",
                (manualSender is null ? "" : $"{manualSender}手动提醒：") + $"截至{Today.AddDays(-1):yyyy-MM-dd}，有{pending.Count(x => x.Actual == 0)}天未填写、{pending.Count(x => x.Actual > 0)}天填写不完整，缺少{pending.Sum(x => x.Expected - x.Actual):0.#}小时。请打开工时管理查看具体日期。暂停提醒及已豁免的日期未计入本次提醒。", null, null, source, clock.GetUtcNow(), null)], ct);
            sent++;
            results.Add(new(person.Username, person.DisplayName, true, "已发送站内工时提醒"));
        }
        return new(sent, names.Count - sent) { Results = results };
    }
    public async Task SendDailyRemindersAsync(CancellationToken ct)
    {
        if (clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).Hour < 9) return;
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        foreach (var company in directory.Organizations.Where(x => x.IsActive))
        {
            var catalog = await store.ReadCatalogAsync(company.Id, ct);
            var names = catalog.Requirements.Select(x => x.Username).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => directory.Users.Any(x => Same(x.Username, n) && x.IsActive && InCompany(x, company.Id))).ToArray();
            await SendReminders(company.Id, names, ct);
        }
    }
}
