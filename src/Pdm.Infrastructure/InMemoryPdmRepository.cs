using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository : IPdmRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, Project> projects = new();
    private readonly ConcurrentDictionary<Guid, PdmDocument> documents = new();
    private readonly ConcurrentDictionary<Guid, DocumentModelDrawingRelation> documentRelations = new();
    private readonly ConcurrentDictionary<Guid, ProjectFolder> projectFolders = new();
    private readonly Dictionary<string, ProjectFolderTemplateNode> folderTemplate = CreateDefaultFolderTemplate();
    private readonly ConcurrentDictionary<Guid, UserAccount> users = new();
    private readonly ConcurrentDictionary<string, UserProfile> userProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, PasswordResetTask> passwordResetTasks = new();
    private readonly ConcurrentDictionary<string, RoleDefinition> roleDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> rolePermissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, PdmCustomer> customers = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> projectResponsibles = new();
    private readonly ConcurrentDictionary<Guid, ProjectOrganization> organizations = new();
    private readonly ConcurrentDictionary<Guid, OrganizationUnit> organizationUnits = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Guid> UnitIds, Guid PrimaryUnitId)> organizationMemberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> userCompanyAccess = new();
    private readonly ConcurrentDictionary<Guid, OrganizationUnitManagers> organizationManagers = new();
    private readonly ConcurrentDictionary<int, EquipmentTypeDefinition> equipmentTypes = new();
    private readonly ConcurrentDictionary<Guid, ReleasePackage> packages = new();
    private readonly ConcurrentDictionary<Guid, DocumentVersion> versions = new();
    private readonly ConcurrentDictionary<Guid, string> documentSourceFingerprints = new();
    private readonly ConcurrentQueue<AuditEntry> audits = new();
    private readonly Dictionary<Guid, int> projectCounters = new();
    private readonly Dictionary<Guid, SortedSet<int>> releasedProjectNumbers = new();
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<Guid, int> serialCounters = new();
    private readonly Dictionary<Guid, SortedSet<int>> releasedSerialNumbers = new();
    private readonly Dictionary<(Guid OrganizationId, string CustomerCode), int> customerCounters = new();
    private readonly Dictionary<(Guid OrganizationId, string CustomerCode), SortedSet<int>> releasedCustomerNumbers = new();
    private readonly List<BomItem> bomItems;
    private readonly Dictionary<(Guid ProjectId, BomKind Kind), BomEmptyDeclaration> bomEmptyDeclarations = new();
    private readonly Dictionary<(Guid ProjectId, ProjectBomHeaderKind Kind), ProjectBomHeaderBinding> projectBomHeaders = new();
    private readonly ConcurrentDictionary<Guid, BomVersion> bomVersions = new();
    private readonly ConcurrentDictionary<Guid, ManufacturingBomBaseline> manufacturingBomBaselines = new();
    private readonly Dictionary<Guid, CadPropertyWriteback> cadPropertyWritebacks = new();
    private DocumentReferenceNode referenceTree;
    private Guid referenceRootDocumentId = SeedData.RootDocumentId;
    private PdmSystemSettings systemSettings = new(@"D:\PDM\Vault", @"D:\PDM\Release")
    {
        MaterialAttachmentRoot = @"D:\PDM\MaterialAttachments"
    };
    private CrmIntegrationConfiguration crmIntegrationConfiguration = new(string.Empty, string.Empty, string.Empty, false, 60, null, 0, null, null);

    public int BomBatchApplyCount { get; private set; }

    public InMemoryPdmRepository(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        foreach (var definition in RolePermissionCatalog.Roles)
        {
            roleDefinitions[definition.RoleCode] = definition;
            rolePermissions[definition.RoleCode] = RolePermissionCatalog.InitialPermissions(definition.RoleCode, definition.BaseRole);
        }
        foreach (var organization in SeedOrganizations()) organizations[organization.Id] = organization;
        var project = SeedData.Project();
        project = project with { ResponsibleUsers = [project.Owner] };
        projects[project.Id] = project;
        projectResponsibles[project.Id] = [project.Owner];
        customers[Guid.Parse("c0046500-0000-0000-0000-000000000001")] = new(
            Guid.Parse("c0046500-0000-0000-0000-000000000001"),
            "C00465",
            "中山比亚迪电子有限公司",
            true,
            "u9c",
            timeProvider.GetUtcNow());
        foreach (var code in Enumerable.Range(0, 100)) equipmentTypes[code] = new(code, $"类型{code:D2}", true);
        foreach (var document in SeedData.Documents(timeProvider.GetUtcNow()))
        {
            documents[document.Id] = document;
        }
        foreach (var drawing in documents.Values.Where(item => item.Kind == DocumentKind.Drawing))
        {
            var model = documents.Values.FirstOrDefault(item => item.ProjectId == drawing.ProjectId
                && item.DrawingNumber.Equals(drawing.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                && item.Kind is DocumentKind.Assembly or DocumentKind.Part);
            if (model is not null) documentRelations[drawing.Id] = new(model.Id, drawing.Id);
        }

        referenceTree = SeedData.Tree(documents);
        bomItems = SeedData.Bom().ToList();
        var package = SeedData.ReleasePackage(timeProvider.GetUtcNow());
        packages[package.Id] = package;
        EnsureProjectFolderTree(project.Id);
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(projects.Values.Where(IsInActiveCompany).OrderBy(project => project.Code).ToArray());

    public Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(!HasUserPermission(actor, role, PermissionCodes.ProjectView) ? [] : projects.Values
            .Where(IsInActiveCompany)
            .Select(project => ApplyCapabilities(project, actor, role))
            .OrderBy(project => project.Code)
            .ToArray());

    public Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        projects.TryGetValue(projectId, out var project);
        if (project is not null && !IsInActiveCompany(project)) project = null;
        return Task.FromResult(project);
    }

    public Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(HasUserPermission(actor, role, PermissionCodes.ProjectView) && projects.TryGetValue(projectId, out var project) && IsInActiveCompany(project));

    public Task<bool> HasProjectContentReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(projects.TryGetValue(projectId, out var project) && IsInActiveCompany(project)
            && HasUserPermission(actor, role, PermissionCodes.ProjectContentView));

    private static bool IsInActiveCompany(Project project) => TenantContext.CompanyId is not Guid companyId || project.OrganizationId is null || project.OrganizationId == companyId;

    public Task<bool> HasChildProjectsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(projects.Values.Any(project => project.ParentProjectId == projectId));

    public Task<IReadOnlyList<ProjectBomHeaderBinding>> ListProjectBomHeaderBindingsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ProjectBomHeaderBinding>>(projectBomHeaders.Values
                .Where(item => item.ProjectId == projectId)
                .OrderBy(item => item.Kind)
                .ToArray());
        }
    }

    public Task<ProjectBomHeaderBinding> SaveProjectBomHeaderBindingAsync(Guid projectId, ProjectBomHeaderKind kind, Guid materialId, long expectedRowVersion, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.ContainsKey(projectId)) throw new PdmNotFoundException("项目不存在。");
            if (projectBomHeaders.Values.Any(item => item.ProjectId == projectId && item.Kind != kind && item.MaterialId == materialId))
                throw new PdmConflictException("同一项目的主BOM及三类子BOM必须分别使用不同料号。");

            var key = (projectId, kind);
            if (projectBomHeaders.TryGetValue(key, out var existing))
            {
                if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("BOM料号绑定已变化，请刷新后重试。");
            }
            else if (expectedRowVersion != 0)
            {
                throw new PdmConflictException("BOM料号绑定已变化，请刷新后重试。");
            }

            var saved = new ProjectBomHeaderBinding(
                projectId, kind, kind == ProjectBomHeaderKind.Master ? null : ProjectBomHeaderKind.Master,
                materialId, actor, timeProvider.GetUtcNow(), (existing?.RowVersion ?? 0) + 1);
            projectBomHeaders[key] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            if (projects.Values.Any(project => project.ParentProjectId == projectId))
                throw new PdmConflictException("该项目存在子项目，请先删除子项目。");
            if (documents.Values.Any(document => document.ProjectId == projectId))
                throw new PdmConflictException("该项目存在受控图档，不能删除。");
            if (bomItems.Any(item => item.ProjectId == projectId))
                throw new PdmConflictException("该项目存在BOM数据，不能删除。");
            if (packages.Values.Any(package => package.ProjectId == projectId))
                throw new PdmConflictException("该项目存在审批或发布包，不能删除。");

            ReleaseProjectNumbers(project);
            projects.TryRemove(projectId, out _);
            projectResponsibles.TryRemove(projectId, out _);
            return Task.CompletedTask;
        }
    }

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (projects.Values.Any(project => string.Equals(project.Code, command.Code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PdmConflictException("项目编码已经存在。");
            }

            var project = new Project(
                Guid.NewGuid(),
                command.Code,
                command.Name,
                command.Owner,
                command.VaultLocation,
                command.ReleaseLocation,
                true) { ResponsibleUsers = [actor] };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = [actor];
            EnsureProjectFolderTree(project.Id);
            return Task.FromResult(project);
        }
    }

    public Task<ProjectNumberingOptions> GetProjectNumberingOptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(NumberingOptions());

    public Task<ProjectNumberingOptions> AdvanceOrganizationCountersAsync(Guid organizationId, int currentProjectSequence, int currentSerialSequence, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!NumberingOptions().Organizations.Any(item => item.Id == organizationId)) throw new PdmNotFoundException("组织不存在。");
            if (currentProjectSequence < projectCounters.GetValueOrDefault(organizationId) || currentSerialSequence < serialCounters.GetValueOrDefault(organizationId))
                throw new PdmRuleException("流水基线只能向前调整，不能小于系统当前值。");
            projectCounters[organizationId] = currentProjectSequence;
            serialCounters[organizationId] = currentSerialSequence;
            return Task.FromResult(NumberingOptions());
        }
    }

    public Task<IReadOnlyList<PdmCustomer>> ListCustomersAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmCustomer>>(customers.Values
            .Where(customer => string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase))
            .Where(customer => includeInactive || customer.IsActive)
            .OrderBy(customer => customer.Code)
            .ToArray());

    public Task<PdmCustomer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        customers.TryGetValue(customerId, out var customer);
        return Task.FromResult(customer);
    }

    public Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (customers.Values.Any(customer => customer.Id != customerId && string.Equals(customer.Code, code, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("客户编码已经存在。");
            if (customerId is not null && !customers.ContainsKey(customerId.Value)) throw new PdmNotFoundException("客户不存在。");
            var customer = new PdmCustomer(customerId ?? Guid.NewGuid(), code, name, isActive, "legacy");
            customers[customer.Id] = customer;
            return Task.FromResult(customer);
        }
    }

    public Task<CrmIntegrationConfiguration> GetCrmIntegrationConfigurationAsync(CancellationToken cancellationToken) =>
        Task.FromResult(crmIntegrationConfiguration);

    public Task<CrmIntegrationConfiguration> SaveCrmIntegrationConfigurationAsync(CrmIntegrationConfiguration configuration, string actor, CancellationToken cancellationToken)
    {
        crmIntegrationConfiguration = configuration;
        return Task.FromResult(configuration);
    }

    public Task RecordCrmAutomaticSyncAttemptAsync(DateTimeOffset attemptedAt, string? error, CancellationToken cancellationToken)
    {
        crmIntegrationConfiguration = crmIntegrationConfiguration with
        {
            LastAutoSyncAttemptAt = attemptedAt,
            LastAutoSyncError = error
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PdmCustomer>> ApplyCrmCustomerSyncAsync(IReadOnlyList<CrmCustomerRecord> syncedCustomers, DateTimeOffset syncedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var existing in customers.Values.Where(customer => string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase)).ToArray())
                customers[existing.Id] = existing with { IsActive = false };
            foreach (var item in syncedCustomers)
            {
                var existing = customers.Values.FirstOrDefault(customer => string.Equals(customer.Code, item.Code, StringComparison.OrdinalIgnoreCase));
                var customer = new PdmCustomer(existing?.Id ?? Guid.NewGuid(), item.Code, item.Name, true, "u9c", syncedAt);
                customers[customer.Id] = customer;
            }
            crmIntegrationConfiguration = crmIntegrationConfiguration with { LastSyncAt = syncedAt, LastSyncCount = syncedCustomers.Count };
            return ListCustomersAsync(true, cancellationToken);
        }
    }

    public Task<IReadOnlyList<EquipmentTypeDefinition>> ListEquipmentTypesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EquipmentTypeDefinition>>(equipmentTypes.Values.Where(item => includeInactive || item.IsActive).OrderBy(item => item.Code).ToArray());

    public Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, CancellationToken cancellationToken)
    {
        var item = new EquipmentTypeDefinition(code, name, isActive);
        equipmentTypes[code] = item;
        return Task.FromResult(item);
    }

    public Task<PdmSystemSettings> GetSystemSettingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(BomPropertyMappingCatalog.Apply(systemSettings with
        {
            ApprovalWorkflows = ReleaseApprovalSettings.UseOrganizationHierarchy(systemSettings.ApprovalWorkflows)
        }));

    public Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        systemSettings = BomPropertyMappingCatalog.Apply(settings with
        {
            ApprovalWorkflows = ReleaseApprovalSettings.UseOrganizationHierarchy(settings.ApprovalWorkflows)
        });
        return Task.FromResult(systemSettings);
    }

    public Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserAccount>>(users.Values.OrderBy(user => user.Username).ToArray());

    public Task<RolePermissionDirectory> GetRolePermissionDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(BuildRolePermissionDirectory());

    public Task<IReadOnlySet<string>> GetRolePermissionsAsync(UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(rolePermissions.GetValueOrDefault(role.ToString(), RolePermissionCatalog.Defaults[role]));

    public Task<IReadOnlySet<string>> GetUserPermissionsAsync(string username, UserRole fallbackRole, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(user is null ? PermissionsFor(fallbackRole.ToString(), fallbackRole) : PermissionsFor(user.EffectiveRoleCodes, fallbackRole));
    }

    public Task<bool> HasRolePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken) =>
        Task.FromResult(role == UserRole.Administrator || PermissionsFor(role.ToString(), role).Contains(permissionCode));

    public async Task<bool> HasUserPermissionAsync(string username, UserRole fallbackRole, string permissionCode, CancellationToken cancellationToken) =>
        fallbackRole == UserRole.Administrator || (await GetUserPermissionsAsync(username, fallbackRole, cancellationToken)).Contains(permissionCode);

    public Task<RolePermissionDirectory> SetRolePermissionsAsync(string roleCode, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken)
    {
        var definition = roleDefinitions.GetValueOrDefault(roleCode) ?? throw new PdmNotFoundException("角色不存在。");
        if (!definition.IsSystemAdministrator) rolePermissions[roleCode] = RolePermissionCatalog.Normalize(definition.BaseRole, permissionCodes);
        return Task.FromResult(BuildRolePermissionDirectory());
    }

    public Task<RolePermissionDirectory> CreateRoleAsync(string name, string description, string sourceRoleCode, CancellationToken cancellationToken)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName)) throw new PdmRuleException("角色名称不能为空。");
        if (roleDefinitions.Values.Any(item => string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase))) throw new PdmConflictException("角色名称已经存在。");
        var source = roleDefinitions.GetValueOrDefault(sourceRoleCode) ?? throw new PdmNotFoundException("复制来源角色不存在。");
        if (source.IsSystemAdministrator) throw new PdmRuleException("系统管理员不能作为复制来源。");
        var roleCode = $"custom-{Guid.NewGuid():N}";
        roleDefinitions[roleCode] = new(roleCode, normalizedName, description.Trim(), source.BaseRole);
        rolePermissions[roleCode] = PermissionsFor(source.RoleCode, source.BaseRole).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(BuildRolePermissionDirectory());
    }

    public Task<RolePermissionDirectory> DeleteRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        var definition = roleDefinitions.GetValueOrDefault(roleCode) ?? throw new PdmNotFoundException("角色不存在。");
        if (definition.IsSystem) throw new PdmRuleException("系统角色不能删除。");
        var userCount = users.Values.Count(item => item.HasRole(roleCode));
        if (userCount > 0) throw new PdmConflictException($"该角色仍分配给 {userCount} 个用户，请先调整用户角色。");
        roleDefinitions.TryRemove(roleCode, out _);
        rolePermissions.TryRemove(roleCode, out _);
        return Task.FromResult(BuildRolePermissionDirectory());
    }

    public Task<OrganizationDirectory> GetOrganizationDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new OrganizationDirectory(
            organizations.Values.OrderBy(item => item.ProjectCompanyCode).ToArray(),
            organizationUnits.Values.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArray(),
            organizationMemberships.SelectMany(item => item.Value.UnitIds.Select(unitId => new OrganizationMembership(unitId, item.Key, unitId == item.Value.PrimaryUnitId))).ToArray(),
            organizationManagers.Values.ToArray(),
            users.Values.OrderBy(item => item.Username).Select(item => new OrganizationDirectoryUser(item.Username, item.DisplayName, item.Role, item.IsActive, item.EffectiveRoleCode, item.CompanyId, item.CrossCompanyView,
                userCompanyAccess.TryGetValue(item.Id, out var access) ? access.ToArray() : Array.Empty<Guid>(), item.EffectiveRoleCodes)).ToArray()));

    public Task<UserCompanyScope?> GetUserCompanyScopeAsync(string username, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null) return Task.FromResult<UserCompanyScope?>(null);
        var access = userCompanyAccess.TryGetValue(user.Id, out var companyIds) ? companyIds.ToArray() : Array.Empty<Guid>();
        return Task.FromResult<UserCompanyScope?>(new UserCompanyScope(user.Id, user.CompanyId, user.CrossCompanyView, access));
    }

    public Task<UserCompanyScope> SetUserCompanyScopeAsync(string username, Guid companyId, bool crossCompanyView, IReadOnlyList<Guid> accessibleCompanyIds, string actor, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("用户不存在。");
        if (!organizations.TryGetValue(companyId, out var company) || !company.IsActive) throw new PdmRuleException("所属公司不存在或已停用。");
        var normalized = accessibleCompanyIds.Where(id => id != companyId).Distinct().ToArray();
        if (normalized.Any(id => !organizations.TryGetValue(id, out var target) || !target.IsActive)) throw new PdmRuleException("跨公司授权目标不存在或已停用。");
        var updated = user with { CompanyId = companyId, CrossCompanyView = crossCompanyView && normalized.Length > 0, TokenVersion = user.TokenVersion + 1 };
        users[user.Id] = updated;
        userCompanyAccess[user.Id] = normalized.ToHashSet();
        if (organizationMemberships.TryGetValue(username, out var membership)
            && membership.UnitIds.Any(id => organizationUnits.TryGetValue(id, out var unit) && unit.OrganizationId != companyId))
            organizationMemberships.TryRemove(username, out _);
        return Task.FromResult(new UserCompanyScope(user.Id, companyId, updated.CrossCompanyView, normalized));
    }

    public Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (organizations.Values.Any(item => item.Id != command.Id && (string.Equals(item.Name, command.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ProjectCompanyCode, command.ProjectCompanyCode, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ModelCompanyCode, command.ModelCompanyCode, StringComparison.OrdinalIgnoreCase))))
                throw new PdmConflictException("公司名称或代码已经存在。");
            var current = command.Id is null ? null : organizations.GetValueOrDefault(command.Id.Value);
            if (command.Id is not null && current is null) throw new PdmNotFoundException("公司不存在。");
            var saved = new ProjectOrganization(command.Id ?? Guid.NewGuid(), command.Name, command.ProjectCompanyCode, command.ModelCompanyCode, command.Name, command.IsActive,
                current?.CurrentProjectSequence ?? 0, current?.CurrentSerialSequence ?? 0);
            organizations[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<OrganizationUnit> SaveOrganizationUnitAsync(SaveOrganizationUnitCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (organizationUnits.Values.Any(item => item.Id != command.Id && item.OrganizationId == command.OrganizationId && string.Equals(item.Code, command.Code, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("同一公司内的组织编码已经存在。");
            if (command.Id is not null && !organizationUnits.ContainsKey(command.Id.Value)) throw new PdmNotFoundException("组织单元不存在。");
            var saved = new OrganizationUnit(command.Id ?? Guid.NewGuid(), command.OrganizationId, command.ParentUnitId, command.Code, command.Name, command.Kind, command.IsActive, command.SortOrder, command.CanManufacture);
            organizationUnits[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public async Task<OrganizationDirectory> SetOrganizationMembershipsAsync(string username, IReadOnlyList<Guid> unitIds, Guid primaryUnitId, CancellationToken cancellationToken)
    {
        organizationMemberships[username] = (unitIds.ToArray(), primaryUnitId);
        return await GetOrganizationDirectoryAsync(cancellationToken);
    }

    public async Task<OrganizationDirectory> SetOrganizationUnitManagersAsync(Guid unitId, string primaryManager, IReadOnlyList<string> collaborativeManagers, CancellationToken cancellationToken)
    {
        var unit = organizationUnits.GetValueOrDefault(unitId) ?? throw new PdmNotFoundException("组织不存在。");
        foreach (var username in new[] { primaryManager }.Concat(collaborativeManagers)
                     .Where(username => !string.IsNullOrWhiteSpace(username))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (organizationMemberships.TryGetValue(username, out var current))
            {
                if (current.UnitIds.Any(memberUnitId => organizationUnits.GetValueOrDefault(memberUnitId)?.OrganizationId != unit.OrganizationId))
                    throw new PdmConflictException("部门负责人已经属于其他公司，不能跨公司任职。");
                if (!current.UnitIds.Contains(unitId)) organizationMemberships[username] = (current.UnitIds.Append(unitId).ToArray(), current.PrimaryUnitId);
            }
            else
            {
                organizationMemberships[username] = ([unitId], unitId);
            }
        }
        if (string.IsNullOrWhiteSpace(primaryManager)) organizationManagers.TryRemove(unitId, out _);
        else organizationManagers[unitId] = new OrganizationUnitManagers(unitId, primaryManager, collaborativeManagers.ToArray());
        return await GetOrganizationDirectoryAsync(cancellationToken);
    }

    public Task<Project> UpdateProjectDetailsAsync(Guid projectId, UpdateProjectDetailsCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            if (project.ParentProjectId is not null)
            {
                var childOrganization = organizations[project.OrganizationId!.Value];
                project = project with
                {
                    Name = command.Name,
                    ProjectAlias = command.ProjectAlias,
                    Quantity = command.Quantity,
                    SerialNumbers = ResizeSerialNumbers(project, childOrganization, command.Quantity)
                };
                projects[projectId] = project;
                return Task.FromResult(project);
            }

            var organizationId = command.OrganizationId ?? throw new PdmRuleException("所属公司不能为空。");
            var equipmentTypeCode = command.EquipmentTypeCode ?? throw new PdmRuleException("设备类型不能为空。");
            var projectTypeCode = command.ProjectTypeCode ?? throw new PdmRuleException("项目类型不能为空。");
            var organization = organizations.GetValueOrDefault(organizationId) ?? throw new PdmRuleException("所选组织不存在。");
            var customer = command.CustomerId is null
                ? new PdmCustomer(Guid.Empty, project.CustomerCode ?? throw new PdmRuleException("项目缺少客户编码。"), project.CustomerName ?? "", true)
                : customers.GetValueOrDefault(command.CustomerId.Value) ?? throw new PdmRuleException("所选客户不存在。");
            var oldOrganization = organizations[project.OrganizationId!.Value];
            var organizationChanged = organization.Id != oldOrganization.Id;
            var codeChanged = organizationChanged || !string.Equals(project.ProjectTypeCode, projectTypeCode, StringComparison.OrdinalIgnoreCase);
            var customerChanged = organizationChanged || !string.Equals(project.CustomerCode, customer.Code, StringComparison.OrdinalIgnoreCase);
            var oldProjectSequence = codeChanged ? ParseProjectSequence(project, oldOrganization) : 0;
            var projectSequence = organizationChanged
                ? ReserveNumber(projectCounters, releasedProjectNumbers, organization.Id, 99999)
                : oldProjectSequence;
            if (organizationChanged) ReleaseNumber(releasedProjectNumbers, oldOrganization.Id, oldProjectSequence);

            var oldCustomerKey = (oldOrganization.Id, project.CustomerCode!.ToUpperInvariant());
            var newCustomerKey = (organization.Id, customer.Code.ToUpperInvariant());
            var customerSequence = customerChanged
                ? ReserveNumber(customerCounters, releasedCustomerNumbers, newCustomerKey, 999)
                : project.CustomerProjectSequence!.Value;
            if (customerChanged) ReleaseNumber(releasedCustomerNumbers, oldCustomerKey, project.CustomerProjectSequence!.Value);

            var rootCode = codeChanged ? $"{projectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}" : project.Code;
            var tree = projects.Values.Where(item => item.Id == project.Id || item.RootProjectId == project.Id).ToArray();
            foreach (var item in tree)
            {
                IReadOnlyList<string> serials;
                if (organizationChanged)
                {
                    ReleaseSerialNumbers(item, oldOrganization);
                    serials = ReserveSerials(organization, item.Id == project.Id ? command.Quantity : item.Quantity);
                }
                else
                {
                    serials = ResizeSerialNumbers(item, organization, item.Id == project.Id ? command.Quantity : item.Quantity);
                }

                var code = item.Id == project.Id ? rootCode : $"{rootCode}{item.Code[project.Code.Length..]}";
                var itemEquipmentTypeCode = item.Id == project.Id ? equipmentTypeCode : item.EquipmentTypeCode ?? equipmentTypeCode;
                var updated = item with
                {
                    Code = code,
                    Name = item.Id == project.Id ? command.Name : item.Name,
                    ProjectAlias = item.Id == project.Id ? command.ProjectAlias : item.ProjectAlias,
                    OrganizationId = organization.Id,
                    OrganizationName = organization.Name,
                    ProjectTypeCode = projectTypeCode,
                    EquipmentTypeCode = itemEquipmentTypeCode,
                    CustomerCode = customer.Code,
                    CustomerName = customer.Name,
                    CustomerProjectSequence = customerSequence,
                    DeviceModel = $"{organization.ModelCompanyCode}-{itemEquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-{BuildModelSuffixFromCode(code)}",
                    SignedDate = command.SignedDate,
                    Quantity = item.Id == project.Id ? command.Quantity : item.Quantity,
                    SerialNumbers = serials,
                    VaultLocation = ReplaceTerminalDirectory(item.VaultLocation, code),
                    ReleaseLocation = ReplaceTerminalDirectory(item.ReleaseLocation, code)
                };
                projects[item.Id] = updated;
            }
            return Task.FromResult(projects[projectId]);
        }
    }

    public Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project) || project.ParentProjectId is not null) throw new PdmNotFoundException("主项目不存在。");
            var unit = organizationUnits.GetValueOrDefault(executionUnitId) ?? throw new PdmNotFoundException("执行事业部不存在。");
            project = project with
            {
                ExecutionUnitId = unit.Id, ExecutionUnitName = unit.Name, PrimaryProjectManager = null,
                CollaborativeProjectManagers = [], DesignLead = null, DesignLeads = [], Designers = []
            };
            projects[projectId] = project;
            foreach (var child in projects.Values.Where(item => item.RootProjectId == projectId && item.Id != projectId).ToArray())
                projects[child.Id] = child with { ExecutionUnitId = unit.Id, ExecutionUnitName = unit.Name, PrimaryProjectManager = null, CollaborativeProjectManagers = [], DesignLead = null, DesignLeads = [], Designers = [] };
            return Task.FromResult(project);
        }
    }

    public Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project) || project.ParentProjectId is not null) throw new PdmNotFoundException("主项目不存在。");
            var previousPrimaryProjectManager = project.PrimaryProjectManager;
            project = project with { PrimaryProjectManager = command.PrimaryProjectManager, CollaborativeProjectManagers = command.CollaborativeProjectManagers.ToArray(), DesignLead = command.DesignLeads.FirstOrDefault(), DesignLeads = command.DesignLeads.ToArray() };
            projects[projectId] = project;
            foreach (var child in projects.Values.Where(item => item.RootProjectId == projectId && item.Id != projectId).ToArray())
                projects[child.Id] = child with { PrimaryProjectManager = string.IsNullOrWhiteSpace(child.PrimaryProjectManager) || string.Equals(child.PrimaryProjectManager, previousPrimaryProjectManager, StringComparison.OrdinalIgnoreCase) ? command.PrimaryProjectManager : child.PrimaryProjectManager, CollaborativeProjectManagers = command.CollaborativeProjectManagers.ToArray(), DesignLead = command.DesignLeads.FirstOrDefault(), DesignLeads = command.DesignLeads.ToArray() };
            return Task.FromResult(project);
        }
    }

    public Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            project = project with { Designers = designers.ToArray() };
            projects[projectId] = project;
            return Task.FromResult(project);
        }
    }

    public Task<Project> SetChildProjectManagerAsync(Guid projectId, string projectManager, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project) || project.ParentProjectId is null) throw new PdmNotFoundException("子项目不存在。");
            project = project with { PrimaryProjectManager = projectManager };
            projects[projectId] = project;
            return Task.FromResult(project);
        }
    }

    public Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var organization = NumberingOptions().Organizations.Single(item => item.Id == command.OrganizationId);
            if (!customers.TryGetValue(command.CustomerId, out var customer) || !customer.IsActive) throw new PdmRuleException("所选客户不存在或已停用。");
            var projectSequence = ReserveNumber(projectCounters, releasedProjectNumbers, organization.Id, 99999);
            var customerKey = (organization.Id, customer.Code.ToUpperInvariant());
            var customerSequence = ReserveNumber(customerCounters, releasedCustomerNumbers, customerKey, 999);
            var serials = ReserveSerials(organization, command.Quantity);
            var code = $"{command.ProjectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}";
            var model = $"{organization.ModelCompanyCode}-{command.EquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-00";
            var projectId = Guid.NewGuid();
            var project = BuildNumberedProject(projectId, code, command.Name, command.ProjectAlias, organization, command.ProjectTypeCode,
                command.EquipmentTypeCode, customer.Code, customer.Name, customerSequence, model, command.SignedDate,
                command.Quantity, null, null, command.Owner, Path.Combine(command.VaultLocation, code), Path.Combine(command.ReleaseLocation, code), serials);
            project = project with
            {
                RootProjectId = projectId,
                BomItemCategoryCode = command.BomItemCategoryCode,
                ResponsibleUsers = [command.Owner]
            };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = project.ResponsibleUsers;
            EnsureProjectFolderTree(project.Id);
            return Task.FromResult(project);
        }
    }

    public Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(command.ParentProjectId, out var parent)) throw new PdmNotFoundException("上级项目不存在。");
            if (parent.OrganizationId is null || parent.EquipmentTypeCode is null || parent.CustomerProjectSequence is null
                || parent.SignedDate is null || string.IsNullOrWhiteSpace(parent.ProjectTypeCode)
                || string.IsNullOrWhiteSpace(parent.CustomerCode) || string.IsNullOrWhiteSpace(parent.CustomerName))
                throw new PdmRuleException("旧项目缺少自动编号资料，不能直接创建子项目。");
            var organization = NumberingOptions().Organizations.Single(item => item.Id == parent.OrganizationId);
            var usedChildSequences = projects.Values.Where(item => item.ParentProjectId == parent.Id).Select(item => item.ChildSequence ?? 0).ToHashSet();
            var childSequence = Enumerable.Range(1, 99).FirstOrDefault(value => !usedChildSequences.Contains(value));
            if (childSequence == 0) throw new PdmRuleException("该主项目的两位子项目号已用尽。");
            var serials = ReserveSerials(organization, command.Quantity);
            var code = $"{parent.Code}-{childSequence}";
            var equipmentTypeCode = command.EquipmentTypeCode ?? parent.EquipmentTypeCode.Value;
            var model = $"{organization.ModelCompanyCode}-{equipmentTypeCode}-{parent.CustomerCode}-{parent.CustomerProjectSequence.Value:D3}-{BuildChildModelSuffix(parent.Code, childSequence)}";
            var project = BuildNumberedProject(Guid.NewGuid(), code, command.Name, command.ProjectAlias, organization, parent.ProjectTypeCode,
                equipmentTypeCode, parent.CustomerCode, parent.CustomerName, parent.CustomerProjectSequence.Value, model,
                parent.SignedDate.Value, command.Quantity, parent.Id, childSequence, parent.Owner,
                Path.Combine(command.VaultRoot ?? systemSettings.VaultRoot, code), Path.Combine(command.ReleaseRoot ?? systemSettings.ReleaseRoot, code), serials);
            project = project with
            {
                RootProjectId = parent.RootProjectId ?? parent.Id,
                BomItemCategoryCode = "0302",
                ResponsibleUsers = parent.ResponsibleUsers,
                ExecutionUnitId = parent.ExecutionUnitId,
                ExecutionUnitName = parent.ExecutionUnitName,
                PrimaryProjectManager = parent.PrimaryProjectManager,
                CollaborativeProjectManagers = parent.CollaborativeProjectManagers,
                DesignLead = parent.DesignLead,
                DesignLeads = parent.DesignLeads
            };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = project.ResponsibleUsers;
            EnsureProjectFolderTree(project.Id);
            return Task.FromResult(project);
        }
    }

    public Task EnsureProjectFolderTreeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) EnsureProjectFolderTree(projectId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectFolder>> ListProjectFoldersAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            EnsureProjectFolderTree(projectId);
            var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
            var rootId = project.RootProjectId ?? project.Id;
            var folders = projectFolders.Values.Where(item => item.RootProjectId == rootId).OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArray();
            var result = folders.Select(folder => folder with
            {
                EffectiveAccess = ResolveFolderAccess(folder, folders, actor, role)
            }).ToArray();
            return Task.FromResult<IReadOnlyList<ProjectFolder>>(result);
        }
    }

    public Task<IReadOnlyList<ProjectFolderTemplateNode>> ListFolderTemplateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProjectFolderTemplateNode>>(folderTemplate.Values.OrderBy(item => item.ParentKey).ThenBy(item => item.SortOrder).ThenBy(item => item.FolderKey).ToArray());

    public Task<IReadOnlyList<ProjectFolderTemplateNode>> SaveFolderTemplateAsync(IReadOnlyList<SaveFolderTemplateNodeCommand> nodes, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (nodes.Count != folderTemplate.Count || nodes.Any(item => !folderTemplate.ContainsKey(item.FolderKey)))
                throw new PdmRuleException("目录模板节点必须完整，不能新增或删除系统目录。");
            foreach (var command in nodes)
            {
                var name = command.Name.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 160) throw new PdmRuleException("目录名称不能为空且不能超过160个字符。");
                var existing = folderTemplate[command.FolderKey];
                folderTemplate[command.FolderKey] = existing with
                {
                    Name = name,
                    SortOrder = command.SortOrder,
                    InheritPermissions = command.InheritPermissions,
                    Permissions = NormalizeFolderPermissions(command.Permissions)
                };
            }
            foreach (var main in projects.Values.Where(item => item.ParentProjectId is null)) EnsureProjectFolderTree(main.Id);
            return ListFolderTemplateAsync(cancellationToken);
        }
    }

    public Task<IReadOnlyList<ProjectFolder>> SetProjectFolderPermissionsAsync(Guid projectId, Guid folderId, IReadOnlyList<SaveFolderPermissionCommand> permissions, string actor, UserRole role, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            EnsureProjectFolderTree(projectId);
            var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
            var rootId = project.RootProjectId ?? project.Id;
            if (!projectFolders.TryGetValue(folderId, out var folder) || folder.RootProjectId != rootId) throw new PdmNotFoundException("项目目录不存在。");
            projectFolders[folderId] = folder with { Permissions = NormalizeFolderPermissions(permissions) };
            return ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
        }
    }

    public Task<ProjectFolder> CreateProjectFolderAsync(Guid projectId, Guid parentFolderId, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var parent = projectFolders.GetValueOrDefault(parentFolderId) ?? throw new PdmNotFoundException("目标业务目录不存在。");
            if (parent.Purpose != ProjectFolderPurpose.Standard) throw new PdmRuleException("只能在普通业务目录下新建文件夹。");
            if (projectFolders.Values.Any(item => item.ParentFolderId == parentFolderId && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new PdmConflictException("该目录下已存在同名文件夹。");
            var id = Guid.NewGuid();
            var folder = new ProjectFolder(id, parent.RootProjectId, parentFolderId, null, $"custom:{id:N}", "custom", name, ProjectFolderPurpose.Standard, projectFolders.Values.Where(item => item.ParentFolderId == parentFolderId).Select(item => item.SortOrder).DefaultIfEmpty().Max() + 10, false, true);
            projectFolders[id] = folder;
            return Task.FromResult(folder);
        }
    }

    public Task<ProjectFolder> RenameProjectFolderAsync(Guid projectId, Guid folderId, string name, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var folder = projectFolders.GetValueOrDefault(folderId) ?? throw new PdmNotFoundException("自定义文件夹不存在。");
            if (folder.IsSystem) throw new PdmRuleException("系统预置目录不能重命名。");
            if (projectFolders.Values.Any(item => item.Id != folderId && item.ParentFolderId == folder.ParentFolderId && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new PdmConflictException("该目录下已存在同名文件夹。");
            folder = folder with { Name = name };
            projectFolders[folderId] = folder;
            return Task.FromResult(folder);
        }
    }

    public Task<ProjectFolder> MoveProjectFolderAsync(Guid projectId, Guid folderId, Guid parentFolderId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var folder = projectFolders.GetValueOrDefault(folderId) ?? throw new PdmNotFoundException("自定义文件夹不存在。");
            var parent = projectFolders.GetValueOrDefault(parentFolderId) ?? throw new PdmNotFoundException("目标业务目录不存在。");
            if (folder.IsSystem || parent.Purpose != ProjectFolderPurpose.Standard) throw new PdmRuleException("文件夹不能移动到该目录。");
            for (var current = parent; current is not null; current = current.ParentFolderId is Guid id ? projectFolders.GetValueOrDefault(id) : null) if (current.Id == folderId) throw new PdmRuleException("文件夹不能移动到自身或其子目录。");
            folder = folder with { ParentFolderId = parentFolderId };
            projectFolders[folderId] = folder;
            return Task.FromResult(folder);
        }
    }

    public Task DeleteProjectFolderAsync(Guid projectId, Guid folderId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var folder = projectFolders.GetValueOrDefault(folderId) ?? throw new PdmNotFoundException("自定义文件夹不存在。");
            if (folder.IsSystem || projectFolders.Values.Any(item => item.ParentFolderId == folderId)) throw new PdmConflictException("只能删除空的自定义文件夹。");
            projectFolders.TryRemove(folderId, out _);
            return Task.CompletedTask;
        }
    }

    private ProjectNumberingOptions NumberingOptions() => new(
        organizations.Values.Where(item => item.IsActive).OrderBy(item => item.ProjectCompanyCode).Select(item => item with
        {
            CurrentProjectSequence = projectCounters.GetValueOrDefault(item.Id),
            CurrentSerialSequence = serialCounters.GetValueOrDefault(item.Id)
        }).ToArray(),
        [new("P", "标准项目", true), new("W", "外发项目", true), new("R", "研发项目", true), new("S", "售后项目", true)],
        equipmentTypes.Values.Where(item => item.IsActive).OrderBy(item => item.Code).ToArray());

    private static ProjectOrganization[] SeedOrganizations() =>
    [
        new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "昆山阿普顿自动化系统有限公司", "7", "AK", "昆山阿普顿自动化系统有限公司", true),
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"), "广州阿普顿自动化系统有限公司", "3", "AG", "广州阿普顿自动化系统有限公司", true),
        new(Guid.Parse("90000000-0000-0000-0000-000000000001"), "南京阿普顿自动化系统有限公司", "9", "AN", "南京阿普顿自动化系统有限公司", true)
    ];

    private static int ReserveNumber<TKey>(Dictionary<TKey, int> counters, Dictionary<TKey, SortedSet<int>> releasedNumbers, TKey key, int maximum) where TKey : notnull
    {
        if (releasedNumbers.TryGetValue(key, out var released) && released.Count > 0)
        {
            var reused = released.Min;
            released.Remove(reused);
            return reused;
        }
        var next = counters.GetValueOrDefault(key) + 1;
        if (next > maximum) throw new PdmRuleException("可用流水号已用尽。");
        counters[key] = next;
        return next;
    }

    private IReadOnlyList<string> ReserveSerials(ProjectOrganization organization, int quantity)
    {
        var values = new List<int>(quantity);
        var released = releasedSerialNumbers.GetValueOrDefault(organization.Id);
        while (released is { Count: > 0 } && values.Count < quantity)
        {
            values.Add(released.Min);
            released.Remove(released.Min);
        }
        while (values.Count < quantity)
        {
            var next = serialCounters.GetValueOrDefault(organization.Id) + 1;
            if (next > 9999999) throw new PdmRuleException("该组织的7位序列流水号已用尽。");
            serialCounters[organization.Id] = next;
            values.Add(next);
        }
        return values.Order().Select(value => $"{organization.ProjectCompanyCode}{value:D7}").ToArray();
    }

    private static Project BuildNumberedProject(
        Guid id, string code, string name, string? projectAlias, ProjectOrganization organization, string projectTypeCode,
        int equipmentTypeCode, string customerCode, string customerName, int customerProjectSequence, string deviceModel,
        DateOnly signedDate, int quantity, Guid? parentProjectId, int? childSequence, string owner,
        string vaultLocation, string releaseLocation, IReadOnlyList<string> serialNumbers) =>
        new(id, code, name, owner, vaultLocation, releaseLocation, true)
        {
            ProjectAlias = projectAlias,
            OrganizationId = organization.Id,
            OrganizationName = organization.Name,
            ProjectTypeCode = projectTypeCode,
            EquipmentTypeCode = equipmentTypeCode,
            CustomerCode = customerCode,
            CustomerName = customerName,
            CustomerProjectSequence = customerProjectSequence,
            DeviceModel = deviceModel,
            SignedDate = signedDate,
            Quantity = quantity,
            ParentProjectId = parentProjectId,
            ChildSequence = childSequence,
            SerialNumbers = serialNumbers
        };

    private static string BuildChildModelSuffix(string parentCode, int childSequence)
    {
        var parentSegments = parentCode.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(segment => int.TryParse(segment, out var value) ? value.ToString("D2") : segment);
        return string.Join('-', parentSegments.Append(childSequence.ToString("D2")));
    }

    private static string BuildModelSuffixFromCode(string code)
    {
        var segments = code.Split('-', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        return segments.Length == 0
            ? "00"
            : string.Join('-', segments.Select(segment => int.TryParse(segment, out var value) ? value.ToString("D2") : segment));
    }

    private IReadOnlyList<string> ResizeSerialNumbers(Project project, ProjectOrganization organization, int quantity)
    {
        if (quantity == project.SerialNumbers.Count) return project.SerialNumbers;
        if (quantity < project.SerialNumbers.Count)
        {
            foreach (var serial in project.SerialNumbers.Skip(quantity)) ReleaseSerialNumber(serial, organization);
            return project.SerialNumbers.Take(quantity).ToArray();
        }
        return project.SerialNumbers.Concat(ReserveSerials(organization, quantity - project.SerialNumbers.Count)).ToArray();
    }

    private void ReleaseProjectNumbers(Project project)
    {
        if (project.OrganizationId is null || !organizations.TryGetValue(project.OrganizationId.Value, out var organization)) return;
        if (project.ParentProjectId is null)
        {
            if (TryParseProjectSequence(project, organization, out var projectSequence))
                ReleaseNumber(releasedProjectNumbers, organization.Id, projectSequence);
            if (!string.IsNullOrWhiteSpace(project.CustomerCode) && project.CustomerProjectSequence is not null)
                ReleaseNumber(releasedCustomerNumbers, (organization.Id, project.CustomerCode.ToUpperInvariant()), project.CustomerProjectSequence.Value);
        }
        ReleaseSerialNumbers(project, organization);
    }

    private void ReleaseSerialNumbers(Project project, ProjectOrganization organization)
    {
        foreach (var serial in project.SerialNumbers) ReleaseSerialNumber(serial, organization);
    }

    private void ReleaseSerialNumber(string serial, ProjectOrganization organization)
    {
        if (serial.StartsWith(organization.ProjectCompanyCode, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(serial[organization.ProjectCompanyCode.Length..], out var value))
            ReleaseNumber(releasedSerialNumbers, organization.Id, value);
    }

    private static void ReleaseNumber<TKey>(Dictionary<TKey, SortedSet<int>> releasedNumbers, TKey key, int value) where TKey : notnull
    {
        if (value <= 0) return;
        if (!releasedNumbers.TryGetValue(key, out var released)) releasedNumbers[key] = released = [];
        released.Add(value);
    }

    private static int ParseProjectSequence(Project project, ProjectOrganization organization)
    {
        if (!TryParseProjectSequence(project, organization, out var sequence))
            throw new PdmRuleException("项目号不是系统自动编号，不能变更编号资料。");
        return sequence;
    }

    private static bool TryParseProjectSequence(Project project, ProjectOrganization organization, out int sequence)
    {
        sequence = 0;
        var prefix = $"{project.ProjectTypeCode}{organization.ProjectCompanyCode}";
        var number = project.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? project.Code[prefix.Length..] : string.Empty;
        return number.Length == 5 && int.TryParse(number, out sequence);
    }

    private static string ReplaceTerminalDirectory(string path, string code)
    {
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(parent) ? path : Path.Combine(parent, code);
    }

    public Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values
            .Where(document => document.ProjectId == projectId)
            .Select(WithStoredVersionCount)
            .OrderBy(document => document.DrawingNumber)
            .ThenBy(document => document.Kind)
            .ToArray());

    public Task<IReadOnlyList<PdmDocument>> ListProjectTreeDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.RootProjectId ?? project.Id;
        var projectIds = projects.Values.Where(item => item.Id == rootId || item.RootProjectId == rootId).Select(item => item.Id).ToHashSet();
        return Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values
            .Where(item => projectIds.Contains(item.ProjectId))
            .Select(WithStoredVersionCount)
            .OrderBy(item => item.ProjectId)
            .ThenBy(item => item.DrawingNumber)
            .ToArray());
    }

    private PdmDocument WithStoredVersionCount(PdmDocument document) =>
        document with { StoredVersionCount = versions.Values.Count(version => version.DocumentId == document.Id) };

    public Task<IReadOnlyList<DocumentModelDrawingRelation>> ListDocumentRelationsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.RootProjectId ?? project.Id;
        var projectIds = projects.Values.Where(item => item.Id == rootId || item.RootProjectId == rootId).Select(item => item.Id).ToHashSet();
        var documentIds = documents.Values.Where(item => projectIds.Contains(item.ProjectId)).Select(item => item.Id).ToHashSet();
        return Task.FromResult<IReadOnlyList<DocumentModelDrawingRelation>>(documentRelations.Values
            .Where(item => documentIds.Contains(item.ModelDocumentId) && documentIds.Contains(item.DrawingDocumentId))
            .OrderBy(item => item.ModelDocumentId)
            .ThenBy(item => item.DrawingDocumentId)
            .ToArray());
    }

    public Task<IReadOnlyList<DocumentWhereUsed>> ListWhereUsedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (!documents.ContainsKey(documentId)) throw new PdmNotFoundException("图档不存在。");
        var result = new List<DocumentWhereUsed>();
        CollectWhereUsed(referenceTree, documentId, result);
        return Task.FromResult<IReadOnlyList<DocumentWhereUsed>>(result
            .OrderBy(item => item.ProjectCode)
            .ThenBy(item => item.ParentDrawingNumber)
            .ThenBy(item => item.InstancePath)
            .ToArray());
    }

    private void CollectWhereUsed(DocumentReferenceNode parent, Guid documentId, ICollection<DocumentWhereUsed> result)
    {
        if (parent.DocumentId is Guid parentDocumentId
            && documents.TryGetValue(parentDocumentId, out var parentDocument)
            && projects.TryGetValue(parentDocument.ProjectId, out var project))
        {
            foreach (var child in parent.Children.Where(child => child.DocumentId == documentId))
            {
                result.Add(new DocumentWhereUsed(
                    documentId,
                    parentDocumentId,
                    project.Id,
                    project.Code,
                    project.Name,
                    parentDocument.DrawingNumber,
                    parentDocument.Name,
                    parentDocument.FileName,
                    parentDocument.Kind,
                    parentDocument.State,
                    parentDocument.Revision,
                    child.InstancePath,
                    child.Configuration,
                    child.Quantity));
            }
        }

        foreach (var child in parent.Children) CollectWhereUsed(child, documentId, result);
    }

    public Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        documents.TryGetValue(documentId, out var document);
        return Task.FromResult(document);
    }

    public Task<IReadOnlyList<DocumentContentFingerprint>> ListDocumentContentFingerprintsAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        var ids = (projectIds ?? Array.Empty<Guid>()).ToHashSet();
        var result = documents.Values
            .Where(document => ids.Contains(document.ProjectId))
            .Select(document => new { Document = document, Sha256 = CurrentSourceFingerprint(document.Id) })
            .Select(item => new DocumentContentFingerprint(item.Document, item.Sha256 ?? string.Empty))
            .ToArray();
        return Task.FromResult<IReadOnlyList<DocumentContentFingerprint>>(result);
    }

    public Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(command.ProjectId, out var project) || !project.IsActive)
            {
                throw new PdmNotFoundException("项目不存在或已停用。");
            }

            EnsureProjectFolderTree(command.ProjectId);
            var folder = ResolveDocumentFolder(command.ProjectId, command.FolderId);

            var existing = documents.Values.FirstOrDefault(document =>
                document.ProjectId == command.ProjectId
                && string.Equals(document.FileName, command.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(command.SourceSha256)
                    && !string.Equals(CurrentSourceFingerprint(existing.Id), command.SourceSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PdmConflictException($"项目中已存在同名但内容不同的图档{command.FileName}，不能覆盖或自动升版。");
                }
                if (existing.FolderId is null)
                {
                    existing = existing with { FolderId = folder.Id };
                    documents[existing.Id] = existing;
                }
                SaveDocumentRelation(command, existing);
                return Task.FromResult(existing);
            }

            var document = new PdmDocument(
                Guid.NewGuid(),
                command.ProjectId,
                command.DrawingNumber,
                command.Name,
                command.FileName,
                command.Kind,
                DocumentLifecycleState.Work,
                RevisionLabel.InitialWork(),
                null,
                DateTimeOffset.UtcNow) { FolderId = folder.Id };
            documents[document.Id] = document;
            if (!string.IsNullOrWhiteSpace(command.SourceSha256)) documentSourceFingerprints[document.Id] = command.SourceSha256;
            SaveDocumentRelation(command, document);
            return Task.FromResult(document);
        }
    }

    private void SaveDocumentRelation(RegisterDocumentCommand command, PdmDocument drawing)
    {
        if (command.RelatedModelDocumentId is not Guid modelDocumentId) return;
        if (!documents.TryGetValue(modelDocumentId, out var model)
            || model.ProjectId != drawing.ProjectId
            || model.Kind is not (DocumentKind.Assembly or DocumentKind.Part)
            || drawing.Kind != DocumentKind.Drawing)
            throw new PdmRuleException("工程图只能关联同一项目中的装配体或零件。");
        documentRelations[drawing.Id] = new(model.Id, drawing.Id);
    }

    public Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken) =>
        HasDocumentAccessAsync(documentId, actor, role, FolderAccess.View, cancellationToken);

    public Task<bool> HasDocumentAccessAsync(Guid documentId, string actor, UserRole role, FolderAccess requiredAccess, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)
                || !projects.TryGetValue(document.ProjectId, out var project)
                || !IsInActiveCompany(project)
                || !HasUserPermission(actor, role, PermissionCodes.ProjectContentView))
                return Task.FromResult(false);
            EnsureProjectFolderTree(document.ProjectId);
            var folder = document.FolderId is null
                ? projectFolders.Values.FirstOrDefault(item => item.TargetProjectId == document.ProjectId && item.TemplateKey == "mechanical.project")
                : projectFolders.GetValueOrDefault(document.FolderId.Value);
            if (folder is null) return Task.FromResult(false);
            var rootFolders = projectFolders.Values.Where(item => item.RootProjectId == folder.RootProjectId).ToArray();
            var access = ResolveFolderAccess(folder, rootFolders, actor, role);
            return Task.FromResult((access & requiredAccess) == requiredAccess);
        }
    }

    public Task<IReadOnlyList<DocumentVersion>> ListDocumentVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentVersion>>(versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).ToArray());

    public Task<IReadOnlyList<DocumentVersion>> ListProjectDocumentVersionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var projectDocumentIds = documents.Values
                .Where(document => document.ProjectId == projectId)
                .Select(document => document.Id)
                .ToHashSet();
            return Task.FromResult<IReadOnlyList<DocumentVersion>>(versions.Values
                .Where(version => projectDocumentIds.Contains(version.DocumentId))
                .OrderBy(version => version.DocumentId)
                .ThenByDescending(version => version.CreatedAt)
                .ToArray());
        }
    }

    public Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        versions.TryGetValue(versionId, out var version);
        return Task.FromResult(version?.DocumentId == documentId ? version : null);
    }

    public Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<DocumentReferenceNode?>(projectId == SeedData.ProjectId ? referenceTree : null);

    public Task<CadReferenceSnapshot?> GetLatestReferenceSnapshotAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<CadReferenceSnapshot?>(projectId == SeedData.ProjectId
            ? new CadReferenceSnapshot(SeedData.SnapshotId, projectId, referenceRootDocumentId, DateTimeOffset.UtcNow, "seed", referenceTree, string.Empty)
            : null);

    public Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BomItem>>(bomItems.Where(item => item.ProjectId == projectId && item.Kind == kind).OrderBy(item => item.Sequence).ToArray());

    public Task<IReadOnlyList<BomItem>> ReplaceBomAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            bomItems.RemoveAll(item => item.ProjectId == projectId && item.Kind == kind);
            bomItems.AddRange(items);
            return Task.FromResult<IReadOnlyList<BomItem>>(items.OrderBy(item => item.Sequence).ToArray());
        }
    }

    public Task ApplyBomBatchAsync(Guid projectId, IReadOnlyList<BomItem> standardItems, IReadOnlyList<BomItem> nonStandardItems, IReadOnlyList<BomItem> unclassifiedItems, IReadOnlyList<BomItem> electricalItems, IReadOnlyList<BomItem> virtualItems, IReadOnlyList<CadPropertyWriteback> writebacks, IReadOnlyList<AuditEntry> auditEntries, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            BomBatchApplyCount++;
            bomItems.RemoveAll(item => item.ProjectId == projectId && item.Kind is BomKind.Standard or BomKind.NonStandard or BomKind.Unclassified or BomKind.Electrical or BomKind.Virtual);
            bomItems.AddRange(standardItems);
            bomItems.AddRange(nonStandardItems);
            bomItems.AddRange(unclassifiedItems);
            bomItems.AddRange(electricalItems);
            bomItems.AddRange(virtualItems);
            foreach (var request in writebacks)
            {
                foreach (var existing in cadPropertyWritebacks.Values
                             .Where(item => item.BomItemId == request.BomItemId && item.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress)
                             .ToArray())
                    cadPropertyWritebacks[existing.Id] = existing with { Status = CadPropertyWritebackStatus.Superseded, CompletedAt = timeProvider.GetUtcNow() };
                cadPropertyWritebacks[request.Id] = request;
            }
            foreach (var entry in auditEntries) audits.Enqueue(entry);
        }
        return Task.CompletedTask;
    }

    public Task<BomItem?> FindBomItemAsync(Guid projectId, Guid itemId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<BomItem?>(bomItems.FirstOrDefault(item => item.ProjectId == projectId && item.Id == itemId));
        }
    }

    public Task<BomItem> UpdateBomMaterialCodeAsync(Guid projectId, Guid itemId, string materialCode, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = bomItems.FindIndex(item => item.ProjectId == projectId && item.Id == itemId);
            if (index < 0) throw new PdmNotFoundException("BOM物料不存在。");
            var current = bomItems[index];
            var saved = current with
            {
                DrawingNumber = materialCode.Trim(),
                PropertyWritebackStatus = current.SourceDocumentId.HasValue && current.Kind != BomKind.Electrical
                    ? CadPropertyWritebackStatus.Pending
                    : current.PropertyWritebackStatus
            };
            bomItems[index] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<BomItem> UpdateBomReconciliationAsync(
        Guid projectId,
        Guid itemId,
        string? status,
        string? note,
        string? updatedBy,
        DateTimeOffset? updatedAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = bomItems.FindIndex(item => item.ProjectId == projectId && item.Id == itemId);
            if (index < 0) throw new PdmNotFoundException("BOM物料不存在。");
            var saved = bomItems[index] with
            {
                ReconciliationStatus = status,
                ReconciliationNote = note,
                ReconciliationUpdatedBy = updatedBy,
                ReconciliationUpdatedAt = updatedAt
            };
            bomItems[index] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<CadPropertyWriteback> EnqueueCadPropertyWritebackAsync(CadPropertyWriteback request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var existing in cadPropertyWritebacks.Values
                         .Where(item => item.BomItemId == request.BomItemId && item.SourceDocumentId == request.SourceDocumentId
                             && item.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress)
                         .ToArray())
            {
                cadPropertyWritebacks[existing.Id] = existing with { Status = CadPropertyWritebackStatus.Superseded, CompletedAt = timeProvider.GetUtcNow() };
            }
            cadPropertyWritebacks[request.Id] = request;
            var index = bomItems.FindIndex(item => item.Id == request.BomItemId);
            if (index >= 0) bomItems[index] = bomItems[index] with { PropertyWritebackStatus = CadPropertyWritebackStatus.Pending };
            return Task.FromResult(request);
        }
    }

    public Task<IReadOnlyList<CadPropertyWriteback>> ListCadPropertyWritebacksAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<CadPropertyWriteback>>(cadPropertyWritebacks.Values
                .Where(item => item.ProjectId == projectId)
                .OrderByDescending(item => item.RequestedAt)
                .ToArray());
        }
    }

    public Task<CadPropertyWriteback?> FindCadPropertyWritebackAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cadPropertyWritebacks.TryGetValue(id, out var request);
            return Task.FromResult<CadPropertyWriteback?>(request);
        }
    }

    public Task<CadPropertyWriteback> UpdateCadPropertyWritebackAsync(Guid id, CadPropertyWritebackStatus status, Guid? resultVersionId, string? error, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!cadPropertyWritebacks.TryGetValue(id, out var request)) throw new PdmNotFoundException("属性写回任务不存在。");
            var now = timeProvider.GetUtcNow();
            var updated = request with
            {
                Status = status,
                StartedAt = status == CadPropertyWritebackStatus.InProgress ? now : request.StartedAt,
                CompletedAt = status is CadPropertyWritebackStatus.Succeeded or CadPropertyWritebackStatus.Conflict or CadPropertyWritebackStatus.Failed or CadPropertyWritebackStatus.Superseded ? now : null,
                ResultVersionId = resultVersionId,
                LastError = error
            };
            cadPropertyWritebacks[id] = updated;
            var index = bomItems.FindIndex(item => item.Id == request.BomItemId);
            if (index >= 0)
            {
                var related = cadPropertyWritebacks.Values.Where(item => item.BomItemId == request.BomItemId).ToArray();
                var aggregate = related.Any(item => item.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress)
                    ? CadPropertyWritebackStatus.Pending
                    : related.Any(item => item.Status is CadPropertyWritebackStatus.Conflict or CadPropertyWritebackStatus.Failed)
                        ? CadPropertyWritebackStatus.Failed
                        : status;
                bomItems[index] = bomItems[index] with { PropertyWritebackStatus = aggregate };
            }
            return Task.FromResult(updated);
        }
    }

    public Task<IReadOnlyList<BomEmptyDeclaration>> GetBomEmptyDeclarationsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<BomEmptyDeclaration>>(bomEmptyDeclarations.Where(item => item.Key.ProjectId == projectId).Select(item => item.Value).ToArray());
        }
    }

    public Task<BomEmptyDeclaration> SetBomEmptyDeclarationAsync(Guid projectId, BomKind kind, bool declaredEmpty, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var declaration = new BomEmptyDeclaration(kind, declaredEmpty, actor, timeProvider.GetUtcNow());
            bomEmptyDeclarations[(projectId, kind)] = declaration;
            return Task.FromResult(declaration);
        }
    }

    public Task<IReadOnlyList<BomVersion>> ListBomVersionsAsync(Guid projectId, BomKind? kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BomVersion>>(bomVersions.Values
            .Where(version => version.ProjectId == projectId && (!kind.HasValue || version.Kind == kind.Value))
            .OrderBy(version => version.Kind).ThenByDescending(version => version.VersionNumber).ToArray());

    public Task<BomVersion?> FindBomVersionAsync(Guid projectId, Guid versionId, CancellationToken cancellationToken)
    {
        bomVersions.TryGetValue(versionId, out var version);
        return Task.FromResult(version?.ProjectId == projectId ? version : null);
    }

    public Task<BomVersion> SaveBomDraftAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, string actor, CancellationToken cancellationToken)
    {
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("只有标准件、非标件和电气BOM支持独立版本控制。");
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var headerKind = kind == BomKind.Standard ? ProjectBomHeaderKind.Standard
                : kind == BomKind.NonStandard ? ProjectBomHeaderKind.NonStandard
                : ProjectBomHeaderKind.Electrical;
            projectBomHeaders.TryGetValue((projectId, headerKind), out var header);
            var draft = bomVersions.Values.FirstOrDefault(version => version.ProjectId == projectId && version.Kind == kind && version.State == BomVersionState.Draft);
            if (draft is not null)
            {
                draft = draft with { Items = items.OrderBy(item => item.Sequence).ToArray(), UpdatedBy = actor, UpdatedAt = now, MotherMaterialId = header?.MaterialId };
                bomVersions[draft.Id] = draft;
                return Task.FromResult(draft);
            }

            var latest = bomVersions.Values.Where(version => version.ProjectId == projectId && version.Kind == kind).OrderByDescending(version => version.VersionNumber).FirstOrDefault();
            var number = (latest?.VersionNumber ?? 0) + 1;
            var prefix = kind == BomKind.Standard ? "S" : kind == BomKind.NonStandard ? "N" : "E";
            if (!projects.TryGetValue(projectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            draft = new BomVersion(Guid.NewGuid(), projectId, kind, number, $"{prefix}-{ProjectNumberPolicy.BusinessCode(project)}-B{number:D2}", BomVersionState.Draft,
                latest?.Id, null, null, null, null, items.OrderBy(item => item.Sequence).ToArray(), actor, now, actor, now, null)
            {
                MotherMaterialId = header?.MaterialId
            };
            bomVersions[draft.Id] = draft;
            return Task.FromResult(draft);
        }
    }

    public Task<BomVersion> UpdateBomVersionReleaseInfoAsync(Guid versionId, string changeNumber, string changeReason, string effectiveSerialFrom, string? effectiveSerialTo, IReadOnlyList<string> validationRequiredFields, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!bomVersions.TryGetValue(versionId, out var version)) throw new PdmNotFoundException("BOM版本不存在。");
            if (version.State != BomVersionState.Draft) throw new PdmConflictException("已发布或已进入审批的BOM版本不可修改。");
            var updated = version with
            {
                ChangeNumber = changeNumber,
                ChangeReason = changeReason,
                EffectiveSerialFrom = effectiveSerialFrom,
                EffectiveSerialTo = effectiveSerialTo,
                ValidationRequiredFields = validationRequiredFields.ToArray()
            };
            bomVersions[versionId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task SetBomVersionStateAsync(IReadOnlyList<Guid> versionIds, BomVersionState state, string actor, DateTimeOffset? releasedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var id in versionIds)
                if (bomVersions.TryGetValue(id, out var version))
                    bomVersions[id] = version with { State = state, UpdatedBy = actor, UpdatedAt = now, ReleasedAt = state == BomVersionState.Released ? releasedAt : version.ReleasedAt };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManufacturingBomBaseline>> ListManufacturingBomBaselinesAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ManufacturingBomBaseline>>(manufacturingBomBaselines.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.Sequence).ToArray());

    public Task<ManufacturingBomBaseline> CreateManufacturingBomBaselineAsync(ManufacturingBomBaseline baseline, CancellationToken cancellationToken)
    {
        if (!manufacturingBomBaselines.TryAdd(baseline.Id, baseline)) throw new PdmConflictException("制造BOM基线已经存在。");
        return Task.FromResult(baseline);
    }

    public Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleasePackage>>(packages.Values.Where(package => package.ProjectId == projectId).OrderByDescending(package => package.CreatedAt).ToArray());

    public Task<IReadOnlyList<PendingApprovalTask>> ListPendingApprovalTasksAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingApprovalTask>>(packages.Values
            .Where(package => package.ProjectId == projectId && package.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval)
            .Select(package => (Package: package, Task: package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault(task => task.Decision is null)))
            .Where(item => item.Task is not null)
            .Select(item => new PendingApprovalTask(item.Task!.Id, item.Package.Id, item.Package.Number, item.Task.Stage, item.Package.State, item.Task.Assignee, item.Package.CreatedAt))
            .OrderBy(item => item.CreatedAt)
            .ToArray());

    public Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        packages.TryGetValue(releasePackageId, out var package);
        return Task.FromResult(package);
    }

    public Task<IReadOnlyList<PdmDocument>> ListCheckedOutDocumentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values.Where(document => !string.IsNullOrWhiteSpace(document.CheckedOutBy)).OrderBy(document => document.CheckedOutAt).ToArray());

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return CheckoutAsync(documentId, actor, Guid.NewGuid(), "legacy-client", now.AddMinutes(15), null, cancellationToken);
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
        => CheckoutAsync(documentId, actor, sessionId, machineName, leaseExpiresAt, null, cancellationToken);

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, Guid? drawingReviewWritebackId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw new PdmNotFoundException("图档不存在。 ");
            }
            if (IsDocumentUnderActiveDrawingReview(documentId)
                && (!drawingReviewWritebackId.HasValue || !IsActiveDrawingReviewWriteback(documentId, drawingReviewWritebackId.Value)))
            {
                throw new PdmConflictException("图档正在进行图纸审核，不能获取编辑权限。");
            }

            var now = timeProvider.GetUtcNow();
            var sameUserAndMachine = document.CheckedOutBy is not null
                && string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(document.CheckoutMachine)
                    || string.Equals(document.CheckoutMachine, machineName, StringComparison.OrdinalIgnoreCase));
            if (document.CheckedOutBy is not null && !sameUserAndMachine)
            {
                throw new PdmConflictException($"图档正在由{document.CheckedOutBy}编辑。 ");
            }

            var updated = document with
            {
                CheckedOutBy = actor,
                CheckedOutAt = document.CheckedOutAt ?? now,
                CheckoutSessionId = sessionId,
                CheckoutMachine = machineName,
                CheckoutLastHeartbeatAt = now,
                CheckoutLeaseExpiresAt = leaseExpiresAt,
                CheckoutReleaseRequestedBy = null,
                CheckoutReleaseRequestedAt = null,
                CheckoutReleaseRequestReason = null,
                UpdatedAt = now
            };
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<IReadOnlyList<Guid>> HeartbeatCheckoutSessionAsync(Guid sessionId, string actor, string machineName, IReadOnlyList<Guid> documentIds, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var active = new List<Guid>();
            foreach (var documentId in documentIds.Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document)
                    || !string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)
                    || document.CheckoutSessionId != sessionId) continue;
                documents[documentId] = document with
                {
                    CheckoutMachine = machineName,
                    CheckoutLastHeartbeatAt = now,
                    CheckoutLeaseExpiresAt = leaseExpiresAt
                };
                active.Add(documentId);
            }
            return Task.FromResult<IReadOnlyList<Guid>>(active);
        }
    }

    public Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, string sha256, CancellationToken cancellationToken)
    {
        return CompleteEditWithoutChangesAsync(documentId, actor, CurrentSession(documentId), sha256, cancellationToken);
    }

    public Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, Guid sessionId, string sha256, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            EnsureSessionOwner(document, actor, sessionId, "结束编辑");
            var latest = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault()
                ?? throw new PdmConflictException("图档尚无存档版本，必须先提交W1。");
            if (!string.Equals(latest.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("文件已经发生变更，请使用提交存档。");
            var updated = ClearEditLock(document, timeProvider.GetUtcNow());
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        return DiscardCheckoutAsync(documentId, actor, CurrentSession(documentId), cancellationToken);
    }

    public Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, Guid sessionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            EnsureSessionOwner(document, actor, sessionId, "放弃编辑");
            var updated = ClearEditLock(document, timeProvider.GetUtcNow());
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> RequestCheckoutReleaseAsync(Guid documentId, string requestedBy, string reason, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (string.IsNullOrWhiteSpace(document.CheckedOutBy)) throw new PdmConflictException("图档当前没有可申请释放的编辑权限。");
            var now = timeProvider.GetUtcNow();
            var updated = document with { CheckoutReleaseRequestedBy = requestedBy, CheckoutReleaseRequestedAt = now, CheckoutReleaseRequestReason = reason.Trim(), UpdatedAt = now };
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> ForceReleaseCheckoutAsync(Guid documentId, string releasedBy, string reason, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (string.IsNullOrWhiteSpace(document.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可释放。");
            var updated = ClearEditLock(document, timeProvider.GetUtcNow());
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> CheckInAsync(Guid documentId, string actor, RevisionLabel nextRevision, CadReferenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw new PdmNotFoundException("图档不存在。 ");
            }

            if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmConflictException("只有当前编辑人员可以提交存档。 ");
            }

            var updated = ClearEditLock(document with { Revision = nextRevision }, timeProvider.GetUtcNow());
            documents[documentId] = updated;
            referenceTree = snapshot.Root;
            return Task.FromResult(updated);
        }
    }

    public Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, DocumentVersionCommit commit, CancellationToken cancellationToken)
    {
        return CheckInVersionAsync(documentId, actor, CurrentSession(documentId), commit, null, cancellationToken);
    }

    public Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, CancellationToken cancellationToken)
        => CheckInVersionAsync(documentId, actor, sessionId, commit, null, cancellationToken);

    public Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, Guid? drawingReviewWritebackId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            EnsureSessionOwner(document, actor, sessionId, "提交存档");
            if (IsDocumentUnderActiveDrawingReview(documentId)
                && (!drawingReviewWritebackId.HasValue || !IsActiveDrawingReviewWriteback(documentId, drawingReviewWritebackId.Value)))
            {
                throw new PdmConflictException("图档正在进行图纸审核，不能提交存档。");
            }
            var renamedDocument = document with
            {
                DrawingNumber = commit.DrawingNumber ?? document.DrawingNumber,
                Name = commit.Name ?? document.Name,
                FileName = commit.FileName ?? document.FileName
            };
            var latest = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault();
            string? latestSourceSha256 = null;
            latest?.PropertySnapshot.TryGetValue("SourceFileSha256", out latestSourceSha256);
            commit.Properties.TryGetValue("SourceFileSha256", out var sourceFileSha256);
            var sameFile = latest is not null
                && (string.Equals(latest.Sha256, commit.File.Sha256, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(latestSourceSha256)
                        && !string.IsNullOrWhiteSpace(sourceFileSha256)
                        && string.Equals(latestSourceSha256, sourceFileSha256, StringComparison.OrdinalIgnoreCase)));
            if (!commit.ForceVersion
                && sameFile)
            {
                if (commit.IsProjectRoot)
                {
                    referenceTree = commit.ReferenceSnapshot.Root;
                    referenceRootDocumentId = documentId;
                }

                var unchanged = ClearEditLock(renamedDocument, timeProvider.GetUtcNow());
                documents[documentId] = unchanged;
                if (!string.IsNullOrWhiteSpace(sourceFileSha256)) documentSourceFingerprints[documentId] = sourceFileSha256;
                return Task.FromResult(new DocumentCheckInResult(unchanged, null, false));
            }
            var revision = versions.Values.Any(version => version.DocumentId == documentId) || document.Revision.IsReleased ? document.Revision.NextWork() : RevisionLabel.InitialWork();
            var version = new DocumentVersion(Guid.NewGuid(), documentId, revision, DocumentVersionStatus.Work, commit.File.RelativePath, commit.File.Length, commit.File.Sha256, actor, DateTimeOffset.UtcNow, commit.ChangeNote, commit.Properties, commit.ReferenceSnapshot.Root, commit.MechanicalBomSnapshot, commit.ElectricalBomSnapshot, commit.SourceVersionId, commit.SourceDescription, null, null);
            versions[version.Id] = version;
            var updated = ClearEditLock(renamedDocument with { Revision = revision, State = DocumentLifecycleState.Work }, version.CreatedAt);
            documents[documentId] = updated;
            if (!string.IsNullOrWhiteSpace(sourceFileSha256)) documentSourceFingerprints[documentId] = sourceFileSha256;
            if (commit.IsProjectRoot)
            {
                referenceTree = commit.ReferenceSnapshot.Root;
                referenceRootDocumentId = documentId;
            }

            return Task.FromResult(new DocumentCheckInResult(updated, version, true));
        }
    }

    private string? CurrentSourceFingerprint(Guid documentId)
    {
        if (documentSourceFingerprints.TryGetValue(documentId, out var fingerprint)) return fingerprint;
        var latest = versions.Values
            .Where(version => version.DocumentId == documentId)
            .OrderByDescending(version => version.CreatedAt)
            .FirstOrDefault();
        if (latest is null) return null;
        return latest.PropertySnapshot.TryGetValue("SourceFileSha256", out var sourceSha256)
            && !string.IsNullOrWhiteSpace(sourceSha256)
                ? sourceSha256
                : latest.Sha256;
    }

    public Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(Guid documentId, Guid sourceVersionId, string actor, StoredFile restoredFile, string changeNote, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!versions.TryGetValue(sourceVersionId, out var source) || source.DocumentId != documentId) throw new PdmNotFoundException("历史版本不存在。");
            var revision = document.Revision.NextWork();
            var restored = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Work, StorageRelativePath = restoredFile.RelativePath, FileLength = restoredFile.Length, Sha256 = restoredFile.Sha256, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = changeNote, SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}恢复生成{revision.Display}", ApprovalTaskId = null, ReleasePackageId = null, Preview = null };
            versions[restored.Id] = restored;
            var updated = ClearEditLock(document with { Revision = revision, State = DocumentLifecycleState.Work }, restored.CreatedAt);
            documents[documentId] = updated;
            if (source.PropertySnapshot.TryGetValue("SourceFileSha256", out var restoredSourceSha256)
                && !string.IsNullOrWhiteSpace(restoredSourceSha256))
                documentSourceFingerprints[documentId] = restoredSourceSha256;
            return Task.FromResult((updated, restored));
        }
    }

    public Task<DocumentVersion> PublishDocumentVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!versions.TryGetValue(sourceVersionId, out var source) || source.DocumentId != documentId) throw new PdmNotFoundException("待发布工作版本不存在。");
            if (source.Status != DocumentVersionStatus.Work) throw new PdmConflictException("只能从工作版本生成正式版本。");
            if (!string.Equals(source.Revision.Display, document.Revision.Display, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("只能发布图档当前最新的工作版本。");
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State is not (ReleasePackageState.Publishing or ReleasePackageState.Published)) throw new PdmConflictException("发布包尚未审批通过，不能生成正式版本。");
            if (!package.ApprovalTasks.Any(task => task.Id == approvalTaskId && task.Stage == ApprovalStage.Approval && task.Decision == ApprovalDecision.Approved)) throw new PdmConflictException("最终批准记录与发布包不匹配或尚未批准。");
            var revision = source.Revision.Release();
            var released = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = $"审批发布{revision.Display}", SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布", ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId };
            versions[released.Id] = released;
            documents[documentId] = ClearEditLock(document with { Revision = revision, State = DocumentLifecycleState.Released }, released.CreatedAt);
            return Task.FromResult(released);
        }
    }

    public Task<IReadOnlyList<ReleasePreviewSource>> ListReleasePreviewSourcesAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State != ReleasePackageState.Publishing)
                throw new PdmConflictException("发布包尚未进入服务器转换状态。");
            var sources = new List<ReleasePreviewSource>();
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document)
                    || document.Kind is not (DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing)) continue;
                var source = versions.Values
                    .Where(version => version.DocumentId == documentId)
                    .OrderByDescending(version => version.CreatedAt)
                    .FirstOrDefault();
                if (source is null) continue;
                source.PropertySnapshot.TryGetValue("SourceFileSha256", out var sourceSha256);
                sources.Add(new ReleasePreviewSource(
                    document.Id,
                    source.Id,
                    document.DrawingNumber,
                    document.FileName,
                    document.Kind,
                    source.StorageRelativePath,
                    source.FileLength,
                    source.Sha256,
                    string.IsNullOrWhiteSpace(sourceSha256) ? source.Sha256 : sourceSha256));
            }
            return Task.FromResult<IReadOnlyList<ReleasePreviewSource>>(sources);
        }
    }

    public Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(
        Guid releasePackageId,
        Guid approvalTaskId,
        string actor,
        IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State != ReleasePackageState.Publishing)
                throw new PdmConflictException("发布包尚未进入发布状态。");
            if (!package.ApprovalTasks.Any(task => task.Id == approvalTaskId && task.Decision == ApprovalDecision.Approved))
                throw new PdmConflictException("最终批准记录无效。");
            var released = new List<DocumentVersion>();
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document)) continue;
                var source = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault();
                if (source is null || source.Status != DocumentVersionStatus.Work || !string.Equals(source.Revision.Display, document.Revision.Display, StringComparison.OrdinalIgnoreCase)) continue;
                DocumentPreviewArtifact? preview = null;
                if (document.Kind is DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing
                    && !previews.TryGetValue(documentId, out preview))
                    throw new PdmConflictException($"图档{document.DrawingNumber}缺少服务器生成的发布预览文件。");
                var revision = source.Revision.Release();
                var version = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = $"审批发布{revision.Display}", SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布", ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId, Preview = preview };
                versions[version.Id] = version;
                documents[documentId] = ClearEditLock(document with { Revision = revision, State = DocumentLifecycleState.Released }, version.CreatedAt);
                released.Add(version);
            }
            var now = timeProvider.GetUtcNow();
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (documents.TryGetValue(documentId, out var document) && document.State == DocumentLifecycleState.InReview)
                    documents[documentId] = document with { State = DocumentLifecycleState.Released, UpdatedAt = now };
            }
            return Task.FromResult<IReadOnlyList<DocumentVersion>>(released);
        }
    }

    private static IEnumerable<Guid> EnumerateDocumentIds(DocumentReferenceNode node)
    {
        if (node.DocumentId.HasValue) yield return node.DocumentId.Value;
        foreach (var child in node.Children)
            foreach (var id in EnumerateDocumentIds(child)) yield return id;
    }

    public Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        if (!packages.TryAdd(package.Id, package))
        {
            throw new PdmConflictException("发布包编号已经存在。 ");
        }

        return Task.FromResult(package);
    }

    public Task<ReleasePackage> UpdateDraftReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(package.Id, out var current)) throw new PdmNotFoundException("发布包不存在。");
            if (current.State != ReleasePackageState.Draft) throw new PdmConflictException("只有草稿发布包可以编辑，请刷新后重试。");
            packages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task DeleteDraftReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State != ReleasePackageState.Draft) throw new PdmConflictException("只有草稿发布包可以删除，请刷新后重试。");
            packages.TryRemove(releasePackageId, out _);
            return Task.CompletedTask;
        }
    }

    public Task<ReleasePackage> UpdateReleasePackageBomVersionsAsync(Guid releasePackageId, BomVersion standard, BomVersion nonStandard, BomVersion electrical, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected or ReleasePackageState.PublishFailed))
                throw new PdmConflictException("发布包状态已变化，不能更新BOM版本。");
            var updated = package with
            {
                StandardBomVersionId = standard.Id,
                NonStandardBomVersionId = nonStandard.Id,
                ElectricalBomVersionId = electrical.Id,
                StandardBomRevision = standard.Label,
                NonStandardBomRevision = nonStandard.Label,
                ElectricalBomRevision = electrical.Label,
                StandardBomSnapshot = standard.Items.ToArray(),
                NonStandardBomSnapshot = nonStandard.Items.ToArray(),
                MechanicalBomSnapshot = standard.Items.Concat(nonStandard.Items).ToArray(),
                ElectricalBomSnapshot = electrical.Items.ToArray()
            };
            packages[releasePackageId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ReleasePackage> ApplyReleasePackageMaterialCodesAsync(Guid releasePackageId, IReadOnlyDictionary<Guid, string> materialCodes, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval)) throw new PdmConflictException("只有审批中的发布包可以在终审时回填非标件料号。");
            BomItem Apply(BomItem item) => materialCodes.TryGetValue(item.Id, out var code) ? item with { DrawingNumber = code } : item;
            var updated = package with { NonStandardBomSnapshot = package.NonStandardBomSnapshot.Select(Apply).ToArray(), MechanicalBomSnapshot = package.MechanicalBomSnapshot.Select(Apply).ToArray() };
            packages[releasePackageId] = updated;
            if (package.NonStandardBomVersionId is Guid versionId && bomVersions.TryGetValue(versionId, out var version))
                bomVersions[versionId] = version with { Items = updated.NonStandardBomSnapshot, UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() };
            return Task.FromResult(updated);
        }
    }

    public Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected or ReleasePackageState.PublishFailed))
                throw new PdmConflictException("只有草稿、已驳回或发布失败的发布包可以提交。");
            var now = timeProvider.GetUtcNow();
            if (package.LocksDocuments)
            {
                var documentIds = EnumerateDocumentIds(referenceTree).Distinct().ToArray();
                var editing = documentIds.Select(id => documents.GetValueOrDefault(id)).FirstOrDefault(document => document?.CheckedOutBy is not null);
                if (editing is not null) throw new PdmConflictException($"图档{editing.DrawingNumber}正在由{editing.CheckedOutBy}编辑，不能提交审批。");
                foreach (var documentId in documentIds)
                {
                    if (documents.TryGetValue(documentId, out var document) && document.State != DocumentLifecycleState.Obsolete)
                        documents[documentId] = document with { State = DocumentLifecycleState.InReview, UpdatedAt = now };
                }
            }
            var resetTasks = package.ApprovalTasks.OrderBy(task => task.StepOrder).Select(task => task with
            {
                DecisionBy = null, Decision = null, Comment = null, DecidedAt = null,
                IsEmergencySubstitute = false, EmergencyReason = null
            }).ToArray();
            if (package.Scope != ReleaseScope.LegacyCombined && resetTasks.Length > 0)
                resetTasks[0] = resetTasks[0] with { DecisionBy = actor, Decision = ApprovalDecision.Approved, Comment = "提交人自检", DecidedAt = now };
            var pendingCount = resetTasks.Count(task => task.Decision is null);
            var state = pendingCount <= 1 ? ReleasePackageState.Approval : ReleasePackageState.ProcessReview;
            var submitted = package with { State = state, ApprovalTasks = resetTasks, PublishedAt = null, PublishedPath = null, PublishError = null };
            packages[package.Id] = submitted;
            return Task.FromResult(submitted);
        }
    }

    public Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
                throw new PdmConflictException("只有审批中的发布包可以撤回。");
            var now = timeProvider.GetUtcNow();
            if (package.LocksDocuments)
            {
                foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
                {
                    if (documents.TryGetValue(documentId, out var document) && document.State == DocumentLifecycleState.InReview)
                        documents[documentId] = document with { State = DocumentLifecycleState.Work, UpdatedAt = now };
                }
            }
            var tasks = package.ApprovalTasks.Select(task => task with
            {
                DecisionBy = null, Decision = null, Comment = null, DecidedAt = null,
                IsEmergencySubstitute = false, EmergencyReason = null
            }).ToArray();
            var withdrawn = package with { State = ReleasePackageState.Draft, ApprovalTasks = tasks };
            packages[releasePackageId] = withdrawn;
            return Task.FromResult(withdrawn);
        }
    }

    public Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, bool emergencySubstitute, string? emergencyReason, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = packages.Values.FirstOrDefault(candidate => candidate.ApprovalTasks.Any(task => task.Id == taskId))
                ?? throw new PdmNotFoundException("审批任务不存在。 ");
            var task = package.ApprovalTasks.Single(item => item.Id == taskId);
            if (task.Decision is not null)
            {
                throw new PdmConflictException("审批任务已经处理。 ");
            }

            if (!emergencySubstitute && !string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase) && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmRuleException("只能处理分配给自己的审批任务。 ");
            }

            var currentTask = package.ApprovalTasks.OrderBy(item => item.StepOrder).FirstOrDefault(item => item.Decision is null);
            if (currentTask?.Id != taskId || package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
            {
                throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
            }

            var updatedTask = task with
            {
                Decision = decision,
                DecisionBy = actor,
                Comment = comment,
                DecidedAt = timeProvider.GetUtcNow(),
                IsEmergencySubstitute = emergencySubstitute,
                EmergencyReason = emergencyReason
            };
            var updatedTasks = package.ApprovalTasks.Select(item => item.Id == taskId ? updatedTask : item).ToArray();
            var remaining = updatedTasks.Count(item => item.Decision is null);
            var nextState = decision == ApprovalDecision.Rejected
                ? ReleasePackageState.Rejected
                : remaining == 0 ? ReleasePackageState.Publishing
                : remaining == 1 ? ReleasePackageState.Approval
                : ReleasePackageState.ProcessReview;
            var updated = package with { ApprovalTasks = updatedTasks, State = nextState };
            packages[package.Id] = updated;
            if (nextState == ReleasePackageState.Rejected)
            {
                var now = timeProvider.GetUtcNow();
                if (package.LocksDocuments)
                {
                    foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
                    {
                        if (documents.TryGetValue(documentId, out var document) && document.State == DocumentLifecycleState.InReview)
                            documents[documentId] = document with { State = DocumentLifecycleState.Work, UpdatedAt = now };
                    }
                }
            }
            return Task.FromResult(updated);
        }
    }

    public Task<ReleasePackage> TransferApprovalAsync(Guid taskId, string expectedAssignee, string targetUsername, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = packages.Values.FirstOrDefault(candidate => candidate.ApprovalTasks.Any(task => task.Id == taskId))
                ?? throw new PdmNotFoundException("审批任务不存在。 ");
            var task = package.ApprovalTasks.Single(item => item.Id == taskId);
            var currentTask = package.ApprovalTasks.OrderBy(item => item.StepOrder).FirstOrDefault(item => item.Decision is null);
            if (task.Decision is not null || currentTask?.Id != taskId || package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
                throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
            if (!string.Equals(task.Assignee, expectedAssignee, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(expectedAssignee, "admin", StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException("审批任务已转交给其他用户，请刷新后重试。 ");

            var updated = package with
            {
                ApprovalTasks = package.ApprovalTasks.Select(item => item.Id == taskId
                    ? item with { Assignee = targetUsername }
                    : item).ToArray()
            };
            packages[package.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (document.CheckedOutBy is not null) throw new PdmConflictException("图档正在编辑，不能作废。");
            if (document.State == DocumentLifecycleState.InReview) throw new PdmConflictException("图档正在审批，不能作废。");
            if (document.State == DocumentLifecycleState.Obsolete) return Task.FromResult(document);
            var obsolete = document with { State = DocumentLifecycleState.Obsolete, UpdatedAt = timeProvider.GetUtcNow() };
            documents[documentId] = obsolete;
            return Task.FromResult(obsolete);
        }
    }

    public Task MarkPublishedAsync(Guid releasePackageId, string publishedPath, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package))
            {
                throw new PdmNotFoundException("发布包不存在。 ");
            }

            packages[releasePackageId] = package with
            {
                State = ReleasePackageState.Published,
                PublishedPath = publishedPath,
                PublishedAt = publishedAt
            };
            foreach (var versionId in new[] { package.StandardBomVersionId, package.NonStandardBomVersionId, package.ElectricalBomVersionId }.Where(id => id.HasValue).Select(id => id!.Value))
                if (bomVersions.TryGetValue(versionId, out var version) && version.State == BomVersionState.InReview)
                    bomVersions[versionId] = version with { State = BomVersionState.Released, UpdatedAt = publishedAt, ReleasedAt = publishedAt };
            return Task.CompletedTask;
        }
    }

    public Task<ManufacturingBomBaseline> MarkPublishedWithBomBaselineAsync(ReleasePackage package, string publishedPath, DateTimeOffset publishedAt, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(package.Id, out var current) || current.State != ReleasePackageState.Publishing)
                throw new PdmConflictException("发布包状态已变化，不能生成制造BOM基线。");
            if (!current.StandardBomVersionId.HasValue || !current.NonStandardBomVersionId.HasValue || !current.ElectricalBomVersionId.HasValue)
                throw new PdmConflictException("发布包没有绑定三套独立BOM版本。");
            var versionIds = new[] { current.StandardBomVersionId.Value, current.NonStandardBomVersionId.Value, current.ElectricalBomVersionId.Value };
            foreach (var id in versionIds)
                if (bomVersions.TryGetValue(id, out var version) && version.State == BomVersionState.InReview)
                    bomVersions[id] = version with { State = BomVersionState.Released, UpdatedBy = actor, UpdatedAt = publishedAt, ReleasedAt = publishedAt };
            var sequence = manufacturingBomBaselines.Values.Where(item => item.ProjectId == current.ProjectId).Select(item => item.Sequence).DefaultIfEmpty().Max() + 1;
            if (!projects.TryGetValue(current.ProjectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            var baseline = new ManufacturingBomBaseline(Guid.NewGuid(), current.ProjectId, sequence, $"BL-{ProjectNumberPolicy.BusinessCode(project)}-{sequence:D3}", versionIds[0], versionIds[1], versionIds[2],
                current.ChangeNumber ?? current.Number, current.ChangeReason ?? "兼容既有发布流程创建的设变", current.EffectiveSerialFrom ?? "未指定", current.EffectiveSerialTo,
                current.Id, actor, publishedAt);
            manufacturingBomBaselines[baseline.Id] = baseline;
            packages[current.Id] = current with { State = ReleasePackageState.Published, PublishedPath = publishedPath, PublishedAt = publishedAt, PublishError = null };
            return Task.FromResult(baseline);
        }
    }

    public Task MarkPublishFailedAsync(Guid releasePackageId, string error, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package))
            {
                throw new PdmNotFoundException("发布包不存在。 ");
            }

            packages[releasePackageId] = package with { State = ReleasePackageState.PublishFailed };
            return Task.CompletedTask;
        }
    }

    public Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(user);
    }

    public Task<UserProfile?> FindUserProfileAsync(string username, CancellationToken cancellationToken)
    {
        if (userProfiles.TryGetValue(username, out var profile)) return Task.FromResult<UserProfile?>(profile);
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<UserProfile?>(user is null ? null : CreateDefaultProfile(user));
    }

    public Task<UserProfile> UpdateUserProfileAsync(string username, string? nickname, string gender, string? landline, string? mobilePhone, string? email, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("用户不存在。");
        var profile = new UserProfile(user.Username, user.DisplayName, nickname, gender, landline, mobilePhone, email);
        userProfiles[user.Username] = profile;
        return Task.FromResult(profile);
    }

    public Task<UserAccount> UpdateUserPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("用户不存在。");
        var updated = user with { PasswordHash = passwordHash, TokenVersion = user.TokenVersion + 1 };
        users[user.Id] = updated;
        return Task.FromResult(updated);
    }

    public Task CreatePasswordResetRequestAsync(UserAccount user, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        var existing = passwordResetTasks.Values.FirstOrDefault(item => string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            passwordResetTasks[existing.Id] = existing with { RequestedAt = requestedAt };
            return Task.CompletedTask;
        }
        var task = new PasswordResetTask(Guid.NewGuid(), user.Username, user.DisplayName, requestedAt);
        passwordResetTasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PasswordResetTask>> ListPasswordResetTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PasswordResetTask>>(passwordResetTasks.Values.OrderBy(item => item.RequestedAt).ToArray());

    public Task CompletePasswordResetTaskAsync(Guid taskId, string passwordHash, string actor, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        if (!passwordResetTasks.TryRemove(taskId, out var task)) throw new PdmNotFoundException("密码重置申请不存在或已处理。");
        return UpdateUserPasswordAsync(task.Username, passwordHash, cancellationToken);
    }

    public Task<int> CountUsersAsync(CancellationToken cancellationToken) => Task.FromResult(users.Count);

    public Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken)
    {
        if (user.CompanyId is null)
        {
            Guid? defaultCompanyId = null;
            foreach (var project in projects.Values)
            {
                if (project.OrganizationId is Guid organizationId && organizations.TryGetValue(organizationId, out var organization) && organization.IsActive)
                {
                    defaultCompanyId = organizationId;
                    break;
                }
            }
            if (!defaultCompanyId.HasValue || defaultCompanyId == Guid.Empty) defaultCompanyId = SeedOrganizations().First(item => item.IsActive).Id;
            user = user with { CompanyId = defaultCompanyId == Guid.Empty ? null : defaultCompanyId };
        }
        if (users.Values.Any(item => string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase)) || !users.TryAdd(user.Id, user))
        {
            throw new PdmConflictException("用户名已经存在。 ");
        }

        return Task.CompletedTask;
    }

    public Task<UserAccount> UpdateUserAsync(string username, string displayName, UserRole role, string roleCode, IReadOnlyList<string> roleCodes, bool isActive, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("用户不存在。");
        var normalizedRoles = roleCodes.Prepend(roleCode).Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = user with { DisplayName = displayName, Role = role, RoleCode = roleCode, RoleCodes = normalizedRoles, IsActive = isActive, TokenVersion = user.TokenVersion + 1 };
        users[user.Id] = updated;
        return Task.FromResult(updated);
    }

    private static UserProfile CreateDefaultProfile(UserAccount user) => new(user.Username, user.DisplayName, null, "unspecified", null, null, null);

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        audits.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(audits.Where(entry => HasUserPermission(actor, role, PermissionCodes.AuditView) || string.Equals(entry.Actor, actor, StringComparison.OrdinalIgnoreCase)).OrderByDescending(entry => entry.OccurredAt).Take(Math.Clamp(take, 1, 500)).ToArray());

    public Task<IReadOnlyList<AuditEntry>> ListProjectAuditAsync(Guid projectId, int take, CancellationToken cancellationToken)
    {
        var documentIds = documents.Values.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToHashSet();
        var versionIds = versions.Values.Where(item => documentIds.Contains(item.DocumentId)).Select(item => item.Id).ToHashSet();
        var packageIds = packages.Values.Where(item => item.ProjectId == projectId).Select(item => item.Id).ToHashSet();
        var taskIds = packages.Values.Where(item => item.ProjectId == projectId).SelectMany(item => item.ApprovalTasks).Select(item => item.Id).ToHashSet();
        var projectIdText = projectId.ToString();
        var entries = audits.Where(entry =>
                (entry.EntityType == nameof(Project) && entry.EntityId == projectIdText)
                || (entry.EntityType == nameof(BomItem) && entry.EntityId == projectIdText)
                || (entry.EntityType == nameof(PdmDocument) && Guid.TryParse(entry.EntityId, out var documentId) && documentIds.Contains(documentId))
                || (entry.EntityType == nameof(DocumentVersion) && Guid.TryParse(entry.EntityId, out var versionOrDocumentId) && (versionIds.Contains(versionOrDocumentId) || documentIds.Contains(versionOrDocumentId)))
                || (entry.EntityType == nameof(ReleasePackage) && Guid.TryParse(entry.EntityId, out var packageId) && packageIds.Contains(packageId))
                || (entry.EntityType == nameof(ApprovalTask) && Guid.TryParse(entry.EntityId, out var taskId) && taskIds.Contains(taskId)))
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToArray();
        return Task.FromResult<IReadOnlyList<AuditEntry>>(entries);
    }

    private void EnsureProjectFolderTree(Guid projectId)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.RootProjectId ?? project.Id;
        var root = projects.GetValueOrDefault(rootId) ?? throw new PdmNotFoundException("主项目不存在。");
        var targets = projects.Values.Where(item => item.Id == root.Id || item.RootProjectId == root.Id).OrderBy(item => item.Code).ToArray();
        var actualIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var rootFolder = UpsertFolder(root.Id, null, root.Id, "root", "root", root.Code, ProjectFolderPurpose.Root, 0, true, true);
        actualIds["root"] = rootFolder.Id;

        var pending = folderTemplate.Values.Where(item => item.Purpose != ProjectFolderPurpose.ProjectContainer).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(item => item.ParentKey is null || actualIds.ContainsKey(item.ParentKey)).ToArray();
            if (ready.Length == 0) throw new PdmRuleException("目录模板存在无效的父子关系。");
            foreach (var node in ready)
            {
                var parentId = node.ParentKey is null ? rootFolder.Id : actualIds[node.ParentKey];
                actualIds[node.FolderKey] = UpsertFolder(root.Id, parentId, null, node.FolderKey, node.FolderKey, node.Name,
                    node.Purpose, node.SortOrder, node.IsSystem, node.InheritPermissions).Id;
                pending.Remove(node);
            }
        }

        foreach (var templateKey in new[] { "mechanical.project", "electrical.project" })
        {
            var node = folderTemplate[templateKey];
            var parentId = actualIds[node.ParentKey!];
            foreach (var target in targets)
            {
                var name = target.Id == root.Id ? $"{root.Code}-0" : target.Code;
                UpsertFolder(root.Id, parentId, target.Id, $"{templateKey}:{target.Id:N}", templateKey, name,
                    ProjectFolderPurpose.ProjectContainer, 10 + (target.ChildSequence ?? 0), true, node.InheritPermissions);
            }
        }

        foreach (var document in documents.Values.Where(item => item.FolderId is null && targets.Any(target => target.Id == item.ProjectId)).ToArray())
            documents[document.Id] = document with { FolderId = ResolveDocumentFolder(document.ProjectId, null).Id };
    }

    private ProjectFolder UpsertFolder(Guid rootProjectId, Guid? parentId, Guid? targetProjectId, string folderKey, string templateKey,
        string name, ProjectFolderPurpose purpose, int sortOrder, bool isSystem, bool inheritPermissions)
    {
        var existing = projectFolders.Values.FirstOrDefault(item => item.RootProjectId == rootProjectId && string.Equals(item.FolderKey, folderKey, StringComparison.OrdinalIgnoreCase));
        var folder = existing is null
            ? new ProjectFolder(Guid.NewGuid(), rootProjectId, parentId, targetProjectId, folderKey, templateKey, name, purpose, sortOrder, isSystem, inheritPermissions)
            : existing with { ParentFolderId = parentId, TargetProjectId = targetProjectId, TemplateKey = templateKey, Name = name, Purpose = purpose, SortOrder = sortOrder, IsSystem = isSystem, InheritPermissions = inheritPermissions };
        projectFolders[folder.Id] = folder;
        return folder;
    }

    private ProjectFolder ResolveDocumentFolder(Guid projectId, Guid? requestedFolderId)
    {
        var folder = requestedFolderId is null
            ? projectFolders.Values.FirstOrDefault(item => item.TargetProjectId == projectId && item.TemplateKey == "mechanical.project")
            : projectFolders.GetValueOrDefault(requestedFolderId.Value);
        if (folder is null || folder.TargetProjectId != projectId || folder.Purpose != ProjectFolderPurpose.ProjectContainer)
            throw new PdmRuleException("图档只能登记到机械图纸或电气图纸下当前项目对应的目录。");
        return folder;
    }

    private FolderAccess ResolveFolderAccess(ProjectFolder folder, IReadOnlyList<ProjectFolder> folders, string actor, UserRole role)
    {
        if (role is UserRole.Administrator or UserRole.PlatformAdministrator) return FolderAccess.All;
        var roleCodes = users.Values.FirstOrDefault(item => string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase))?.EffectiveRoleCodes ?? [role.ToString()];
        ProjectFolder? current = folder;
        while (current is not null)
        {
            var rules = current.Permissions.Where(item =>
                item.PrincipalType == FolderPrincipalType.User && string.Equals(item.PrincipalKey, actor, StringComparison.OrdinalIgnoreCase)
                || item.PrincipalType == FolderPrincipalType.Role && roleCodes.Contains(item.PrincipalKey, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (rules.Length == 0 && folderTemplate.TryGetValue(current.TemplateKey, out var template))
                rules = template.Permissions.Where(item =>
                    item.PrincipalType == FolderPrincipalType.User && string.Equals(item.PrincipalKey, actor, StringComparison.OrdinalIgnoreCase)
                    || item.PrincipalType == FolderPrincipalType.Role && roleCodes.Contains(item.PrincipalKey, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (rules.Length > 0) return rules.Aggregate(FolderAccess.None, (value, item) => value | item.Access);
            if (!current.InheritPermissions || current.ParentFolderId is null) break;
            current = folders.FirstOrDefault(item => item.Id == current.ParentFolderId.Value);
        }
        if (folder.Purpose == ProjectFolderPurpose.Release) return FolderAccess.View | FolderAccess.Download;
        return role == UserRole.Engineer
            ? FolderAccess.View | FolderAccess.Download | FolderAccess.Upload | FolderAccess.Edit
            : FolderAccess.View | FolderAccess.Download;
    }

    private static IReadOnlyList<FolderPermissionRule> NormalizeFolderPermissions(IEnumerable<SaveFolderPermissionCommand> permissions)
    {
        var normalized = permissions.Select(item => item with { PrincipalKey = item.PrincipalKey.Trim() }).ToArray();
        if (normalized.Any(item => string.IsNullOrWhiteSpace(item.PrincipalKey))) throw new PdmRuleException("权限主体不能为空。");
        if (normalized.GroupBy(item => $"{item.PrincipalType}:{item.PrincipalKey}", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new PdmRuleException("同一目录不能重复配置相同权限主体。");
        return normalized.Select(item => new FolderPermissionRule(Guid.NewGuid(), item.PrincipalType, item.PrincipalKey, item.Access)).ToArray();
    }

    private static Dictionary<string, ProjectFolderTemplateNode> CreateDefaultFolderTemplate()
    {
        ProjectFolderTemplateNode Node(string key, string? parent, string name, ProjectFolderPurpose purpose, int order) => new(key, parent, name, purpose, order, true, true);
        return new[]
        {
            Node("mechanical", null, "机械图纸", ProjectFolderPurpose.MechanicalRoot, 10),
            Node("electrical", null, "电气图纸", ProjectFolderPurpose.ElectricalRoot, 20),
            Node("purchase", null, "采购清单", ProjectFolderPurpose.Standard, 30),
            Node("production", null, "生产资料", ProjectFolderPurpose.Standard, 40),
            Node("project-files", null, "项目文件", ProjectFolderPurpose.Standard, 50),
            Node("presales", null, "售前资料", ProjectFolderPurpose.Standard, 60),
            Node("customer-files", null, "客户资料", ProjectFolderPurpose.Standard, 70),
            Node("acceptance", null, "验收资料", ProjectFolderPurpose.Standard, 80),
            Node("media", null, "照片视频", ProjectFolderPurpose.Standard, 90),
            Node("minutes", null, "会议纪要", ProjectFolderPurpose.Standard, 100),
            Node("mechanical.project", "mechanical", "项目目录（自动生成）", ProjectFolderPurpose.ProjectContainer, 10),
            Node("mechanical.air-sequence", "mechanical", "气路时序", ProjectFolderPurpose.Standard, 100),
            Node("mechanical.nameplate", "mechanical", "铭牌", ProjectFolderPurpose.Standard, 110),
            Node("mechanical.other", "mechanical", "其他图纸", ProjectFolderPurpose.Standard, 120),
            Node("mechanical.release", "mechanical", "机械发布", ProjectFolderPurpose.Release, 130),
            Node("electrical.project", "electrical", "项目目录（自动生成）", ProjectFolderPurpose.ProjectContainer, 10),
            Node("electrical.release", "electrical", "电气发布", ProjectFolderPurpose.Release, 130)
        }.ToDictionary(item => item.FolderKey, StringComparer.OrdinalIgnoreCase);
    }

    private Project ApplyCapabilities(Project project, string actor, UserRole role)
    {
        var documentCount = documents.Values.Count(item => item.ProjectId == project.Id);
        var modelDocumentCount = documents.Values.Count(item => item.ProjectId == project.Id && item.Kind is DocumentKind.Assembly or DocumentKind.Part);
        var drawingDocumentCount = documents.Values.Count(item => item.ProjectId == project.Id && item.Kind == DocumentKind.Drawing);
        var businessStatus = BuildBusinessStatus(project.Id);
        var rootDocumentCheckedOutBy = RootDocumentCheckedOutBy(project.Id);
        if (role is UserRole.Administrator or UserRole.PlatformAdministrator || TenantContext.Current?.IsPlatformAdministrator == true)
            return project with { CanAssignExecutionUnit = project.ParentProjectId is null, CanManageMainStaffing = project.ParentProjectId is null && project.ExecutionUnitId is not null, CanAssignDesigners = project.ExecutionUnitId is not null, CanReadContent = true, CanSubmitArchive = true, DocumentCount = documentCount, ModelDocumentCount = modelDocumentCount, DrawingDocumentCount = drawingDocumentCount, BusinessStatus = businessStatus, RootDocumentCheckedOutBy = rootDocumentCheckedOutBy };
        var managesExecutionUnit = project.ExecutionUnitId is Guid executionUnitId
            && organizationManagers.TryGetValue(executionUnitId, out var managers)
            && (string.Equals(managers.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || managers.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase));
        var canManage = project.ParentProjectId is null && managesExecutionUnit;
        var belongsToProjectStaffing = string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
            || project.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase);
        var actorAccount = users.Values.FirstOrDefault(item => string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase));
        var isMechanicalSupervisor = project.ExecutionUnitId is Guid supervisorUnitId
            && actorAccount?.EffectiveRoleCodes.Contains("MechanicalManager", StringComparer.OrdinalIgnoreCase) == true
            && organizationMemberships.TryGetValue(actor, out var supervisorMembership)
            && supervisorMembership.UnitIds.Any(unitId => IsUnitWithin(unitId, supervisorUnitId));
        var canReadContent = HasUserPermission(actor, role, PermissionCodes.ProjectContentView);
        return project with
        {
            CanAssignExecutionUnit = HasUserPermission(actor, role, PermissionCodes.ProjectExecutionAssign) && project.ParentProjectId is null && project.OrganizationId == UserPrimaryCompanyId(actor),
            CanManageMainStaffing = HasUserPermission(actor, role, PermissionCodes.ProjectStaffingManage) && canManage,
            CanAssignDesigners = HasUserPermission(actor, role, PermissionCodes.ProjectDesignerAssign) && project.ExecutionUnitId is not null && (managesExecutionUnit || isMechanicalSupervisor || belongsToProjectStaffing),
            CanReadContent = canReadContent,
            CanSubmitArchive = canReadContent
                && HasUserPermission(actor, role, PermissionCodes.DocumentEdit)
                && ProjectSubmissionPolicy.CanSubmitArchive(project, actor, false),
            DocumentCount = canReadContent ? documentCount : null,
            ModelDocumentCount = canReadContent ? modelDocumentCount : null,
            DrawingDocumentCount = canReadContent ? drawingDocumentCount : null,
            BusinessStatus = canReadContent ? businessStatus : null,
            RootDocumentCheckedOutBy = canReadContent ? rootDocumentCheckedOutBy : null
        };
    }

    private string BuildBusinessStatus(Guid projectId)
    {
        var statuses = new List<string>();
        if (!string.IsNullOrWhiteSpace(RootDocumentCheckedOutBy(projectId))) statuses.Add("编辑中");
        var projectPackages = packages.Values.Where(item => item.ProjectId == projectId).ToArray();
        if (projectPackages.Any(item => item.State == ReleasePackageState.Draft)) statuses.Add("待提交");
        if (projectPackages.Any(item => item.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval)) statuses.Add("待审批");
        if (projectPackages.Any(item => item.State == ReleasePackageState.Rejected)) statuses.Add("审批退回");
        if (projectPackages.Any(item => item.State == ReleasePackageState.Publishing)) statuses.Add("发布中");
        if (projectPackages.Any(item => item.State == ReleasePackageState.PublishFailed)) statuses.Add("发布失败");
        return statuses.Count == 0 ? "正常" : string.Join("、", statuses);
    }

    private string? RootDocumentCheckedOutBy(Guid projectId) =>
        projectId == SeedData.ProjectId && documents.TryGetValue(referenceRootDocumentId, out var rootDocument)
            ? rootDocument.CheckedOutBy
            : null;

    private Guid CurrentSession(Guid documentId)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            return document.CheckoutSessionId ?? throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        }
    }

    private static void EnsureSessionOwner(PdmDocument document, string actor, Guid sessionId, string action)
    {
        if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase) || document.CheckoutSessionId != sessionId)
            throw new PdmConflictException($"编辑会话已经失效，不能{action}。请另存本地修改或重新获取权限。");
    }

    private static PdmDocument ClearEditLock(PdmDocument document, DateTimeOffset now) => document with
    {
        CheckedOutBy = null,
        CheckedOutAt = null,
        CheckoutSessionId = null,
        CheckoutMachine = null,
        CheckoutLastHeartbeatAt = null,
        CheckoutLeaseExpiresAt = null,
        CheckoutReleaseRequestedBy = null,
        CheckoutReleaseRequestedAt = null,
        CheckoutReleaseRequestReason = null,
        UpdatedAt = now
    };

    private bool HasUserPermission(string actor, UserRole role, string permissionCode) => role == UserRole.Administrator
        || PermissionsFor(users.Values.FirstOrDefault(item => string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase))?.EffectiveRoleCodes ?? [role.ToString()], role).Contains(permissionCode);

    private bool HasRolePermission(UserRole role, string permissionCode) => role == UserRole.Administrator
        || PermissionsFor(role.ToString(), role).Contains(permissionCode);

    private IReadOnlySet<string> PermissionsFor(string roleCode, UserRole fallbackRole) => fallbackRole == UserRole.Administrator
        ? RolePermissionCatalog.Defaults[UserRole.Administrator]
        : rolePermissions.GetValueOrDefault(roleCode, RolePermissionCatalog.Defaults[fallbackRole]);

    private IReadOnlySet<string> PermissionsFor(IEnumerable<string> roleCodes, UserRole fallbackRole) => fallbackRole == UserRole.Administrator
        ? RolePermissionCatalog.Defaults[UserRole.Administrator]
        : roleCodes.SelectMany(roleCode => rolePermissions.GetValueOrDefault(roleCode, new HashSet<string>(StringComparer.Ordinal))).ToHashSet(StringComparer.Ordinal);

    private HashSet<Guid> UserOrganizationIds(string username)
    {
        if (!organizationMemberships.TryGetValue(username, out var membership)) return [];
        return membership.UnitIds.Select(unitId => organizationUnits.GetValueOrDefault(unitId)?.OrganizationId)
            .Where(organizationId => organizationId is not null).Select(organizationId => organizationId!.Value).ToHashSet();
    }

    private Guid? UserPrimaryCompanyId(string username) => users.Values
        .FirstOrDefault(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))?.CompanyId;

    private bool IsUnitWithin(Guid unitId, Guid ancestorId)
    {
        var current = organizationUnits.GetValueOrDefault(unitId);
        while (current is not null && current.IsActive)
        {
            if (current.Id == ancestorId) return true;
            current = current.ParentUnitId is Guid parentId ? organizationUnits.GetValueOrDefault(parentId) : null;
        }
        return false;
    }

    private RolePermissionDirectory BuildRolePermissionDirectory() => new(
        RolePermissionCatalog.Permissions,
        roleDefinitions.Values.OrderByDescending(definition => definition.IsSystem).ThenBy(definition => definition.Name).Select(definition => new RolePermissionSettings(
            definition.RoleCode,
            definition.Name,
            definition.Description,
            definition.BaseRole,
            definition.IsSystem,
            definition.IsSystemAdministrator,
            PermissionsFor(definition.RoleCode, definition.BaseRole).Order().ToArray(),
            users.Values.Count(item => item.HasRole(definition.RoleCode)))).ToArray());
}
