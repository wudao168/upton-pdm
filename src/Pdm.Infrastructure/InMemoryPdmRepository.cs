using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryPdmRepository : IPdmRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, Project> projects = new();
    private readonly ConcurrentDictionary<Guid, PdmDocument> documents = new();
    private readonly ConcurrentDictionary<Guid, DocumentModelDrawingRelation> documentRelations = new();
    private readonly ConcurrentDictionary<Guid, ProjectFolder> projectFolders = new();
    private readonly Dictionary<string, ProjectFolderTemplateNode> folderTemplate = CreateDefaultFolderTemplate();
    private readonly ConcurrentDictionary<Guid, UserAccount> users = new();
    private readonly ConcurrentDictionary<UserRole, IReadOnlySet<string>> rolePermissions = new();
    private readonly ConcurrentDictionary<Guid, PdmCustomer> customers = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> projectResponsibles = new();
    private readonly ConcurrentDictionary<Guid, ProjectOrganization> organizations = new();
    private readonly ConcurrentDictionary<Guid, OrganizationUnit> organizationUnits = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<Guid> UnitIds, Guid PrimaryUnitId)> organizationMemberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, OrganizationUnitManagers> organizationManagers = new();
    private readonly ConcurrentDictionary<int, EquipmentTypeDefinition> equipmentTypes = new();
    private readonly ConcurrentDictionary<Guid, ReleasePackage> packages = new();
    private readonly ConcurrentDictionary<Guid, DocumentVersion> versions = new();
    private readonly ConcurrentDictionary<Guid, string> documentSourceFingerprints = new();
    private readonly ConcurrentQueue<AuditEntry> audits = new();
    private readonly Dictionary<Guid, int> projectCounters = new();
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<Guid, int> serialCounters = new();
    private readonly Dictionary<(Guid OrganizationId, string CustomerCode), int> customerCounters = new();
    private readonly List<BomItem> bomItems;
    private DocumentReferenceNode referenceTree;
    private Guid referenceRootDocumentId = SeedData.RootDocumentId;
    private PdmSystemSettings systemSettings = new(@"D:\PDM\Vault", @"D:\PDM\Release");
    private CrmIntegrationConfiguration crmIntegrationConfiguration = new(string.Empty, string.Empty, string.Empty, false, 60, null, 0, null, null);

    public InMemoryPdmRepository(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        foreach (var (role, permissions) in RolePermissionCatalog.Defaults) rolePermissions[role] = permissions;
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
            "crm",
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
        Task.FromResult<IReadOnlyList<Project>>(projects.Values.OrderBy(project => project.Code).ToArray());

    public Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(!HasRolePermission(role, PermissionCodes.ProjectView) ? [] : projects.Values
            .Where(project => CanViewProject(project, actor, role))
            .Select(project => ApplyCapabilities(project, actor, role))
            .OrderBy(project => project.Code)
            .ToArray());

    public Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        projects.TryGetValue(projectId, out var project);
        return Task.FromResult(project);
    }

    public Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(HasRolePermission(role, PermissionCodes.ProjectView) && projects.TryGetValue(projectId, out var project) && CanViewProject(project, actor, role));

    public Task<bool> HasProjectContentReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(role == UserRole.Administrator || (HasRolePermission(role, PermissionCodes.ProjectContentView)
            && projects.TryGetValue(projectId, out var project) && HasProjectContentAssignment(project, actor)));

    public Task<bool> HasChildProjectsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult(projects.Values.Any(project => project.ParentProjectId == projectId));

    public Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.ContainsKey(projectId)) throw new PdmNotFoundException("项目不存在。");
            if (projects.Values.Any(project => project.ParentProjectId == projectId))
                throw new PdmConflictException("该项目存在子项目，请先删除子项目。");
            if (documents.Values.Any(document => document.ProjectId == projectId))
                throw new PdmConflictException("该项目存在受控图档，不能删除。");
            if (bomItems.Any(item => item.ProjectId == projectId))
                throw new PdmConflictException("该项目存在BOM数据，不能删除。");
            if (packages.Values.Any(package => package.ProjectId == projectId))
                throw new PdmConflictException("该项目存在审批或发布包，不能删除。");

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
            .Where(customer => string.Equals(customer.SourceSystem, "crm", StringComparison.OrdinalIgnoreCase))
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
            foreach (var existing in customers.Values.Where(customer => string.Equals(customer.SourceSystem, "crm", StringComparison.OrdinalIgnoreCase)).ToArray())
                customers[existing.Id] = existing with { IsActive = false };
            foreach (var item in syncedCustomers)
            {
                var existing = customers.Values.FirstOrDefault(customer => string.Equals(customer.Code, item.Code, StringComparison.OrdinalIgnoreCase));
                var customer = new PdmCustomer(existing?.Id ?? Guid.NewGuid(), item.Code, item.Name, true, "crm", syncedAt);
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

    public Task<PdmSystemSettings> GetSystemSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(systemSettings);

    public Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        systemSettings = settings;
        return Task.FromResult(settings);
    }

    public Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserAccount>>(users.Values.OrderBy(user => user.Username).ToArray());

    public Task<RolePermissionDirectory> GetRolePermissionDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(BuildRolePermissionDirectory());

    public Task<IReadOnlySet<string>> GetRolePermissionsAsync(UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(rolePermissions.GetValueOrDefault(role, RolePermissionCatalog.Defaults[role]));

    public Task<bool> HasRolePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken) =>
        Task.FromResult(role == UserRole.Administrator
            || rolePermissions.GetValueOrDefault(role, RolePermissionCatalog.Defaults[role]).Contains(permissionCode));

    public Task<RolePermissionDirectory> SetRolePermissionsAsync(UserRole role, IReadOnlyList<string> permissionCodes, CancellationToken cancellationToken)
    {
        rolePermissions[role] = RolePermissionCatalog.Normalize(role, permissionCodes);
        return Task.FromResult(BuildRolePermissionDirectory());
    }

    public Task<OrganizationDirectory> GetOrganizationDirectoryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new OrganizationDirectory(
            organizations.Values.OrderBy(item => item.ProjectCompanyCode).ToArray(),
            organizationUnits.Values.OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArray(),
            organizationMemberships.SelectMany(item => item.Value.UnitIds.Select(unitId => new OrganizationMembership(unitId, item.Key, unitId == item.Value.PrimaryUnitId))).ToArray(),
            organizationManagers.Values.ToArray(),
            users.Values.OrderBy(item => item.Username).Select(item => new OrganizationDirectoryUser(item.Username, item.DisplayName, item.Role, item.IsActive)).ToArray()));

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
            var saved = new OrganizationUnit(command.Id ?? Guid.NewGuid(), command.OrganizationId, command.ParentUnitId, command.Code, command.Name, command.Kind, command.IsActive, command.SortOrder);
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
        organizationManagers[unitId] = new OrganizationUnitManagers(unitId, primaryManager, collaborativeManagers.ToArray());
        return await GetOrganizationDirectoryAsync(cancellationToken);
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
                CollaborativeProjectManagers = [], DesignLead = null, Designers = []
            };
            projects[projectId] = project;
            foreach (var child in projects.Values.Where(item => item.ParentProjectId == projectId).ToArray())
                projects[child.Id] = child with { ExecutionUnitId = unit.Id, ExecutionUnitName = unit.Name, PrimaryProjectManager = null, CollaborativeProjectManagers = [], DesignLead = null, Designers = [] };
            return Task.FromResult(project);
        }
    }

    public Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project) || project.ParentProjectId is not null) throw new PdmNotFoundException("主项目不存在。");
            project = project with { PrimaryProjectManager = command.PrimaryProjectManager, CollaborativeProjectManagers = command.CollaborativeProjectManagers.ToArray(), DesignLead = command.DesignLead };
            projects[projectId] = project;
            foreach (var child in projects.Values.Where(item => item.ParentProjectId == projectId).ToArray())
                projects[child.Id] = child with { PrimaryProjectManager = command.PrimaryProjectManager, CollaborativeProjectManagers = command.CollaborativeProjectManagers.ToArray(), DesignLead = command.DesignLead };
            return Task.FromResult(project);
        }
    }

    public Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project) || project.ParentProjectId is null) throw new PdmNotFoundException("子项目不存在。");
            project = project with { Designers = designers.ToArray() };
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
            var projectSequence = Next(projectCounters, organization.Id);
            var customerKey = (organization.Id, customer.Code.ToUpperInvariant());
            var customerSequence = Next(customerCounters, customerKey);
            var serialStart = ReserveSerials(organization.Id, command.Quantity);
            var code = $"{command.ProjectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}";
            var model = $"{organization.ModelCompanyCode}-{command.EquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-00";
            var project = BuildNumberedProject(Guid.NewGuid(), code, command.Name, command.ProjectAlias, organization, command.ProjectTypeCode,
                command.EquipmentTypeCode, customer.Code, customer.Name, customerSequence, model, command.SignedDate,
                command.Quantity, null, null, command.Owner, Path.Combine(command.VaultLocation, code), Path.Combine(command.ReleaseLocation, code), serialStart);
            project = project with { ResponsibleUsers = [command.Owner] };
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
            if (!projects.TryGetValue(command.ParentProjectId, out var parent)) throw new PdmNotFoundException("主项目不存在。");
            if (parent.ParentProjectId is not null) throw new PdmRuleException("只能在主项目下创建子项目。");
            if (parent.OrganizationId is null || parent.EquipmentTypeCode is null || parent.CustomerProjectSequence is null
                || parent.SignedDate is null || string.IsNullOrWhiteSpace(parent.ProjectTypeCode)
                || string.IsNullOrWhiteSpace(parent.CustomerCode) || string.IsNullOrWhiteSpace(parent.CustomerName))
                throw new PdmRuleException("旧项目缺少自动编号资料，不能直接创建子项目。");
            var organization = NumberingOptions().Organizations.Single(item => item.Id == parent.OrganizationId);
            var childSequence = projects.Values.Where(item => item.ParentProjectId == parent.Id).Select(item => item.ChildSequence ?? 0).DefaultIfEmpty().Max() + 1;
            var serialStart = ReserveSerials(organization.Id, command.Quantity);
            var code = $"{parent.Code}-{childSequence}";
            var model = $"{organization.ModelCompanyCode}-{parent.EquipmentTypeCode.Value}-{parent.CustomerCode}-{parent.CustomerProjectSequence.Value:D3}-{childSequence:D2}";
            var project = BuildNumberedProject(Guid.NewGuid(), code, command.Name, command.ProjectAlias, organization, parent.ProjectTypeCode,
                parent.EquipmentTypeCode.Value, parent.CustomerCode, parent.CustomerName, parent.CustomerProjectSequence.Value, model,
                parent.SignedDate.Value, command.Quantity, parent.Id, childSequence, parent.Owner,
                Path.Combine(command.VaultRoot ?? systemSettings.VaultRoot, code), Path.Combine(command.ReleaseRoot ?? systemSettings.ReleaseRoot, code), serialStart);
            project = project with
            {
                ResponsibleUsers = parent.ResponsibleUsers,
                ExecutionUnitId = parent.ExecutionUnitId,
                ExecutionUnitName = parent.ExecutionUnitName,
                PrimaryProjectManager = parent.PrimaryProjectManager,
                CollaborativeProjectManagers = parent.CollaborativeProjectManagers,
                DesignLead = parent.DesignLead
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
            var rootId = project.ParentProjectId ?? project.Id;
            var folders = projectFolders.Values.Where(item => item.RootProjectId == rootId).OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArray();
            var result = folders.Select(folder => folder with
            {
                EffectiveAccess = folder.TargetProjectId is not null && (!projects.TryGetValue(folder.TargetProjectId.Value, out var target) || !CanViewProject(target, actor, role))
                    ? FolderAccess.None
                    : ResolveFolderAccess(folder, folders, actor, role)
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
            var rootId = project.ParentProjectId ?? project.Id;
            if (!projectFolders.TryGetValue(folderId, out var folder) || folder.RootProjectId != rootId) throw new PdmNotFoundException("项目目录不存在。");
            projectFolders[folderId] = folder with { Permissions = NormalizeFolderPermissions(permissions) };
            return ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
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

    private static int Next<TKey>(Dictionary<TKey, int> counters, TKey key) where TKey : notnull
    {
        var next = counters.GetValueOrDefault(key) + 1;
        counters[key] = next;
        return next;
    }

    private int ReserveSerials(Guid organizationId, int quantity)
    {
        var start = serialCounters.GetValueOrDefault(organizationId) + 1;
        serialCounters[organizationId] = start + quantity - 1;
        return start;
    }

    private static Project BuildNumberedProject(
        Guid id, string code, string name, string? projectAlias, ProjectOrganization organization, string projectTypeCode,
        int equipmentTypeCode, string customerCode, string customerName, int customerProjectSequence, string deviceModel,
        DateOnly signedDate, int quantity, Guid? parentProjectId, int? childSequence, string owner,
        string vaultLocation, string releaseLocation, int serialStart) =>
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
            SerialNumbers = Enumerable.Range(serialStart, quantity).Select(value => $"{organization.ProjectCompanyCode}{value:D7}").ToArray()
        };

    public Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values.Where(document => document.ProjectId == projectId).OrderBy(document => document.DrawingNumber).ThenBy(document => document.Kind).ToArray());

    public Task<IReadOnlyList<PdmDocument>> ListProjectTreeDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.ParentProjectId ?? project.Id;
        var projectIds = projects.Values.Where(item => item.Id == rootId || item.ParentProjectId == rootId).Select(item => item.Id).ToHashSet();
        return Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values.Where(item => projectIds.Contains(item.ProjectId)).OrderBy(item => item.ProjectId).ThenBy(item => item.DrawingNumber).ToArray());
    }

    public Task<IReadOnlyList<DocumentModelDrawingRelation>> ListDocumentRelationsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        var rootId = project.ParentProjectId ?? project.Id;
        var projectIds = projects.Values.Where(item => item.Id == rootId || item.ParentProjectId == rootId).Select(item => item.Id).ToHashSet();
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

            if (!string.IsNullOrWhiteSpace(command.SourceSha256))
            {
                var duplicateContent = documents.Values.FirstOrDefault(document =>
                    document.ProjectId == command.ProjectId
                    && !string.Equals(document.FileName, command.FileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(CurrentSourceFingerprint(document.Id), command.SourceSha256, StringComparison.OrdinalIgnoreCase));
                if (duplicateContent is not null && !command.AllowDuplicateContent)
                {
                    throw new PdmConflictException($"项目中已有内容完全相同的图档{duplicateContent.FileName}。请选择引用已有图档，或确认独立登记并填写原因。");
                }
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
                || (!HasRolePermission(role, PermissionCodes.ProjectContentView) || !HasProjectContentAssignment(project, actor)) && role != UserRole.Administrator)
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

    public Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleasePackage>>(packages.Values.Where(package => package.ProjectId == projectId).OrderByDescending(package => package.CreatedAt).ToArray());

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
        return CheckoutAsync(documentId, actor, Guid.NewGuid(), "legacy-client", now.AddMinutes(15), cancellationToken);
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, Guid sessionId, string machineName, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw new PdmNotFoundException("图档不存在。 ");
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
        return CheckInVersionAsync(documentId, actor, CurrentSession(documentId), commit, cancellationToken);
    }

    public Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            EnsureSessionOwner(document, actor, sessionId, "提交存档");
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
            var restored = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Work, StorageRelativePath = restoredFile.RelativePath, FileLength = restoredFile.Length, Sha256 = restoredFile.Sha256, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = changeNote, SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}恢复生成{revision.Display}", ApprovalTaskId = null, ReleasePackageId = null };
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

    public Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State != ReleasePackageState.Publishing)
                throw new PdmConflictException("发布包尚未进入发布状态。");
            if (!package.ApprovalTasks.Any(task => task.Id == approvalTaskId && task.Stage == ApprovalStage.Approval && task.Decision == ApprovalDecision.Approved))
                throw new PdmConflictException("最终批准记录无效。");
            var released = new List<DocumentVersion>();
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document)) continue;
                var source = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault();
                if (source is null || source.Status != DocumentVersionStatus.Work || !string.Equals(source.Revision.Display, document.Revision.Display, StringComparison.OrdinalIgnoreCase)) continue;
                var revision = source.Revision.Release();
                var version = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = $"审批发布{revision.Display}", SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布", ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId };
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

    public Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected or ReleasePackageState.PublishFailed))
                throw new PdmConflictException("只有草稿、已驳回或发布失败的发布包可以提交。");
            var documentIds = EnumerateDocumentIds(referenceTree).Distinct().ToArray();
            var editing = documentIds.Select(id => documents.GetValueOrDefault(id)).FirstOrDefault(document => document?.CheckedOutBy is not null);
            if (editing is not null) throw new PdmConflictException($"图档{editing.DrawingNumber}正在由{editing.CheckedOutBy}编辑，不能提交审批。");
            var now = timeProvider.GetUtcNow();
            foreach (var documentId in documentIds)
            {
                if (documents.TryGetValue(documentId, out var document) && document.State != DocumentLifecycleState.Obsolete)
                    documents[documentId] = document with { State = DocumentLifecycleState.InReview, UpdatedAt = now };
            }
            var tasks = package.ApprovalTasks.Select(task => task with { DecisionBy = null, Decision = null, Comment = null, DecidedAt = null }).ToArray();
            var submitted = package with { State = ReleasePackageState.ProcessReview, ApprovalTasks = tasks, PublishedAt = null, PublishedPath = null, PublishError = null };
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
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (documents.TryGetValue(documentId, out var document) && document.State == DocumentLifecycleState.InReview)
                    documents[documentId] = document with { State = DocumentLifecycleState.Work, UpdatedAt = now };
            }
            var tasks = package.ApprovalTasks.Select(task => task with { DecisionBy = null, Decision = null, Comment = null, DecidedAt = null }).ToArray();
            var withdrawn = package with { State = ReleasePackageState.Draft, ApprovalTasks = tasks };
            packages[releasePackageId] = withdrawn;
            return Task.FromResult(withdrawn);
        }
    }

    public Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
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

            if (!string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase) && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmRuleException("只能处理分配给自己的审批任务。 ");
            }

            var expectedState = task.Stage == ApprovalStage.ProcessReview ? ReleasePackageState.ProcessReview : ReleasePackageState.Approval;
            if (package.State != expectedState)
            {
                throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
            }

            var updatedTask = task with
            {
                Decision = decision,
                DecisionBy = actor,
                Comment = comment,
                DecidedAt = DateTimeOffset.UtcNow
            };
            var updatedTasks = package.ApprovalTasks.Select(item => item.Id == taskId ? updatedTask : item).ToArray();
            var nextState = decision == ApprovalDecision.Rejected
                ? ReleasePackageState.Rejected
                : task.Stage == ApprovalStage.ProcessReview
                    ? ReleasePackageState.Approval
                    : ReleasePackageState.Publishing;
            var updated = package with { ApprovalTasks = updatedTasks, State = nextState };
            packages[package.Id] = updated;
            if (nextState == ReleasePackageState.Rejected)
            {
                var now = timeProvider.GetUtcNow();
                foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
                {
                    if (documents.TryGetValue(documentId, out var document) && document.State == DocumentLifecycleState.InReview)
                        documents[documentId] = document with { State = DocumentLifecycleState.Work, UpdatedAt = now };
                }
            }
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
            return Task.CompletedTask;
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

    public Task<int> CountUsersAsync(CancellationToken cancellationToken) => Task.FromResult(users.Count);

    public Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken)
    {
        if (users.Values.Any(item => string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase)) || !users.TryAdd(user.Id, user))
        {
            throw new PdmConflictException("用户名已经存在。 ");
        }

        return Task.CompletedTask;
    }

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        audits.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(audits.Where(entry => HasRolePermission(role, PermissionCodes.AuditView) || string.Equals(entry.Actor, actor, StringComparison.OrdinalIgnoreCase)).OrderByDescending(entry => entry.OccurredAt).Take(Math.Clamp(take, 1, 500)).ToArray());

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
        var root = project.ParentProjectId is null
            ? project
            : projects.GetValueOrDefault(project.ParentProjectId.Value) ?? throw new PdmNotFoundException("主项目不存在。");
        var targets = projects.Values.Where(item => item.Id == root.Id || item.ParentProjectId == root.Id).OrderBy(item => item.ChildSequence).ToArray();
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
        if (role == UserRole.Administrator) return FolderAccess.All;
        ProjectFolder? current = folder;
        while (current is not null)
        {
            var rules = current.Permissions.Where(item =>
                item.PrincipalType == FolderPrincipalType.User && string.Equals(item.PrincipalKey, actor, StringComparison.OrdinalIgnoreCase)
                || item.PrincipalType == FolderPrincipalType.Role && string.Equals(item.PrincipalKey, role.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rules.Length == 0 && folderTemplate.TryGetValue(current.TemplateKey, out var template))
                rules = template.Permissions.Where(item =>
                    item.PrincipalType == FolderPrincipalType.User && string.Equals(item.PrincipalKey, actor, StringComparison.OrdinalIgnoreCase)
                    || item.PrincipalType == FolderPrincipalType.Role && string.Equals(item.PrincipalKey, role.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
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

    private bool CanViewProject(Project project, string actor, UserRole role)
    {
        if (role == UserRole.Administrator) return true;
        var root = project.ParentProjectId is null ? project : projects.GetValueOrDefault(project.ParentProjectId.Value);
        if (root is null) return false;
        if (string.Equals(root.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
            || root.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || string.Equals(root.DesignLead, actor, StringComparison.OrdinalIgnoreCase)
            || project.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase)) return true;
        if (project.ParentProjectId is null && projects.Values.Any(child => child.ParentProjectId == project.Id && child.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase))) return true;
        if (root.ExecutionUnitId is not null && organizationManagers.TryGetValue(root.ExecutionUnitId.Value, out var managers)
            && (string.Equals(managers.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || managers.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase))) return true;
        if (packages.Values.Any(package => package.ProjectId == project.Id && package.ApprovalTasks.Any(task => string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)))) return true;
        return HasRolePermission(role, PermissionCodes.ProjectExecutionAssign) && root.OrganizationId is not null && UserOrganizationIds(actor).Contains(root.OrganizationId.Value);
    }

    private Project ApplyCapabilities(Project project, string actor, UserRole role)
    {
        var documentCount = documents.Values.Count(item => item.ProjectId == project.Id);
        var businessStatus = BuildBusinessStatus(project.Id);
        if (role == UserRole.Administrator)
            return project with { CanAssignExecutionUnit = project.ParentProjectId is null, CanManageMainStaffing = project.ParentProjectId is null && project.ExecutionUnitId is not null, CanAssignDesigners = project.ParentProjectId is not null, CanReadContent = true, DocumentCount = documentCount, BusinessStatus = businessStatus };
        var canManage = project.ParentProjectId is null && project.ExecutionUnitId is not null
            && organizationManagers.TryGetValue(project.ExecutionUnitId.Value, out var managers)
            && (string.Equals(managers.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || managers.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase));
        var canReadContent = HasRolePermission(role, PermissionCodes.ProjectContentView) && HasProjectContentAssignment(project, actor);
        return project with
        {
            CanAssignExecutionUnit = HasRolePermission(role, PermissionCodes.ProjectExecutionAssign) && project.ParentProjectId is null && project.OrganizationId is not null && UserOrganizationIds(actor).Contains(project.OrganizationId.Value),
            CanManageMainStaffing = HasRolePermission(role, PermissionCodes.ProjectStaffingManage) && canManage,
            CanAssignDesigners = HasRolePermission(role, PermissionCodes.ProjectDesignerAssign) && project.ParentProjectId is not null && string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase),
            CanReadContent = canReadContent,
            DocumentCount = canReadContent ? documentCount : null,
            BusinessStatus = canReadContent ? businessStatus : null
        };
    }

    private string BuildBusinessStatus(Guid projectId)
    {
        var statuses = new List<string>();
        if (documents.Values.Any(item => item.ProjectId == projectId && !string.IsNullOrWhiteSpace(item.CheckedOutBy))) statuses.Add("已检出");
        var projectPackages = packages.Values.Where(item => item.ProjectId == projectId).ToArray();
        if (projectPackages.Any(item => item.State == ReleasePackageState.Draft)) statuses.Add("待提交");
        if (projectPackages.Any(item => item.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval)) statuses.Add("待审批");
        if (projectPackages.Any(item => item.State == ReleasePackageState.Rejected)) statuses.Add("审批退回");
        if (projectPackages.Any(item => item.State == ReleasePackageState.Publishing)) statuses.Add("发布中");
        if (projectPackages.Any(item => item.State == ReleasePackageState.PublishFailed)) statuses.Add("发布失败");
        return statuses.Count == 0 ? "正常" : string.Join("、", statuses);
    }

    private bool HasProjectContentAssignment(Project project, string actor)
    {
        var root = project.ParentProjectId is null ? project : projects.GetValueOrDefault(project.ParentProjectId.Value);
        return (root is not null && string.Equals(root.DesignLead, actor, StringComparison.OrdinalIgnoreCase))
            || project.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || packages.Values.Any(package => package.ProjectId == project.Id
                && package.ApprovalTasks.Any(task => string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)));
    }

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

    private bool HasRolePermission(UserRole role, string permissionCode) => role == UserRole.Administrator
        || rolePermissions.GetValueOrDefault(role, RolePermissionCatalog.Defaults[role]).Contains(permissionCode);

    private HashSet<Guid> UserOrganizationIds(string username)
    {
        if (!organizationMemberships.TryGetValue(username, out var membership)) return [];
        return membership.UnitIds.Select(unitId => organizationUnits.GetValueOrDefault(unitId)?.OrganizationId)
            .Where(organizationId => organizationId is not null).Select(organizationId => organizationId!.Value).ToHashSet();
    }

    private RolePermissionDirectory BuildRolePermissionDirectory() => new(
        RolePermissionCatalog.Permissions,
        RolePermissionCatalog.Roles.Select(definition => new RolePermissionSettings(
            definition.Role,
            definition.Name,
            definition.Description,
            definition.IsSystemAdministrator,
            rolePermissions.GetValueOrDefault(definition.Role, RolePermissionCatalog.Defaults[definition.Role]).Order().ToArray())).ToArray());
}
