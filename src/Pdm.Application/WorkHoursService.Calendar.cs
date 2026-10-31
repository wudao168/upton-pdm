using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record MoveWorkHoursDay(Guid CompanyId, DateOnly SourceDate, DateOnly TargetDate,
    long SourceRowVersion, long TargetRowVersion);

public sealed partial class WorkHoursService
{
    public async Task<WorkHoursView> MoveDayAsync(MoveWorkHoursDay command, string actor, UserRole role, CancellationToken ct)
    {
        Range(command.SourceDate, command.SourceDate, 1);
        Range(command.TargetDate, command.TargetDate, 1);
        if (command.SourceDate == command.TargetDate || command.SourceDate > Today || command.TargetDate > Today
            || command.SourceRowVersion < 0 || command.TargetRowVersion < 0)
            throw new PdmRuleException("请选择不同的有效日期；实际工时不能移动到未来日期。");
        var (company, _, user) = await Context(actor, ct);
        if (command.CompanyId != company) throw new PdmRuleException("当前公司已切换，请返回原公司重新剪切工时。");
        var sourceMonth = Month(command.SourceDate);
        var targetMonth = Month(command.TargetDate);
        var sheets = new List<WorkHoursMonth>();
        foreach (var month in new[] { sourceMonth, targetMonth }.Distinct())
            sheets.AddRange((await store.ReadAsync(company, month, month, ct)).Where(x => Same(x.Username, actor)));
        var source = sheets.FirstOrDefault(x => x.Month == sourceMonth) ?? new(company, actor, sourceMonth, 0, [], []);
        var target = sheets.FirstOrDefault(x => x.Month == targetMonth) ?? new(company, actor, targetMonth, 0, [], []);
        if (source.RowVersion != command.SourceRowVersion || target.RowVersion != command.TargetRowVersion)
            throw new PdmConflictException("源日期或目标日期的工时已更新，请刷新后重新剪切；本次没有覆盖任何记录。");
        var moved = source.Entries.Where(x => x.Date == command.SourceDate).ToArray();
        if (moved.Length == 0) throw new PdmRuleException("源日期没有可剪切的工时，请刷新后重新选择。");
        var merged = target.Entries.Where(x => x.Date == command.TargetDate).ToDictionary(x => (x.ProjectId, x.Location));
        var visible = (await repository.ListProjectsForUserAsync(actor, user.Role, ct)).Where(x => x.OrganizationId == company).ToArray();
        foreach (var entry in moved)
        {
            var key = (entry.ProjectId, entry.Location);
            if (merged.TryGetValue(key, out var existing)) merged[key] = existing with { Hours = existing.Hours + entry.Hours };
            else
            {
                var project = visible.FirstOrDefault(x => x.Id == entry.ProjectId && x.IsActive);
                if (project is null) throw new UnauthorizedAccessException("源工时的项目已停用或不在可填报范围内，请核对后再剪切。");
                var option = ProjectOption(project, visible);
                merged[key] = new(Guid.NewGuid(), command.TargetDate, entry.Location, entry.Hours, option.Id,
                    option.Code, option.Name, option.RootProjectId, option.RootProjectCode, option.RootProjectName);
            }
        }
        if (merged.Values.Sum(x => x.Hours) > 24) throw new PdmRuleException("目标日期的工时合计不能超过24小时；源日期记录未删除。");
        var changed = new List<WorkHoursMonth>();
        foreach (var month in new[] { sourceMonth, targetMonth }.Distinct())
        {
            var sheet = month == sourceMonth ? source : target;
            var dates = new[] { command.SourceDate, command.TargetDate }.Where(x => Month(x) == month).Distinct().Order().ToArray();
            var before = sheet.Entries.Where(x => dates.Contains(x.Date)).ToArray();
            var after = month == targetMonth ? merged.Values.ToArray() : [];
            var revision = new WorkHourRevision(Guid.NewGuid(), clock.GetUtcNow(), actor, dates, before, after,
                $"剪切工时：{command.SourceDate:yyyy-MM-dd} → {command.TargetDate:yyyy-MM-dd}");
            changed.Add(sheet with { Entries = sheet.Entries.Where(x => !dates.Contains(x.Date)).Concat(after).OrderBy(x => x.Date).ToArray(), History = sheet.History.Append(revision).ToArray() });
        }
        if (!await store.SaveAsync(changed, ct))
            throw new PdmConflictException("工时已在其他页面修改，请刷新后重新剪切；本次没有覆盖任何记录。");
        await ResolveRemindersAsync(company, actor, ct);
        return await ReadAsync(command.TargetDate, command.TargetDate, false, actor, role, ct);
    }
}
