using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class AuthorizationEndpoints
{
    public static void MapAuthorizationCenter(this WebApplication app)
    {
        var api = app.MapGroup("/api/authorization-center").RequireAuthorization();
        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, HttpContext http, IPdmRepository repository, CancellationToken ct) =>
        {
            if (!await AuthorizationPolicy.CanAsync(repository, projectId, Actor(http), "project.templates.assign", ct)) throw new UnauthorizedAccessException("当前账号没有分配此项目岗位模板的权限。");
            var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
            var settings = await repository.GetAuthorizationSettingsAsync(ct);
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            return Results.Ok(new { settings.Revision, Templates = settings.Templates.Where(t => t.IsActive && t.ProjectAssignable),
                Assignments = settings.Assignments.Where(a => a.TargetId == projectId && a.CompanyId == project.OrganizationId && a.Scope is "project" or "subtree"),
                Users = directory.Users.Where(u => u.IsActive && u.CompanyId == project.OrganizationId).Select(u => new { u.Username, u.DisplayName }) });
        });
        api.MapPut("/projects/{projectId:guid}", async (Guid projectId, ProjectAuthorizationRequest request, HttpContext http, IPdmRepository repository, CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (!await AuthorizationPolicy.CanAsync(repository, projectId, actor, "project.templates.assign", ct)) throw new UnauthorizedAccessException("当前账号没有分配此项目岗位模板的权限。");
            var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
            var settings = await repository.GetAuthorizationSettingsAsync(ct);
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            if (request.Assignments is null || request.Assignments.Count > 1000 || request.Assignments.Select(a => a.Id).Distinct().Count() != request.Assignments.Count) throw new PdmRuleException("项目授权记录无效或重复。");
            var allProjects = await repository.ListProjectsAsync(ct);
            var previous = settings.Assignments.Where(a => a.CompanyId == project.OrganizationId && a.TargetId == projectId && a.Scope is "project" or "subtree").ToArray();
            foreach (var assignment in request.Assignments)
            {
                if (previous.Contains(assignment) && !settings.Templates.Any(t => t.Key == assignment.TemplateKey && t.ProjectAssignable)) continue;
                var template = settings.Templates.FirstOrDefault(t => t.Key == assignment.TemplateKey && t.IsActive && t.ProjectAssignable);
                if (template is null || template.Grants.Any(BlocksProjectAssignment)
                    || assignment.TargetId != projectId || assignment.CompanyId != project.OrganizationId || assignment.Scope is not ("project" or "subtree")
                    || !directory.Users.Any(u => u.IsActive && u.CompanyId == project.OrganizationId && u.Username.Equals(assignment.Username, StringComparison.OrdinalIgnoreCase)))
                    throw new PdmRuleException("只能给本公司在职人员分配当前项目范围内已批准的岗位模板。");
                if (assignment.Scope == "subtree")
                    foreach (var child in allProjects.Where(p => AuthorizationPolicy.ProjectAncestors(p, allProjects).Contains(projectId)))
                        if (!await AuthorizationPolicy.CanAsync(repository, child.Id, actor, "project.templates.assign", ct)) throw new UnauthorizedAccessException("当前账号不能管理全部子项目，请使用仅本项目范围。");
            }
            if (previous.Any(a => !settings.Templates.Any(t => t.Key == a.TemplateKey && t.ProjectAssignable) && !request.Assignments.Contains(a)))
                throw new UnauthorizedAccessException("管理员直接分配的岗位请在权限中心维护。");
            var combined = settings.Assignments.Except(previous).Concat(request.Assignments).ToArray();
            if (combined.Select(a => a.Id).Distinct().Count() != combined.Length || request.Assignments.Any(a => a.Id == Guid.Empty)) throw new PdmRuleException("项目授权标识无效或重复，请刷新后重试。");
            var saved = await repository.SaveAuthorizationSettingsAsync(settings with { Assignments = combined }, request.Revision, ct);
            await repository.AppendAuditAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, actor, "authorization.project.assign", nameof(Project), projectId.ToString(), $"项目岗位授权更新为 {request.Assignments.Count} 条；配置版本 {saved.Revision}"), ct);
            return Results.Ok(new { saved.Revision });
        });
        api.MapGet("/", async (HttpContext http, IPdmRepository repository, CancellationToken ct) =>
        {
            await Require(repository, http, PermissionCodes.RoleSettingsView, ct);
            var settings = await repository.GetAuthorizationSettingsAsync(ct);
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var company = TenantContext.CompanyId;
            return Results.Ok(new { settings = settings with { Assignments = settings.Assignments.Where(a => company is null || a.CompanyId == company).ToArray() }, permissions = RolePermissionCatalog.Permissions.Where(p => !p.Hidden), scopes = AuthorizationActions.Scopes,
                defaultRoleGrants = AuthorizationPolicy.NewRoleDefaultGrants,
                users = directory.Users.Where(u => company is null || u.CompanyId == company).Select(u => new { u.Username, u.DisplayName, u.CompanyId, role = u.EffectiveRoleCode, roles = u.EffectiveRoleCodes, u.IsActive }),
                projects = (await repository.ListProjectsAsync(ct)).Select(p => new { p.Id, p.Code, p.Name, p.ParentProjectId, p.OrganizationId }),
                departments = directory.Units.Where(u => u.IsActive && (company is null || u.OrganizationId == company)),
                canEdit = await repository.HasUserPermissionAsync(Actor(http), Role(http), PermissionCodes.RoleSettingsEdit, ct) });
        });
        api.MapPut("/", async (AuthorizationSettings settings, HttpContext http, IPdmRepository repository, CancellationToken ct) =>
        {
            await Require(repository, http, PermissionCodes.RoleSettingsEdit, ct);
            var previous = await repository.GetAuthorizationSettingsAsync(ct);
            Validate(settings, previous);
            settings = settings with { Templates = settings.Templates.Select(role => role.RoleCode is null ? AuthorizationPolicy.ApplyNewRoleDefaults(role) : role).ToArray() };
            if (TenantContext.CompanyId is Guid activeCompany && settings.Assignments.Any(a => a.CompanyId != activeCompany)) throw new UnauthorizedAccessException("只能修改当前公司的人员授权。");
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var projects = await repository.ListProjectsAsync(ct);
            foreach (var assignment in settings.Assignments)
            {
                var user = directory.Users.FirstOrDefault(u => u.Username.Equals(assignment.Username, StringComparison.OrdinalIgnoreCase));
                if (user is null || user.CompanyId != assignment.CompanyId && !(user.CrossCompanyView && (user.AccessibleCompanyIds ?? []).Contains(assignment.CompanyId)))
                    throw new PdmRuleException("授权人员与公司不匹配，请先核对账号的公司访问范围。");
                if (assignment.Scope is "project" or "subtree" && !projects.Any(p => p.Id == assignment.TargetId && p.OrganizationId == assignment.CompanyId))
                    throw new PdmRuleException("授权项目不存在或不属于所选公司。");
                if (assignment.Scope == "department" && !directory.Units.Any(u => u.Id == assignment.TargetId && u.OrganizationId == assignment.CompanyId && u.IsActive))
                    throw new PdmRuleException("授权部门不存在或不属于所选公司。");
                if (assignment.Scope != "company" && settings.Templates.Single(t => t.Key == assignment.TemplateKey).Grants.Any(BlocksProjectAssignment))
                    throw new PdmRuleException("项目或部门授权只能使用包含项目操作的模板，请复制模板并移除系统管理操作。");
            }
            settings = settings with { Assignments = previous.Assignments.Where(a => TenantContext.CompanyId is Guid active && a.CompanyId != active).Concat(settings.Assignments).ToArray() };
            var users = await repository.ListUsersAsync(ct);
            if (!users.Any(u => AuthorizationPolicy.GlobalAllows(settings, u, u.CompanyId, PermissionCodes.RoleSettingsEdit) && AuthorizationPolicy.GlobalAllows(settings, u, u.CompanyId, PermissionCodes.RoleSettingsView)))
                throw new PdmRuleException("至少保留一位启用且能管理权限的人员，避免权限中心无法维护。");
            var saved = await repository.SaveAuthorizationSettingsAsync(settings with
            {
                MigratedAt = previous.MigratedAt,
                Templates = settings.Templates.Select(role => role with
                {
                    Name = role.Name.Trim(),
                    Description = role.Description?.Trim() ?? string.Empty
                }).ToArray()
            }, settings.Revision, ct, updateRoles: true);
            await repository.AppendAuditAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, Actor(http), "authorization.update", "AuthorizationSettings", saved.Revision.ToString(),
                $"权限中心版本 {previous.Revision} → {saved.Revision}；岗位模板 {saved.Templates.Count} 个，指定范围授权 {saved.Assignments.Count} 条"), ct);
            return Results.Ok(saved with { Assignments = saved.Assignments.Where(a => TenantContext.CompanyId is null || a.CompanyId == TenantContext.CompanyId).ToArray() });
        });
        api.MapGet("/preview", async (string username, Guid? projectId, HttpContext http, IPdmRepository repository, CancellationToken ct) =>
        {
            await Require(repository, http, PermissionCodes.RoleSettingsView, ct);
            var user = await repository.FindUserAsync(username, ct) ?? throw new PdmNotFoundException("账号不存在。");
            var company = TenantContext.CompanyId ?? user.CompanyId;
            if (user.CompanyId != company) throw new UnauthorizedAccessException("仅可检查当前公司的人员权限。");
            var settings = await repository.GetAuthorizationSettingsAsync(ct);
            var context = projectId is Guid id ? await AuthorizationPolicy.CreateContextAsync(repository, id, username, ct) : null;
            if (projectId is not null && context is null) throw new UnauthorizedAccessException("所选人员不能访问此项目所在公司，或项目已不存在。");
            var decisions = context is null ? [] : AuthorizationPolicy.Decisions(settings, context);
            return Results.Ok(RolePermissionCatalog.Permissions.Where(p => !p.Hidden && (context is not null || p.Scope != "project")).Select(p => context is not null && p.Scope == "project"
                ? decisions.Single(d => d.Permission == p.Code)
                : new AuthorizationDecision(p.Code, AuthorizationPolicy.GlobalAllows(settings, user, company, p.Code),
                    p.Scope == "project" ? "请选择具体项目检查范围与前置权限" : "按权限中心的公司岗位及指定授权计算", [])));
        });
        api.MapGet("/mine", async (HttpContext http, IPdmRepository repository, CancellationToken ct) =>
            Results.Ok(new { revision = (await repository.GetAuthorizationSettingsAsync(ct)).Revision,
                permissions = await repository.GetUserPermissionsAsync(Actor(http), Role(http), ct) }));
    }

    private static void Validate(AuthorizationSettings settings, AuthorizationSettings previous)
    {
        if (settings.Templates is null || settings.Assignments is null || settings.Positions is null || settings.Templates.Count > 500 || settings.Assignments.Count > 20000)
            throw new PdmRuleException("权限配置格式无效或超出允许数量。");
        if (settings.Revision != previous.Revision) throw new PdmConflictException("权限配置已被修改，请刷新后重试。");
        if (!settings.Positions.SequenceEqual(previous.Positions)) throw new PdmRuleException("历史项目岗位定义不能通过权限配置覆盖，请使用岗位模板及人员授权。");
        var permissions = RolePermissionCatalog.Permissions.Where(p => !p.Hidden).Select(p => p.Code).ToHashSet();
        var scopes = AuthorizationActions.Scopes.Keys.Concat(settings.Positions.Select(p => p.Key)).ToHashSet();
        if (settings.Templates.Select(t => t.Key).Distinct().Count() != settings.Templates.Count || settings.Assignments.Select(a => a.Id).Distinct().Count() != settings.Assignments.Count)
            throw new PdmRuleException("角色标识或授权记录重复。");
        if (settings.Templates.Select(t => t.Name?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Templates.Count)
            throw new PdmRuleException("角色名称不能重复。");
        var previousRoles = previous.Templates.ToDictionary(t => t.Key);
        foreach (var template in settings.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.Key) || string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 80 || template.Description?.Length > 300 || template.Grants is null
                || template.Grants.Select(g => g.Permission).Distinct().Count() != template.Grants.Count
                || template.Grants.Any(g => !permissions.Contains(g.Permission) || g.Scopes is null || g.Scopes.Any(s => !scopes.Contains(s))))
                throw new PdmRuleException("请填写角色名称和说明，并检查重复操作及适用范围。名称最多80字，说明最多300字。");
            if (template.RoleCode != previousRoles.GetValueOrDefault(template.Key)?.RoleCode)
                throw new PdmRuleException("角色编码由系统维护，请刷新角色后重试。");
            if (template.Grants.Any(g => RolePermissionCatalog.Permissions.First(p => p.Code == g.Permission).Scope != "project" && g.Scopes.Any(s => s != "company"))) throw new PdmRuleException("公司级操作不能使用项目关系范围。");
            if (template.ProjectAssignable && template.Grants.Any(BlocksProjectAssignment))
                throw new PdmRuleException("允许项目分工分配的模板只能包含项目操作，请移除系统管理操作。");
        }
        foreach (var assignment in settings.Assignments)
            if (assignment.Id == Guid.Empty || assignment.CompanyId == Guid.Empty || string.IsNullOrWhiteSpace(assignment.Username)
                || !settings.Templates.Any(t => t.Key == assignment.TemplateKey) || assignment.Scope is not ("company" or "department" or "project" or "subtree")
                || (assignment.Scope == "company") != (assignment.TargetId is null)) throw new PdmRuleException("人员授权的模板或适用范围无效。");
    }
    private static bool BlocksProjectAssignment(AuthorizationGrant grant) =>
        grant.Permission != PermissionCodes.DingTalkSelfBind && RolePermissionCatalog.Permissions.First(p => p.Code == grant.Permission).Scope != "project";

    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "";
    private static UserRole Role(HttpContext http) => Enum.TryParse<UserRole>(http.User.FindFirstValue(ClaimTypes.Role), out var role) ? role : UserRole.ProductionViewer;
    private static async Task Require(IPdmRepository repository, HttpContext http, string permission, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(Actor(http), Role(http), permission, ct)) throw new UnauthorizedAccessException("当前账号未获准管理或查看权限中心。");
    }
}

public sealed record ProjectAuthorizationRequest(long Revision, IReadOnlyList<AuthorizationAssignment> Assignments);
