using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PurchaseAnalysisService(IPurchaseAnalysisStore store, IPdmRepository repository,
    IMaterialRepository materials, IU9OpenApiClient u9, IU9SecretProtector secrets, TimeProvider clock)
{
    public async Task<PurchaseAnalysisResult> QueryAsync(PurchaseAnalysisFilter filter, string actor, bool export, CancellationToken ct)
    {
        var company = Company();
        var user = await repository.FindUserAsync(actor, ct) ?? throw new UnauthorizedAccessException("请重新登录。");
        var authorization = await repository.GetAuthorizationSettingsAsync(ct);
        var global = new Dictionary<string, bool>();
        bool Global(string code) {
            if (!global.TryGetValue(code, out var allowed)) global[code] = allowed = AuthorizationPolicy.GlobalAllows(authorization, user, company, code);
            return allowed;
        }
        var snapshot = await store.ReadAsync(company, ct);
        var settings = await store.SettingsAsync(company, ct);
        var projects = (await repository.ListProjectHeadersAsync(ct)).Where(p => p.OrganizationId == company).ToArray();
        var projectByCode = projects.GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var permissions = new Dictionary<Guid, HashSet<string>>();
        var needed = AuthorizationPolicy.PotentialPermissions(authorization, user, company)
            .Where(c => c is PurchaseAnalysisPermissions.View or PurchaseAnalysisPermissions.Cost or PurchaseAnalysisPermissions.Export).Where(c => !Global(c)).ToArray();
        foreach (var project in projects.Where(_ => needed.Length > 0))
        {
            var context = await AuthorizationPolicy.CreateContextAsync(repository, project.Id, actor, ct);
            permissions[project.Id] = context is null ? [] : AuthorizationPolicy.Decisions(authorization, context).Where(d => d.Allowed).Select(d => d.Permission).ToHashSet();
        }
        bool Allows(PurchaseAnalysisLine row, string code)
        {
            if (Global(code)) return true;
            var projectCode = string.IsNullOrWhiteSpace(row.SubprojectCode) ? row.ProjectCode : row.SubprojectCode;
            if (projectByCode.TryGetValue(projectCode, out var project)) return permissions.GetValueOrDefault(project.Id)?.Contains(code) == true;
            // Unmapped / non-project purchases are company data, never granted by project relationships.
            return Global(code);
        }
        if (!Global(PurchaseAnalysisPermissions.View) && !permissions.Values.Any(p => p.Contains(PurchaseAnalysisPermissions.View)))
            throw new UnauthorizedAccessException("当前账号未获采购分析查看权限。");
        if (export && !Global(PurchaseAnalysisPermissions.Export) && !permissions.Values.Any(p => p.Contains(PurchaseAnalysisPermissions.Export)))
            throw new UnauthorizedAccessException("当前账号未获采购分析导出权限。");
        var canCost = Global(PurchaseAnalysisPermissions.Cost) || permissions.Values.Any(p => p.Contains(PurchaseAnalysisPermissions.Cost));
        var allowed = snapshot.Lines.Where(r => settings.Organizations.Contains(r.Organization, StringComparer.OrdinalIgnoreCase)
            && Allows(r, PurchaseAnalysisPermissions.View) && (!export || Allows(r, PurchaseAnalysisPermissions.Export)))
            .Select(r => Allows(r, PurchaseAnalysisPermissions.Cost) ? r : r with { NetAmount = null }).ToArray();
        if (filter.Categories is null) throw new PdmRuleException("物料分类条件无效，请重新选择。");
        var categories = await materials.ListCategoriesAsync(true, ct);
        // Category children are resolved against the existing authoritative catalogue, not code-prefix guesses.
        if (filter.Categories.Length > 0)
        {
            var selected = filter.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool changed;
            do { changed = false; foreach (var category in categories) if (category.ParentCode is not null && selected.Contains(category.ParentCode)) changed |= selected.Add(category.Code); } while (changed);
            filter = filter with { Categories = selected.ToArray() };
        }
        var result = PurchaseAnalysisEngine.Build(allowed, snapshot.Status with { SourceCount = allowed.Length }, filter,
            DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).Date), canCost,
            Global(PurchaseAnalysisPermissions.Export) || permissions.Values.Any(p => p.Contains(PurchaseAnalysisPermissions.Export)),
            Global(PurchaseAnalysisPermissions.Synchronize), Global(PurchaseAnalysisPermissions.Settings), export);
        var byCategory = categories.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var optionMap = result.Options["categories"].ToDictionary(o => o.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var option in optionMap.Values.ToArray()) {
            var code = option.Value; var visited = new HashSet<string>();
            while (visited.Add(code) && byCategory.TryGetValue(code, out var category) && category.ParentCode is { } parent && byCategory.TryGetValue(parent, out var node)) {
                optionMap.TryAdd(parent, new(parent, node.Name + "（含下级）")); code = parent;
            }
        }
        var options = result.Options.ToDictionary(p => p.Key, p => p.Value); options["categories"] = optionMap.Values.OrderBy(o => o.Value).ToArray();
        return result with { Options = options };
    }

    public Task<PurchaseAnalysisSettings> SettingsAsync(CancellationToken ct) => store.SettingsAsync(Company(), ct);
    public async Task SaveSettingsAsync(PurchaseAnalysisSettings input, string actor, CancellationToken ct)
    {
        if (input.Organizations is null || input.Organizations.Length is < 1 or > 20 || input.HistoryMonths is < 1 or > 120
            || input.IntervalMinutes is < 15 or > 1440 || input.IntervalMinutes % 15 != 0
            || input.Organizations.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 100 || s.Any(c => char.IsControl(c) || c == ',')))
            throw new PdmRuleException("请填写1至20个U9C组织编码；历史范围为1至120个月，同步间隔为15至1440分钟且按15分钟递增。");
        var settings = input with { Organizations = input.Organizations.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        await store.SaveSettingsAsync(Company(), settings, ct);
        await AuditAsync(actor, "purchase-analysis.settings", Company(), "更新采购分析组织和历史采集范围。", ct);
    }

    public async Task SynchronizeAsync(Guid company, string actor, CancellationToken ct)
    {
        var settings = await store.SettingsAsync(company, ct);
        if (settings.Organizations.Length == 0) throw new PdmRuleException("请先配置当前公司的U9C组织编码。");
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        if (string.IsNullOrWhiteSpace(config.ClientSecretCiphertext)) throw new PdmRuleException("请先完善U9C接口设置。");
        var start = clock.GetUtcNow(); var through = DateOnly.FromDateTime(start.ToOffset(TimeSpan.FromHours(8)).Date);
        var from = through.AddMonths(-settings.HistoryMonths).AddDays(1);
        var rows = new List<PurchaseAnalysisLine>();
        foreach (var organization in settings.Organizations)
        {
            var auth = await u9.AuthenticateAsync(new(config.BaseUrl, config.EnterpriseCode, organization, config.UserCode,
                config.ClientId, secrets.Unprotect(config.ClientSecretCiphertext)), ct);
            rows.AddRange(await u9.QueryPurchaseAnalysisAsync(config.BaseUrl, auth.Token, organization, from, through, ct));
        }
        if (rows.Any(r => !settings.Organizations.Contains(r.Organization) || r.Date < from || r.Date > through)
            || rows.Select(r => (r.Organization, r.Id)).Distinct().Count() != rows.Count)
            throw new PdmRuleException("采购源数据存在重复或范围不一致，已保留上次完整快照。");
        await store.ReplaceAsync(company, settings.Revision, new(new(start, clock.GetUtcNow(), from, through, null, rows.Count), rows), ct);
        await AuditAsync(actor, "purchase-analysis.synchronize", company, $"只读采集{rows.Count}行采购记录，范围{from:yyyy-MM-dd}至{through:yyyy-MM-dd}。", ct);
    }
    private Task AuditAsync(string actor, string action, Guid company, string text, CancellationToken ct) =>
        repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "PurchaseAnalysis", company.ToString(), text), ct);
    private static Guid Company() => TenantContext.CompanyId ?? throw new UnauthorizedAccessException("请先选择公司。");
}
