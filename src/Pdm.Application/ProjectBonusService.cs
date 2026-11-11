using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectBonusService(IPdmRepository repository, IProjectBonusRepository store,
    IProjectBudgetRepository budgets, TimeProvider clock)
{
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Manager(Project project, string actor) => Same(project.PrimaryProjectManager, actor) || project.CollaborativeProjectManagers.Any(u => Same(u, actor));
    private async Task<Project> ProjectAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        if ((await repository.FindUserAsync(actor, ct))?.IsActive != true) throw new UnauthorizedAccessException("账号已停用，请重新登录。");
        var project = await repository.FindProjectAsync(id, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(id, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目。");
        if (project.ParentProjectId is not null)
        {
            project = await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct) ?? throw new PdmNotFoundException("主项目不存在。");
            if (!await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct)) throw new UnauthorizedAccessException("项目奖金统一在主项目申请，请联系项目经理。");
        }
        return project;
    }
    private Task<bool> CanConfigure(string actor, UserRole role, CancellationToken ct) => repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct);
    private async Task<Guid> CompanyAsync(string actor, CancellationToken ct) => TenantContext.CompanyId
        ?? (await repository.FindUserAsync(actor, ct))?.CompanyId ?? throw new PdmRuleException("请先选择公司。");
    public async Task<BonusSettingsView> SettingsAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await CanConfigure(actor, role, ct)) throw new UnauthorizedAccessException("没有奖金规则设置权限。");
        var company = await CompanyAsync(actor, ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        return new(await store.GetSettingsAsync(company, ct) ?? ProjectBonusDefaults.Create(), true,
            directory.Users.Where(u => u.IsActive && (u.CompanyId is null || u.CompanyId == company)).Select(u => new BonusUserOption(u.Username, u.DisplayName)).ToArray(),
            directory.Units.Where(u => u.IsActive && u.OrganizationId == company).Select(u => new ProjectBudgetDepartment(u.Id, u.Name)).ToArray());
    }
    public async Task<BonusSettingsView> SaveSettingsAsync(ProjectBonusSettings input, string actor, UserRole role, CancellationToken ct)
    {
        if (!await CanConfigure(actor, role, ct)) throw new UnauthorizedAccessException("没有奖金规则设置权限。");
        ProjectBonusRules.ValidateSettings(input);
        var company = await CompanyAsync(actor, ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        foreach (var d in input.Departments)
        {
            if (d.UnitId.HasValue && !directory.Units.Any(u => u.Id == d.UnitId && u.IsActive && u.OrganizationId == company))
                throw new PdmRuleException($"“{d.Name}”须绑定本公司启用部门。");
            foreach (var reviewer in d.Reviewers ?? [])
                if (!directory.Users.Any(u => u.IsActive && Same(u.Username, reviewer) && (u.CompanyId is null || u.CompanyId == company))
                    || !directory.Memberships.Any(m => m.UnitId == d.UnitId && Same(m.Username, reviewer)))
                    throw new PdmRuleException($"“{d.Name}”的评分/审批人员必须是所选部门的有效成员。");
        }
        await store.SaveSettingsAsync(company, input, input.Version, ct);
        return await SettingsAsync(actor, role, ct);
    }
    private async Task<decimal?> Pool(Project project, CancellationToken ct)
    {
        var budget = await budgets.FindAsync(project.Id, ct);
        var lines = budget?.SettlementLines;
        if (lines is null || lines.Count == 0 || lines.Any(l => !l.Quantity.HasValue || !l.UnitPrice.HasValue)) return null;
        var settlement = lines.Sum(l => decimal.Round(l.Quantity!.Value * l.UnitPrice!.Value, 2, MidpointRounding.AwayFromZero));
        return settlement < 200000 ? 0 : decimal.Round(settlement * budget!.BonusRate, 2, MidpointRounding.AwayFromZero);
    }
    private static bool PaymentMember(BonusApplication app, string actor) => app.Template?.Departments
        .FirstOrDefault(d => d.Key == app.Template.PaymentDepartmentKey)?.Reviewers?.Any(u => Same(u, actor)) == true;
    private static bool Full(BonusApplication app, Project project, string actor) => Manager(project, actor)
        || app.Steps.Any(s => s.Participants.Members.Any(u => Same(u, actor))) || PaymentMember(app, actor);
    public async Task<ProjectBonusView> GetAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(id, actor, role, ct);
        var book = await store.FindAsync(project.Id, ct) ?? new(project.Id, 0, null, []);
        var manager = Manager(project, actor);
        var canApprove = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, ct);
        var applications = new List<BonusApplicationView>();
        foreach (var app in book.Applications)
        {
            var full = Full(app, project, actor);
            var tasks = app.ScoringTasks.Where(t => t.Members.Any(u => Same(u, actor))).ToArray();
            if (!full && tasks.Length == 0 && !app.People.Any(p => Same(p.Username, actor))) continue;
            var visible = app;
            if (!full)
            {
                var ownIds = app.People.Where(p => Same(p.Username, actor)).Select(p => p.Id).ToHashSet();
                var visibleIds = ownIds.Concat(tasks.Select(t => t.PersonId)).ToHashSet();
                visible = app with { People = app.People.Where(p => visibleIds.Contains(p.Id)).ToArray(),
                    Awards = app.Awards.Where(a => ownIds.Contains(a.PersonId)).Select(a => a with { Share = 0 }).ToArray(),
                    Scores = app.Scores.Where(s => ownIds.Contains(s.PersonId) || tasks.Any(t => t.PersonId == s.PersonId && t.IndicatorId == s.IndicatorId && t.DepartmentKey == s.DepartmentKey)).ToArray(),
                    ScoringTasks = tasks, Evidence = app.Evidence.Where(e => visibleIds.Contains(e.PersonId)).ToArray(),
                    Steps = [], Audit = [], Payment = null, ProjectScore = null,
                    Template = app.Template is null ? null : app.Template with { Departments = app.Template.Departments.Select(d => d with { Reviewers = [] }).ToArray(), ApprovalNodes = [] } };
            }
            applications.Add(new(visible, manager && app.Status is "Draft" or "Rejected" or "Withdrawn",
                manager && app.Status == "Scored", canApprove && app.Status == "Pending" && app.Steps.FirstOrDefault(s => s.Decision is null)?.Participants.CanSign(actor) == true,
                app.Status == "Approved" && PaymentMember(app, actor) && app.Payment is null, app.Status == "Scoring" ? tasks : []));
        }
        var showPool = manager || book.Applications.Any(a => Full(a, project, actor));
        var settings = manager ? await store.GetSettingsAsync(project.OrganizationId ?? await CompanyAsync(actor, ct), ct) ?? ProjectBonusDefaults.Create() : null;
        var users = manager ? (await repository.ListUsersAsync(ct)).Where(u => u.IsActive && (u.CompanyId is null || u.CompanyId == project.OrganizationId))
            .Select(u => new BonusUserOption(u.Username, u.DisplayName)).ToArray() : [];
        return new(project.Id, project.Code, project.Name, book.RowVersion, showPool ? book.FrozenPool ?? await Pool(project, ct) : null,
            book.FrozenPool.HasValue, manager, actor, applications, settings, users);
    }
    public async Task<ProjectBonusView> SaveAsync(Guid id, string phase, SaveBonusDraft input, string actor, UserRole role, CancellationToken ct)
    {
        ProjectBonusRules.Phase(phase);
        var project = await ProjectAsync(id, actor, role, ct);
        if (!Manager(project, actor)) throw new UnauthorizedAccessException("仅项目经理可发起和分配项目奖金。");
        var book = await store.FindAsync(project.Id, ct) ?? new(project.Id, 0, null, []);
        var old = book.Applications.SingleOrDefault(a => a.Phase == phase);
        if (old is not null && old.Status is not ("Draft" or "Rejected" or "Withdrawn")) throw new PdmConflictException("请先撤回当前申请再调整，原评分和审批意见将保留在历史中。");
        var settings = await store.GetSettingsAsync(project.OrganizationId ?? await CompanyAsync(actor, ct), ct) ?? ProjectBonusDefaults.Create();
        ProjectBonusRules.ValidateSettings(settings);
        if (input.People is null || input.People.Count is < 1 or > 100) throw new PdmRuleException("请添加1至100条人员岗位分配。");
        ProjectBonusRules.Unique(input.People.Select(p => p.Id));
        ProjectBonusRules.Unique(input.People.Select(p => p.Username + ":" + p.RoleId));
        var users = await repository.ListUsersAsync(ct);
        foreach (var p in input.People)
        {
            var template = settings.Roles.SingleOrDefault(r => r.Id == p.RoleId && r.Phase == phase) ?? throw new PdmRuleException("请选择当前阶段的岗位模板。");
            if (!users.Any(u => u.IsActive && Same(u.Username, p.Username) && (u.CompanyId is null || u.CompanyId == project.OrganizationId))) throw new PdmRuleException("分配人员不存在、已停用或不属于本公司。");
            if (p.Weight <= 0 || p.Weight > 1000) throw new PdmRuleException("人员分配权重须大于0且不超过1000。");
            ProjectBonusRules.Text(p.Id, "人员行标识", 100);
            ProjectBonusRules.Text(p.Reason, "贡献说明", 2000, input.StartScoring || p.Weight != template.SuggestedWeight);
        }
        var app = new BonusApplication(phase, input.StartScoring ? "Scoring" : "Draft", actor, input.People,
            input.Evidence ?? [], ProjectBonusRules.Text(input.MilestoneEvidence, "阶段凭证", 2000, input.StartScoring), settings, [], [], [], [], null,
            [.. old?.Audit ?? [], new(actor, input.StartScoring ? "发起部门评分" : "保存草稿", "人员、指标与权重已记录。", clock.GetUtcNow())]);
        if (app.Evidence.Count > 3100) throw new PdmRuleException("证据条目过多。");
        ProjectBonusRules.Unique(app.Evidence.Select(e => e.PersonId + ":" + e.IndicatorId));
        foreach (var e in app.Evidence) { ProjectBonusRules.Indicator(app, e.PersonId, e.IndicatorId); ProjectBonusRules.Text(e.Text, "指标证据", 2000, false); }
        if (input.StartScoring)
        {
            if (!settings.Enabled) throw new PdmRuleException("请先在系统设置的审批流程中配置并启用项目奖金规则。");
            var pool = book.FrozenPool ?? await Pool(project, ct);
            if (pool is null || pool <= 0) throw new PdmRuleException("当前奖金池未录入或为0，请先核对项目结算与奖金比例。");
            if (phase == "Acceptance" && !book.Applications.Any(a => a.Phase == "Shipment" && a.Status is "Approved" or "Paid"))
                throw new PdmRuleException("请先完成发货阶段奖金审批，再申请终验收阶段。");
            var tasks = new List<BonusScoringTask>();
            async Task Add(string personId, string? target, IReadOnlyList<BonusIndicator> indicators)
            {
                foreach (var i in indicators)
                {
                    if (!app.Evidence.Any(e => e.PersonId == personId && e.IndicatorId == i.Id && !string.IsNullOrWhiteSpace(e.Text)))
                        throw new PdmRuleException($"请补充“{i.Name}”的评分依据。");
                    foreach (var d in i.Departments)
                    {
                        var members = await Members(settings, d.DepartmentKey, project, ct);
                        members = members.Where(m => !Same(m, target)).ToArray();
                        if (members.Count == 0) throw new PdmRuleException($"“{i.Name}”没有可独立评分的人员，不能给本人评分，请调整部门评分人员。");
                        tasks.Add(new(i.Id, personId, d.DepartmentKey, members));
                    }
                }
            }
            await Add("", actor, settings.ProjectIndicators);
            foreach (var p in app.People) await Add(p.Id, p.Username, settings.Roles.Single(r => r.Id == p.RoleId).Indicators);
            var steps = new List<ExpenseApprovalStep>();
            foreach (var node in settings.ApprovalNodes)
            {
                var branches = new List<ApprovalDepartmentBranch>();
                foreach (var key in node.DepartmentKeys)
                {
                    var d = settings.Departments.Single(d => d.Key == key);
                    var members = await Members(settings, key, project, ct, true);
                    branches.Add(new(d.UnitId!.Value, d.Name, members, false, []));
                }
                if (branches.Select(b => b.UnitId).Distinct().Count() != branches.Count) throw new PdmRuleException("同一并行节点不能重复配置同一个实际部门。");
                var group = new ApprovalGroup(branches.SelectMany(b => b.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), branches.Count > 1, [])
                    { Departments = branches, Policy = new(true, false, true) };
                steps.Add(new(Guid.NewGuid(), node.Name, group));
            }
            app = app with { ScoringTasks = tasks, Steps = steps };
            book = book with { FrozenPool = pool };
        }
        await SaveBook(book, app, input.ExpectedRowVersion, ct);
        return await GetAsync(project.Id, actor, role, ct);
    }
    private async Task<IReadOnlyList<string>> Members(ProjectBonusSettings settings, string key, Project project, CancellationToken ct, bool approval = false)
    {
        var department = settings.Departments.Single(d => d.Key == key);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        if (!directory.Units.Any(u => u.Id == department.UnitId && u.IsActive && u.OrganizationId == project.OrganizationId)) throw new PdmRuleException($"“{department.Name}”部门不可用，请检查配置。");
        var names = new List<string>();
        foreach (var name in department.Reviewers ?? [])
        {
            var user = directory.Users.FirstOrDefault(u => u.IsActive && Same(u.Username, name) && (u.CompanyId is null || u.CompanyId == project.OrganizationId));
            if (user is not null && directory.Memberships.Any(m => m.UnitId == department.UnitId && Same(m.Username, name))
                && await repository.HasProjectContentReadAccessAsync(project.Id, name, user.Role, ct)
                && (!approval || await repository.HasUserPermissionAsync(name, user.Role, PermissionCodes.ApprovalDecide, ct))) names.Add(name);
        }
        if (names.Count == 0) throw new PdmRuleException($"“{department.Name}”没有可处理本项目的有效人员，请检查部门配置和项目权限。");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public async Task<ProjectBonusView> ScoreAsync(Guid id, string phase, SaveBonusScores input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(id, actor, role, ct);
        var (book, app) = await Current(project.Id, phase, ct);
        if (app.Status != "Scoring") throw new PdmConflictException("当前申请不在部门评分阶段。");
        if (input.Scores is null || input.Scores.Count is < 1 or > 3100) throw new PdmRuleException("请填写评分。");
        ProjectBonusRules.Unique(input.Scores.Select(s => s.PersonId + ":" + s.IndicatorId + ":" + s.DepartmentKey));
        var scores = app.Scores.ToList();
        foreach (var score in input.Scores)
        {
            var task = app.ScoringTasks.SingleOrDefault(t => t.PersonId == score.PersonId && t.IndicatorId == score.IndicatorId && t.DepartmentKey == score.DepartmentKey);
            if (task is null || !task.Members.Any(m => Same(m, actor))) throw new UnauthorizedAccessException("只能填写本人负责部门的评分。");
            // Recheck active department membership even when a frozen task still contains the old user.
            if (!(await Members(app.Template!, task.DepartmentKey, project, ct)).Any(m => Same(m, actor))) throw new UnauthorizedAccessException("您已不属于该评分部门。");
            var indicator = ProjectBonusRules.Indicator(app, score.PersonId, score.IndicatorId);
            if (score.Score is null || score.Score < 0 || score.Score > indicator.MaxScore) throw new PdmRuleException($"“{indicator.Name}”分数须在0至{indicator.MaxScore}之间。");
            var comment = ProjectBonusRules.Text(score.Comment, "评分依据");
            scores.RemoveAll(s => s.PersonId == score.PersonId && s.IndicatorId == score.IndicatorId && s.DepartmentKey == score.DepartmentKey);
            scores.Add(score with { Comment = comment, Actor = actor, At = clock.GetUtcNow() });
        }
        app = app with { Scores = scores, Audit = [.. app.Audit, new(actor, "提交部门评分", $"提交{input.Scores.Count}项评分。", clock.GetUtcNow())] };
        if (app.ScoringTasks.All(t => scores.Any(s => s.PersonId == t.PersonId && s.IndicatorId == t.IndicatorId && s.DepartmentKey == t.DepartmentKey)))
        {
            var result = ProjectBonusRules.Calculate(book.FrozenPool!.Value, app);
            app = app with { Status = "Scored", Awards = result.Awards, ProjectScore = result.ProjectScore };
        }
        await SaveBook(book, app, input.ExpectedRowVersion, ct);
        return await GetAsync(project.Id, actor, role, ct);
    }
    public async Task<ProjectBonusView> ActAsync(Guid id, string phase, string action, BonusAction input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(id, actor, role, ct);
        var (book, app) = await Current(project.Id, phase, ct);
        var comment = ProjectBonusRules.Text(input.Comment, "处理意见", 2000, action != "submit");
        var now = clock.GetUtcNow();
        string name;
        if (action == "withdraw")
        {
            if (!Manager(project, actor)) throw new UnauthorizedAccessException("仅项目经理可撤回申请。");
            if (app.Status is not ("Scoring" or "Scored" or "Pending")) throw new PdmConflictException("当前状态不能撤回。");
            name = "撤回申请"; app = app with { Status = "Withdrawn" };
        }
        else if (action == "submit")
        {
            if (!Manager(project, actor)) throw new UnauthorizedAccessException("仅项目经理可确认分配并提交审批。");
            if (app.Status != "Scored") throw new PdmConflictException("请等待全部部门完成评分。");
            var result = ProjectBonusRules.Calculate(book.FrozenPool!.Value, app);
            name = "项目经理确认分配并提交"; app = app with { Status = "Pending", ProjectScore = result.ProjectScore, Awards = result.Awards };
        }
        else if (action is "approve" or "reject")
        {
            var step = app.Status == "Pending" ? app.Steps.FirstOrDefault(s => s.Decision is null) : null;
            if (step is null) throw new PdmConflictException("当前没有待处理的审批节点。");
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, ct) || !step.Participants.CanSign(actor)) throw new UnauthorizedAccessException("您不是当前节点的待审批人员。");
            var approve = action == "approve";
            var group = ApprovalNodeRules.Sign(step.Participants, actor, approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected, comment, now, false, out var waiting)!;
            var changed = step with { Participants = group, Decision = waiting ? null : approve ? "Approved" : "Rejected", Actor = actor, Comment = comment, DecidedAt = now };
            var steps = app.Steps.Select(s => s.Id == step.Id ? changed : s).ToArray();
            name = (approve ? "同意 · " : "退回 · ") + step.Name;
            app = app with { Steps = steps, Status = !approve ? "Rejected" : steps.All(s => s.Decision == "Approved") ? "Approved" : "Pending" };
        }
        else throw new PdmRuleException("不支持的奖金操作。");
        app = app with { Audit = [.. app.Audit, new(actor, name, comment, now)] };
        await SaveBook(book, app, input.ExpectedRowVersion, ct);
        return await GetAsync(project.Id, actor, role, ct);
    }
    public async Task<ProjectBonusView> PayAsync(Guid id, string phase, BonusPaymentCommand input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(id, actor, role, ct);
        var (book, app) = await Current(project.Id, phase, ct);
        if (!PaymentMember(app, actor) || !(await Members(app.Template!, app.Template!.PaymentDepartmentKey, project, ct)).Any(m => Same(m, actor)))
            throw new UnauthorizedAccessException("仅配置的财务人员可登记发放。");
        if (app.Status != "Approved" || app.Payment is not null) throw new PdmConflictException("当前申请尚未批准或已经登记发放。");
        if (input.Amount != app.Awards.Sum(a => a.Amount) || input.Date == DateOnly.MinValue || input.Date > DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new PdmRuleException("登记金额须等于批准分配总额，发放日期不得为空或晚于今天。");
        var voucher = ProjectBonusRules.Text(input.Voucher, "发放凭证", 500);
        app = app with { Status = "Paid", Payment = new(input.Amount, input.Date, voucher, actor, clock.GetUtcNow()),
            Audit = [.. app.Audit, new(actor, "登记奖金发放", voucher, clock.GetUtcNow())] };
        await SaveBook(book, app, input.ExpectedRowVersion, ct);
        return await GetAsync(project.Id, actor, role, ct);
    }
    public async Task<IReadOnlyList<ProjectBonusBook>> HistoryAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(id, actor, role, ct);
        // Historical versions can contain former employees' allocations; restrict to the current project manager.
        if (!Manager(project, actor)) throw new UnauthorizedAccessException("仅项目经理可查看完整历史分配版本。");
        return await store.HistoryAsync(project.Id, ct);
    }
    private async Task<(ProjectBonusBook, BonusApplication)> Current(Guid id, string phase, CancellationToken ct)
    {
        ProjectBonusRules.Phase(phase);
        var book = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("奖金申请不存在。");
        return (book, book.Applications.SingleOrDefault(a => a.Phase == phase) ?? throw new PdmNotFoundException("该阶段尚未申请奖金。"));
    }
    private async Task SaveBook(ProjectBonusBook book, BonusApplication app, long expected, CancellationToken ct)
    {
        var changed = book with { Applications = book.Applications.Where(a => a.Phase != app.Phase).Append(app).ToArray() };
        var notifications = new List<UserNotification>();
        foreach (var pending in changed.Applications)
        {
            var recipients = pending.Status == "Scoring" ? pending.ScoringTasks.Where(t => !pending.Scores.Any(s => s.PersonId == t.PersonId && s.IndicatorId == t.IndicatorId && s.DepartmentKey == t.DepartmentKey)).SelectMany(t => t.Members)
                : pending.Status == "Pending" ? pending.Steps.First(s => s.Decision is null).Participants.Remaining()
                : pending.Status == "Scored" ? [pending.Applicant]
                : pending.Status == "Approved" ? pending.Template!.Departments.Single(d => d.Key == pending.Template.PaymentDepartmentKey).Reviewers ?? [] : Array.Empty<string>();
            foreach (var user in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
                notifications.Add(new(Guid.NewGuid(), user, "ProjectBonusPending", pending.Status == "Scoring" ? "项目奖金待评分" : pending.Status == "Scored" ? "项目奖金待确认分配" : pending.Status == "Approved" ? "项目奖金待登记发放" : "项目奖金待审批",
                    $"请进入项目预算的项目奖金页签处理{(pending.Phase == "Shipment" ? "发货" : "终验收")}阶段申请。", book.ProjectId, null,
                    $"project-bonus:{book.ProjectId:N}:{expected + 1}:{pending.Phase}", clock.GetUtcNow(), null));
        }
        if (app.Status is "Approved" or "Rejected" or "Paid")
            notifications.Add(new(Guid.NewGuid(), app.Applicant, "ProjectBonusResult", app.Status == "Approved" ? "项目奖金已批准" : app.Status == "Paid" ? "项目奖金已登记发放" : "项目奖金已退回",
                "请进入项目奖金查看处理记录。", book.ProjectId, null, $"project-bonus:{book.ProjectId:N}:{expected + 1}:result", clock.GetUtcNow(), null));
        await store.SaveAsync(changed, expected, notifications, ct);
    }
}
