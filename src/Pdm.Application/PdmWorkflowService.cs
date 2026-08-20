using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PdmWorkflowService(IPdmRepository repository, IFileStorage fileStorage, IReleasePackagePublisher publisher, TimeProvider timeProvider)
{
    private const string ReconcileAutoAdded = "AutoAdded";
    private const string ReconcileClassificationChanged = "ClassificationChanged";
    private const string ReconcilePendingClassification = "PendingClassification";
    private const string ReconcilePendingRemoval = "PendingRemoval";
    private const string ReconcileManualUnmatched = "ManualUnmatched";
    private const string ReconcileManuallyClassified = "ManuallyClassified";
    private const string ReconcileManuallyRetained = "ManuallyRetained";
    private const string ReconcileManuallyExcluded = "ManuallyExcluded";
    private const string ReconcileManualAdded = "ManualAdded";
    private const string ReconcileRestored = "Restored";
    private const string ReconcileDeleted = "Deleted";

    public async Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, cancellationToken);
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
        if (!string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("所选客户不是从U9C同步的数据，请重新选择客户。");

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
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectChildCreate, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.CustomerSettingsManage, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        name = name?.Trim() ?? string.Empty;
        if (code is < 0 or > 99) throw new PdmRuleException("设备类型编码必须为0到99。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new PdmRuleException("设备类型名称不能为空且不能超过100个字符。");
        var equipmentType = await repository.SaveEquipmentTypeAsync(code, name, isActive, cancellationToken);
        await AuditAsync(actor, "equipment-type.update", nameof(EquipmentTypeDefinition), code.ToString("D2"), $"{code:D2} · {name} · {(isActive ? "启用" : "停用")}", cancellationToken);
        return equipmentType;
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings input, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(input.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(input.ReleaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        var settingsInput = BomPropertyMappingCatalog.Apply(input with
        {
            VaultRoot = vaultRoot,
            ReleaseRoot = releaseRoot,
            ValidationRules = NormalizeBomValidationRules(input.ValidationRules)
        });
        ValidateCheckoutSettings(settingsInput);
        ValidateBomPropertyMappings(settingsInput);
        var settings = await repository.UpdateSystemSettingsAsync(settingsInput, cancellationToken);
        await AuditAsync(actor, "system.storage.update", nameof(PdmSystemSettings), "storage", $"图档根目录：{settings.VaultRoot}；发包根目录：{settings.ReleaseRoot}", cancellationToken);
        await AuditAsync(actor, "system.checkout-policy.update", nameof(PdmSystemSettings), "checkout-policy", $"心跳{settings.CheckoutHeartbeatSeconds}秒；离线宽限{settings.CheckoutOfflineGraceMinutes}分钟；超时{settings.CheckoutOverdueHours}小时；强制释放{settings.CheckoutForceReleaseHours}小时", cancellationToken);
        await AuditAsync(actor, "system.bom-property-mapping.update", nameof(PdmSystemSettings), "bom-property-mapping", $"PDM属性{settings.BomPropertyMappings.Count}项；SolidWorks映射{settings.BomPropertyMappings.Count(item => item.MappingEditable)}项", cancellationToken);
        await AuditAsync(actor, "system.bom-validation.update", nameof(PdmSystemSettings), "bom-validation", $"标准件{settings.ValidationRules.Standard.Count}项；非标件{settings.ValidationRules.NonStandard.Count}项；电气件{settings.ValidationRules.Electrical.Count}项", cancellationToken);
        return settings;
    }

    private static BomValidationRules NormalizeBomValidationRules(BomValidationRules? input)
    {
        input ??= BomValidationRules.Default;
        IReadOnlyList<string> Normalize(IReadOnlyList<string> fields, string label)
        {
            var normalized = BomValidationFieldCatalog.Normalize(fields);
            var unknown = normalized.FirstOrDefault(field => !BomValidationFieldCatalog.AllFields.Contains(field, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null) throw new PdmRuleException($"{label}BOM包含未知校验字段：{unknown}。");
            var missingCore = BomValidationFieldCatalog.CoreFields.Where(field => !normalized.Contains(field, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (missingCore.Length > 0)
                throw new PdmRuleException($"{label}BOM不能取消系统基础必填项：{string.Join('、', missingCore.Select(BomValidationFieldCatalog.Label))}。");
            return normalized;
        }

        return new(
            Normalize(input.Standard, "标准件"),
            Normalize(input.NonStandard, "非标件"),
            Normalize(input.Electrical, "电气件"));
    }

    private static void ValidateBomPropertyMappings(PdmSystemSettings settings)
    {
        var mappings = settings.BomPropertyMappings.Where(mapping => mapping.MappingEditable).ToArray();
        if (mappings.Any(mapping =>
                string.IsNullOrWhiteSpace(mapping.PdmPropertyKey)
                || mapping.PdmPropertyKey.Trim().Length > 100
                || string.IsNullOrWhiteSpace(mapping.PdmPropertyName)
                || mapping.PdmPropertyName.Trim().Length > 100
                || string.IsNullOrWhiteSpace(mapping.SolidWorksProperty)
                || mapping.SolidWorksProperty.Trim().Length > 100))
            throw new PdmRuleException("SolidWorks属性名称不能为空且不能超过100个字符。");
    }

    public async Task<RolePermissionDirectory> UpdateRolePermissionsAsync(string targetRoleCode, IReadOnlyList<string> permissionCodes, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var unknown = permissionCodes.Where(code => !RolePermissionCatalog.IsKnown(code)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new PdmRuleException($"包含未登记的权限代码：{string.Join('、', unknown)}。");
        var directory = await repository.SetRolePermissionsAsync(targetRoleCode, permissionCodes, cancellationToken);
        var saved = directory.Roles.Single(item => string.Equals(item.Role, targetRoleCode, StringComparison.OrdinalIgnoreCase));
        await AuditAsync(actor, "role.permissions.update", nameof(RoleDefinition), targetRoleCode, $"{saved.Name} · {saved.Permissions.Count}项权限", cancellationToken);
        return directory;
    }

    public async Task<RolePermissionDirectory> CreateRoleAsync(string name, string description, string sourceRoleCode, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        name = name?.Trim() ?? string.Empty;
        description = description?.Trim() ?? string.Empty;
        sourceRoleCode = sourceRoleCode?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80) throw new PdmRuleException("角色名称不能为空且不能超过80个字符。");
        if (description.Length > 300) throw new PdmRuleException("角色说明不能超过300个字符。");
        if (string.IsNullOrWhiteSpace(sourceRoleCode)) throw new PdmRuleException("请选择复制来源角色。");
        var directory = await repository.CreateRoleAsync(name, description, sourceRoleCode, cancellationToken);
        var saved = directory.Roles.Single(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) && !item.IsSystem);
        await AuditAsync(actor, "role.create", nameof(RoleDefinition), saved.Role, $"{saved.Name} · 复制自{sourceRoleCode}", cancellationToken);
        return directory;
    }

    public async Task<RolePermissionDirectory> DeleteRoleAsync(string roleCode, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var directory = await repository.GetRolePermissionDirectoryAsync(cancellationToken);
        var role = directory.Roles.SingleOrDefault(item => string.Equals(item.Role, roleCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("角色不存在。");
        var saved = await repository.DeleteRoleAsync(role.Role, cancellationToken);
        await AuditAsync(actor, "role.delete", nameof(RoleDefinition), role.Role, role.Name, cancellationToken);
        return saved;
    }

    public async Task<UserAccount> CreateManagedUserAsync(CreateManagedUserCommand command, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var username = NormalizeUsername(command.Username);
        var displayName = NormalizeDisplayName(command.DisplayName);
        if (await repository.FindUserAsync(username, cancellationToken) is not null) throw new PdmConflictException("用户名已经存在。");
        var targetRole = await FindRoleAsync(command.RoleCode, cancellationToken);
        var user = new UserAccount(Guid.NewGuid(), username, displayName, command.PasswordHash, targetRole.BaseRole, command.IsActive, RoleCode: targetRole.Role);
        await repository.CreateUserAsync(user, cancellationToken);
        await AuditAsync(actor, "user.create", nameof(UserAccount), username, $"{displayName} · {targetRole.Name} · {(command.IsActive ? "启用" : "停用")}", cancellationToken);
        return user;
    }

    public async Task<UserAccount> UpdateManagedUserAsync(UpdateManagedUserCommand command, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var username = NormalizeUsername(command.Username);
        var displayName = NormalizeDisplayName(command.DisplayName);
        var current = await repository.FindUserAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
        var targetRole = await FindRoleAsync(command.RoleCode, cancellationToken);
        if (string.Equals(actor, username, StringComparison.OrdinalIgnoreCase) && (!command.IsActive || !string.Equals(targetRole.Role, current.EffectiveRoleCode, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("不能停用当前登录账号或修改其系统角色。");
        if (current.Role == UserRole.Administrator && current.IsActive && (targetRole.BaseRole != UserRole.Administrator || !command.IsActive))
        {
            var activeAdministrators = (await repository.ListUsersAsync(cancellationToken)).Count(user => user.Role == UserRole.Administrator && user.IsActive);
            if (activeAdministrators <= 1) throw new PdmRuleException("系统至少需要保留一个启用的管理员账号。");
        }
        var saved = await repository.UpdateUserAsync(username, displayName, targetRole.BaseRole, targetRole.Role, command.IsActive, cancellationToken);
        await AuditAsync(actor, "user.update", nameof(UserAccount), username, $"{displayName} · {targetRole.Name} · {(command.IsActive ? "启用" : "停用")}", cancellationToken);
        return saved;
    }

    public async Task<UserAccount> ResetManagedUserPasswordAsync(string username, string passwordHash, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        username = NormalizeUsername(username);
        if (string.Equals(actor, username, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("请在个人设置中修改当前账号密码。");
        var saved = await repository.UpdateUserPasswordAsync(username, passwordHash, cancellationToken);
        await AuditAsync(actor, "user.password.reset", nameof(UserAccount), username, "管理员重置为初始密码", cancellationToken);
        return saved;
    }

    public async Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
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
            if (command.Kind == OrganizationUnitKind.BusinessDivision) throw new PdmRuleException("可承接项目的部门必须直接隶属于公司。");
            if (command.Id is not null && IsUnitWithin(directory.Units, parent.Id, command.Id.Value)) throw new PdmRuleException("上级组织不能选择当前组织自身或其下级。");
            var proposedDepth = GetOrganizationUnitDepth(directory.Units, parent.Id) + 1;
            var subtreeHeight = command.Id is null ? 1 : GetOrganizationSubtreeHeight(directory.Units, command.Id.Value);
            if (proposedDepth + subtreeHeight - 1 > 10) throw new PdmRuleException("公司下的组织层级不能超过10级。");
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
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        primaryManager = primaryManager?.Trim() ?? string.Empty;
        var collaborators = NormalizeUsers(collaborativeManagers).Where(username => !string.Equals(username, primaryManager, StringComparison.OrdinalIgnoreCase)).ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.SingleOrDefault(item => item.Id == unitId && item.IsActive && item.Kind == OrganizationUnitKind.BusinessDivision)
            ?? throw new PdmRuleException("只能为启用的直属部门配置负责人。");
        var candidates = new[] { primaryManager }.Concat(collaborators).ToArray();
        if (string.IsNullOrWhiteSpace(primaryManager) || candidates.Any(username => !IsActiveMemberOfDivision(directory, username, unit.Id)))
            throw new PdmRuleException("部门负责人必须是该部门内的启用账号。");
        var saved = await repository.SetOrganizationUnitManagersAsync(unitId, primaryManager, collaborators, cancellationToken);
        await AuditAsync(actor, "organization.managers.update", nameof(OrganizationUnitManagers), unitId.ToString(), $"主负责人：{primaryManager}；协同：{string.Join('、', collaborators)}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectExecutionAssign, cancellationToken);
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

    public async Task<Project> UpdateProjectDetailsAsync(Guid projectId, UpdateProjectDetailsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectEdit, cancellationToken);
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前账号无权编辑该项目。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        if (command.SignedDate == default) throw new PdmRuleException("订单日期不能为空。");

        if (project.ParentProjectId is null)
        {
            if (command.OrganizationId is null || string.IsNullOrWhiteSpace(command.ProjectTypeCode)
                || command.EquipmentTypeCode is null)
                throw new PdmRuleException("所属公司、项目类型和设备类型不能为空。");

            var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
            if (!options.Organizations.Any(item => item.Id == command.OrganizationId && item.IsActive))
                throw new PdmRuleException("所选组织不存在或已停用。");
            if (!options.ProjectTypes.Any(item => string.Equals(item.Code, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase) && item.IsActive))
                throw new PdmRuleException("所选项目类型不存在或已停用。");
            if (!options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
                throw new PdmRuleException("所选设备类型不存在或已停用。");
            var customer = command.CustomerId is null ? null : await repository.FindCustomerAsync(command.CustomerId.Value, cancellationToken);
            if (command.CustomerId is not null && (customer is null || !customer.IsActive || !string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException("所选客户不存在、已停用或不是从U9C同步的数据。");

            var codeWillChange = project.OrganizationId != command.OrganizationId
                || !string.Equals(project.ProjectTypeCode, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase);
            var numberingWillChange = codeWillChange
                || project.EquipmentTypeCode != command.EquipmentTypeCode
                || customer is not null && !string.Equals(project.CustomerCode, customer.Code, StringComparison.OrdinalIgnoreCase)
                || project.Quantity != command.Quantity;
            if (numberingWillChange)
            {
                var tree = (await repository.ListProjectsAsync(cancellationToken))
                    .Where(item => item.Id == project.Id || item.ParentProjectId == project.Id)
                    .ToArray();
                if (codeWillChange)
                {
                    foreach (var item in tree)
                        if ((await repository.ListDocumentsAsync(item.Id, cancellationToken)).Count > 0)
                            throw new PdmConflictException("项目或子项目已有受控图档，不能修改所属公司或项目类型。请在图档入库前完成编号调整。");
                }
                if (command.Quantity < project.Quantity && (await repository.ListDocumentsAsync(project.Id, cancellationToken)).Count > 0)
                    throw new PdmConflictException("项目已有受控图档，不能减少设备数量和序列号。");
                foreach (var item in tree)
                    if ((await repository.ListReleasePackagesAsync(item.Id, cancellationToken)).Count > 0)
                        throw new PdmConflictException("项目或子项目已有审批或发布包，不能修改编号资料。");
            }
        }
        else if (command.Quantity != project.Quantity && (await repository.ListReleasePackagesAsync(project.Id, cancellationToken)).Count > 0)
        {
            throw new PdmConflictException("子项目已有审批或发布包，不能修改数量和序列号。");
        }
        else if (command.Quantity < project.Quantity && (await repository.ListDocumentsAsync(project.Id, cancellationToken)).Count > 0)
        {
            throw new PdmConflictException("子项目已有受控图档，不能减少设备数量和序列号。");
        }

        var normalized = command with
        {
            ProjectTypeCode = NullIfWhiteSpace(command.ProjectTypeCode)?.ToUpperInvariant(),
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias)
        };
        var saved = await repository.UpdateProjectDetailsAsync(projectId, normalized, cancellationToken);
        await AuditAsync(actor, "project.details.update", nameof(Project), project.Id.ToString(),
            $"{project.Code} → {saved.Code}；{project.DeviceModel ?? "—"} → {saved.DeviceModel ?? "—"}；数量 {project.Quantity} → {saved.Quantity}；{saved.Name}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectStaffingManage, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectDesignerAssign, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectDelete, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await repository.DeleteProjectAsync(projectId, cancellationToken);
        await AuditAsync(actor, "project.delete", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
            throw new PdmRuleException("设计树存在缺失引用，不能提交存档。 ");
        }

        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (isProjectRoot)
        {
            var currentProjectRoot = await repository.GetLatestReferenceSnapshotAsync(document.ProjectId, cancellationToken);
            if (currentProjectRoot is not null && currentProjectRoot.RootDocumentId != documentId)
            {
                throw new PdmRuleException("所选图档不是项目根装配体，普通存档不能替换项目完整结构。");
            }
        }
        await fileStorage.VerifyStoredFileAsync(project, file, cancellationToken);
        var standard = await repository.GetBomAsync(document.ProjectId, BomKind.Standard, cancellationToken);
        var nonStandard = await repository.GetBomAsync(document.ProjectId, BomKind.NonStandard, cancellationToken);
        var legacyMechanical = await repository.GetBomAsync(document.ProjectId, BomKind.Mechanical, cancellationToken);
        var mechanical = standard.Concat(nonStandard).Concat(legacyMechanical).Where(item => !item.IsManuallyExcluded).ToArray();
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
        if (result.VersionCreated)
        {
            try
            {
                var bomSnapshot = isProjectRoot
                    ? snapshot
                    : await repository.GetLatestReferenceSnapshotAsync(document.ProjectId, cancellationToken);
                result = bomSnapshot is null
                    ? result with { BomUpdateError = "项目尚无已存档的主结构，BOM未自动更新。请先提交主装配。" }
                    : result with
                    {
                        BomUpdate = await GenerateMechanicalBomFromSnapshotAsync(
                            document.ProjectId,
                            bomSnapshot,
                            actor,
                            cancellationToken,
                            true)
                    };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = result with { BomUpdateError = exception.Message };
                try
                {
                    await AuditAsync(actor, "bom.generate.failed", nameof(BomItem), document.ProjectId.ToString(), exception.Message, cancellationToken);
                }
                catch
                {
                    // BOM refresh is best-effort after the immutable document version has already been stored.
                }
            }
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var document = await repository.DiscardCheckoutAsync(documentId, actor, sessionId, cancellationToken);
        await AuditAsync(actor, "document.checkout.discard", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<IReadOnlyList<EditLockSummary>> ListEditLocksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        var canRequestPermission = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
        var canForcePermission = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Publish, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var version = await repository.PublishDocumentVersionAsync(documentId, sourceVersionId, releasePackageId, approvalTaskId, actor, cancellationToken);
        await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        return version;
    }

    public async Task AuditVersionReadAsync(Guid documentId, Guid versionId, string actor, UserRole role, string action, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken);
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
            await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
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
        var warnings = new List<string>();
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
            var optionalCurrentDrawing = allowCurrentVersionFallback
                && !isRoot
                && node.Kind == DocumentKind.Drawing;
            if (!isRoot && node.DocumentId is Guid authorizedReferenceId)
                await RequireDocumentAccessAsync(authorizedReferenceId, actor, role, FolderAccess.View, cancellationToken);
            if (node.Status == ReferenceNodeStatus.Missing)
            {
                if (optionalCurrentDrawing)
                {
                    warnings.Add($"引用文件{node.FileName}缺失，本次按缺失引用打开。");
                    continue;
                }
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
                    if (optionalCurrentDrawing)
                    {
                        warnings.Add($"引用文件{node.FileName}尚未登记，本次按缺失引用打开。");
                        continue;
                    }
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }
                if (matches.Length > 1)
                {
                    throw new PdmRuleException($"项目中存在多个同名图档{node.FileName}，不能安全关联。");
                }

                referencedDocumentId = matches[0].Id;
            }

            DocumentVersion? version;
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

                    version = versions.FirstOrDefault();
                    if (version is null && allowCurrentVersionFallback)
                    {
                        warnings.Add($"引用文件{node.FileName}尚未形成受控版本，本次按缺失引用打开。");
                        continue;
                    }
                    if (version is null)
                    {
                        throw new PdmNotFoundException($"引用文件{node.FileName}尚无可用的最新受控版本。");
                    }
                }
                else
                {
                    var referencedRevision = node.Revision.GetValueOrDefault().Display;
                    version = versions.FirstOrDefault(item => item.Revision.Display.Equals(referencedRevision, StringComparison.OrdinalIgnoreCase));
                    if (version is null && allowCurrentVersionFallback)
                    {
                        version = versions.FirstOrDefault();
                        if (version is null)
                        {
                            warnings.Add($"引用文件{node.FileName}尚未形成受控版本，本次按缺失引用打开。");
                            continue;
                        }
                        warnings.Add($"引用文件{node.FileName}的快照版本{referencedRevision}不存在，已使用最新受控版本{version.Revision.Display}。");
                    }
                    if (version is null)
                    {
                        throw new PdmNotFoundException($"引用文件{node.FileName}的受控版本{referencedRevision}不存在。");
                    }
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
                    ?? drawingVersions.FirstOrDefault();
                if (drawingVersion is null)
                {
                    warnings.Add($"关联工程图{drawingDocument.FileName}尚未形成受控版本，本次未下载。");
                    continue;
                }
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
            files,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
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
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");
        return await CreateReleasePackageAsync(
            projectId, referenceSnapshotId, number, number, "兼容既有发布流程创建的设变",
            project.SerialNumbers.FirstOrDefault() ?? "未指定", null,
            processReviewer, approver, actor, role, cancellationToken);
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string changeNumber,
        string changeReason,
        string effectiveSerialFrom,
        string? effectiveSerialTo,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
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
        changeNumber = RequiredComment(changeNumber, "设变编号");
        changeReason = RequiredComment(changeReason, "设变原因");
        effectiveSerialFrom = RequiredComment(effectiveSerialFrom, "生效起始序列号");
        effectiveSerialTo = NullIfWhiteSpace(effectiveSerialTo);
        if (project.SerialNumbers.Count > 0 && effectiveSerialFrom != "未指定" && !project.SerialNumbers.Contains(effectiveSerialFrom, StringComparer.OrdinalIgnoreCase))
            throw new PdmRuleException("生效起始序列号不属于当前项目。");
        if (effectiveSerialTo is not null && project.SerialNumbers.Count > 0 && !project.SerialNumbers.Contains(effectiveSerialTo, StringComparer.OrdinalIgnoreCase))
            throw new PdmRuleException("生效结束序列号不属于当前项目。");
        if (effectiveSerialTo is not null && project.SerialNumbers.Count > 0)
        {
            var startIndex = project.SerialNumbers.ToList().FindIndex(serial => string.Equals(serial, effectiveSerialFrom, StringComparison.OrdinalIgnoreCase));
            var endIndex = project.SerialNumbers.ToList().FindIndex(serial => string.Equals(serial, effectiveSerialTo, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0 || endIndex < startIndex) throw new PdmRuleException("生效结束序列号不能早于起始序列号。");
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能创建发布包。");
        if (referenceSnapshotId.HasValue && referenceSnapshotId.Value != Guid.Empty && referenceSnapshotId.Value != snapshot.SnapshotId)
        {
            throw new PdmConflictException("指定的引用树快照不是项目当前最新快照，请刷新后重试。");
        }

        var standard = await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken);
        var nonStandard = await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken);
        var unclassified = await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken);
        var legacyMechanical = await repository.GetBomAsync(projectId, BomKind.Mechanical, cancellationToken);
        var mechanical = standard.Concat(nonStandard).Concat(legacyMechanical).Where(item => !item.IsManuallyExcluded).ToArray();
        var electrical = await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken);
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var legacyMode = legacyMechanical.Count > 0 && standard.Count == 0 && nonStandard.Count == 0;
        if (unclassified.Any(item => !item.IsManuallyExcluded))
            throw new PdmRuleException("源数据中仍有待分类或待确认物料，请处理完成后再创建发布包。");
        if ((!legacyMode && !BomReady(BomKind.Standard, standard, validationRules))
            || (!legacyMode && !BomReady(BomKind.NonStandard, nonStandard, validationRules))
            || !BomReady(BomKind.Electrical, electrical, validationRules))
        {
            throw new PdmRuleException("标准件BOM、非标件BOM和电气BOM中的有效物料资料必须齐全；空BOM自动按无此类物料处理。");
        }

        var standardVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.Standard, standard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);
        var nonStandardVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.NonStandard, nonStandard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);
        var electricalVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.Electrical, electrical.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);

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
            StandardBomVersionId = standardVersion.Id,
            NonStandardBomVersionId = nonStandardVersion.Id,
            ElectricalBomVersionId = electricalVersion.Id,
            StandardBomRevision = standardVersion.Label,
            NonStandardBomRevision = nonStandardVersion.Label,
            StandardBomSnapshot = standardVersion.Items.ToArray(),
            NonStandardBomSnapshot = nonStandardVersion.Items.ToArray(),
            ChangeNumber = changeNumber,
            ChangeReason = changeReason,
            EffectiveSerialFrom = effectiveSerialFrom,
            EffectiveSerialTo = effectiveSerialTo,
            MechanicalBomSnapshot = mechanical.ToArray(),
            ElectricalBomSnapshot = electrical.ToArray()
        };

        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await publisher.PrepareAsync(created, project, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), number, cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        var items = await repository.GetBomAsync(projectId, kind, cancellationToken);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            return items.Select(item => item with { IsComplete = false }).ToArray();
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        return items.Select(item => item with { IsComplete = HasRequiredBomValues(item, kind, settings.ValidationRules) }).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> ReplaceBomAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItemInput> inputs,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("BOM类型必须是标准件、非标件或电气。");
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);
        var duplicateSequence = inputs.GroupBy(item => item.Sequence).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            throw new PdmRuleException($"BOM序号{duplicateSequence.Key}重复。");
        }

        var duplicateDrawing = inputs.GroupBy(item => item.DrawingNumber.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateDrawing is not null)
        {
            throw new PdmRuleException($"BOM物料编码{duplicateDrawing.Key}重复。");
        }

        var existing = await repository.GetBomAsync(projectId, kind, cancellationToken);
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var recycledConflict = existing.FirstOrDefault(existingItem => existingItem.IsManuallyExcluded && inputs.Any(input =>
            input.SourceDocumentId.HasValue
                ? input.SourceDocumentId == existingItem.SourceDocumentId
                    && string.Equals(input.SourceConfiguration ?? string.Empty, existingItem.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                : string.Equals(input.DrawingNumber.Trim(), existingItem.DrawingNumber, StringComparison.OrdinalIgnoreCase)));
        if (recycledConflict is not null)
            throw new PdmConflictException($"物料“{recycledConflict.DrawingNumber}”已在回收站中，请先从回收站恢复。");
        if (kind is BomKind.Standard or BomKind.NonStandard)
        {
        var omittedSourceItem = existing.FirstOrDefault(existingItem => existingItem.SourceDocumentId.HasValue
                && !existingItem.IsManuallyExcluded
                && !inputs.Any(input => input.SourceDocumentId.HasValue
                    ? input.SourceDocumentId == existingItem.SourceDocumentId
                        && string.Equals(input.SourceConfiguration ?? string.Empty, existingItem.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(input.DrawingNumber.Trim(), existingItem.DrawingNumber, StringComparison.OrdinalIgnoreCase)));
            if (omittedSourceItem is not null)
                throw new PdmRuleException($"图纸来源物料“{omittedSourceItem.DrawingNumber}”不可直接删除；请先从图纸更新，再处理待移除项。");
        }
        var items = inputs.OrderBy(item => item.Sequence).Select(input =>
        {
            if (input.Sequence <= 0 || input.Quantity <= 0 || string.IsNullOrWhiteSpace(input.DrawingNumber)
                || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Unit)
                || string.IsNullOrWhiteSpace(input.Revision))
            {
                throw new PdmRuleException("BOM序号、图号、名称、数量、单位和版本必须有效。");
            }

            var previous = input.SourceDocumentId.HasValue
                ? existing.FirstOrDefault(item => item.SourceDocumentId == input.SourceDocumentId
                    && string.Equals(item.SourceConfiguration ?? string.Empty, input.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                : existing.FirstOrDefault(item => string.Equals(item.DrawingNumber, input.DrawingNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            var material = NullIfWhiteSpace(input.Material);
            var specification = NullIfWhiteSpace(input.Specification);
            var candidate = new BomItem(
                previous?.Id ?? Guid.NewGuid(), projectId, kind, input.Sequence, input.DrawingNumber.Trim(), input.Name.Trim(), input.Quantity,
                U9UnitCatalog.NormalizeBomUnit(input.Unit), material, specification, input.Revision.Trim(),
                false)
            {
                Remark = NullIfWhiteSpace(input.Remark),
                Brand = NullIfWhiteSpace(input.Brand),
                SurfaceTreatment = NullIfWhiteSpace(input.SurfaceTreatment),
                Weight = NullIfWhiteSpace(input.Weight),
                SourceDocumentId = previous?.SourceDocumentId ?? input.SourceDocumentId,
                SourceConfiguration = previous?.SourceConfiguration ?? NullIfWhiteSpace(input.SourceConfiguration),
                Source = previous?.Source ?? "Manual",
                IsManuallyOverridden = previous?.Source == "Auto" || previous?.IsManuallyOverridden == true,
                IsPendingRemoval = false,
                IsPendingClassification = input.IsPendingClassification,
                IsManualUnmatched = input.IsManualUnmatched,
                IsManuallyRetained = input.IsManuallyRetained,
                IsManuallyExcluded = false,
                ReconciliationStatus = previous?.ReconciliationStatus ?? (previous is null ? ReconcileManualAdded : null),
                ReconciliationNote = previous?.ReconciliationNote ?? (previous is null ? "人工新增物料，不来源于当前图档源数据。" : null),
                ReconciliationUpdatedBy = previous?.ReconciliationUpdatedBy ?? (previous is null ? actor : null),
                ReconciliationUpdatedAt = previous?.ReconciliationUpdatedAt ?? (previous is null ? timeProvider.GetUtcNow() : null),
                PropertyWritebackStatus = previous?.PropertyWritebackStatus
            };
            return candidate with { IsComplete = HasRequiredBomValues(candidate, kind, validationRules) };
        }).ToArray();

        items = items.Concat(existing.Where(item => item.IsManuallyExcluded && items.All(savedItem => savedItem.Id != item.Id)))
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();

        var writebackIds = kind == BomKind.Electrical
            ? new HashSet<Guid>()
            : items.Where(item => item.SourceDocumentId.HasValue
                    && (item.PropertyWritebackStatus == CadPropertyWritebackStatus.PendingSave
                        || CadWritableValuesChanged(existing.FirstOrDefault(previous => previous.Id == item.Id), item)))
                .Select(item => item.Id)
                .ToHashSet();
        items = items.Select(item => writebackIds.Contains(item.Id)
            ? item with { PropertyWritebackStatus = CadPropertyWritebackStatus.Pending }
            : item).ToArray();
        var saved = await repository.ReplaceBomAsync(projectId, kind, items, cancellationToken);
        foreach (var item in saved.Where(item => writebackIds.Contains(item.Id)))
            await EnqueueCadPropertyWritebackAsync(item, actor, cancellationToken);
        await repository.SetBomEmptyDeclarationAsync(projectId, kind, false, actor, cancellationToken);
        await repository.SaveBomDraftAsync(projectId, kind, saved.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
        await AuditAsync(actor, "bom.replace", nameof(BomItem), projectId.ToString(), $"{kind}:{saved.Count}", cancellationToken);
        return await repository.GetBomAsync(projectId, kind, cancellationToken);
    }

    public async Task<BomGenerationResult> GenerateMechanicalBomAsync(Guid projectId, bool apply, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的设计树，不能生成BOM。");
        return await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, apply);
    }

    public async Task<IReadOnlyList<BomItem>> GetBomSourceDataAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的查看权限。");
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken);
        if (snapshot is null) return Array.Empty<BomItem>();
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
        var maintained = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken))
            .Where(item => item.SourceDocumentId.HasValue && !item.IsPendingRemoval && !item.IsManuallyExcluded)
            .ToArray();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var now = timeProvider.GetUtcNow();
        return generated.StandardItems
            .Concat(generated.NonStandardItems)
            .Concat(generated.UnclassifiedItems)
            .Where(item => item.SourceDocumentId.HasValue && !item.IsPendingRemoval)
            .OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) =>
            {
                var current = maintained.FirstOrDefault(candidate => SameBomSource(candidate, item));
                if (current is null)
                {
                    return item with
                    {
                        Sequence = index + 1,
                        IsComplete = item.Kind is BomKind.Standard or BomKind.NonStandard && HasRequiredBomValues(item, item.Kind, validationRules),
                        ReconciliationStatus = ReconcilePendingClassification,
                        ReconciliationNote = "尚未归入标准件或非标件BOM。",
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = now
                    };
                }

                var differences = SourceDataDifferences(current, item);
                return item with
                {
                    Id = current.Id,
                    Sequence = index + 1,
                    IsComplete = item.Kind is BomKind.Standard or BomKind.NonStandard && HasRequiredBomValues(item, item.Kind, validationRules),
                    ReconciliationStatus = differences.Count == 0 ? "SourceMatched" : "ManualOverrideMismatch",
                    ReconciliationNote = differences.Count == 0
                        ? "BOM维护值已与图档源数据一致。"
                        : $"BOM维护值与图档源数据不一致：{string.Join('、', differences)}。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> ResolveBomItemAsync(Guid projectId, Guid itemId, ResolveBomItemCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var item = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        var now = timeProvider.GetUtcNow();
        standard.RemoveAll(candidate => candidate.Id == itemId);
        nonStandard.RemoveAll(candidate => candidate.Id == itemId);
        unclassified.RemoveAll(candidate => candidate.Id == itemId);
        electrical.RemoveAll(candidate => candidate.Id == itemId);
        BomItem? resolved = null;
        switch (command.Action.Trim().ToLowerInvariant())
        {
            case "remove":
                break;
            case "retain":
                resolved = item with
                {
                    Source = "Manual",
                    IsPendingRemoval = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = true,
                    ReconciliationStatus = ReconcileManuallyRetained,
                    ReconciliationNote = $"最新图档源数据中无对应项，已由{actor}确认保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now
                };
                break;
            case "classify":
                if (command.TargetKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
                    throw new PdmRuleException("待分类物料只能归入标准件、非标件或电气BOM。");
                if (item.SourceDocumentId.HasValue && command.TargetKind == BomKind.Electrical)
                    throw new PdmRuleException("图档源数据只能归入标准件或非标件BOM；电气BOM独立维护。");
                resolved = item with
                {
                    Kind = command.TargetKind.Value,
                    IsManuallyOverridden = true,
                    IsPendingClassification = false,
                    IsPendingRemoval = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = false,
                    IsComplete = HasRequiredBomValues(item with { Kind = command.TargetKind.Value }, command.TargetKind.Value, validationRules),
                    ReconciliationStatus = ReconcileManuallyClassified,
                    ReconciliationNote = $"图档未提供有效分类，已由{actor}归入{BomKindLabel(command.TargetKind.Value)}BOM。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now,
                    PropertyWritebackStatus = item.SourceDocumentId.HasValue ? CadPropertyWritebackStatus.PendingSave : item.PropertyWritebackStatus
                };
                break;
            default:
                throw new PdmRuleException("不支持的BOM处理操作。");
        }
        if (resolved is not null)
        {
            if (resolved.Kind == BomKind.Standard) standard.Add(resolved);
            else if (resolved.Kind == BomKind.NonStandard) nonStandard.Add(resolved);
            else if (resolved.Kind == BomKind.Electrical) electrical.Add(resolved);
            else unclassified.Add(resolved);
        }
        static BomItem[] Resequence(IEnumerable<BomItem> items) => items.OrderBy(candidate => candidate.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((candidate, index) => candidate with { Sequence = index + 1 }).ToArray();
        await repository.ReplaceBomAsync(projectId, BomKind.Standard, Resequence(standard), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.NonStandard, Resequence(nonStandard), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.Unclassified, Resequence(unclassified), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.Electrical, Resequence(electrical), cancellationToken);
        foreach (var changedKind in new[] { item.Kind, resolved?.Kind }.Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind!.Value, actor, cancellationToken);
        var auditDetail = command.Action.Equals("remove", StringComparison.OrdinalIgnoreCase)
            ? $"确认删除：{item.DrawingNumber}；原状态：{item.ReconciliationStatus ?? "未记录"}"
            : $"{command.Action}:{command.TargetKind}；{resolved?.ReconciliationNote}";
        await AuditAsync(actor, "bom.reconcile", nameof(BomItem), itemId.ToString(), auditDetail, cancellationToken);
        return (await repository.GetBomAsync(projectId, resolved?.Kind ?? item.Kind, cancellationToken));
    }

    public async Task<IReadOnlyList<BomItem>> BatchUpdateBomItemsAsync(Guid projectId, BatchUpdateBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);

        var itemIds = command.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Length > 500) throw new PdmRuleException("单次最多批量编辑500条BOM物料。");
        var fields = command.Fields.Select(field => field.Trim()).Where(field => field.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fields.Count == 0) throw new PdmRuleException("请至少选择一个要批量修改的属性。");
        var allowedFields = new HashSet<string>(["kind", "unit", "drawingNumber", "name", "specification", "remark", "brand", "material", "surfaceTreatment", "weight", "quantity", "revision"], StringComparer.OrdinalIgnoreCase);
        var unsupported = fields.FirstOrDefault(field => !allowedFields.Contains(field));
        if (unsupported is not null) throw new PdmRuleException($"不支持批量修改属性：{unsupported}。");
        if (fields.Contains("kind") && command.TargetKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("物料分类只能批量改为标准件、非标件或电气件。");
        if (fields.Contains("drawingNumber") && itemIds.Length > 1)
            throw new PdmRuleException("物料编码具有唯一性，只能单条修改。");
        if (fields.Contains("quantity") && command.Quantity is null or <= 0)
            throw new PdmRuleException("数量必须大于0。");
        static string Required(string? value, string label)
        {
            value = value?.Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new PdmRuleException($"{label}不能为空。");
            return value;
        }
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        string RequiredIfSelected(string field, string? value, string label) => fields.Contains(field) ? Required(value, label) : string.Empty;
        if (fields.Contains("unit")) _ = U9UnitCatalog.Normalize(command.Unit);
        _ = RequiredIfSelected("drawingNumber", command.DrawingNumber, "物料编码");
        _ = RequiredIfSelected("name", command.Name, "物料名称");
        _ = RequiredIfSelected("revision", command.Revision, "版本");

        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var originals = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).Where(item => itemIds.Contains(item.Id)).ToDictionary(item => item.Id);
        if (originals.Count != itemIds.Length) throw new PdmNotFoundException("选中的BOM物料已变化，请刷新后重新选择。");
        if (originals.Values.Any(item => item.IsManuallyExcluded))
            throw new PdmRuleException("回收站中的物料不能直接编辑，请先执行恢复。");
        if (fields.Contains("kind") && command.TargetKind == BomKind.Electrical && originals.Values.Any(item => item.SourceDocumentId.HasValue))
            throw new PdmRuleException("图档源数据只能归入标准件或非标件BOM；电气BOM独立维护。");
        var updatedById = new Dictionary<Guid, BomItem>();
        var reconciliationTime = timeProvider.GetUtcNow();
        foreach (var itemId in itemIds)
        {
            var original = originals[itemId];
            var targetKind = fields.Contains("kind") ? command.TargetKind!.Value : original.Kind;
            var updated = original with
            {
                Kind = targetKind,
                Unit = fields.Contains("unit") ? U9UnitCatalog.Normalize(command.Unit) : original.Unit,
                DrawingNumber = fields.Contains("drawingNumber") ? Required(command.DrawingNumber, "物料编码") : original.DrawingNumber,
                Name = fields.Contains("name") ? Required(command.Name, "物料名称") : original.Name,
                Specification = fields.Contains("specification") ? Optional(command.Specification) : original.Specification,
                Remark = fields.Contains("remark") ? Optional(command.Remark) : original.Remark,
                Brand = fields.Contains("brand") ? Optional(command.Brand) : original.Brand,
                Material = fields.Contains("material") ? Optional(command.Material) : original.Material,
                SurfaceTreatment = fields.Contains("surfaceTreatment") ? Optional(command.SurfaceTreatment) : original.SurfaceTreatment,
                Weight = fields.Contains("weight") ? Optional(command.Weight) : original.Weight,
                Quantity = fields.Contains("quantity") ? command.Quantity!.Value : original.Quantity,
                Revision = fields.Contains("revision") ? Required(command.Revision, "版本") : original.Revision,
                IsManuallyOverridden = original.IsManuallyOverridden || original.Source == "Auto",
                IsPendingClassification = fields.Contains("kind") ? false : original.IsPendingClassification,
                IsPendingRemoval = fields.Contains("kind") ? false : original.IsPendingRemoval,
                IsManualUnmatched = fields.Contains("kind") ? false : original.IsManualUnmatched,
                IsManuallyRetained = fields.Contains("kind") ? false : original.IsManuallyRetained,
                IsManuallyExcluded = fields.Contains("kind") ? false : original.IsManuallyExcluded,
                ReconciliationStatus = fields.Contains("kind") ? ReconcileManuallyClassified : original.ReconciliationStatus,
                ReconciliationNote = fields.Contains("kind") ? $"已由{actor}人工归入{BomKindLabel(targetKind)}BOM。" : original.ReconciliationNote,
                ReconciliationUpdatedBy = fields.Contains("kind") ? actor : original.ReconciliationUpdatedBy,
                ReconciliationUpdatedAt = fields.Contains("kind") ? reconciliationTime : original.ReconciliationUpdatedAt
            };
            updated = updated with { IsComplete = HasRequiredBomValues(updated, targetKind, validationRules) };
            if (CadWritableValuesChanged(original, updated) && updated.SourceDocumentId.HasValue)
                updated = updated with { PropertyWritebackStatus = CadPropertyWritebackStatus.PendingSave };
            updatedById[itemId] = updated;
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken);
        if (snapshot is not null)
        {
            var raw = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
            var rawItems = raw.StandardItems.Concat(raw.NonStandardItems).Concat(raw.ElectricalItems).Concat(raw.UnclassifiedItems).ToArray();
            foreach (var (id, maintained) in updatedById.ToArray())
            {
                if (!maintained.SourceDocumentId.HasValue) continue;
                var source = rawItems.FirstOrDefault(candidate => candidate.SourceDocumentId == maintained.SourceDocumentId
                    && string.Equals(candidate.SourceConfiguration ?? string.Empty, maintained.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (source is null) continue;
                var differences = SourceDataDifferences(maintained, source);
                updatedById[id] = maintained with
                {
                    ReconciliationStatus = differences.Count > 0 ? "ManualOverrideMismatch" : "SourceMatched",
                    ReconciliationNote = differences.Count > 0
                        ? $"BOM维护值与最新图档源数据不一致：{string.Join('、', differences)}。"
                        : "BOM维护值已与最新图档源数据一致。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                };
            }
        }

        standard.RemoveAll(item => itemIds.Contains(item.Id));
        nonStandard.RemoveAll(item => itemIds.Contains(item.Id));
        unclassified.RemoveAll(item => itemIds.Contains(item.Id));
        electrical.RemoveAll(item => itemIds.Contains(item.Id));
        foreach (var item in updatedById.Values)
            if (item.Kind == BomKind.Standard) standard.Add(item);
            else if (item.Kind == BomKind.NonStandard) nonStandard.Add(item);
            else if (item.Kind == BomKind.Unclassified) unclassified.Add(item);
            else electrical.Add(item);
        static BomItem[] Resequence(IEnumerable<BomItem> items) => items.OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Sequence = index + 1 }).ToArray();
        var updatedStandard = Resequence(standard);
        var updatedNonStandard = Resequence(nonStandard);
        var updatedUnclassified = Resequence(unclassified);
        var updatedElectrical = Resequence(electrical);
        foreach (var group in updatedStandard.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
                     .Concat(updatedNonStandard.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase))
                     .Concat(updatedElectrical.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)))
            if (group.Count() > 1) throw new PdmRuleException($"BOM物料编码{group.Key}重复。");

        var now = timeProvider.GetUtcNow();
        var audits = new[]
        {
            new AuditEntry(Guid.NewGuid(), now, actor, "bom.batch-update", nameof(BomItem), projectId.ToString(), $"物料{itemIds.Length}条；属性{string.Join(',', fields.Order())}；待保存BOM")
        };
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, [], audits, cancellationToken);
        foreach (var changedKind in originals.Values.Select(item => item.Kind).Concat(updatedById.Values.Select(item => item.Kind))
                     .Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).Where(item => itemIds.Contains(item.Id)).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> RestoreBomItemsFromSourceAsync(Guid projectId, RestoreBomItemsFromSourceCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);

        var itemIds = command.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Length > 500) throw new PdmRuleException("单次最多恢复500条BOM物料。");
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var originals = standard.Concat(nonStandard).Where(item => itemIds.Contains(item.Id)).ToDictionary(item => item.Id);
        if (originals.Count != itemIds.Length) throw new PdmRuleException("只能恢复标准件BOM或非标件BOM中的物料。");
        if (originals.Values.Any(item => !item.SourceDocumentId.HasValue))
            throw new PdmRuleException("人工新增物料没有图档源数据，不能执行恢复源数据。");

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的设计树，不能恢复图档源数据。");
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
        var rawItems = generated.StandardItems.Concat(generated.NonStandardItems).Concat(generated.UnclassifiedItems).ToArray();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var now = timeProvider.GetUtcNow();
        var restoredById = new Dictionary<Guid, BomItem>();
        foreach (var itemId in itemIds)
        {
            var original = originals[itemId];
            var source = rawItems.FirstOrDefault(candidate => SameBomSource(candidate, original))
                ?? throw new PdmRuleException($"物料{original.DrawingNumber}在当前图档源数据中不存在，不能恢复。");
            var restored = original with
            {
                Unit = source.Unit,
                DrawingNumber = source.DrawingNumber,
                Name = source.Name,
                Specification = source.Specification,
                Remark = source.Remark,
                Brand = source.Brand,
                Material = source.Material,
                SurfaceTreatment = source.SurfaceTreatment,
                Weight = source.Weight,
                Quantity = source.Quantity,
                Revision = source.Revision,
                IsPendingRemoval = false,
                IsManualUnmatched = false,
                IsManuallyRetained = false,
                IsManuallyExcluded = false,
                PropertyWritebackStatus = null,
                ReconciliationUpdatedBy = actor,
                ReconciliationUpdatedAt = now
            };
            var differences = SourceDataDifferences(restored, source);
            restored = restored with
            {
                IsManuallyOverridden = differences.Count > 0,
                IsComplete = HasRequiredBomValues(restored, original.Kind, validationRules),
                ReconciliationStatus = differences.Count == 0 ? "SourceMatched" : "ManualOverrideMismatch",
                ReconciliationNote = differences.Count == 0
                    ? $"已由{actor}恢复为最新图档源数据；BOM分类与排序保持不变。"
                    : $"已由{actor}恢复图档属性；BOM分类与排序保持不变，仍与图档源数据不一致：{string.Join('、', differences)}。"
            };
            restoredById[itemId] = restored;
        }

        static BomItem[] Apply(IEnumerable<BomItem> items, IReadOnlyDictionary<Guid, BomItem> restored) =>
            items.Select(item => restored.TryGetValue(item.Id, out var replacement) ? replacement : item)
                .OrderBy(item => item.Sequence)
                .ToArray();
        var updatedStandard = Apply(standard, restoredById);
        var updatedNonStandard = Apply(nonStandard, restoredById);
        var updatedUnclassified = unclassified.OrderBy(item => item.Sequence).ToArray();
        var updatedElectrical = electrical.OrderBy(item => item.Sequence).ToArray();
        foreach (var group in updatedStandard.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
                     .Concat(updatedNonStandard.GroupBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)))
            if (group.Count() > 1) throw new PdmRuleException($"恢复后BOM物料编码{group.Key}重复，请先处理重复编码。");
        var audit = new AuditEntry(Guid.NewGuid(), now, actor, "bom.restore-source", nameof(BomItem), projectId.ToString(), $"恢复图档源数据{itemIds.Length}条；保留BOM分类与排序");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, [], [audit], cancellationToken);
        foreach (var changedKind in restoredById.Values.Select(item => item.Kind).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return restoredById.Values.ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> BatchDeleteBomItemsAsync(Guid projectId, BatchDeleteBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);

        var itemIds = command.ItemIds.Distinct().ToHashSet();
        if (itemIds.Count == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Count > 500) throw new PdmRuleException("单次最多删除500条BOM物料。");
        var reason = command.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw new PdmRuleException("删除原因不能为空。");
        if (reason.Length > 500) throw new PdmRuleException("删除原因不能超过500个字符。");
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var all = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).ToArray();
        var selected = all.Where(item => itemIds.Contains(item.Id)).ToArray();
        if (selected.Length != itemIds.Count) throw new PdmNotFoundException("选中的BOM物料已变化，请刷新后重新选择。");
        if (selected.Any(item => item.IsManuallyExcluded)) throw new PdmRuleException("选中的BOM物料已在回收站中，请刷新后重试。");

        var deletedAt = timeProvider.GetUtcNow();
        IReadOnlyList<BomItem> Apply(IEnumerable<BomItem> source)
        {
            return source.OrderBy(item => item.Sequence).Select(item => !itemIds.Contains(item.Id) ? item : item with
                {
                    IsManuallyExcluded = true,
                    IsPendingRemoval = false,
                    IsPendingClassification = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = false,
                    ReconciliationStatus = item.SourceDocumentId.HasValue ? ReconcileManuallyExcluded : ReconcileDeleted,
                    ReconciliationNote = $"已由{actor}移入回收站。原因：{reason}",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = deletedAt,
                    DeletedAt = deletedAt,
                    DeletedBy = actor,
                    DeleteReason = reason
                })
                .OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
                .Select((item, index) => item with { Sequence = index + 1 }).ToArray();
        }

        var updatedStandard = Apply(standard);
        var updatedNonStandard = Apply(nonStandard);
        var updatedUnclassified = Apply(unclassified);
        var updatedElectrical = Apply(electrical);
        var sourceCount = selected.Count(item => item.SourceDocumentId.HasValue);
        var manualCount = selected.Length - sourceCount;
        var removedDetails = string.Join('、', selected.Select(item => item.DrawingNumber));
        var audit = new AuditEntry(Guid.NewGuid(), deletedAt, actor, "bom.batch-delete", nameof(BomItem), projectId.ToString(), $"移入回收站{itemIds.Count}条；有源{sourceCount}条；人工{manualCount}条；原因：{reason}；物料：{removedDetails}");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, [], [audit], cancellationToken);
        foreach (var changedKind in selected.Select(item => item.Kind).Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> BatchRestoreBomItemsAsync(Guid projectId, BatchRestoreBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);

        var itemIds = command.ItemIds.Distinct().ToHashSet();
        if (itemIds.Count == 0) throw new PdmRuleException("请至少选择一条回收站物料。");
        if (itemIds.Count > 500) throw new PdmRuleException("单次最多恢复500条BOM物料。");
        var mode = command.Mode?.Trim() ?? "Original";
        if (mode is not ("Original" or "AsManual")) throw new PdmRuleException("恢复方式无效。");

        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var all = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).ToArray();
        var selected = all.Where(item => itemIds.Contains(item.Id)).ToArray();
        if (selected.Length != itemIds.Count) throw new PdmNotFoundException("选中的回收站物料已变化，请刷新后重新选择。");
        if (selected.Any(item => !item.IsManuallyExcluded)) throw new PdmRuleException("选中的物料不在回收站中，请刷新后重试。");
        if (mode == "AsManual" && selected.Any(item => !item.SourceDocumentId.HasValue))
            throw new PdmRuleException("“转人工恢复”只适用于有图档来源的物料。");

        if (mode == "Original")
        {
            foreach (var item in selected.Where(item => item.SourceDocumentId.HasValue))
                if (await repository.FindDocumentAsync(item.SourceDocumentId!.Value, cancellationToken) is null)
                    throw new PdmRuleException($"物料“{item.DrawingNumber}”的源图档已不存在，请改用“转人工恢复”。");
        }

        var active = all.Where(item => !item.IsManuallyExcluded && !itemIds.Contains(item.Id)).ToArray();
        var selectedSourceConflict = selected.Where(item => mode == "Original" && item.SourceDocumentId.HasValue)
            .GroupBy(item => new { item.SourceDocumentId, Configuration = (item.SourceConfiguration ?? string.Empty).ToUpperInvariant() })
            .FirstOrDefault(group => group.Count() > 1);
        var selectedDrawingConflict = selected.Where(item => mode == "AsManual" || !item.SourceDocumentId.HasValue)
            .GroupBy(item => item.DrawingNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (selectedSourceConflict is not null || selectedDrawingConflict is not null)
            throw new PdmConflictException("选中的回收站物料之间存在重复编码或相同源关系，不能批量恢复。");
        foreach (var item in selected)
        {
            var sourceConflict = mode == "Original" && item.SourceDocumentId.HasValue && active.Any(candidate =>
                candidate.SourceDocumentId == item.SourceDocumentId
                && string.Equals(candidate.SourceConfiguration ?? string.Empty, item.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            var drawingConflict = (mode == "AsManual" || !item.SourceDocumentId.HasValue) && active.Any(candidate =>
                string.Equals(candidate.DrawingNumber.Trim(), item.DrawingNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sourceConflict || drawingConflict)
                throw new PdmConflictException($"物料“{item.DrawingNumber}”与当前BOM中的有效物料重复，不能恢复。");
        }

        var restoredAt = timeProvider.GetUtcNow();
        IReadOnlyList<BomItem> Apply(IEnumerable<BomItem> source) => source.Select(item => !itemIds.Contains(item.Id) ? item : item with
            {
                SourceDocumentId = mode == "AsManual" ? null : item.SourceDocumentId,
                SourceConfiguration = mode == "AsManual" ? null : item.SourceConfiguration,
                Source = mode == "AsManual" ? "Manual" : item.Source,
                IsManuallyOverridden = mode == "AsManual" || item.IsManuallyOverridden,
                IsManuallyExcluded = false,
                IsPendingRemoval = false,
                IsPendingClassification = false,
                IsManualUnmatched = false,
                IsManuallyRetained = false,
                ReconciliationStatus = ReconcileRestored,
                ReconciliationNote = mode == "AsManual" ? $"源图档关系已解除，由{actor}转为人工物料恢复。" : $"已由{actor}从回收站恢复。",
                ReconciliationUpdatedBy = actor,
                ReconciliationUpdatedAt = restoredAt,
                DeletedAt = null,
                DeletedBy = null,
                DeleteReason = null
            })
            .OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
            .Select((item, index) => item with { Sequence = index + 1 }).ToArray();

        var updatedStandard = Apply(standard);
        var updatedNonStandard = Apply(nonStandard);
        var updatedUnclassified = Apply(unclassified);
        var updatedElectrical = Apply(electrical);
        var audit = new AuditEntry(Guid.NewGuid(), restoredAt, actor, "bom.batch-restore", nameof(BomItem), projectId.ToString(), $"恢复{itemIds.Count}条；方式：{mode}；物料：{string.Join('、', selected.Select(item => item.DrawingNumber))}");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, [], [audit], cancellationToken);
        foreach (var changedKind in selected.Select(item => item.Kind).Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).ToArray();
    }

    public async Task<IReadOnlyList<CadPropertyWriteback>> ListCadPropertyWritebacksAsync(Guid projectId, bool activeOnly, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        var items = await repository.ListCadPropertyWritebacksAsync(projectId, cancellationToken);
        return activeOnly ? items.Where(item => item.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress).ToArray() : items;
    }

    public async Task<CadPropertyWriteback> StartCadPropertyWritebackAsync(Guid id, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        if (request.Status != CadPropertyWritebackStatus.Pending) throw new PdmConflictException("属性写回任务已被处理，请刷新后重试。");
        var latest = (await repository.ListDocumentVersionsAsync(request.SourceDocumentId, cancellationToken)).FirstOrDefault();
        if (latest?.Id != request.ExpectedVersionId)
        {
            await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.Conflict, null, "图档已产生新版本，请在客户端重新保存BOM后再写回。", cancellationToken);
            throw new PdmConflictException("图档版本已变化，属性写回已转为冲突状态。");
        }
        return await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.InProgress, null, null, cancellationToken);
    }

    public async Task<CadPropertyWriteback> CompleteCadPropertyWritebackAsync(Guid id, Guid resultVersionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        if (request.Status != CadPropertyWritebackStatus.InProgress) throw new PdmConflictException("属性写回任务不在执行中。");
        var result = await repository.FindDocumentVersionAsync(request.SourceDocumentId, resultVersionId, cancellationToken)
            ?? throw new PdmRuleException("写回结果版本不存在。");
        var updated = await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.Succeeded, result.Id, null, cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.complete", nameof(CadPropertyWriteback), id.ToString(), result.Revision.Display, cancellationToken);
        return updated;
    }

    public async Task<CadPropertyWriteback> FailCadPropertyWritebackAsync(Guid id, string error, bool conflict, string actor, UserRole role, CancellationToken cancellationToken)
    {
        _ = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        var status = conflict ? CadPropertyWritebackStatus.Conflict : CadPropertyWritebackStatus.Failed;
        var updated = await repository.UpdateCadPropertyWritebackAsync(id, status, null, RequiredReason(error), cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.fail", nameof(CadPropertyWriteback), id.ToString(), $"{status}:{error}", cancellationToken);
        return updated;
    }

    public async Task<BomEmptyDeclaration> SetBomEmptyDeclarationAsync(Guid projectId, BomKind kind, bool declaredEmpty, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("BOM类型必须是标准件、非标件或电气。");
        if (declaredEmpty)
        {
            var items = await repository.GetBomAsync(projectId, kind, cancellationToken);
            if (items.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) throw new PdmRuleException("当前分类仍有有效物料，不能声明为空。");
        }
        var result = await repository.SetBomEmptyDeclarationAsync(projectId, kind, declaredEmpty, actor, cancellationToken);
        await SyncBomDraftAsync(projectId, kind, actor, cancellationToken);
        await AuditAsync(actor, "bom.empty-declaration", nameof(BomItem), projectId.ToString(), $"{kind}:{declaredEmpty}", cancellationToken);
        return result;
    }

    public async Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var standard = (await repository.GetBomAsync(package.ProjectId, BomKind.Standard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var nonStandard = (await repository.GetBomAsync(package.ProjectId, BomKind.NonStandard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var electrical = (await repository.GetBomAsync(package.ProjectId, BomKind.Electrical, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var unclassified = await repository.GetBomAsync(package.ProjectId, BomKind.Unclassified, cancellationToken);
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        if (unclassified.Any(item => !item.IsManuallyExcluded)
            || !BomReady(BomKind.Standard, standard, validationRules)
            || !BomReady(BomKind.NonStandard, nonStandard, validationRules)
            || !BomReady(BomKind.Electrical, electrical, validationRules))
        {
            var missing = new[]
            {
                MissingBomSummary(BomKind.Standard, standard, validationRules),
                MissingBomSummary(BomKind.NonStandard, nonStandard, validationRules),
                MissingBomSummary(BomKind.Electrical, electrical, validationRules)
            }.Where(summary => summary is not null).ToArray();
            var detail = missing.Length == 0 ? string.Empty : $" 缺项：{string.Join("；", missing)}。";
            throw new PdmRuleException($"三个独立BOM中仍有待处理或资料不完整的物料，不能提交设变审批。{detail}");
        }
        var changeNumber = package.ChangeNumber ?? package.Number;
        var changeReason = package.ChangeReason ?? "兼容既有发布流程创建的设变";
        var effectiveSerialFrom = package.EffectiveSerialFrom ?? project.SerialNumbers.FirstOrDefault() ?? "未指定";
        var standardVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.Standard, standard, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
        var nonStandardVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.NonStandard, nonStandard, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
        var electricalVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.Electrical, electrical, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
        package = await repository.UpdateReleasePackageBomVersionsAsync(package.Id, standardVersion, nonStandardVersion, electricalVersion, cancellationToken);
        await publisher.PrepareAsync(package, project, cancellationToken);
        await publisher.ValidateAsync(package, project, cancellationToken);
        var submitted = await repository.SubmitReleasePackageAsync(releasePackageId, actor, cancellationToken);
        var drafts = new[] { standardVersion, nonStandardVersion, electricalVersion }.Where(version => version.State == BomVersionState.Draft).Select(version => version.Id).ToArray();
        await repository.SetBomVersionStateAsync(drafts, BomVersionState.InReview, actor, null, cancellationToken);
        await AuditAsync(actor, package.State == ReleasePackageState.Rejected ? "release-package.resubmit" : "release-package.submit", nameof(ReleasePackage), package.Id.ToString(), package.Number, cancellationToken);
        return submitted;
    }

    public async Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        comment = RequiredComment(comment, "撤回原因");
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var withdrawn = await repository.WithdrawReleasePackageAsync(releasePackageId, actor, cancellationToken);
        await repository.SetBomVersionStateAsync(await PackageBomVersionIdsInStateAsync(package, BomVersionState.InReview, cancellationToken), BomVersionState.Draft, actor, null, cancellationToken);
        await AuditAsync(actor, "release-package.withdraw", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；{comment}", cancellationToken);
        return withdrawn;
    }

    public async Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
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
        await RequirePermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken);

        var package = await repository.DecideApprovalAsync(taskId, actor, decision, comment, cancellationToken);
        await AuditAsync(actor, "approval.decide", nameof(ApprovalTask), taskId.ToString(), decision.ToString(), cancellationToken);

        if (package.State != ReleasePackageState.Publishing)
        {
            if (package.State == ReleasePackageState.Rejected)
                await repository.SetBomVersionStateAsync(await PackageBomVersionIdsInStateAsync(package, BomVersionState.InReview, cancellationToken), BomVersionState.Draft, actor, null, cancellationToken);
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
        var baseline = await repository.MarkPublishedWithBomBaselineAsync(package, publishedPath, publishedAt, actor, cancellationToken);
        await AuditAsync(actor, "bom-baseline.publish", nameof(ManufacturingBomBaseline), baseline.Id.ToString(), $"{baseline.Label}:{baseline.ChangeNumber}:{baseline.EffectiveSerialFrom}-{baseline.EffectiveSerialTo ?? "以后"}", cancellationToken);
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

    private async Task<BomGenerationResult> GenerateMechanicalBomFromSnapshotAsync(
        Guid projectId,
        CadReferenceSnapshot snapshot,
        string actor,
        CancellationToken cancellationToken,
        bool apply,
        bool preserveManualOverrides = true)
    {
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var documents = (await repository.ListDocumentsAsync(projectId, cancellationToken)).ToDictionary(item => item.Id);
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToArray();
        var existing = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken))
            .ToArray();
        var sourceCache = new Dictionary<Guid, (PdmDocument Document, DocumentVersion? Version)>();
        var candidates = new List<BomItem>();
        var virtualCount = 0;
        var reconciliationTime = timeProvider.GetUtcNow();

        async Task<(PdmDocument Document, DocumentVersion? Version)?> SourceAsync(Guid? documentId)
        {
            if (!documentId.HasValue || !documents.TryGetValue(documentId.Value, out var document)) return null;
            if (sourceCache.TryGetValue(documentId.Value, out var cached)) return cached;
            var latest = (await repository.ListDocumentVersionsAsync(documentId.Value, cancellationToken)).FirstOrDefault();
            var result = (document, latest);
            sourceCache[documentId.Value] = result;
            return result;
        }

        async Task VisitAsync(DocumentReferenceNode node, decimal parentQuantity, bool isRoot)
        {
            if (node.Status == ReferenceNodeStatus.Suppressed || node.Status == ReferenceNodeStatus.Missing) return;
            var quantity = parentQuantity * Math.Max(node.Quantity, 1);
            if (node.Kind == DocumentKind.Drawing) return;

            var source = await SourceAsync(node.DocumentId);
            var properties = source?.Version?.PropertySnapshot;
            var classificationProperty = BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类");
            var classification = PropertyValue(properties, node.Configuration, classificationProperty);
            if (node.Status == ReferenceNodeStatus.Virtual || string.Equals(classification, "虚拟件", StringComparison.OrdinalIgnoreCase))
            {
                virtualCount++;
                foreach (var child in node.Children) await VisitAsync(child, quantity, false);
                return;
            }

            var kind = string.Equals(classification, "标准件", StringComparison.OrdinalIgnoreCase) ? BomKind.Standard
                : string.Equals(classification, "非标件", StringComparison.OrdinalIgnoreCase) ? BomKind.NonStandard
                : (BomKind?)null;

            var includeCurrent = !isRoot && source.HasValue;
            var purchasedAssembly = !isRoot && source.HasValue && node.Kind == DocumentKind.Assembly && kind == BomKind.Standard;
            if (includeCurrent)
            {
                var (document, version) = source!.Value;
                var previous = existing.FirstOrDefault(candidate => candidate.SourceDocumentId == document.Id
                    && string.Equals(candidate.SourceConfiguration ?? string.Empty, node.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                var hasManualClassification = previous?.IsManuallyOverridden == true
                    && !previous.IsPendingClassification
                    && previous.Kind is BomKind.Standard or BomKind.NonStandard;
                var resolvedKind = preserveManualOverrides && hasManualClassification
                    ? previous!.Kind
                    : kind ?? BomKind.Unclassified;
                var drawingNumber = PropertyValue(properties, node.Configuration, settings.BomDrawingNumberProperty) ?? document.DrawingNumber;
                var name = PropertyValue(properties, node.Configuration, settings.BomNameProperty) ?? document.Name;
                var remark = PropertyValue(properties, node.Configuration, settings.BomDescriptionProperty);
                var brand = PropertyValue(properties, node.Configuration, settings.BomBrandProperty);
                var material = PropertyValue(properties, node.Configuration, settings.BomMaterialProperty);
                var specification = PropertyValue(properties, node.Configuration, settings.BomSpecificationProperty);
                var unit = PropertyValue(properties, node.Configuration, settings.BomUnitProperty) ?? "个";
                var surfaceTreatment = PropertyValue(properties, node.Configuration, settings.BomSurfaceTreatmentProperty);
                var weight = PropertyValue(properties, node.Configuration, settings.BomWeightProperty);
                var revision = document.Revision.Display;
                var reconciliationStatus = previous?.ReconciliationStatus;
                var reconciliationNote = previous?.ReconciliationNote;
                var reconciliationUpdatedBy = previous?.ReconciliationUpdatedBy;
                var reconciliationUpdatedAt = previous?.ReconciliationUpdatedAt;
                if (!kind.HasValue && !(preserveManualOverrides && hasManualClassification))
                {
                    reconciliationStatus = ReconcilePendingClassification;
                    reconciliationNote = "图档源数据未填写有效的物料分类，等待人工归入标准件或非标件BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (!kind.HasValue)
                {
                    if (!string.Equals(reconciliationStatus, ReconcileManuallyClassified, StringComparison.Ordinal))
                    {
                        reconciliationStatus = ReconcileManuallyClassified;
                        reconciliationNote = $"图档源数据未填写有效分类，继续沿用人工分类：{BomKindLabel(resolvedKind)}。";
                        reconciliationUpdatedBy = actor;
                        reconciliationUpdatedAt = reconciliationTime;
                    }
                }
                else if (previous is null)
                {
                    reconciliationStatus = ReconcileAutoAdded;
                    reconciliationNote = $"图档源数据新增，已根据物料分类自动进入{BomKindLabel(resolvedKind)}BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (previous.IsPendingRemoval || previous.IsManualUnmatched)
                {
                    reconciliationStatus = ReconcileRestored;
                    reconciliationNote = $"图档源数据中已重新出现，已恢复到{BomKindLabel(resolvedKind)}BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (previous.IsPendingClassification || previous.Kind != resolvedKind)
                {
                    reconciliationStatus = ReconcileClassificationChanged;
                    reconciliationNote = previous.IsPendingClassification
                        ? $"图档源数据已补充分类，已自动进入{BomKindLabel(resolvedKind)}BOM。"
                        : $"图档源数据分类由{BomKindLabel(previous.Kind)}变更为{BomKindLabel(resolvedKind)}，已自动迁移。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                var candidate = new BomItem(Guid.NewGuid(), projectId, resolvedKind, 0, drawingNumber.Trim(), name.Trim(), quantity, U9UnitCatalog.NormalizeBomUnit(unit), NullIfWhiteSpace(material), NullIfWhiteSpace(specification), revision, false)
                {
                    Remark = NullIfWhiteSpace(remark),
                    Brand = NullIfWhiteSpace(brand),
                    SurfaceTreatment = NullIfWhiteSpace(surfaceTreatment),
                    Weight = NullIfWhiteSpace(weight),
                    SourceDocumentId = document.Id,
                    SourceConfiguration = NullIfWhiteSpace(node.Configuration),
                    Source = "Auto",
                    IsPendingClassification = !kind.HasValue && !(preserveManualOverrides && hasManualClassification),
                    ReconciliationStatus = reconciliationStatus,
                    ReconciliationNote = reconciliationNote,
                    ReconciliationUpdatedBy = reconciliationUpdatedBy,
                    ReconciliationUpdatedAt = reconciliationUpdatedAt
                };
                if (preserveManualOverrides && previous?.IsManuallyOverridden == true)
                {
                    var differences = SourceDataDifferences(previous, candidate).ToList();
                    var sourceKind = kind ?? BomKind.Unclassified;
                    if (previous.Kind != sourceKind && !differences.Contains("物料分类", StringComparer.Ordinal))
                        differences.Insert(0, "物料分类");
                    if (differences.Count > 0)
                    {
                        candidate = candidate with
                        {
                            ReconciliationStatus = "ManualOverrideMismatch",
                            ReconciliationNote = $"BOM维护值与最新图档源数据不一致：{string.Join('、', differences)}。",
                            ReconciliationUpdatedBy = actor,
                            ReconciliationUpdatedAt = reconciliationTime
                        };
                    }
                    else if (string.Equals(previous.ReconciliationStatus, "ManualOverrideMismatch", StringComparison.Ordinal))
                    {
                        candidate = candidate with
                        {
                            ReconciliationStatus = "SourceMatched",
                            ReconciliationNote = "BOM维护值已与最新图档源数据一致。",
                            ReconciliationUpdatedBy = actor,
                            ReconciliationUpdatedAt = reconciliationTime
                        };
                    }
                }
                candidates.Add(candidate with
                {
                    IsComplete = (kind.HasValue || preserveManualOverrides && hasManualClassification)
                        && HasRequiredBomValues(candidate, resolvedKind, settings.ValidationRules)
                });
            }

            if (!purchasedAssembly)
                foreach (var child in node.Children) await VisitAsync(child, quantity, false);
        }

        await VisitAsync(snapshot.Root, 1, true);
        var generated = candidates
            .GroupBy(item => new { item.Kind, item.SourceDocumentId, Configuration = item.SourceConfiguration ?? string.Empty })
            .Select(group => group.First() with { Quantity = group.Sum(item => item.Quantity) })
            .ToArray();
        var matchedIds = new HashSet<Guid>();
        var merged = new List<BomItem>();
        foreach (var item in generated)
        {
            var previous = existing.FirstOrDefault(candidate => candidate.SourceDocumentId == item.SourceDocumentId
                && string.Equals(candidate.SourceConfiguration ?? string.Empty, item.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (previous is not null) matchedIds.Add(previous.Id);
            merged.Add(preserveManualOverrides && previous?.IsManuallyOverridden == true
                ? previous with
                {
                    Kind = item.Kind,
                    Quantity = item.Quantity,
                    Revision = item.Revision,
                    IsPendingRemoval = false,
                    IsPendingClassification = item.IsPendingClassification,
                    IsManualUnmatched = false,
                    ReconciliationStatus = item.ReconciliationStatus,
                    ReconciliationNote = item.ReconciliationNote,
                    ReconciliationUpdatedBy = item.ReconciliationUpdatedBy,
                    ReconciliationUpdatedAt = item.ReconciliationUpdatedAt
                }
                : item with
                {
                    Id = previous?.Id ?? item.Id,
                    IsManuallyOverridden = preserveManualOverrides && previous?.IsManuallyOverridden == true,
                    IsManuallyExcluded = preserveManualOverrides && previous?.IsManuallyExcluded == true,
                    DeletedAt = preserveManualOverrides ? previous?.DeletedAt : null,
                    DeletedBy = preserveManualOverrides ? previous?.DeletedBy : null,
                    DeleteReason = preserveManualOverrides ? previous?.DeleteReason : null,
                    PropertyWritebackStatus = preserveManualOverrides ? previous?.PropertyWritebackStatus : null
                });
        }
        merged.AddRange(existing.Where(item => !matchedIds.Contains(item.Id)).Select(item =>
            item.Source == "Auto"
                ? item with
                {
                    IsPendingRemoval = true,
                    IsComplete = false,
                    ReconciliationStatus = ReconcilePendingRemoval,
                    ReconciliationNote = "最新图档源数据中已不存在，等待确认删除或人工保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                }
                : item.IsManuallyRetained ? item : item with
                {
                    IsManualUnmatched = true,
                    IsComplete = false,
                    ReconciliationStatus = ReconcileManualUnmatched,
                    ReconciliationNote = "BOM中存在，但最新图档源数据中无对应项，等待确认删除或人工保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                }));
        var unclassifiedCount = merged.Count(item => item.IsPendingClassification && !item.IsManuallyExcluded);

        static BomItem[] PrepareKind(IEnumerable<BomItem> items, BomKind kind) => items
            .Where(item => item.Kind == kind)
            .OrderBy(item => item.IsManuallyExcluded)
            .ThenBy(item => item.IsPendingRemoval)
            .ThenBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();

        var standard = PrepareKind(merged, BomKind.Standard);
        var nonStandard = PrepareKind(merged, BomKind.NonStandard);
        var unclassified = PrepareKind(merged, BomKind.Unclassified);
        var pendingRemovalCount = merged.Count(item => item.IsPendingRemoval && !item.IsManuallyExcluded);
        var manualUnmatchedCount = merged.Count(item => item.IsManualUnmatched && !item.IsManuallyExcluded);
        if (apply)
        {
            await repository.ReplaceBomAsync(projectId, BomKind.Standard, standard, cancellationToken);
            await repository.ReplaceBomAsync(projectId, BomKind.NonStandard, nonStandard, cancellationToken);
            await repository.ReplaceBomAsync(projectId, BomKind.Unclassified, unclassified, cancellationToken);
            await repository.SaveBomDraftAsync(projectId, BomKind.Standard, standard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
            await repository.SaveBomDraftAsync(projectId, BomKind.NonStandard, nonStandard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
            if (standard.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) await repository.SetBomEmptyDeclarationAsync(projectId, BomKind.Standard, false, actor, cancellationToken);
            if (nonStandard.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) await repository.SetBomEmptyDeclarationAsync(projectId, BomKind.NonStandard, false, actor, cancellationToken);
            await AuditAsync(actor, "bom.generate", nameof(BomItem), projectId.ToString(), $"标准件{standard.Length}；非标件{nonStandard.Length}；电气BOM独立维护；虚拟件{virtualCount}；待分类{unclassifiedCount}；待移除{pendingRemovalCount}；人工待确认{manualUnmatchedCount}", cancellationToken);
        }
        return new BomGenerationResult(standard, nonStandard, electrical, unclassified, virtualCount, unclassifiedCount, pendingRemovalCount, manualUnmatchedCount, apply);
    }

    private static IReadOnlyList<string> SourceDataDifferences(BomItem maintained, BomItem source)
    {
        var differences = new List<string>();
        static bool Different(string? left, string? right) => !string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (maintained.Kind != source.Kind) differences.Add("物料分类");
        if (Different(maintained.Unit, source.Unit)) differences.Add("单位");
        if (Different(maintained.DrawingNumber, source.DrawingNumber)) differences.Add("物料编码");
        if (Different(maintained.Name, source.Name)) differences.Add("物料名称");
        if (Different(maintained.Specification, source.Specification)) differences.Add("型号");
        if (Different(maintained.Remark, source.Remark)) differences.Add("备注信息");
        if (Different(maintained.Brand, source.Brand)) differences.Add("品牌");
        if (Different(maintained.Material, source.Material)) differences.Add("材质");
        if (Different(maintained.SurfaceTreatment, source.SurfaceTreatment)) differences.Add("表面处理");
        if (Different(maintained.Weight, source.Weight)) differences.Add("重量");
        if (maintained.Quantity != source.Quantity) differences.Add("数量");
        if (Different(maintained.Revision, source.Revision)) differences.Add("版本");
        return differences;
    }

    private static bool SameBomSource(BomItem left, BomItem right) =>
        left.SourceDocumentId.HasValue
        && left.SourceDocumentId == right.SourceDocumentId
        && string.Equals(left.SourceConfiguration ?? string.Empty, right.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string? PropertyValue(IReadOnlyDictionary<string, string?>? properties, string configuration, string propertyName)
    {
        if (properties is null || string.IsNullOrWhiteSpace(propertyName)) return null;
        var names = new[]
        {
            $"配置:{configuration}/{propertyName.Trim()}",
            $"全局/{propertyName.Trim()}",
            propertyName.Trim()
        };
        foreach (var name in names)
            if (properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static bool HasRequiredBomValues(BomItem item, BomKind kind, BomValidationRules validationRules) =>
        kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical
        && BomValidationFieldCatalog.MissingFields(item, validationRules.RequiredFields(kind)).Count == 0;

    private static string BomKindLabel(BomKind kind) => kind switch
    {
        BomKind.Standard => "标准件",
        BomKind.NonStandard => "非标件",
        BomKind.Unclassified => "待分类",
        BomKind.Electrical => "电气",
        _ => kind.ToString()
    };

    private static bool CadWritableValuesChanged(BomItem? previous, BomItem current)
    {
        if (previous is null) return current.SourceDocumentId.HasValue;
        static bool Different(string? left, string? right) => !string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);
        return previous.Kind != current.Kind
            || Different(previous.DrawingNumber, current.DrawingNumber)
            || Different(previous.Name, current.Name)
            || Different(previous.Unit, current.Unit)
            || Different(previous.Specification, current.Specification)
            || Different(previous.Remark, current.Remark)
            || Different(previous.Brand, current.Brand)
            || Different(previous.Material, current.Material)
            || Different(previous.SurfaceTreatment, current.SurfaceTreatment)
            || Different(previous.Weight, current.Weight);
    }

    private async Task<CadPropertyWriteback?> EnqueueCadPropertyWritebackAsync(BomItem item, string actor, CancellationToken cancellationToken)
    {
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var request = await CreateCadPropertyWritebackAsync(item, actor, settings, cancellationToken);
        if (request is null) return null;
        var saved = await repository.EnqueueCadPropertyWritebackAsync(request, cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.enqueue", nameof(CadPropertyWriteback), saved.Id.ToString(), $"{item.DrawingNumber}:{saved.ExpectedRevision}", cancellationToken);
        return saved;
    }

    private async Task<CadPropertyWriteback?> CreateCadPropertyWritebackAsync(BomItem item, string actor, PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        if (!item.SourceDocumentId.HasValue || item.Kind == BomKind.Electrical) return null;
        var latest = (await repository.ListDocumentVersionsAsync(item.SourceDocumentId.Value, cancellationToken)).FirstOrDefault();
        if (latest is null) return null;
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Set(string propertyName, string? value)
        {
            if (!string.IsNullOrWhiteSpace(propertyName)) properties[propertyName.Trim()] = value?.Trim();
        }
        if (item.Kind is BomKind.Standard or BomKind.NonStandard)
            Set(BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类"), item.Kind == BomKind.Standard ? "标准件" : "非标件");
        Set(settings.BomUnitProperty, item.Unit);
        Set(settings.BomDrawingNumberProperty, item.DrawingNumber);
        Set(settings.BomNameProperty, item.Name);
        Set(settings.BomSpecificationProperty, item.Specification);
        Set(settings.BomDescriptionProperty, item.Remark);
        Set(settings.BomBrandProperty, item.Brand);
        Set(settings.BomMaterialProperty, item.Material);
        Set(settings.BomSurfaceTreatmentProperty, item.SurfaceTreatment);
        Set(settings.BomWeightProperty, item.Weight);
        return new CadPropertyWriteback(
            Guid.NewGuid(), item.ProjectId, item.Id, item.SourceDocumentId.Value, item.SourceConfiguration,
            latest.Id, latest.Revision.Display, properties, CadPropertyWritebackStatus.Pending, actor, timeProvider.GetUtcNow());
    }

    private async Task<CadPropertyWriteback> RequireCadPropertyWritebackAccessAsync(Guid id, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await repository.FindCadPropertyWritebackAsync(id, cancellationToken)
            ?? throw new PdmNotFoundException("属性写回任务不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(request.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return request;
    }

    private static bool BomReady(BomKind kind, IReadOnlyList<BomItem> items, BomValidationRules validationRules)
    {
        var included = items.Where(item => !item.IsManuallyExcluded).ToArray();
        var active = included.Where(item => !item.IsPendingRemoval).ToArray();
        if (included.Any(item => item.IsPendingRemoval || item.IsPendingClassification || item.IsManualUnmatched)) return false;
        if (active.Any(item => item.PropertyWritebackStatus is CadPropertyWritebackStatus.PendingSave or CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress or CadPropertyWritebackStatus.Conflict or CadPropertyWritebackStatus.Failed)) return false;
        return active.Length == 0 || active.All(item => HasRequiredBomValues(item, kind, validationRules));
    }

    private static string? MissingBomSummary(BomKind kind, IReadOnlyList<BomItem> items, BomValidationRules validationRules)
    {
        var missing = items.Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval)
            .SelectMany(item => BomValidationFieldCatalog.MissingFields(item, validationRules.RequiredFields(kind)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(BomValidationFieldCatalog.Label)
            .ToArray();
        return missing.Length == 0 ? null : $"{BomKindLabel(kind)}BOM缺少{string.Join('、', missing)}";
    }

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

    private async Task<RolePermissionSettings> FindRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        roleCode = roleCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(roleCode)) throw new PdmRuleException("请选择系统角色。");
        return (await repository.GetRolePermissionDirectoryAsync(cancellationToken)).Roles
            .SingleOrDefault(item => string.Equals(item.Role, roleCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmRuleException("所选系统角色不存在。");
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken))
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

    private async Task EnsureBomChangeAllowedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var active = (await repository.ListReleasePackagesAsync(projectId, cancellationToken)).FirstOrDefault(package =>
            package.State is (ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing)
            && PackageBomVersionIds(package).Length == 3);
        if (active is not null)
            throw new PdmConflictException($"设变{active.ChangeNumber ?? active.Number}正在审批或发布，三个BOM已锁定；请先完成、驳回或撤回该设变。");
    }

    private static Guid[] PackageBomVersionIds(ReleasePackage package) =>
        new[] { package.StandardBomVersionId, package.NonStandardBomVersionId, package.ElectricalBomVersionId }
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();

    private async Task<Guid[]> PackageBomVersionIdsInStateAsync(ReleasePackage package, BomVersionState state, CancellationToken cancellationToken)
    {
        var ids = PackageBomVersionIds(package).ToHashSet();
        if (ids.Count == 0) return [];
        return (await repository.ListBomVersionsAsync(package.ProjectId, null, cancellationToken))
            .Where(version => ids.Contains(version.Id) && version.State == state).Select(version => version.Id).ToArray();
    }

    private async Task SyncBomDraftAsync(Guid projectId, BomKind kind, string actor, CancellationToken cancellationToken)
    {
        var items = (await repository.GetBomAsync(projectId, kind, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        await repository.SaveBomDraftAsync(projectId, kind, items, actor, cancellationToken);
    }

    private async Task<BomVersion> ResolveBomVersionForReleaseAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItem> currentItems,
        string actor,
        string changeNumber,
        string changeReason,
        string effectiveSerialFrom,
        string? effectiveSerialTo,
        BomValidationRules validationRules,
        CancellationToken cancellationToken)
    {
        var versions = await repository.ListBomVersionsAsync(projectId, kind, cancellationToken);
        var draft = versions.FirstOrDefault(version => version.State == BomVersionState.Draft);
        var latestReleased = versions.FirstOrDefault(version => version.State == BomVersionState.Released);
        BomVersion selected;
        if (draft is not null)
        {
            selected = await repository.SaveBomDraftAsync(projectId, kind, currentItems, actor, cancellationToken);
        }
        else if (latestReleased is not null && BomSnapshotsEqual(latestReleased.Items, currentItems))
        {
            return latestReleased;
        }
        else
        {
            selected = await repository.SaveBomDraftAsync(projectId, kind, currentItems, actor, cancellationToken);
        }
        return await repository.UpdateBomVersionReleaseInfoAsync(
            selected.Id,
            changeNumber,
            changeReason,
            effectiveSerialFrom,
            effectiveSerialTo,
            validationRules.RequiredFields(kind),
            cancellationToken);
    }

    private static bool BomSnapshotsEqual(IReadOnlyList<BomItem> left, IReadOnlyList<BomItem> right) =>
        string.Equals(BomRevision("V", left), BomRevision("V", right), StringComparison.Ordinal);

    private static string BomRevision(string prefix, IReadOnlyList<BomItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8]}";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeUsername(string? value)
    {
        var username = value?.Trim() ?? string.Empty;
        if (username.Length is < 2 or > 100 || username.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_' or '@')))
            throw new PdmRuleException("账号必须为2到100位，只能包含字母、数字、点、短横线、下划线和@。");
        return username;
    }

    private static string NormalizeDisplayName(string? value)
    {
        var displayName = value?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 100) throw new PdmRuleException("姓名不能为空且不能超过100个字符。");
        return displayName;
    }

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

    private static int GetOrganizationUnitDepth(IReadOnlyList<OrganizationUnit> units, Guid unitId)
    {
        var depth = 0;
        var current = units.SingleOrDefault(unit => unit.Id == unitId);
        while (current is not null)
        {
            depth++;
            current = current.ParentUnitId is null ? null : units.SingleOrDefault(unit => unit.Id == current.ParentUnitId);
        }
        return depth;
    }

    private static int GetOrganizationSubtreeHeight(IReadOnlyList<OrganizationUnit> units, Guid unitId)
    {
        var childHeights = units.Where(unit => unit.ParentUnitId == unitId)
            .Select(unit => GetOrganizationSubtreeHeight(units, unit.Id))
            .ToArray();
        return childHeights.Length == 0 ? 1 : childHeights.Max() + 1;
    }

    private static void ValidateProjectDetails(string? name, string? projectAlias, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new PdmRuleException("项目名称不能为空。");
        if (name.Trim().Length > 200 || projectAlias?.Trim().Length > 200) throw new PdmRuleException("项目名称或项目别名超过允许长度。");
        if (quantity is < 1 or > 10000) throw new PdmRuleException("数量必须在1到10000之间。");
    }
}
