using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record AuthorizationDecision(string Permission, bool Allowed, string Reason, IReadOnlyList<string> Sources);

public static class AuthorizationPolicy
{
    public static IReadOnlyList<AuthorizationGrant> NewRoleDefaultGrants { get; } =
        [new(PermissionCodes.DingTalkSelfBind, ["company"])];

    public static AuthorizationTemplate ApplyNewRoleDefaults(AuthorizationTemplate role)
    {
        var defaults = NewRoleDefaultGrants.Where(grant => role.Grants.All(existing => existing.Permission != grant.Permission));
        // An explicit empty scope list overrides the preset for a newly created role.
        var grants = role.Grants.Where(grant => grant.Scopes.Count > 0 || NewRoleDefaultGrants.All(preset => preset.Permission != grant.Permission));
        return role with { Grants = grants.Concat(defaults).ToArray() };
    }

    public static FolderAccess FolderDefaults(AuthorizationSettings settings, Context context)
    {
        var decisions = Decisions(settings, context).Where(d => d.Allowed).Select(d => d.Permission).ToHashSet();
        if (decisions.Contains(AuthorizationActions.FolderManage)) return FolderAccess.All;
        var access = FolderAccess.None;
        if (decisions.Contains(AuthorizationActions.FolderView)) access |= FolderAccess.View;
        if (decisions.Contains(AuthorizationActions.FolderDownload)) access |= FolderAccess.Download;
        if (decisions.Contains(AuthorizationActions.FolderUpload)) access |= FolderAccess.Upload;
        if (decisions.Contains(AuthorizationActions.FolderEdit)) access |= FolderAccess.Edit;
        return access;
    }
    public static RolePermissionDirectory ProjectDirectory(RolePermissionDirectory directory, AuthorizationSettings? settings) => settings is null ? directory : directory with
    {
        Roles = directory.Roles.Select(role =>
        {
            var template = settings.Templates.FirstOrDefault(t => Same(t.RoleCode, role.Role));
            return role with
            {
                Name = template?.Name ?? role.Name,
                Description = template?.Description ?? role.Description,
                IsSystemAdministrator = false,
                Permissions = template is { IsActive: true }
                    ? template.Grants.Where(g => g.Scopes.Count > 0).Select(g => g.Permission).ToArray()
                    : []
            };
        }).ToArray()
    };

