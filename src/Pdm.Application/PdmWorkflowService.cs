using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PdmWorkflowService(IPdmRepository repository, IFileStorage fileStorage, IReleasePackagePublisher publisher, TimeProvider timeProvider)
{
    public async Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectCreate, cancellationToken);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);

        var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
        if (!options.Organizations.Any(item => item.Id == command.OrganizationId && item.IsActive))
            throw new PdmRuleException("所选组织不存在或已停用。");
        if (!options.ProjectTypes.Any(item => string.Equals(item.Code, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase) && item.IsActive))
            throw new PdmRuleException("所选项目类型不存在或已停用。");
        if (!options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
            throw new PdmRuleException("所选设备类型不存在或已停用。");
        var customer = await repository.FindCustomerAsync(command.CustomerId, cancellationToken);
        if (customer is null || !customer.IsActive)
            throw new PdmRuleException("所选客户不存在或已停用。");
        if (!string.Equals(customer.SourceSystem, "crm", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("所选客户不是从CRM同步的数据，请重新选择客户。");

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");

        var project = await repository.CreateNumberedProjectAsync(command with
        {
            ProjectTypeCode = command.ProjectTypeCode.Trim().ToUpperInvariant(),
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            Owner = actor,
            VaultLocation = vaultRoot,
            ReleaseLocation = releaseRoot
        }, cancellationToken);
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectChildCreate, cancellationToken);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        if (!await repository.HasProjectReadAccessAsync(command.ParentProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该主项目的访问权限。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var project = await repository.CreateSubprojectAsync(command with
        {
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            VaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot),
            ReleaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot)
        }, cancellationToken);
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        await AuditAsync(actor, "project.child.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    public async Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.CustomerSettingsManage, cancellationToken);
        code = code?.Trim().ToUpperInvariant() ?? string.Empty;
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            throw new PdmRuleException("客户编码和客户名称不能为空。");
        if (code.Length > 30 || name.Length > 200)
            throw new PdmRuleException("客户编码或名称超过允许长度。");
        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PdmRuleException("客户编码只能包含字母、数字、短横线和下划线。");
        var customer = await repository.SaveCustomerAsync(customerId, code, name, isActive, cancellationToken);
        await AuditAsync(actor, customerId is null ? "customer.create" : "customer.update", nameof(PdmCustomer), customer.Id.ToString(), $"{customer.Code} · {customer.Name} · {(customer.IsActive ? "启用" : "停用")}", cancellationToken);
        return customer;
    }

    public async Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.StorageSettingsManage, cancellationToken);
        name = name?.Trim() ?? string.Empty;
        if (code is < 0 or > 99) throw new PdmRuleException("设备类型编码必须为0到99。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new PdmRuleException("设备类型名称不能为空且不能超过100个字符。");
        var equipmentType = await repository.SaveEquipmentTypeAsync(code, name, isActive, cancellationToken);
        await AuditAsync(actor, "equipment-type.update", nameof(EquipmentTypeDefinition), code.ToString("D2"), $"{code:D2} · {name} · {(isActive ? "启用" : "停用")}", cancellationToken);
        return equipmentType;
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings input, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(input.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(input.ReleaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        var settingsInput = input with { VaultRoot = vaultRoot, ReleaseRoot = releaseRoot };
        ValidateCheckoutSettings(settingsInput);
        var settings = await repository.UpdateSystemSettingsAsync(settingsInput, cancellationToken);
        await AuditAsync(actor, "system.storage.update", nameof(PdmSystemSettings), "storage", $"图档根目录：{settings.VaultRoot}；发包根目录：{settings.ReleaseRoot}", cancellationToken);
        await AuditAsync(actor, "system.checkout-policy.update", nameof(PdmSystemSettings), "checkout-policy", $"心跳{settings.CheckoutHeartbeatSeconds}秒；离线宽限{settings.CheckoutOfflineGraceMinutes}分钟；超时{settings.CheckoutOverdueHours}小时；强制释放{settings.CheckoutForceReleaseHours}小时", cancellationToken);
        return settings;
    }

    public async Task<RolePermissionDirectory> UpdateRolePermissionsAsync(UserRole targetRole, IReadOnlyList<string> permissionCodes, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var unknown = permissionCodes.Where(code => !RolePermissionCatalog.IsKnown(code)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new PdmRuleException($"包含未登记的权限代码：{string.Join('、', unknown)}。");
        var directory = await repository.SetRolePermissionsAsync(targetRole, permissionCodes, cancellationToken);
        var saved = directory.Roles.Single(item => item.Role == targetRole);
        await AuditAsync(actor, "role.permissions.update", nameof(UserRole), targetRole.ToString(), $"{saved.Name} · {saved.Permissions.Count}项权限", cancellationToken);
        return directory;
    }

    public async Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        var name = command.Name?.Trim() ?? string.Empty;
        var projectCode = command.ProjectCompanyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var modelCode = command.ModelCompanyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (name.Length is < 1 or > 200) throw new PdmRuleException("公司名称不能为空且不能超过200个字符。");
        if (projectCode.Length != 1 || !char.IsLetterOrDigit(projectCode[0])) throw new PdmRuleException("项目号公司代码必须是1位字母或数字。");
        if (modelCode.Length is < 1 or > 8 || modelCode.Any(character => !char.IsLetterOrDigit(character)))
            throw new PdmRuleException("设备型号公司代码必须是1到8位字母或数字。");
        var saved = await repository.SaveProjectOrganizationAsync(command with { Name = name, ProjectCompanyCode = projectCode, ModelCompanyCode = modelCode }, cancellationToken);
        await AuditAsync(actor, command.Id is null ? "organization.create" : "organization.update", nameof(ProjectOrganization), saved.Id.ToString(), $"{saved.Name} · {saved.ProjectCompanyCode}/{saved.ModelCompanyCode} · {(saved.IsActive ? "启用" : "停用")}", cancellationToken);
        return saved;
    }

    public async Task<OrganizationUnit> SaveOrganizationUnitAsync(SaveOrganizationUnitCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (!directory.Organizations.Any(item => item.Id == command.OrganizationId)) throw new PdmRuleException("所属公司不存在。");
        var code = command.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = command.Name?.Trim() ?? string.Empty;
        if (code.Length is < 1 or > 40 || code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PdmRuleException("组织编码只能包含字母、数字、短横线和下划线，且不能超过40位。");
        if (name.Length is < 1 or > 160) throw new PdmRuleException("组织名称不能为空且不能超过160个字符。");
        if (command.ParentUnitId is not null)
        {
            var parent = directory.Units.SingleOrDefault(item => item.Id == command.ParentUnitId);
            if (parent is null || parent.OrganizationId != command.OrganizationId) throw new PdmRuleException("上级组织必须属于同一公司。");
            if (command.Kind == OrganizationUnitKind.BusinessDivision) throw new PdmRuleException("事业部必须直接隶属于公司。");
            if (command.Id is not null && IsUnitWithin(directory.Units, parent.Id, command.Id.Value)) throw new PdmRuleException("上级组织不能选择当前组织自身或其下级。");
        }
        else if (command.Kind != OrganizationUnitKind.BusinessDivision)
        {
            throw new PdmRuleException("部门或团队必须选择上级组织。");
        }
        var saved = await repository.SaveOrganizationUnitAsync(command with { Code = code, Name = name }, cancellationToken);
        await AuditAsync(actor, command.Id is null ? "organization-unit.create" : "organization-unit.update", nameof(OrganizationUnit), saved.Id.ToString(), $"{saved.Code} · {saved.Name} · {saved.Kind}", cancellationToken);
        return saved;
    }

    public async Task<OrganizationDirectory> SetOrganizationMembershipsAsync(string username, IReadOnlyList<Guid> unitIds, Guid primaryUnitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        username = username?.Trim() ?? string.Empty;
        var distinctUnitIds = unitIds.Distinct().ToArray();
        if (distinctUnitIds.Length == 0 || !distinctUnitIds.Contains(primaryUnitId)) throw new PdmRuleException("人员至少需要一个所属组织，且主组织必须包含在所选组织中。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (!directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("账号不存在或已停用。");
        var units = directory.Units.Where(unit => distinctUnitIds.Contains(unit.Id) && unit.IsActive).ToArray();
        if (units.Length != distinctUnitIds.Length || units.Select(unit => unit.OrganizationId).Distinct().Count() != 1)
            throw new PdmRuleException("人员所属组织必须启用并且属于同一公司。");
        var saved = await repository.SetOrganizationMembershipsAsync(username, distinctUnitIds, primaryUnitId, cancellationToken);
        await AuditAsync(actor, "organization.memberships.update", nameof(OrganizationMembership), username, $"主组织：{primaryUnitId}；共{distinctUnitIds.Length}个组织", cancellationToken);
        return saved;
    }

    public async Task<OrganizationDirectory> SetOrganizationUnitManagersAsync(Guid unitId, string primaryManager, IReadOnlyList<string> collaborativeManagers, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        primaryManager = primaryManager?.Trim() ?? string.Empty;
        var collaborators = NormalizeUsers(collaborativeManagers).Where(username => !string.Equals(username, primaryManager, StringComparison.OrdinalIgnoreCase)).ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.SingleOrDefault(item => item.Id == unitId && item.IsActive && item.Kind == OrganizationUnitKind.BusinessDivision)
            ?? throw new PdmRuleException("只能为启用的事业部配置负责人。");
        var candidates = new[] { primaryManager }.Concat(collaborators).ToArray();
        if (string.IsNullOrWhiteSpace(primaryManager) || candidates.Any(username => !IsActiveMemberOfDivision(directory, username, unit.Id)))
            throw new PdmRuleException("事业部负责人必须是该事业部内的启用账号。");
        var saved = await repository.SetOrganizationUnitManagersAsync(unitId, primaryManager, collaborators, cancellationToken);
        await AuditAsync(actor, "organization.managers.update", nameof(OrganizationUnitManagers), unitId.ToString(), $"主负责人：{primaryManager}；协同：{string.Join('、', collaborators)}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectExecutionAssign, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.ParentProjectId is not null) throw new PdmRuleException("执行事业部只能在主项目上配置。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.SingleOrDefault(item => item.Id == executionUnitId && item.IsActive && item.Kind == OrganizationUnitKind.BusinessDivision)
            ?? throw new PdmRuleException("执行事业部不存在或已停用。");
        if (project.OrganizationId != unit.OrganizationId) throw new PdmRuleException("执行事业部必须属于项目公司。");
        if (role != UserRole.Administrator)
        {
            if (!directory.Memberships.Any(item => string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase)
                    && directory.Units.Any(memberUnit => memberUnit.Id == item.UnitId && memberUnit.OrganizationId == unit.OrganizationId)))
                throw new UnauthorizedAccessException("计划管理只能分配本人所属公司的项目。");
        }
        var saved = await repository.SetProjectExecutionUnitAsync(projectId, executionUnitId, actor, cancellationToken);
        await AuditAsync(actor, "project.execution-unit.update", nameof(Project), project.Id.ToString(), $"{project.Code} · {unit.Name}；原项目分工已清空", cancellationToken);
        return saved;
    }

    public async Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectStaffingManage, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.ParentProjectId is not null || project.ExecutionUnitId is null) throw new PdmRuleException("请先为主项目分配执行事业部。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (role != UserRole.Administrator && !directory.Managers.Any(item => item.UnitId == project.ExecutionUnitId
                && (string.Equals(item.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || item.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase))))
            throw new UnauthorizedAccessException("只有执行事业部负责人可以配置项目经理和设计负责人。");
        var primary = command.PrimaryProjectManager?.Trim() ?? string.Empty;
        var designLead = command.DesignLead?.Trim() ?? string.Empty;
        var collaborators = NormalizeUsers(command.CollaborativeProjectManagers).Where(username => !string.Equals(username, primary, StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = new[] { primary, designLead }.Concat(collaborators).ToArray();
        if (string.IsNullOrWhiteSpace(primary) || string.IsNullOrWhiteSpace(designLead)
            || candidates.Any(username => !IsActiveMemberOfDivision(directory, username, project.ExecutionUnitId.Value)))
            throw new PdmRuleException("项目经理、协同项目经理和设计负责人必须是执行事业部内的启用账号。");
        var saved = await repository.SetMainProjectStaffingAsync(projectId, new(primary, collaborators, designLead), actor, cancellationToken);
        await AuditAsync(actor, "project.staffing.update", nameof(Project), project.Id.ToString(), $"项目经理：{primary}；设计负责人：{designLead}；协同：{string.Join('、', collaborators)}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectDesignerAssign, cancellationToken);
        var child = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("子项目不存在。");
        if (child.ParentProjectId is null) throw new PdmRuleException("设计人员只能配置到子项目。");
        var root = await repository.FindProjectAsync(child.ParentProjectId.Value, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。");
        if (role != UserRole.Administrator && !string.Equals(root.DesignLead, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只有主项目设计负责人可以分配子项目设计人员。");
        var normalized = NormalizeUsers(designers);
        if (normalized.Length == 0) throw new PdmRuleException("请至少选择一名子项目设计人员。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (root.OrganizationId is null || normalized.Any(username => !IsActiveMemberOfOrganization(directory, username, root.OrganizationId.Value)))
            throw new PdmRuleException("设计人员必须是项目公司组织内的启用账号；当前阶段不允许跨公司分配。");
        var saved = await repository.SetChildProjectDesignersAsync(projectId, normalized, actor, cancellationToken);
        await AuditAsync(actor, "project.designers.update", nameof(Project), child.Id.ToString(), $"{child.Code} · {string.Join('、', normalized)}", cancellationToken);
        return saved;
    }

    public async Task<Project> DeleteProjectAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectDelete, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await repository.DeleteProjectAsync(projectId, cancellationToken);
        await AuditAsync(actor, "project.delete", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectCreate, cancellationToken);
        var code = command.Code?.Trim() ?? string.Empty;
        var name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(command.VaultLocation)
            || string.IsNullOrWhiteSpace(command.ReleaseLocation))
        {
            throw new PdmRuleException("项目编码、项目名称和存储位置不能为空。");
        }

        if (code.Length > 80 || name.Length > 200)
        {
            throw new PdmRuleException("项目编码或名称超过允许长度。");
        }

        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new PdmRuleException("项目编码只能包含字母、数字、短横线、下划线和点。");
        }

        var vaultLocation = StorageLocationPolicy.Normalize(command.VaultLocation);
        var releaseLocation = StorageLocationPolicy.Normalize(command.ReleaseLocation);
        if (string.Equals(vaultLocation, releaseLocation, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        }

        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand(code, name, actor, vaultLocation, releaseLocation),
            actor,
            cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<IReadOnlyList<DocumentRegistrationMatch>> PreflightDocumentRegistrationAsync(
        Guid projectId,
        IReadOnlyList<DocumentRegistrationCandidate> candidates,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有目标项目的图档权限。");
        if (candidates is null || candidates.Count == 0) return Array.Empty<DocumentRegistrationMatch>();
        if (candidates.Count > 2000) throw new PdmRuleException("单次最多检查2000个待入库图档。");

        var normalized = candidates.Select(candidate => candidate with
        {
            CandidateKey = candidate.CandidateKey?.Trim() ?? string.Empty,
            FileName = Path.GetFileName(candidate.FileName?.Trim() ?? string.Empty),
            SourceSha256 = NormalizeSha256(candidate.SourceSha256)
        }).ToArray();
        if (normalized.Any(candidate => candidate.CandidateKey.Length == 0 || candidate.FileName.Length == 0))
            throw new PdmRuleException("待入库图档标识和文件名不能为空。");
        if (normalized.Select(candidate => candidate.CandidateKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new PdmRuleException("待入库图档标识不能重复。");

        var projects = await repository.ListProjectsForUserAsync(actor, role, cancellationToken);
        var visibleProjects = projects.ToDictionary(project => project.Id);
        if (!visibleProjects.ContainsKey(projectId))
            throw new UnauthorizedAccessException("当前用户无权查看目标项目。");
        var allFingerprints = await repository.ListDocumentContentFingerprintsAsync(visibleProjects.Keys.ToArray(), cancellationToken);
        var candidateFileNames = normalized.Select(candidate => candidate.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateFingerprints = normalized.Select(candidate => candidate.SourceSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantFingerprints = allFingerprints.Where(fingerprint =>
            (fingerprint.Document.ProjectId == projectId && candidateFileNames.Contains(fingerprint.Document.FileName))
            || candidateFingerprints.Contains(fingerprint.SourceSha256)).ToArray();
        var visibleFingerprints = new List<DocumentContentFingerprint>(relevantFingerprints.Length);
        foreach (var fingerprint in relevantFingerprints)
        {
            if (await repository.HasDocumentReadAccessAsync(fingerprint.Document.Id, actor, role, cancellationToken))
                visibleFingerprints.Add(fingerprint);
        }
        var fingerprints = visibleFingerprints;
        var results = new List<DocumentRegistrationMatch>(normalized.Length);
        foreach (var candidate in normalized)
        {
            var sameName = fingerprints.FirstOrDefault(item =>
                item.Document.ProjectId == projectId
                && string.Equals(item.Document.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null)
            {
                results.Add(ToRegistrationMatch(
                    candidate.CandidateKey,
                    string.Equals(sameName.SourceSha256, candidate.SourceSha256, StringComparison.OrdinalIgnoreCase)
                        ? DocumentRegistrationMatchKind.SameNameSameContent
                        : DocumentRegistrationMatchKind.SameNameDifferentContent,
                    sameName,
                    visibleProjects));
                continue;
            }

            var sameContent = fingerprints.FirstOrDefault(item =>
                item.Document.ProjectId == projectId
                && string.Equals(item.SourceSha256, candidate.SourceSha256, StringComparison.OrdinalIgnoreCase));
            if (sameContent is not null)
            {
                results.Add(ToRegistrationMatch(
                    candidate.CandidateKey,
                    DocumentRegistrationMatchKind.SameContentDifferentName,
                    sameContent,
                    visibleProjects));
                continue;
            }

            var otherProjectContent = fingerprints.FirstOrDefault(item =>
                item.Document.ProjectId != projectId
                && string.Equals(item.SourceSha256, candidate.SourceSha256, StringComparison.OrdinalIgnoreCase));
            results.Add(otherProjectContent is null
                ? new DocumentRegistrationMatch(candidate.CandidateKey, DocumentRegistrationMatchKind.New, null, null, null, null, null, null, null)
                : ToRegistrationMatch(
                    candidate.CandidateKey,
                    DocumentRegistrationMatchKind.SameContentOtherProject,
                    otherProjectContent,
                    visibleProjects));
        }

        return results;
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        if (string.IsNullOrWhiteSpace(command.DrawingNumber)
            || string.IsNullOrWhiteSpace(command.Name)
            || string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new PdmRuleException("图号、名称和文件名不能为空。");
        }

        if (command.Kind is not (DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing))
        {
            throw new PdmRuleException("只有SolidWorks装配体、零件和工程图可以登记。");
        }

        if (string.IsNullOrWhiteSpace(command.SourceSha256))
            throw new PdmRuleException("登记图档前必须提供源文件SHA-256指纹并完成重复预检。");
        var sourceSha256 = NormalizeSha256(command.SourceSha256);
        var duplicateReason = command.DuplicateReason?.Trim();
        if (command.AllowDuplicateContent && string.IsNullOrWhiteSpace(duplicateReason))
            throw new PdmRuleException("确认独立登记完全相同的图档时，必须填写原因。");
        if (duplicateReason?.Length > 500)
            throw new PdmRuleException("独立登记原因不能超过500个字符。");

        if (command.RelatedModelDocumentId.HasValue)
        {
            if (command.Kind != DocumentKind.Drawing)
                throw new PdmRuleException("只有工程图可以关联三维模型。");
            var relatedModel = await repository.FindDocumentAsync(command.RelatedModelDocumentId.Value, cancellationToken)
                ?? throw new PdmNotFoundException("关联的三维模型不存在。");
            if (relatedModel.ProjectId != command.ProjectId
                || relatedModel.Kind is not (DocumentKind.Assembly or DocumentKind.Part))
                throw new PdmRuleException("工程图只能关联同一项目中的装配体或零件。");
        }

        var project = await repository.FindProjectAsync(command.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!project.IsActive)
        {
            throw new PdmConflictException("项目已停用，不能登记图档。");
        }
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        var folders = await repository.ListProjectFoldersAsync(project.Id, actor, role, cancellationToken);
        var targetFolder = command.FolderId is null
            ? folders.FirstOrDefault(item => item.TargetProjectId == project.Id && item.TemplateKey == "mechanical.project")
            : folders.FirstOrDefault(item => item.Id == command.FolderId.Value);
        if (targetFolder is null || targetFolder.TargetProjectId != project.Id || targetFolder.Purpose != ProjectFolderPurpose.ProjectContainer)
            throw new PdmRuleException("图档只能登记到机械图纸或电气图纸下当前项目对应的目录。");
        if ((targetFolder.EffectiveAccess & FolderAccess.Upload) == 0)
            throw new UnauthorizedAccessException("当前用户没有向该目录登记图档的权限。");
        var normalized = command with
        {
            DrawingNumber = command.DrawingNumber.Trim(),
            Name = command.Name.Trim(),
            FileName = Path.GetFileName(command.FileName.Trim()),
            FolderId = targetFolder.Id,
            SourceSha256 = sourceSha256,
            DuplicateReason = duplicateReason
        };
        var document = await repository.RegisterDocumentAsync(normalized, actor, cancellationToken);
        await AuditAsync(actor, "document.register", nameof(PdmDocument), document.Id.ToString(), document.FileName, cancellationToken);
        if (normalized.AllowDuplicateContent)
            await AuditAsync(actor, "document.register.duplicate-content", nameof(PdmDocument), document.Id.ToString(), duplicateReason!, cancellationToken);
        return document;
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken) =>
        CheckoutAsync(documentId, actor, role, Guid.NewGuid(), "legacy-client", cancellationToken);

    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, Guid sessionId, string machineName, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        if (sessionId == Guid.Empty) throw new PdmRuleException("编辑会话编号不能为空。");
        if (string.IsNullOrWhiteSpace(machineName)) throw new PdmRuleException("客户端电脑名称不能为空。");
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (document.State == DocumentLifecycleState.InReview)
        {
            throw new PdmConflictException("图档正在审批，不能获取编辑权限。 ");
        }
        if (document.State == DocumentLifecycleState.Obsolete)
        {
            throw new PdmConflictException("图档已作废，不能获取编辑权限。 ");
        }

        var normalizedMachineName = machineName.Trim();
        var now = timeProvider.GetUtcNow();
        var sameUserAndMachine = document.CheckedOutBy is not null
            && string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(document.CheckoutMachine)
                || string.Equals(document.CheckoutMachine, normalizedMachineName, StringComparison.OrdinalIgnoreCase));
        var reclaimingLocalSession = sameUserAndMachine && document.CheckoutSessionId != sessionId;
        if (document.CheckedOutBy is not null && !sameUserAndMachine)
        {
            throw new PdmConflictException($"图档正在由{document.CheckedOutBy}在{document.CheckoutMachine ?? "其他会话"}编辑。 ");
        }

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var updated = await repository.CheckoutAsync(documentId, actor, sessionId, normalizedMachineName, now.AddMinutes(settings.CheckoutLeaseMinutes), cancellationToken);
        var action = reclaimingLocalSession ? "document.checkout.reclaim-local-session" : "document.checkout";
        var detail = reclaimingLocalSession
            ? $"{updated.Revision.Display}；旧会话{document.CheckoutSessionId}；新会话{sessionId}；电脑{normalizedMachineName}"
            : $"{updated.Revision.Display}；会话{sessionId}；电脑{normalizedMachineName}";
        await AuditAsync(actor, action, nameof(PdmDocument), documentId.ToString(), detail, cancellationToken);
        return updated;
    }

    public async Task<EditSessionHeartbeat> HeartbeatEditSessionAsync(Guid sessionId, string actor, UserRole role, string machineName, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        if (sessionId == Guid.Empty) throw new PdmRuleException("编辑会话编号不能为空。");
        if (string.IsNullOrWhiteSpace(machineName)) throw new PdmRuleException("客户端电脑名称不能为空。");
        var ids = documentIds.Where(id => id != Guid.Empty).Distinct().Take(1000).ToArray();
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var leaseExpiresAt = now.AddMinutes(settings.CheckoutLeaseMinutes);
        var active = await repository.HeartbeatCheckoutSessionAsync(sessionId, actor, machineName.Trim(), ids, leaseExpiresAt, cancellationToken);
        var activeSet = active.ToHashSet();
        return new(sessionId, now, leaseExpiresAt, active, ids.Where(id => !activeSet.Contains(id)).ToArray(), settings);
    }

    public async Task<DocumentCheckInResult> CheckInAsync(
        Guid documentId,
        string actor,
        UserRole role,
        StoredFile file,
        string changeNote,
        IReadOnlyDictionary<string, string?> properties,
        CadReferenceSnapshot snapshot,
        bool isProjectRoot,
        bool forceVersion,
        CancellationToken cancellationToken,
        string? drawingNumber = null,
        string? name = null,
        string? fileName = null)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。 ");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CheckInAsync(documentId, actor, role, document.CheckoutSessionId.Value, file, changeNote, properties, snapshot, isProjectRoot, forceVersion, cancellationToken, drawingNumber, name);
    }

    public async Task<DocumentCheckInResult> CheckInAsync(
        Guid documentId,
        string actor,
        UserRole role,
        Guid checkoutSessionId,
        StoredFile file,
        string changeNote,
        IReadOnlyDictionary<string, string?> properties,
        CadReferenceSnapshot snapshot,
        bool isProjectRoot,
        bool forceVersion,
        CancellationToken cancellationToken,
        string? drawingNumber = null,
        string? name = null,
        string? fileName = null)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (snapshot.ProjectId != document.ProjectId || snapshot.RootDocumentId != documentId)
        {
            throw new PdmRuleException("引用树快照必须属于当前项目和当前图档。");
        }

        if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase) || document.CheckoutSessionId != checkoutSessionId)
        {
            throw new PdmConflictException("编辑会话已经失效，不能提交存档。请另存本地修改或重新获取权限。 ");
        }

        var normalizedChangeNote = changeNote?.Trim() ?? string.Empty;
        var normalizedDrawingNumber = drawingNumber?.Trim();
        var normalizedName = name?.Trim();
        var normalizedFileName = fileName?.Trim();
        if (drawingNumber is not null && string.IsNullOrWhiteSpace(normalizedDrawingNumber))
        {
            throw new PdmRuleException("图号不能为空。");
        }
        if (name is not null && string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new PdmRuleException("图档名称不能为空。");
        }
        if (normalizedDrawingNumber?.Length > 160 || normalizedName?.Length > 300)
        {
            throw new PdmRuleException("图号或图档名称超过允许长度。");
        }
        if (fileName is not null
            && (string.IsNullOrWhiteSpace(normalizedFileName)
                || normalizedFileName.Length > 260
                || !string.Equals(Path.GetFileName(normalizedFileName), normalizedFileName, StringComparison.Ordinal)))
        {
            throw new PdmRuleException("文件名无效。");
        }

        if (snapshot.Root.HasBlockingIssue)
        {
            throw new PdmRuleException("结构树存在缺失引用，不能提交存档。 ");
        }

        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await fileStorage.VerifyStoredFileAsync(project, file, cancellationToken);
        var mechanical = await repository.GetBomAsync(document.ProjectId, BomKind.Mechanical, cancellationToken);
        var electrical = await repository.GetBomAsync(document.ProjectId, BomKind.Electrical, cancellationToken);
        var result = await repository.CheckInVersionAsync(
            documentId,
            actor,
            checkoutSessionId,
            new DocumentVersionCommit(
                file,
                normalizedChangeNote,
                properties,
                snapshot,
                mechanical,
                electrical,
                IsProjectRoot: isProjectRoot,
                ForceVersion: forceVersion,
                DrawingNumber: normalizedDrawingNumber,
                Name: normalizedName,
                FileName: normalizedFileName),
            cancellationToken);
        if (result.VersionCreated && result.Version is not null)
        {
            await AuditAsync(actor, "document.checkin", nameof(DocumentVersion), result.Version.Id.ToString(), result.Version.Revision.Display, cancellationToken);
        }
        else
        {
            await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), result.Document.Revision.Display, cancellationToken);
        }
        if (!string.Equals(document.DrawingNumber, result.Document.DrawingNumber, StringComparison.Ordinal)
            || !string.Equals(document.Name, result.Document.Name, StringComparison.Ordinal))
        {
            await AuditAsync(
                actor,
                "document.identity.update",
                nameof(PdmDocument),
                documentId.ToString(),
                string.Concat(document.DrawingNumber, " / ", document.Name, " -> ", result.Document.DrawingNumber, " / ", result.Document.Name),
                cancellationToken);
        }
        return result;
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, UserRole role, string sha256, CancellationToken cancellationToken)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CompleteEditWithoutChangesAsync(documentId, actor, role, document.CheckoutSessionId.Value, sha256, cancellationToken);
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, UserRole role, Guid sessionId, string sha256, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new PdmRuleException("文件指纹不能为空。");
        }

        var document = await repository.CompleteEditWithoutChangesAsync(documentId, actor, sessionId, sha256.Trim(), cancellationToken);
        await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await DiscardCheckoutAsync(documentId, actor, role, document.CheckoutSessionId.Value, cancellationToken);
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, UserRole role, Guid sessionId, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var document = await repository.DiscardCheckoutAsync(documentId, actor, sessionId, cancellationToken);
        await AuditAsync(actor, "document.checkout.discard", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<IReadOnlyList<EditLockSummary>> ListEditLocksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        var canRequestPermission = await repository.HasRolePermissionAsync(role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
        var canForcePermission = await repository.HasRolePermissionAsync(role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var result = new List<EditLockSummary>();
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(document.CheckedOutBy)) continue;
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) continue;
            var owned = string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase);
            var canEditDocument = !owned && canRequestPermission
                && await repository.HasDocumentAccessAsync(document.Id, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
            var lockManager = canForcePermission && IsProjectLockManager(project, actor, role);
            if (!owned && !canEditDocument && !lockManager) continue;

            var checkedOutAt = document.CheckedOutAt ?? document.UpdatedAt;
            var heartbeatAt = document.CheckoutLastHeartbeatAt ?? checkedOutAt;
            var leaseExpiresAt = document.CheckoutLeaseExpiresAt ?? heartbeatAt.AddMinutes(settings.CheckoutLeaseMinutes);
            var connectionState = now <= leaseExpiresAt
                ? EditLockConnectionState.Active
                : now <= heartbeatAt.AddMinutes(settings.CheckoutOfflineGraceMinutes)
                    ? EditLockConnectionState.OfflineGrace
                    : EditLockConnectionState.Offline;
            var attention = AttentionLevel(now - checkedOutAt, settings);
            result.Add(new(
                document.Id, document.ProjectId, project.Code, project.Name, document.DrawingNumber, document.Name, document.FileName,
                document.CheckedOutBy, checkedOutAt, document.CheckoutMachine, heartbeatAt, leaseExpiresAt, connectionState, attention,
                document.CheckoutReleaseRequestedBy, document.CheckoutReleaseRequestedAt, document.CheckoutReleaseRequestReason,
                owned, canEditDocument, lockManager && attention == EditLockAttentionLevel.Reclaimable));
        }
        return result.OrderByDescending(item => item.AttentionLevel).ThenBy(item => item.CheckedOutAt).ToArray();
    }

    public async Task<EditLockSummary> RequestEditLockReleaseAsync(Guid documentId, string actor, UserRole role, string reason, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var current = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (string.IsNullOrWhiteSpace(current.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可申请释放。");
        if (string.Equals(current.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("当前编辑权限属于本人，请在SolidWorks中提交存档或放弃编辑。");
        reason = RequiredReason(reason);
        await repository.RequestCheckoutReleaseAsync(documentId, actor, reason, cancellationToken);
        await AuditAsync(actor, "document.checkout.release-request", nameof(PdmDocument), documentId.ToString(), $"当前编辑人：{current.CheckedOutBy}；原因：{reason}", cancellationToken);
        return (await ListEditLocksAsync(actor, role, cancellationToken)).Single(item => item.DocumentId == documentId);
    }

    public async Task<PdmDocument> ForceReleaseEditLockAsync(Guid documentId, string actor, UserRole role, string reason, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
        var current = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (string.IsNullOrWhiteSpace(current.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可释放。");
        var project = await repository.FindProjectAsync(current.ProjectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (!IsProjectLockManager(project, actor, role)) throw new UnauthorizedAccessException("只有项目经理、设计负责人或系统管理员可以强制释放本项目权限。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var checkedOutAt = current.CheckedOutAt ?? current.UpdatedAt;
        if (timeProvider.GetUtcNow() < checkedOutAt.AddHours(settings.CheckoutForceReleaseHours))
            throw new PdmRuleException($"编辑权限获取未满{settings.CheckoutForceReleaseHours}小时，只能先催办并申请释放。");
        reason = RequiredReason(reason);
        var priorOwner = current.CheckedOutBy;
        var priorSession = current.CheckoutSessionId;
        var updated = await repository.ForceReleaseCheckoutAsync(documentId, actor, reason, cancellationToken);
        await AuditAsync(actor, "document.checkout.force-release", nameof(PdmDocument), documentId.ToString(), $"原编辑人：{priorOwner}；原会话：{priorSession}；电脑：{current.CheckoutMachine ?? "未知"}；原因：{reason}", cancellationToken);
        return updated;
    }

    public async Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(
        Guid documentId,
        Guid sourceVersionId,
        string actor,
        UserRole role,
        string changeNote,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var source = await repository.FindDocumentVersionAsync(documentId, sourceVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("历史版本不存在。");
        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var restoredPath = Path.Combine(".versions", document.Id.ToString("N"), Guid.NewGuid().ToString("N"), document.FileName);
        var restoredFile = await fileStorage.CopyVersionAsync(
            project,
            new StoredFile(source.StorageRelativePath, source.FileLength, source.Sha256, source.CreatedAt),
            restoredPath,
            cancellationToken);
        var result = await repository.RestoreVersionAsync(documentId, sourceVersionId, actor, restoredFile, changeNote, cancellationToken);
        await AuditAsync(actor, "document.version.restore", nameof(DocumentVersion), sourceVersionId.ToString(), $"生成{result.Version.Revision.Display}", cancellationToken);
        return result;
    }

    public async Task<DocumentVersionComparison> CompareVersionsAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectContentView, cancellationToken);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var left = await repository.FindDocumentVersionAsync(documentId, leftVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("左侧历史版本不存在。");
        var right = await repository.FindDocumentVersionAsync(documentId, rightVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("右侧历史版本不存在。");
        var comparison = DocumentVersionDiff.Compare(left, right);
        await AuditAsync(actor, "document.version.compare", nameof(PdmDocument), documentId.ToString(), $"{left.Revision.Display} -> {right.Revision.Display}", cancellationToken);
        return comparison;
    }

    public async Task<DocumentVersion> PublishVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ApprovalDecide, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Publish, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var version = await repository.PublishDocumentVersionAsync(documentId, sourceVersionId, releasePackageId, approvalTaskId, actor, cancellationToken);
        await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        return version;
    }

    public async Task AuditVersionReadAsync(Guid documentId, Guid versionId, string actor, UserRole role, string action, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ProjectContentView, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role,
            action == "document.version.download" ? FolderAccess.View | FolderAccess.Download : FolderAccess.View, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        if (versionId != Guid.Empty)
        {
            _ = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken)
                ?? throw new PdmNotFoundException("历史版本不存在。");
        }
        await AuditAsync(actor, action, nameof(DocumentVersion), versionId.ToString(), documentId.ToString(), cancellationToken);
    }

    public async Task<ControlledOpenManifest> CreateControlledOpenManifestAsync(
        Guid documentId,
        Guid? versionId,
        bool releasedOnly,
        bool forEdit,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireDocumentAccessAsync(documentId, actor, role, forEdit ? FolderAccess.View | FolderAccess.Edit : FolderAccess.View, cancellationToken);
        if (forEdit)
        {
            await RequirePermissionAsync(role, PermissionCodes.DocumentEdit, cancellationToken);
            if (releasedOnly || versionId.HasValue)
            {
                throw new PdmRuleException("编辑模式只能获取当前最新受控版本；历史版和正式版只能只读打开。");
            }
        }

        var rootDocument = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        var project = await repository.FindProjectAsync(rootDocument.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var rootVersions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken);
        var rootVersion = versionId.HasValue
            ? rootVersions.SingleOrDefault(item => item.Id == versionId.Value)
            : releasedOnly
                ? rootVersions.FirstOrDefault(item => item.Status == DocumentVersionStatus.Released)
                : rootVersions.FirstOrDefault(item => item.Revision.Display.Equals(rootDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                    ?? rootVersions.FirstOrDefault();
        if (rootVersion is null)
        {
            throw new PdmNotFoundException(releasedOnly ? "该图档尚无正式发布版本。" : "该图档尚无可打开的受控版本。");
        }

        if (forEdit
            && rootDocument.CheckedOutBy is not null
            && !rootDocument.CheckedOutBy.Equals(actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException($"图档正在由{rootDocument.CheckedOutBy}编辑。");
        }

        var files = new List<ControlledOpenFile>();
        var filesByDocument = new Dictionary<Guid, ControlledOpenFile>();
        var fileNames = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var allowCurrentVersionFallback = !versionId.HasValue && !releasedOnly;
        var currentProjectDocuments = allowCurrentVersionFallback
            ? await repository.ListDocumentsAsync(project.Id, cancellationToken)
            : Array.Empty<PdmDocument>();
        var nodes = FlattenOpenNodes(rootVersion.ReferenceSnapshot).ToArray();
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var isRoot = index == 0;
            if (!isRoot && node.DocumentId is Guid authorizedReferenceId)
                await RequireDocumentAccessAsync(authorizedReferenceId, actor, role, FolderAccess.View, cancellationToken);
            if (node.Status == ReferenceNodeStatus.Missing)
            {
                throw new PdmRuleException($"引用文件{node.FileName}缺失，不能生成完整打开清单。");
            }
            if (node.Status == ReferenceNodeStatus.Virtual)
            {
                continue;
            }
            Guid referencedDocumentId;
            if (node.DocumentId.HasValue)
            {
                referencedDocumentId = node.DocumentId.Value;
            }
            else
            {
                if (!allowCurrentVersionFallback)
                {
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }

                var referencedFileName = Path.GetFileName(node.FileName);
                var matches = currentProjectDocuments
                    .Where(document => document.FileName.Equals(referencedFileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                {
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }
                if (matches.Length > 1)
                {
                    throw new PdmRuleException($"项目中存在多个同名图档{node.FileName}，不能安全关联。");
                }

                referencedDocumentId = matches[0].Id;
            }

            DocumentVersion version;
            if (isRoot)
            {
                if (referencedDocumentId != documentId)
                {
                    throw new PdmRuleException("版本引用快照的根图档与所选图档不一致。");
                }
                version = rootVersion;
            }
            else
            {
                var versions = await repository.ListDocumentVersionsAsync(referencedDocumentId, cancellationToken);
                if (node.Revision is null)
                {
                    if (versionId.HasValue || releasedOnly)
                    {
                        throw new PdmRuleException($"引用文件{node.FileName}的快照未记录版本，不能用当前最新版本替代。");
                    }

                    version = versions.FirstOrDefault()
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}尚无可用的最新受控版本。");
                }
                else
                {
                    var referencedRevision = node.Revision.GetValueOrDefault().Display;
                    version = versions.FirstOrDefault(item => item.Revision.Display.Equals(referencedRevision, StringComparison.OrdinalIgnoreCase))
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}的受控版本{referencedRevision}不存在。");
                }
            }

            if (filesByDocument.TryGetValue(referencedDocumentId, out var existing))
            {
                if (existing.VersionId != version.Id)
                {
                    throw new PdmRuleException($"同一图档{node.FileName}在快照中引用了不同版本，不能安全打开。");
                }
                continue;
            }

            var fileName = Path.GetFileName(node.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new PdmRuleException("引用快照包含无效文件名。");
            }
            if (fileNames.TryGetValue(fileName, out var conflictingDocumentId) && conflictingDocumentId != referencedDocumentId)
            {
                throw new PdmRuleException($"项目中存在同名文件{fileName}，不能放入同一受控工作区。");
            }

            await fileStorage.VerifyStoredFileAsync(
                project,
                new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt),
                cancellationToken);
            var item = new ControlledOpenFile(
                referencedDocumentId,
                version.Id,
                version.Revision.Display,
                fileName,
                fileName,
                version.FileLength,
                version.Sha256,
                node.Configuration,
                isRoot);
            files.Add(item);
            filesByDocument.Add(referencedDocumentId, item);
            fileNames[fileName] = referencedDocumentId;
        }

        if (allowCurrentVersionFallback)
        {
            var currentDocumentsById = currentProjectDocuments.ToDictionary(document => document.Id);
            var drawingRelations = await repository.ListDocumentRelationsAsync(project.Id, cancellationToken);
            foreach (var relation in drawingRelations)
            {
                if (!filesByDocument.ContainsKey(relation.ModelDocumentId)
                    || filesByDocument.ContainsKey(relation.DrawingDocumentId)
                    || !currentDocumentsById.TryGetValue(relation.DrawingDocumentId, out var drawingDocument))
                {
                    continue;
                }

                await RequireDocumentAccessAsync(drawingDocument.Id, actor, role, FolderAccess.View, cancellationToken);
                var drawingVersions = await repository.ListDocumentVersionsAsync(drawingDocument.Id, cancellationToken);
                var drawingVersion = drawingVersions.FirstOrDefault(item =>
                        item.Revision.Display.Equals(drawingDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                    ?? drawingVersions.FirstOrDefault()
                    ?? throw new PdmNotFoundException($"关联工程图{drawingDocument.FileName}尚无可用的受控版本。");
                var drawingFileName = Path.GetFileName(drawingDocument.FileName);
                if (string.IsNullOrWhiteSpace(drawingFileName))
                {
                    throw new PdmRuleException("关联工程图包含无效文件名。");
                }
                if (fileNames.TryGetValue(drawingFileName, out var conflictingDocumentId)
                    && conflictingDocumentId != drawingDocument.Id)
                {
                    throw new PdmRuleException($"项目中存在同名文件{drawingFileName}，不能放入同一受控工作区。");
                }

                await fileStorage.VerifyStoredFileAsync(
                    project,
                    new StoredFile(drawingVersion.StorageRelativePath, drawingVersion.FileLength, drawingVersion.Sha256, drawingVersion.CreatedAt),
                    cancellationToken);
                var drawingFile = new ControlledOpenFile(
                    drawingDocument.Id,
                    drawingVersion.Id,
                    drawingVersion.Revision.Display,
                    drawingFileName,
                    drawingFileName,
                    drawingVersion.FileLength,
                    drawingVersion.Sha256,
                    "图纸",
                    false);
                files.Add(drawingFile);
                filesByDocument.Add(drawingDocument.Id, drawingFile);
                fileNames[drawingFileName] = drawingDocument.Id;
            }
        }

        var rootFile = files.Single(item => item.IsRoot);
        var manifest = new ControlledOpenManifest(
            Guid.NewGuid(),
            project.Id,
            project.Code,
            rootDocument.Id,
            rootVersion.Id,
            rootVersion.Revision.Display,
            rootFile.RelativePath,
            forEdit,
            files);
        await AuditAsync(
            actor,
            forEdit ? "document.open-manifest.edit" : releasedOnly ? "document.open-manifest.released" : "document.open-manifest.readonly",
            nameof(PdmDocument),
            documentId.ToString(),
            $"{rootVersion.Revision.Display}; files={files.Count}",
            cancellationToken);
        return manifest;
    }

    private static IEnumerable<DocumentReferenceNode> FlattenOpenNodes(DocumentReferenceNode root)
    {
        var pending = new Stack<DocumentReferenceNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ReleaseManage, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");

        if (string.IsNullOrWhiteSpace(number) || number.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PdmRuleException("发布包编号不能为空，且不能包含文件名非法字符。");
        }

        if (string.IsNullOrWhiteSpace(processReviewer) || string.IsNullOrWhiteSpace(approver))
        {
            throw new PdmRuleException("必须指定工艺审核人和批准人。");
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能创建发布包。");
        if (referenceSnapshotId.HasValue && referenceSnapshotId.Value != Guid.Empty && referenceSnapshotId.Value != snapshot.SnapshotId)
        {
            throw new PdmConflictException("指定的引用树快照不是项目当前最新快照，请刷新后重试。");
        }

        var mechanical = await repository.GetBomAsync(projectId, BomKind.Mechanical, cancellationToken);
        var electrical = await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken);
        if (mechanical.Count == 0 || electrical.Count == 0 || mechanical.Concat(electrical).Any(item => !item.IsComplete))
        {
            throw new PdmRuleException("机械BOM和电气BOM必须存在且完整。 ");
        }

        var packageId = Guid.NewGuid();
        var tasks = new[]
        {
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.ProcessReview, processReviewer, null, null, null, null),
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.Approval, approver, null, null, null, null)
        };
        var package = new ReleasePackage(
            packageId,
            projectId,
            number,
            ReleasePackageState.Draft,
            snapshot.SnapshotId,
            BomRevision("M", mechanical),
            BomRevision("E", electrical),
            tasks,
            timeProvider.GetUtcNow(),
            null,
            null)
        {
            MechanicalBomSnapshot = mechanical.ToArray(),
            ElectricalBomSnapshot = electrical.ToArray()
        };

        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await publisher.PrepareAsync(created, project, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), number, cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<BomItem>> ReplaceBomAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItemInput> inputs,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.BomEdit, cancellationToken);
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (inputs.Count == 0)
        {
            throw new PdmRuleException("BOM至少需要一条物料。");
        }

        var duplicateSequence = inputs.GroupBy(item => item.Sequence).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            throw new PdmRuleException($"BOM序号{duplicateSequence.Key}重复。");
        }

        var duplicateDrawing = inputs.GroupBy(item => item.DrawingNumber.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateDrawing is not null)
        {
            throw new PdmRuleException($"BOM图号{duplicateDrawing.Key}重复。");
        }

        var items = inputs.OrderBy(item => item.Sequence).Select(input =>
        {
            if (input.Sequence <= 0 || input.Quantity <= 0 || string.IsNullOrWhiteSpace(input.DrawingNumber)
                || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Unit)
                || string.IsNullOrWhiteSpace(input.Revision))
            {
                throw new PdmRuleException("BOM序号、图号、名称、数量、单位和版本必须有效。");
            }

            return new BomItem(
                Guid.NewGuid(), projectId, kind, input.Sequence, input.DrawingNumber.Trim(), input.Name.Trim(), input.Quantity,
                input.Unit.Trim(), NullIfWhiteSpace(input.Material), NullIfWhiteSpace(input.Specification), input.Revision.Trim(), input.IsComplete);
        }).ToArray();

        var saved = await repository.ReplaceBomAsync(projectId, kind, items, cancellationToken);
        await AuditAsync(actor, "bom.replace", nameof(BomItem), projectId.ToString(), $"{kind}:{saved.Count}", cancellationToken);
        return saved;
    }

    public async Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        await publisher.ValidateAsync(package, project, cancellationToken);
        var submitted = await repository.SubmitReleasePackageAsync(releasePackageId, actor, cancellationToken);
        await AuditAsync(actor, package.State == ReleasePackageState.Rejected ? "release-package.resubmit" : "release-package.submit", nameof(ReleasePackage), package.Id.ToString(), package.Number, cancellationToken);
        return submitted;
    }

    public async Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ReleaseManage, cancellationToken);
        comment = RequiredComment(comment, "撤回原因");
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var withdrawn = await repository.WithdrawReleasePackageAsync(releasePackageId, actor, cancellationToken);
        await AuditAsync(actor, "release-package.withdraw", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；{comment}", cancellationToken);
        return withdrawn;
    }

    public async Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ReleaseManage, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit | FolderAccess.Publish, cancellationToken);
        comment = RequiredComment(comment, "作废原因");
        var obsolete = await repository.ObsoleteDocumentAsync(documentId, actor, cancellationToken);
        await AuditAsync(actor, "document.obsolete", nameof(PdmDocument), documentId.ToString(), $"{obsolete.DrawingNumber}；{comment}", cancellationToken);
        return obsolete;
    }

    public async Task<IReadOnlyList<DocumentWhereUsed>> ListWhereUsedAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var usages = await repository.ListWhereUsedAsync(documentId, cancellationToken);
        var visible = new List<DocumentWhereUsed>();
        foreach (var usage in usages)
        {
            if (await repository.HasProjectContentReadAccessAsync(usage.ProjectId, actor, role, cancellationToken)) visible.Add(usage);
        }
        return visible;
    }

    public async Task<ReleasePackage> DecideAsync(Guid taskId, string actor, UserRole role, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, PermissionCodes.ApprovalDecide, cancellationToken);

        var package = await repository.DecideApprovalAsync(taskId, actor, decision, comment, cancellationToken);
        await AuditAsync(actor, "approval.decide", nameof(ApprovalTask), taskId.ToString(), decision.ToString(), cancellationToken);

        if (package.State != ReleasePackageState.Publishing)
        {
            return package;
        }

        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。 ");
        string publishedPath;
        try
        {
            publishedPath = await publisher.PublishAsync(package, project, cancellationToken);
        }
        catch (Exception exception)
        {
            await repository.MarkPublishFailedAsync(package.Id, exception.Message, cancellationToken);
            await AuditAsync(actor, "release-package.publish-failed", nameof(ReleasePackage), package.Id.ToString(), exception.Message, cancellationToken);
            throw;
        }

        var publishedAt = timeProvider.GetUtcNow();
        var releasedVersions = await repository.PublishReleasePackageVersionsAsync(package.Id, package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.Approval).Id, actor, cancellationToken);
        foreach (var version in releasedVersions)
        {
            await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        }
        await repository.MarkPublishedAsync(package.Id, publishedPath, publishedAt, cancellationToken);
        await AuditAsync(actor, "release-package.publish", nameof(ReleasePackage), package.Id.ToString(), publishedPath, cancellationToken);
        return (await repository.FindReleasePackageAsync(package.Id, cancellationToken))!;
    }

    private static void ValidateCheckoutSettings(PdmSystemSettings settings)
    {
        if (settings.CheckoutHeartbeatSeconds is < 30 or > 600) throw new PdmRuleException("编辑心跳间隔必须为30到600秒。");
        if (settings.CheckoutLeaseMinutes is < 2 or > 60 || settings.CheckoutLeaseMinutes * 60 < settings.CheckoutHeartbeatSeconds * 2)
            throw new PdmRuleException("编辑租约必须为2到60分钟，且至少是心跳间隔的两倍。");
        if (settings.CheckoutOfflineGraceMinutes is < 5 or > 1440) throw new PdmRuleException("离线宽限必须为5到1440分钟。");
        if (settings.CheckoutReminderHours < 1
            || settings.CheckoutStrongReminderHours <= settings.CheckoutReminderHours
            || settings.CheckoutOverdueHours <= settings.CheckoutStrongReminderHours
            || settings.CheckoutForceReleaseHours <= settings.CheckoutOverdueHours
            || settings.CheckoutForceReleaseHours > 720)
            throw new PdmRuleException("提醒、强提醒、超时和强制释放时间必须依次增大，且不超过720小时。");
    }

    private static EditLockAttentionLevel AttentionLevel(TimeSpan elapsed, PdmSystemSettings settings) =>
        elapsed >= TimeSpan.FromHours(settings.CheckoutForceReleaseHours) ? EditLockAttentionLevel.Reclaimable
        : elapsed >= TimeSpan.FromHours(settings.CheckoutOverdueHours) ? EditLockAttentionLevel.Overdue
        : elapsed >= TimeSpan.FromHours(settings.CheckoutStrongReminderHours) ? EditLockAttentionLevel.StrongReminder
        : elapsed >= TimeSpan.FromHours(settings.CheckoutReminderHours) ? EditLockAttentionLevel.Reminder
        : EditLockAttentionLevel.Normal;

    private static bool IsProjectLockManager(Project project, string actor, UserRole role) =>
        role == UserRole.Administrator
        || string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
        || project.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)
        || string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase);

    private static string RequiredReason(string reason)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw new PdmRuleException("请填写申请或强制释放原因。");
        if (reason.Length > 500) throw new PdmRuleException("释放原因不能超过500个字符。");
        return reason;
    }

    private static string RequiredComment(string comment, string label)
    {
        comment = comment?.Trim() ?? string.Empty;
        if (comment.Length == 0) throw new PdmRuleException($"请填写{label}。");
        if (comment.Length > 500) throw new PdmRuleException($"{label}不能超过500个字符。");
        return comment;
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new PdmRuleException("待入库图档的SHA-256指纹无效。");
        return normalized;
    }

    private static DocumentRegistrationMatch ToRegistrationMatch(
        string candidateKey,
        DocumentRegistrationMatchKind matchKind,
        DocumentContentFingerprint fingerprint,
        IReadOnlyDictionary<Guid, Project> projects)
    {
        projects.TryGetValue(fingerprint.Document.ProjectId, out var project);
        return new DocumentRegistrationMatch(
            candidateKey,
            matchKind,
            fingerprint.Document.Id,
            fingerprint.Document.ProjectId,
            project?.Code,
            project?.Name,
            fingerprint.Document.DrawingNumber,
            fingerprint.Document.FileName,
            fingerprint.Document.Revision.Display);
    }

    private async Task RequirePermissionAsync(UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (!await repository.HasRolePermissionAsync(role, permissionCode, cancellationToken))
            throw new UnauthorizedAccessException("当前角色未配置执行此操作的权限。");
    }

    private async Task<PdmDocument> RequireDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");

    private async Task RequireDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View, cancellationToken);
    }

    private async Task RequireDocumentAccessAsync(Guid documentId, string actor, UserRole role, FolderAccess requiredAccess, CancellationToken cancellationToken)
    {
        if (!await repository.HasDocumentAccessAsync(documentId, actor, role, requiredAccess, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目目录下图档的对应操作权限。");
    }

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);

    private static string BomRevision(string prefix, IReadOnlyList<BomItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8]}";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] NormalizeUsers(IEnumerable<string> usernames) => usernames
        .Select(username => username?.Trim() ?? string.Empty)
        .Where(username => username.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsActiveMemberOfOrganization(OrganizationDirectory directory, string username, Guid organizationId) =>
        directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
        && directory.Memberships.Any(membership => string.Equals(membership.Username, username, StringComparison.OrdinalIgnoreCase)
            && directory.Units.Any(unit => unit.Id == membership.UnitId && unit.IsActive && unit.OrganizationId == organizationId));

    private static bool IsActiveMemberOfDivision(OrganizationDirectory directory, string username, Guid divisionId) =>
        directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
        && directory.Memberships.Any(membership => string.Equals(membership.Username, username, StringComparison.OrdinalIgnoreCase)
            && IsUnitWithin(directory.Units, membership.UnitId, divisionId));

    private static bool IsUnitWithin(IReadOnlyList<OrganizationUnit> units, Guid unitId, Guid ancestorId)
    {
        var current = units.SingleOrDefault(unit => unit.Id == unitId && unit.IsActive);
        while (current is not null)
        {
            if (current.Id == ancestorId) return true;
            current = current.ParentUnitId is null ? null : units.SingleOrDefault(unit => unit.Id == current.ParentUnitId && unit.IsActive);
        }
        return false;
    }

    private static void ValidateProjectDetails(string? name, string? projectAlias, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new PdmRuleException("项目名称不能为空。");
        if (name.Trim().Length > 200 || projectAlias?.Trim().Length > 200) throw new PdmRuleException("项目名称或项目别名超过允许长度。");
        if (quantity is < 1 or > 10000) throw new PdmRuleException("数量必须在1到10000之间。");
    }
}