    public static AuthorizationSettings BindRoles(AuthorizationSettings settings)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new List<AuthorizationTemplate>(settings.Templates.Count);
        foreach (var template in settings.Templates)
        {
            var name = template.Name;
            // Earlier configurations allowed duplicate names. Keep every role and its assignments.
            for (var number = 2; !usedNames.Add(name); number++)
            {
                var suffix = $"（{number}）";
                name = template.Name[..Math.Min(template.Name.Length, 80 - suffix.Length)] + suffix;
            }
            templates.Add(template with { Name = name, RoleCode = template.RoleCode ?? $"custom-{Guid.NewGuid():N}" });
        }
        return settings with { Templates = templates, RolesUnified = true };
    }

    public static void CheckRoleDeletion(AuthorizationSettings previous, AuthorizationSettings next, string roleCode, bool isSystem, bool hasMembers)
    {
        if (isSystem) throw new PdmRuleException("系统预置角色不能删除。");
        if (hasMembers) throw new PdmConflictException("该角色仍分配给用户，请先在用户设置中调整角色。");
        var keys = previous.Templates.Where(t => Same(t.RoleCode, roleCode)).Select(t => t.Key).ToHashSet();
        if (next.Assignments.Any(a => keys.Contains(a.TemplateKey)))
            throw new PdmConflictException("该角色仍有指定范围授权，请先撤销人员授权。");
    }

    public static AuthorizationSettings UpdateRole(AuthorizationSettings settings, string role, IReadOnlyList<string> permissions)
    {
        var template = settings.Templates.FirstOrDefault(t => t.RoleCode == role) ?? throw new PdmNotFoundException("岗位模板不存在。");
        var requested = FunctionPermissionCatalog.Expand(permissions.Concat([FunctionPermissionCatalog.SchemaMarker, ProjectCenterPermissions.SchemaMarker])).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(requested);
        while (pending.TryDequeue(out var next)) foreach (var dependency in RolePermissionCatalog.Permissions.FirstOrDefault(p => p.Code == next)?.Dependencies ?? []) if (requested.Add(dependency)) pending.Enqueue(dependency);
        var codes = requested.Where(code => RolePermissionCatalog.Permissions.Any(p => p.Code == code && !p.Hidden)).Distinct().ToArray();
        var updated = template with { Grants = codes.Select(code => new AuthorizationGrant(code, template.Grants.FirstOrDefault(g => g.Permission == code)?.Scopes ?? ["company"])).ToArray() };
        return settings with { Templates = settings.Templates.Select(t => t.Key == template.Key ? updated : t).ToArray() };
    }
    public static Project ApplyCapabilities(AuthorizationSettings settings, Context context)
    {
        var project = context.Project;
        var allowed = Decisions(settings, context).Where(d => d.Allowed && RolePermissionCatalog.Permissions.Any(p => p.Code == d.Permission && p.Scope == "project")).Select(d => d.Permission).ToHashSet();
        var read = allowed.Contains(PermissionCodes.ProjectContentView);
        return project with
        {
            EffectiveProjectPermissions = allowed.ToArray(), CanReadContent = read,
            CanAssignExecutionUnit = project.ParentProjectId is null && allowed.Contains(PermissionCodes.ProjectExecutionAssign),
            CanManageMainStaffing = project.ParentProjectId is null && project.ExecutionUnitId is not null && allowed.Contains(PermissionCodes.ProjectStaffingManage),
            CanAssignDesigners = (project.ExecutionUnitId ?? context.Root.ExecutionUnitId) is not null && allowed.Contains(PermissionCodes.ProjectDesignerAssign),
            CanSubmitArchive = read && allowed.Contains(AuthorizationActions.ArchiveSubmit) && allowed.Contains("document.archive"),
            DocumentCount = read ? project.DocumentCount : null, ModelDocumentCount = read ? project.ModelDocumentCount : null,
            DrawingDocumentCount = read ? project.DrawingDocumentCount : null, BusinessStatus = read ? project.BusinessStatus : null,
            RootDocumentCheckedOutBy = read ? project.RootDocumentCheckedOutBy : null
        };
    }
    public sealed record Context(UserAccount User, Project Project, Project Root, OrganizationDirectory Directory,
        ProjectContactInformation? Contacts = null, string? FolderTemplate = null, IReadOnlySet<Guid>? Ancestors = null);

    public static IReadOnlySet<string> PotentialPermissions(AuthorizationSettings settings, UserAccount user, Guid? companyId)
    {
        if (!user.IsActive) return new HashSet<string>();
        var explicitKeys = settings.Assignments.Where(a => Same(a.Username, user.Username) && a.CompanyId == companyId).Select(a => a.TemplateKey).ToHashSet();
        var codes = settings.Templates.Where(t => t.IsActive && (t.RoleCode is not null && user.EffectiveRoleCodes.Contains(t.RoleCode, StringComparer.OrdinalIgnoreCase) || explicitKeys.Contains(t.Key)))
            .SelectMany(t => t.Grants).Where(g => g.Scopes.Count > 0).Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);
        // 旧聚合权限只作接口兼容查询；不反向展开成已撤销的细粒度操作。
        foreach (var definition in RolePermissionCatalog.Permissions.Where(p => p.LegacyPermission is not null && codes.Contains(p.Code))) codes.Add(definition.LegacyPermission!);
        codes.RemoveWhere(code => RolePermissionCatalog.Permissions.FirstOrDefault(p => p.Code == code) is { Scope: not "project" } && !GlobalAllows(settings, user, companyId, code));
        codes.Add(ProjectCenterPermissions.SchemaMarker);
        return codes;
    }

    public static bool GlobalAllows(AuthorizationSettings settings, UserAccount user, Guid? companyId, string permission)
    {
        if (!user.IsActive) return false;
        var codes = settings.Templates.Where(t => t.IsActive && (t.RoleCode is not null && user.EffectiveRoleCodes.Contains(t.RoleCode, StringComparer.OrdinalIgnoreCase)
            || settings.Assignments.Any(a => a.TemplateKey == t.Key && Same(a.Username, user.Username) && a.CompanyId == companyId && a.Scope == "company")))
            .SelectMany(t => t.Grants).Where(g => g.Scopes.Contains("company")).Select(g => g.Permission).ToHashSet();
        foreach (var p in RolePermissionCatalog.Permissions.Where(p => p.LegacyPermission is not null && codes.Contains(p.Code))) codes.Add(p.LegacyPermission!);
        bool Allows(string code, HashSet<string> visiting)
        {
            if (!codes.Contains(code) || !visiting.Add(code)) return false;
            var definition = RolePermissionCatalog.Permissions.FirstOrDefault(p => p.Code == code);
            var allowed = definition is not null && definition.Dependencies.All(d => Allows(d, visiting));
            visiting.Remove(code); return allowed;
        }
        return Allows(permission, []);
    }

    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, string actor, string permission, CancellationToken ct)
    {
        var context = await CreateContextAsync(repository, projectId, actor, ct);
        if (context is null) return false;
        return Decide(await repository.GetAuthorizationSettingsAsync(ct), context, permission).Allowed;
    }

    public static async Task<Context?> CreateContextAsync(IPdmRepository repository, Guid projectId, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor)) return null;
        var user = await repository.FindUserAsync(actor, ct);
        var project = await repository.FindProjectAsync(projectId, ct);
        if (user is null || !user.IsActive || project is null) return null;
        var root = project.ParentProjectId is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct);
        if (root is null || root.OrganizationId != project.OrganizationId || root.ParentProjectId is not null) return null;
        if (TenantContext.CompanyId is Guid company && company != project.OrganizationId) return null;
        if (TenantContext.Current is null && user.CompanyId is Guid primary && project.OrganizationId is Guid owner && owner != primary)
        {
            var companies = await repository.GetUserCompanyScopeAsync(actor, ct);
            if (companies?.CrossCompanyView != true || !companies.AccessibleCompanyIds.Contains(owner)) return null;
        }
        var ancestors = new HashSet<Guid>();
        var parentId = project.ParentProjectId;
        while (parentId is Guid parent && ancestors.Add(parent))
        {
            var node = parent == root.Id ? root : await repository.FindProjectAsync(parent, ct);
            if (node is null || node.OrganizationId != project.OrganizationId || ancestors.Count > 100) return null;
            parentId = node.ParentProjectId;
        }
        if (parentId is not null) return null;
        return new(user, project, root, await repository.GetOrganizationDirectoryAsync(ct), await repository.FindProjectContactInformationAsync(root.Id, ct), Ancestors: ancestors);
    }

    public static AuthorizationDecision Decide(AuthorizationSettings settings, Context context, string permission) =>
        Decisions(settings, context).FirstOrDefault(d => d.Permission == permission) ?? new(permission, false, "未注册的权限", []);

    public static IReadOnlyList<AuthorizationDecision> Decisions(AuthorizationSettings settings, Context context)
    {
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var matchedScopes = new Dictionary<string, bool>();
        if (context.User.IsActive)
        foreach (var template in settings.Templates.Where(t => t.IsActive))
        {
            var automatic = template.RoleCode is not null && context.User.EffectiveRoleCodes.Contains(template.RoleCode, StringComparer.OrdinalIgnoreCase);
            if (!automatic && !settings.Assignments.Any(a => a.TemplateKey == template.Key && Same(a.Username, context.User.Username) && AssignmentMatches(a, context))) continue;
            foreach (var grant in template.Grants)
            {
                if (grant.Permission == PermissionCodes.DingTalkSelfBind && !automatic
                    && !settings.Assignments.Any(a => a.TemplateKey == template.Key && Same(a.Username, context.User.Username)
                        && a.CompanyId == context.Project.OrganizationId && a.Scope == "company")) continue;
                if (!grant.Scopes.Any(scope => {
                    if (!matchedScopes.TryGetValue(scope, out var matches)) matchedScopes[scope] = matches = ScopeMatches(scope, context, settings.Positions);
                    return matches;
                })) continue;
                if (!sources.TryGetValue(grant.Permission, out var list)) sources[grant.Permission] = list = [];
                list.Add(template.Name + (automatic ? "（公司岗位）" : "（指定范围授权）"));
            }
        }
        foreach (var definition in RolePermissionCatalog.Permissions.Where(p => p.LegacyPermission is not null && sources.ContainsKey(p.Code)))
        {
            if (!sources.TryGetValue(definition.LegacyPermission!, out var list)) sources[definition.LegacyPermission!] = list = [];
            list.AddRange(sources[definition.Code]);
        }
        var definitions = RolePermissionCatalog.Permissions.ToDictionary(p => p.Code);
        var evaluated = new Dictionary<string, bool>();
        bool Allows(string code, HashSet<string> visiting)
        {
            if (evaluated.TryGetValue(code, out var cached)) return cached;
            if (!sources.ContainsKey(code) || !definitions.TryGetValue(code, out var definition)) return evaluated[code] = false;
            if (!visiting.Add(code)) return false;
            var allowed = definition.Dependencies.All(d => Allows(d, visiting));
            visiting.Remove(code);
            return evaluated[code] = allowed;
        }
        return definitions.Values.Select(p => {
            var allowed = Allows(p.Code, []);
            return new AuthorizationDecision(p.Code, allowed, !context.User.IsActive ? "账号已停用" : allowed ? "授权允许；执行时仍须满足目录、锁定及流程状态条件" : sources.ContainsKey(p.Code) ? "缺少当前项目范围内的前置查看或功能权限" : "未在当前项目范围内获得此权限", sources.GetValueOrDefault(p.Code, []).Distinct().ToArray());
        }).ToArray();
    }

    public static bool AssignmentMatches(AuthorizationAssignment assignment, Context context)
    {
        if (assignment.CompanyId != context.Project.OrganizationId) return false;
        return assignment.Scope switch
        {
            "company" => assignment.TargetId is null,
            "project" => assignment.TargetId == context.Project.Id,
            "subtree" => assignment.TargetId == context.Project.Id || assignment.TargetId == context.Root.Id || assignment.TargetId is Guid ancestor && context.Ancestors?.Contains(ancestor) == true,
            "department" => Within(context.Project.ExecutionUnitId ?? context.Root.ExecutionUnitId, assignment.TargetId, context.Directory),
            _ => false
        };
    }

    public static bool ScopeMatches(string scope, Context c, IReadOnlyList<ProjectPositionDefinition> positions)
    {
        var actor = c.User.Username;
        bool Manager(Project p) => Same(p.PrimaryProjectManager, actor) || p.CollaborativeProjectManagers.Any(x => Same(x, actor));
        bool Lead(Project p) => Same(p.DesignLead, actor) || p.DesignLeads.Any(x => Same(x, actor));
        bool Engineer(Project p) => p.Designers.Any(x => Same(x, actor)) || new[] { "ElectricalEngineer", "ElectricalHardware" }.Any(key => Names(p.PhaseOwners.GetValueOrDefault(key, "")).Any(x => Same(x, actor)));
        var unit = c.Project.ExecutionUnitId ?? c.Root.ExecutionUnitId;
        bool Custom(ProjectPositionDefinition p) => p.IsActive && (c.Project.CustomPositionMembers.GetValueOrDefault(p.Key, []).Any(x => Same(x, actor))
            || p.Scope == "subtree" && c.Root.CustomPositionMembers.GetValueOrDefault(p.Key, []).Any(x => Same(x, actor)));
        return scope switch
        {
            "company" => true,
            "MainManager" => Manager(c.Root),
            "ChildManager" => c.Project.ParentProjectId is not null && Manager(c.Project),
            "MainDesigner" => Lead(c.Root) || Lead(c.Project),
            "Engineer" => Engineer(c.Project) || Engineer(c.Root) && c.Root.PhaseOwners.Any(p => p.Key is "ElectricalEngineer" or "ElectricalHardware" && Names(p.Value).Any(x => Same(x, actor))),
            "primary-project-manager" => Same(c.Project.PrimaryProjectManager, actor),
            "root-primary-manager" => Same(c.Root.PrimaryProjectManager, actor),
            "project-manager" => Manager(c.Project),
            "current-designer" => c.Project.Designers.Any(x => Same(x, actor)),
            "root-design-lead" => c.Project.ParentProjectId is null && Lead(c.Project),
            "current-hardware" => Names(c.Project.PhaseOwners.GetValueOrDefault("ElectricalHardware", "")).Any(x => Same(x, actor)),
            "project-lead" => Lead(c.Project),
            "project-designer" => Lead(c.Project) || c.Project.Designers.Any(x => Same(x, actor)),
            "division-manager" => c.Directory.Managers.Any(m => (Same(m.PrimaryManager, actor) || m.CollaborativeManagers.Any(x => Same(x, actor))) && Within(unit, m.UnitId, c.Directory)),
            "division-member" => c.Directory.Memberships.Any(m => Same(m.Username, actor) && Within(m.UnitId, unit, c.Directory)),
            "sales-contact" => Names(c.Contacts?.SalesPeople ?? "").Any(x => Same(x, actor) || Same(x, c.User.DisplayName)),
            "presales-contact" => Names(c.Contacts?.PresalesPeople ?? "").Any(x => Same(x, actor) || Same(x, c.User.DisplayName)),
            "commercial-folder" => c.FolderTemplate is "presales" or "customer-files",
            "release-folder" => c.FolderTemplate == "release",
            "Other" => !Manager(c.Root) && !Manager(c.Project) && !Lead(c.Root) && !Lead(c.Project) && !Engineer(c.Project) && !Engineer(c.Root) && !positions.Where(p => !p.IsSystem).Any(Custom),
            _ => positions.FirstOrDefault(p => p.Key == scope) is { } position && Custom(position)
        };
    }

    public static IReadOnlySet<Guid> ProjectAncestors(Project project, IEnumerable<Project> projects)
    {
        var byId = projects.ToDictionary(p => p.Id);
        var result = new HashSet<Guid>(); var parent = project.ParentProjectId;
        while (parent is Guid id && result.Add(id)) parent = byId.GetValueOrDefault(id)?.ParentProjectId;
        if (project.RootProjectId is Guid root && root != project.Id) result.Add(root);
        return result;
    }
    public static bool Within(Guid? unitId, Guid? ancestor, OrganizationDirectory directory)
    {
        if (ancestor is null) return false;
        var seen = new HashSet<Guid>();
        while (unitId is Guid id && seen.Add(id))
        { if (id == ancestor) return true; unitId = directory.Units.FirstOrDefault(u => u.Id == id && u.IsActive)?.ParentUnitId; }
        return false;
    }
    private static IEnumerable<string> Names(string text) => text.Split(['、', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool PermissionMatches(string grant, string requested) => grant == requested || RolePermissionCatalog.Permissions.Any(p => p.Code == grant && p.LegacyPermission == requested);
}
